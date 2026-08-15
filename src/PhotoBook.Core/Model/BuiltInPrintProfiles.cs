namespace PhotoBook.Core.Model;

/// <summary>The print profiles shipped with the app.</summary>
public static class BuiltInPrintProfiles
{
    /// <summary>
    /// The generic profile of kernel §3 — 0.125 in bleed, 0.375 in safe margin, 0.5 in gutter
    /// caution, 300 DPI, JPEG quality 90, sRGB. Returns a fresh instance each call.
    /// <para>
    /// It declares four trims rather than one. Doc 12 is explicit that adding a page size is two
    /// data changes — a <c>pageSizes</c> entry here, and a template set carrying that
    /// <see cref="Template.PageSize"/> id — and that only the second one is missing today. Listing
    /// the sizes the geometry genuinely handles is what lets the book-settings surface tell a user
    /// which sizes have authored layouts instead of pretending the app has exactly one page shape
    /// (R19). <c>11x8.5-landscape</c> stays first because callers fall back to
    /// <see cref="PrintProfile.PageSizes"/><c>[0]</c> when a book names a size the profile lost.
    /// </para>
    /// </summary>
    public static PrintProfile Generic => new()
    {
        Name = "Generic photo book",
        PageSizes =
        [
            new PageSizeSpec
            {
                Id = PageGeometry.DefaultPageSizeId,
                TrimWidthIn = PageGeometry.TrimWidthIn,
                TrimHeightIn = PageGeometry.TrimHeightIn,
            },
            new PageSizeSpec { Id = "8.5x11-portrait", TrimWidthIn = 8.5, TrimHeightIn = 11.0 },
            new PageSizeSpec { Id = "12x12-square", TrimWidthIn = 12.0, TrimHeightIn = 12.0 },
            new PageSizeSpec { Id = "8x8-square", TrimWidthIn = 8.0, TrimHeightIn = 8.0 },
        ],
    };

    /// <summary>Every profile shipped with the app, in display order. Fresh instances each call.</summary>
    public static IReadOnlyList<PrintProfile> All => [Generic];

    /// <summary>
    /// The shipped profile with this id, falling back to <see cref="Generic"/>. A book that names a
    /// profile this build does not carry still opens and still exports — on the generic geometry,
    /// which is the honest default rather than a crash.
    /// </summary>
    public static PrintProfile Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal)) ?? Generic;
}
