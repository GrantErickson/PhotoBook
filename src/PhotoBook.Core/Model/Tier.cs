using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>
/// How good a photo is relative to the other photos of its month (R26, kernel §4). Tier drives slot
/// size affinity in template scoring and the day's layout demand. Serialized uppercase.
/// </summary>
public enum Tier
{
    /// <summary>Top 10% of the month — hero shots: full-bleed and the largest slots.</summary>
    [JsonStringEnumMemberName("S")] S,

    /// <summary>Next 25% — large and medium slots.</summary>
    [JsonStringEnumMemberName("A")] A,

    /// <summary>Next 45% — standard grid slots. The default for a photo that has not been analyzed.</summary>
    [JsonStringEnumMemberName("B")] B,

    /// <summary>Bottom 20% — small slots; the engine prefers not to feature them.</summary>
    [JsonStringEnumMemberName("C")] C,
}
