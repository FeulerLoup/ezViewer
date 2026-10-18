using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using EzViewer.Core;
using EzViewer.Imaging;
using EzViewer.Sources;

namespace EzViewer.Library;

/// <summary>
/// 封面缩略图：内存 LRU + 磁盘缓存（%LocalAppData%\ezViewer\thumbs）。
/// 请求按“后进先出”处理，快速滚动时优先加载当前可见的封面。
/// </summary>
public sealed class ThumbnailService
{
    public const int StoreWidth = 300;
    public const int StoreHeight = 420;
    private const int MaxQueue = 400;
    private const int MemoryCapacity = 400;
    private const long CacheLimitBytes = 1024L * 1024 * 1024;
    private const long CacheTrimTargetBytes = 800L * 1024 * 1024;
    private const string CacheVersion = "v1";

    public static ThumbnailService Instance { get; } = new();

    private readonly object _gate = new();
    private readonly LinkedList<ComicTile> _queue = new();
    private readonly LinkedList<ComicTile> _lru = new();
    private readonly string _cacheDir;
    private Dispatcher? _dispatcher;
    private volatile int _displayHeight = 252;
    private bool _started;

    private ThumbnailService()
    {
        _cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ezViewer", "thumbs");
    }

    public string CacheDirectory => _cacheDir;

    /// <summary>封面在屏幕上的像素高度（随 DPI 变化），内存中的缩略图按它解码。</summary>
    public int DisplayHeight
    {
        get => _displayHeight;
        set => _displayHeight = Math.Clamp(value, 64, StoreHeight * 2);
    }

    public void Start(Dispatcher dispatcher, int workerCount = 3)
    {
        if (_started) return;
        _started = true;
        _dispatcher = dispatcher;
        for (int i = 0; i < workerCount; i++)
        {
            new Thread(Worker)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "ezViewer thumbnail " + i,
            }.Start();
        }
        ThreadPool.QueueUserWorkItem(_ => TrimCache());
    }

    internal void OnCoverAccessed(ComicTile tile)
    {
        if (!_started) return;
        lock (_gate)
        {
            if (tile.CoverValue != null)
            {
                if (tile.LruNode != null)
                {
                    _lru.Remove(tile.LruNode);
                    _lru.AddFirst(tile.LruNode);
                }
                return;
            }
            if (tile.CoverFailed || tile.Loading) return;

            if (tile.QueueNode != null)
            {
                _queue.Remove(tile.QueueNode);
                _queue.AddFirst(tile.QueueNode);
            }
            else
            {
                tile.QueueNode = _queue.AddFirst(tile);
                if (_queue.Count > MaxQueue)
                {
                    var last = _queue.Last!;
                    _queue.RemoveLast();
                    last.Value.QueueNode = null;
                }
            }
            Monitor.Pulse(_gate);
        }
    }

    /// <summary>切换书库目录时丢弃还没开始的请求。</summary>
    public void CancelPending()
    {
        lock (_gate)
        {
            foreach (var tile in _queue) tile.QueueNode = null;
            _queue.Clear();
        }
    }

    internal void Remove(ComicTile tile)
    {
        lock (_gate)
        {
            if (tile.QueueNode != null)
            {
                _queue.Remove(tile.QueueNode);
                tile.QueueNode = null;
            }
            if (tile.LruNode != null)
            {
                _lru.Remove(tile.LruNode);
                tile.LruNode = null;
            }
        }
        tile.Cover = null;
    }

    private void Worker()
    {
        while (true)
        {
            ComicTile tile;
            lock (_gate)
            {
                while (_queue.Count == 0) Monitor.Wait(_gate);
                tile = _queue.First!.Value;
                _queue.RemoveFirst();
                tile.QueueNode = null;
                tile.Loading = true;
            }

            ImageSource? cover = null;
            try
            {
                cover = Load(tile.Item);
            }
            catch (Exception)
            {
            }

            _dispatcher!.BeginInvoke(DispatcherPriority.Background, new Action(() => Complete(tile, cover)));
        }
    }

    private void Complete(ComicTile tile, ImageSource? cover)
    {
        List<ComicTile>? victims = null;
        lock (_gate)
        {
            tile.Loading = false;
            if (cover != null)
            {
                tile.LruNode ??= _lru.AddFirst(tile);
                while (_lru.Count > MemoryCapacity)
                {
                    var victim = _lru.Last!.Value;
                    _lru.RemoveLast();
                    victim.LruNode = null;
                    (victims ??= new List<ComicTile>()).Add(victim);
                }
            }
        }

        if (cover == null) tile.CoverFailed = true;
        else tile.Cover = cover;

        if (victims != null)
            foreach (var v in victims) v.Cover = null;
    }

    private ImageSource Load(ComicItem item)
    {
        var path = CachePath(item);
        if (path != null && File.Exists(path))
        {
            try
            {
                return ImageDecoder.DecodeCached(File.ReadAllBytes(path), _displayHeight);
            }
            catch (Exception)
            {
                TryDelete(path);
            }
        }

        var thumb = CoverExtractor.Extract(item, StoreWidth, StoreHeight, CancellationToken.None);
        var jpeg = ImageDecoder.EncodeJpeg(thumb, 85);
        if (path != null) TryWrite(path, jpeg);
        return ImageDecoder.DecodeCached(jpeg, _displayHeight);
    }

    private string? CachePath(ComicItem item)
    {
        try
        {
            long ticks;
            long length = 0;
            if (item.Kind == ComicKind.Folder)
            {
                ticks = Directory.GetLastWriteTimeUtc(item.Path).Ticks;
            }
            else
            {
                var fi = new FileInfo(item.Path);
                if (!fi.Exists) return null;
                ticks = fi.LastWriteTimeUtc.Ticks;
                length = fi.Length;
            }
            var raw = item.Path.ToUpperInvariant() + "|" + ticks + "|" + length + "|" + StoreWidth + "x" + StoreHeight + "|" + CacheVersion;
            var hex = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
            return Path.Combine(_cacheDir, hex[..2], hex + ".jpg");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void TryWrite(string path, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    private void TrimCache()
    {
        try
        {
            if (!Directory.Exists(_cacheDir)) return;
            var files = new DirectoryInfo(_cacheDir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            long total = files.Sum(f => f.Length);
            if (total <= CacheLimitBytes) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= CacheTrimTargetBytes) break;
                total -= f.Length;
                TryDelete(f.FullName);
            }
        }
        catch (Exception)
        {
        }
    }
}
