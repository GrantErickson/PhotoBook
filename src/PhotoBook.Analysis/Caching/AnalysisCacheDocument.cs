using System.Text.Json;
using System.Text.Json.Serialization;
using PhotoBook.Core.Abstractions;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Caching;

/// <summary>
/// The on-disk shape of <c>cache/analysis/{contentHash}.{analyzerId}.json</c>, exactly as doc 06
/// prints it. Derived regions only — face and saliency; user and person regions are fusion output
/// and belong to <c>photos.json</c>.
/// </summary>
public sealed record AnalysisCacheDocument
{
    /// <summary>Schema version of this file; currently 1.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>The analyzer that produced the entry.</summary>
    public string AnalyzerId { get; set; } = string.Empty;

    /// <summary>The analyzer version; a mismatch makes the entry stale.</summary>
    public string AnalyzerVersion { get; set; } = string.Empty;

    /// <summary>The derived focus regions.</summary>
    public IList<FocusRegion> Regions { get; set; } = new List<FocusRegion>();

    /// <summary>The raw quality signals.</summary>
    public RawQualitySignals? Signals { get; set; }

    /// <summary>Members written by a newer revision, preserved verbatim on round-trip (doc 04 §4 rule 6).</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }

    /// <summary>Builds a document from an analyzer result.</summary>
    public static AnalysisCacheDocument From(string analyzerId, string analyzerVersion, AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AnalysisCacheDocument
        {
            AnalyzerId = analyzerId,
            AnalyzerVersion = analyzerVersion,
            Regions = result.Regions.ToList(),
            Signals = result.Signals,
        };
    }

    /// <summary>The analyzer result this document holds, or null when it is malformed.</summary>
    public AnalysisResult? ToResult() =>
        Signals is null ? null : new AnalysisResult(Regions.ToList(), Signals);
}
