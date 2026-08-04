namespace PhotoBook.Rendering;

/// <summary>
/// What a requested font family actually resolved to on this machine.
/// <para>
/// Doc 10 §3 assumes the three OFL families ship inside the app; <b>this build bundles no font
/// files</b> (see <see cref="FontLibrary"/>), so a family either resolves exactly — because the user
/// installed it or pointed the library at font files — or it falls back. Every fallback is reported
/// here so the setup documentation, the export log and preflight can tell the user precisely which
/// font to install.
/// </para>
/// </summary>
/// <param name="RequestedFamily">The family named in the <see cref="PhotoBook.Core.Model.Style"/>.</param>
/// <param name="ResolvedFamily">The family Skia actually gave back.</param>
/// <param name="IsExactMatch">True when the resolved family is the requested one.</param>
/// <param name="SourceFile">The font file the face came from, when it was loaded from disk rather than the system.</param>
/// <param name="FallbackChain">The candidates tried, in order, up to and including the one that won.</param>
public sealed record FontResolution(
    string RequestedFamily,
    string ResolvedFamily,
    bool IsExactMatch,
    string? SourceFile,
    IReadOnlyList<string> FallbackChain)
{
    /// <summary>A one-line, user-readable description — what preflight and the export log print.</summary>
    public string Describe() => IsExactMatch
        ? $"'{RequestedFamily}' resolved exactly{(SourceFile is null ? " (system font)" : $" from {Path.GetFileName(SourceFile)}")}."
        : $"'{RequestedFamily}' is not installed; substituted '{ResolvedFamily}'. Install {RequestedFamily} for the intended typography.";
}
