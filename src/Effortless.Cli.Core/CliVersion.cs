namespace Effortless.Cli;

public static class CliVersion
{
    public const string Value = "2026.921.2357";

    /// <summary>
    /// Human-unambiguous, zero-padded form of the same UTC instant as
    /// <see cref="Value"/>: <c>v{yyyy}-{MM}-{dd}-{HHmm}</c>. Stamped by
    /// scripts/release.sh alongside Value.
    /// </summary>
    public const string DisplayVersion = "v2026-09-21-2357";

    /// <summary>
    /// The full commit SHA the release was cut from. Stamped by
    /// scripts/release.sh immediately before the release commit.
    /// </summary>
    public const string CommitSha = "3196139189d5af8bb21ac736c3e07a26a51268ce";
}
