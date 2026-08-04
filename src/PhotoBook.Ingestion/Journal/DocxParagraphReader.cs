using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace PhotoBook.Ingestion.Journal;

/// <summary>
/// Flattens <c>w:document/w:body</c> into the ordered <see cref="ParagraphRecord"/> stream of doc 11,
/// using the OpenXML SDK directly — no Word interop, no Word installation (ADR-0008).
/// <para>Flattening rules, verbatim from doc 11:</para>
/// <list type="bullet">
/// <item><description>Tracked changes are read as if accepted: <c>w:ins</c> content is kept,
/// <c>w:del</c> content dropped.</description></item>
/// <item><description>Tables are flattened row-major; each cell contributes its paragraphs in order.</description></item>
/// <item><description>Headers, footers, footnotes, endnotes, comments and page-anchored text boxes are
/// ignored entirely; embedded images never enter the journal (photos come through the photo pipeline, R1).</description></item>
/// <item><description>Empty paragraphs are dropped but counted as paragraph breaks.</description></item>
/// <item><description>Hyperlinks contribute their display text only.</description></item>
/// </list>
/// </summary>
public static partial class DocxParagraphReader
{
    /// <summary>Reads the paragraph stream from a <c>.docx</c> stream. The stream is not disposed.</summary>
    /// <param name="content">The document bytes; readable and seekable.</param>
    /// <exception cref="InvalidDataException">The stream is not a readable Word document.</exception>
    public static IReadOnlyList<ParagraphRecord> Read(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.CanSeek) content.Position = 0;

