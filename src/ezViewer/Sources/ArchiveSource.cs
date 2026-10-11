using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using EzViewer.Core;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace EzViewer.Sources;

/// <summary>
/// zip/cbz/rar/cbr/7z/cb7/tar。非固实包按需随机读取；固实包随机读取极慢，
/// 打开后在后台线程按顺序整包解压，页面解出后即可显示。
/// </summary>
public sealed class ArchiveSource : ComicSource
{
    // 固实包解压后的总大小超过该值时落到临时目录，避免占用过多内存
    private const long SpillThreshold = 768L * 1024 * 1024;

    private readonly FileStream _stream;
    private readonly IArchive _archive;
    private readonly List<IArchiveEntry> _entries;
    private readonly string[] _names;
    private readonly bool _solid;
    private readonly object _lock = new();

    private byte[]?[]? _buffers;
    private bool[]? _available;
    private string? _spillDir;
    private bool _extractDone;
    private Exception? _extractError;
    private Thread? _extractThread;
    private IReader? _reader;

    public ArchiveSource(ComicItem item) : base(item)
    {
        _stream = OpenStream(item.Path);
        try
        {
            _archive = ArchiveFactory.Open(_stream, CreateReaderOptions());
            var pages = ListPages(_archive);

            _entries = new List<IArchiveEntry>(pages.Count);
            _names = new string[pages.Count];
            for (int i = 0; i < pages.Count; i++)
            {
                _entries.Add(pages[i].Entry);
                _names[i] = pages[i].Name;
            }

            if (_entries.Count > 0 && _entries[0].IsEncrypted)
                throw new NotSupportedException("压缩包已加密，暂不支持带密码的压缩包");

            _solid = IsSolidArchive(_archive);
        }
        catch
        {
            _archive?.Dispose();
            _stream.Dispose();
            throw;
        }

        if (_solid && _entries.Count > 0) StartExtraction();
    }

    public override int PageCount => _entries.Count;

    public bool IsSolid => _solid;

    public override string GetPageName(int index) => _names[index];

