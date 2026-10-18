using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.VisualBasic.FileIO;

namespace EzViewer.Core;

/// <summary>列表模式打开某个路径后的结果：显示哪个文件夹的列表、选中哪一项。</summary>
public sealed record ListOpenPlan(
    string ListFolder,
    List<ComicItem> Items,
    int SelectedIndex,
    string? StartImagePath);

public static class ComicScanner
{
    private const int MaxDepth = 40;

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private readonly record struct DirContents(List<string> Subdirs, List<string> ComicFiles, bool HasImages);

    private readonly record struct DirProbe(bool HasImages, bool HasComicFiles, bool HasSubdirs)
    {
        public bool CanEnter => HasComicFiles || HasSubdirs;
    }

    private static DirContents Read(string dir)
    {
        var subdirs = new List<string>();
        var files = new List<string>();
        bool hasImages = false;
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options))
            {
                if ((info.Attributes & FileAttributes.Directory) != 0)
                    subdirs.Add(info.FullName);
                else if (FileTypes.IsComicFile(info.Name))
                    files.Add(info.FullName);
                else if (!hasImages && FileTypes.IsImage(info.Name))
                    hasImages = true;
            }
        }
        catch (Exception)
        {
            // 无权限、被删除等情况按空目录处理
        }

        subdirs.Sort(NaturalComparer.Instance);
        files.Sort(NaturalComparer.Instance);
        return new DirContents(subdirs, files, hasImages);
    }

    /// <summary>只看一层：是否直接含图片、压缩包/PDF、子文件夹。</summary>
    private static DirProbe Probe(string dir)
    {
        bool images = false, comicFiles = false, subdirs = false;
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", Options))
            {
                if ((info.Attributes & FileAttributes.Directory) != 0) subdirs = true;
                else if (FileTypes.IsComicFile(info.Name)) comicFiles = true;
                else if (FileTypes.IsImage(info.Name)) images = true;
                if (images && comicFiles && subdirs) break;
            }
        }
        catch (Exception)
        {
        }
        return new DirProbe(images, comicFiles, subdirs);
    }

    public static List<string> GetImageFiles(string dir)
    {
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", Options))
                if (FileTypes.IsImage(f)) list.Add(f);
        }
        catch (Exception)
        {
        }
        list.Sort(NaturalComparer.Instance);
        return list;
    }

    /// <summary>
    /// 列表模式中一个文件夹的内容：自身的图片（若有）、子文件夹（含图片的是漫画，否则是可进入的文件夹）、压缩包、PDF。
    /// 一层里什么都没有的子文件夹不显示。
    /// </summary>
    public static List<ComicItem> ListEntries(string dir)
    {
        var c = Read(dir);
        var result = new List<ComicItem>();
        if (c.HasImages) result.Add(new ComicItem(dir, ComicKind.Folder, canEnter: false, isSelf: true));
        foreach (var sub in c.Subdirs)
        {
            var p = Probe(sub);
            if (p.HasImages) result.Add(new ComicItem(sub, ComicKind.Folder, canEnter: p.CanEnter));
            else if (p.CanEnter) result.Add(new ComicItem(sub, ComicKind.Directory, canEnter: true));
        }
        foreach (var f in c.ComicFiles)
        {
            var item = ComicItem.TryCreateForFile(f);
            if (item != null) result.Add(item);
        }
        return result;
    }

    /// <summary>默认选中第一本可以阅读的漫画；全是文件夹时选中第一项。</summary>
    public static int PreferredIndex(List<ComicItem> items)
    {
        int i = items.FindIndex(x => x.IsReadable);
        return i >= 0 ? i : 0;
    }

    /// <summary>递归查找 root 下所有漫画，顺序与目录树一致（自身、子文件夹、文件）。</summary>
    public static void ScanRecursive(string root, Action<ComicItem> onFound, CancellationToken ct)
    {
        Scan(root, 0);

        void Scan(string dir, int depth)
        {
            ct.ThrowIfCancellationRequested();
            var c = Read(dir);
            if (c.HasImages)
                onFound(new ComicItem(dir, ComicKind.Folder, canEnter: c.Subdirs.Count > 0 || c.ComicFiles.Count > 0));
            if (depth < MaxDepth)
                foreach (var s in c.Subdirs) Scan(s, depth + 1);
            foreach (var f in c.ComicFiles)
            {
                var item = ComicItem.TryCreateForFile(f);
                if (item != null) onFound(item);
            }
        }
    }

    public static ListOpenPlan PlanOpen(string path, CancellationToken ct)
    {
        path = PathUtil.Normalize(path);

        if (File.Exists(path))
        {
            var dir = Path.GetDirectoryName(path) ?? throw new IOException("无法确定文件所在的文件夹");
            if (FileTypes.IsComicFile(path))
            {
                var items = ListEntries(dir);
                int idx = items.FindIndex(i => PathUtil.Equal(i.Path, path));
                if (idx < 0)
                {
                    items.Add(ComicItem.TryCreateForFile(path)!);
                    idx = items.Count - 1;
                }
                return new ListOpenPlan(dir, items, idx, null);
            }

            if (FileTypes.IsImage(path))
            {
                var plan = PlanForFolder(dir);
                int idx = plan.Items.FindIndex(i => PathUtil.Equal(i.Path, dir));
                if (idx < 0)
                {
                    plan.Items.Insert(0, new ComicItem(dir, ComicKind.Folder, isSelf: PathUtil.Equal(plan.ListFolder, dir)));
                    idx = 0;
                }
                return plan with { SelectedIndex = idx, StartImagePath = path };
            }

            throw new NotSupportedException("不支持的文件类型：" + Path.GetExtension(path));
        }

        if (Directory.Exists(path))
            return PlanForFolder(path);

        throw new FileNotFoundException("路径不存在：" + path);
    }

    private static ListOpenPlan PlanForFolder(string dir)
    {
        var probe = Probe(dir);
        if (probe.HasImages && !probe.CanEnter)
        {
            // 只有图片的文件夹本身就是一本漫画：显示上一级的列表并选中它，方便上下切换同级的卷
            var parent = Path.GetDirectoryName(dir);
            if (parent != null)
            {
                var siblings = ListEntries(parent);
                int idx = siblings.FindIndex(i => PathUtil.Equal(i.Path, dir));
                if (idx >= 0) return new ListOpenPlan(parent, siblings, idx, null);
            }
        }

        var items = ListEntries(dir);
        if (items.Count == 0) throw new DirectoryNotFoundException("该文件夹中没有漫画，也没有子文件夹：" + dir);
        return new ListOpenPlan(dir, items, PreferredIndex(items), null);
    }

    /// <summary>
    /// 将漫画移动到回收站；若漫画文件是其所在目录中仅有的文件（且不含子目录），则连同该目录一起移动到回收站。
    /// </summary>
    public static void MoveToRecycleBin(ComicItem item, string? root = null)
    {
        var path = PathUtil.Normalize(item.Path);
        if (item.Kind == ComicKind.Folder)
        {
            if (!Directory.Exists(path)) return;
            if (CanRecycleDirectory(path, root) && IsPureImageDirectory(path))
            {
                FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            }
            else
            {
                foreach (var img in GetImageFiles(path))
                    FileSystem.DeleteFile(img, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            }
            return;
        }

        if (!File.Exists(path)) return;
        var dir = Path.GetDirectoryName(path);
        if (dir != null && CanRecycleDirectory(dir, root) && IsOnlyFileInDirectory(path, dir))
            FileSystem.DeleteDirectory(dir, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
        else
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }

    private static bool CanRecycleDirectory(string dir, string? root) =>
        !string.IsNullOrEmpty(dir) &&
        Path.GetDirectoryName(dir) != null &&
        (root == null || !PathUtil.Equal(dir, root));

    private static bool IsOnlyFileInDirectory(string filePath, string dir)
    {
        try
        {
            bool foundSelf = false;
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.Directory) != 0) return false;
                if (PathUtil.Equal(info.FullName, filePath))
                {
                    foundSelf = true;
                    continue;
                }
                if (IsIgnorableSystemFile(info)) continue;
                return false;
            }
            return foundSelf;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsPureImageDirectory(string dir)
    {
        try
        {
            foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.Directory) != 0) return false;
                if (FileTypes.IsImage(info.Name) || IsIgnorableSystemFile(info)) continue;
                return false;
            }
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsIgnorableSystemFile(FileSystemInfo info) =>
        (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 &&
        (info.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
         info.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));
}
