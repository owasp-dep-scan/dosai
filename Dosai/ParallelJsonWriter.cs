using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Depscan;

/// <summary>
///     Streams a result object as JSON byte-identical to
///     <c>JsonSerializer.Serialize(stream, value, options)</c>, with the elements of its large lists
///     serialized on the worker team. The root object and its object-valued properties are written
///     here, property by property, from the serializer's own contract (same names, order and null
///     handling); every other value, and every list element, is still written by
///     <see cref="JsonSerializer" /> with the same options. A large list is cut into chunks of
///     consecutive elements, each chunk is serialized into its own buffer by a worker, and the
///     chunks are appended to the stream in list order (<see cref="DedicatedStack.ForEachInOrder" />,
///     a bounded window ahead of the writer), so memory stays at a few chunks per worker while the
///     single-threaded serializer was the whole phase (issue #65).
///     <para>
///         Every value is serialized on its own, so the serializer's depth limit (64, a cycle
///         guard) counts from that value rather than from the document root: a few levels more
///         than a whole-document call allows, far beyond the nesting of any Dosai result.
///     </para>
/// </summary>
internal static class ParallelJsonWriter
{
    /// <summary>Elements per chunk: big enough to amortize a serializer call, small enough to balance.</summary>
    private const int ChunkElements = 512;

    /// <summary>Lists shorter than this are not worth a team.</summary>
    private const int ParallelThreshold = 8 * ChunkElements;

    public static void Serialize<T>(Stream stream, T value, JsonSerializerOptions options)
    {
        // The serializer fills in the reflection resolver on first use; do the same, so
        // GetTypeInfo below works on options that have not serialized anything yet.
        options.MakeReadOnly(populateMissingResolver: true);
        if (!Supports(options))
        {
            JsonSerializer.Serialize(stream, value, options);
            return;
        }

        using var document = new Document(stream, options);
        document.WriteValue(value, options.GetTypeInfo(typeof(T)), objectLevels: 2);
    }

    /// <summary>
    ///     The option shapes this writer reproduces exactly; anything else (indentation, other
    ///     ignore conditions, reference handling, number handling) goes to the serializer whole.
    /// </summary>
    private static bool Supports(JsonSerializerOptions options) =>
        !options.WriteIndented &&
        options.ReferenceHandler is null &&
        options.NumberHandling == JsonNumberHandling.Strict &&
        options.DefaultIgnoreCondition is JsonIgnoreCondition.Never or JsonIgnoreCondition.WhenWritingNull &&
        !options.IgnoreReadOnlyProperties &&
        !options.IgnoreReadOnlyFields &&
        !options.IncludeFields;

    /// <summary>
    ///     An object the default converter writes as its contract's properties, in the contract's
    ///     order (explicit <c>JsonPropertyOrder</c> already applied, plain-ignored properties
    ///     already absent), and nothing else: no custom converter, polymorphism, extension data,
    ///     per-property converter, number handling, or conditional ignore (a
    ///     <c>ShouldSerialize</c> predicate) this writer would have to reproduce.
    /// </summary>
    private static bool IsPlainObject(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.PolymorphismOptions is not null || typeInfo.NumberHandling is not null ||
            typeInfo.Converter.GetType().Assembly != typeof(JsonSerializer).Assembly)
        {
            return false;
        }

