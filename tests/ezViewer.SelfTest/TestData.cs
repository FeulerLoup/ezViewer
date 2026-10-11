using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EzViewer.SelfTest;

/// <summary>生成测试用的漫画库。</summary>
internal static class TestData
{
    public static void Generate(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        // 作者A：三个图片文件夹，检验自然排序（第2卷 在 第10卷 之前）
        MakeImageFolder(Path.Combine(root, "作者A", "第1卷"), "作者A 第1卷", 12, Color.FromRgb(0xF4, 0xD0, 0x3F));
        MakeImageFolder(Path.Combine(root, "作者A", "第2卷"), "作者A 第2卷", 10, Color.FromRgb(0x9A, 0xD0, 0xF5));
        MakeImageFolder(Path.Combine(root, "作者A", "第10卷"), "作者A 第10卷", 8, Color.FromRgb(0xB8, 0xE9, 0x86));

        // 作者B：zip（UTF-8 文件名）、zip（GBK 文件名）、cbz（内含子目录）、PDF
        var b = Path.Combine(root, "作者B");
        Directory.CreateDirectory(b);
        MakeZip(Path.Combine(b, "合集.zip"), "合集", 15, Color.FromRgb(0xF5, 0xA6, 0x9A), Encoding.UTF8, "页面_{0:D3}.jpg");
        MakeZip(Path.Combine(b, "旧压缩包GBK.zip"), "GBK", 6, Color.FromRgb(0xD9, 0xB8, 0xF5), Encoding.GetEncoding(936), "图片{0:D2}.jpg");
        MakeZip(Path.Combine(b, "漫画.cbz"), "cbz", 9, Color.FromRgb(0xF5, 0xE6, 0x9A), Encoding.UTF8, "内部目录/{0}.png", png: true);
        MakePdf(Path.Combine(b, "杂志.pdf"), "PDF", 5, Color.FromRgb(0xC8, 0xF0, 0xE0));

        // 作者C：这一层没有漫画，下一层“系列”里是固实 7z 和 tar
        var series = Path.Combine(root, "作者C", "系列");
        Directory.CreateDirectory(series);
        var tmp = Path.Combine(root, "_tmp7z");
        MakeImageFolder(tmp, "7z 卷1", 11, Color.FromRgb(0xF0, 0xC8, 0xC8));
        RunTar("--format", "7zip", "-cf", Path.Combine(series, "卷1.7z"), "-C", tmp, ".");
        Directory.Delete(tmp, true);
        MakeImageFolder(tmp, "tar 卷2", 7, Color.FromRgb(0xC8, 0xD8, 0xF0));
        RunTar("-cf", Path.Combine(series, "卷2.cbt"), "-C", tmp, ".");
        Directory.Delete(tmp, true);

        // 散图：自身含图片 + 子文件夹
        var loose = Path.Combine(root, "散图");
        MakeImageFolder(loose, "散图", 3, Color.FromRgb(0xE0, 0xE0, 0xE0), png: true);
        MakeImageFolder(Path.Combine(loose, "插画"), "插画", 4, Color.FromRgb(0xFF, 0xD8, 0xB0));

        // 宽图与长图
        var odd = Path.Combine(root, "特殊尺寸");
        Directory.CreateDirectory(odd);
        File.WriteAllBytes(Path.Combine(odd, "01 横向跨页.jpg"), MakePage("横向跨页", 1, 3, 2600, 1700, Color.FromRgb(0xB0, 0xE0, 0xFF)));
        File.WriteAllBytes(Path.Combine(odd, "02 长条.jpg"), MakePage("长条", 2, 3, 800, 6000, Color.FromRgb(0xFF, 0xF0, 0xB0)));
        File.WriteAllBytes(Path.Combine(odd, "03 小图.jpg"), MakePage("小图", 3, 3, 400, 560, Color.FromRgb(0xE8, 0xC8, 0xFF)));
        File.WriteAllBytes(Path.Combine(odd, "04 损坏.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 0x00, 0x01, 0x02 });

        Directory.CreateDirectory(Path.Combine(root, "空文件夹"));

        // 多语言：没有 UTF-8 标记的日文、韩文、繁体中文文件名，以及 macOS 打包的 UTF-8（分解形式）文件名
        var multi = Path.Combine(root, "多语言");
        Directory.CreateDirectory(multi);
        foreach (var (file, encoding, names) in MultilingualZips)
            MakeNamedZip(Path.Combine(multi, file), encoding, names);

        // 动图：GIF（局部帧、恢复背景、透明色）、APNG（半透明叠加、恢复到上一帧）
        var anim = Path.Combine(root, "动图");
        Directory.CreateDirectory(anim);
        File.WriteAllBytes(Path.Combine(anim, "01 合成.gif"), MakeTestGif());
        File.WriteAllBytes(Path.Combine(anim, "02 半透明.png"), MakeTestApng());

        // 长列表：测试列表滚动
        for (int i = 1; i <= 60; i++)
        {
            var d = Path.Combine(root, "Z长列表", "短篇" + i.ToString("D2"));
            Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(d, "001.jpg"), MakePage("短篇" + i, 1, 1, 300, 420, Color.FromRgb((byte)(120 + i * 2), 0xB0, (byte)(240 - i * 2))));
        }
    }

    /// <summary>文件名、编码（null 表示不带标记的 UTF-8）、包内文件名。</summary>
    public static (string File, Encoding? Encoding, string[] Names)[] MultilingualZips => new[]
    {
        ("日文SJIS.zip", Encoding.GetEncoding(932), new[] { "第01話 カラー/表紙.jpg", "第01話 カラー/本編01.jpg", "第01話 カラー/本編02.jpg", "第01話 カラー/本編10.jpg", "第01話 カラー/ソース.jpg" }),
        ("韩文CP949.zip", Encoding.GetEncoding(949), new[] { "원피스 1권/표지.jpg", "원피스 1권/본편01.jpg", "원피스 1권/본편02.jpg", "원피스 1권/본편10.jpg", "원피스 1권/후기.jpg" }),
        ("繁体Big5.zip", Encoding.GetEncoding(950), new[] { "海賊王 第1卷/封面.jpg", "海賊王 第1卷/內頁01.jpg", "海賊王 第1卷/內頁02.jpg", "海賊王 第1卷/內頁10.jpg", "海賊王 第1卷/後記.jpg" }),
        ("macOS UTF8.zip", null, new[] { "ガンダム ザク/表紙.jpg", "ガンダム ザク/本編01.jpg", "ガンダム ザク/本編02.jpg", "ガンダム ザク/本編10.jpg", "ガンダム ザク/ジオン.jpg" }),
    };

    /// <summary>下载一个 WebP 动图（12 帧 400x400）。系统没有 WebP 解码器时不放进测试库。</summary>
    public static void DownloadWebpSample(string path)
    {
        byte[] data;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
            data = http.GetByteArrayAsync("https://mathiasbynens.be/demo/animated-webp-supported.webp").GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  download failed animated webp: " + ex.Message);
            return;
        }
        try
        {
            BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        }
        catch (NotSupportedException)
        {
            Console.WriteLine("  系统没有安装 WebP 图像扩展，跳过 WebP 动图");
            return;
        }
        File.WriteAllBytes(path, data);
        Console.WriteLine("  downloaded animated webp");
    }

