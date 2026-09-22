using System.Text.Json.Serialization;

namespace Depscan;

public class NamedArgumentInfo
{
    public string? Name { get; set; }

    /// <summary>
    ///     A named argument applied as null (<c>[Attr(Name = null)]</c>) is a real null since
    ///     schema 5.1.0 (previously flattened to the empty string). The explicit Never condition
    ///     keeps the property in the JSON when null - under WhenWritingNull it would silently
    ///     disappear and a consumer could not tell a null value from an absent one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Value { get; set; }
}
