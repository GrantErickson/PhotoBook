using PhotoBook.Analysis.Pixels;

namespace PhotoBook.Analysis.Classical;

/// <summary>
/// Hou &amp; Zhang spectral-residual saliency (CVPR 2007), implemented over a real FFT on a small
/// square grid. This is the no-model stand-in for U2-Netp (doc 06): without it, smart-crop on a
/// machine with no downloaded models would fall back to blind center-cropping, which is exactly the
/// failure the north star forbids.
/// <para>
/// The algorithm: downscale luma to <see cref="MapSize"/>², FFT, take the log amplitude spectrum,
/// subtract its local average (a 3×3 box blur) to leave the <em>spectral residual</em> — the part of
/// the spectrum that is <em>not</em> statistically ordinary — then inverse-transform that residual
/// with the original phase and smooth the squared magnitude. Ordinary, repeated structure (sky,
/// grass, wallpaper) cancels out; the unusual thing in the frame lights up.
/// </para>
/// </summary>
internal static class SpectralResidualSaliency
{
    /// <summary>Grid the residual is computed on. 64 keeps the transform trivial and matches the paper's scale.</summary>
    public const int MapSize = 64;

    /// <summary>Radius of the box blur that estimates the "average" log spectrum (the paper's 3×3 filter).</summary>
    public const int SpectrumBlurRadius = 1;

    /// <summary>Radius of the post-smoothing applied to the saliency map before thresholding.</summary>
    public const int SaliencyBlurRadius = 2;

    /// <summary>
    /// Computes a normalized <c>0..1</c> saliency map on a <paramref name="size"/>² grid from a
    /// luma plane of the same dimensions.
    /// </summary>
    /// <param name="gray">Luma plane, <c>size × size</c> entries, any scale.</param>
    /// <param name="size">Grid edge; must be a power of two.</param>
    public static float[] Compute(float[] gray, int size)
    {
        ArgumentNullException.ThrowIfNull(gray);
        if (gray.Length != size * size)
            throw new ArgumentException($"Expected {size * size} samples for a {size}×{size} grid.", nameof(gray));

        var n = size * size;
        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < n; i++) re[i] = gray[i];

        Fft.Transform2D(re, im, size, inverse: false);

        var logAmplitude = new float[n];
        var phaseRe = new double[n];
        var phaseIm = new double[n];
        for (var i = 0; i < n; i++)
        {
            var amplitude = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
            logAmplitude[i] = (float)Math.Log(amplitude + 1e-8);
            if (amplitude > 1e-12)
            {
                phaseRe[i] = re[i] / amplitude;
                phaseIm[i] = im[i] / amplitude;
            }
            else
            {
                phaseRe[i] = 1;
                phaseIm[i] = 0;
            }
        }

        var averaged = ImageOps.BoxBlur(logAmplitude, size, size, SpectrumBlurRadius);

        for (var i = 0; i < n; i++)
        {
            var residual = Math.Exp(logAmplitude[i] - averaged[i]);
            re[i] = residual * phaseRe[i];
            im[i] = residual * phaseIm[i];
        }

        Fft.Transform2D(re, im, size, inverse: true);

        var saliency = new float[n];
        for (var i = 0; i < n; i++) saliency[i] = (float)(re[i] * re[i] + im[i] * im[i]);

        var smoothed = ImageOps.ApproximateGaussian(saliency, size, size, SaliencyBlurRadius);
        return ImageOps.Normalize01(smoothed);
    }
}
