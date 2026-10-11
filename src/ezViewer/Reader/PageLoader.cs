using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using EzViewer.Core;
using EzViewer.Imaging;
using EzViewer.Sources;

namespace EzViewer.Reader;

public sealed class PageResult
{
    public PageResult(int index, DecodedPage? page, string? error)
    {
        Index = index;
        Image = page?.Image;
        Animation = page?.Animation;
        Error = error;
    }

    public int Index { get; }

    public BitmapSource? Image { get; }

    /// <summary>动图时不为 null，Image 是它的第一帧。</summary>
    public AnimatedImage? Animation { get; }

    /// <summary>Image 与 Error 都为 null 表示请求被取消（翻页太快，已不需要这一页）。</summary>
    public string? Error { get; }
}

/// <summary>
/// 后台加载页面：始终优先当前页，然后是翻页方向上的后几页和反方向的一页；
/// 离当前页太远的缓存会被丢弃。
/// </summary>
public sealed class PageLoader : IDisposable
{
    private const int Ahead = 3;
    private const int Behind = 1;
    private const int KeepAhead = 4;
    private const int KeepBehind = 2;

    private readonly ComicSource _source;
    private readonly object _gate = new();
    private readonly Dictionary<int, PageResult> _cache = new();
    private readonly Dictionary<int, TaskCompletionSource<PageResult>> _waiters = new();
    private readonly List<int> _queue = new();
    private readonly HashSet<int> _inFlight = new();
    private readonly Thread[] _workers;
    private readonly CancellationTokenSource _cts = new();
    private RenderHint _hint;
    private int _center;
    private int _direction = 1;
    private bool _disposed;

    public PageLoader(ComicSource source, int workerCount, RenderHint hint)
    {
        _source = source;
        _hint = hint;
        _workers = new Thread[Math.Max(1, workerCount)];
        for (int i = 0; i < _workers.Length; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = "ezViewer page loader " + i };
            _workers[i].Start();
        }
    }

    public ComicSource Source => _source;

    public void UpdateHint(RenderHint hint)
    {
        lock (_gate) _hint = hint;
    }

    public Task<PageResult> GetPageAsync(int index)
    {
        lock (_gate)
        {
            if (_disposed) return Task.FromResult(new PageResult(index, null, null));

            if (index != _center) _direction = index > _center ? 1 : -1;
            _center = index;
            Evict();

            Task<PageResult> task;
            if (_cache.TryGetValue(index, out var cached))
            {
                task = Task.FromResult(cached);
            }
            else
            {
                if (!_waiters.TryGetValue(index, out var tcs))
                {
                    tcs = new TaskCompletionSource<PageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[index] = tcs;
                }
                task = tcs.Task;
            }

            RebuildQueue();
            Monitor.PulseAll(_gate);
            return task;
        }
    }

    private void RebuildQueue()
    {
        _queue.Clear();
        int count = _source.PageCount;

        void Add(int i)
        {
            if (i >= 0 && i < count && !_cache.ContainsKey(i) && !_inFlight.Contains(i) && !_queue.Contains(i))
                _queue.Add(i);
        }

        Add(_center);
        for (int k = 1; k <= Ahead; k++) Add(_center + _direction * k);
        for (int k = 1; k <= Behind; k++) Add(_center - _direction * k);

        foreach (var key in _waiters.Keys.ToList())
        {
            if (_queue.Contains(key) || _inFlight.Contains(key)) continue;
            _waiters.Remove(key, out var stale);
            stale!.TrySetResult(new PageResult(key, null, null));
        }
    }

    private bool InKeepWindow(int index)
    {
        int ahead = _direction > 0 ? KeepAhead : KeepBehind;
        int behind = _direction > 0 ? KeepBehind : KeepAhead;
        return index >= _center - behind && index <= _center + ahead;
    }

    private void Evict()
    {
        foreach (var key in _cache.Keys.Where(k => !InKeepWindow(k)).ToList())
            _cache.Remove(key);
    }

    private void WorkerLoop()
    {
        while (true)
        {
            int index;
            RenderHint hint;
            lock (_gate)
            {
                while (!_disposed && _queue.Count == 0) Monitor.Wait(_gate);
                if (_disposed) return;
                index = _queue[0];
                _queue.RemoveAt(0);
                _inFlight.Add(index);
                hint = _hint;
            }

            PageResult result;
            try
            {
                result = new PageResult(index, _source.LoadPage(index, hint, _cts.Token), null);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                result = new PageResult(index, null, null);
            }
            catch (Exception ex)
            {
                result = new PageResult(index, null, Describe(ex));
            }

            TaskCompletionSource<PageResult>? waiter;
            lock (_gate)
            {
                _inFlight.Remove(index);
                if (!_disposed && (result.Image != null || result.Error != null) && InKeepWindow(index))
                    _cache[index] = result;
                _waiters.Remove(index, out waiter);
            }
            waiter?.TrySetResult(result);
        }
    }

    /// <summary>立即返回；工作线程退出后在后台释放数据源。</summary>
    public void Dispose()
    {
        List<TaskCompletionSource<PageResult>> waiters;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            waiters = _waiters.Values.ToList();
            _waiters.Clear();
            _cache.Clear();
            _queue.Clear();
            Monitor.PulseAll(_gate);
        }
        foreach (var w in waiters) w.TrySetResult(new PageResult(-1, null, null));

        Task.Run(() =>
        {
            foreach (var t in _workers) t.Join(15000);
            try
            {
                _source.Dispose();
            }
            catch (Exception)
            {
            }
            _cts.Dispose();
        });
    }

    public static string Describe(Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => "文件不存在或已被移动",
        UnauthorizedAccessException => "没有访问权限",
        NotSupportedException when ex.Message.Contains("imaging component", StringComparison.OrdinalIgnoreCase)
            => "不支持的图片格式（WebP/AVIF/HEIC 等需要系统安装对应的图像扩展）",
        NotSupportedException => ex.Message,
        System.IO.FileFormatException => "图片已损坏或格式不正确",
        SharpCompress.Common.CryptographicException => "压缩包已加密，暂不支持带密码的压缩包",
        OutOfMemoryException => "内存不足",
        _ => ex.Message,
    };
}

/// <summary>一本已打开的漫画：数据源 + 页面加载器。</summary>
public sealed class ReaderSession : IDisposable
{
    private ReaderSession(ComicItem item, ComicSource source, PageLoader loader)
    {
        Item = item;
        Source = source;
        Loader = loader;
    }

    public ComicItem Item { get; }

    public ComicSource Source { get; }

    public PageLoader Loader { get; }

    public int PageCount => Source.PageCount;

    public static ReaderSession Open(ComicItem item, RenderHint hint)
    {
        var source = ComicSource.Open(item);
        try
        {
            int workers = item.Kind == ComicKind.Pdf ? 1 : 2;
            return new ReaderSession(item, source, new PageLoader(source, workers, hint));
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public void Dispose() => Loader.Dispose();
}
