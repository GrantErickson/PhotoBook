using System.Globalization;
using PhotoBook.Core.Model;
using PhotoBook.Core.Persistence;
using SkiaSharp;

namespace PhotoBook.Rendering;

/// <summary>What one preflight run examines.</summary>
public sealed record PreflightRequest
{
    /// <summary>The loaded project.</summary>
    public required ProjectSnapshot Project { get; init; }

    /// <summary>The profile the book would be exported against.</summary>
    public required PrintProfile Profile { get; init; }

    /// <summary>Resolves a template id to a library template.</summary>
    public required Func<string, Template?> Templates { get; init; }

    /// <summary>Which pages to examine; the Unplaced-bin check is always book-wide (doc 12).</summary>
    public ExportScope Scope { get; init; } = ExportScope.WholeBook;

    /// <summary>The font library, so substituted families can be reported.</summary>
    public FontLibrary Fonts { get; init; } = FontLibrary.Default;
}

/// <summary>
/// The kernel §11 export gate: empty slots, effective image DPI below 200, text overflow, a non-empty
/// Unplaced bin, and <c>dateUncertain</c> photos — plus the profile constraints and the caption and
/// font checks doc 12 and doc 10 add.
/// <para>
/// Every finding is structured and navigable: errors disable the Export button with no override,
/// warnings need one explicit acknowledgement covering all of them, and the Draft PDF button skips
/// the gate entirely (doc 12 "Preflight gate").
/// </para>
/// <para>
/// This runs the same geometry and the same text shaping the renderer does — findings are computed
/// from <see cref="CropMath"/> and <see cref="SkiaTextMeasurer"/>, not from a parallel model — so a
/// page that preflights clean renders clean.
/// </para>
/// </summary>
public sealed class PreflightChecker
{
    private readonly FontLibrary _fonts;

    /// <summary>Creates a checker.</summary>
    /// <param name="fonts">The font library; defaults to <see cref="FontLibrary.Default"/>.</param>
    public PreflightChecker(FontLibrary? fonts = null) => _fonts = fonts ?? FontLibrary.Default;

    /// <summary>Runs every gate and returns the findings, ordered for the dialog.</summary>
    public PreflightReport Check(PreflightRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var project = request.Project;
        var profile = request.Profile;
        var findings = new List<PreflightFinding>();

        var allPages = BookPagination.Paginate(project);
        var scoped = BookPagination.InScope(allPages, request.Scope);

        CheckProfile(request, allPages, scoped, findings);

        var placedPhotoIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bookPage in scoped)
        {
            var template = bookPage.Page.ResolveTemplate(id => request.Templates(id));
            if (template is null)
            {
                findings.Add(new PreflightFinding(
                    PreflightCheck.MissingTemplate, PreflightSeverity.Error,
                    "Missing template",
                    $"Page {bookPage.PageNumber} references template '{bookPage.Page.TemplateRef}', which is not in the library.",
                    bookPage.PageNumber, bookPage.Page.Id, ChapterMonth: bookPage.Chapter.Month));
                continue;
            }

            var style = StyleResolver.Resolve(project.Book, bookPage.Chapter, bookPage.Page);
            var geometry = PageGeometryMapper.Create(profile, project.Book.PageSize, PageGeometry.PointsPerInch);

            CheckSlots(request, bookPage, template, style, geometry, placedPhotoIds, findings);
            CheckJournalText(request, bookPage, template, style, geometry, findings);
        }

        CheckUnplacedBin(project, allPages, findings);
        CheckDateUncertain(project, placedPhotoIds, findings);
        CheckFonts(request, findings);

