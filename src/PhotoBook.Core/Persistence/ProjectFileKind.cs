namespace PhotoBook.Core.Persistence;

/// <summary>The root documents of a project folder, each versioned independently (doc 04 §5).</summary>
public enum ProjectFileKind
{
    /// <summary><c>book.json</c>.</summary>
    Book,

    /// <summary><c>photos.json</c>.</summary>
    Photos,

    /// <summary><c>journal.json</c>.</summary>
    Journal,

    /// <summary><c>chapters/{year}-{month:00}.json</c>.</summary>
    Chapter,
}
