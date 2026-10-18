using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EzViewer.Core;
using EzViewer.Library;
using Microsoft.Win32;

namespace EzViewer.Views;

public sealed class FavoriteEntry
{
    public FavoriteEntry(string path)
    {
        Path = path;
        Name = PathUtil.GetDisplayName(path);
    }

    public string Path { get; }

    public string Name { get; }
}

/// <summary>书库（自由模式）：收藏目录 + 递归扫描出的所有漫画封面。</summary>
public partial class LibraryView : UserControl
{
    public const double TileOuterWidth = 204;
    public const double RowHeight = 340;
    private const double CoverHeight = 252;

    public event Action? OpenFolderRequested;
    public event Action? OpenFileRequested;
    public event Action? HelpRequested;
    public event Action<string>? OpenListRequested;
    public event Action<List<ComicItem>, int, string, string>? OpenComicRequested;

    private AppSettings _settings = new();
    private readonly ObservableCollection<FavoriteEntry> _favorites = new();
    private readonly BulkObservableCollection<TileRow> _rows = new();
    private readonly Dictionary<string, List<ComicItem>> _scanCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _drainTimer;
    private readonly DispatcherTimer _filterTimer;
    private ConcurrentQueue<ComicItem> _incoming = new();
    private List<ComicTile> _scanned = new();
    private List<ComicTile> _visible = new();
    private CancellationTokenSource? _scanCts;
    private bool _scanning;
    private string? _root;
    private int _columns = 1;
    private string[] _terms = Array.Empty<string>();
    private ScrollViewer? _scroll;
    private double? _restoreOffset;
    private bool _suppressOrderEvent;

    public LibraryView()
    {
        InitializeComponent();
        FavoritesList.ItemsSource = _favorites;
        TileGrid.ItemsSource = _rows;

        _drainTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _drainTimer.Tick += (_, _) => DrainIncoming(final: false);
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            _restoreOffset = null;
            ApplyFilter();
            ScrollToRow(0);
        };

