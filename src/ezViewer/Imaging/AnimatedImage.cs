using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EzViewer.Imaging;

/// <summary>
/// 动图（GIF、WebP、APNG）。解码页面时只读出画布尺寸、每帧时长和合成好的第一帧，
/// 显示时再由阅读界面在后台线程上用 CreateDecoder 逐帧解码播放。
/// </summary>
public sealed class AnimatedImage
{
    // 画布太大时只当静态图显示第一帧
    private const long MaxPixels = 4096L * 4096;

    private readonly byte[] _data;
    private readonly Func<byte[], AnimationDecoder> _factory;

    private AnimatedImage(byte[] data, Func<byte[], AnimationDecoder> factory, AnimationDecoder decoder, BitmapSource firstFrame)
    {
        _data = data;
        _factory = factory;
        Width = decoder.Width;
        Height = decoder.Height;
        Delays = decoder.Delays;
        LoopCount = decoder.LoopCount;
        FirstFrame = firstFrame;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>每帧显示的毫秒数。</summary>
    public IReadOnlyList<int> Delays { get; }

    public int FrameCount => Delays.Count;

    /// <summary>播放遍数，0 表示一直循环。</summary>
    public int LoopCount { get; }

    /// <summary>合成好的第一帧（Pbgra32，已冻结）。</summary>
    public BitmapSource FirstFrame { get; }

    /// <summary>多帧的 GIF、WebP、APNG 返回动图；其他图片以及无法按动图解析的返回 null，按静态图处理。</summary>
    public static AnimatedImage? TryCreate(byte[] data)
    {
        Func<byte[], AnimationDecoder>? factory =
            GifDecoder.IsGif(data) ? d => new GifDecoder(d)
            : WebpDecoder.IsAnimated(data) ? d => new WebpDecoder(d)
            : ApngDecoder.IsAnimated(data) ? d => new ApngDecoder(d)
            : null;
        if (factory == null) return null;

        try
        {
            var decoder = factory(data);
            int w = decoder.Width, h = decoder.Height;
            if (decoder.FrameCount < 2 || w <= 0 || h <= 0 || (long)w * h > MaxPixels || Math.Max(w, h) > ImageDecoder.MaxLongSide) return null;
            var pixels = new byte[w * h * 4];
            decoder.Render(0, pixels);
            var first = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
            first.Freeze();
            return new AnimatedImage(data, factory, decoder, first);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>新建逐帧解码器。解码器只能在创建它的线程上使用。</summary>
    public AnimationDecoder CreateDecoder() => _factory(_data);
}

/// <summary>按顺序解码动图的每一帧，输出合成后的整幅画面（Pbgra32，每行 Width × 4 字节）。</summary>
public abstract class AnimationDecoder
{
    public int Width { get; protected set; }

    public int Height { get; protected set; }

    public int[] Delays { get; protected set; } = Array.Empty<int>();

    /// <summary>播放遍数，0 表示一直循环。</summary>
    public int LoopCount { get; protected set; }

    public int FrameCount => Delays.Length;

    /// <summary>把第 index 帧画进 buffer。按顺序取最快；往回取时从第一帧重新合成。</summary>
    public abstract void Render(int index, byte[] buffer);

    /// <summary>与浏览器一致：不超过 10 毫秒的帧按 100 毫秒显示。</summary>
    protected static int NormalizeDelay(int ms) => ms <= 10 ? 100 : ms;

    protected static BitmapDecoder OpenWic(byte[] data) =>
        BitmapDecoder.Create(new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true),
            BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
}

/// <summary>GIF、APNG 的每帧只是画布上的一块区域，要按处置方式和混合方式自己合成。</summary>
public abstract class CompositingDecoder : AnimationDecoder
{
    protected enum DisposeOp
    {
        None,
        Background,
        Previous,
    }

    protected readonly record struct FrameInfo(int X, int Y, int Width, int Height, DisposeOp Dispose, bool BlendOver);

    private byte[]? _canvas;
    private byte[]? _saved;
    private byte[] _pixels = Array.Empty<byte>();
    private int _last = -1;

    protected FrameInfo[] Frames { get; set; } = Array.Empty<FrameInfo>();

    /// <summary>第 index 帧本身的图像。</summary>
    protected abstract BitmapSource DecodeFrame(int index);

    public override void Render(int index, byte[] buffer)
    {
        _canvas ??= new byte[Width * Height * 4];
        if (index <= _last) _last = -1;
        while (_last < index) Step(++_last);
        _canvas.CopyTo(buffer, 0);
    }

    private void Step(int index)
    {
        var canvas = _canvas!;
        if (index == 0)
        {
            Array.Clear(canvas);
        }
        else
        {
            var prev = Frames[index - 1];
            if (prev.Dispose == DisposeOp.Background) Clear(canvas, prev);
            else if (prev.Dispose == DisposeOp.Previous && _saved != null) _saved.CopyTo(canvas, 0);
        }

        var frame = Frames[index];
        if (frame.Dispose == DisposeOp.Previous)
        {
            _saved ??= new byte[canvas.Length];
            canvas.CopyTo(_saved, 0);
        }
        Draw(canvas, frame, DecodeFrame(index));
    }

    private void Clear(byte[] canvasBytes, FrameInfo f)
    {
        var canvas = MemoryMarshal.Cast<byte, uint>(canvasBytes.AsSpan());
        int x0 = Math.Max(f.X, 0), x1 = Math.Min(f.X + f.Width, Width);
        if (x1 <= x0) return;
        for (int y = Math.Max(f.Y, 0); y < Math.Min(f.Y + f.Height, Height); y++)
            canvas.Slice(y * Width + x0, x1 - x0).Clear();
    }

    private void Draw(byte[] canvasBytes, FrameInfo f, BitmapSource frame)
    {
        int fw = frame.PixelWidth, fh = frame.PixelHeight;
        int stride = fw * 4;
        if (_pixels.Length < stride * fh) _pixels = new byte[stride * fh];
        BitmapSource src = frame.Format == PixelFormats.Pbgra32 ? frame : new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        src.CopyPixels(_pixels, stride, 0);

        var canvas = MemoryMarshal.Cast<byte, uint>(canvasBytes.AsSpan());
        var pixels = MemoryMarshal.Cast<byte, uint>(_pixels.AsSpan(0, stride * fh));
        int x0 = Math.Max(f.X, 0), x1 = Math.Min(f.X + fw, Width);
        int y0 = Math.Max(f.Y, 0), y1 = Math.Min(f.Y + fh, Height);
        for (int y = y0; y < y1; y++)
        {
            int s = (y - f.Y) * fw - f.X;
            int d = y * Width;
            for (int x = x0; x < x1; x++)
            {
                uint p = pixels[s + x];
                uint a = p >> 24;
                if (!f.BlendOver || a == 255) canvas[d + x] = p;
                else if (a != 0) canvas[d + x] = p + Scale(canvas[d + x], 255 - a);
            }
        }
    }

    /// <summary>预乘颜色的四个通道各乘以 k / 255。</summary>
    private static uint Scale(uint p, uint k)
    {
        uint rb = (p & 0x00FF00FF) * k;
        uint ag = ((p >> 8) & 0x00FF00FF) * k;
        rb = ((rb + 0x00800080 + ((rb >> 8) & 0x00FF00FF)) >> 8) & 0x00FF00FF;
        ag = ((ag + 0x00800080 + ((ag >> 8) & 0x00FF00FF)) >> 8) & 0x00FF00FF;
        return rb | (ag << 8);
    }
}

internal sealed class GifDecoder : CompositingDecoder
{
    private readonly BitmapDecoder _decoder;

    public static bool IsGif(byte[] d)
    {
        if (d.Length < 14) return false;
        var header = d.AsSpan(0, 6);
        return header.SequenceEqual("GIF89a"u8) || header.SequenceEqual("GIF87a"u8);
    }

    public GifDecoder(byte[] data)
    {
        _decoder = OpenWic(data);
        var frames = _decoder.Frames;
        BitmapMetadata? global = null;
        try
        {
            global = _decoder.Metadata;
        }
        catch (NotSupportedException)
        {
        }

        int width = Query(global, "/logscrdesc/Width");
        int height = Query(global, "/logscrdesc/Height");

        // 没有 NETSCAPE2.0 扩展的只播一遍；有扩展时其中记的是“重复”次数，0 表示一直循环
        LoopCount = 1;
        if (QueryBytes(global, "/appext/Application") is { } app && Encoding.ASCII.GetString(app) is "NETSCAPE2.0" or "ANIMEXTS1.0"
            && QueryBytes(global, "/appext/Data") is { Length: >= 4 } ext && ext[1] == 1)
        {
            int repeat = ext[2] | ext[3] << 8;
            LoopCount = repeat == 0 ? 0 : repeat + 1;
        }

        var infos = new FrameInfo[frames.Count];
        var delays = new int[frames.Count];
        for (int i = 0; i < infos.Length; i++)
        {
            var meta = frames[i].Metadata as BitmapMetadata;
            int w = Query(meta, "/imgdesc/Width"), h = Query(meta, "/imgdesc/Height");
            if (w == 0 || h == 0) (w, h) = (frames[i].PixelWidth, frames[i].PixelHeight);
            var dispose = Query(meta, "/grctlext/Disposal") switch
            {
                2 => DisposeOp.Background,
                3 => DisposeOp.Previous,
                _ => DisposeOp.None,
            };
            infos[i] = new FrameInfo(Query(meta, "/imgdesc/Left"), Query(meta, "/imgdesc/Top"), w, h, dispose, BlendOver: true);
            delays[i] = NormalizeDelay(Query(meta, "/grctlext/Delay") * 10);
        }

        if (width <= 0 || height <= 0)
        {
            foreach (var f in infos)
            {
                width = Math.Max(width, f.X + f.Width);
                height = Math.Max(height, f.Y + f.Height);
            }
        }
        Width = width;
        Height = height;
        Frames = infos;
        Delays = delays;
    }

    protected override BitmapSource DecodeFrame(int index) => _decoder.Frames[index];

    private static int Query(BitmapMetadata? meta, string query)
    {
        try
        {
            return meta?.GetQuery(query) switch
            {
                ushort v => v,
                byte v => v,
                uint v => (int)Math.Min(v, int.MaxValue),
                _ => 0,
            };
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static byte[]? QueryBytes(BitmapMetadata? meta, string query)
    {
        try
        {
            return meta?.GetQuery(query) as byte[];
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Windows 自带的 WebP 解码器给出的已经是合成好的整幅画面；每帧时长和循环次数从 RIFF 块里读。</summary>
internal sealed class WebpDecoder : AnimationDecoder
{
    private readonly BitmapDecoder _decoder;

    public static bool IsAnimated(byte[] d) =>
        d.Length > 30 && d.AsSpan(0, 4).SequenceEqual("RIFF"u8) && d.AsSpan(8, 4).SequenceEqual("WEBP"u8)
        && d.AsSpan(12, 4).SequenceEqual("VP8X"u8) && (d[20] & 0x02) != 0;

    public WebpDecoder(byte[] data)
    {
        _decoder = OpenWic(data);
        var first = _decoder.Frames[0];
        Width = first.PixelWidth;
        Height = first.PixelHeight;

        var durations = new List<int>();
        int pos = 12;
        while (pos + 8 <= data.Length)
        {
            var type = data.AsSpan(pos, 4);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4));
            int body = pos + 8;
            if (type.SequenceEqual("ANIM"u8) && body + 6 <= data.Length)
                LoopCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 4));
            else if (type.SequenceEqual("ANMF"u8) && body + 16 <= data.Length)
                durations.Add(data[body + 12] | data[body + 13] << 8 | data[body + 14] << 16);
            long next = body + size + (size & 1);
            if (next > data.Length) break;
            pos = (int)next;
        }

        Delays = new int[_decoder.Frames.Count];
        for (int i = 0; i < Delays.Length; i++)
            Delays[i] = NormalizeDelay(i < durations.Count ? durations[i] : 0);
    }

    public override void Render(int index, byte[] buffer)
    {
        BitmapSource frame = _decoder.Frames[index];
        if (frame.PixelWidth != Width || frame.PixelHeight != Height) throw new InvalidDataException("WebP 动图各帧尺寸不一致");
        if (frame.Format != PixelFormats.Pbgra32) frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
        frame.CopyPixels(buffer, Width * 4, 0);
    }
}

/// <summary>
/// Windows 的 PNG 解码器只认 APNG 的默认图像。把每帧的 fcTL 区域和 fdAT 数据拼成一张独立的 PNG
/// （沿用原图的 IHDR、PLTE、tRNS 等），交给系统解码后再自己合成。
/// </summary>
internal sealed class ApngDecoder : CompositingDecoder
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly byte[] _data;
    private readonly byte[] _ihdr;
    private readonly List<(int Offset, int Length)> _ancillary = new();
    private readonly List<List<(int Offset, int Length)>> _frameData = new();

    public static bool IsAnimated(byte[] d)
    {
        if (d.Length < 33 || !d.AsSpan(0, 8).SequenceEqual(Signature)) return false;
        foreach (var (type, _, _) in Chunks(d))
        {
            if (type == "acTL") return true;
            if (type is "IDAT" or "IEND") return false;
        }
        return false;
    }

    public ApngDecoder(byte[] data)
    {
        _data = data;
        _ihdr = Array.Empty<byte>();
        var infos = new List<FrameInfo>();
        var delays = new List<int>();
        bool seenImageData = false;
        List<(int Offset, int Length)>? current = null;

        foreach (var (type, offset, length) in Chunks(data))
        {
            var body = data.AsSpan(offset, length);
            switch (type)
            {
                case "IHDR":
                    _ihdr = body.ToArray();
                    Width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                    Height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    break;
                case "acTL":
                    LoopCount = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(body[4..]), int.MaxValue);
                    break;
                case "fcTL" when length >= 26:
                {
                    int w = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    int h = (int)BinaryPrimitives.ReadUInt32BigEndian(body[8..]);
                    int x = (int)BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
                    int y = (int)BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
                    int num = BinaryPrimitives.ReadUInt16BigEndian(body[20..]);
                    int den = BinaryPrimitives.ReadUInt16BigEndian(body[22..]);
                    var dispose = body[24] switch
                    {
                        1 => DisposeOp.Background,
                        2 => infos.Count == 0 ? DisposeOp.Background : DisposeOp.Previous,
                        _ => DisposeOp.None,
                    };
                    infos.Add(new FrameInfo(x, y, w, h, dispose, BlendOver: body[25] == 1));
                    delays.Add(NormalizeDelay(num * 1000 / (den == 0 ? 100 : den)));
                    current = new List<(int, int)>();
                    _frameData.Add(current);
                    break;
                }
                case "IDAT":
                    // fcTL 出现在 IDAT 之前时默认图像就是第一帧，否则默认图像不参与动画
                    seenImageData = true;
                    current?.Add((offset, length));
                    break;
                case "fdAT" when length > 4:
                    seenImageData = true;
                    current?.Add((offset + 4, length - 4));
                    break;
                default:
                    if (!seenImageData && type is not ("acTL" or "fcTL" or "fdAT" or "IEND")) _ancillary.Add((offset - 8, length + 12));
                    break;
            }
        }

        // 去掉没有图像数据的帧（文件被截断等）
        for (int i = infos.Count - 1; i >= 0; i--)
        {
            if (_frameData[i].Count > 0) continue;
            infos.RemoveAt(i);
            delays.RemoveAt(i);
            _frameData.RemoveAt(i);
        }
        Frames = infos.ToArray();
        Delays = delays.ToArray();
    }

    protected override BitmapSource DecodeFrame(int index)
    {
        var f = Frames[index];
        using var png = new MemoryStream();
        png.Write(Signature);
        var ihdr = (byte[])_ihdr.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)f.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)f.Height);
        WriteChunk(png, "IHDR"u8, ihdr);
        foreach (var (offset, length) in _ancillary) png.Write(_data, offset, length);
        foreach (var (offset, length) in _frameData[index]) WriteChunk(png, "IDAT"u8, _data.AsSpan(offset, length));
        WriteChunk(png, "IEND"u8, ReadOnlySpan<byte>.Empty);
        png.Position = 0;
        return BitmapDecoder.Create(png, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
    }

    private static IEnumerable<(string Type, int Offset, int Length)> Chunks(byte[] data)
    {
        int pos = 8;
        while (pos + 12 <= data.Length)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (pos + 12 + length > data.Length) yield break;
            var type = Encoding.ASCII.GetString(data, pos + 4, 4);
            yield return (type, pos + 8, (int)length);
            if (type == "IEND") yield break;
            pos += 12 + (int)length;
        }
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)body.Length);
        s.Write(word);
        s.Write(type);
        s.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in body) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFF);
        s.Write(word);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
