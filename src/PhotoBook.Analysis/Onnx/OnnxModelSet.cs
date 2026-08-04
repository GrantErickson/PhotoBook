namespace PhotoBook.Analysis.Onnx;

/// <summary>
/// Which ONNX model files are present in the app's <c>models/</c> folder.
/// <para>
/// The models are <b>not shipped</b> — their licences and their size (and the fact that the app must
/// be fully usable without them) keep them out of the repo, and <c>.gitignore</c> excludes
/// <c>models/*.onnx</c>. The user downloads them once and drops them in; this type is the only place
/// that knows their file names, and <see cref="OnnxAnalyzer"/> degrades cleanly for every file that
/// is absent.
/// </para>
/// <list type="table">
/// <item><term><c>face_detection_yunet_2023mar.onnx</c></term><description>YuNet face detection, ~230 KB — OpenCV Zoo.</description></item>
/// <item><term><c>u2netp.onnx</c></term><description>U2-Netp saliency, ~4.5 MB.</description></item>
/// <item><term><c>nima-mobilenet.onnx</c></term><description>NIMA/MobileNet aesthetics, ~13 MB, AVA-trained.</description></item>
/// </list>
/// </summary>
/// <param name="Directory">The folder that was probed.</param>
/// <param name="FaceModelPath">Absolute path of the YuNet model, or null when absent.</param>
/// <param name="SaliencyModelPath">Absolute path of the U2-Netp model, or null when absent.</param>
/// <param name="AestheticModelPath">Absolute path of the NIMA model, or null when absent.</param>
public sealed record OnnxModelSet(
    string Directory,
    string? FaceModelPath,
    string? SaliencyModelPath,
    string? AestheticModelPath)
{
    /// <summary>Folder name, relative to the app directory, the models are looked for in.</summary>
    public const string FolderName = "models";

    /// <summary>Canonical file name of the YuNet face detector.</summary>
    public const string FaceModelFileName = "face_detection_yunet_2023mar.onnx";

    /// <summary>Canonical file name of the U2-Netp saliency model.</summary>
    public const string SaliencyModelFileName = "u2netp.onnx";

    /// <summary>Canonical file name of the NIMA/MobileNet aesthetic model.</summary>
    public const string AestheticModelFileName = "nima-mobilenet.onnx";

    /// <summary>Accepted file names for the face model, canonical name first.</summary>
    public static IReadOnlyList<string> FaceModelFileNames { get; } =
        [FaceModelFileName, "face_detection_yunet.onnx", "yunet.onnx"];

    /// <summary>Accepted file names for the saliency model, canonical name first.</summary>
    public static IReadOnlyList<string> SaliencyModelFileNames { get; } =
        [SaliencyModelFileName, "u2net.onnx", "u2netp_320.onnx"];

    /// <summary>Accepted file names for the aesthetic model, canonical name first.</summary>
    public static IReadOnlyList<string> AestheticModelFileNames { get; } =
        [AestheticModelFileName, "nima.onnx", "nima_mobilenet.onnx"];

    /// <summary>The default folder: <c>models/</c> beside the executable.</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, FolderName);

    /// <summary>True when at least one model file was found — the condition for the ONNX analyzer to exist at all.</summary>
    public bool HasAny => FaceModelPath is not null || SaliencyModelPath is not null || AestheticModelPath is not null;

    /// <summary>True when the full doc 06 pipeline is installed.</summary>
    public bool HasAll => FaceModelPath is not null && SaliencyModelPath is not null && AestheticModelPath is not null;

    /// <summary>Canonical file names of the models that are missing — what the setup UI tells the user to download.</summary>
    public IReadOnlyList<string> MissingFileNames
    {
        get
        {
            var missing = new List<string>(3);
            if (FaceModelPath is null) missing.Add(FaceModelFileName);
            if (SaliencyModelPath is null) missing.Add(SaliencyModelFileName);
            if (AestheticModelPath is null) missing.Add(AestheticModelFileName);
            return missing;
        }
    }

    /// <summary>
    /// A short suffix naming which models are in play — <c>f</c> face, <c>s</c> saliency,
    /// <c>a</c> aesthetic. It rides in <see cref="OnnxAnalyzer.Version"/> so that adding a model
    /// later re-keys the analysis cache and the affected photos are re-analyzed (doc 06).
    /// </summary>
    public string Signature =>
        (FaceModelPath is not null ? "f" : string.Empty) +
        (SaliencyModelPath is not null ? "s" : string.Empty) +
        (AestheticModelPath is not null ? "a" : string.Empty);

    /// <summary>Probes a folder for the model files. Never throws: a missing folder is simply an empty set.</summary>
    /// <param name="directory">Folder to probe; <see cref="DefaultDirectory"/> when null or blank.</param>
    public static OnnxModelSet Probe(string? directory = null)
    {
        var folder = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : Path.GetFullPath(directory);
        if (!System.IO.Directory.Exists(folder)) return new OnnxModelSet(folder, null, null, null);

        return new OnnxModelSet(
            folder,
            FindFirst(folder, FaceModelFileNames),
            FindFirst(folder, SaliencyModelFileNames),
            FindFirst(folder, AestheticModelFileNames));
    }

    private static string? FindFirst(string folder, IReadOnlyList<string> fileNames)
    {
        foreach (var name in fileNames)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return path;
        }

        return null;
    }
}