        foreach (var property in typeInfo.Properties)
        {
            if (property.Get is null || property.IsExtensionData || property.CustomConverter is not null || property.NumberHandling is not null || property.ShouldSerialize is not null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A <c>List&lt;T&gt;</c> the serializer writes as a plain JSON array of its elements.</summary>
    private static bool IsPlainList(JsonTypeInfo typeInfo) =>
        typeInfo.Kind == JsonTypeInfoKind.Enumerable &&
        typeInfo.PolymorphismOptions is null &&
        typeInfo.Type.IsGenericType &&
        typeInfo.Type.GetGenericTypeDefinition() == typeof(List<>) &&
        typeInfo.Converter.GetType().Assembly == typeof(JsonSerializer).Assembly;

    /// <summary>A worker's output buffer and writer, reused for every chunk of the document.</summary>
    private sealed class ChunkBuffer(JsonWriterOptions writerOptions) : IDisposable
    {
        public readonly ArrayBufferWriter<byte> Bytes = new(64 * 1024);
        public readonly Utf8JsonWriter Writer = new(new ArrayBufferWriter<byte>(1), writerOptions);

        public void Dispose() => Writer.Dispose();
    }

    /// <summary>
    ///     The exploded levels write their own punctuation and property names straight to the
    ///     stream; values go through <see cref="JsonSerializer" />'s stream overload, which flushes
    ///     as it goes, so no value is ever buffered whole.
    /// </summary>
    private sealed class Document(Stream stream, JsonSerializerOptions options) : IDisposable
    {
        // The serializer's own writer options for a compact document.
        private readonly JsonWriterOptions _writerOptions = new() { Encoder = options.Encoder, MaxDepth = options.MaxDepth == 0 ? 64 : options.MaxDepth, SkipValidation = true };
        private readonly ConcurrentBag<ChunkBuffer> _buffers = [];

        public void WriteValue(object? value, JsonTypeInfo typeInfo, int objectLevels)
        {
            if (value is not null && objectLevels > 0 && IsPlainObject(typeInfo))
            {
                WriteObject(value, typeInfo, objectLevels - 1);
            }
            else if (value is IList { Count: >= ParallelThreshold } list && IsPlainList(typeInfo))
            {
                WriteList(list, typeInfo);
            }
            else
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
            }
        }

        private void WriteObject(object value, JsonTypeInfo typeInfo, int objectLevels)
        {
            stream.WriteByte((byte)'{');
            var first = true;
            foreach (var property in typeInfo.Properties)
            {
                var propertyValue = property.Get!(value);
                if (propertyValue is null && options.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull)
                {
                    continue;
                }

                if (!first)
                {
                    stream.WriteByte((byte)',');
                }

                first = false;
                // Escaped with the options' encoder, as the serializer escapes property names.
                stream.WriteByte((byte)'"');
                stream.Write(JsonEncodedText.Encode(property.Name, options.Encoder).EncodedUtf8Bytes);
                stream.Write("\":"u8);
                WriteValue(propertyValue, options.GetTypeInfo(property.PropertyType), objectLevels);
            }

            stream.WriteByte((byte)'}');
        }

        /// <summary>
        ///     Each chunk is serialized as a list of the list's own type holding just its elements:
        ///     one serializer call per chunk (a call per element costs the serializer's per-call
        ///     state for every element, gigabytes on a large slice). The chunk's brackets are dropped
        ///     and its elements appended, comma-joined, in list order.
        /// </summary>
        private void WriteList(IList list, JsonTypeInfo listInfo)
        {
            stream.WriteByte((byte)'[');
            var windows = new ConcurrentBag<IList>();
            var first = true;
            var chunkCount = (list.Count + ChunkElements - 1) / ChunkElements;
            DedicatedStack.ForEachInOrder("Dosai json writer", Math.Max(1, Dosai.MaxSymbolAnalysisWorkers), chunkCount, index =>
            {
                var buffer = _buffers.TryTake(out var pooled) ? pooled : new ChunkBuffer(_writerOptions);
                var window = windows.TryTake(out var pooledWindow) ? pooledWindow : (IList)Activator.CreateInstance(listInfo.Type, ChunkElements)!;
                var end = Math.Min(list.Count, (index + 1) * ChunkElements);
                for (var element = index * ChunkElements; element < end; element++)
                {
                    window.Add(list[element]);
                }

                buffer.Bytes.ResetWrittenCount();
                buffer.Writer.Reset(buffer.Bytes);
                JsonSerializer.Serialize(buffer.Writer, window, listInfo);
                buffer.Writer.Flush();
                window.Clear();
                windows.Add(window);
                return buffer;
            }, buffer =>
            {
                if (!first)
                {
                    stream.WriteByte((byte)',');
                }

                first = false;
                stream.Write(buffer.Bytes.WrittenSpan[1..^1]);
                _buffers.Add(buffer);
            });

            stream.WriteByte((byte)']');
        }

        public void Dispose()
        {
            foreach (var buffer in _buffers)
            {
                buffer.Dispose();
            }
        }
    }
}