        findings.Sort(Compare);
        return new PreflightReport(findings, scoped.Count, request.Scope);
    }

    // ---- profile ---------------------------------------------------------------------------------

    private static void CheckProfile(
        PreflightRequest request,
        IReadOnlyList<BookPage> allPages,
        IReadOnlyList<BookPage> scoped,
        List<PreflightFinding> findings)
    {
        var profile = request.Profile;
        var book = request.Project.Book;

        if (profile.FindPageSize(book.PageSize) is null)
        {
            findings.Add(new PreflightFinding(
                PreflightCheck.ProfileConstraint, PreflightSeverity.Error,
                "Page size not supported by this print profile",
                $"The book's page size '{book.PageSize}' is not one of the sizes profile '{profile.Name}' prints. " +
                "Choose a different profile, or add the size to it."));
        }

        if (scoped.Count == 0)
        {
            findings.Add(new PreflightFinding(
                PreflightCheck.ProfileConstraint, PreflightSeverity.Error,
                "Nothing to export",
                $"The scope {request.Scope} selects no pages."));
            return;
        }

        if (!request.Scope.EnforcesPageCount)
        {
            if (!profile.IsPageCountValid(allPages.Count))
            {
                findings.Add(new PreflightFinding(
                    PreflightCheck.ProfileConstraint, PreflightSeverity.Warning,
                    "Partial export",
                    $"This is a {request.Scope} proof, so the profile's page-count rules are not enforced — " +
                    $"the full book's {allPages.Count} pages would not satisfy them. Proofs are for review, not upload."));
            }

            return;
        }

        if (profile.IsPageCountValid(allPages.Count)) return;

        var rules = new List<string>();
        if (profile.MinPages is { } min && allPages.Count < min) rules.Add($"at least {min}");
        if (profile.MaxPages is { } max && allPages.Count > max) rules.Add($"at most {max}");
        if (profile.PageCountMultiple is > 0 && allPages.Count % profile.PageCountMultiple.Value != 0)
            rules.Add($"a multiple of {profile.PageCountMultiple}");

        findings.Add(new PreflightFinding(
            PreflightCheck.ProfileConstraint, PreflightSeverity.Error,
            "Page count not accepted by this print profile",
            $"The book has {allPages.Count} pages; profile '{profile.Name}' requires {string.Join(" and ", rules)}.",
            Value: allPages.Count));
    }

    // ---- slots -----------------------------------------------------------------------------------

    private static void CheckSlots(
        PreflightRequest request,
        BookPage bookPage,
        Template template,
        Style style,
        PageGeometryMapper geometry,
        HashSet<string> placedPhotoIds,
        List<PreflightFinding> findings)
    {
        foreach (var slot in template.Slots)
        {
            var placement = bookPage.Page.PlacementFor(slot.Id);
            if (placement is null || string.IsNullOrEmpty(placement.PhotoId))
            {
                findings.Add(new PreflightFinding(
                    PreflightCheck.EmptySlot, PreflightSeverity.Error,
                    "Empty image slot",
                    $"Slot '{slot.Id}' on page {bookPage.PageNumber} has no photo. Fill it from the bin, or change the page layout.",
                    bookPage.PageNumber, bookPage.Page.Id, slot.Id, ChapterMonth: bookPage.Chapter.Month));
                continue;
            }

            var photo = request.Project.Photos.Find(placement.PhotoId);
            if (photo is null)
            {
                findings.Add(new PreflightFinding(
                    PreflightCheck.EmptySlot, PreflightSeverity.Error,
                    "Placed photo is missing",
                    $"Slot '{slot.Id}' on page {bookPage.PageNumber} references photo '{placement.PhotoId}', which is not in the catalog.",
                    bookPage.PageNumber, bookPage.Page.Id, slot.Id, placement.PhotoId, bookPage.Chapter.Month));
                continue;
            }

            placedPhotoIds.Add(photo.Id);

            var mapped = geometry.MapPageRect(slot.Rect);
            var rect = geometry.ApplyBleedExtension(slot.Rect, mapped);
            var (widthIn, heightIn) = geometry.PhysicalSizeIn(rect);
            if (photo.Width > 0 && photo.Height > 0 && widthIn > 0 && heightIn > 0)
            {
                var dpi = CropMath.EffectiveDpi(placement.Crop, photo.Width, photo.Height, widthIn, heightIn);
                if (dpi < PageGeometry.MinimumEffectiveDpi)
                {
                    findings.Add(new PreflightFinding(
                        PreflightCheck.EffectiveDpi, PreflightSeverity.Warning,
                        "Low effective resolution",
                        string.Create(CultureInfo.InvariantCulture,
                            $"'{photo.OriginalFileName}' prints at {dpi:0} DPI in slot '{slot.Id}' on page {bookPage.PageNumber} " +
                            $"(below the {PageGeometry.MinimumEffectiveDpi} DPI floor). Zoom out, use a larger source, or move it to a smaller slot."),
                        bookPage.PageNumber, bookPage.Page.Id, slot.Id, photo.Id, bookPage.Chapter.Month, dpi));
                }
            }

            CheckCaption(request, bookPage, slot, photo, style, geometry, rect, findings);
        }
    }

    /// <summary>
    /// Captions are hard-truncated with an ellipsis when they exceed their band, and doc 10 §4 asks
    /// preflight to say so. Measured with the same shaping the renderer uses.
    /// </summary>
    private static void CheckCaption(
        PreflightRequest request,
        BookPage bookPage,
        ImageSlot slot,
        Photo photo,
        Style style,
        PageGeometryMapper geometry,
        SKRect slotRect,
        List<PreflightFinding> findings)
    {
        if (slot.CaptionPolicy == CaptionPolicy.None || string.IsNullOrWhiteSpace(photo.Caption)) return;

        var captionStyle = style.CaptionText ?? BuiltInStyles.DefaultCaptionText;
        var measurer = new SkiaTextMeasurer(request.Fonts, TextRole.Caption);

        double widthIn;
        double heightIn;
        if (slot.CaptionPolicy == CaptionPolicy.Below)
        {
            widthIn = geometry.ToInches(slotRect.Width);
            heightIn = Math.Min(PageRenderer.CaptionBandIn, geometry.ToInches(slotRect.Height) / 2)
                       - PageRenderer.CaptionGapPt / PageGeometry.PointsPerInch;
        }
        else
        {
            var padding = (style.OverlayScrim?.PaddingPt ?? 12) / PageGeometry.PointsPerInch;
            widthIn = geometry.ToInches(slotRect.Width) - 2 * padding;
            heightIn = Math.Max(geometry.ToInches(slotRect.Height) - 2 * padding, 0);
        }

        if (widthIn <= 0 || heightIn <= 0) return;

        var layout = measurer.Measure([photo.Caption!], captionStyle, widthIn, heightIn, PageRenderer.MaxCaptionLines);
        if (!layout.Overflowed) return;

        findings.Add(new PreflightFinding(
            PreflightCheck.CaptionTruncated, PreflightSeverity.Warning,
            "Caption will be shortened",
            $"The caption on '{photo.OriginalFileName}' (page {bookPage.PageNumber}, slot '{slot.Id}') is longer than its band " +
            "and will print with an ellipsis. Shorten it, or use a slot with an overlay caption.",
            bookPage.PageNumber, bookPage.Page.Id, slot.Id, photo.Id, bookPage.Chapter.Month));
    }

    // ---- text ------------------------------------------------------------------------------------

    /// <summary>
    /// Text overflow is an <b>error</b>: nothing is ever auto-shrunk (kernel §9), so a day whose
    /// journal text failed doc 11's atomic-text ladder would print clipped. The fixes are editorial —
    /// a roomier template, a shorter entry, or a smaller journal size in Style (R23).
    /// </summary>
    private static void CheckJournalText(
        PreflightRequest request,
        BookPage bookPage,
        Template template,
        Style style,
        PageGeometryMapper geometry,
        List<PreflightFinding> findings)
    {
        var journalStyle = style.JournalText ?? BuiltInStyles.DefaultJournalText;
        var measurer = new SkiaTextMeasurer(request.Fonts);

        foreach (var textSlot in template.TextSlots.Where(t => t.Role == TextRole.Journal))
        {
            var paragraphs = Paragraphs(request.Project.Journal, bookPage.Page, textSlot.Id);
            if (paragraphs.Count == 0) continue;

            var rect = geometry.MapPageRect(textSlot.Rect);
            var widthIn = geometry.ToInches(rect.Width);
            var heightIn = geometry.ToInches(rect.Height);
            if (widthIn <= 0 || heightIn <= 0) continue;

            var layout = measurer.Measure(paragraphs, journalStyle, widthIn, heightIn);
            if (!layout.Overflowed) continue;

            findings.Add(new PreflightFinding(
                PreflightCheck.TextOverflow, PreflightSeverity.Error,
                "Journal text does not fit",
                string.Create(CultureInfo.InvariantCulture,
                    $"About {layout.OverflowCharacters} characters of journal text overflow slot '{textSlot.Id}' on page {bookPage.PageNumber}. Text is never auto-shrunk: choose a roomier template, trim the entry in Word and re-import, or lower the journal size in Style."),
                bookPage.PageNumber, bookPage.Page.Id, textSlot.Id, ChapterMonth: bookPage.Chapter.Month,
                Value: layout.OverflowCharacters));
        }
    }

    private static IReadOnlyList<string> Paragraphs(JournalDocument journal, Page page, string textSlotId)
    {
        var assignment = page.JournalAssignments
            .FirstOrDefault(a => string.Equals(a.TextSlotId, textSlotId, StringComparison.Ordinal));
        if (assignment is null || assignment.EntryIds.Count == 0) return [];

        var paragraphs = new List<string>();
        foreach (var entryId in assignment.EntryIds)
        {
            var entry = journal.Entries.FirstOrDefault(e => string.Equals(e.Id, entryId, StringComparison.Ordinal));
            if (entry is null || entry.Excluded) continue;
            paragraphs.AddRange(entry.Paragraphs.Where(p => p is not null));
        }

        return paragraphs;
    }

    // ---- book-level checks -----------------------------------------------------------------------

    /// <summary>
    /// The Unplaced bin is always evaluated book-wide, even for a chapter or range export: photos on
    /// no page will not print, and that is worth saying once (doc 12).
    /// </summary>
    private static void CheckUnplacedBin(
        ProjectSnapshot project, IReadOnlyList<BookPage> allPages, List<PreflightFinding> findings)
    {
        var placed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in allPages)
        {
            foreach (var placement in page.Page.Placements)
            {
                if (!string.IsNullOrEmpty(placement.PhotoId)) placed.Add(placement.PhotoId);
            }
        }

        var unplaced = project.Photos.Photos
            .Where(p => !p.Excluded && p.TakenAt.Year == project.Book.Year && !placed.Contains(p.Id))
            .OrderBy(p => p.TakenAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        if (unplaced.Count == 0) return;

        findings.Add(new PreflightFinding(
            PreflightCheck.UnplacedBin, PreflightSeverity.Warning,
            "Photos in the Unplaced bin",
            $"{unplaced.Count} photo(s) are in the book's Unplaced bin and will not print. " +
            "Place them, or exclude them from the book so they stop being counted.",
            PhotoId: unplaced[0].Id,
            Value: unplaced.Count));
    }

    private static void CheckDateUncertain(
        ProjectSnapshot project, HashSet<string> placedPhotoIds, List<PreflightFinding> findings)
    {
        var uncertain = project.Photos.Photos
            .Where(p => p.DateUncertain && placedPhotoIds.Contains(p.Id))
            .OrderBy(p => p.TakenAt)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        foreach (var photo in uncertain)
        {
            findings.Add(new PreflightFinding(
                PreflightCheck.DateUncertain, PreflightSeverity.Warning,
                "Photo date is a guess",
                $"'{photo.OriginalFileName}' has no reliable capture date, so it may be in the wrong month. " +
                "Set its date in the Photos tab to confirm or correct it.",
                PhotoId: photo.Id));
        }
    }

    /// <summary>
    /// Doc 10 §3 wants the three OFL families embedded from app resources. Until they ship, any family
    /// that resolved to a substitute is reported here — that is the line the setup documentation reads
    /// to tell the user exactly what to install.
    /// </summary>
    private void CheckFonts(PreflightRequest request, List<PreflightFinding> findings)
    {
        var fonts = request.Fonts ?? _fonts;
        fonts.WarmDefaults();
        foreach (var substitution in fonts.Substitutions)
        {
            findings.Add(new PreflightFinding(
                PreflightCheck.FontFallback, PreflightSeverity.Warning,
                "Font substituted",
                substitution.Describe() +
                " Output is only byte-identical across machines that resolve the same faces.",
                Value: null));
        }
    }

    private static int Compare(PreflightFinding a, PreflightFinding b)
    {
        var byCheck = a.Check.CompareTo(b.Check);
        if (byCheck != 0) return byCheck;
        var bySeverity = b.Severity.CompareTo(a.Severity);
        if (bySeverity != 0) return bySeverity;
        var byPage = (a.BookPageNumber ?? int.MaxValue).CompareTo(b.BookPageNumber ?? int.MaxValue);
        if (byPage != 0) return byPage;
        return string.CompareOrdinal(a.SlotId ?? a.PhotoId ?? string.Empty, b.SlotId ?? b.PhotoId ?? string.Empty);
    }
}
