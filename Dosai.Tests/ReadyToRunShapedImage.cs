using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Dosai.Tests;

/// <summary>
///     Writes a file laid out like a ReadyToRun image of an IL-only assembly (review of issue
///     #83): crossgen2 keeps the metadata and IL, module version id included, and moves the method
///     bodies and static field data behind a native header and native code, so the file has other
///     bytes and another length. The tests cannot run crossgen2 (it ships per platform), and copy
///     detection reads only the parts this keeps.
/// </summary>
internal static class ReadyToRunShapedImage
{
    private const int CliHeaderSize = 72;
    private const int ReadyToRunHeaderSize = 16;
    private const int NativeCodeSize = 128;

    /// <param name="ilOnlyPath">The IL build.</param>
    /// <param name="outputPath">The image to write.</param>
    /// <param name="patchFieldData">Changes each static field's data as it is copied, for an image of other content.</param>
    /// <param name="readyToRunHeader">False writes no ReadyToRun header: a file that is not IL-only for another reason (mixed-mode code).</param>
    public static void Write(string ilOnlyPath, string outputPath, Action<byte[]>? patchFieldData = null, bool readyToRunHeader = true)
    {
        using var peReader = new PEReader(ImmutableArray.Create(File.ReadAllBytes(ilOnlyPath)));
        var reader = peReader.GetMetadataReader();
        var corHeader = peReader.PEHeaders.CorHeader ?? throw new InvalidOperationException("not a managed assembly");
        var metadata = peReader.GetMetadata().GetContent().ToArray();

        // Each part's new place, as an offset into the one section, behind the headers and the
        // native code.
        var offset = CliHeaderSize + (readyToRunHeader ? ReadyToRunHeaderSize : 0) + NativeCodeSize;
        var parts = new List<(int Offset, byte[] Bytes)>();
        int Place(byte[] bytes, int alignment)
        {
            offset = (offset + alignment - 1) / alignment * alignment;
            parts.Add((offset, bytes));
            var placed = offset;
            offset += bytes.Length;
            return placed;
        }

        var bodyOffsets = new Dictionary<int, int>();
        foreach (var handle in reader.MethodDefinitions)
        {
            var rva = reader.GetMethodDefinition(handle).RelativeVirtualAddress;
            if (rva != 0 && !bodyOffsets.ContainsKey(rva))
            {
                bodyOffsets[rva] = Place(peReader.GetSectionData(rva).GetContent(0, peReader.GetMethodBody(rva).Size).ToArray(), 4);
            }
        }

        // FieldRVA rows (ECMA-335 II.22.18): the RVA, then the field's row number.
        var fieldRvaTable = reader.GetTableMetadataOffset(TableIndex.FieldRva);
        var fieldRvaRowSize = reader.GetTableRowSize(TableIndex.FieldRva);
        var fieldOffsets = new List<(int Row, int Offset)>();
        for (var row = 0; row < reader.GetTableRowCount(TableIndex.FieldRva); row++)
        {
            var rowStart = fieldRvaTable + row * fieldRvaRowSize;
            var rva = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(rowStart));
            var fieldRow = fieldRvaRowSize - sizeof(int) == sizeof(ushort)
                ? BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(rowStart + sizeof(int)))
                : BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(rowStart + sizeof(int)));
            var data = peReader.GetSectionData(rva).GetContent(0, FieldDataSize(reader, MetadataTokens.FieldDefinitionHandle(fieldRow))).ToArray();
            patchFieldData?.Invoke(data);
            fieldOffsets.Add((row, Place(data, 8)));
        }

        var resources = corHeader.ResourcesDirectory;
        var resourcesOffset = resources.Size > 0 ? Place(peReader.GetSectionData(resources.RelativeVirtualAddress).GetContent(0, resources.Size).ToArray(), 8) : 0;
        var metadataOffset = Place(metadata, 4);

        var builder = new SingleSectionBuilder(sectionRva =>
        {
            // The RVA columns point at the new places; nothing else in the metadata changes.
            var methodTable = reader.GetTableMetadataOffset(TableIndex.MethodDef);
            var methodRowSize = reader.GetTableRowSize(TableIndex.MethodDef);
            var methodRow = 0;
            foreach (var handle in reader.MethodDefinitions)
            {
                var rva = reader.GetMethodDefinition(handle).RelativeVirtualAddress;
                BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(methodTable + methodRow++ * methodRowSize), rva == 0 ? 0 : sectionRva + bodyOffsets[rva]);
            }

            foreach (var (row, fieldOffset) in fieldOffsets)
            {
                BinaryPrimitives.WriteInt32LittleEndian(metadata.AsSpan(fieldRvaTable + row * fieldRvaRowSize), sectionRva + fieldOffset);
            }

            // IMAGE_COR20_HEADER: crossgen2 clears the IL-only flag and sets IL-library.
            var content = new BlobBuilder();
            content.WriteInt32(CliHeaderSize);
            content.WriteUInt16(2);
            content.WriteUInt16(5);
            content.WriteInt32(sectionRva + metadataOffset);
            content.WriteInt32(metadata.Length);
            content.WriteInt32((int)((corHeader.Flags & ~CorFlags.ILOnly) | (readyToRunHeader ? CorFlags.ILLibrary : 0)));
            content.WriteInt32(corHeader.EntryPointTokenOrRelativeVirtualAddress);
            content.WriteInt32(resources.Size > 0 ? sectionRva + resourcesOffset : 0);
            content.WriteInt32(resources.Size);
            // Strong-name signature, code manager table, v-table fixups, export address table jumps.
            content.WriteBytes(0, 4 * 2 * sizeof(int));
            content.WriteInt32(readyToRunHeader ? sectionRva + CliHeaderSize : 0);
            content.WriteInt32(readyToRunHeader ? ReadyToRunHeaderSize : 0);
            if (readyToRunHeader)
            {
                // READYTORUN_HEADER: the "RTR" signature, version 9.2, no flags, no sections.
                content.WriteUInt32(0x00525452);
                content.WriteUInt16(9);
                content.WriteUInt16(2);
                content.WriteInt32(0);
                content.WriteInt32(0);
            }

            content.WriteBytes(0xCC, NativeCodeSize);
            foreach (var (partOffset, bytes) in parts)
            {
                content.WriteBytes(0, partOffset - content.Count);
                content.WriteBytes(bytes);
            }

            return content;
        });

        var image = new BlobBuilder();
        builder.Serialize(image);
        File.WriteAllBytes(outputPath, image.ToArray());
    }

    /// <summary>A static field's data size: a primitive, or a value type of this module with an explicit size.</summary>
    private static int FieldDataSize(MetadataReader reader, FieldDefinitionHandle handle)
    {
        var signature = reader.GetBlobReader(reader.GetFieldDefinition(handle).Signature);
        signature.ReadSignatureHeader();
        return signature.ReadSignatureTypeCode() switch
        {
            SignatureTypeCode.Boolean or SignatureTypeCode.SByte or SignatureTypeCode.Byte => 1,
            SignatureTypeCode.Char or SignatureTypeCode.Int16 or SignatureTypeCode.UInt16 => 2,
            SignatureTypeCode.Int32 or SignatureTypeCode.UInt32 or SignatureTypeCode.Single => 4,
            SignatureTypeCode.Int64 or SignatureTypeCode.UInt64 or SignatureTypeCode.Double => 8,
            SignatureTypeCode.TypeHandle => reader.GetTypeDefinition((TypeDefinitionHandle)signature.ReadTypeHandle()).GetLayout().Size,
            var other => throw new InvalidOperationException($"unexpected static data type {other}")
        };
    }

    /// <summary>One code section holding the CLI header and everything it points at.</summary>
    private sealed class SingleSectionBuilder(Func<int, BlobBuilder> serialize)
        : PEBuilder(new PEHeaderBuilder(Machine.Amd64, imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll), _ => new BlobContentId(Guid.Empty, 0x5EED))
    {
        private PEDirectoriesBuilder? _directories;

        protected override ImmutableArray<Section> CreateSections() =>
            [new Section(".text", SectionCharacteristics.ContainsCode | SectionCharacteristics.MemExecute | SectionCharacteristics.MemRead)];

        protected override BlobBuilder SerializeSection(string name, SectionLocation location)
        {
            _directories = new PEDirectoriesBuilder { CorHeaderTable = new DirectoryEntry(location.RelativeVirtualAddress, CliHeaderSize) };
            return serialize(location.RelativeVirtualAddress);
        }

        protected override PEDirectoriesBuilder GetDirectories() => _directories ?? throw new InvalidOperationException("The section is serialized first.");
    }
}
