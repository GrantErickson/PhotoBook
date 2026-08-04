using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PhotoBook.Tests.Fixtures;

/// <summary>
/// Builds real <c>.docx</c> journals with the OpenXML SDK (ADR-0008 — never Word interop), so the
/// journal tests exercise the same package reader the app uses rather than a hand-rolled paragraph
/// list.
/// </summary>
public static class SyntheticJournal
{
    /// <summary>One paragraph to write.</summary>
    /// <param name="Text">The paragraph text.</param>
    /// <param name="StyleId">A <c>w:pStyle</c> value such as <c>Heading1</c>; null for body text.</param>
    public sealed record Para(string Text, string? StyleId = null)
    {
        /// <summary>A <c>Heading1</c> paragraph — what doc 11 calls a strong heading.</summary>
        public static Para Heading(string text) => new(text, "Heading1");
    }

    /// <summary>Writes a document with the given paragraphs and returns the path.</summary>
    /// <param name="path">Absolute destination path, <c>.docx</c>.</param>
    /// <param name="paragraphs">The paragraphs, in document order.</param>
    public static string Write(string path, IEnumerable<Para> paragraphs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(paragraphs);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document();
        var body = main.Document.AppendChild(new Body());

        var stylePart = main.AddNewPart<StyleDefinitionsPart>();
        stylePart.Styles = BuildHeadingStyles();
        stylePart.Styles.Save();

        foreach (var para in paragraphs) body.AppendChild(BuildParagraph(para));

        main.Document.Save();
        return path;
    }

    /// <summary>
    /// The canonical fixture: one entry per date format doc 11's matcher list names, plus one heading
    /// with no parseable date at all. Every date lands in <paramref name="year"/>.
    /// </summary>
    /// <param name="path">Absolute destination path.</param>
    /// <param name="year">The book year the dates belong to; the expectations below assume 2024.</param>
    public static string WriteMixedFormats(string path, int year = 2024) => Write(path,
    [
        // explicitHeading — a heading whose entire text is a full date, weekday included.
        Para.Heading($"Friday, January 5, {year}"),
        new Para("We drove up to the cabin after work and got the fire going before dark."),
        new Para("The kids stayed up far too late."),

        // monthDayPrefix — a body paragraph opening with a month name and day.
        new Para("January 12 — Sledding on the hill behind the school until our gloves froze."),

        // numericDate — a numeric date at the start of a paragraph.
        new Para("1/20 — Snow day. Nobody left the house and nobody complained."),

        // weekdayOrdinal — a weekday plus a bare ordinal, no month.
        new Para("Monday the 22nd was the first day the sun came back out."),

        // range — a span written with a tight dash.
        new Para("January 25–27 we had my parents staying with us for the long weekend."),

        // No date anywhere: doc 11 requires this heading be surfaced, never silently swallowed.
        Para.Heading("Some Thoughts About This Winter"),
        new Para("I keep meaning to write these down closer to when they happen."),
    ]);

    /// <summary>The dates <see cref="WriteMixedFormats"/> must resolve to, keyed by matcher name.</summary>
    /// <param name="year">The book year used when the document was written.</param>
    public static IReadOnlyList<(string Matcher, DateOnly Start, DateOnly End)> ExpectedMixedFormats(int year = 2024) =>
    [
        ("explicitHeading", new DateOnly(year, 1, 5), new DateOnly(year, 1, 5)),
        ("monthDayPrefix", new DateOnly(year, 1, 12), new DateOnly(year, 1, 12)),
        ("numericDate", new DateOnly(year, 1, 20), new DateOnly(year, 1, 20)),
        ("weekdayOrdinal", new DateOnly(year, 1, 22), new DateOnly(year, 1, 22)),
        ("range", new DateOnly(year, 1, 25), new DateOnly(year, 1, 27)),
    ];

    /// <summary>The heading text of the deliberately undated entry.</summary>
    public const string UndatedHeading = "Some Thoughts About This Winter";

    private static Paragraph BuildParagraph(Para para)
    {
        var paragraph = new Paragraph();

        if (para.StyleId is { } styleId)
        {
            paragraph.ParagraphProperties = new ParagraphProperties(
                new ParagraphStyleId { Val = styleId },
                new OutlineLevel { Val = 0 });
        }

        paragraph.AppendChild(new Run(new Text(para.Text) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }

    private static Styles BuildHeadingStyles()
    {
        var styles = new Styles();
        for (var level = 1; level <= 6; level++)
        {
            styles.AppendChild(new Style
            {
                Type = StyleValues.Paragraph,
                StyleId = $"Heading{level}",
                StyleName = new StyleName { Val = $"heading {level}" },
                StyleParagraphProperties = new StyleParagraphProperties(new OutlineLevel { Val = level - 1 }),
                StyleRunProperties = new StyleRunProperties(new Bold()),
            });
        }

        return styles;
    }
}
