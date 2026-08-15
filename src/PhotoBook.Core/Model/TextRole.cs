namespace PhotoBook.Core.Model;

/// <summary>What a <see cref="TextSlot"/> holds (kernel §6, doc 07).</summary>
public enum TextRole
{
    /// <summary>A day's journal text (R2, R5).</summary>
    Journal,

    /// <summary>A rare standalone caption block placed beside an image.</summary>
    Caption,

    /// <summary>The Chapter title; the one role allowed to overlap image slots (R24).</summary>
    MonthTitle,
}
