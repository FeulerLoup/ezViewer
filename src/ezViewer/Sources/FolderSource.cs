using System.Collections.Generic;
using System.IO;
using System.Threading;
using EzViewer.Core;

namespace EzViewer.Sources;

public sealed class FolderSource : ComicSource
{
    private readonly List<string> _files;

    public FolderSource(ComicItem item) : base(item)
    {
        if (!Directory.Exists(item.Path)) throw new DirectoryNotFoundException("文件夹不存在：" + item.Path);
        _files = ComicScanner.GetImageFiles(item.Path);
    }

    public override int PageCount => _files.Count;

    public override string GetPageName(int index) => Path.GetFileName(_files[index]);

    protected override byte[] ReadPageBytes(int index, CancellationToken ct) => File.ReadAllBytes(_files[index]);

    public override int IndexOfPage(string pagePath) => _files.FindIndex(f => PathUtil.Equal(f, pagePath));
}
