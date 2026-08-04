using System.Globalization;

namespace PhotoBook.Engine;

/// <summary>
/// The engine's single source of randomness (doc 08 §9): <c>rand(entityId) =
/// SplitMix64(seed XOR Fnv1a64(entityStableId))</c>. There is no <see cref="Random"/>, no
/// <c>Guid.NewGuid</c> and no clock anywhere in <c>PhotoBook.Engine</c>; every "random" value is a
/// pure function of the book Seed and a <b>stable</b> id (photo content hash, template id, day
/// date, page-run index) — never of array position or iteration order.
/// </summary>
public static class LayoutRandom
{
    private const ulong Fnv64OffsetBasis = 14695981039346656037UL;
    private const ulong Fnv64Prime = 1099511628211UL;

    /// <summary>FNV-1a over the UTF-16 code units of <paramref name="value"/>, byte-wise and culture-free.</summary>
    public static ulong Fnv1a64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var hash = Fnv64OffsetBasis;
        foreach (var c in value)
        {
            hash ^= (byte)(c & 0xFF);
            hash *= Fnv64Prime;
            hash ^= (byte)(c >> 8);
            hash *= Fnv64Prime;
        }

        return hash;
    }

    /// <summary>The SplitMix64 finalizer — a full-period 64-bit mixer with no state to carry.</summary>
    public static ulong SplitMix64(ulong state)
    {
        var z = state + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>The doc 08 §9 draw: <c>SplitMix64(seed XOR Fnv1a64(entityStableId))</c>.</summary>
    public static ulong Hash(ulong seed, string entityStableId) =>
        SplitMix64(seed ^ Fnv1a64(entityStableId));

    /// <summary>A uniform double in <c>[0, 1)</c> derived from <see cref="Hash"/>; 53 significant bits.</summary>
    public static double Unit(ulong seed, string entityStableId) =>
        (Hash(seed, entityStableId) >> 11) * (1.0 / 9007199254740992.0);

    /// <summary>A uniform double in <c>[-bound, +bound]</c> — the template-score jitter of doc 08 §6.</summary>
    public static double Signed(ulong seed, string entityStableId, double bound) =>
        (Unit(seed, entityStableId) * 2.0 - 1.0) * bound;

    /// <summary>
    /// A deterministic, content-derived page id (<c>pg-</c> + 16 hex characters). The engine cannot
    /// use <see cref="PhotoBook.Core.Model.Ids.NewPageId"/>, which mixes in the wall clock and a CSPRNG
    /// and would break byte-stable output (doc 08 §1, §9).
    /// </summary>
    /// <param name="seed">The book seed.</param>
    /// <param name="stableKey">A key built only from stable ids — dates, photo ids, run indices.</param>
    public static string PageId(ulong seed, string stableKey) =>
        "pg-" + Hash(seed, stableKey).ToString("x16", CultureInfo.InvariantCulture);
}
