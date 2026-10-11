using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using EzViewer.Core;
using EzViewer.Interop;
using EzViewer.Library;
using EzViewer.Views;
using Microsoft.Win32;

namespace EzViewer;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string? _startupPath;
    private bool _fullscreen;
    private WindowStyle _restoreStyle;
    private ResizeMode _restoreResize;
    private NativeMethods.WINDOWPLACEMENT _restorePlacement;
    private NativeMethods.RECT _restoreRect;
    private Rect _restoreBounds;
    private bool _restoreMaximized;
    private readonly DispatcherTimer _transitionTimer;

    public MainWindow(AppSettings settings, string? startupPath)
    {
        _settings = settings;
        _startupPath = startupPath;
        InitializeComponent();
        _transitionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _transitionTimer.Tick += (_, _) =>
        {
            _transitionTimer.Stop();
            NativeMethods.SetTransitionsEnabled(new WindowInteropHelper(this).Handle, true);
        };
        RestorePlacement();
        BuildHelp();

        Library.Initialize(settings);
        Reader.Initialize(settings);

        Library.OpenFolderRequested += ShowOpenFolderDialog;
        Library.OpenFileRequested += ShowOpenFileDialog;
        Library.HelpRequested += ToggleHelp;
        Library.OpenListRequested += OpenListMode;
        Library.OpenComicRequested += OpenFreeMode;

        Reader.BackRequested += ShowLibrary;
        Reader.FullscreenToggleRequested += ToggleFullscreen;
        Reader.AddFavoriteRequested += path =>
        {
            Library.AddFavorite(path);
            Reader.Toast("已加入收藏夹：" + PathUtil.GetDisplayName(path));
        };
        Reader.CurrentComicChanged += item => Title = item == null ? "ezViewer" : item.Name + " - ezViewer";

        SourceInitialized += (_, _) => NativeMethods.TryEnableDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_startupPath)) OpenListMode(_startupPath);
        };
    }

    // ───────────── 视图切换 ─────────────

    internal async void OpenListMode(string path)
    {
        ShowReader();
        try
        {
            var full = PathUtil.Normalize(path);
            _settings.LastOpenFolder = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
            _settings.Save();
        }
        catch (Exception)
        {
        }
        await Reader.OpenListAsync(path);
    }

    private void OpenFreeMode(List<ComicItem> items, int index, string title, string root)
    {
        ShowReader();
        Reader.OpenFree(items, index, title, root);
    }

    private void ShowReader()
    {
        HelpOverlay.Visibility = Visibility.Collapsed;
        Library.Visibility = Visibility.Collapsed;
        Reader.Visibility = Visibility.Visible;
        Keyboard.ClearFocus();
        Focus();
    }

    private void ShowLibrary()
    {
        var last = Reader.CurrentItem;
        bool free = Reader.IsFreeMode;
        Reader.Close();
        Reader.Visibility = Visibility.Collapsed;
        Library.Visibility = Visibility.Visible;
        Title = "ezViewer";
        if (free && last != null) Library.Reveal(last);
    }

    private void ShowOpenFolderDialog()
    {
        var dlg = new OpenFolderDialog { Title = "选择漫画文件夹（列表模式）" };
        if (!string.IsNullOrEmpty(_settings.LastOpenFolder) && Directory.Exists(_settings.LastOpenFolder))
            dlg.InitialDirectory = _settings.LastOpenFolder;
        if (dlg.ShowDialog(this) == true) OpenListMode(dlg.FolderName);
    }

    private void ShowOpenFileDialog()
    {
        var dlg = new OpenFileDialog { Title = "打开漫画文件", Filter = FileTypes.OpenFileFilter };
        if (!string.IsNullOrEmpty(_settings.LastOpenFolder) && Directory.Exists(_settings.LastOpenFolder))
            dlg.InitialDirectory = _settings.LastOpenFolder;
        if (dlg.ShowDialog(this) == true) OpenListMode(dlg.FileName);
    }

    // ───────────── 全屏 ─────────────

    /// <summary>
    /// 全屏 = 无边框窗口直接铺满所在显示器，不借助最大化；退出时按进入前的位置和最大化状态原样恢复。
    /// 切换期间关闭 DWM 的最大化/还原动画，否则会看到窗口先缩小再放大、边框闪动。
    /// </summary>
    private void ToggleFullscreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.SetTransitionsEnabled(hwnd, false);
        _transitionTimer.Stop();
        _transitionTimer.Start();

        if (!_fullscreen)
        {
            _restoreMaximized = WindowState == WindowState.Maximized;
            _restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            _restorePlacement = NativeMethods.GetPlacement(hwnd);
            _restoreRect = NativeMethods.GetWindowBounds(hwnd);
            _restoreStyle = WindowStyle;
            _restoreResize = ResizeMode;
            _fullscreen = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // 最大化时直接还原成铺满显示器的大小，避免先闪一下原来的窗口尺寸
            if (WindowState == WindowState.Maximized) NativeMethods.UnmaximizeToMonitor(hwnd, _restorePlacement);
            NativeMethods.CoverMonitor(hwnd);
        }
        else
        {
            _fullscreen = false;
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResize;
            if (_restoreMaximized)
            {
                // 先从全屏直接最大化，再在最大化状态下写回原来的还原尺寸
                WindowState = WindowState.Maximized;
                NativeMethods.RestorePlacement(hwnd, _restorePlacement);
            }
            else
            {
                NativeMethods.SetWindowBounds(hwnd, _restoreRect);
            }
        }
        Reader.SetFullscreen(_fullscreen);
    }

    // ───────────── 键盘 ─────────────

    private static Key RealKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        _ => e.Key,
    };

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed && Reader.Visibility != Visibility.Visible ? e.Key : RealKey(e);
        if (ProcessKeyDown(key, Keyboard.Modifiers, e.IsRepeat)) e.Handled = true;
    }

    internal bool ProcessKeyDown(Key key, ModifierKeys mods, bool isRepeat)
    {
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        // 按任意键结束中键自动滚动，Esc 只用来结束滚动
        if (AutoScroll.Cancel() && key == Key.Escape) return true;

        if (HelpOverlay.Visibility == Visibility.Visible)
        {
            if (key is not (Key.Escape or Key.F1)) return false;
            HelpOverlay.Visibility = Visibility.Collapsed;
            return true;
        }

        switch (key)
        {
            case Key.F1:
                ToggleHelp();
                return true;
            case Key.F11:
                ToggleFullscreen();
                return true;
            case Key.O when ctrl:
                if (shift) ShowOpenFileDialog();
                else ShowOpenFolderDialog();
                return true;
            case Key.Escape:
                if (_fullscreen)
                {
                    ToggleFullscreen();
                    return true;
                }
                if (Reader.Visibility == Visibility.Visible)
                {
                    ShowLibrary();
                    return true;
                }
                break;
        }

        return Reader.Visibility == Visibility.Visible
            ? Reader.HandleKeyDown(key, mods, isRepeat)
            : Library.HandleKeyDown(key, mods);
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        var key = RealKey(e);
        Reader.HandleKeyUp(key);
        // 单独按下再松开 Alt 会让窗口进入系统菜单模式，之后的方向键都会被菜单吃掉
        if (key is Key.LeftAlt or Key.RightAlt) e.Handled = true;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        AutoScroll.Cancel();
        Reader.ReleaseInput();
    }

    // ───────────── 拖放 ─────────────

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            e.Handled = true;
            Activate();
            OpenListMode(paths[0]);
        }
    }

    // ───────────── 帮助 ─────────────

    private void ToggleHelp() =>
        HelpOverlay.Visibility = HelpOverlay.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void HelpOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, HelpOverlay)) HelpOverlay.Visibility = Visibility.Collapsed;
    }

    private void HelpClose_Click(object sender, RoutedEventArgs e) => HelpOverlay.Visibility = Visibility.Collapsed;

    private void BuildHelp()
    {
        var reading = new (string[] Keys, string Text)[]
        {
            (new[] { "←", "→" }, "上一页 / 下一页（首页、末页继续翻进入上一本 / 下一本）"),
            (new[] { "↑", "↓" }, "列表中的上一项 / 下一项"),
            (new[] { "Alt+↑", "Backspace" }, "返回上一级文件夹（正在读的漫画保持打开）"),
            (new[] { "Alt+↓", "Enter" }, "进入选中的文件夹（也可双击列表项）"),
            (new[] { "Home", "End" }, "第一页 / 最后一页"),
            (new[] { "PageUp", "PageDown" }, "放大 / 缩小（翻页后恢复长边适配）"),
            (new[] { "W", "A", "S", "D" }, "放大后移动画面，按住 Shift 加速"),
            (new[] { "Tab" }, "显示 / 隐藏漫画列表"),
            (new[] { "F11" }, "全屏（也可以双击画面）"),
            (new[] { "Esc" }, "退出全屏 / 返回书库"),
        };
        var mouse = new (string[] Keys, string Text)[]
        {
            (new[] { "滚轮" }, "翻页"),
            (new[] { "Ctrl+滚轮" }, "以指针为中心缩放"),
            (new[] { "左键拖动" }, "移动画面"),
            (new[] { "双击" }, "切换全屏"),
            (new[] { "中键" }, "在封面网格或漫画列表上按下中键后上下移动鼠标，自动滚动"),
            (new[] { "拖放" }, "把文件夹、压缩包、PDF 或图片拖进窗口"),
        };
        var general = new (string[] Keys, string Text)[]
        {
            (new[] { "Ctrl+O" }, "打开文件夹（列表模式）"),
            (new[] { "Ctrl+Shift+O" }, "打开压缩包 / PDF / 图片"),
            (new[] { "Ctrl+F" }, "在书库中搜索"),
            (new[] { "F5" }, "重新扫描当前收藏目录"),
            (new[] { "F1" }, "显示 / 关闭本帮助"),
        };

        HelpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        HelpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        HelpGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        AddSection(left, "阅读", reading);
        var right = new StackPanel();
        AddSection(right, "鼠标", mouse);
        AddSection(right, "通用", general);
        Grid.SetColumn(right, 2);
        HelpGrid.Children.Add(left);
        HelpGrid.Children.Add(right);

        SettingsPathText.Text = "设置文件：" + _settings.StoragePath + "　　封面缓存：" + ThumbnailService.Instance.CacheDirectory;
    }

    private void AddSection(StackPanel panel, string title, (string[] Keys, string Text)[] rows)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("SubtleTextBrush"),
            Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 16, 0, 8),
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var keyStyle = (Style)FindResource("KeyCap");
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new WrapPanel { Margin = new Thickness(0, 3, 12, 3), VerticalAlignment = VerticalAlignment.Center };
            foreach (var k in rows[i].Keys)
                keys.Children.Add(new Border { Style = keyStyle, Child = new TextBlock { Text = k, FontSize = 12 } });
            var text = new TextBlock
            {
                Text = rows[i].Text,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 330,
            };
            Grid.SetRow(keys, i);
            Grid.SetRow(text, i);
            Grid.SetColumn(text, 1);
            grid.Children.Add(keys);
            grid.Children.Add(text);
        }
        panel.Children.Add(grid);
    }

    // ───────────── 窗口位置 ─────────────

    private void RestorePlacement()
    {
        var s = _settings;
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }
        if (!double.IsNaN(s.WindowLeft) && !double.IsNaN(s.WindowTop))
        {
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var titleBar = new Rect(s.WindowLeft + 40, s.WindowTop, Math.Max(80, Width - 80), 30);
            if (screen.IntersectsWith(titleBar))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft;
                Top = s.WindowTop;
            }
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = _fullscreen ? _restoreBounds
            : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }
        _settings.WindowMaximized = _fullscreen ? _restoreMaximized : WindowState == WindowState.Maximized;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        SavePlacement();
        _settings.Save();
        Reader.Close();
    }
}
