namespace PhotoBook.Rendering;

/// <summary>
/// Which output the <see cref="PageRenderer"/> is drawing into (doc 12).
/// <para>
/// The target never changes <b>what</b> is drawn — the same draw code produces the screen preview
/// and the PDF (ADR-0003) — it only selects the pixel tier the image source hands back and switches
/// off the editor-only affordances (the amber empty-slot flags of doc 09 §3.6 never reach paper).
/// </para>
/// </summary>
public enum RenderTarget
{
    /// <summary>The on-screen editor preview; images come from the 1024 px layout tier (kernel §10).</summary>
    Screen,

    /// <summary>The final print-ready PDF: 300 DPI image prep, JPEG q90, no editor chrome (kernel §3).</summary>
    Export300Dpi,

    /// <summary>The draft escape hatch of doc 12: 150 DPI, JPEG q75, diagonal DRAFT watermark.</summary>
    Draft150Dpi,
}

/// <summary>Conveniences over <see cref="RenderTarget"/>.</summary>
public static class RenderTargets
{
    /// <summary>
    /// The raster resolution images are prepared at, in dots per inch, or <c>0</c> for
    /// <see cref="RenderTarget.Screen"/> — on screen the resolution is whatever the
    /// <see cref="PageGeometryMapper.Scale"/> of the preview happens to be.
    /// </summary>
    public static int RasterDpi(this RenderTarget target) => target switch
    {
        RenderTarget.Export300Dpi => 300,
        RenderTarget.Draft150Dpi => 150,
        _ => 0,
    };

    /// <summary>The JPEG quality Skia caps embedded images at (doc 12: q90 final, q75 draft).</summary>
    public static int JpegQuality(this RenderTarget target) => target switch
    {
        RenderTarget.Draft150Dpi => 75,
        _ => 90,
    };

    /// <summary>True for the two PDF targets — everything that is not the live editor preview.</summary>
    public static bool IsExport(this RenderTarget target) => target != RenderTarget.Screen;
}
