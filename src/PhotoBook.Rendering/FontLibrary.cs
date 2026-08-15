using System.Collections.Concurrent;
using PhotoBook.Core.Model;
using SkiaSharp;

namespace PhotoBook.Rendering;

/// <summary>
/// Resolves the doc 10 §3 font families to concrete <see cref="SKTypeface"/>s and records what
/// actually happened.
/// <para>
/// <b>No fonts are bundled in this assembly.</b> Doc 10 §3 and doc 12 "Fonts" call for the three OFL
/// families to ship as app resources and be subset-embedded, which is the right end state — until
/// those files are added to <c>PhotoBook.App</c> this library resolves them by name, with a
/// documented fallback chain to faces that exist on a stock Windows install, and reports every
/// substitution through <see cref="Resolutions"/>. Point <see cref="Create"/> at the OFL files (or
/// install them) and resolution becomes exact with no code change.
/// </para>
/// <para>
/// Two honest consequences of falling back, both surfaced by preflight
/// (<see cref="PreflightCheck.FontFallback"/>): PDF output is only byte-stable across machines that
/// resolve the same faces, and the substituted metrics differ from the authored ones, so line counts
/// can differ from a machine with the real families installed.
/// </para>
/// </summary>
public sealed class FontLibrary : IDisposable
{
    /// <summary>Fallback chain for the journal face — Source Serif 4 (doc 10 §3).</summary>
    public static IReadOnlyList<string> JournalChain { get; } =
        ["Source Serif 4", "Source Serif Pro", "Source Serif", "Georgia", "Cambria", "Times New Roman", "serif"];

    /// <summary>Fallback chain for the caption face — Source Sans 3 (doc 10 §3).</summary>
    public static IReadOnlyList<string> CaptionChain { get; } =
        ["Source Sans 3", "Source Sans Pro", "Source Sans", "Segoe UI", "Selawik", "Arial", "sans-serif"];

    /// <summary>Fallback chain for the month-title display face — Playfair Display (doc 10 §3, R24).</summary>
    public static IReadOnlyList<string> MonthTitleChain { get; } =
        ["Playfair Display", "Bodoni MT", "Didot", "Georgia", "Cambria", "Times New Roman", "serif"];

    private readonly Dictionary<string, SKTypeface> _loadedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, FontResolution> _resolutions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SKTypeface> _typefaces = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SKTypeface> _owned = [];
    private bool _disposed;

    private FontLibrary(IEnumerable<string> fontFiles)
    {
        foreach (var file in fontFiles)
        {
            SKTypeface? face = null;
            try
            {
                face = SKTypeface.FromFile(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A font file we cannot read is a fallback, not a crash: the chain below still applies.
            }

            if (face is null) continue;
            _owned.Add(face);
            LoadedFiles[face.FamilyName] = file;
            _loadedFiles[face.FamilyName] = face;
        }
    }

    /// <summary>
    /// The process-wide library that resolves against installed system fonts only. Use
    /// <see cref="Create"/> when the app ships or locates the real OFL files.
    /// </summary>
    public static FontLibrary Default { get; } = new([]);

    /// <summary>Family name → the file it was loaded from, for the faces this library owns.</summary>
    public Dictionary<string, string> LoadedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every family this library has been asked for so far and what it resolved to — the report doc
    /// 12's export log and the setup documentation consume.
    /// </summary>
    public IReadOnlyList<FontResolution> Resolutions =>
        _resolutions.Values.OrderBy(r => r.RequestedFamily, StringComparer.Ordinal).ToList();

    /// <summary>Resolutions that fell back — the ones worth telling the user about.</summary>
    public IReadOnlyList<FontResolution> Substitutions =>
        Resolutions.Where(r => !r.IsExactMatch).ToList();

    /// <summary>
    /// A library that prefers font files found in the given files or directories (<c>.ttf</c>,
    /// <c>.otf</c>, <c>.ttc</c>) and falls back to system families for anything they do not supply.
    /// </summary>
    public static FontLibrary Create(params string[] fontFilesOrDirectories) =>
        Create((IEnumerable<string>)fontFilesOrDirectories);

    /// <inheritdoc cref="Create(string[])"/>
    public static FontLibrary Create(IEnumerable<string> fontFilesOrDirectories)
    {
        ArgumentNullException.ThrowIfNull(fontFilesOrDirectories);
        return new FontLibrary(Expand(fontFilesOrDirectories));
    }

    /// <summary>
    /// Warms the resolutions for the three doc 10 roles so <see cref="Substitutions"/> is complete
    /// before anything is rendered — what the app calls at startup to decide whether to nag the user.
    /// </summary>
    public FontLibrary WarmDefaults()
    {
        Resolve(BuiltInStyles.JournalFamily, TextRole.Journal);
        Resolve(BuiltInStyles.CaptionFamily, TextRole.Caption);
        Resolve(BuiltInStyles.MonthTitleFamily, TextRole.MonthTitle);
        return this;
    }

    /// <summary>The fallback chain doc 10 assigns to a text role.</summary>
    public static IReadOnlyList<string> ChainFor(TextRole role) => role switch
    {
        TextRole.Caption => CaptionChain,
        TextRole.MonthTitle => MonthTitleChain,
        _ => JournalChain,
    };

    /// <summary>
    /// The typeface for a family, walking the role's fallback chain when the family is unavailable.
    /// Results are cached; the returned face is owned by the library and must not be disposed.
    /// </summary>
    /// <param name="family">The family named in the style, or null to use the role's default.</param>
    /// <param name="role">The text role, which selects the fallback chain.</param>
    /// <param name="weight">The style's weight name, e.g. <c>"regular"</c> or <c>"bold"</c>.</param>
    public SKTypeface Resolve(string? family, TextRole role, string? weight = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var chain = ChainFor(role);
        var requested = string.IsNullOrWhiteSpace(family) ? chain[0] : family.Trim();
        var style = StyleFor(weight);
        var key = $"{requested}|{style.Weight}|{style.Slant}";

        return _typefaces.GetOrAdd(key, _ =>
        {
            var candidates = new List<string> { requested };
            foreach (var c in chain)
            {
                if (!candidates.Contains(c, StringComparer.OrdinalIgnoreCase)) candidates.Add(c);
            }

            var tried = new List<string>();
            foreach (var candidate in candidates)
            {
                tried.Add(candidate);

                if (_loadedFiles.TryGetValue(candidate, out var loaded))
                {
                    Record(requested, candidate, true, LoadedFiles.GetValueOrDefault(candidate), tried);
                    return loaded;
                }

                var face = SKTypeface.FromFamilyName(candidate, style);
                if (face is null) continue;

                // FromFamilyName never fails outright — it silently substitutes. Only an actual family
                // name match counts as a hit; anything else keeps walking the chain.
                if (Matches(face.FamilyName, candidate))
                {
                    Track(face);
                    Record(requested, face.FamilyName, string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase), null, tried);
                    return face;
                }

                Track(face);
            }

            var fallback = SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
            Track(fallback);
            Record(requested, fallback.FamilyName, false, null, tried);
            return fallback;
        });
    }

