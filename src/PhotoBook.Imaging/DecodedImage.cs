namespace PhotoBook.Imaging;

/// <summary>
/// A decoded raster in the one pixel layout the whole app agrees on.
///
/// <para><b>Layout: BGRA8888, 8 bits per channel, 4 bytes per pixel, top-left origin, row-major.</b></para>
///
/// <para>
/// Byte <c>i*4+0</c> is blue, <c>+1</c> green, <c>+2</c> red, <c>+3</c> alpha — i.e. the same memory a
/// little-endian <c>uint32 0xAARRGGBB</c> occupies. Rows are tightly packed: <see cref="Stride"/> is
/// always <c>Width * 4</c>, with no padding, so the buffer can be handed to a consumer whole.
/// </para>
///
/// <para>This is exactly what both downstream consumers want, which is why it is the only layout produced:</para>
/// <list type="bullet">
/// <item><description>SkiaSharp: <c>SKColorType.Bgra8888</c> (see <see cref="AlphaIsPremultiplied"/>
/// for which <c>SKAlphaType</c> to declare).</description></item>
/// <item><description>WPF: <c>PixelFormats.Bgra32</c> for a straight buffer, <c>PixelFormats.Pbgra32</c>
/// once <see cref="PremultiplyInPlace"/> has run.</description></item>
/// </list>
///
/// <para>
/// Colors are sRGB and the pixels are already EXIF-oriented: the decoder applies orientation exactly
/// once, at decode, and nothing downstream ever rotates again (kernel §10).
/// </para>
///
/// <para>
/// Alpha is <b>straight (unpremultiplied)</b> as produced by <see cref="ImageDecoder"/>. Camera photos
/// are opaque, so for the overwhelmingly common case straight and premultiplied are identical and
/// <see cref="IsOpaque"/> is true. Call <see cref="PremultiplyInPlace"/> before handing a transparent
/// image to a consumer that expects premultiplied alpha.
/// </para>
/// </summary>
public sealed class DecodedImage
{
    /// <summary>Bytes per pixel in the BGRA8888 layout.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>Wraps an existing tightly packed BGRA8888 buffer.</summary>
    /// <param name="pixels">The pixel buffer; must be exactly <c>width * height * 4</c> bytes.</param>
    /// <param name="width">Pixel width, after EXIF orientation.</param>
    /// <param name="height">Pixel height, after EXIF orientation.</param>
    /// <param name="isOpaque">True when every alpha byte is 255.</param>
    /// <param name="alphaIsPremultiplied">True when the color channels are already scaled by alpha.</param>
    public DecodedImage(byte[] pixels, int width, int height, bool isOpaque = true, bool alphaIsPremultiplied = false)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        var expected = (long)width * height * BytesPerPixel;
        if (pixels.LongLength != expected)
            throw new ArgumentException(
                $"A {width}×{height} BGRA8888 buffer must be {expected} bytes; got {pixels.LongLength}.", nameof(pixels));

        Pixels = pixels;
        Width = width;
        Height = height;
        IsOpaque = isOpaque;
        AlphaIsPremultiplied = alphaIsPremultiplied || isOpaque;
    }

    /// <summary>The tightly packed BGRA8888 bytes, <c>Height * Stride</c> long.</summary>
    public byte[] Pixels { get; }

    /// <summary>Pixel width of the oriented image.</summary>
    public int Width { get; }

    /// <summary>Pixel height of the oriented image.</summary>
    public int Height { get; }

    /// <summary>Bytes per row: always <c>Width * 4</c>, no padding.</summary>
    public int Stride => Width * BytesPerPixel;

    /// <summary>True when every pixel is fully opaque — true for every camera photo.</summary>
    public bool IsOpaque { get; }

    /// <summary>
    /// True when color channels are premultiplied by alpha. An opaque image is trivially both, so this
    /// is true whenever <see cref="IsOpaque"/> is. Maps to <c>SKAlphaType.Premul</c> when true and
    /// <c>SKAlphaType.Unpremul</c> when false.
    /// </summary>
    public bool AlphaIsPremultiplied { get; private set; }

    /// <summary>The image's aspect ratio (width / height).</summary>
    public double Aspect => Height <= 0 ? double.NaN : (double)Width / Height;

    /// <summary>The buffer as a span, for zero-copy blits into an <c>SKBitmap</c> or a <c>WriteableBitmap</c>.</summary>
    public ReadOnlySpan<byte> AsSpan() => Pixels;

    /// <summary>
    /// Scales the color channels by alpha in place, converting a straight buffer into a premultiplied
    /// one (WPF <c>Pbgra32</c> / <c>SKAlphaType.Premul</c>). A no-op when the image is opaque or
    /// already premultiplied.
    /// </summary>
    /// <returns>This instance, for chaining.</returns>
    public DecodedImage PremultiplyInPlace()
    {
        if (AlphaIsPremultiplied) return this;

        var pixels = Pixels;
        for (var i = 0; i < pixels.Length; i += BytesPerPixel)
        {
            int a = pixels[i + 3];
            if (a == 255) continue;
            if (a == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                continue;
            }

            pixels[i] = (byte)((pixels[i] * a + 127) / 255);
            pixels[i + 1] = (byte)((pixels[i + 1] * a + 127) / 255);
            pixels[i + 2] = (byte)((pixels[i + 2] * a + 127) / 255);
        }

        AlphaIsPremultiplied = true;
        return this;
    }
}
