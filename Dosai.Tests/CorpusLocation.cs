namespace Dosai.Tests;

/// <summary>
///     Where the corpus of real sample apps lives. <c>DOSAI_CORPUS_DIR</c> wins when it is set and
///     not blank, otherwise the corpus is <c>~/sandbox/dosai-corpus</c>. A leading <c>~</c> in the
///     variable is expanded to the user profile directory (a shell does that for an
///     <c>export</c>, but not for a value that came from an IDE or a CI settings page);
///     <c>Corpus/setup.sh</c> applies the same rule.
/// </summary>
internal static class CorpusLocation
{
    public const string EnvironmentVariable = "DOSAI_CORPUS_DIR";

    public static string Root { get; } = Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string Resolve(string? configured, string home)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(home, "sandbox", "dosai-corpus");
        }

        var value = configured.Trim();
        if (value == "~")
        {
            return home;
        }

        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(home, value[2..]);
        }

        return value;
    }
}
