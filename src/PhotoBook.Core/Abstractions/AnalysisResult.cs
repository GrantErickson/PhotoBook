using PhotoBook.Core.Model;

namespace PhotoBook.Core.Abstractions;

/// <summary>
/// What an <see cref="IImageAnalyzer"/> returns for one photo (doc 06): region <em>proposals</em> and
/// scalar signals — raw material, never decisions.
/// </summary>
/// <param name="Regions">
/// Proposed focus regions of kind <see cref="FocusKind.Face"/> or <see cref="FocusKind.Saliency"/>, in
/// normalized image coordinates. <see cref="FocusKind.Person"/> and <see cref="FocusKind.User"/>
/// regions are added by fusion, not by analyzers.
/// </param>
/// <param name="Signals">The raw quality signals.</param>
public sealed record AnalysisResult(
    IReadOnlyList<FocusRegion> Regions,
    RawQualitySignals Signals);
