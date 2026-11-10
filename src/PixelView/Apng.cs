using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PixelView;

internal static class Apng
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();
    private const int CacheLimit = 160 * 1024 * 1024;

    public static ImageDocument? TryRead(Stream source)
    {
        Span<byte> signature = stackalloc byte[8];
        if (source.ReadAtLeast(signature, 8, false) != 8 || !signature.SequenceEqual(Signature))
            return null;

        byte[]? header = null;
        int width = 0, height = 0, expected = 0;
        var globals = new List<(uint Type, byte[] Data)>();
        var frames = new List<BitmapSource>();
        var delays = new List<int>();
        byte[]? canvas = null;
        Control? control = null;
        using var encoded = new MemoryStream();
        Span<byte> chunk = stackalloc byte[8];
        while (source.ReadAtLeast(chunk, 8, false) == 8)
        {
            uint length = ReadUInt(chunk);
            uint type = ReadUInt(chunk[4..]);
            if (length > source.Length - source.Position - 4)
                return null;
            if (type == 0x49444154 && expected == 0) return null; // IDAT of a still PNG
            if (type == 0x49454E44) break;

            bool keep = type is 0x49484452 or 0x6163544C or 0x6663544C or 0x66644154 or 0x49444154
                or 0x504C5445 or 0x74524E53 or 0x67414D41 or 0x73524742 or 0x6348524D or 0x69434350;
            if (!keep)
            {
                source.Seek(length + 4L, SeekOrigin.Current);
                continue;
            }
            if (length > CacheLimit) return null;
            byte[] data = new byte[length];
            source.ReadExactly(data);
            source.Seek(4, SeekOrigin.Current); // CRCs are regenerated when wrapping a frame as a PNG.
            switch (type)
            {
                case 0x49484452: // IHDR
                    if (data.Length != 13) return null;
                    header = data;
                    width = checked((int)ReadUInt(data));
                    height = checked((int)ReadUInt(data.AsSpan(4)));
                    break;
                case 0x6163544C: // acTL
                    if (data.Length != 8 || header == null) return null;
                    ImageDocument.CheckDimensions(width, height);
                    uint count = ReadUInt(data);
                    if (count == 0 || count > 2500 || 4L * width * height * count > CacheLimit)
                        return null;
                    expected = (int)count;
                    canvas = new byte[checked(width * height * 4)];
                    break;
                case 0x6663544C: // fcTL
                    if (expected == 0 || data.Length != 26) return null;
                    if (control is { } previous) Finish(previous);
                    if (frames.Count >= expected) return null;
                    control = Control.Read(data);
                    if (!control.Value.Fits(width, height)) return null;
                    encoded.SetLength(0);
                    break;
                case 0x49444154: // IDAT, which can also be a standalone preview
                    if (encoded.Length + data.Length > CacheLimit) return null;
                    if (control.HasValue) WriteChunk(encoded, 0x49444154, data);
                    break;
                case 0x66644154: // fdAT
                    if (!control.HasValue || data.Length < 4) return null;
                    if (encoded.Length + data.Length > CacheLimit) return null;
                    WriteChunk(encoded, 0x49444154, data.AsSpan(4));
                    break;
                default:
                    if (frames.Count == 0 && encoded.Length == 0)
                    {
                        if (globals.Count >= 16 || length > 4 * 1024 * 1024) return null;
                        globals.Add((type, data));
                    }
                    break;
            }
        }
        if (control is { } last) Finish(last);
        return frames.Count > 0 && frames.Count == expected
            ? ImageDocument.FromAnimation(frames.ToArray(), delays.ToArray()) : null;

        void Finish(Control frame)
        {
            if (encoded.Length == 0) throw new InvalidDataException("动画帧缺少图像数据。");
            using var pngData = new MemoryStream();
            pngData.Write(Signature);
            byte[] localHeader = (byte[])header!.Clone();
            BinaryPrimitives.WriteUInt32BigEndian(localHeader, (uint)frame.Width);
            BinaryPrimitives.WriteUInt32BigEndian(localHeader.AsSpan(4), (uint)frame.Height);
            WriteChunk(pngData, 0x49484452, localHeader);
            foreach (var global in globals) WriteChunk(pngData, global.Type, global.Data);
            encoded.Position = 0;
            encoded.CopyTo(pngData);
            WriteChunk(pngData, 0x49454E44, []);
            pngData.Position = 0;
            var png = new PngBitmapDecoder(pngData, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var pixels = new FormatConvertedBitmap(png.Frames[0], PixelFormats.Pbgra32, null, 0);
            int stride = frame.Width * 4;
            var patch = new byte[checked(stride * frame.Height)];
            pixels.CopyPixels(patch, stride, 0);
            byte[]? restore = frame.Dispose == 2 ? (byte[])canvas!.Clone() : null;
            for (int y = 0; y < frame.Height; y++)
            {
                int target = ((frame.Y + y) * width + frame.X) * 4;
                if (frame.Blend == 0)
                    patch.AsSpan(y * stride, stride).CopyTo(canvas.AsSpan(target, stride));
                else
                    BlendOver(patch.AsSpan(y * stride, stride), canvas.AsSpan(target, stride));
            }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, canvas!, width * 4);
            bitmap.Freeze();
            frames.Add(bitmap);
            delays.Add(frame.Delay);
            if (frame.Dispose == 1)
                for (int y = 0; y < frame.Height; y++)
                    canvas.AsSpan(((frame.Y + y) * width + frame.X) * 4, stride).Clear();
            if (restore != null) canvas = restore;
        }
    }

    private static void BlendOver(ReadOnlySpan<byte> source, Span<byte> target)
    {
        for (int i = 0; i < source.Length; i += 4)
        {
            int remainder = 255 - source[i + 3];
            for (int channel = i; channel < i + 4; channel++)
                target[channel] = (byte)(source[channel] + (target[channel] * remainder + 127) / 255);
        }
    }

    private static uint ReadUInt(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static void WriteChunk(Stream stream, uint type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], type);
        stream.Write(header);
        stream.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte b in header[4..]) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(header, ~crc);
        stream.Write(header[..4]);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint crc = index;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) == 0 ? crc >> 1 : 0xEDB88320 ^ (crc >> 1);
            table[index] = crc;
        }
        return table;
    }

    private readonly record struct Control(int Width, int Height, int X, int Y, int Delay, byte Dispose, byte Blend)
    {
        public static Control Read(ReadOnlySpan<byte> data)
        {
            int numerator = BinaryPrimitives.ReadUInt16BigEndian(data[20..]);
            int denominator = BinaryPrimitives.ReadUInt16BigEndian(data[22..]);
            int delay = Math.Clamp((int)Math.Round(1000d * numerator / (denominator == 0 ? 100 : denominator)), 20, 60_000);
            return new((int)ReadUInt(data[4..]), (int)ReadUInt(data[8..]), (int)ReadUInt(data[12..]),
                (int)ReadUInt(data[16..]), delay, data[24], data[25]);
        }

        public bool Fits(int width, int height) => Width > 0 && Height > 0 && X >= 0 && Y >= 0 &&
            (long)X + Width <= width && (long)Y + Height <= height && Dispose <= 2 && Blend <= 1;
    }
}
