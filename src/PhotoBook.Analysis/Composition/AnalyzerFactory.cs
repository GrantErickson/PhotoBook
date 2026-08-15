using PhotoBook.Analysis.Classical;
using PhotoBook.Analysis.Onnx;
using PhotoBook.Core.Model;

namespace PhotoBook.Analysis.Composition;

/// <summary>
/// Picks the analyzer for a book from its <see cref="AnalysisSettings"/> (kernel §10), always
/// landing on something that works.
/// <list type="bullet">
/// <item><description><c>local-onnx</c> — the default. Uses the ONNX pipeline when model files are
/// installed, wrapped in a <see cref="CompositeAnalyzer"/> over the model-free analyzer so partial
/// installs and inference failures degrade instead of failing. With no model files it falls back to
/// the model-free analyzer outright.</description></item>
/// <item><description><c>classical</c> — the model-free pipeline, explicitly.</description></item>
/// <item><description><c>azure-vision</c> — not implemented in v1 (doc 06 documents the adapter;
/// no code ships). Falls back exactly like an unavailable local pipeline, and says so.</description></item>
/// </list>
/// The choice never changes semantics: fusion, weighting, percentiles and tiers are shared code, so
/// swapping analyzers changes perception quality only (doc 06).
/// </summary>
public static class AnalyzerFactory
{
    /// <summary>Creates the analyzer for a book's settings.</summary>
    /// <param name="settings">The book's analysis settings; the default id is used when null.</param>
    /// <param name="options">Host configuration; defaults when null.</param>
    public static AnalyzerSelection Create(AnalysisSettings? settings, AnalyzerFactoryOptions? options = null)
    {
        options ??= AnalyzerFactoryOptions.Default;
        var requested = string.IsNullOrWhiteSpace(settings?.AnalyzerId)
            ? AnalyzerIds.LocalOnnx
            : settings!.AnalyzerId.Trim();

        var classical = new ClassicalAnalyzer(options.ImageLoader, options.Classical);

        if (string.Equals(requested, AnalyzerIds.Classical, StringComparison.OrdinalIgnoreCase))
        {
            return new AnalyzerSelection(classical, requested, Degraded: false,
                "Using the model-free analyzer, as configured.");
        }

        var prefix = string.Equals(requested, AnalyzerIds.LocalOnnx, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : string.Equals(requested, AnalyzerIds.AzureVision, StringComparison.OrdinalIgnoreCase)
                ? "The 'azure-vision' adapter is not implemented in v1. "
                : $"Unknown analyzer id '{requested}'. ";

        var onnxOptions = new OnnxAnalyzerOptions
        {
            ModelsDirectory = options.ModelsDirectory,
            ImageLoader = options.ImageLoader,
            Classical = options.Classical,
            IntraOpThreads = options.IntraOpThreads,
            Log = options.Log,
        };

        if (OnnxAnalyzer.TryCreate(onnxOptions, out var onnx, out var reason) && onnx is not null)
        {
            var composite = new CompositeAnalyzer(onnx, classical, ownsChildren: true, options.Log);
            var degraded = prefix.Length > 0 || !onnx.Models.HasAll;
            var message = prefix + reason;
            options.Log?.Invoke(message);
            return new AnalyzerSelection(composite, requested, degraded, message);
        }

        var fallbackMessage = prefix + reason;
        options.Log?.Invoke(fallbackMessage);
        return new AnalyzerSelection(classical, requested, Degraded: true, fallbackMessage);
    }

    /// <summary>Creates the analyzer for a book.</summary>
    /// <param name="book">The open book; its <see cref="Book.Analysis"/> settings decide.</param>
    /// <param name="options">Host configuration; defaults when null.</param>
    public static AnalyzerSelection CreateFor(Book book, AnalyzerFactoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(book);
        return Create(book.Analysis, options);
    }
}
