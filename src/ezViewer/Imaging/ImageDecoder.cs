using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EzViewer.Imaging;

/// <summary>解码好的一页。动图的 Image 是合成好的第一帧，Animation 用于播放。</summary>
public sealed record DecodedPage(BitmapSource Image, AnimatedImage? Animation = null);

/// <summary>在后台线程把图片字节解码成已冻结、可跨线程使用的 BitmapSource。</summary>
public static class ImageDecoder
{
    // D3D9 纹理上限 16384；再大的长图按比例缩小，同时限制总像素避免内存暴涨
    public const int MaxLongSide = 16384;
    public const double MaxPixels = 60_000_000;

    public static DecodedPage DecodePage(byte[] data)
    {
        var animation = AnimatedImage.TryCreate(data);
        return animation != null ? new DecodedPage(animation.FirstFrame, animation) : new DecodedPage(Decode(data));
    }

    public static BitmapSource Decode(byte[] data)
    {
        using var ms = new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true);
        var (w, h) = Probe(ms);
        double scale = 1;
        if (Math.Max(w, h) > MaxLongSide) scale = (double)MaxLongSide / Math.Max(w, h);
        if ((double)w * h * scale * scale > MaxPixels) scale = Math.Sqrt(MaxPixels / ((double)w * h));
        ms.Position = 0;
        return ToRenderFormat(DecodeScaled(ms, w, h, scale));
    }

    /// <summary>按“填满 box”的比例解码缩略图（显示时 UniformToFill 裁切）。</summary>
    public static BitmapSource DecodeThumbnail(byte[] data, int boxWidth, int boxHeight)
    {
        using var ms = new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true);
        var (w, h) = Probe(ms);
        double scale = Math.Max((double)boxWidth / w, (double)boxHeight / h);
        if (h * scale > boxHeight * 3.0) scale = boxHeight * 3.0 / h;
        if (w * scale > boxWidth * 3.0) scale = boxWidth * 3.0 / w;
        scale = Math.Min(scale, 1);
        ms.Position = 0;
        var bmp = DecodeScaled(ms, w, h, scale);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>解码磁盘缓存里的缩略图，按显示所需高度缩小（不放大）。</summary>
    public static BitmapSource DecodeCached(byte[] data, int maxHeight)
    {
        using var ms = new MemoryStream(data, 0, data.Length, writable: false, publiclyVisible: true);
        var (_, h) = Probe(ms);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.StreamSource = ms;
        if (maxHeight > 0 && h > maxHeight) bi.DecodePixelHeight = maxHeight;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public static byte[] EncodeJpeg(BitmapSource source, int quality = 85)
    {
        BitmapSource src = source.Format == PixelFormats.Bgr24 || source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Gray8
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static (int Width, int Height) Probe(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("图片中没有可显示的帧");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) throw new InvalidDataException("图片尺寸无效");
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static BitmapSource DecodeScaled(Stream stream, int w, int h, double scale)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bi.StreamSource = stream;
        if (scale < 0.999)
        {
            if (w >= h) bi.DecodePixelWidth = Math.Max(1, (int)Math.Round(w * scale));
            else bi.DecodePixelHeight = Math.Max(1, (int)Math.Round(h * scale));
        }
        bi.EndInit();
        return bi;
    }

    /// <summary>提前转换成 WPF 渲染线程直接可用的像素格式，翻页时不在渲染线程里转换。</summary>
    private static BitmapSource ToRenderFormat(BitmapSource bmp)
    {
        var f = bmp.Format;
        if (f == PixelFormats.Bgr32 || f == PixelFormats.Pbgra32)
        {
            bmp.Freeze();
            return bmp;
        }

        bool hasAlpha = f == PixelFormats.Bgra32 || f == PixelFormats.Prgba64 || f == PixelFormats.Rgba64
            || f == PixelFormats.Prgba128Float || f == PixelFormats.Rgba128Float
            || f == PixelFormats.Indexed1 || f == PixelFormats.Indexed2 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed8;
        var converted = new FormatConvertedBitmap(bmp, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, 0);
        var cached = new CachedBitmap(converted, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }
}
