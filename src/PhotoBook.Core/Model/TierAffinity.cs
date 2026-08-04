using System.Text.Json.Serialization;

namespace PhotoBook.Core.Model;

/// <summary>
/// A slot's preferred <see cref="Tier"/> (doc 07). A <b>soft</b> preference only: the Hungarian
/// assignment adds cost for a mismatch, it never filters — a chapter may simply have no S-tier
/// photos left.
/// </summary>
public enum TierAffinity
{
    /// <summary>Prefers S-tier photos.</summary>
    [JsonStringEnumMemberName("S")] S,

    /// <summary>Prefers A-tier photos.</summary>
    [JsonStringEnumMemberName("A")] A,

    /// <summary>Prefers B-tier photos.</summary>
    [JsonStringEnumMemberName("B")] B,

    /// <summary>Prefers C-tier photos.</summary>
    [JsonStringEnumMemberName("C")] C,

    /// <summary>No preference; contributes zero tier cost.</summary>
    Any,
}
