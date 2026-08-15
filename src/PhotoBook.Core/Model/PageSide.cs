namespace PhotoBook.Core.Model;

/// <summary>Which side of a spread a page falls on. Even page indices are left-hand pages (doc 07, doc 09).</summary>
public enum PageSide
{
    /// <summary>The verso (left-hand) page; mirrorable templates are mirrored here.</summary>
    Left,

    /// <summary>The recto (right-hand) page; templates are authored for this side.</summary>
    Right,
}
