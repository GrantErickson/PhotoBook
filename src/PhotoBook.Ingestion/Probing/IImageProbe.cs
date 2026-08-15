namespace PhotoBook.Ingestion.Probing;

/// <summary>
/// Reads the metadata ingestion needs from an image file: oriented dimensions plus the EXIF fields
/// that feed the date chain (doc 05). It is the seam between <c>PhotoBook.Ingestion</c> and the
/// decode backend: <see cref="ImageDecoderProbe"/> delegates to <c>PhotoBook.Imaging</c>'s
/// Magick.NET pipeline (ADR-0004), and <see cref="ImageMetadataProbe"/> is the dependency-free
/// fallback that keeps the local-folder path working even when no imaging backend can start.
/// <para>Contract rules:</para>
/// <list type="bullet">
/// <item><description>A probe never throws for bad input — it returns
/// <see cref="ImageProbeResult.Failure"/>, because one unreadable file must not abort an import
/// batch (doc 05).</description></item>
/// <item><description>Dimensions are reported <b>after</b> EXIF orientation, matching
/// <see cref="PhotoBook.Core.Model.Photo.Width"/>/<see cref="PhotoBook.Core.Model.Photo.Height"/>.</description></item>
/// <item><description>Probing is read-only: it never writes to the file or to the project.</description></item>
/// </list>
/// </summary>
public interface IImageProbe
{
    /// <summary>Probes an image file.</summary>
    /// <param name="filePath">Absolute path of the bytes to read; it may carry a temporary extension.</param>
    /// <param name="originalFileName">
    /// The file name the source knows the item by, used only as a format hint when the bytes are
    /// ambiguous.
    /// </param>
    ImageProbeResult Probe(string filePath, string originalFileName);
}
