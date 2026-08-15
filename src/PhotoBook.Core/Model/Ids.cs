using System.Security.Cryptography;
using System.Text;

namespace PhotoBook.Core.Model;

/// <summary>
/// The id conventions of doc 03 §1 and doc 04 §7. All cross-file references are by string id, never
/// by array index: photo ids are content-derived, journal entry ids are content-derived, and
/// everything else is an opaque stable string assigned at creation.
/// </summary>
public static class Ids
{
    /// <summary>Prefix of a <see cref="Photo"/> id.</summary>
    public const string PhotoPrefix = "ph-";

    /// <summary>Prefix of a <see cref="JournalEntry"/> id.</summary>
    public const string JournalEntryPrefix = "je-";

    /// <summary>Prefix of a <see cref="Page"/> id.</summary>
    public const string PagePrefix = "pg-";

    /// <summary>Prefix of a <see cref="Book"/> id.</summary>
    public const string BookPrefix = "bk-";

    /// <summary>Number of hex characters of the content hash used in a photo id and original file name.</summary>
    public const int HashPrefixLength = 16;

    /// <summary>
    /// The short content-hash prefix used in <see cref="PhotoId"/> and in <c>originals/</c> file
    /// names: the first 16 lowercase hex characters of the full SHA-256 (doc 04 §7).
    /// </summary>
    public static string HashPrefix(string contentHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        var normalized = contentHash.Trim().ToLowerInvariant();
        return normalized.Length <= HashPrefixLength ? normalized : normalized[..HashPrefixLength];
    }

    /// <summary>
    /// The content-derived photo id <c>"ph-" + contentHash[..16]</c>. Photo identity <em>is</em>
    /// content identity, so re-importing identical bytes is a no-op (doc 04 §7, doc 03 invariant 11).
    /// </summary>
    public static string PhotoId(string contentHash) => PhotoPrefix + HashPrefix(contentHash);

    /// <summary>
    /// The content-derived journal entry id <c>"je-" + sha256(sourceKey + "#" + occurrence)[..12]</c>
    /// (doc 11). Content keys survive reordering and unrelated edits, where positional ids would not.
    /// </summary>
    public static string JournalEntryId(string sourceKey, int occurrence)
    {
        ArgumentNullException.ThrowIfNull(sourceKey);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceKey}#{occurrence}"));
        return JournalEntryPrefix + Convert.ToHexStringLower(hash)[..12];
    }

    /// <summary>A new opaque page id: <c>"pg-"</c> plus a time-sortable 26-character ULID.</summary>
    public static string NewPageId() => PagePrefix + NewUlid();

    /// <summary>A new opaque book id: <c>"bk-"</c> plus a time-sortable 26-character ULID.</summary>
    public static string NewBookId() => BookPrefix + NewUlid();

    /// <summary>
    /// A ULID: 48 bits of millisecond timestamp plus 80 random bits, Crockford base32 encoded.
    /// Sortable by creation time and stable forever once assigned.
    /// </summary>
    public static string NewUlid()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        Span<byte> bytes = stackalloc byte[16];
        var timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(timestamp & 0xFF);
            timestamp >>= 8;
        }

        RandomNumberGenerator.Fill(bytes[6..]);

        // 128 bits → 26 base32 characters (the first character carries only 2 significant bits).
        Span<char> chars = stackalloc char[26];
        var bitOffset = 0;
        for (var i = 0; i < 26; i++)
        {
            var value = 0;
            for (var bit = 0; bit < 5; bit++, bitOffset++)
            {
                var index = bitOffset - 2; // left-pad the 128 bits into 130 bit slots
                var b = index < 0 ? 0 : (bytes[index / 8] >> (7 - index % 8)) & 1;
                value = (value << 1) | b;
            }

            chars[i] = alphabet[value];
        }

        return new string(chars);
    }
}
