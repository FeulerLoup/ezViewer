using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace EzViewer.Views;

/// <summary>
/// 鼠标中键自动滚动：按下中键后上下移动鼠标，离按下位置越远滚得越快。
/// 按住中键拖动时松开即停；只单击一下中键则持续滚动，直到再次点击、滚动滚轮或按 Esc。
/// 可附加在 ScrollViewer 上，也可附加在 ListBox 等内部带 ScrollViewer 的控件上。
/// </summary>
public static class AutoScroll
{
    private const double DeadZone = 10;
    private const double ClickSlop = 6;
    private const long HoldThresholdMs = 300;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static Session? _active;

#if DEBUG
    internal static Func<Point>? DebugPointer;
#endif

    public static bool IsActive => _active != null;

    /// <summary>正在自动滚动时停止并返回 true。</summary>
    public static bool Cancel()
    {
        if (_active == null) return false;
        Stop();
        return true;
    }

    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    internal static bool Start(ScrollViewer viewer, Point anchor)
    {
        if (_active != null || !viewer.CaptureMouse()) return false;
        _active = new Session(viewer, anchor);
        _active.Begin();
        return true;
    }

    private static void Stop()
    {
        var s = _active;
        _active = null;
        s?.End();
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.PreviewMouseDown -= OnPreviewMouseDown;
        if ((bool)e.NewValue) element.PreviewMouseDown += OnPreviewMouseDown;
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _active != null) return;
        var viewer = sender as ScrollViewer ?? FindScrollViewer((DependencyObject)sender);
        if (viewer == null || viewer.ScrollableHeight <= 0) return;
        if (Start(viewer, e.GetPosition(viewer))) e.Handled = true;
    }

    private sealed class Session
    {
        private readonly ScrollViewer _viewer;
        private readonly Point _anchor;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long _startedAt = Environment.TickCount64;
        private AdornerLayer? _layer;
        private AnchorAdorner? _adorner;
        private TimeSpan _last;
        private double _target;
        private bool _sticky;
        private bool _moved;

        public Session(ScrollViewer viewer, Point anchor)
        {
            _viewer = viewer;
            _anchor = anchor;
            _target = viewer.VerticalOffset;
        }

        public void Begin()
        {
            _layer = AdornerLayer.GetAdornerLayer(_viewer);
            if (_layer != null)
            {
                _adorner = new AnchorAdorner(_viewer, _anchor);
                _layer.Add(_adorner);
            }
            Mouse.OverrideCursor = Cursors.ScrollNS;
            _viewer.PreviewMouseMove += OnMove;
            _viewer.PreviewMouseUp += OnUp;
            _viewer.PreviewMouseDown += OnDown;
            _viewer.PreviewMouseWheel += OnWheel;
            _viewer.LostMouseCapture += OnLostCapture;
            CompositionTarget.Rendering += OnFrame;
        }

        public void End()
        {
            CompositionTarget.Rendering -= OnFrame;
            _viewer.PreviewMouseMove -= OnMove;
            _viewer.PreviewMouseUp -= OnUp;
            _viewer.PreviewMouseDown -= OnDown;
            _viewer.PreviewMouseWheel -= OnWheel;
            _viewer.LostMouseCapture -= OnLostCapture;
            if (_adorner != null) _layer?.Remove(_adorner);
            Mouse.OverrideCursor = null;
            if (_viewer.IsMouseCaptured) _viewer.ReleaseMouseCapture();
        }

        private Point Pointer()
        {
#if DEBUG
            if (DebugPointer != null) return DebugPointer();
#endif
            return Mouse.GetPosition(_viewer);
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            var now = _clock.Elapsed;
            double dt = Math.Min((now - _last).TotalSeconds, 0.05);
            _last = now;

            double dy = Pointer().Y - _anchor.Y;
            double distance = Math.Abs(dy) - DeadZone;
            Mouse.OverrideCursor = distance <= 0 ? Cursors.ScrollNS : dy < 0 ? Cursors.ScrollN : Cursors.ScrollS;
            if (distance <= 0 || dt <= 0) return;

            // 离锚点越远越快：100px 约 1300px/s，300px 约 6800px/s
            double speed = distance * 8 + distance * distance * 0.05;
            _target = Math.Clamp(_target + Math.Sign(dy) * speed * dt, 0, _viewer.ScrollableHeight);
            _viewer.ScrollToVerticalOffset(_target);
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_moved && (e.GetPosition(_viewer) - _anchor).Length > ClickSlop) _moved = true;
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ChangedButton != MouseButton.Middle || _sticky) return;
            if (_moved || Environment.TickCount64 - _startedAt > HoldThresholdMs) Stop();
            else _sticky = true;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            Stop();
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            e.Handled = true;
            Stop();
        }

        private void OnLostCapture(object sender, MouseEventArgs e) => Stop();
    }

    private sealed class AnchorAdorner : Adorner
    {
        private static readonly Brush Fill = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x26, 0x26, 0x2B)));
        private static readonly Pen Ring = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)), 1.2));
        private static readonly Geometry Arrows = Frozen(Geometry.Parse("M 0,-12 L 5,-6 L -5,-6 Z M 0,12 L 5,6 L -5,6 Z"));
        private readonly Point _center;

        public AnchorAdorner(UIElement adorned, Point center) : base(adorned)
        {
            _center = center;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawEllipse(Fill, Ring, _center, 17, 17);
            dc.PushTransform(new TranslateTransform(_center.X, _center.Y));
            dc.DrawGeometry(Brushes.White, null, Arrows);
            dc.DrawEllipse(Brushes.White, null, new Point(0, 0), 2.2, 2.2);
            dc.Pop();
        }

        private static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }
    }
}
