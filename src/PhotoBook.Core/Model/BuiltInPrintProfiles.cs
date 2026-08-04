namespace PhotoBook.Core.Model;

/// <summary>The print profiles shipped with the app.</summary>
public static class BuiltInPrintProfiles
{
    /// <summary>
    /// The generic 11 × 8.5 in landscape profile of kernel §3 — 0.125 in bleed, 0.375 in safe margin,
    /// 0.5 in gutter caution, 300 DPI, JPEG quality 90, sRGB. Returns a fresh instance each call.
    /// </summary>
    public static PrintProfile Generic => new();
}
