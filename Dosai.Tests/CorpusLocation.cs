namespace Dosai.Tests;

/// <summary>
///     Where the corpus of real sample apps lives. <c>DOSAI_CORPUS_DIR</c> wins when it is set and
///     not blank (surrounding white space is ignored), otherwise the corpus is
///     <c>~/sandbox/dosai-corpus</c>. A leading <c>~</c> in the variable is expanded to the user
///     profile directory (a shell does that for an <c>export</c>, but not for a value that came
///     from an IDE or a CI settings page). The result must be an absolute path: a relative one
///     would name one directory for <c>Corpus/setup.sh</c> (the shell's current directory) and
///     another for the tests (the test host's output directory). <c>Corpus/setup.sh</c> applies
///     the same rule.
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
            value = home;
        }
        else if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
        {
            value = Path.Join(home, value[2..].TrimStart('/', '\\'));
        }

        return Path.IsPathRooted(value)
            ? value
            : throw new InvalidOperationException($"{EnvironmentVariable} must be an absolute path, not '{configured}'.");
    }
}
