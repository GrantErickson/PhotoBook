namespace PhotoBook.Core.Model;

/// <summary>Where a caption goes when the placed photo has one (doc 07, R5).</summary>
public enum CaptionPolicy
{
    /// <summary>This slot cannot show a caption.</summary>
    None,

    /// <summary>A caption band is reserved at the bottom of the slot rect, but only when a caption is present.</summary>
    Below,

    /// <summary>The caption sits inside the image on the bottom scrim — the policy for full-bleed heroes.</summary>
    Overlay,
}
