namespace PhotoBook.Analysis.Onnx;

/// <summary>
/// Whether the local ONNX pipeline can run, and what to tell the user if it cannot. Probing never
/// throws and never loads a model — it is safe to call on the UI thread to render a settings page.
/// </summary>
/// <param name="IsAvailable">True when at least one model file was found.</param>
/// <param name="Models">The probe result, including the folder that was searched.</param>
/// <param name="Reason">A human-readable sentence for the settings page and the log.</param>
public sealed record OnnxAvailability(bool IsAvailable, OnnxModelSet Models, string Reason)
{
    /// <summary>Probes a models folder and phrases the outcome.</summary>
    /// <param name="modelsDirectory">Folder to probe; <see cref="OnnxModelSet.DefaultDirectory"/> when null.</param>
    public static OnnxAvailability Probe(string? modelsDirectory = null)
    {
        var models = OnnxModelSet.Probe(modelsDirectory);
        if (!models.HasAny)
        {
            return new OnnxAvailability(false, models,
                $"No ONNX model files found in '{models.Directory}'. Expected {OnnxModelSet.FaceModelFileName}, " +
                $"{OnnxModelSet.SaliencyModelFileName} and {OnnxModelSet.AestheticModelFileName}. " +
                "The app runs the model-free analyzer instead — everything works, faces are simply not detected.");
        }

        if (models.HasAll)
            return new OnnxAvailability(true, models, $"All three ONNX models are installed in '{models.Directory}'.");

        return new OnnxAvailability(true, models,
            $"Partial ONNX install in '{models.Directory}'; missing {string.Join(", ", models.MissingFileNames)}. " +
            "The missing stages fall back to the model-free pipeline.");
    }
}