    public static void DownloadRarSamples(string dir)
    {
        Directory.CreateDirectory(dir);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        foreach (var name in new[] { "Rar.rar", "Rar5.rar", "Rar.solid.rar", "Rar5.solid.rar" })
        {
            var target = Path.Combine(dir, name);
            if (File.Exists(target)) continue;
            try
            {
                var url = "https://raw.githubusercontent.com/adamhathcock/sharpcompress/master/tests/TestArchives/Archives/" + name;
                File.WriteAllBytes(target, http.GetByteArrayAsync(url).GetAwaiter().GetResult());
                Console.WriteLine("  downloaded " + name);
            }
            catch (Exception ex)
            {
                Console.WriteLine("  download failed " + name + ": " + ex.Message);
            }
        }
    }

    private static void RunTar(params string[] args)
    {
        var psi = new ProcessStartInfo(@"C:\Windows\System32\tar.exe") { UseShellExecute = false, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new Exception("tar failed: " + err);
    }

    private static void MakeImageFolder(string dir, string title, int count, Color bg, bool png = false)
    {
        Directory.CreateDirectory(dir);
        for (int i = 1; i <= count; i++)
            File.WriteAllBytes(Path.Combine(dir, i.ToString("D3") + (png ? ".png" : ".jpg")), MakePage(title, i, count, 1200, 1700, Shade(bg, i), png));
    }

    private static void MakeZip(string path, string title, int count, Color bg, Encoding nameEncoding, string pattern, bool png = false)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, false, nameEncoding);
        // 故意打乱写入顺序，阅读时应按自然排序
        var order = new List<int>();
        for (int i = count; i >= 1; i--) order.Add(i);
        foreach (int i in order)
        {
            var entry = zip.CreateEntry(string.Format(pattern, i), CompressionLevel.Fastest);
            using var s = entry.Open();
            var data = MakePage(title, i, count, 1200, 1700, Shade(bg, i), png);
            s.Write(data, 0, data.Length);
        }
        var junk = zip.CreateEntry("__MACOSX/._001.jpg");
        using (var s = junk.Open()) s.Write(new byte[] { 0, 1, 2, 3 });
        var txt = zip.CreateEntry("说明.txt");
        using (var s = txt.Open()) s.Write(Encoding.UTF8.GetBytes("not an image"));
    }

    /// <summary>按给定文件名写 zip（倒序写入）。nameEncoding 为 null 时写不带 UTF-8 标记的 UTF-8 分解形式文件名，和 macOS 自带的压缩工具一样。</summary>
    private static void MakeNamedZip(string path, Encoding? nameEncoding, string[] names)
    {
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create, false, nameEncoding ?? new UnflaggedUtf8());
        for (int i = names.Length - 1; i >= 0; i--)
        {
            var name = nameEncoding == null ? names[i].Normalize(NormalizationForm.FormD) : names[i];
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            using var s = entry.Open();
            var data = MakePage(Path.GetFileNameWithoutExtension(path), i + 1, names.Length, 600, 850, Shade(Color.FromRgb(0xC8, 0xDC, 0xF0), i));
            s.Write(data, 0, data.Length);
        }
    }

    /// <summary>与 UTF-8 的字节相同，但 ZipArchive 不会因此设置 UTF-8 标记。</summary>
    private sealed class UnflaggedUtf8 : Encoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => UTF8.GetByteCount(chars, index, count);

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) =>
            UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);

        public override int GetCharCount(byte[] bytes, int index, int count) => UTF8.GetCharCount(bytes, index, count);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) =>
            UTF8.GetChars(bytes, byteIndex, byteCount, chars, charIndex);

        public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);

        public override int GetMaxCharCount(int byteCount) => UTF8.GetMaxCharCount(byteCount);
    }

    /// <summary>
    /// 300x420，3 帧：红色整幅；左上蓝块（之后恢复背景）；右下一块左半绿色、右半透明。
    /// 时长 50、100、0（应按 100）毫秒，一直循环。
    /// </summary>
    public static byte[] MakeTestGif() => WriteGif(300, 420, 0,
        new GifFrame(0, 0, 300, 420, new[] { Colors.Red }, (_, _) => 0, 5, 1),
        new GifFrame(20, 20, 100, 100, new[] { Colors.Blue }, (_, _) => 0, 10, 2),
        new GifFrame(150, 200, 100, 100, new[] { Colors.Lime, Colors.Magenta }, (x, _) => x < 50 ? 0 : 1, 0, 1, Transparent: 1));

    private readonly record struct GifFrame(int X, int Y, int Width, int Height, Color[] Palette, Func<int, int, int> Index, int DelayCs, int Disposal, int Transparent = -1);

    /// <summary>
    /// 最简单的 GIF 写入：每帧带 128 色局部色表，像素不压缩（码宽固定 8 位，每 120 个像素插一个清除码，字典长不到 256）。
    /// WPF 的 GifBitmapEncoder 会丢掉帧的位置、时长和处置方式，做不了合成测试。
    /// </summary>
    private static byte[] WriteGif(int width, int height, int repeat, params GifFrame[] frames)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("GIF89a"));
        w.Write((ushort)width);
        w.Write((ushort)height);
        w.Write(new byte[] { 0, 0, 0 });
        w.Write(new byte[] { 0x21, 0xFF, 0x0B });
        w.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        w.Write(new byte[] { 3, 1 });
        w.Write((ushort)repeat);
        w.Write((byte)0);
        foreach (var f in frames)
        {
            w.Write(new byte[] { 0x21, 0xF9, 4, (byte)(f.Disposal << 2 | (f.Transparent >= 0 ? 1 : 0)) });
            w.Write((ushort)f.DelayCs);
            w.Write((byte)Math.Max(f.Transparent, 0));
            w.Write((byte)0);
            w.Write((byte)0x2C);
            w.Write((ushort)f.X);
            w.Write((ushort)f.Y);
            w.Write((ushort)f.Width);
            w.Write((ushort)f.Height);
            w.Write((byte)(0x80 | 6));
            for (int i = 0; i < 128; i++)
            {
                var c = i < f.Palette.Length ? f.Palette[i] : Colors.Black;
                w.Write(new[] { c.R, c.G, c.B });
            }
            w.Write((byte)7);
            var codes = new List<byte> { 128 };
            int sinceClear = 0;
            for (int y = 0; y < f.Height; y++)
            {
                for (int x = 0; x < f.Width; x++)
                {
                    if (sinceClear == 120)
                    {
                        codes.Add(128);
                        sinceClear = 0;
                    }
                    codes.Add((byte)f.Index(x, y));
                    sinceClear++;
                }
            }
            codes.Add(129);
            for (int i = 0; i < codes.Count; i += 255)
            {
                int n = Math.Min(255, codes.Count - i);
                w.Write((byte)n);
                w.Write(codes.GetRange(i, n).ToArray());
            }
            w.Write((byte)0);
        }
        w.Write((byte)0x3B);
        return ms.ToArray();
    }

    /// <summary>
    /// 300x420，4 帧：白色整幅；半透明蓝块叠加（OVER）；同一位置半透明红块替换（SOURCE，之后恢复到上一帧）；左上角黑块。
    /// 时长 1/10 秒、50/1000 秒、0/0（应按 100 毫秒）、3/100 秒，播放 2 遍。
    /// </summary>
    public static byte[] MakeTestApng() => WriteApng(300, 420, 2,
        new ApngFrame(0, 0, SolidBitmap(300, 420, Color.FromArgb(255, 255, 255, 255)), 1, 10, 0, 0),
        new ApngFrame(50, 50, SolidBitmap(100, 100, Color.FromArgb(128, 0, 0, 255)), 50, 1000, 0, 1),
        new ApngFrame(50, 50, SolidBitmap(100, 100, Color.FromArgb(128, 255, 0, 0)), 0, 0, 2, 0),
        new ApngFrame(0, 0, SolidBitmap(10, 10, Color.FromArgb(255, 0, 0, 0)), 3, 100, 0, 0));

    private readonly record struct ApngFrame(int X, int Y, BitmapSource Image, int DelayNum, int DelayDen, byte Dispose, byte Blend);

    private static BitmapSource SolidBitmap(int w, int h, Color c)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = c.B;
            px[i + 1] = c.G;
            px[i + 2] = c.R;
            px[i + 3] = c.A;
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
    }

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>每帧先用 PngBitmapEncoder 编成独立的 PNG，取出 IDAT 数据，再按 APNG 的 fcTL / fdAT 拼起来。</summary>
    private static byte[] WriteApng(int width, int height, int plays, params ApngFrame[] frames)
    {
        byte[]? ihdr = null;
        int sequence = 0;
        var chunks = new List<(string Type, byte[] Body)>();
        for (int i = 0; i < frames.Length; i++)
        {
            var f = frames[i];
            var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
            encoder.Frames.Add(BitmapFrame.Create(f.Image));
            using var single = new MemoryStream();
            encoder.Save(single);
            var data = new MemoryStream();
            foreach (var (type, body) in ReadPngChunks(single.ToArray()))
            {
                if (type == "IDAT")
                {
                    data.Write(body);
                }
                else if (type == "IHDR")
                {
                    var header = (byte[])body.Clone();
                    BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
                    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
                    if (ihdr == null) ihdr = header;
                    else if (!header.AsSpan(8).SequenceEqual(ihdr.AsSpan(8))) throw new Exception("APNG 各帧的颜色类型不一致");
                }
            }

            var fctl = new byte[26];
            BinaryPrimitives.WriteUInt32BigEndian(fctl, (uint)sequence++);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(4), (uint)f.Image.PixelWidth);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(8), (uint)f.Image.PixelHeight);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(12), (uint)f.X);
            BinaryPrimitives.WriteUInt32BigEndian(fctl.AsSpan(16), (uint)f.Y);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), (ushort)f.DelayNum);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), (ushort)f.DelayDen);
            fctl[24] = f.Dispose;
            fctl[25] = f.Blend;
            chunks.Add(("fcTL", fctl));
            if (i == 0)
            {
                chunks.Add(("IDAT", data.ToArray()));
            }
            else
            {
                var fdat = new byte[4 + data.Length];
                BinaryPrimitives.WriteUInt32BigEndian(fdat, (uint)sequence++);
                data.ToArray().CopyTo(fdat, 4);
                chunks.Add(("fdAT", fdat));
            }
        }

        using var ms = new MemoryStream();
        ms.Write(PngSignature);
        WritePngChunk(ms, "IHDR", ihdr!);
        var actl = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(actl, (uint)frames.Length);
        BinaryPrimitives.WriteUInt32BigEndian(actl.AsSpan(4), (uint)plays);
        WritePngChunk(ms, "acTL", actl);
        foreach (var (type, body) in chunks) WritePngChunk(ms, type, body);
        WritePngChunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static IEnumerable<(string Type, byte[] Body)> ReadPngChunks(byte[] png)
    {
        for (int pos = 8; pos + 12 <= png.Length;)
        {
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            yield return (Encoding.ASCII.GetString(png, pos + 4, 4), png.AsSpan(pos + 8, length).ToArray());
            pos += 12 + length;
        }
    }

    private static void WritePngChunk(Stream s, string type, byte[] body)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var word = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)body.Length);
        s.Write(word);
        s.Write(typeBytes);
        s.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in typeBytes.Concat(body))
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFF);
        s.Write(word);
    }

    private static void MakePdf(string path, string title, int count, Color bg)
    {
        var pages = new List<(byte[] Jpeg, int W, int H)>();
        for (int i = 1; i <= count; i++)
        {
            int w = i == 3 ? 2400 : 1200;
            pages.Add((MakePage(title, i, count, w, 1700, Shade(bg, i)), w, 1700));
        }
        WritePdf(path, pages);
    }

    /// <summary>最小的 PDF 写入器：每页一张 JPEG（DCTDecode）。</summary>
    private static void WritePdf(string path, List<(byte[] Jpeg, int W, int H)> pages)
    {
        using var fs = File.Create(path);
        var offsets = new SortedDictionary<int, long>();
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); }

        W("%PDF-1.4\n");
        fs.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

        int n = pages.Count;
        offsets[1] = fs.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var kids = new StringBuilder();
        for (int i = 0; i < n; i++) kids.Append(3 + i * 3).Append(" 0 R ");
        offsets[2] = fs.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [" + kids + "] /Count " + n + " >>\nendobj\n");

        for (int i = 0; i < n; i++)
        {
            var (jpeg, pw, ph) = pages[i];
            int pageObj = 3 + i * 3, contentObj = pageObj + 1, imageObj = pageObj + 2;
            double wPt = pw / 2.0, hPt = ph / 2.0;
            string wS = wPt.ToString(CultureInfo.InvariantCulture), hS = hPt.ToString(CultureInfo.InvariantCulture);

            offsets[pageObj] = fs.Position;
            W(pageObj + " 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + wS + " " + hS + "] /Resources << /XObject << /Im0 "
              + imageObj + " 0 R >> >> /Contents " + contentObj + " 0 R >>\nendobj\n");

            var content = "q " + wS + " 0 0 " + hS + " 0 0 cm /Im0 Do Q";
            offsets[contentObj] = fs.Position;
            W(contentObj + " 0 obj\n<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream\nendobj\n");

            offsets[imageObj] = fs.Position;
            W(imageObj + " 0 obj\n<< /Type /XObject /Subtype /Image /Width " + pw + " /Height " + ph
              + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length + " >>\nstream\n");
            fs.Write(jpeg, 0, jpeg.Length);
            W("\nendstream\nendobj\n");
        }

        long xref = fs.Position;
        int size = 3 + n * 3;
        W("xref\n0 " + size + "\n0000000000 65535 f \n");
        for (int obj = 1; obj < size; obj++) W(offsets[obj].ToString("D10") + " 00000 n \n");
        W("trailer\n<< /Size " + size + " /Root 1 0 R >>\nstartxref\n" + xref + "\n%%EOF\n");
    }

    private static Color Shade(Color c, int i)
    {
        double f = 1 - (i % 4) * 0.05;
        return Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    }

    public static byte[] MakePage(string title, int page, int total, int w, int h, Color bg, bool png = false)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, w, h));
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), 2);
            for (int x = 100; x < w; x += 100) dc.DrawLine(grid, new Point(x, 0), new Point(x, h));
            for (int y = 100; y < h; y += 100) dc.DrawLine(grid, new Point(0, y), new Point(w, y));
            // 细线组：检查缩小时的抗锯齿质量
            var thin = new Pen(Brushes.Black, 1.5);
            for (int k = 0; k < 24; k++)
                dc.DrawLine(thin, new Point(60, h - 420 + k * 6), new Point(w - 60, h - 520 + k * 9));
            dc.DrawRectangle(null, new Pen(Brushes.Black, 14), new Rect(7, 7, w - 14, h - 14));
            dc.DrawRectangle(Brushes.Crimson, null, new Rect(24, 24, 120, 120));
            dc.DrawRectangle(Brushes.RoyalBlue, null, new Rect(w - 144, h - 144, 120, 120));

            var big = new FormattedText(page.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI Black"), Math.Min(w, h) / 2.2, Brushes.Black, 1.0);
            dc.DrawText(big, new Point((w - big.Width) / 2, (h - big.Height) / 2));
            var caption = new FormattedText(title + "   " + page + " / " + total, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"), Math.Max(24, Math.Min(w, h) / 24.0), Brushes.Black, 1.0);
            dc.DrawText(caption, new Point(170, 50));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        BitmapEncoder enc = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 88 };
        enc.Frames.Add(BitmapFrame.Create(png ? rtb : new FormatConvertedBitmap(rtb, PixelFormats.Bgr24, null, 0)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
