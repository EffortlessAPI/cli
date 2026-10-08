namespace Effortless.Cli;

public static class CliVersion
{
    public const string Value = "2026.1008.1747";

    /// <summary>
    /// Human-unambiguous, zero-padded form of the same UTC instant as
    /// <see cref="Value"/>: <c>v{yyyy}-{MM}-{dd}-{HHmm}</c>. Stamped by
    /// scripts/release.sh alongside Value.
    /// </summary>
    public const string DisplayVersion = "v2026-10-08-1747";

    /// <summary>
    /// The full commit SHA the release was cut from. Stamped by
    /// scripts/release.sh immediately before the release commit.
    /// </summary>
    public const string CommitSha = "9b6126f0869c6c104b20ea8d0858366045f5039c";
}
