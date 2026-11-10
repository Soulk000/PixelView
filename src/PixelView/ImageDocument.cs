using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageMagick;
using ImageMagick.Configuration;

namespace PixelView;

internal sealed class ImageDocument : IDisposable
{
    private const long PixelLimit = 110_000_000;
    private const long AnimationCacheLimit = 96 * 1024 * 1024;
    private readonly MagickImageCollection? images;
    private readonly BitmapSource?[] cache;
    private long cachedBytes;
    private readonly int[]? frameDelays;
    private static readonly HashSet<string> Excluded = new(
        "A AI AVI APNG B BAYER BAYERA BGR BGRA BGRO C C2PA CANVAS CAPTION CLIP CLIPBOARD CMYK CMYKA CUBE DATA DFONT EPDF EPI EPS EPS2 EPS3 EPSF EPSI EPT EPT2 EPT3 FFMPEG FILE FLV FRACTAL FTP FTXT G GRADIENT GRANITE GRAY GRAYA HALD HISTOGRAM HTTP HTTPS INLINE K LABEL M M2V M4V MAGICK MASK MATTE META MKV MOV MP4 MPC MPEG MPG MSL MSVG MVG NULL O OTF PAL PCL PDF PDFA PFA PFB PFM PANGO PATTERN PLASMA POCKETMOD PREVIEW PRINT PS PS2 PS3 R RADIALGRADIENT RAW RGB RGB565 RGBA RGBO RSVG SCREENSHOT SF3 STEGANO STRIMG TEXT THREEFR THREEG2 THREEGP TILE TTC TTF TXT UIL UNIQUE URL UYVY VID VIDEO WBINFO WEBM WIN WMV XC XPS Y YCBCR YCBCRA YUV"
            .Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CameraRaw = new(
        ".3fr .arw .cr2 .cr3 .crw .dcr .dng .erf .fff .iiq .k25 .kdc .mdc .mef .mos .mrw .nef .nrw .orf .pef .raf .rw2 .rwl .sr2 .srf .srw .x3f"
            .Split(' '), StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, MagickFormat> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jfif"] = MagickFormat.Jpeg, [".jpe"] = MagickFormat.Jpeg,
        [".cur"] = MagickFormat.Ico, [".dib"] = MagickFormat.Bmp,
        [".avifs"] = MagickFormat.Avif, [".heifs"] = MagickFormat.Heic,
        [".j2c"] = MagickFormat.J2k, [".tif"] = MagickFormat.Tiff,
        [".svgz"] = MagickFormat.Svg, [".apng"] = MagickFormat.Png,
        [".aseprite"] = MagickFormat.Ase, [".3fr"] = MagickFormat.ThreeFr,
        [".kra"] = MagickFormat.Ora
    };
    private static readonly Lazy<Dictionary<string, MagickFormat>> Formats = new(BuildFormats);
    public static IEnumerable<string> Extensions => Formats.Value.Keys;
    public static bool IsImagePath(string path) => Formats.Value.ContainsKey(Path.GetExtension(path));

    public string Format { get; }
    public int Count => cache.Length;
    public bool Animated => frameDelays != null;
    public BitmapSource First => cache[0]!;

    private ImageDocument(MagickImageCollection collection, string format, bool animated)
    {
        images = collection;
        Format = format;
        cache = new BitmapSource?[collection.Count];
        if (animated)
            frameDelays = collection.Select(image =>
                Math.Clamp((int)Math.Round(1000d * image.AnimationDelay / Math.Max(1, image.AnimationTicksPerSecond)), 20, 60_000))
                .ToArray();
        cache[0] = ConvertFrame(collection[0]);
        cachedBytes = SizeInBytes(cache[0]!);
    }

    private ImageDocument(BitmapSource[] frames, string format, int[]? delays)
    {
        cache = frames;
        Format = format;
        frameDelays = delays;
    }

    public static ImageDocument FromBitmap(BitmapSource source)
    {
        CheckDimensions(source.PixelWidth, source.PixelHeight);
        if (!source.IsFrozen)
        {
            source = source.Clone();
            source.Freeze();
        }
        return new ImageDocument([source], "剪贴板", null);
    }

    public static ImageDocument FromAnimation(BitmapSource[] frames, int[] delays) => new(frames, "APNG", delays);

    public static ImageDocument Open(string path)
    {
        _ = Formats.Value;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string extension = Path.GetExtension(path);
        if (extension.Equals(".kra", StringComparison.OrdinalIgnoreCase))
            return ReadKritaPreview(stream);
        var settings = new MagickReadSettings { BackgroundColor = MagickColors.Transparent };
        settings.SetDefine(MagickFormat.Svg, "parse-huge", "false");
        settings.SetDefine(MagickFormat.Dng, "use-camera-wb", "true");
        var collection = new MagickImageCollection();
        try
        {
            if (Apng.TryRead(stream) is { } animation)
            {
                collection.Dispose();
                return animation;
            }
            stream.Position = 0;
            if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                settings.Format = MagickFormat.Msvg;
                collection.Read(stream, settings);
            }
            else if (extension.Equals(".svgz", StringComparison.OrdinalIgnoreCase))
            {
                settings.Format = MagickFormat.Msvg;
                using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
                collection.Read(gzip, settings);
            }
            else if (CameraRaw.Contains(extension) && Formats.Value.TryGetValue(extension, out var rawFormat))
            {
                settings.Format = rawFormat;
                collection.Read(stream, settings);
            }
            else
            {
                try { collection.Read(stream, settings); }
                catch (MagickMissingDelegateErrorException) when (Formats.Value.TryGetValue(extension, out var format))
                {
                    collection.Clear();
                    stream.Position = 0;
                    settings.Format = format;
                    collection.Read(stream, settings);
                }
            }

            if (collection.Count == 0) throw new InvalidDataException("文件里没有可显示的图像。");
            string name = collection[0].Format.ToString().ToUpperInvariant();
            if (name == "MSVG") name = "SVG";
            if (CameraRaw.Contains(extension)) name = extension.TrimStart('.').ToUpperInvariant();
            bool animated = collection.Count > 1 && (name is "GIF" or "WEBP" or "MNG" ||
                name is ("JXL" or "AVIF" or "HEIC" or "ASE") && collection.Any(image => image.AnimationDelay > 0));
            if (name is "PSD" or "PSB" or "XCF" or "ORA")
            {
                // Layered formats normally expose the composited preview as their first image.
                while (collection.Count > 1)
                {
                    var layer = collection[^1];
                    collection.RemoveAt(collection.Count - 1);
                    layer.Dispose();
                }
            }
            if (animated && name is ("GIF" or "WEBP" or "MNG"))
                collection.Coalesce();
            return new ImageDocument(collection, name, animated);
        }
        catch (MagickException magickError)
        {
            collection.Dispose();
            if (magickError is MagickPolicyErrorException) throw;
            try
            {
                stream.Position = 0;
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frames = decoder.Frames.Select(frame =>
                {
                    CheckDimensions(frame.PixelWidth, frame.PixelHeight);
                    frame.Freeze();
                    return (BitmapSource)frame;
                }).ToArray();
                if (frames.Length == 0) throw new InvalidDataException();
                return new ImageDocument(frames, Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), null);
            }
            catch (Exception error) when (error is NotSupportedException or FormatException or IOException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                throw new InvalidDataException("解码失败。文件可能已损坏，或当前版本缺少对应的解码器。", magickError);
            }
        }
        catch
        {
            collection.Dispose();
            throw;
        }
    }

    public BitmapSource GetFrame(int index)
    {
        if (cache[index] is { } existing) return existing;
        var next = ConvertFrame(images![index]);
        long bytes = SizeInBytes(next);
        if (cachedBytes + bytes <= AnimationCacheLimit)
        {
            cache[index] = next;
            cachedBytes += bytes;
        }
        return next;
    }

    public int Delay(int index) => frameDelays?[index] ?? 100;
    public void Dispose() => images?.Dispose();

    private static ImageDocument ReadKritaPreview(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var preview = archive.GetEntry("mergedimage.png");
        if (preview == null || preview.Length > 160 * 1024 * 1024)
            throw new InvalidDataException("Krita 文件缺少可读取的合并预览。");
        using var contents = preview.Open();
        using var image = new MagickImage(contents, MagickFormat.Png);
        return new ImageDocument([ConvertFrame(image)], "KRA", null);
    }

    public static void CheckDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > PixelLimit)
            throw new InvalidDataException("单帧尺寸超出可显示范围（约 1.1 亿像素）。");
    }

    private static long SizeInBytes(BitmapSource source) => 4L * source.PixelWidth * source.PixelHeight;

    private static BitmapSource ConvertFrame(IMagickImage<byte> image)
    {
        int width = checked((int)image.Width);
        int height = checked((int)image.Height);
        CheckDimensions(width, height);
        image.AutoOrient();
        if (image.GetColorProfile() != null)
        {
            try { image.TransformColorSpace(ColorProfiles.SRGB); }
            catch (MagickException) { image.ColorSpace = ColorSpace.sRGB; }
        }
        else
        {
            image.ColorSpace = ColorSpace.sRGB;
        }
        using var pixels = image.GetPixelsUnsafe();
        byte[] data = pixels.ToByteArray(PixelMapping.BGRA) ?? throw new InvalidDataException("无法读取像素。");
        width = (int)image.Width;
        height = (int)image.Height;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, data, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Dictionary<string, MagickFormat> BuildFormats()
    {
        var config = ConfigurationFiles.Default;
        config.Policy.Data = """
            <policymap>
              <policy domain="delegate" rights="none" pattern="*" />
              <policy domain="coder" rights="none" pattern="{URL,HTTPS,HTTP,FTP,MSL,PDF,PDFA,PS,PS2,PS3,EPS,EPSF,EPSI,EPI,AI,XPS,VIDEO,FFMPEG,WIN,SCREENSHOT}" />
              <policy domain="path" rights="none" pattern="@*" />
              <policy domain="resource" name="width" value="40000" />
              <policy domain="resource" name="height" value="40000" />
              <policy domain="resource" name="list-length" value="2500" />
              <policy domain="resource" name="memory" value="384MiB" />
              <policy domain="resource" name="map" value="768MiB" />
              <policy domain="resource" name="disk" value="2GiB" />
              <policy domain="resource" name="thread" value="4" />
              <policy domain="resource" name="time" value="45" />
            </policymap>
            """;
        MagickNET.Initialize(config);
        var formats = new Dictionary<string, MagickFormat>(Aliases, StringComparer.OrdinalIgnoreCase);
        foreach (var info in MagickNET.SupportedFormats)
        {
            string name = info.Format.ToString();
            if (info.SupportsReading && !Excluded.Contains(name))
                formats.TryAdd("." + name.ToLowerInvariant(), info.Format);
        }
        return formats;
    }
}
