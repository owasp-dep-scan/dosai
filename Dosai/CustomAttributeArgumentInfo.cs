namespace Depscan;

/// <summary>
///     One constructor argument of a custom attribute, in a structured, lossless form
///     (schema 5.1.0; previously a comma-joined string). The encoding distinguishes the four
///     cases a flat string collapsed or crashed on (issue #56 - <c>[Attr(null)]</c> threw):
///     <list type="bullet">
///         <item>A null constant - <c>[Attr(null)]</c>, including a null array reference - sets
///         <see cref="IsNull" />. <see cref="Value" /> and <see cref="Elements" /> stay null.</item>
///         <item>An array constant (<c>params</c> arrays, <c>[Attr(new string[0])]</c>) sets
///         <see cref="IsArray" /> and lists its elements in source order in
///         <see cref="Elements" />; an empty array is an empty list, distinct from
///         <see cref="IsNull" />. Elements recurse, so jagged arrays nest.</item>
///         <item>A scalar constant carries its text form in <see cref="Value" />.</item>
///     </list>
///     <see cref="IsNull" /> and <see cref="IsArray" /> are nullable so the serializer's
///     WhenWritingNull policy omits them when false, keeping scalar output compact.
/// </summary>
public sealed class CustomAttributeArgumentInfo
{
    public string? Type { get; set; }
    public bool? IsArray { get; set; }
    public bool? IsNull { get; set; }
    public string? Value { get; set; }
    public List<CustomAttributeArgumentInfo>? Elements { get; set; }
}
