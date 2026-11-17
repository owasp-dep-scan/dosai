using System.Text;

namespace Depscan;

/// <summary>
///     Escapes text written into the XML exports (GraphML and GEXF node ids, labels, attribute
///     values). The five markup characters become entity references, and every character the
///     XML 1.0 Char production rejects, plus every lone surrogate, becomes U+FFFD, so
///     identifiers, file names and code text from an analyzed tree can never produce a document
///     an XML parser refuses. U+FFFD rather than a visible <c>\uXXXX</c> form: GraphML and GEXF
///     define no such escape, and replacement keeps downstream parsers working
///     (docs/graph-formats.md).
/// </summary>
internal static class XmlText
{
    /// <summary>
    ///     Returns <paramref name="value" /> with the markup entities escaped and every
    ///     XML-invalid character replaced. A string that needs no work is returned as-is, so
    ///     exports of text whose characters are all legal stay byte-identical to the output of
    ///     the escaper this replaces (<see cref="System.Security.SecurityElement" /> escaped the
    ///     same five entities and passed invalid characters through).
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder? builder = null;
        var plainStart = 0;
        var index = 0;
        while (index < value.Length)
        {
            var current = value[index];

            // The XML 1.0 Char production keeps tab, newline and carriage return, everything
            // from U+0020 to U+D7FF, and U+E000 to U+FFFD; inside that range only the five
            // markup characters need work.
            if (current is '\t' or '\n' or '\r')
            {
                index++;
                continue;
            }

            if (current >= ' ' && current is not ('<' or '>' or '&' or '\'' or '"' or '\uFFFE' or '\uFFFF'))
            {
                if (current < 0xD800 || current > 0xDFFF)
                {
                    index++;
                    continue;
                }

                if (current <= 0xDBFF && index + 1 < value.Length && value[index + 1] is >= '\uDC00' and <= '\uDFFF')
                {
                    // A complete surrogate pair is one supplementary character (for example an
                    // emoji) and is copied as-is.
                    index += 2;
                    continue;
                }

                // A surrogate without its partner here is lone and is replaced below.
            }

            builder ??= new StringBuilder(value.Length + 8);
            builder.Append(value, plainStart, index - plainStart);
            switch (current)
            {
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '&':
                    builder.Append("&amp;");
                    break;
                case '\'':
                    builder.Append("&apos;");
                    break;
                case '"':
                    builder.Append("&quot;");
                    break;
                default:
                    builder.Append('\uFFFD');
                    break;
            }

            index++;
            plainStart = index;
        }

        if (builder is null)
        {
            return value;
        }

        builder.Append(value, plainStart, value.Length - plainStart);
        return builder.ToString();
    }
}
