using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EzViewer.Core;
using EzViewer.Imaging;
using EzViewer.Reader;
using EzViewer.Sources;

namespace EzViewer.Views;

public partial class ReaderView : UserControl
{
    private const double ZoomStep = 1.25;
    private const double WheelZoomStep = 1.15;
    private const double MinZoom = 0.5;
    private const double MaxZoom = 10;
    private const double BottomRevealZone = 90;
    private const long EdgeCooldownMs = 450;

    private enum TurnSource
    {
        Key,
        KeyRepeat,
        Wheel,
        Button,
    }

    public event Action? BackRequested;
    public event Action? FullscreenToggleRequested;
    public event Action<string>? AddFavoriteRequested;
    public event Action<ComicItem?>? CurrentComicChanged;

    private AppSettings _settings = new();

    // 导航：当前列表（列表模式为某个文件夹里的子文件夹和漫画，自由模式为书库中的漫画）
    private bool _freeMode;
    private List<ComicItem> _items = new();
    private int _index = -1;
    private string? _listFolder;
    private int _planVersion;
    private int _folderNavVersion;

    // 当前漫画
    private ReaderSession? _session;
    private bool _opening;
    private int _openVersion;
    private int _showVersion;
    private int _page;
    private int _pageCount;
    private BitmapSource? _bitmap;
    private AnimationPlayer? _animation;

    // 视图：图片左上角在 Viewport 中的位置 + 缩放（_fitScale 为长边适配比例，_zoom 为其倍数）
    private double _fitScale = 1;
    private double _zoom = 1;
    private double _offsetX;
    private double _offsetY;
    private bool _pannable;
    private readonly MatrixTransform _transform = new();
    private bool _fullscreen;
    private bool _sidebarVisible = true;

    // 输入
    private readonly HashSet<Key> _panKeys = new();
    private bool _panHooked;
    private readonly Stopwatch _panClock = new();
    private TimeSpan _panLast;
    private bool _dragging;
    private Point _dragStart;
    private double _dragOffsetX;
    private double _dragOffsetY;
    private int _wheelAccumulator;
    private long _edgeArrivedAt;
    private Point _lastMouse = new(double.NaN, double.NaN);

    // 控件状态
    private bool _syncingSlider;
    private bool _sliderDragging;
    private double _sliderGrabOffset;
    private bool _syncingList;
    private bool _centerList;
    private int _pendingOpenIndex = -1;
    private int _sliderPendingPage = -1;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _loadingTimer;
    private readonly DispatcherTimer _sliderTimer;
    private readonly DispatcherTimer _openDebounce;
    private readonly DispatcherTimer _barHideTimer;
    private readonly DispatcherTimer _cursorTimer;

    public ReaderView()
    {
        InitializeComponent();
        PageImage.RenderTransform = _transform;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            FadeToast(0);
        };