    protected override byte[] ReadPageBytes(int index, CancellationToken ct)
    {
        if (_solid)
        {
            lock (_lock)
            {
                while (!_available![index])
                {
                    if (Disposed) throw new ObjectDisposedException(nameof(ArchiveSource));
                    if (_extractDone)
                        throw new InvalidDataException("无法从压缩包中解出此页" + (_extractError != null ? "：" + _extractError.Message : string.Empty));
                    ct.ThrowIfCancellationRequested();
                    Monitor.Wait(_lock, 250);
                }
                if (_buffers != null) return _buffers[index]!;
            }
            return File.ReadAllBytes(SpillPath(index));
        }

        lock (_lock)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(ArchiveSource));
            var entry = _entries[index];
            using var s = entry.OpenEntryStream();
            return ReadAll(s, entry.Size);
        }
    }

    private void StartExtraction()
    {
        int n = _entries.Count;
        _available = new bool[n];
        long total = 0;
        try
        {
            total = _archive.TotalUncompressSize;
        }
        catch (Exception)
        {
        }

        if (total > SpillThreshold)
        {
            _spillDir = Path.Combine(Path.GetTempPath(), "ezViewer", "x" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_spillDir);
        }
        else
        {
            _buffers = new byte[]?[n];
        }

        var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            var key = _entries[i].Key ?? string.Empty;
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<int>(1);
            list.Add(i);
        }

        _extractThread = new Thread(() => ExtractAll(map))
        {
            IsBackground = true,
            Name = "ezViewer solid extract",
        };
        _extractThread.Start();
    }

    private void ExtractAll(Dictionary<string, List<int>> map)
    {
        try
        {
            IReader reader;
            lock (_lock)
            {
                if (Disposed) return;
                reader = _archive.ExtractAllEntries();
                _reader = reader;
            }

            using (reader)
            {
                while (!Disposed && reader.MoveToNextEntry())
                {
                    var e = reader.Entry;
                    if (e.IsDirectory || e.Key is null || !map.TryGetValue(e.Key, out var indices)) continue;

                    int idx = -1;
                    lock (_lock)
                    {
                        foreach (var i in indices)
                        {
                            if (!_available![i])
                            {
                                idx = i;
                                break;
                            }
                        }
                    }
                    if (idx < 0) continue;

                    byte[] data;
                    using (var es = reader.OpenEntryStream())
                        data = ReadAll(es, e.Size);

                    if (_spillDir != null) File.WriteAllBytes(SpillPath(idx), data);

                    lock (_lock)
                    {
                        if (_buffers != null) _buffers[idx] = data;
                        _available![idx] = true;
                        Monitor.PulseAll(_lock);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            lock (_lock) _extractError = ex;
        }
        finally
        {
            lock (_lock)
            {
                _extractDone = true;
                _reader = null;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private string SpillPath(int index) => Path.Combine(_spillDir!, index.ToString("D6"));

    public override void Dispose()
    {
        lock (_lock)
        {
            if (Disposed) return;
            Disposed = true;
            try
            {
                _reader?.Cancel();
            }
            catch (Exception)
            {
            }
            Monitor.PulseAll(_lock);
        }

        var thread = _extractThread;
        if (thread != null && thread.IsAlive) thread.Join(5000);

        lock (_lock)
        {
            try
            {
                _archive.Dispose();
            }
            catch (Exception)
            {
            }
            _stream.Dispose();
            _buffers = null;
        }

        if (_spillDir != null)
        {
            try
            {
                Directory.Delete(_spillDir, true);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>读出按自然排序的第一张图片（封面）。</summary>
    public static byte[] ReadFirstImage(string path, CancellationToken ct)
    {
        using var stream = OpenStream(path);
        using var archive = ArchiveFactory.Open(stream, CreateReaderOptions());
        var pages = ListPages(archive);
        if (pages.Count == 0) throw new InvalidDataException("压缩包中没有图片");
        var first = pages[0].Entry;
        if (first.IsEncrypted) throw new NotSupportedException("压缩包已加密");
        ct.ThrowIfCancellationRequested();

        if (!IsSolidArchive(archive))
        {
            using var s = first.OpenEntryStream();
            return ReadAll(s, first.Size);
        }

        using var reader = archive.ExtractAllEntries();
        while (reader.MoveToNextEntry())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.Entry.Key == first.Key)
            {
                using var s = reader.OpenEntryStream();
                return ReadAll(s, reader.Entry.Size);
            }
        }
        throw new InvalidDataException("压缩包中没有图片");
    }

    private static FileStream OpenStream(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);

    private static bool IsSolidArchive(IArchive archive)
    {
        try
        {
            return archive.IsSolid;
        }
        catch (Exception)
        {
            return archive.Type == ArchiveType.SevenZip;
        }
    }

    /// <summary>压缩包里的图片，按文件名自然排序。文件名的编码用整个包的文件名一起判断。</summary>
    private static List<(IArchiveEntry Entry, string Name)> ListPages(IArchive archive)
    {
        var entries = archive.Entries.ToList();
        var encoding = ArchiveNameDecoder.Detect(entries.Select(e => e.Key));
        var pages = new List<(IArchiveEntry Entry, string Name)>();
        foreach (var e in entries)
        {
            if (e.IsDirectory || e.Key is null) continue;
            var name = NormalizeName(ArchiveNameDecoder.Resolve(e.Key, encoding));
            if (IsPageEntry(name)) pages.Add((e, name));
        }
        pages.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        return pages;
    }

    private static string NormalizeName(string name)
    {
        name = name.Replace('\\', '/');
        try
        {
            // macOS 打包的文件名是分解形式（NFD），韩文会显示成一个个分开的字母
            return name.IsNormalized() ? name : name.Normalize();
        }
        catch (ArgumentException)
        {
            return name;
        }
    }

    private static bool IsPageEntry(string name)
    {
        if (!FileTypes.IsImage(name)) return false;
        if (name.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) || name.Contains("/__MACOSX/", StringComparison.OrdinalIgnoreCase))
            return false;
        int slash = name.LastIndexOf('/');
        return !name.AsSpan(slash + 1).StartsWith("._");
    }

    private static ReaderOptions CreateReaderOptions() => new()
    {
        LeaveStreamOpen = true,
        ArchiveEncoding = new ArchiveEncoding { CustomDecoder = ArchiveNameDecoder.Escape },
    };

    internal static byte[] ReadAll(Stream s, long size)
    {
        if (size > 0 && size < (1L << 30))
        {
            var buf = new byte[size];
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            if (total < buf.Length)
            {
                Array.Resize(ref buf, total);
                return buf;
            }
            int extra = s.ReadByte();
            if (extra < 0) return buf;

            // 记录的大小不准确，把剩余部分也读出来
            using var rest = new MemoryStream();
            rest.Write(buf, 0, total);
            rest.WriteByte((byte)extra);
            s.CopyTo(rest);
            return rest.ToArray();
        }

        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
