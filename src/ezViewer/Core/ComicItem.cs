using System;
using System.IO;

namespace EzViewer.Core;

public enum ComicKind
{
    Folder,
    Archive,
    Pdf,

    /// <summary>本身不直接含图片、只能进入的文件夹（列表模式中出现）。</summary>
    Directory,
}

/// <summary>
/// 一本漫画（直接包含图片的文件夹、压缩包或 PDF），或列表模式中可以进入的文件夹。
/// </summary>
public sealed class ComicItem : IEquatable<ComicItem>
{
    public ComicItem(string path, ComicKind kind, bool canEnter = false, bool isSelf = false)
    {
        Path = path;
        Kind = kind;
        CanEnter = canEnter;
        IsSelf = isSelf;
        Name = kind is ComicKind.Folder or ComicKind.Directory ? PathUtil.GetDisplayName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(Name)) Name = path;
        ParentDirectory = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
    }

    public string Path { get; }

    public ComicKind Kind { get; }

    public string Name { get; }

    public string ParentDirectory { get; }

    /// <summary>文件夹里还有子文件夹或压缩包/PDF，可以用 Alt+↓ 进入。</summary>
    public bool CanEnter { get; }

    /// <summary>列表所在文件夹自身的图片。</summary>
    public bool IsSelf { get; }

    public bool IsReadable => Kind != ComicKind.Directory;

    public bool IsDirectory => Kind == ComicKind.Directory;

    public string DisplayName => IsSelf ? Name + "（本文件夹）" : Name;

    public string KindLabel => Kind switch
    {
        ComicKind.Folder => "图片",
        ComicKind.Pdf => "PDF",
        ComicKind.Directory => "文件夹",
        _ => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant(),
    };

    public static ComicItem? TryCreateForFile(string path)
    {
        if (FileTypes.IsArchive(path)) return new ComicItem(path, ComicKind.Archive);
        if (FileTypes.IsPdf(path)) return new ComicItem(path, ComicKind.Pdf);
        return null;
    }

    public bool Equals(ComicItem? other) => other is not null && PathUtil.Equal(Path, other.Path);

    public override bool Equals(object? obj) => obj is ComicItem other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Path);

    public override string ToString() => Path;
}

public static class PathUtil
{
    public static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path.Trim().Trim('"'));
        var root = System.IO.Path.GetPathRoot(full);
        if (full.Length > (root?.Length ?? 0))
            full = full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        return full;
    }

    public static bool Equal(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    public static string GetDisplayName(string path)
    {
        var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>child 位于 root 之内（或相同）时返回相对路径，否则返回 null。</summary>
    public static string? GetRelative(string root, string child)
    {
        if (Equal(root, child)) return string.Empty;
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? child[prefix.Length..] : null;
    }

    /// <summary>path 等于 folder，或位于 folder 之内。</summary>
    public static bool IsSameOrInside(string folder, string path) => GetRelative(folder, path) != null;
}
