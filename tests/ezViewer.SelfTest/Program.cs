using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EzViewer.Core;
using EzViewer.Imaging;
using EzViewer.Reader;
using EzViewer.Sources;

namespace EzViewer.SelfTest;

internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string root = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "ezViewer-testdata");
        string lib = Path.Combine(root, "lib");

        Console.WriteLine("== 生成测试数据: " + lib);
        TestData.Generate(lib);
        TestData.DownloadRarSamples(Path.Combine(lib, "RAR样例"));
        TestData.DownloadWebpSample(Path.Combine(lib, "动图", "03 动图.webp"));

        Run("自然排序", TestNaturalSort);
        Run("列表：书库根（子文件夹 + 图片文件夹）", () => TestList(lib,
            "RAR样例/", "Z长列表/", "动图", "多语言/", "散图>", "特殊尺寸", "作者A/", "作者B/", "作者C/"));
        Run("列表：作者A", () => TestList(Path.Combine(lib, "作者A"), "第1卷", "第2卷", "第10卷"));
        Run("列表：作者B", () => TestList(Path.Combine(lib, "作者B"), "合集", "旧压缩包GBK", "漫画", "杂志"));
        Run("列表：散图（自身 + 子文件夹）", () => TestList(Path.Combine(lib, "散图"), "散图（本文件夹）", "插画"));
        Run("列表：作者C（只有子文件夹）", () => TestList(Path.Combine(lib, "作者C"), "系列/"));
        Run("数据源：全部漫画", () => TestAllSources(lib));
        Run("文件名编码识别", TestNameDecoding);
        Run("GBK 文件名", () => TestGbkNames(Path.Combine(lib, "作者B", "旧压缩包GBK.zip")));
        Run("日文、韩文、繁体、macOS 压缩包", () => TestMultilingual(Path.Combine(lib, "多语言")));
        Run("动图解码", () => TestAnimations(Path.Combine(lib, "动图")));
        Run("zip 排序与过滤", () => TestZipOrder(Path.Combine(lib, "作者B", "合集.zip")));
        Run("PDF 渲染尺寸", () => TestPdf(Path.Combine(lib, "作者B", "杂志.pdf")));
        Run("打开计划", () => TestPlans(lib));
        Run("递归扫描", () => TestScan(lib));
        Run("封面提取", () => TestCovers(lib));
        Run("页面加载器（快速翻页）", () => TestLoader(Path.Combine(lib, "作者C", "系列", "卷1.7z")));

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "== 全部通过" : "== 失败 " + _failures + " 项");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        Console.WriteLine();
        Console.WriteLine("── " + name);
        var sw = Stopwatch.StartNew();
        try
        {
            test();
            Console.WriteLine("   通过 (" + sw.ElapsedMilliseconds + " ms)");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.WriteLine("   失败: " + ex);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("断言失败: " + message);
    }

    private static void TestNaturalSort()
    {
        var list = new List<string> { "第10卷", "第2卷", "第1卷", "img12.jpg", "img2.jpg", "img1.jpg" };
        list.Sort(NaturalComparer.Instance);
        Console.WriteLine("   " + string.Join(", ", list));
        Check(list.IndexOf("第2卷") < list.IndexOf("第10卷"), "第2卷 应在 第10卷 之前");
        Check(list.IndexOf("img2.jpg") < list.IndexOf("img12.jpg"), "img2 应在 img12 之前");
    }

    /// <summary>期望值中 “/” 结尾表示只能进入的文件夹，“>” 结尾表示可阅读且可进入。</summary>
    private static void TestList(string dir, params string[] expected)
    {
        var items = ComicScanner.ListEntries(dir);
        string Describe(ComicItem i) => i.DisplayName + (i.IsDirectory ? "/" : i.CanEnter ? ">" : "");
        foreach (var i in items) Console.WriteLine("   " + i.KindLabel.PadRight(4) + " " + Describe(i));
        Check(items.Select(Describe).SequenceEqual(expected), "列表应为 " + string.Join(", ", expected));
    }

    private static void TestAllSources(string lib)
    {
        var all = new List<ComicItem>();
        ComicScanner.ScanRecursive(lib, all.Add, CancellationToken.None);
        var hint = new RenderHint(1920, 1080);
        foreach (var item in all)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var src = ComicSource.Open(item);
                string solid = src is ArchiveSource { IsSolid: true } ? "(固实)" : "";
                int ok = 0, bad = 0;
                var errors = new List<string>();
                for (int p = 0; p < src.PageCount; p++)
                {
                    try
                    {
                        var bmp = src.LoadPage(p, hint, CancellationToken.None).Image;
                        Check(bmp.IsFrozen && bmp.PixelWidth > 0, "位图应已冻结");
                        ok++;
                    }
                    catch (Exception ex)
                    {
                        bad++;
                        errors.Add(src.GetPageName(p) + ": " + PageLoader.Describe(ex));
                    }
                }
                Console.WriteLine($"   [{item.KindLabel}] {Rel(lib, item.Path)}  页数={src.PageCount} 成功={ok} 失败={bad}  {sw.ElapsedMilliseconds}ms {solid}");
                foreach (var e in errors) Console.WriteLine("        " + e);
                bool expectBroken = item.Path.EndsWith("特殊尺寸", StringComparison.Ordinal);
                Check(bad == (expectBroken ? 1 : 0), Rel(lib, item.Path) + " 失败页数不符合预期");
            }
            catch (Exception ex) when (item.Path.Contains("RAR样例"))
            {
                Console.WriteLine($"   [{item.KindLabel}] {Rel(lib, item.Path)}  打开失败: {PageLoader.Describe(ex)}");
                throw;
            }
        }
    }

    /// <summary>单个文件名（最难的情况，压缩包里通常有多个文件名一起判断）在中文、日文、韩文系统上的识别结果。</summary>
    private static void TestNameDecoding()
    {
        Console.WriteLine("   系统 ANSI 代码页: " + EzViewer.Interop.NativeMethods.GetACP());
        var cases = new (int CodePage, string[] Names)[]
        {
            (65001, new[] { "页面_001.jpg", "第1話 カラー.jpg", "001.jpg", "Глава 1.jpg", "Café.jpg" }),
            (936, new[] { "图片01.jpg", "第一卷 封面.jpg", "海贼王 第1000话.png", "目录.jpg", "彩页.jpg" }),
            (932, new[] { "進撃の巨人 第01巻.jpg", "あとがき.jpg", "表紙.jpg", "ソードアート・オンライン.jpg" }),
            (949, new[] { "원피스 1권 표지.jpg", "나 혼자만 레벨업.jpg", "목차.jpg" }),
            (950, new[] { "進擊的巨人 第1話.jpg", "海賊王 第1卷.jpg", "封面.jpg" }),
        };
        foreach (int system in new[] { 936, 932, 949 })
        {
            foreach (var (codePage, names) in cases)
            {
                foreach (var s in names)
                {
                    var bytes = codePage == 65001 ? Encoding.UTF8.GetBytes(s) : Encoding.GetEncoding(codePage).GetBytes(s);
                    var r = ArchiveNameDecoder.DecodeName(bytes, system);
                    if (system == 936) Console.WriteLine($"   [{codePage}] {s} → {r}");
                    Check(r == s, $"系统代码页 {system}：{codePage} 文件名 {s} 识别成了 {r}");
                }
            }
        }
    }

    private static void TestMultilingual(string dir)
    {
        foreach (var (file, _, expected) in TestData.MultilingualZips)
        {
            using var src = new ArchiveSource(new ComicItem(Path.Combine(dir, file), ComicKind.Archive));
            var names = Enumerable.Range(0, src.PageCount).Select(src.GetPageName).ToList();
            Console.WriteLine("   " + file + ": " + string.Join(", ", names));
            Check(names.ToHashSet().SetEquals(expected), file + " 的文件名应正确解码");
            int i02 = names.FindIndex(n => n.EndsWith("02.jpg", StringComparison.Ordinal));
            int i10 = names.FindIndex(n => n.EndsWith("10.jpg", StringComparison.Ordinal));
            Check(i02 >= 0 && i02 < i10, file + " 应按自然顺序排列（02 在 10 之前）");
            var cover = CoverExtractor.Extract(new ComicItem(src.Item.Path, ComicKind.Archive), 300, 420, CancellationToken.None);
            Check(cover.PixelWidth > 0, file + " 应能读出封面");
        }
    }

    private static void TestAnimations(string dir)
    {
        static uint Pixel(byte[] frame, int width, int x, int y) => BitConverter.ToUInt32(frame, (y * width + x) * 4);

        static List<byte[]> RenderAll(AnimatedImage a)
        {
            var decoder = a.CreateDecoder();
            var frames = new List<byte[]>();
            for (int i = 0; i < a.FrameCount; i++)
            {
                var buffer = new byte[a.Width * a.Height * 4];
                decoder.Render(i, buffer);
                frames.Add(buffer);
            }
            // 往回取第一帧要重新合成，结果应与第一次相同
            var again = new byte[a.Width * a.Height * 4];
            decoder.Render(0, again);
            Check(again.AsSpan().SequenceEqual(frames[0]), "回到第一帧应与首次合成结果相同");
            return frames;
        }

        static bool Near(uint actual, uint expected) =>
            Enumerable.Range(0, 4).All(k => Math.Abs((int)((actual >> (8 * k)) & 0xFF) - (int)((expected >> (8 * k)) & 0xFF)) <= 1);

        const uint Red = 0xFFFF0000, Blue = 0xFF0000FF, Lime = 0xFF00FF00, White = 0xFFFFFFFF, Black = 0xFF000000, Clear = 0;

        var gifPage = ImageDecoder.DecodePage(File.ReadAllBytes(Path.Combine(dir, "01 合成.gif")));
        var gif = gifPage.Animation;
        Check(gif != null, "GIF 应识别为动图");
        Console.WriteLine($"   GIF: {gif!.Width}x{gif.Height} {gif.FrameCount} 帧 时长 {string.Join("/", gif.Delays)} 循环 {gif.LoopCount}");
        Check(gif.Width == 300 && gif.Height == 420 && gif.FrameCount == 3, "GIF 画布与帧数");
        Check(gif.Delays.SequenceEqual(new[] { 50, 100, 100 }), "GIF 时长应为 50/100/100（0 按 100 毫秒）");
        Check(gif.LoopCount == 0, "NETSCAPE 扩展重复 0 次表示一直循环");
        Check(ReferenceEquals(gifPage.Image, gif.FirstFrame) && gif.FirstFrame.IsFrozen, "页面图片应是冻结的第一帧");
        var g = RenderAll(gif);
        Check(Pixel(g[0], 300, 50, 50) == Red, "GIF 第 1 帧整幅红色");
        Check(Pixel(g[1], 300, 50, 50) == Blue && Pixel(g[1], 300, 5, 5) == Red, "GIF 第 2 帧左上蓝块叠在红底上");
        Check(Pixel(g[2], 300, 50, 50) == Clear, "GIF 第 2 帧处置方式是恢复背景，第 3 帧时蓝块处应透明");
        Check(Pixel(g[2], 300, 160, 250) == Lime && Pixel(g[2], 300, 230, 250) == Red, "GIF 第 3 帧左半绿色，右半透明露出红底");

        var apng = ImageDecoder.DecodePage(File.ReadAllBytes(Path.Combine(dir, "02 半透明.png"))).Animation;
        Check(apng != null, "APNG 应识别为动图");
        Console.WriteLine($"   APNG: {apng!.Width}x{apng.Height} {apng.FrameCount} 帧 时长 {string.Join("/", apng.Delays)} 播放 {apng.LoopCount} 遍");
        Check(apng.Width == 300 && apng.Height == 420 && apng.FrameCount == 4, "APNG 画布与帧数");
        Check(apng.Delays.SequenceEqual(new[] { 100, 50, 100, 30 }), "APNG 时长应为 100/50/100/30");
        Check(apng.LoopCount == 2, "APNG 应播放 2 遍");
        var p = RenderAll(apng);
        Check(Pixel(p[0], 300, 100, 100) == White, "APNG 第 1 帧白色");
        Check(Near(Pixel(p[1], 300, 100, 100), 0xFF7F7FFF), $"APNG 第 2 帧半透明蓝叠在白底上，实际 {Pixel(p[1], 300, 100, 100):X8}");
        Check(Near(Pixel(p[2], 300, 100, 100), 0x80800000) && Pixel(p[2], 300, 40, 40) == White, "APNG 第 3 帧半透明红直接替换");
        Check(Near(Pixel(p[3], 300, 100, 100), 0xFF7F7FFF) && Pixel(p[3], 300, 5, 5) == Black, "APNG 第 3 帧处置方式是恢复到上一帧");

        var webpPath = Path.Combine(dir, "03 动图.webp");
        if (File.Exists(webpPath))
        {
            var webp = ImageDecoder.DecodePage(File.ReadAllBytes(webpPath)).Animation;
            Check(webp != null, "WebP 动图应识别为动图");
            Console.WriteLine($"   WebP: {webp!.Width}x{webp.Height} {webp.FrameCount} 帧 时长 {string.Join("/", webp.Delays.Distinct())} 循环 {webp.LoopCount}");
            Check(webp.Width == 400 && webp.Height == 400 && webp.FrameCount == 12 && webp.Delays.All(d => d == 70), "WebP 画布、帧数、时长");
            var w = RenderAll(webp);
            Check(!w[0].AsSpan().SequenceEqual(w[6]), "WebP 各帧画面应不同");
        }
        else
        {
            Console.WriteLine("   没有 WebP 样例，跳过");
        }

        Check(ImageDecoder.DecodePage(TestData.MakePage("静态", 1, 1, 300, 420, Colors.White)).Animation == null, "静态 JPEG 不是动图");
        var staticGif = new GifBitmapEncoder();
        staticGif.Frames.Add(BitmapFrame.Create(BitmapSource.Create(4, 4, 96, 96, PixelFormats.Gray8, null, new byte[16], 4)));
        using var ms = new MemoryStream();
        staticGif.Save(ms);
        Check(ImageDecoder.DecodePage(ms.ToArray()).Animation == null, "单帧 GIF 不是动图");
    }

    private static void TestGbkNames(string path)
    {
        using var src = new ArchiveSource(new ComicItem(path, ComicKind.Archive));
        var names = Enumerable.Range(0, src.PageCount).Select(src.GetPageName).ToList();
        Console.WriteLine("   " + string.Join(", ", names));
        Check(names.Count == 6, "应有 6 页");
        Check(names.All(n => n.StartsWith("图片", StringComparison.Ordinal)), "GBK 文件名应正确解码");
        Check(names[0] == "图片01.jpg" && names[5] == "图片06.jpg", "应按名称排序");
    }

    private static void TestZipOrder(string path)
    {
        using var src = new ArchiveSource(new ComicItem(path, ComicKind.Archive));
        var names = Enumerable.Range(0, src.PageCount).Select(src.GetPageName).ToList();
        Console.WriteLine("   " + string.Join(", ", names.Take(5)) + " …");
        Check(src.PageCount == 15, "应过滤掉 __MACOSX 与 txt，剩 15 页");
        Check(names[0] == "页面_001.jpg" && names[14] == "页面_015.jpg", "写入顺序是倒序，读取应为正序");
    }

    private static void TestPdf(string path)
    {
        using var src = new PdfSource(new ComicItem(path, ComicKind.Pdf));
        Check(src.PageCount == 5, "PDF 应有 5 页");
        var p1 = src.LoadPage(0, new RenderHint(1920, 1080), CancellationToken.None).Image;
        var p3 = src.LoadPage(2, new RenderHint(1920, 1080), CancellationToken.None).Image;
        Console.WriteLine($"   第1页 {p1.PixelWidth}x{p1.PixelHeight}，第3页（横向）{p3.PixelWidth}x{p3.PixelHeight}");
        Check(p1.PixelHeight > p1.PixelWidth, "第1页应为纵向");
        Check(p3.PixelWidth > p3.PixelHeight, "第3页应为横向");
        Check(Math.Max(p1.PixelWidth, p1.PixelHeight) >= 1600, "渲染分辨率应足够");
    }

    private static void TestPlans(string lib)
    {
        void Show(string label, ListOpenPlan p) =>
            Console.WriteLine($"   {label}: 列表={Rel(lib, p.ListFolder)} 选中={p.Items[p.SelectedIndex].DisplayName} 起始图={(p.StartImagePath == null ? "-" : Path.GetFileName(p.StartImagePath))}");

        var p1 = ComicScanner.PlanOpen(lib, CancellationToken.None);
        Show("打开书库根", p1);
        Check(PathUtil.Equal(p1.ListFolder, lib) && p1.Items[p1.SelectedIndex].Name == "动图", "应列出书库根并选中第一本可阅读的漫画");

        var p2 = ComicScanner.PlanOpen(Path.Combine(lib, "作者A", "第2卷"), CancellationToken.None);
        Show("打开图片文件夹", p2);
        Check(PathUtil.Equal(p2.ListFolder, Path.Combine(lib, "作者A")) && p2.Items[p2.SelectedIndex].Name == "第2卷", "纯图片文件夹应显示上级列表并选中自己");

        var p3 = ComicScanner.PlanOpen(Path.Combine(lib, "作者B", "漫画.cbz"), CancellationToken.None);
        Show("打开压缩包", p3);
        Check(p3.Items[p3.SelectedIndex].Name == "漫画", "应选中打开的压缩包");

        var p4 = ComicScanner.PlanOpen(Path.Combine(lib, "散图", "002.png"), CancellationToken.None);
        Show("打开单张图片（文件夹里还有子文件夹）", p4);
        Check(PathUtil.Equal(p4.ListFolder, Path.Combine(lib, "散图")) && p4.Items[p4.SelectedIndex].IsSelf && p4.StartImagePath != null,
            "应列出图片所在文件夹，选中“本文件夹”并定位到该图");

        var p5 = ComicScanner.PlanOpen(Path.Combine(lib, "特殊尺寸", "03 小图.jpg"), CancellationToken.None);
        Show("打开单张图片（纯图片文件夹）", p5);
        Check(PathUtil.Equal(p5.ListFolder, lib) && p5.Items[p5.SelectedIndex].Name == "特殊尺寸" && p5.StartImagePath != null,
            "应列出上一级并选中该文件夹");

        var p6 = ComicScanner.PlanOpen(Path.Combine(lib, "作者C"), CancellationToken.None);
        Show("打开只有子文件夹的文件夹", p6);
        Check(PathUtil.Equal(p6.ListFolder, Path.Combine(lib, "作者C")) && p6.Items[p6.SelectedIndex].IsDirectory, "应列出子文件夹并选中第一项");

        try
        {
            ComicScanner.PlanOpen(Path.Combine(lib, "空文件夹"), CancellationToken.None);
            Check(false, "空文件夹应报错");
        }
        catch (DirectoryNotFoundException ex)
        {
            Console.WriteLine("   空文件夹: " + ex.Message);
        }
    }

    private static void TestScan(string lib)
    {
        var all = new List<ComicItem>();
        ComicScanner.ScanRecursive(lib, all.Add, CancellationToken.None);
        foreach (var i in all) Console.WriteLine("   " + i.KindLabel.PadRight(4) + " " + Rel(lib, i.Path));
        Check(all.Count >= 13, "至少应扫描到 13 本");
        Check(all.FindIndex(i => i.Name == "第2卷") < all.FindIndex(i => i.Name == "第10卷"), "扫描结果应自然排序");
    }

    private static void TestCovers(string lib)
    {
        var all = new List<ComicItem>();
        ComicScanner.ScanRecursive(lib, all.Add, CancellationToken.None);
        foreach (var item in all)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var bmp = CoverExtractor.Extract(item, 300, 420, CancellationToken.None);
                Console.WriteLine($"   {Rel(lib, item.Path)}  {bmp.PixelWidth}x{bmp.PixelHeight}  {sw.ElapsedMilliseconds}ms");
                Check(bmp.PixelWidth >= 290 || bmp.PixelHeight >= 410 || item.Path.Contains("RAR样例"), "封面尺寸应填满 300x420");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   {Rel(lib, item.Path)}  封面失败: {PageLoader.Describe(ex)}");
                throw;
            }
        }
    }

    private static void TestLoader(string path)
    {
        var item = new ComicItem(path, ComicKind.Archive);
        using var session = ReaderSession.Open(item, new RenderHint(1920, 1080));
        Console.WriteLine("   固实包: " + (session.Source is ArchiveSource { IsSolid: true } ? "是" : "否") + "  页数 " + session.PageCount);

        // 快速连续请求，只等待最后一页
        Task<PageResult>? last = null;
        for (int i = 0; i < session.PageCount; i++) last = session.Loader.GetPageAsync(i);
        var result = last!.Wait(TimeSpan.FromSeconds(20)) ? last.Result : null;
        Check(result?.Image != null, "最后请求的页面应能加载");

        var back = session.Loader.GetPageAsync(0);
        Check(back.Wait(TimeSpan.FromSeconds(20)) && back.Result.Image != null, "跳回第 1 页应能加载");
        Console.WriteLine("   快速翻页后最后一页: " + result!.Image!.PixelWidth + "x" + result.Image.PixelHeight);
    }

    private static string Rel(string root, string path) => PathUtil.GetRelative(root, path) is { } r ? (r.Length == 0 ? "." : r) : path;
}
