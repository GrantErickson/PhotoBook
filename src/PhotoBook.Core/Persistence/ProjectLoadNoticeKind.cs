namespace PhotoBook.Core.Persistence;

/// <summary>Why a load produced a notice (doc 04 §6).</summary>
public enum ProjectLoadNoticeKind
{
    /// <summary>A stray <c>*.tmp</c> from an interrupted write was deleted before reading.</summary>
    DeletedStrayTemp,

    /// <summary>The file was missing or malformed and its rolling <c>.bak</c> was used instead.</summary>
    RecoveredFromBackup,

    /// <summary>An optional file did not exist and an empty document was substituted.</summary>
    MissingFileDefaulted,

    /// <summary>A sync-engine conflict copy such as <c>book-Copy.json</c> was found and ignored.</summary>
    IgnoredConflictCopy,
}
