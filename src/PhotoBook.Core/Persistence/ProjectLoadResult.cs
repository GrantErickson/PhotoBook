namespace PhotoBook.Core.Persistence;

/// <summary>A loaded document plus anything the loader had to do to get it (doc 04 §6).</summary>
/// <typeparam name="T">The document type.</typeparam>
/// <param name="Value">The loaded document.</param>
/// <param name="Notices">Recovery and warning notices; empty on a clean load.</param>
public sealed record ProjectLoadResult<T>(T Value, IReadOnlyList<ProjectLoadNotice> Notices)
{
    /// <summary>True when the loader had to recover a file from its backup.</summary>
    public bool RecoveredAnything => Notices.Any(n => n.Kind == ProjectLoadNoticeKind.RecoveredFromBackup);
}
