namespace PhotoBook.Core.Model;

/// <summary>A level of the style cascade (doc 10 §2) — the source of the UI's inheritance chip.</summary>
public enum StyleLevel
{
    /// <summary>The shipped defaults in <see cref="BuiltInStyles"/>.</summary>
    Default,

    /// <summary>The book's global style in <c>book.json</c>.</summary>
    Book,

    /// <summary>The chapter's sparse override.</summary>
    Chapter,

    /// <summary>The page's sparse override.</summary>
    Page,
}
