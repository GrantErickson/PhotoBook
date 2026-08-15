using PhotoBook.Core.Abstractions;

namespace PhotoBook.Analysis.Caching;

/// <summary>
/// A cache that remembers nothing — for tests, for one-off re-analysis, and for callers that have no
/// project folder open. Everything analysis writes here is regenerable, so dropping it is always
/// legal.
/// </summary>
public sealed class NullAnalysisCache : IAnalysisCache
{
    /// <summary>The shared instance.</summary>
    public static NullAnalysisCache Instance { get; } = new();

    /// <inheritdoc/>
    public Task<AnalysisResult?> TryReadAsync(string contentHash, string analyzerId, string analyzerVersion, CancellationToken ct = default) =>
        Task.FromResult<AnalysisResult?>(null);

    /// <inheritdoc/>
    public Task WriteAsync(string contentHash, string analyzerId, string analyzerVersion, AnalysisResult result, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <inheritdoc/>
    public Task InvalidateAsync(string contentHash) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task<int> CollectOrphansAsync(IReadOnlyCollection<string> liveContentHashes, CancellationToken ct = default) =>
        Task.FromResult(0);
}