        _loadingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingTimer.Stop();
            LoadingText.Visibility = Visibility.Visible;
        };

        _sliderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _sliderTimer.Tick += (_, _) =>
        {
            _sliderTimer.Stop();
            if (_sliderPendingPage < 0) return;
            int p = _sliderPendingPage;
            _sliderPendingPage = -1;
            if (p != _page) ShowPage(p);
        };

        _openDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _openDebounce.Tick += (_, _) =>
        {
            _openDebounce.Stop();
            if (_pendingOpenIndex < 0) return;
            int i = _pendingOpenIndex;
            _pendingOpenIndex = -1;
            OpenEntry(i);
        };

        _barHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _barHideTimer.Tick += (_, _) =>
        {
            _barHideTimer.Stop();
            if (_fullscreen && !BottomBar.IsMouseOver && !_sliderDragging) BottomBar.Visibility = Visibility.Collapsed;
        };

        _cursorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _cursorTimer.Tick += (_, _) =>
        {
            _cursorTimer.Stop();
            if (_fullscreen && !_dragging && !BottomBar.IsMouseOver) Viewport.Cursor = Cursors.None;
        };
    }

    public ComicItem? CurrentItem => _index >= 0 && _index < _items.Count ? _items[_index] : null;

    public bool IsFreeMode => _freeMode;

    public void Initialize(AppSettings settings)
    {
        _settings = settings;
        SetSidebarVisible(settings.SidebarVisible, save: false);
    }

    // ───────────── 打开 ─────────────

    /// <summary>列表模式：打开文件夹 / 压缩包 / PDF / 图片。</summary>
    public async Task OpenListAsync(string path)
    {
        var version = ++_planVersion;
        ++_openVersion;
        _opening = true;
        CloseSession();
        SetBitmap(null);
        _pageCount = 0;
        _page = 0;
        UpdatePageUi();
        ComicTitleText.Text = string.Empty;
        HideError();
        HideFolderPanel();
        StartLoadingIndicator();

        ListOpenPlan plan;
        try
        {
            plan = await Task.Run(() => ComicScanner.PlanOpen(path, CancellationToken.None));
        }
        catch (Exception ex)
        {
            if (version != _planVersion) return;
            _opening = false;
            StopLoadingIndicator();
            _freeMode = false;
            SetList(new List<ComicItem>(), null, PathUtil.GetDisplayName(path), path);
            ComicTitleText.Text = string.Empty;
            UpdatePageUi();
            ShowError("无法打开\n" + path + "\n" + ex.Message);
            return;
        }
        if (version != _planVersion) return;

        _opening = false;
        _freeMode = false;
        SetList(plan.Items, plan.ListFolder, PathUtil.GetDisplayName(plan.ListFolder), plan.ListFolder);
        OpenEntry(plan.SelectedIndex, atLastPage: false, plan.StartImagePath);
    }

    /// <summary>自由模式：在书库列表中从第 index 本开始阅读。</summary>
    public void OpenFree(List<ComicItem> items, int index, string title, string rootPath)
    {
        ++_planVersion;
        _freeMode = true;
        SetList(items, null, title, rootPath);
        OpenEntry(index);
    }

    /// <summary>返回书库时调用：释放当前漫画占用的内存和文件句柄。</summary>
    public void Close()
    {
        ++_planVersion;
        ++_openVersion;
        ++_folderNavVersion;
        _opening = false;
        _openDebounce.Stop();
        _pendingOpenIndex = -1;
        CloseSession();
        SetBitmap(null);
        HideError();
        HideFolderPanel();
        StopLoadingIndicator();
        ReleaseInput();
        _items = new List<ComicItem>();
        _index = -1;
        _centerList = false;
        _syncingList = true;
        ComicList.ItemsSource = null;
        _syncingList = false;
        CurrentComicChanged?.Invoke(null);
    }

    private void SetList(List<ComicItem> items, string? listFolder, string title, string path)
    {
        _items = items;
        _listFolder = listFolder;
        _index = -1;
        _centerList = true;
        _syncingList = true;
        ComicList.ItemsSource = _items;
        ComicList.SelectedIndex = -1;
        _syncingList = false;
        ListTitleText.Text = title + "  ·  " + items.Count;
        ListTitleText.ToolTip = title;
        ListPathText.Text = path;
        ListPathText.ToolTip = path;
        FavoriteButton.Visibility = _freeMode ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>打开列表中的一项：漫画直接阅读，文件夹显示预览面板。</summary>
    private void OpenEntry(int index, bool atLastPage = false, string? startImage = null)
    {
        if (index < 0 || index >= _items.Count) return;
        if (_items[index].IsReadable) OpenComic(index, atLastPage, startImage);
        else ShowDirectory(index);
    }

    private async void OpenComic(int index, bool atLastPage, string? startImage = null)
    {
        if (index < 0 || index >= _items.Count) return;
        _openDebounce.Stop();
        _pendingOpenIndex = -1;
        _index = index;
        var item = _items[index];
        var version = ++_openVersion;

        SyncListSelection();
        ComicTitleText.Text = item.Name;
        ComicTitleText.ToolTip = item.Path;
        CurrentComicChanged?.Invoke(item);

        CloseSession();
        _opening = true;
        _pageCount = 0;
        _page = 0;
        UpdatePageUi();
        HideError();
        HideFolderPanel();
        StartLoadingIndicator();
        MarkEdge();

        var hint = CurrentHint();
        ReaderSession? session = null;
        Exception? error = null;
        try
        {
            session = await Task.Run(() => ReaderSession.Open(item, hint));
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (version != _openVersion)
        {
            session?.Dispose();
            return;
        }
        _opening = false;
        StopLoadingIndicator();

        if (session == null)
        {
            SetBitmap(null);
            ShowError("无法打开「" + item.Name + "」\n" + PageLoader.Describe(error!));
            return;
        }
        if (session.PageCount == 0)
        {
            session.Dispose();
            SetBitmap(null);
            ShowError("「" + item.Name + "」里没有可显示的图片");
            return;
        }

        _session = session;
        _pageCount = session.PageCount;
        int page = atLastPage ? _pageCount - 1 : 0;
        if (startImage != null)
        {
            int p = session.Source.IndexOfPage(startImage);
            if (p >= 0) page = p;
        }
        ShowPage(page);
    }

    private void CloseSession()
    {
        ++_showVersion;
        _sliderDragging = false;
        _sliderTimer.Stop();
        _sliderPendingPage = -1;
        if (PageSlider.IsMouseCaptured) PageSlider.ReleaseMouseCapture();
        StopAnimation();
        var s = _session;
        _session = null;
        s?.Dispose();
    }

    /// <summary>选中的是只能进入的文件夹：关闭当前漫画，显示文件夹信息和操作提示。</summary>
    private async void ShowDirectory(int index)
    {
        _openDebounce.Stop();
        _pendingOpenIndex = -1;
        _index = index;
        var entry = _items[index];
        var version = ++_openVersion;
        _opening = false;

        SyncListSelection();
        ComicTitleText.Text = entry.Name;
        ComicTitleText.ToolTip = entry.Path;
        CurrentComicChanged?.Invoke(entry);

        CloseSession();
        SetBitmap(null);
        _pageCount = 0;
        _page = 0;
        UpdatePageUi();
        HideError();
        StopLoadingIndicator();
        MarkEdge();

        FolderNameText.Text = entry.Name;
        FolderInfoText.Text = "正在读取…";
        FolderPanel.Visibility = Visibility.Visible;

        List<ComicItem>? children = null;
        try
        {
            children = await Task.Run(() => ComicScanner.ListEntries(entry.Path));
        }
        catch (Exception)
        {
        }
        if (version != _openVersion) return;
        FolderInfoText.Text = children == null ? "无法读取这个文件夹" : Summarize(children);
    }

    private static string Summarize(List<ComicItem> items)
    {
        int dirs = items.Count(i => i.IsDirectory);
        int comics = items.Count - dirs;
        var parts = new List<string>();
        if (dirs > 0) parts.Add(dirs + " 个文件夹");
        if (comics > 0) parts.Add(comics + " 本漫画");
        return parts.Count == 0 ? "空文件夹" : string.Join(" · ", parts);
    }

    private void HideFolderPanel() => FolderPanel.Visibility = Visibility.Collapsed;

    private async void ShowPage(int page)
    {
        var session = _session;
        if (session == null || _pageCount == 0) return;
        page = Math.Clamp(page, 0, _pageCount - 1);
        _page = page;
        UpdatePageUi();

        var version = ++_showVersion;
        var task = session.Loader.GetPageAsync(page);
        if (!task.IsCompleted) StartLoadingIndicator();
        var result = await task;
        if (version != _showVersion || !ReferenceEquals(session, _session)) return;

        StopLoadingIndicator();
        if (result.Image != null)
        {
            HideError();
            SetBitmap(result.Image, result.Animation);
        }
        else if (result.Error != null)
        {
            SetBitmap(null);
            ShowError("第 " + (page + 1) + " 页无法显示\n" + result.Error);
        }
    }

    private RenderHint CurrentHint()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = Viewport.ActualWidth;
        double h = Viewport.ActualHeight;
        if (w < 100 || h < 100)
        {
            w = SystemParameters.PrimaryScreenWidth;
            h = SystemParameters.PrimaryScreenHeight;
        }
        return new RenderHint((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY));
    }

    // ───────────── 翻页 / 切换漫画 / 切换文件夹 ─────────────

    private void NextPage(TurnSource source)
    {
        if (_opening) return;
        if (_session != null && _page < _pageCount - 1)
        {
            ShowPage(_page + 1);
            if (_page == _pageCount - 1)
            {
                MarkEdge();
                if (FindReadable(_index + 1, 1) >= 0) Toast("已是最后一页，继续翻页进入下一本");
            }
            return;
        }
        if (!CanCrossEdge(source)) return;
        int next = FindReadable(_index + 1, 1);
        if (next >= 0)
        {
            OpenComic(next, atLastPage: false);
            Toast("下一本：" + _items[next].Name);
        }
        else
        {
            MarkEdge();
            Toast("已经是最后一本了");
        }
    }

    private void PrevPage(TurnSource source)
    {
        if (_opening) return;
        if (_session != null && _page > 0)
        {
            ShowPage(_page - 1);
            if (_page == 0)
            {
                MarkEdge();
                if (FindReadable(_index - 1, -1) >= 0) Toast("已是第一页，继续往前翻进入上一本");
            }
            return;
        }
        if (!CanCrossEdge(source)) return;
        int prev = FindReadable(_index - 1, -1);
        if (prev >= 0)
        {
            OpenComic(prev, atLastPage: true);
            Toast("上一本：" + _items[prev].Name);
        }
        else
        {
            MarkEdge();
            Toast("已经是第一本了");
        }
    }

    /// <summary>从 start 起按 step 方向找下一本可阅读的漫画（翻页跨本时跳过文件夹）。</summary>
    private int FindReadable(int start, int step)
    {
        for (int i = start; i >= 0 && i < _items.Count; i += step)
            if (_items[i].IsReadable) return i;
        return -1;
    }

    /// <summary>按住方向键或快速滚动滚轮时，不会一口气越过好几本。</summary>
    private bool CanCrossEdge(TurnSource source) => source switch
    {
        TurnSource.KeyRepeat => false,
        TurnSource.Wheel => Environment.TickCount64 - _edgeArrivedAt > EdgeCooldownMs,
        _ => true,
    };

    private void MarkEdge() => _edgeArrivedAt = Environment.TickCount64;

    private void StepComic(int delta, bool isRepeat)
    {
        if (_items.Count == 0) return;
        int current = _pendingOpenIndex >= 0 ? _pendingOpenIndex : _index;
        int target = current + delta;
        if (target < 0)
        {
            if (!isRepeat) Toast("已经是第一本了");
            return;
        }
        if (target >= _items.Count)
        {
            if (!isRepeat) Toast("已经是最后一本了");
            return;
        }

        if (isRepeat)
        {
            // 按住上下键快速浏览：先只移动列表选中项，松开后再真正打开
            _pendingOpenIndex = target;
            _syncingList = true;
            ComicList.SelectedIndex = target;
            if (_sidebarVisible) ComicList.ScrollIntoView(_items[target]);
            _syncingList = false;
            ComicTitleText.Text = _items[target].Name;
            _openDebounce.Stop();
            _openDebounce.Start();
            return;
        }
        OpenEntry(target);
    }

    /// <summary>
    /// Alt+↑：列出上一级文件夹并选中刚才所在的文件夹。正在读的漫画保持打开，
    /// 自由模式下则切换为该漫画所在文件夹的列表。
    /// </summary>
    private async void ParentFolder()
    {
        string from;
        string? target;
        if (_freeMode || _listFolder == null)
        {
            var current = CurrentItem;
            if (current == null) return;
            from = current.Path;
            target = string.IsNullOrEmpty(current.ParentDirectory) ? null : current.ParentDirectory;
        }
        else
        {
            from = _listFolder;
            target = System.IO.Path.GetDirectoryName(_listFolder);
        }
        if (target == null)
        {
            Toast("已经是根目录了");
            return;
        }

        var version = ++_folderNavVersion;
        List<ComicItem> entries;
        try
        {
            entries = await Task.Run(() => ComicScanner.ListEntries(target));
        }
        catch (Exception ex)
        {
            if (version == _folderNavVersion) Toast("无法打开上一级文件夹：" + ex.Message);
            return;
        }
        if (version != _folderNavVersion) return;

        int select = entries.FindIndex(e => PathUtil.Equal(e.Path, from));
        ShowFolderList(target, entries, select, "上一级：", goingUp: true);
    }

    /// <summary>Alt+↓：进入列表中选中的文件夹。</summary>
    private async void EnterFolder()
    {
        int idx = _pendingOpenIndex >= 0 ? _pendingOpenIndex : _index;
        if (idx < 0 || idx >= _items.Count) return;
        var entry = _items[idx];
        if (entry.IsSelf)
        {
            Toast("已经在「" + entry.Name + "」里了");
            return;
        }
        if (!entry.CanEnter)
        {
            Toast(entry.Kind == ComicKind.Folder ? "「" + entry.Name + "」里没有子文件夹" : "「" + entry.Name + "」不是文件夹");
            return;
        }

        var version = ++_folderNavVersion;
        List<ComicItem> entries;
        try
        {
            entries = await Task.Run(() => ComicScanner.ListEntries(entry.Path));
        }
        catch (Exception ex)
        {
            if (version == _folderNavVersion) Toast("无法进入文件夹：" + ex.Message);
            return;
        }
        if (version != _folderNavVersion) return;
        if (entries.Count == 0)
        {
            Toast("「" + entry.Name + "」里没有漫画");
            return;
        }
        ShowFolderList(entry.Path, entries, -1, "进入：", goingUp: false);
    }

    /// <summary>
    /// 换成 folder 的列表。正在读的漫画就在这个文件夹（或其子文件夹）里时继续阅读，
    /// 只把列表定位到通往它的那一项；否则打开 select 指定的项或第一本漫画。
    /// </summary>
    private void ShowFolderList(string folder, List<ComicItem> entries, int select, string toastPrefix, bool goingUp)
    {
        _openDebounce.Stop();
        _pendingOpenIndex = -1;
        var reading = _session?.Item;
        int onPath = reading == null ? -1 : FindOnPath(entries, reading.Path);

        _freeMode = false;
        var name = PathUtil.GetDisplayName(folder);
        SetList(entries, folder, name, folder);
        Toast(toastPrefix + name);

        if (onPath >= 0)
        {
            _index = onPath;
            SyncListSelection();
            return;
        }
        if (goingUp && reading != null)
        {
            // 从隐藏文件夹往上退时列表里找不到它：继续阅读，列表定位到能找到的那一项
            _index = select;
            SyncListSelection();
            return;
        }
        OpenEntry(select >= 0 ? select : ComicScanner.PreferredIndex(entries));
    }

    private static int FindOnPath(List<ComicItem> entries, string path)
    {
        int exact = entries.FindIndex(e => PathUtil.Equal(e.Path, path));
        if (exact >= 0) return exact;
        return entries.FindIndex(e => !e.IsSelf && (e.Kind is ComicKind.Folder or ComicKind.Directory) && PathUtil.IsSameOrInside(e.Path, path));
    }

    // ───────────── 显示与缩放 ─────────────

    private void SetBitmap(BitmapSource? bmp, AnimatedImage? animation = null)
    {
        StopAnimation();
        _bitmap = bmp;
        PageImage.Source = bmp;
        if (bmp != null)
        {
            PageImage.Width = bmp.PixelWidth;
            PageImage.Height = bmp.PixelHeight;
        }
        ResetView();
        if (animation != null) _animation = new AnimationPlayer(animation, frame => PageImage.Source = frame);
    }

    private void StopAnimation()
    {
        _animation?.Dispose();
        _animation = null;
    }

    /// <summary>长边适配：等比缩放到整页恰好放进窗口。</summary>
    private void ResetView()
    {
        _zoom = 1;
        ComputeFit();
        _offsetX = double.NegativeInfinity;
        _offsetY = double.NegativeInfinity;
        ClampOffsets();
        ApplyTransform();
    }

    private void ComputeFit()
    {
        if (_bitmap == null) return;
        double vw = Viewport.ActualWidth;
        double vh = Viewport.ActualHeight;
        if (vw <= 0 || vh <= 0) return;
        _fitScale = Math.Min(vw / _bitmap.PixelWidth, vh / _bitmap.PixelHeight);
    }

    private void ClampOffsets()
    {
        if (_bitmap == null) return;
        double s = _fitScale * _zoom;
        double w = _bitmap.PixelWidth * s;
        double h = _bitmap.PixelHeight * s;
        double vw = Viewport.ActualWidth;
        double vh = Viewport.ActualHeight;

        _offsetX = w <= vw ? (vw - w) / 2 : Math.Clamp(double.IsNegativeInfinity(_offsetX) ? (vw - w) / 2 : _offsetX, vw - w, 0);
        // 图片比窗口高时从顶部开始看
        _offsetY = h <= vh ? (vh - h) / 2 : Math.Clamp(double.IsNegativeInfinity(_offsetY) ? 0 : _offsetY, vh - h, 0);

        bool pannable = w > vw + 0.5 || h > vh + 0.5;
        if (pannable != _pannable)
        {
            _pannable = pannable;
            if (Viewport.Cursor != Cursors.None) Viewport.Cursor = pannable ? Cursors.SizeAll : null;
        }
    }

    private void ApplyTransform()
    {
        double s = _fitScale * _zoom;
        _transform.Matrix = new Matrix(s, 0, 0, s, _offsetX, _offsetY);
    }

    private void ZoomAtCenter(double factor) =>
        ZoomAt(factor, new Point(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2));

    private void ZoomAt(double factor, Point anchor)
    {
        if (_bitmap == null) return;
        double newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - 1) < 0.02) newZoom = 1;
        if (Math.Abs(newZoom - _zoom) < 1e-6)
        {
            Toast(newZoom >= MaxZoom ? "已放大到最大" : "已缩小到最小");
            return;
        }

        double old = _fitScale * _zoom;
        _zoom = newZoom;
        double s = _fitScale * _zoom;
        _offsetX = anchor.X - (anchor.X - _offsetX) * s / old;
        _offsetY = anchor.Y - (anchor.Y - _offsetY) * s / old;
        ClampOffsets();
        ApplyTransform();
        Toast("缩放 " + Math.Round(_zoom * 100) + "%", 0.9);
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _session?.Loader.UpdateHint(CurrentHint());
        if (_bitmap == null) return;

        // 保持窗口中心对准的图片位置不变
        double oldScale = _fitScale * _zoom;
        double cx = (e.PreviousSize.Width / 2 - _offsetX) / oldScale;
        double cy = (e.PreviousSize.Height / 2 - _offsetY) / oldScale;
        ComputeFit();
        double s = _fitScale * _zoom;
        _offsetX = e.NewSize.Width / 2 - cx * s;
        _offsetY = e.NewSize.Height / 2 - cy * s;
        if (double.IsNaN(_offsetX) || double.IsInfinity(_offsetX)) _offsetX = double.NegativeInfinity;
        if (double.IsNaN(_offsetY) || double.IsInfinity(_offsetY)) _offsetY = double.NegativeInfinity;
        ClampOffsets();
        ApplyTransform();
    }

    // ───────────── 键盘 ─────────────

    public bool HandleKeyDown(Key key, ModifierKeys mods, bool isRepeat)
    {
        bool alt = (mods & ModifierKeys.Alt) != 0;
        bool ctrl = (mods & ModifierKeys.Control) != 0;

        switch (key)
        {
            case Key.Left:
                PrevPage(isRepeat ? TurnSource.KeyRepeat : TurnSource.Key);
                return true;
            case Key.Right:
                NextPage(isRepeat ? TurnSource.KeyRepeat : TurnSource.Key);
                return true;
            case Key.Up:
                if (alt)
                {
                    if (!isRepeat) ParentFolder();
                }
                else
                {
                    StepComic(-1, isRepeat);
                }
                return true;
            case Key.Down:
                if (alt)
                {
                    if (!isRepeat) EnterFolder();
                }
                else
                {
                    StepComic(1, isRepeat);
                }
                return true;
            case Key.Back:
                if (!isRepeat) ParentFolder();
                return true;
            case Key.Enter:
                if (ctrl || alt) return false;
                if (!isRepeat) EnterFolder();
                return true;
            case Key.PageUp:
                ZoomAtCenter(ZoomStep);
                return true;
            case Key.PageDown:
                ZoomAtCenter(1 / ZoomStep);
                return true;
            case Key.Home:
                if (_session != null) ShowPage(0);
                return true;
            case Key.End:
                if (_session != null) ShowPage(_pageCount - 1);
                return true;
            case Key.W:
            case Key.A:
            case Key.S:
            case Key.D:
                if (ctrl || alt) return false;
                _panKeys.Add(key);
                HookPan();
                return true;
            case Key.Tab:
                if (ctrl || alt) return false;
                ToggleSidebar();
                return true;
        }
        return false;
    }

    public void HandleKeyUp(Key key)
    {
        if (_panKeys.Remove(key) && _panKeys.Count == 0) UnhookPan();
    }

    public void ReleaseInput()
    {
        _panKeys.Clear();
        UnhookPan();
        EndDrag();
        EndSliderDrag();
    }

    private void HookPan()
    {
        if (_panHooked) return;
        _panHooked = true;
        _panClock.Restart();
        _panLast = TimeSpan.Zero;
        CompositionTarget.Rendering += OnPanFrame;
    }

    private void UnhookPan()
    {
        if (!_panHooked) return;
        _panHooked = false;
        CompositionTarget.Rendering -= OnPanFrame;
    }

    /// <summary>WASD 按住时每帧平滑移动画面。</summary>
    private void OnPanFrame(object? sender, EventArgs e)
    {
        if (_panKeys.Count == 0)
        {
            UnhookPan();
            return;
        }
        var now = _panClock.Elapsed;
        double dt = Math.Min((now - _panLast).TotalSeconds, 0.05);
        _panLast = now;
        if (dt <= 0 || _bitmap == null || !_pannable) return;

        double speed = Math.Max(Viewport.ActualWidth, Viewport.ActualHeight) * 1.1;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) speed *= 2.5;
        double dx = 0, dy = 0;
        if (_panKeys.Contains(Key.W)) dy += 1;
        if (_panKeys.Contains(Key.S)) dy -= 1;
        if (_panKeys.Contains(Key.A)) dx += 1;
        if (_panKeys.Contains(Key.D)) dx -= 1;
        if (dx == 0 && dy == 0) return;

        _offsetX += dx * speed * dt;
        _offsetY += dy * speed * dt;
        ClampOffsets();
        ApplyTransform();
    }

    // ───────────── 鼠标 ─────────────

    private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        HandleWheel(e.Delta, e.GetPosition(Viewport), (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    internal void HandleWheel(int delta, Point position, bool ctrl)
    {
        if (ctrl)
        {
            ZoomAt(Math.Pow(WheelZoomStep, delta / 120.0), position);
            return;
        }

        if (Math.Sign(delta) != Math.Sign(_wheelAccumulator)) _wheelAccumulator = 0;
        _wheelAccumulator += delta;
        while (_wheelAccumulator >= 120)
        {
            _wheelAccumulator -= 120;
            PrevPage(TurnSource.Wheel);
        }
        while (_wheelAccumulator <= -120)
        {
            _wheelAccumulator += 120;
            NextPage(TurnSource.Wheel);
        }
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            EndDrag();
            FullscreenToggleRequested?.Invoke();
            e.Handled = true;
            return;
        }
        if (_bitmap == null || !_pannable) return;
        _dragging = true;
        _dragStart = e.GetPosition(Viewport);
        _dragOffsetX = _offsetX;
        _dragOffsetY = _offsetY;
        Viewport.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(Viewport);
        _offsetX = _dragOffsetX + (p.X - _dragStart.X);
        _offsetY = _dragOffsetY + (p.Y - _dragStart.Y);
        ClampOffsets();
        ApplyTransform();
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void Viewport_LostMouseCapture(object sender, MouseEventArgs e) => _dragging = false;

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (Viewport.IsMouseCaptured) Viewport.ReleaseMouseCapture();
    }

    private void RootGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullscreen) return;
        var p = e.GetPosition(RootGrid);
        if (p == _lastMouse) return;
        _lastMouse = p;

        if (Viewport.Cursor == Cursors.None) Viewport.Cursor = _pannable ? Cursors.SizeAll : null;
        _cursorTimer.Stop();
        _cursorTimer.Start();

        if (p.Y >= RootGrid.ActualHeight - BottomRevealZone)
        {
            _barHideTimer.Stop();
            BottomBar.Visibility = Visibility.Visible;
        }
        else if (BottomBar.Visibility == Visibility.Visible && !_sliderDragging && !_barHideTimer.IsEnabled)
        {
            _barHideTimer.Start();
        }
    }

    // ───────────── 全屏 / 侧栏 ─────────────

    public void SetFullscreen(bool fullscreen)
    {
        _fullscreen = fullscreen;
        FullscreenButton.Content = fullscreen ? "\uE73F" : "\uE740";
        FullscreenButton.ToolTip = fullscreen ? "退出全屏 (F11 / Esc)" : "全屏 (F11)";
        _barHideTimer.Stop();
        _cursorTimer.Stop();
        if (fullscreen)
        {
            Grid.SetRow(BottomBar, 0);
            BottomBar.VerticalAlignment = VerticalAlignment.Bottom;
            BottomBar.Visibility = Visibility.Collapsed;
            _cursorTimer.Start();
        }
        else
        {
            Grid.SetRow(BottomBar, 1);
            BottomBar.VerticalAlignment = VerticalAlignment.Stretch;
            BottomBar.Visibility = Visibility.Visible;
            Viewport.Cursor = _pannable ? Cursors.SizeAll : null;
        }
    }

    public void ToggleSidebar() => SetSidebarVisible(!_sidebarVisible, save: true);

    private void SetSidebarVisible(bool visible, bool save)
    {
        if (!visible && _sidebarVisible && SidebarColumn.ActualWidth >= 160)
            _settings.SidebarWidth = SidebarColumn.ActualWidth;

        _sidebarVisible = visible;
        if (visible)
        {
            SidebarColumn.MinWidth = 160;
            SidebarColumn.Width = new GridLength(Math.Clamp(_settings.SidebarWidth, 160, 640));
            Sidebar.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            Sidebar.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
        }
        SidebarToggle.IsChecked = visible;

        if (save)
        {
            _settings.SidebarVisible = visible;
            _settings.Save();
        }
        if (visible)
        {
            _centerList = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(SyncListSelection));
        }
    }

    private void SyncListSelection()
    {
        if (_index < 0 || _index >= _items.Count) return;
        _syncingList = true;
        ComicList.SelectedIndex = _index;
        if (_sidebarVisible)
        {
            if (_centerList)
            {
                _centerList = false;
                ScrollListToCenter(_index);
            }
            else
            {
                ComicList.ScrollIntoView(_items[_index]);
            }
        }
        _syncingList = false;
    }

    private void ScrollListToCenter(int index, bool defer = true)
    {
        if (index < 0 || index >= _items.Count) return;
        ComicList.UpdateLayout();
        if (AutoScroll.FindScrollViewer(ComicList) is not { ViewportHeight: > 0 } sv)
        {
            if (defer)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
                {
                    if (_sidebarVisible && _index >= 0 && _index < _items.Count) ScrollListToCenter(_index, defer: false);
                }));
            }
            return;
        }
        ComicList.ScrollIntoView(_items[index]);
        ComicList.UpdateLayout();
        if (ComicList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            double top = container.TranslatePoint(new Point(0, 0), sv).Y;
            sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + top - (sv.ViewportHeight - container.ActualHeight) / 2));
        }
    }

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (SidebarColumn.ActualWidth < 160) return;
        _settings.SidebarWidth = SidebarColumn.ActualWidth;
        _settings.Save();
    }

    // ───────────── 进度条 / 提示 ─────────────

    private void UpdatePageUi()
    {
        PageText.Text = _pageCount > 0 ? (_page + 1) + " / " + _pageCount : "- / -";
        if (_session != null && _pageCount > 0) PageText.ToolTip = _session.Source.GetPageName(_page);
        else PageText.ToolTip = null;

        if (_sliderDragging) return;
        _syncingSlider = true;
        // 没有页面时最大值取 2，进度条显示为空而不是满格
        PageSlider.Maximum = _pageCount == 0 ? 2 : _pageCount;
        PageSlider.Value = _page + 1;
        PageSlider.IsEnabled = _pageCount > 1;
        _syncingSlider = false;
    }

    private int SliderPage() => Math.Clamp((int)Math.Round(PageSlider.Value) - 1, 0, Math.Max(0, _pageCount - 1));

    private void PageSlider_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_session == null || _pageCount <= 1) return;
        e.Handled = true;
        double x = e.GetPosition(PageSlider).X;
        double usable = Math.Max(1, PageSlider.ActualWidth - 16);
        double range = PageSlider.Maximum - PageSlider.Minimum;
        double thumbCenter = 8 + (range > 0 ? (PageSlider.Value - PageSlider.Minimum) / range * usable : 0);
        if (Math.Abs(x - thumbCenter) <= 8)
        {
            _sliderGrabOffset = x - thumbCenter;
        }
        else
        {
            _sliderGrabOffset = 0;
            SetSliderValueFromMouse(x);
        }
        _sliderDragging = true;
        PageSlider.CaptureMouse();
    }

    private void PageSlider_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_sliderDragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndSliderDrag();
            return;
        }
        SetSliderValueFromMouse(e.GetPosition(PageSlider).X);
    }

    private void PageSlider_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndSliderDrag();

    private void PageSlider_LostMouseCapture(object sender, MouseEventArgs e) => EndSliderDrag();

    private void SetSliderValueFromMouse(double x)
    {
        double usable = PageSlider.ActualWidth - 16;
        if (usable <= 0) return;
        double ratio = Math.Clamp((x - _sliderGrabOffset - 8) / usable, 0, 1);
        double value = Math.Round(PageSlider.Minimum + ratio * (PageSlider.Maximum - PageSlider.Minimum));
        if (value != PageSlider.Value) PageSlider.Value = value;
    }

    private void EndSliderDrag()
    {
        if (!_sliderDragging) return;
        _sliderDragging = false;
        _sliderTimer.Stop();
        _sliderPendingPage = -1;
        if (PageSlider.IsMouseCaptured) PageSlider.ReleaseMouseCapture();
        if (_session != null && _pageCount > 0)
        {
            int page = SliderPage();
            if (page != _page) ShowPage(page);
            else UpdatePageUi();
        }
        if (_fullscreen && !BottomBar.IsMouseOver && !_barHideTimer.IsEnabled)
            _barHideTimer.Start();
    }

    private void PageSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider || _session == null) return;
        int page = SliderPage();
        PageText.Text = (page + 1) + " / " + _pageCount;
        if (_sliderDragging)
        {
            _sliderPendingPage = page;
            if (!_sliderTimer.IsEnabled) _sliderTimer.Start();
        }
        else if (page != _page)
        {
            ShowPage(page);
        }
    }

    public void Toast(string text, double seconds = 1.4)
    {
        ToastText.Text = text;
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(seconds);
        _toastTimer.Start();
        FadeToast(1);
    }

    private void FadeToast(double to) =>
        ToastBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 120 : 260)));

    private void StartLoadingIndicator()
    {
        if (LoadingText.Visibility == Visibility.Visible || _loadingTimer.IsEnabled) return;
        _loadingTimer.Start();
    }

    private void StopLoadingIndicator()
    {
        _loadingTimer.Stop();
        LoadingText.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    // ───────────── 按钮 ─────────────

    private void ComicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList) return;
        int i = ComicList.SelectedIndex;
        if (i >= 0 && i != _index) OpenEntry(i);
    }

    private void ComicList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement(ComicList, e.OriginalSource as DependencyObject) is not ListBoxItem container) return;
        if (container.DataContext is not ComicItem item || !item.CanEnter) return;
        int i = _items.IndexOf(item);
        if (i < 0) return;
        if (i != _index) OpenEntry(i);
        EnterFolder();
        e.Handled = true;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => FullscreenToggleRequested?.Invoke();

    private void ParentFolder_Click(object sender, RoutedEventArgs e) => ParentFolder();

    private void EnterFolder_Click(object sender, RoutedEventArgs e) => EnterFolder();

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (_listFolder != null) AddFavoriteRequested?.Invoke(_listFolder);
    }

