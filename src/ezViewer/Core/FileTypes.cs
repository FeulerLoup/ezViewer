using System;
using System.Collections.Generic;
using System.IO;

namespace EzViewer.Core;

public static class FileTypes
{
    // 实际解码交给 WIC：webp/avif/heic/jxl 需要系统装有对应的图像扩展。
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".apng", ".bmp", ".dib", ".gif", ".webp",
        ".tif", ".tiff", ".avif", ".heic", ".heif", ".jxl", ".jxr", ".wdp",
    };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".cbz", ".rar", ".cbr", ".7z", ".cb7", ".tar", ".cbt",
    };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsComicFile(string path) => IsArchive(path) || IsPdf(path);

    public static bool IsSupportedFile(string path) => IsComicFile(path) || IsImage(path);

    public const string OpenFileFilter =
        "漫画文件|*.zip;*.cbz;*.rar;*.cbr;*.7z;*.cb7;*.tar;*.cbt;*.pdf;*.jpg;*.jpeg;*.png;*.apng;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.avif;*.heic;*.jxl|所有文件|*.*";
}
