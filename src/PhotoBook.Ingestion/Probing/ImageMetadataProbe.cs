using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PhotoBook.Ingestion.Probing;

/// <summary>
/// The dependency-free default <see cref="IImageProbe"/>: it parses image containers directly for
/// the two things ingestion needs — oriented pixel dimensions and EXIF <c>DateTimeOriginal</c> —
/// without decoding a single pixel. JPEG, PNG, WebP, HEIC/HEIF, TIFF and BMP are understood, which
/// is exactly <see cref="SupportedImageFormats.Extensions"/>.
/// <para>
/// Why not Magick.NET here: the local-folder import path must work with no imaging backend wired up,
/// a 2,000-photo re-scan should not pay for 2,000 full decodes, and reading a header is far cheaper
/// than decoding. When <c>PhotoBook.Imaging</c>'s decoder is available it can be substituted through
/// <see cref="IImageProbe"/> — the pipeline is unchanged either way.
/// </para>
/// </summary>
public sealed class ImageMetadataProbe : IImageProbe
{
    /// <summary>How much of a file is read looking for headers and EXIF; headers live at the front.</summary>
    private const int MaxHeaderBytes = 16 * 1024 * 1024;

    /// <summary>A shared instance; the probe is stateless and thread-safe.</summary>
    public static ImageMetadataProbe Instance { get; } = new();

