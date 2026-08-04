using PhotoBook.Core.Model;

namespace PhotoBook.Core.Abstractions;

/// <summary>
/// A place photos come from: a local folder today, a OneDrive album or folder via Microsoft Graph
/// tomorrow (doc 05, ADR-0011). Ingestion is the <b>only</b> code that talks to a source system; once
/// bytes are copied into <c>originals/</c> nothing downstream ever reads the source again, so a
/// project stays a self-contained archive (doc 04 §7).
/// <para>Contract rules:</para>
/// <list type="bullet">
/// <item><description><see cref="EnumerateAsync"/> streams, so a 2,000-item album can be paged and
/// shown incrementally, and it must be safe to call repeatedly — re-sync is a normal operation.</description></item>
/// <item><description>Implementations never write to the source; OneDrive access is requested
/// read-only (<c>Files.Read</c>).</description></item>
/// <item><description>Implementations never write into the project folder: they hand bytes to the
/// caller, which owns hashing, naming and cataloguing.</description></item>
/// <item><description>One bad item must not abort a batch — failures surface per item so the caller
/// can record them in the Import Report.</description></item>
/// <item><description>The layout engine never sees this interface; photos reach it as catalog records
/// (kernel §12).</description></item>
/// </list>
/// </summary>
public interface IPhotoSource
{
    /// <summary>The kind of source this instance represents, as recorded in <see cref="BookSource.Kind"/>.</summary>
    BookSourceKind Kind { get; }

    /// <summary>Human-readable description of the bound location, e.g. an album name or a folder path.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Lists the source's photos. Enumeration is lazy and may perform paged network calls; callers
    /// diff the result against the catalog by <see cref="SourcePhoto.SourceId"/> first and content
    /// hash second (doc 05).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    IAsyncEnumerable<SourcePhoto> EnumerateAsync(CancellationToken ct = default);

    /// <summary>
    /// Opens the bytes of one source photo for reading. The caller streams them into
    /// <c>originals/</c>, hashing as it goes, and disposes the stream.
    /// </summary>
    /// <param name="photo">An item previously returned by <see cref="EnumerateAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<Stream> OpenReadAsync(SourcePhoto photo, CancellationToken ct = default);
}
