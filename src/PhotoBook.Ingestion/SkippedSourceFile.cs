namespace PhotoBook.Ingestion;

/// <summary>
/// A file a photo source passed over during enumeration — wrong extension, unreadable, or otherwise
/// not a candidate. It never becomes a <see cref="PhotoBook.Core.Abstractions.SourcePhoto"/>, but it
/// is listed in the Import Report so nothing disappears silently (doc 05).
/// </summary>
/// <param name="Path">The source path or item name that was skipped.</param>
/// <param name="Reason">Why it was skipped, in words the user can act on.</param>
public sealed record SkippedSourceFile(string Path, string Reason);

/// <summary>
/// Implemented by photo sources that can report what they passed over while enumerating, so
/// <see cref="PhotoImporter"/> can fold those into the Import Report. Optional: a source that never
/// skips anything simply does not implement it.
/// </summary>
public interface ISkipReportingPhotoSource
{
    /// <summary>Files skipped during the most recent enumeration.</summary>
    IReadOnlyList<SkippedSourceFile> SkippedFiles { get; }
}
