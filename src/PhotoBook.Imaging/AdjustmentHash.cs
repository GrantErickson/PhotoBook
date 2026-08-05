using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PhotoBook.Core.Model;

namespace PhotoBook.Imaging;

/// <summary>
/// The second half of a derived-pixel cache key: a short, stable digest of an
/// <see cref="ImageAdjustments"/>. Doc 02 fixes the key as
/// <c>{contentHash}:{tier}:{adjustmentStackHash}</c> — editing a photo produces new cache entries and
/// the stale ones are swept later, which is what makes an edit invalidate exactly what it must and
/// nothing else.
///
/// <para>
/// Determinism is the whole point, so the digest is taken over a canonical text form: parameters in a
/// fixed order, rounded to four decimals, formatted with the invariant culture. The same stack
/// therefore hashes identically across machines, runs, and app versions — a cache built yesterday is
/// still valid today.
/// </para>
/// </summary>
public static class AdjustmentHash
{
    /// <summary>Hex characters in a key.</summary>
    public const int KeyLength = 12;

    /// <summary>
    /// The key for the identity stack: the empty string. An unedited photo caches under the bare
    /// <c>{contentHash}.jpg</c> name of doc 05, which keeps the common case readable on disk.
    /// </summary>
    public const string IdentityKey = "";

    /// <summary>Computes the key for an adjustment set; <see cref="IdentityKey"/> when nothing is applied.</summary>
    /// <param name="adjustments">The adjustments; null counts as identity.</param>
    public static string Compute(ImageAdjustments? adjustments)
    {
        if (adjustments is null) return IdentityKey;
        var normalized = adjustments.Normalized();
        if (normalized.IsIdentity) return IdentityKey;

        var canonical = Canonicalize(normalized);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(digest)[..KeyLength];
    }

    /// <summary>Computes the key for a persisted catalog stack.</summary>
    /// <param name="stack">The photo's stored adjustments; null counts as identity.</param>
    public static string Compute(AdjustmentStack? stack) => Compute(ImageAdjustments.From(stack));

    /// <summary>
    /// The exact text the digest is taken over. Exposed because a cache key you cannot inspect is a
    /// cache key you cannot debug.
    /// </summary>
    /// <param name="adjustments">The adjustments to render.</param>
    public static string Canonicalize(ImageAdjustments adjustments)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        var a = adjustments.Normalized();
        // The leading tag versions the canonical text, not the project file. Widening the parameter
        // set changes what every non-identity stack hashes to, so it is bumped deliberately: the old
        // cache entries simply stop being named by any live key and CollectOrphansAsync sweeps them on
        // the next open. Nothing under cache/ is user intent (kernel §5), so this costs decode time
        // once and nothing else. The identity key stays the empty string, so an unedited photo's
        // thumbnails are untouched.
        var sb = new StringBuilder(200);
        sb.Append("v2;");
        Append(sb, "rot", a.Rotate);
        Append(sb, "str", a.Straighten);
        Append(sb, "flh", a.FlipHorizontal ? 1 : 0);
        Append(sb, "exp", a.ExposureEv);
        Append(sb, "bri", a.Brightness);
        Append(sb, "con", a.Contrast);
        Append(sb, "hig", a.Highlights);
        Append(sb, "sha", a.Shadows);
        Append(sb, "whi", a.Whites);
        Append(sb, "blk", a.Blacks);
        Append(sb, "tem", a.Temperature);
        Append(sb, "tin", a.Tint);
        Append(sb, "sat", a.Saturation);
        Append(sb, "vib", a.Vibrance);
        Append(sb, "nr", a.NoiseReduction);
        Append(sb, "cla", a.Clarity);
        Append(sb, "shp", a.Sharpen);
        Append(sb, "vig", a.Vignette);
        Append(sb, "bw", a.BlackAndWhite ? 1 : 0);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string name, double value)
    {
        // Round before formatting so 0.30000000000000004 and 0.3 are the same cache entry, and add 0.0
        // so a negative zero never renders as "-0.0000".
        var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero) + 0.0;
        sb.Append(name).Append('=')
          .Append(rounded.ToString("0.0000", CultureInfo.InvariantCulture))
          .Append(';');
    }
}
