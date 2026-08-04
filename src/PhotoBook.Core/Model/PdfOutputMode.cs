using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>How pages are imposed in the exported PDF (doc 12).</summary>
public enum PdfOutputMode
{
    /// <summary>One PDF page per book page, recto and verso in reading order — the default.</summary>
    [JsonStringEnumMemberName("single-pages")] SinglePages,

    /// <summary>One PDF page per facing pair.</summary>
    [JsonStringEnumMemberName("spreads")] Spreads,
}