        try
        {
            using var document = WordprocessingDocument.Open(content, isEditable: false);
            return Read(document);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or FileFormatException or ArgumentException)
        {
            throw new InvalidDataException(
                "The journal could not be read as a Word (.docx) document. Save it from Word as .docx and try again.", ex);
        }
    }

    /// <summary>Reads the paragraph stream from an already-open package.</summary>
    /// <param name="document">The open Word package.</param>
    public static IReadOnlyList<ParagraphRecord> Read(WordprocessingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null) return [];

        var styles = StyleIndex.Build(document);
        var records = new List<ParagraphRecord>();
        var pendingBreaks = 0;

        // Descendants<Paragraph>() visits the body in document order and therefore walks tables
        // row-major and content controls inline — exactly the flattening doc 11 asks for.
        foreach (var paragraph in body.Descendants<Paragraph>())
        {
            if (IsInsideTextBox(paragraph)) continue;

            var text = ExtractText(paragraph);
            if (string.IsNullOrWhiteSpace(text))
            {
                pendingBreaks++;
                continue;
            }

            var styleId = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
            var outlineLevel = paragraph.ParagraphProperties?.OutlineLevel?.Val?.Value ?? styles.OutlineLevel(styleId);
            var isBoldOnly = IsBoldOnly(paragraph, styles, styleId);
            var isAllCaps = IsAllCaps(paragraph, text);
            var strongHeading = styles.IsHeadingStyle(styleId) || outlineLevel is >= 0 and <= 2;

            records.Add(new ParagraphRecord
            {
                Index = records.Count,
                Text = text,
                StyleId = styleId,
                OutlineLevel = outlineLevel,
                IsBoldOnly = isBoldOnly,
                IsAllCaps = isAllCaps,
                IsListItem = paragraph.ParagraphProperties?.NumberingProperties is not null,
                PrecedingBreaks = pendingBreaks,
                IsStrongHeading = strongHeading,
                IsHeadingCandidate = strongHeading || IsWeakHeading(text, isBoldOnly, isAllCaps),
            });

            pendingBreaks = 0;
        }

        return records;
    }

    /// <summary>
    /// The doc 11 weak-heading heuristic: short bold or all-caps text that does not read like a
    /// sentence. It only decides who gets first crack at date matching, never what a date means.
    /// </summary>
    private static bool IsWeakHeading(string text, bool isBoldOnly, bool isAllCaps) =>
        (isBoldOnly || isAllCaps) &&
        text.Length <= 60 &&
        !text.EndsWith('.') && !text.EndsWith('!') && !text.EndsWith('?');

    private static bool IsInsideTextBox(OpenXmlElement element) =>
        element.Ancestors<TextBoxContent>().Any();

    private static string ExtractText(Paragraph paragraph)
    {
        var text = new StringBuilder();

        foreach (var run in paragraph.Descendants<Run>())
        {
            // w:del content is dropped; w:ins content is kept, so the document reads as if all tracked
            // changes were accepted (doc 11).
            if (run.Ancestors<DeletedRun>().Any()) continue;

            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case Text t:
                        text.Append(t.Text);
                        break;
                    case TabChar:
                    case Break:
                        text.Append(' ');
                        break;
                    case NoBreakHyphen:
                        text.Append('-');
                        break;
                    case SoftHyphen:
                        break;
                    case DeletedText:
                        break;                                   // struck-through text never contributes
                }
            }
        }

        return Whitespace().Replace(text.ToString(), " ").Trim();
    }

    private static bool IsBoldOnly(Paragraph paragraph, StyleIndex styles, string? styleId)
    {
        var sawRun = false;
        foreach (var run in paragraph.Descendants<Run>())
        {
            if (run.Ancestors<DeletedRun>().Any()) continue;
            var runText = string.Concat(run.Descendants<Text>().Select(t => t.Text));
            if (string.IsNullOrWhiteSpace(runText)) continue;

            sawRun = true;
            var bold = run.RunProperties?.Bold;
            var isBold = bold is not null && (bold.Val is null || bold.Val.Value);
            if (!isBold && !styles.IsBold(run.RunProperties?.RunStyle?.Val?.Value) && !styles.IsBold(styleId))
                return false;
        }

        return sawRun;
    }

    private static bool IsAllCaps(Paragraph paragraph, string text)
    {
        if (text.Any(char.IsLetter) && string.Equals(text, text.ToUpperInvariant(), StringComparison.Ordinal))
            return true;

        var sawRun = false;
        foreach (var run in paragraph.Descendants<Run>())
        {
            if (run.Ancestors<DeletedRun>().Any()) continue;
            var runText = string.Concat(run.Descendants<Text>().Select(t => t.Text));
            if (string.IsNullOrWhiteSpace(runText)) continue;

            sawRun = true;
            var caps = run.RunProperties?.Caps;
            if (caps is null || (caps.Val is not null && !caps.Val.Value)) return false;
        }

        return sawRun;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Resolved style facts: which styles are headings, which are bold, and their outline levels.</summary>
    private sealed partial class StyleIndex
    {
        private readonly Dictionary<string, (int? Outline, bool Bold, bool Heading)> _styles =
            new(StringComparer.OrdinalIgnoreCase);

        public static StyleIndex Build(WordprocessingDocument document)
        {
            var index = new StyleIndex();
            var styles = document.MainDocumentPart?.StyleDefinitionsPart?.Styles;
            if (styles is null) return index;

            foreach (var style in styles.Elements<Style>())
            {
                var id = style.StyleId?.Value;
                if (string.IsNullOrEmpty(id)) continue;

                var outline = style.StyleParagraphProperties?.OutlineLevel?.Val?.Value;
                var bold = style.StyleRunProperties?.Bold is { } b && (b.Val is null || b.Val.Value);
                var name = style.StyleName?.Val?.Value ?? id;
                var heading = HeadingStyle().IsMatch(id) || HeadingStyle().IsMatch(name.Replace(" ", string.Empty));
                index._styles[id] = (outline, bold, heading);
            }

            return index;
        }

        public int? OutlineLevel(string? styleId) =>
            styleId is not null && _styles.TryGetValue(styleId, out var style) ? style.Outline : null;

        public bool IsBold(string? styleId) =>
            styleId is not null && _styles.TryGetValue(styleId, out var style) && style.Bold;

        public bool IsHeadingStyle(string? styleId)
        {
            if (string.IsNullOrEmpty(styleId)) return false;
            if (_styles.TryGetValue(styleId, out var style) && style.Heading) return true;
            return HeadingStyle().IsMatch(styleId);
        }

        [GeneratedRegex(@"^Heading[1-6]$", RegexOptions.IgnoreCase)]
        private static partial Regex HeadingStyle();
    }
}
