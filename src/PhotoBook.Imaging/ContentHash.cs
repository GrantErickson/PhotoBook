using System.Security.Cryptography;
using PhotoBook.Core.Model;

namespace PhotoBook.Imaging;

/// <summary>
/// The stable content identity of a source file (doc 04 §7, doc 05 "Download to originals"). Photo
/// identity <em>is</em> content identity in this app: the same bytes always produce the same hash, on
/// any machine, in any future version, so re-importing a file is a no-op and every derived artifact
/// keys off it.
///
/// <para>
/// The hash is <b>SHA-256 of the raw file bytes</b>, rendered lowercase hex. The full 64-character
/// digest is what <see cref="Photo.ContentHash"/> stores and what cache keys use; the first
/// <see cref="ShortLength"/> characters form the <c>originals/</c> filename prefix and the photo id
/// (<see cref="Ids.PhotoId"/>). Nothing but file content enters the digest — no path, no timestamp,
/// no size — which is exactly why it survives a move, a rename, and a re-sync.
/// </para>
/// </summary>
public static class ContentHash
{
    /// <summary>Characters of the digest used in file names and ids: 16 (doc 04 §7).</summary>
    public const int ShortLength = Ids.HashPrefixLength;

    /// <summary>Characters in the full digest.</summary>
    public const int FullLength = 64;

    private const int BufferSize = 1024 * 1024;

    /// <summary>Hashes a file's bytes. Streaming, so a 100 MB HEIC never lands in memory whole.</summary>
    /// <param name="path">Absolute or relative path of the file to hash.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full 64-character lowercase hex SHA-256.</returns>
    public static async Task<string> OfFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await OfStreamAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>Hashes a stream from its current position to the end. The stream is not disposed.</summary>
    /// <param name="stream">The stream to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full 64-character lowercase hex SHA-256.</returns>
    public static async Task<string> OfStreamAsync(Stream stream, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var digest = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Hashes an in-memory buffer.</summary>
    /// <param name="bytes">The bytes to hash.</param>
    /// <returns>The full 64-character lowercase hex SHA-256.</returns>
    public static string OfBytes(ReadOnlySpan<byte> bytes)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, digest);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="destination"/> and hashes the bytes in the
    /// same pass — what ingestion wants when streaming a download into <c>originals/</c>, since the
    /// bytes are only read once.
    /// </summary>
    /// <param name="source">Stream to read from.</param>
    /// <param name="destination">Stream to write to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The full 64-character lowercase hex SHA-256 of everything copied.</returns>
    public static async Task<string> CopyAndHashAsync(Stream source, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            hasher.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return Convert.ToHexStringLower(hasher.GetHashAndReset());
    }

    /// <summary>
    /// The 16-character prefix used in <c>originals/</c> file names and photo ids. Accepts a full or
    /// already-shortened digest.
    /// </summary>
    /// <param name="contentHash">A hex digest.</param>
    public static string Short(string contentHash) => Ids.HashPrefix(contentHash);

    /// <summary>Normalizes a digest for comparison and for use as a cache key: trimmed and lowercased.</summary>
    /// <param name="contentHash">A hex digest.</param>
    public static string Normalize(string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        return contentHash.Trim().ToLowerInvariant();
    }

    /// <summary>True when the string looks like a digest this class produced — hex, and 16 or 64 long.</summary>
    /// <param name="contentHash">The candidate string.</param>
    public static bool IsValid(string? contentHash)
    {
        if (contentHash is null) return false;
        if (contentHash.Length is not (ShortLength or FullLength)) return false;
        foreach (var c in contentHash)
        {
            var isHex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!isHex) return false;
        }

        return true;
    }
}
