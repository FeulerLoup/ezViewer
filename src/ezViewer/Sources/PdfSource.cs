using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EzViewer.Core;
using EzViewer.Imaging;
using static EzViewer.Sources.PdfiumNative;

namespace EzViewer.Sources;

public sealed unsafe class PdfSource : ComicSource
{
    // 按“适配窗口尺寸 × 2”渲染，给放大留余量；长边限制在这个范围内
    private const double Oversample = 2.0;
    private const double MinLongSide = 1600;
    private const double MaxLongSide = 6000;

    private readonly FileStream _stream;
    private GCHandle _streamHandle;
    private FPDF_FILEACCESS* _access;
    private IntPtr _document;
    private readonly int _pageCount;

    public PdfSource(ComicItem item) : base(item)
    {
        EnsureInitialized();
        _stream = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        try
        {
            if (_stream.Length > uint.MaxValue) throw new NotSupportedException("PDF 文件超过 4GB，无法打开");

            _streamHandle = GCHandle.Alloc(_stream);
            _access = (FPDF_FILEACCESS*)NativeMemory.AllocZeroed((nuint)sizeof(FPDF_FILEACCESS));
            _access->m_FileLen = (uint)_stream.Length;
            _access->m_GetBlock = &GetBlock;
            _access->m_Param = GCHandle.ToIntPtr(_streamHandle);

            lock (Sync)
            {
                _document = FPDF_LoadCustomDocument(_access, null);
                if (_document == IntPtr.Zero)
                    throw new InvalidDataException(DescribeError(FPDF_GetLastError()));
                _pageCount = Math.Max(0, FPDF_GetPageCount(_document));
            }
        }
        catch
        {
            ReleaseResources();
            throw;
        }
    }

    public override int PageCount => _pageCount;

    public override string GetPageName(int index) => "第 " + (index + 1) + " 页";

    public override DecodedPage LoadPage(int index, RenderHint hint, CancellationToken ct)
    {
        lock (Sync)
        {
            if (_document == IntPtr.Zero) throw new ObjectDisposedException(nameof(PdfSource));
            ct.ThrowIfCancellationRequested();

            var (w, h) = GetPageSize(index);
            double fit = Math.Min(Math.Max(hint.ViewportWidth, 320) / w, Math.Max(hint.ViewportHeight, 320) / h);
            double longSide = Math.Clamp(Math.Max(w, h) * fit * Oversample, MinLongSide, MaxLongSide);
            double scale = longSide / Math.Max(w, h);
            return new DecodedPage(RenderPage(index, (int)Math.Round(w * scale), (int)Math.Round(h * scale)));
        }
    }

    public static BitmapSource RenderCover(string path, int boxWidth, int boxHeight)
    {
        using var source = new PdfSource(new ComicItem(path, ComicKind.Pdf));
        if (source.PageCount == 0) throw new InvalidDataException("PDF 没有页面");
        lock (Sync)
        {
            var (w, h) = source.GetPageSize(0);
            double scale = Math.Max(boxWidth / w, boxHeight / h);
            if (h * scale > boxHeight * 3.0) scale = boxHeight * 3.0 / h;
            if (w * scale > boxWidth * 3.0) scale = boxWidth * 3.0 / w;
            return source.RenderPage(0, (int)Math.Round(w * scale), (int)Math.Round(h * scale));
        }
    }

    // 调用方需持有 Sync
    private (double Width, double Height) GetPageSize(int index)
    {
        FS_SIZEF size;
        if (FPDF_GetPageSizeByIndexF(_document, index, &size) != 0 && size.Width > 1 && size.Height > 1)
            return (size.Width, size.Height);
        return (612, 792);
    }

    // 调用方需持有 Sync
    private BitmapSource RenderPage(int index, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var page = FPDF_LoadPage(_document, index);
        if (page == IntPtr.Zero) throw new InvalidDataException("无法加载第 " + (index + 1) + " 页：" + DescribeError(FPDF_GetLastError()));
        try
        {
            var bitmap = FPDFBitmap_Create(width, height, 0);
            if (bitmap == IntPtr.Zero) throw new OutOfMemoryException("内存不足，无法渲染 PDF 页面");
            try
            {
                FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, FPDF_ANNOT);
                var buffer = FPDFBitmap_GetBuffer(bitmap);
                int stride = FPDFBitmap_GetStride(bitmap);
                var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, buffer, stride * height, stride);
                result.Freeze();
                return result;
            }
            finally
            {
                FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int GetBlock(IntPtr param, uint position, byte* buffer, uint size)
    {
        try
        {
            var stream = (FileStream)GCHandle.FromIntPtr(param).Target!;
            stream.Position = position;
            var span = new Span<byte>(buffer, checked((int)size));
            while (span.Length > 0)
            {
                int n = stream.Read(span);
                if (n <= 0) return 0;
                span = span[n..];
            }
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    public override void Dispose()
    {
        lock (Sync)
        {
            if (Disposed) return;
            Disposed = true;
            if (_document != IntPtr.Zero)
            {
                FPDF_CloseDocument(_document);
                _document = IntPtr.Zero;
            }
        }
        ReleaseResources();
    }

    private void ReleaseResources()
    {
        if (_access != null)
        {
            NativeMemory.Free(_access);
            _access = null;
        }
        if (_streamHandle.IsAllocated) _streamHandle.Free();
        _stream.Dispose();
    }
}
