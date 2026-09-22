namespace Effortless.Cli;

public static class CliVersion
{
    public const string Value = "2026.922.609";

    /// <summary>
    /// Human-unambiguous, zero-padded form of the same UTC instant as
    /// <see cref="Value"/>: <c>v{yyyy}-{MM}-{dd}-{HHmm}</c>. Stamped by
    /// scripts/release.sh alongside Value.
    /// </summary>
    public const string DisplayVersion = "v2026-09-22-0609";

    /// <summary>
    /// The full commit SHA the release was cut from. Stamped by
    /// scripts/release.sh immediately before the release commit.
    /// </summary>
    public const string CommitSha = "ab67a874a71aeded7bc073dc75f282e784e076ac";
}
