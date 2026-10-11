using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EzViewer.Imaging;

namespace EzViewer.Views;

/// <summary>
/// 播放阅读界面上的动图：后台线程按顺序逐帧解码，界面线程按每帧的时长把画面写进同一个 WriteableBitmap。
/// 所有帧加起来不超过 CacheLimit 时，第一遍解出的帧都留着，之后循环直接用；否则边解边播，只占几帧的内存。
/// </summary>
internal sealed class AnimationPlayer : IDisposable
{
    private const long CacheLimit = 128L * 1024 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MinimizedInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxLag = TimeSpan.FromMilliseconds(100);

    private readonly AnimatedImage _image;
    private readonly Action<BitmapSource> _attach;
    private readonly WriteableBitmap _bitmap;
    private readonly int _stride;
    private readonly byte[]?[]? _cache;
    private readonly BlockingCollection<(int Index, byte[] Pixels)> _queue = new(2);
    private readonly ConcurrentBag<byte[]> _spare = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _current;
    private int _plays;
    private TimeSpan _due;
    private bool _attached;
    private bool _disposed;

    /// <param name="attach">第一次换帧时调用，把显示的图片换成播放用的 WriteableBitmap（此前显示的是 FirstFrame）。</param>
    public AnimationPlayer(AnimatedImage image, Action<BitmapSource> attach)
    {
        _image = image;
        _attach = attach;
        _stride = image.Width * 4;
        _bitmap = new WriteableBitmap(image.Width, image.Height, 96, 96, PixelFormats.Pbgra32, null);
        if ((long)_stride * image.Height * image.FrameCount <= CacheLimit)
        {
            _cache = new byte[]?[image.FrameCount];
            var first = new byte[_stride * image.Height];
            image.FirstFrame.CopyPixels(first, _stride, 0);
            _cache[0] = first;
        }

        new Thread(DecodeLoop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "ezViewer animation" }.Start();

        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnTick;
        _due = TimeSpan.FromMilliseconds(image.Delays[0]);
        Schedule();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _cts.Cancel();
    }

    private void DecodeLoop()
    {
        var token = _cts.Token;
        try
        {
            var decoder = _image.CreateDecoder();
            token.ThrowIfCancellationRequested();
            int count = _image.FrameCount;
            int size = _stride * _image.Height;
            // 第一帧已经显示了，这里只为后面的帧建立合成状态
            var first = new byte[size];
            decoder.Render(0, first);
            if (_cache == null) _spare.Add(first);
            for (int i = 1; !token.IsCancellationRequested; i = (i + 1) % count)
            {
                var pixels = _cache == null && _spare.TryTake(out var reused) ? reused : new byte[size];
                decoder.Render(i, pixels);
                _queue.Add((i, pixels), token);
                if (_cache != null && i == count - 1) break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // 后面的帧解不出来就停在已经显示的那一帧
        }
        finally
        {
            _queue.CompleteAdding();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (Application.Current?.MainWindow is { WindowState: WindowState.Minimized })
        {
            // 最小化时不换帧；边解边播时解码线程也会因为队列满了停下来
            _timer.Interval = MinimizedInterval;
            return;
        }

        int next = (_current + 1) % _image.FrameCount;
        if (next == 0 && _image.LoopCount > 0 && _plays + 1 >= _image.LoopCount)
        {
            // 播完规定的遍数，停在最后一帧
            _timer.Stop();
            return;
        }

        var pixels = _cache?[next];
        if (pixels == null)
        {
            if (!_queue.TryTake(out var frame))
            {
                if (_queue.IsCompleted) _timer.Stop();
                else _timer.Interval = PollInterval;
                return;
            }
            next = frame.Index;
            pixels = frame.Pixels;
            if (_cache != null) _cache[next] = pixels;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, _image.Width, _image.Height), pixels, _stride, 0);
        if (_cache == null) _spare.Add(pixels);
        if (!_attached)
        {
            _attached = true;
            _attach(_bitmap);
        }
        if (next == 0) _plays++;
        _current = next;

        // 解码跟不上或者界面卡住过：从现在重新计时，不要连着快进好几帧
        var now = _clock.Elapsed;
        if (now - _due > MaxLag) _due = now;
        _due += TimeSpan.FromMilliseconds(_image.Delays[next]);
        Schedule();
    }

    private void Schedule()
    {
        var wait = _due - _clock.Elapsed;
        _timer.Interval = wait > TimeSpan.FromMilliseconds(1) ? wait : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

#if DEBUG
    internal string DebugState => $"{_current + 1}/{_image.FrameCount}{(_cache != null ? " cached" : " streaming")}{(_timer.IsEnabled ? "" : " stopped")}";
#endif
}