    /// <inheritdoc/>
    public ImageProbeResult Probe(string filePath, string originalFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);
            return Probe(stream, string.IsNullOrWhiteSpace(originalFileName) ? Path.GetFileName(filePath) : originalFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImageProbeResult.Failure(ex.Message);
        }
    }

    /// <summary>Probes image bytes that are already open. The stream is left open; the caller owns it.</summary>
    /// <param name="content">Readable, seekable image bytes.</param>
    /// <param name="fileName">File name, used as a format hint when the magic bytes are unrecognized.</param>
    public ImageProbeResult Probe(Stream content, string fileName)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            var bytes = ReadHeader(content);
            if (bytes.Length == 0) return ImageProbeResult.Failure("The file is empty.");
            return Parse(bytes, fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidDataException)
        {
            return ImageProbeResult.Failure(ex.Message);
        }
        catch (Exception ex)
        {
            // A malformed file must never abort an import batch (doc 05).
            return ImageProbeResult.Failure($"Unreadable image: {ex.Message}");
        }
    }

    private static byte[] ReadHeader(Stream content)
    {
        if (content.CanSeek) content.Position = 0;

        var capacity = content.CanSeek ? (int)Math.Min(content.Length, MaxHeaderBytes) : 0;
        using var buffer = new MemoryStream(capacity > 0 ? capacity : 128 * 1024);
        var chunk = new byte[64 * 1024];
        int read;
        while (buffer.Length < MaxHeaderBytes &&
               (read = content.Read(chunk, 0, (int)Math.Min(chunk.Length, MaxHeaderBytes - buffer.Length))) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        if (content.CanSeek) content.Position = 0;
        return buffer.ToArray();
    }

    private static ImageProbeResult Parse(byte[] bytes, string fileName)
    {
        var span = new ReadOnlySpan<byte>(bytes);

        if (StartsWith(span, [0xFF, 0xD8, 0xFF])) return ParseJpeg(bytes);
        if (StartsWith(span, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])) return ParsePng(bytes);
        if (span.Length > 12 && Ascii(span, 0, 4) == "RIFF" && Ascii(span, 8, 4) == "WEBP") return ParseWebp(bytes);
        if (span.Length > 12 && Ascii(span, 4, 4) == "ftyp") return ParseIsoBmff(bytes);
        if (StartsWith(span, "II*\0"u8) || StartsWith(span, "MM\0*"u8)) return ParseTiff(bytes);
        if (StartsWith(span, "BM"u8)) return ParseBmp(bytes);

        // The extension claimed an image type but the magic bytes say otherwise.
        var ext = SupportedImageFormats.NormalizedExtension(fileName);
        return ImageProbeResult.Failure(
            ext.Length > 0
                ? $"The file does not look like a {ext.TrimStart('.').ToUpperInvariant()} image."
                : "Unrecognized image format.");
    }

    // ---------------------------------------------------------------- JPEG

    private static ImageProbeResult ParseJpeg(byte[] bytes)
    {
        var span = new ReadOnlySpan<byte>(bytes);
        var width = 0;
        var height = 0;
        ExifData exif = default;

        var i = 2;
        while (i + 3 < span.Length)
        {
            if (span[i] != 0xFF) { i++; continue; }             // resync on a stray byte

            var marker = span[i + 1];
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (marker == 0xFF) { i++; continue; }
            if (marker == 0xD9) break;                          // end of image
            if (marker == 0xDA) break;                          // start of scan: nothing left for us

            if (i + 3 >= span.Length) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(i + 2, 2));
            if (length < 2) break;
            var payload = i + 4;
            var payloadLength = length - 2;
            if (payload + payloadLength > span.Length) break;

            // SOF0..SOF15 carry the frame dimensions; DHT/JPG/DAC share the 0xC_ range and do not.
            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
            if (isStartOfFrame && payloadLength >= 5 && width == 0)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(payload + 1, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(payload + 3, 2));
            }
            else if (marker == 0xE1 && payloadLength > 6 && Ascii(span, payload, 4) == "Exif" && exif.IsEmpty)
            {
                exif = TryReadExif(bytes, payload + 6, payloadLength - 6);
            }

            i = payload + payloadLength;
        }

        if (width == 0 || height == 0)
        {
            if (exif.PixelWidth > 0 && exif.PixelHeight > 0) { width = exif.PixelWidth; height = exif.PixelHeight; }
            else return ImageProbeResult.Failure("No JPEG frame header was found.");
        }

        return Build(width, height, exif);
    }

    // ---------------------------------------------------------------- PNG

    private static ImageProbeResult ParsePng(byte[] bytes)
    {
        var span = new ReadOnlySpan<byte>(bytes);
        var width = 0;
        var height = 0;
        ExifData exif = default;

        var i = 8;
        while (i + 8 <= span.Length)
        {
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(span.Slice(i, 4)), int.MaxValue - 12);
            var type = Ascii(span, i + 4, 4);
            var payload = i + 8;
            if (payload + length > span.Length) break;

            switch (type)
            {
                case "IHDR" when length >= 8:
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(payload, 4));
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(payload + 4, 4));
                    break;
                case "eXIf" when exif.IsEmpty:
                    exif = TryReadExif(bytes, payload, length);
                    break;
                case "IDAT":
                case "IEND":
                    i = span.Length;                            // pixel data begins: stop scanning
                    continue;
            }

            i = payload + length + 4;                           // + CRC
        }

        return width > 0 && height > 0
            ? Build(width, height, exif)
            : ImageProbeResult.Failure("The PNG has no IHDR chunk.");
    }

    // ---------------------------------------------------------------- WebP

    private static ImageProbeResult ParseWebp(byte[] bytes)
    {
        var span = new ReadOnlySpan<byte>(bytes);
        var width = 0;
        var height = 0;
        ExifData exif = default;

        var i = 12;
        while (i + 8 <= span.Length)
        {
            var type = Ascii(span, i, 4);
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(i + 4, 4)), int.MaxValue - 8);
            var payload = i + 8;
            if (payload + length > span.Length) break;

            switch (type)
            {
                case "VP8X" when length >= 10:
                    width = ReadUInt24LittleEndian(span.Slice(payload + 4, 3)) + 1;
                    height = ReadUInt24LittleEndian(span.Slice(payload + 7, 3)) + 1;
                    break;
                case "VP8 " when length >= 10 && width == 0:
                    // Lossy: the 3-byte start code 0x9D 0x01 0x2A precedes two 14-bit dimensions.
                    if (span[payload + 3] == 0x9D && span[payload + 4] == 0x01 && span[payload + 5] == 0x2A)
                    {
                        width = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(payload + 6, 2)) & 0x3FFF;
                        height = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(payload + 8, 2)) & 0x3FFF;
                    }

                    break;
                case "VP8L" when length >= 5 && width == 0:
                    // Lossless: signature byte 0x2F, then 14 bits width-1 and 14 bits height-1.
                    if (span[payload] == 0x2F)
                    {
                        var bits = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(payload + 1, 4));
                        width = (int)(bits & 0x3FFF) + 1;
                        height = (int)((bits >> 14) & 0x3FFF) + 1;
                    }

                    break;
                case "EXIF" when exif.IsEmpty:
                    var offset = length > 6 && Ascii(span, payload, 4) == "Exif" ? payload + 6 : payload;
                    exif = TryReadExif(bytes, offset, length - (offset - payload));
                    break;
            }

            i = payload + length + (length % 2);                 // RIFF chunks are word-aligned
        }

        return width > 0 && height > 0
            ? Build(width, height, exif)
            : ImageProbeResult.Failure("The WebP has no readable dimensions.");
    }

    // ---------------------------------------------------------------- HEIC / HEIF (ISO-BMFF)

    private static ImageProbeResult ParseIsoBmff(byte[] bytes)
    {
        var span = new ReadOnlySpan<byte>(bytes);
        var width = 0;
        var height = 0;
        var rotatedQuarterTurns = 0;

        // 'ispe' (image spatial extents) and 'irot' live under meta/iprp/ipco. The primary image is
        // the largest ispe in practice; thumbnails and depth maps are always smaller.
        foreach (var (type, start, length) in EnumerateBoxes(span, 0, span.Length, depth: 0))
        {
            switch (type)
            {
                case "ispe" when length >= 12:
                    var w = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(start + 4, 4));
                    var h = (int)BinaryPrimitives.ReadUInt32BigEndian(span.Slice(start + 8, 4));
                    if ((long)w * h > (long)width * height) { width = w; height = h; }

                    break;
                case "irot" when length >= 1:
                    rotatedQuarterTurns = span[start] & 0x03;
                    break;
            }
        }

        // HEIC keeps EXIF as a separate item whose payload sits in mdat behind an offset table; a
        // bounded scan for the standard "Exif\0\0" + TIFF header prefix finds it without an iloc walk.
        var exif = ScanForExif(bytes);

        if (rotatedQuarterTurns is 1 or 3) (width, height) = (height, width);

        if (width == 0 || height == 0)
        {
            if (exif.PixelWidth > 0 && exif.PixelHeight > 0) { width = exif.PixelWidth; height = exif.PixelHeight; }
            else return ImageProbeResult.Failure("The HEIF container has no image spatial extents box.");
        }

        return Build(width, height, exif);
    }

    /// <summary>Walks the ISO-BMFF box tree, descending only into the containers that can hold 'ispe'.</summary>
    private static List<(string Type, int PayloadStart, int PayloadLength)> EnumerateBoxes(
        ReadOnlySpan<byte> span, int start, int end, int depth)
    {
        var found = new List<(string, int, int)>();
        if (depth > 6) return found;

        var i = start;
        while (i + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(span.Slice(i, 4));
            var type = Ascii(span, i + 4, 4);
            var header = 8;

            if (size == 1)
            {
                if (i + 16 > end) break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(span.Slice(i + 8, 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - i;                                  // box runs to the end of the file
            }

            if (size < header || i + size > end) break;

            var payload = i + header;
            var payloadLength = (int)(size - header);

            switch (type)
            {
                case "meta":                                     // FullBox: 4 bytes of version/flags
                    found.AddRange(EnumerateBoxes(span, payload + 4, payload + payloadLength, depth + 1));
                    break;
                case "iprp":
                case "ipco":
                case "moov":
                case "trak":
                case "mdia":
                    found.AddRange(EnumerateBoxes(span, payload, payload + payloadLength, depth + 1));
                    break;
                case "ispe":
                    found.Add(("ispe", payload, payloadLength)); // FullBox; caller skips version/flags
                    break;
                case "irot":
                    found.Add(("irot", payload, payloadLength));
                    break;
            }

            i += (int)size;
        }

        return found;
    }

    // ---------------------------------------------------------------- TIFF

    private static ImageProbeResult ParseTiff(byte[] bytes)
    {
        var exif = TryReadExif(bytes, 0, bytes.Length);
        if (exif.IsEmpty) return ImageProbeResult.Failure("The TIFF header could not be read.");
        return exif.ImageWidth > 0 && exif.ImageHeight > 0
            ? Build(exif.ImageWidth, exif.ImageHeight, exif)
            : ImageProbeResult.Failure("The TIFF has no image dimensions.");
    }

    // ---------------------------------------------------------------- BMP

    private static ImageProbeResult ParseBmp(byte[] bytes)
    {
        var span = new ReadOnlySpan<byte>(bytes);
        if (span.Length < 26) return ImageProbeResult.Failure("The BMP header is truncated.");
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(14, 4));
        int width, height;
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(18, 2));
            height = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(20, 2));
        }
        else
        {
            width = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(18, 4));
            height = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(22, 4));
        }

        height = Math.Abs(height);                               // negative height = top-down rows
        return width > 0 && height > 0
            ? Build(width, height, default)
            : ImageProbeResult.Failure("The BMP has no readable dimensions.");
    }

    // ---------------------------------------------------------------- EXIF

    /// <summary>
    /// The EXIF fields ingestion cares about. <see cref="ImageWidth"/>/<see cref="ImageHeight"/> come
    /// from IFD0 (TIFF), <see cref="PixelWidth"/>/<see cref="PixelHeight"/> from the Exif sub-IFD.
    /// </summary>
    private readonly record struct ExifData(
        DateTime? DateTimeOriginal,
        string? SubSecTimeOriginal,
        string? Make,
        string? Model,
        int Orientation,
        int ImageWidth,
        int ImageHeight,
        int PixelWidth,
        int PixelHeight)
    {
        public bool IsEmpty => DateTimeOriginal is null && Orientation == 0 && Model is null &&
                               ImageWidth == 0 && PixelWidth == 0;
    }

    private static ImageProbeResult Build(int width, int height, ExifData exif)
    {
        // Orientation 5–8 transpose the image; the catalog stores oriented dimensions (doc 05).
        if (exif.Orientation is >= 5 and <= 8) (width, height) = (height, width);

        return new ImageProbeResult(
            Succeeded: true,
            Width: width,
            Height: height,
            DateTimeOriginal: exif.DateTimeOriginal,
            SubSecTimeOriginal: exif.SubSecTimeOriginal,
            CameraMake: exif.Make,
            CameraModel: exif.Model,
            Orientation: exif.Orientation);
    }

    /// <summary>Finds an embedded "Exif\0\0" + TIFF header anywhere in the buffer (HEIC's EXIF item).</summary>
    private static ExifData ScanForExif(byte[] bytes)
    {
        var span = new ReadOnlySpan<byte>(bytes);
        ReadOnlySpan<byte> marker = "Exif\0\0"u8;
        var offset = 0;
        while (offset < span.Length - 8)
        {
            var found = span[offset..].IndexOf(marker);
            if (found < 0) return default;
            var tiff = offset + found + marker.Length;
            var data = TryReadExif(bytes, tiff, span.Length - tiff);
            if (!data.IsEmpty) return data;
            offset = tiff;
        }

        return default;
    }

    private static ExifData TryReadExif(byte[] bytes, int offset, int length)
    {
        if (offset < 0 || length < 8 || offset + 8 > bytes.Length) return default;
        var span = new ReadOnlySpan<byte>(bytes, offset, Math.Min(length, bytes.Length - offset));

        bool bigEndian;
        if (span[0] == 'I' && span[1] == 'I' && span[2] == 0x2A && span[3] == 0x00) bigEndian = false;
        else if (span[0] == 'M' && span[1] == 'M' && span[2] == 0x00 && span[3] == 0x2A) bigEndian = true;
        else return default;

        var ifd0 = (int)ReadUInt32(span, 4, bigEndian);
        if (ifd0 <= 0 || ifd0 >= span.Length) return default;

        DateTime? dateTimeOriginal = null;
        DateTime? dateTimeDigitized = null;
        DateTime? dateTime = null;
        string? subSec = null;
        string? make = null;
        string? model = null;
        var orientation = 0;
        var imageWidth = 0;
        var imageHeight = 0;
        var pixelWidth = 0;
        var pixelHeight = 0;
        var exifIfd = 0;

        foreach (var entry in ReadIfd(span, ifd0, bigEndian))
        {
            switch (entry.Tag)
            {
                case 0x0100: imageWidth = (int)entry.AsUInt32(span, bigEndian); break;
                case 0x0101: imageHeight = (int)entry.AsUInt32(span, bigEndian); break;
                case 0x0110: model = entry.AsAscii(span); break;
                case 0x010F: make = entry.AsAscii(span); break;
                case 0x0112: orientation = (int)entry.AsUInt32(span, bigEndian); break;
                case 0x0132: dateTime = ParseExifDate(entry.AsAscii(span)); break;
                case 0x8769: exifIfd = (int)entry.AsUInt32(span, bigEndian); break;
            }
        }

        if (exifIfd > 0 && exifIfd < span.Length)
        {
            foreach (var entry in ReadIfd(span, exifIfd, bigEndian))
            {
                switch (entry.Tag)
                {
                    case 0x9003: dateTimeOriginal = ParseExifDate(entry.AsAscii(span)); break;
                    case 0x9004: dateTimeDigitized = ParseExifDate(entry.AsAscii(span)); break;
                    case 0x9291: subSec = entry.AsAscii(span); break;
                    case 0xA002: pixelWidth = (int)entry.AsUInt32(span, bigEndian); break;
                    case 0xA003: pixelHeight = (int)entry.AsUInt32(span, bigEndian); break;
                }
            }
        }

        // DateTimeOriginal is the capture instant; DateTimeDigitized and DateTime are accepted only
        // as stand-ins when a camera or an editor wrote one of those instead (still "exif" provenance).
        return new ExifData(
            dateTimeOriginal ?? dateTimeDigitized ?? dateTime,
            string.IsNullOrWhiteSpace(subSec) ? null : subSec.Trim(),
            make, model, orientation, imageWidth, imageHeight, pixelWidth, pixelHeight);
    }

    private readonly record struct IfdEntry(ushort Tag, ushort Type, uint Count, int ValueOffset)
    {
        private static int SizeOf(ushort type) => type switch
        {
            1 or 2 or 6 or 7 => 1,
            3 or 8 => 2,
            4 or 9 or 11 => 4,
            5 or 10 or 12 => 8,
            _ => 0,
        };

        public uint AsUInt32(ReadOnlySpan<byte> span, bool bigEndian) => Type switch
        {
            3 when ValueOffset + 2 <= span.Length => ReadUInt16(span, ValueOffset, bigEndian),
            4 or 9 when ValueOffset + 4 <= span.Length => ReadUInt32(span, ValueOffset, bigEndian),
            1 when ValueOffset < span.Length => span[ValueOffset],
            _ => 0,
        };

        public string? AsAscii(ReadOnlySpan<byte> span)
        {
            if (Type != 2 || Count == 0) return null;
            var size = (int)Math.Min(Count, 512);
            if (ValueOffset < 0 || ValueOffset + size > span.Length) return null;
            var raw = span.Slice(ValueOffset, size);
            var nul = raw.IndexOf((byte)0);
            if (nul >= 0) raw = raw[..nul];
            var text = Encoding.Latin1.GetString(raw).Trim();
            return text.Length == 0 ? null : text;
        }

        public static int ByteCount(ushort type, uint count) => SizeOf(type) * (int)Math.Min(count, int.MaxValue / 8);
    }

    private static List<IfdEntry> ReadIfd(ReadOnlySpan<byte> span, int offset, bool bigEndian)
    {
        var entries = new List<IfdEntry>();
        if (offset + 2 > span.Length) return entries;

        var count = ReadUInt16(span, offset, bigEndian);
        var cursor = offset + 2;
        for (var i = 0; i < count && cursor + 12 <= span.Length; i++, cursor += 12)
        {
            var tag = ReadUInt16(span, cursor, bigEndian);
            var type = ReadUInt16(span, cursor + 2, bigEndian);
            var valueCount = ReadUInt32(span, cursor + 4, bigEndian);
            var inlineOffset = cursor + 8;
            var byteCount = IfdEntry.ByteCount(type, valueCount);
            var valueOffset = byteCount is > 0 and <= 4
                ? inlineOffset
                : (int)ReadUInt32(span, inlineOffset, bigEndian);
            entries.Add(new IfdEntry(tag, type, valueCount, valueOffset));
        }

        return entries;
    }

    /// <summary>Parses the EXIF "YYYY:MM:DD HH:MM:SS" form; EXIF carries no time zone, so the result is unzoned.</summary>
    private static DateTime? ParseExifDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim().Replace('\0', ' ').Trim();
        string[] formats =
        [
            "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss",
            "yyyy-MM-dd HH:mm", "yyyy:MM:dd",
        ];
        if (DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.NoCurrentDateDefault, out var parsed) &&
            parsed.Year > 1900)
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
        }

        return null;
    }

    // ---------------------------------------------------------------- primitives

    private static bool StartsWith(ReadOnlySpan<byte> span, ReadOnlySpan<byte> prefix) =>
        span.Length >= prefix.Length && span[..prefix.Length].SequenceEqual(prefix);

    private static string Ascii(ReadOnlySpan<byte> span, int offset, int length) =>
        offset < 0 || offset + length > span.Length ? string.Empty : Encoding.ASCII.GetString(span.Slice(offset, length));

    private static ushort ReadUInt16(ReadOnlySpan<byte> span, int offset, bool bigEndian) =>
        offset + 2 > span.Length
            ? (ushort)0
            : bigEndian
                ? BinaryPrimitives.ReadUInt16BigEndian(span.Slice(offset, 2))
                : BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> span, int offset, bool bigEndian) =>
        offset + 4 > span.Length
            ? 0u
            : bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(span.Slice(offset, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> span) => span[0] | (span[1] << 8) | (span[2] << 16);
}
