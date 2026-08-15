using ImageMagick;

namespace PhotoBook.Imaging;

/// <summary>
/// One-time ImageMagick process configuration (ADR-0004). Every entry point in this assembly calls
/// <see cref="Ensure"/> before touching a <see cref="MagickImage"/>, so the caps below are applied
/// exactly once per process no matter which class is used first.
/// <para>
/// The caps stand in for the locked-down <c>policy.xml</c> ADR-0004 asks for: a malformed or hostile
/// file cannot make the decoder allocate an unbounded raster. They are deliberately generous — a
/// 100 MP camera file must still open — they only stop the absurd.
/// </para>
/// </summary>
public static class MagickRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>Maximum accepted pixel width or height of a decoded image.</summary>
    public const uint MaxDimension = 65_535;

    /// <summary>
    /// Applies the process-wide ImageMagick limits. Idempotent and thread-safe; cheap enough to call
    /// on every operation.
    /// </summary>
    public static void Ensure()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;

            // Refuse absurd rasters rather than dying on an OOM deep inside native code.
            ResourceLimits.Width = MaxDimension;
            ResourceLimits.Height = MaxDimension;

            // Q8 is 1 byte/channel; 4 GB of pixel cache is far more than the ~1.5 GB working-set
            // budget of doc 02, so this is a backstop, not a working limit.
            ResourceLimits.Memory = 4UL * 1024 * 1024 * 1024;

            // A photo is one frame. Anything claiming thousands of frames (a bomb GIF) is not ours.
            ResourceLimits.ListLength = 512;

            _initialized = true;
        }
    }
}