        Loaded += (_, _) =>
        {
            UpdateThumbnailDpi(VisualTreeHelper.GetDpi(this));
            TileGrid.ApplyTemplate();
            _scroll = TileGrid.Template.FindName("PART_Scroll", TileGrid) as ScrollViewer;
            UpdateColumns();
        };
    }

    public void Initialize(AppSettings settings)
    {
        _settings = settings;
        foreach (var f in settings.Favorites.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase))
            _favorites.Add(new FavoriteEntry(f));

        _suppressOrderEvent = true;
        (settings.RandomOrder ? RandomRadio : SequentialRadio).IsChecked = true;
        _suppressOrderEvent = false;

        UpdateFavoritesHint();
        UpdateToolbar();

        var selected = _favorites.FirstOrDefault(f => PathUtil.Equal(f.Path, settings.SelectedFavorite));
        if (selected != null) FavoritesList.SelectedItem = selected;
        else ShowWelcome();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateThumbnailDpi(newDpi);
    }

    private static void UpdateThumbnailDpi(DpiScale dpi) =>
        ThumbnailService.Instance.DisplayHeight = (int)Math.Ceiling(CoverHeight * dpi.DpiScaleY);

    // ───────────── 收藏夹 ─────────────

    public void AddFavorite(string path)
    {
        AddFavoriteEntry(path);
    }

    private FavoriteEntry AddFavoriteEntry(string path)
    {
        path = PathUtil.Normalize(path);
        var existing = _favorites.FirstOrDefault(f => PathUtil.Equal(f.Path, path));
        if (existing != null) return existing;
        var entry = new FavoriteEntry(path);
        _favorites.Add(entry);
        SaveFavorites();
        return entry;
    }

    private void RemoveFavorite(FavoriteEntry entry)
    {
        bool wasSelected = ReferenceEquals(FavoritesList.SelectedItem, entry);
        _favorites.Remove(entry);
        _scanCache.Remove(entry.Path);
        SaveFavorites();
        if (wasSelected)
        {
            CancelScan();
            _root = null;
            _settings.SelectedFavorite = null;
            _settings.Save();
            ClearTiles();
            ShowWelcome();
        }
    }

    private void SaveFavorites()
    {
        _settings.Favorites = _favorites.Select(f => f.Path).ToList();
        _settings.Save();
        UpdateFavoritesHint();
    }

    private void UpdateFavoritesHint() =>
        NoFavoritesText.Visibility = _favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void FavoritesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FavoritesList.SelectedItem is FavoriteEntry fav) LoadFavorite(fav.Path, rescan: false);
    }

    private void AddFavorite_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要收藏的漫画目录（可多选）", Multiselect = true };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        FavoriteEntry? last = null;
        foreach (var path in dlg.FolderNames) last = AddFavoriteEntry(path);
        if (last != null)
        {
            if (ReferenceEquals(FavoritesList.SelectedItem, last)) LoadFavorite(last.Path, rescan: false);
            else FavoritesList.SelectedItem = last;
        }
    }

    private void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FavoriteEntry fav) RemoveFavorite(fav);
        e.Handled = true;
    }

    private void FavoriteMenuList_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FavoriteEntry fav) OpenListRequested?.Invoke(fav.Path);
    }

    private void FavoriteMenuRescan_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not FavoriteEntry fav) return;
        _scanCache.Remove(fav.Path);
        if (ReferenceEquals(FavoritesList.SelectedItem, fav)) LoadFavorite(fav.Path, rescan: true);
        else FavoritesList.SelectedItem = fav;
    }

    private void FavoriteMenuExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FavoriteEntry fav) OpenInExplorer(fav.Path, select: false);
    }

    private void FavoriteMenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FavoriteEntry fav) RemoveFavorite(fav);
    }

    // ───────────── 扫描 ─────────────

    private void LoadFavorite(string path, bool rescan)
    {
        double? restoreOffset = rescan && PathUtil.Equal(_root, path)
            ? (_restoreOffset ?? _scroll?.VerticalOffset)
            : null;
        CancelScan();
        ThumbnailService.Instance.CancelPending();
        _root = path;
        _settings.SelectedFavorite = path;
        _settings.Save();

        HeaderTitle.Text = PathUtil.GetDisplayName(path);
        HeaderTitle.ToolTip = path;
        ClearTiles(resetScroll: restoreOffset == null);
        _restoreOffset = restoreOffset;
        UpdateToolbar();

        if (!Directory.Exists(path))
        {
            _restoreOffset = null;
            ShowEmpty("\uE783", "目录不存在", path + "\n可能已被移动、删除，或所在的磁盘没有连接。", showActions: false);
            HeaderStatus.Text = string.Empty;
            return;
        }

        if (!rescan && _scanCache.TryGetValue(path, out var cached))
        {
            _scanned = cached.Select(MakeTile).ToList();
            FinishScan(rebuild: true);
            return;
        }

        HideEmpty();
        _scanning = true;
        var queue = new ConcurrentQueue<ComicItem>();
        _incoming = queue;
        var cts = new CancellationTokenSource();
        _scanCts = cts;
        UpdateStatus();

        var root = path;
        Task.Run(() => ComicScanner.ScanRecursive(root, queue.Enqueue, cts.Token), cts.Token)
            .ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ReferenceEquals(cts, _scanCts)) DrainIncoming(final: true);
            })), TaskScheduler.Default);
        _drainTimer.Start();
    }

    private void CancelScan()
    {
        _drainTimer.Stop();
        _scanning = false;
        _scanCts?.Cancel();
        _scanCts = null;
    }

    private void DrainIncoming(bool final)
    {
        var fresh = new List<ComicTile>();
        while (_incoming.TryDequeue(out var item)) fresh.Add(MakeTile(item));

        if (fresh.Count > 0)
        {
            _scanned.AddRange(fresh);
            var matching = _terms.Length == 0 ? fresh : fresh.Where(Matches).ToList();
            if (matching.Count > 0)
            {
                _visible.AddRange(matching);
                AppendRows(matching);
                HideEmpty();
            }
        }

        if (_restoreOffset is { } offset && (final || _rows.Count * RowHeight >= offset + (_scroll?.ViewportHeight ?? 0)))
        {
            _restoreOffset = null;
            _scroll?.ScrollToVerticalOffset(offset);
        }

        if (final)
        {
            _drainTimer.Stop();
            _scanCts = null;
            if (_root != null) _scanCache[_root] = _scanned.Select(t => t.Item).ToList();
            FinishScan(rebuild: false);
        }
        else
        {
            UpdateStatus();
        }
    }

    private void FinishScan(bool rebuild)
    {
        _scanning = false;
        if (rebuild)
        {
            ApplyFilter();
            ScrollToRow(0);
        }

        if (_scanned.Count == 0)
            ShowEmpty("\uE8F1", "没有找到漫画", "这个目录及其子目录里没有图片文件夹、压缩包或 PDF。", showActions: false);
        else if (_visible.Count == 0 && _terms.Length > 0)
            ShowEmpty("\uE721", "没有匹配的漫画", "换个关键词试试。", showActions: false);
        else
            HideEmpty();
        UpdateStatus();
    }

    private ComicTile MakeTile(ComicItem item)
    {
        string subtitle = string.Empty;
        if (_root != null && !PathUtil.Equal(item.Path, _root))
            subtitle = PathUtil.GetRelative(_root, item.ParentDirectory) ?? item.ParentDirectory;
        return new ComicTile(item, subtitle);
    }

    // ───────────── 过滤 / 布局 ─────────────

    private void ApplyFilter()
    {
        _terms = SearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _visible = _terms.Length == 0 ? new List<ComicTile>(_scanned) : _scanned.Where(Matches).ToList();
        RebuildRows();

        if (_root != null && !_scanning)
        {
            if (_visible.Count == 0 && _scanned.Count > 0)
                ShowEmpty("\uE721", "没有匹配的漫画", "换个关键词试试。", showActions: false);
            else if (_visible.Count > 0)
                HideEmpty();
        }
        UpdateStatus();
    }

    private bool Matches(ComicTile tile)
    {
        foreach (var term in _terms)
            if (!tile.SearchKey.Contains(term, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private void RebuildRows()
    {
        var rows = new List<TileRow>((_visible.Count + _columns - 1) / _columns);
        for (int i = 0; i < _visible.Count; i += _columns)
            rows.Add(new TileRow(_visible.GetRange(i, Math.Min(_columns, _visible.Count - i))));
        _rows.ReplaceAll(rows);
    }

    private void AppendRows(List<ComicTile> tiles)
    {
        int start = 0;
        if (_rows.Count > 0)
        {
            var last = _rows[^1];
            if (last.Tiles.Count < _columns)
            {
                int take = Math.Min(_columns - last.Tiles.Count, tiles.Count);
                _rows[_rows.Count - 1] = new TileRow(last.Tiles.Concat(tiles.Take(take)).ToList());
                start = take;
            }
        }
        for (int i = start; i < tiles.Count; i += _columns)
            _rows.Add(new TileRow(tiles.GetRange(i, Math.Min(_columns, tiles.Count - i))));
    }

    private void ClearTiles(bool resetScroll = true)
    {
        _restoreOffset = null;
        _scanned = new List<ComicTile>();
        _visible = new List<ComicTile>();
        _rows.ReplaceAll(Array.Empty<TileRow>());
        if (resetScroll) ScrollToRow(0);
    }

    private void TileGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateColumns();

    private void UpdateColumns()
    {
        double width = TileGrid.ActualWidth - 10 - 18;
        if (width <= 0) return;
        int cols = Math.Max(1, (int)(width / TileOuterWidth));
        double leftover = Math.Max(0, width - cols * TileOuterWidth);
        TileGrid.Padding = new Thickness(14 + leftover / 2, 0, 4 + leftover / 2, 16);
        if (cols == _columns) return;

        int firstTile = FirstVisibleRow() * _columns;
        if (_restoreOffset is { } offset)
            _restoreOffset = (int)(offset / RowHeight) * _columns / cols * RowHeight;
        _columns = cols;
        RebuildRows();
        ScrollToRow(firstTile / _columns);
    }

    private int FirstVisibleRow() => _scroll == null ? 0 : (int)(_scroll.VerticalOffset / RowHeight);

    private void ScrollToRow(int row) => _scroll?.ScrollToVerticalOffset(Math.Max(0, row * RowHeight));

    private void Scroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || AutoScroll.IsActive) return;
        _restoreOffset = null;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 1.6);
        e.Handled = true;
    }

    /// <summary>从阅读返回时，把刚才读的那本滚动到可见位置。</summary>
    public void Reveal(ComicItem item)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            int idx = _visible.FindIndex(t => t.Item.Equals(item));
            if (idx < 0 || _scroll == null) return;
            double top = idx / _columns * RowHeight;
            if (top < _scroll.VerticalOffset || top + RowHeight > _scroll.VerticalOffset + _scroll.ViewportHeight)
                _scroll.ScrollToVerticalOffset(Math.Max(0, top - (_scroll.ViewportHeight - RowHeight) / 2));
        }));
    }

    // ───────────── 状态显示 ─────────────

    private void UpdateStatus()
    {
        if (_root == null)
        {
            HeaderStatus.Text = string.Empty;
            return;
        }
        string order = _settings.RandomOrder ? "随机阅读" : "顺序阅读";
        if (_scanning)
            HeaderStatus.Text = "正在扫描… 已找到 " + _scanned.Count + " 本";
        else if (_terms.Length > 0)
            HeaderStatus.Text = "筛选出 " + _visible.Count + " / " + _scanned.Count + " 本 · " + order;
        else
            HeaderStatus.Text = "共 " + _scanned.Count + " 本 · " + order + " · " + _root;
    }

    private void UpdateToolbar()
    {
        bool hasRoot = _root != null;
        ToolbarActions.IsEnabled = hasRoot;
        ToolbarActions.Opacity = hasRoot ? 1 : 0.4;
    }

    private void ShowWelcome()
    {
        HeaderTitle.Text = "书库";
        HeaderTitle.ToolTip = null;
        HeaderStatus.Text = string.Empty;
        UpdateToolbar();
        if (_favorites.Count == 0)
            ShowEmpty("\uE8F1", "还没有收藏目录",
                "收藏一个漫画目录后，这里会以封面方式展示其中（包括子目录）的所有漫画，可以顺序或随机阅读。\n也可以点「打开文件夹」以列表模式阅读，或直接把文件夹、压缩包拖进窗口。",
                showActions: true);
        else
            ShowEmpty("\uE8F1", "选择一个收藏目录", "从左侧收藏夹中选择一个目录，浏览其中的所有漫画。", showActions: false);
    }

    private void ShowEmpty(string icon, string title, string text, bool showActions)
    {
        EmptyIcon.Text = icon;
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyActions.Visibility = showActions ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void HideEmpty() => EmptyState.Visibility = Visibility.Collapsed;

    // ───────────── 操作 ─────────────

    /// <summary>
    /// 随机模式下封面顺序不变，只把阅读时的列表打乱：点开的这本排第一，其余随机。
    /// </summary>
    private void OpenTile(ComicTile tile)
    {
        int idx = _visible.IndexOf(tile);
        if (idx < 0) return;
        var items = _visible.Select(t => t.Item).ToList();
        if (_settings.RandomOrder)
        {
            items.RemoveAt(idx);
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (items[i], items[j]) = (items[j], items[i]);
            }
            items.Insert(0, tile.Item);
            idx = 0;
        }
        OpenComicRequested?.Invoke(items, idx, HeaderTitle.Text + (_settings.RandomOrder ? "（随机）" : string.Empty), _root ?? string.Empty);
    }

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ComicTile tile) OpenTile(tile);
    }

    private void TileMenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ComicTile tile) OpenTile(tile);
    }

    private void TileMenuList_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ComicTile tile) OpenListRequested?.Invoke(tile.Item.Path);
    }

    private void TileMenuExplorer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ComicTile tile) OpenInExplorer(tile.Item.Path, select: true);
    }

    private void TileMenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ComicTile tile) DeleteTile(tile);
    }

    private void DeleteTile(ComicTile tile)
    {
        ThumbnailService.Instance.Remove(tile);
        try
        {
            ComicScanner.MoveToRecycleBin(tile.Item, _root);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            if (File.Exists(tile.Item.Path) || Directory.Exists(tile.Item.Path)) return;
        }

        _scanned.RemoveAll(t => t.Item.Equals(tile.Item));
        if (_root != null && _scanCache.TryGetValue(_root, out var cached))
            cached.RemoveAll(i => i.Equals(tile.Item));

        int idx = _visible.FindIndex(t => t.Item.Equals(tile.Item));
        if (idx >= 0)
        {
            _visible.RemoveAt(idx);
            int startRow = idx / _columns;
            int newRowCount = (_visible.Count + _columns - 1) / _columns;
            for (int r = startRow; r < newRowCount; r++)
            {
                int first = r * _columns;
                var slice = _visible.GetRange(first, Math.Min(_columns, _visible.Count - first));
                if (r < _rows.Count) _rows[r] = new TileRow(slice);
                else _rows.Add(new TileRow(slice));
            }
            while (_rows.Count > newRowCount)
                _rows.RemoveAt(_rows.Count - 1);
        }

        if (!_scanning)
        {
            if (_scanned.Count == 0)
                ShowEmpty("\uE8F1", "没有找到漫画", "这个目录及其子目录里没有图片文件夹、压缩包或 PDF。", showActions: false);
            else if (_visible.Count == 0 && _terms.Length > 0)
                ShowEmpty("\uE721", "没有匹配的漫画", "换个关键词试试。", showActions: false);
        }
        UpdateStatus();
    }

    private static void OpenInExplorer(string path, bool select)
    {
        try
        {
            var args = select ? "/select,\"" + path + "\"" : "\"" + path + "\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    private void Order_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressOrderEvent) return;
        _settings.RandomOrder = RandomRadio.IsChecked == true;
        _settings.Save();
        UpdateStatus();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Rescan();

    private void Rescan()
    {
        if (_root == null) return;
        _scanCache.Remove(_root);
        LoadFavorite(_root, rescan: true);
    }

    private void RandomRead_Click(object sender, RoutedEventArgs e)
    {
        if (_visible.Count == 0) return;
        OpenTile(_visible[Random.Shared.Next(_visible.Count)]);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolderRequested?.Invoke();

    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFileRequested?.Invoke();

    private void Help_Click(object sender, RoutedEventArgs e) => HelpRequested?.Invoke();

#if DEBUG
    internal string DebugState =>
        $"root={_root} scanned={_scanned.Count} visible={_visible.Count} rows={_rows.Count} columns={_columns} status={HeaderStatus.Text} " +
        $"empty={(EmptyState.Visibility == Visibility.Visible ? EmptyTitle.Text : "-")} order={string.Join(",", _visible.Take(8).Select(t => t.Title))} " +
        $"scroll={_scroll?.VerticalOffset:0}/{_scroll?.ScrollableHeight:0}";

    internal ScrollViewer? DebugScroll => _scroll;

    internal void DebugOpenTile(int index) => OpenTile(_visible[index]);

    internal void DebugOpenTile(string title) => OpenTile(_visible.First(t => t.Title == title));

    internal void DebugSearch(string text)
    {
        SearchBox.Text = text;
        _filterTimer.Stop();
        ApplyFilter();
    }

    internal void DebugSetRandom(bool random) => (random ? RandomRadio : SequentialRadio).IsChecked = true;
#endif

    public bool HandleKeyDown(Key key, ModifierKeys mods)
    {
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        if (key == Key.F && ctrl)
        {
            if (_root == null) return true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            return true;
        }
        if (key == Key.F5)
        {
            Rescan();
            return true;
        }
        if (SearchBox.IsKeyboardFocusWithin)
        {
            if (key == Key.Escape)
            {
                if (SearchBox.Text.Length > 0) SearchBox.Clear();
                else Keyboard.ClearFocus();
                return true;
            }
            if (key == Key.Enter)
            {
                _filterTimer.Stop();
                ApplyFilter();
                if (_visible.Count > 0) OpenTile(_visible[0]);
                return true;
            }
        }
        return false;
    }
}