    /// <summary>
    /// An <see cref="SKFont"/> for a resolved text style at a given device scale.
    /// </summary>
    /// <param name="style">The resolved text style (family, size in points, weight).</param>
    /// <param name="role">The text role, which selects the fallback chain and the default family.</param>
    /// <param name="pointScale">Device units per point — <see cref="PageGeometryMapper.PointScale"/>.</param>
    /// <param name="defaultSizePt">The size to use when the style does not set one.</param>
    public SKFont CreateFont(TextStyle? style, TextRole role, double pointScale, double defaultSizePt)
    {
        var typeface = Resolve(style?.Family, role, style?.Weight);
        var sizePt = style?.SizePt is > 0 ? style.SizePt!.Value : defaultSizePt;
        var font = new SKFont(typeface, (float)(sizePt * pointScale))
        {
            Subpixel = true,
            Edging = SKFontEdging.SubpixelAntialias,
            Hinting = SKFontHinting.Normal,
        };
        if (IsBold(style?.Weight) && !typeface.IsBold) font.Embolden = true;
        return font;
    }

    /// <summary>The resolution recorded for a family, if it has been resolved.</summary>
    public FontResolution? ResolutionFor(string family) =>
        _resolutions.GetValueOrDefault(family ?? string.Empty);

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var face in _owned) face.Dispose();
        _owned.Clear();
        _typefaces.Clear();
        _loadedFiles.Clear();
    }

    private void Track(SKTypeface face)
    {
        lock (_owned)
        {
            if (!_owned.Contains(face)) _owned.Add(face);
        }
    }

    private void Record(string requested, string resolved, bool exact, string? file, List<string> tried) =>
        _resolutions[requested] = new FontResolution(requested, resolved, exact, file, [.. tried]);

    private static bool Matches(string? actual, string requested) =>
        actual is not null && string.Equals(Normalize(actual), Normalize(requested), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string family) => family.Replace(" ", string.Empty).Replace("-", string.Empty);

    private static bool IsBold(string? weight) =>
        weight is not null && (weight.Contains("bold", StringComparison.OrdinalIgnoreCase)
                               || weight.Contains("semibold", StringComparison.OrdinalIgnoreCase)
                               || weight.Contains("black", StringComparison.OrdinalIgnoreCase));

    private static SKFontStyle StyleFor(string? weight)
    {
        if (weight is null) return SKFontStyle.Normal;
        if (weight.Contains("italic", StringComparison.OrdinalIgnoreCase)) return SKFontStyle.Italic;
        if (weight.Contains("black", StringComparison.OrdinalIgnoreCase)) return SKFontStyle.Bold;
        if (weight.Contains("bold", StringComparison.OrdinalIgnoreCase)) return SKFontStyle.Bold;
        if (weight.Contains("light", StringComparison.OrdinalIgnoreCase))
            return new SKFontStyle(SKFontStyleWeight.Light, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        return SKFontStyle.Normal;
    }

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path)
                             .Where(IsFontFile)
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
            else if (File.Exists(path) && IsFontFile(path))
            {
                yield return path;
            }
        }
    }

    private static bool IsFontFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
    }
}
