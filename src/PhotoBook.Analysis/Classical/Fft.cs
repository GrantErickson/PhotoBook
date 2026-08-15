namespace PhotoBook.Analysis.Classical;

/// <summary>
/// A minimal in-place iterative radix-2 Cooley–Tukey FFT, plus the separable 2-D transform the
/// spectral-residual saliency detector runs on its 64×64 grid. Sizes must be powers of two.
/// <para>
/// Written out rather than approximated: at 64×64 the exact transform costs well under a
/// millisecond, so the saliency stage gets the real log-spectrum residual instead of a stand-in,
/// and the whole classical pipeline stays inside doc 06's 20 ms classical budget.
/// </para>
/// </summary>
internal static class Fft
{
    /// <summary>In-place 1-D transform of <paramref name="re"/>/<paramref name="im"/>; length must be a power of two.</summary>
    /// <param name="re">Real parts.</param>
    /// <param name="im">Imaginary parts.</param>
    /// <param name="offset">Start index of the sequence inside the arrays.</param>
    /// <param name="stride">Element stride, so rows and columns of a 2-D buffer both transform in place.</param>
    /// <param name="n">Sequence length; a power of two.</param>
    /// <param name="inverse">True for the inverse transform (conjugate twiddles plus 1/n scaling).</param>
    public static void Transform(double[] re, double[] im, int offset, int stride, int n, bool inverse)
    {
        if (n <= 1) return;
        if ((n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two.", nameof(n));

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                var a = offset + i * stride;
                var b = offset + j * stride;
                (re[a], re[b]) = (re[b], re[a]);
                (im[a], im[b]) = (im[b], im[a]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var uIndex = offset + (i + k) * stride;
                    var vIndex = offset + (i + k + len / 2) * stride;

                    var uRe = re[uIndex];
                    var uIm = im[uIndex];
                    var vRe = re[vIndex] * curRe - im[vIndex] * curIm;
                    var vIm = re[vIndex] * curIm + im[vIndex] * curRe;

                    re[uIndex] = uRe + vRe;
                    im[uIndex] = uIm + vIm;
                    re[vIndex] = uRe - vRe;
                    im[vIndex] = uIm - vIm;

                    var nextRe = curRe * wRe - curIm * wIm;
                    curIm = curRe * wIm + curIm * wRe;
                    curRe = nextRe;
                }
            }
        }

        if (!inverse) return;
        for (var i = 0; i < n; i++)
        {
            var index = offset + i * stride;
            re[index] /= n;
            im[index] /= n;
        }
    }

    /// <summary>In-place separable 2-D transform of a square <paramref name="size"/> × <paramref name="size"/> buffer.</summary>
    public static void Transform2D(double[] re, double[] im, int size, bool inverse)
    {
        ArgumentNullException.ThrowIfNull(re);
        ArgumentNullException.ThrowIfNull(im);
        for (var y = 0; y < size; y++) Transform(re, im, y * size, 1, size, inverse);
        for (var x = 0; x < size; x++) Transform(re, im, x, size, size, inverse);
    }
}
