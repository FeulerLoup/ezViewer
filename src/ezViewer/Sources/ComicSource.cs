using System;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using EzViewer.Core;
using EzViewer.Imaging;

namespace EzViewer.Sources;

/// <summary>阅读区域的像素尺寸，PDF 按它决定渲染分辨率。</summary>
public readonly record struct RenderHint(int ViewportWidth, int ViewportHeight);

public abstract class ComicSource : IDisposable
{
    protected volatile bool Disposed;

    protected ComicSource(ComicItem item) => Item = item;

    public ComicItem Item { get; }

    public abstract int PageCount { get; }

    public abstract string GetPageName(int index);

    /// <summary>后台线程调用，返回已冻结的位图；动图另带播放用的 Animation。</summary>
    public virtual DecodedPage LoadPage(int index, RenderHint hint, CancellationToken ct)
    {
        var data = ReadPageBytes(index, ct);
        ct.ThrowIfCancellationRequested();
        return ImageDecoder.DecodePage(data);
    }

    protected virtual byte[] ReadPageBytes(int index, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>图片所在的页码（拖入单张图片时定位用），找不到返回 -1。</summary>
    public virtual int IndexOfPage(string pagePath) => -1;

    public virtual void Dispose() => Disposed = true;

    public static ComicSource Open(ComicItem item) => item.Kind switch
    {
        ComicKind.Folder => new FolderSource(item),
        ComicKind.Archive => new ArchiveSource(item),
        ComicKind.Pdf => new PdfSource(item),
        _ => throw new NotSupportedException(),
    };
}

public static class CoverExtractor
{
    public static BitmapSource Extract(ComicItem item, int boxWidth, int boxHeight, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case ComicKind.Folder:
            {
                var files = ComicScanner.GetImageFiles(item.Path);
                if (files.Count == 0) throw new InvalidDataException("文件夹中没有图片");
                Exception? last = null;
                for (int i = 0; i < Math.Min(files.Count, 3); i++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        return ImageDecoder.DecodeThumbnail(File.ReadAllBytes(files[i]), boxWidth, boxHeight);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        last = ex;
                    }
                }
                throw last!;
            }
            case ComicKind.Archive:
                return ImageDecoder.DecodeThumbnail(ArchiveSource.ReadFirstImage(item.Path, ct), boxWidth, boxHeight);
            case ComicKind.Pdf:
                return PdfSource.RenderCover(item.Path, boxWidth, boxHeight);
            default:
                throw new NotSupportedException();
        }
    }
}
