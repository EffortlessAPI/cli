namespace Effortless.Cli;

public static class CliVersion
{
    public const string Value = "2026.913.1830";

    /// <summary>
    /// Human-unambiguous, zero-padded form of the same UTC instant as
    /// <see cref="Value"/>: <c>v{yyyy}-{MM}-{dd}-{HHmm}</c>. Stamped by
    /// scripts/release.sh alongside Value.
    /// </summary>
    public const string DisplayVersion = "v2026-09-13-1830";

    /// <summary>
    /// The full commit SHA the release was cut from. Stamped by
    /// scripts/release.sh immediately before the release commit.
    /// </summary>
    public const string CommitSha = "63b85fda95af02f48008c8c97b969138a5d1347a";
}