#if DEBUG
    internal string DebugState =>
        $"mode={(_freeMode ? "free" : "list")} folder={_listFolder} index={_index}/{_items.Count} entry={CurrentItem?.Kind} selected={CurrentItem?.Name} " +
        $"reading={_session?.Item.Name ?? "-"} folderPanel={FolderPanel.Visibility} listScroll={ListScrollOffset():0} " +
        $"page={_page + 1}/{_pageCount} pageName={(_session != null && _pageCount > 0 ? _session.Source.GetPageName(_page) : "-")} " +
        $"zoom={_zoom:0.###} fit={_fitScale:0.####} offset=({_offsetX:0.#},{_offsetY:0.#}) pannable={_pannable} " +
        $"bitmap={(_bitmap == null ? "-" : _bitmap.PixelWidth + "x" + _bitmap.PixelHeight)} anim={_animation?.DebugState ?? "-"} " +
        $"viewport={Viewport.ActualWidth:0}x{Viewport.ActualHeight:0} " +
        $"fullscreen={_fullscreen} sidebar={_sidebarVisible} bar={BottomBar.Visibility} " +
        $"error={(ErrorText.Visibility == Visibility.Visible ? ErrorText.Text.Replace('\n', '|') : "-")} toast={ToastText.Text}";

    internal string DebugListNames => string.Join(",", _items.Select(i => i.DisplayName + (i.IsDirectory ? "/" : "")));

    internal ScrollViewer? DebugListScroll => AutoScroll.FindScrollViewer(ComicList);

    private double ListScrollOffset() => DebugListScroll?.VerticalOffset ?? -1;
#endif
}
