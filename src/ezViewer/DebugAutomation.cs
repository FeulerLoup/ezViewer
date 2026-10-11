#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EzViewer;

/// <summary>
/// 仅 Debug 版：ezViewer.exe --autotest script.txt，按脚本在进程内模拟操作，输出截图和状态日志。
/// 命令：key Alt+Down | repeat Right | down W | up W | wheel -120 | ctrlwheel 120 | open 路径 | tile 3
///       search 文本 | random on/off | wait 300 | shot 名称 | state | exit
/// </summary>
internal static class DebugAutomation
{
    public static async void Run(MainWindow window, string scriptPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(scriptPath))!;
        var log = Path.Combine(dir, "autotest.log");
        File.WriteAllText(log, string.Empty);
        void Log(string s) => File.AppendAllText(log, s + Environment.NewLine);

        await Task.Delay(1200);
        foreach (var raw in File.ReadAllLines(scriptPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int sp = line.IndexOf(' ');
            var cmd = sp < 0 ? line : line[..sp];
            var arg = sp < 0 ? string.Empty : line[(sp + 1)..].Trim();
            try
            {
                switch (cmd)
                {
                    case "key":
                    case "repeat":
                    {
                        var (key, mods) = ParseKey(arg);
                        window.ProcessKeyDown(key, mods, isRepeat: cmd == "repeat");
                        window.Reader.HandleKeyUp(key);
                        break;
                    }
                    case "down":
                        window.ProcessKeyDown(ParseKey(arg).Key, ModifierKeys.None, false);
                        break;
                    case "up":
                        window.Reader.HandleKeyUp(ParseKey(arg).Key);
                        break;
                    case "wheel":
                    case "ctrlwheel":
                    {
                        var vp = window.Reader.Viewport;
                        window.Reader.HandleWheel(int.Parse(arg), new Point(vp.ActualWidth * 0.75, vp.ActualHeight * 0.25), cmd == "ctrlwheel");
                        break;
                    }
                    case "open":
                        window.OpenListMode(arg);
                        break;
                    case "tile":
                        window.Library.DebugOpenTile(int.Parse(arg));
                        break;
                    case "tilename":
                        window.Library.DebugOpenTile(arg);
                        break;
                    case "winstate":
                        window.WindowState = Enum.Parse<WindowState>(arg);
                        break;
                    case "placement":
                    {
                        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                        var wp = Interop.NativeMethods.GetPlacement(hwnd);
                        var r = wp.rcNormalPosition;
                        Log($"[{arg}] showCmd={wp.showCmd} normal=({r.Left},{r.Top},{r.Right},{r.Bottom}) zoomed={Interop.NativeMethods.DebugIsZoomed(hwnd)} " +
                            $"rect={Interop.NativeMethods.DebugWindowRect(hwnd)} wpf={window.WindowState} restore={window.RestoreBounds}");
                        break;
                    }
                    case "search":
                        window.Library.DebugSearch(arg);
                        break;
                    case "random":
                        window.Library.DebugSetRandom(arg == "on");
                        break;
                    case "wait":
                        await Task.Delay(int.Parse(arg));
                        break;
                    case "shot":
                        Snapshot(window, Path.Combine(dir, arg + ".png"));
                        break;
                    case "state":
                        Log("[" + arg + "] " + (window.Reader.Visibility == Visibility.Visible ? window.Reader.DebugState : window.Library.DebugState)
                            + " title=" + window.Title + " help=" + window.HelpOverlay.Visibility + " windowState=" + window.WindowState + " style=" + window.WindowStyle
                            + $" bounds=({window.Left:0},{window.Top:0},{window.ActualWidth:0}x{window.ActualHeight:0})");
                        break;
                    case "list":
                        Log("[" + arg + "] list=" + window.Reader.DebugListNames);
                        break;
                    case "autoscroll":
                    {
                        // autoscroll library|list 偏移量 毫秒：模拟按下中键后把鼠标移到锚点下方（正数）/ 上方（负数）
                        var parts = arg.Split(' ');
                        var sv = parts[0] == "library" ? window.Library.DebugScroll : window.Reader.DebugListScroll;
                        if (sv == null)
                        {
                            Log("!! autoscroll: 找不到滚动区域");
                            break;
                        }
                        double before = sv.VerticalOffset;
                        var anchor = new Point(sv.ActualWidth / 2, sv.ActualHeight / 2);
                        Views.AutoScroll.DebugPointer = () => new Point(anchor.X, anchor.Y + double.Parse(parts[1]));
                        bool started = Views.AutoScroll.Start(sv, anchor);
                        await Task.Delay(int.Parse(parts[2]));
                        Views.AutoScroll.Cancel();
                        Views.AutoScroll.DebugPointer = null;
                        Log($"[autoscroll {arg}] started={started} offset {before:0} -> {sv.VerticalOffset:0} / {sv.ScrollableHeight:0}");
                        break;
                    }
                    case "exit":
                        window.Close();
                        return;
                }
            }
            catch (Exception ex)
            {
                Log("!! " + line + " -> " + ex);
            }
            await Task.Delay(40);
        }
    }

    private static (Key Key, ModifierKeys Mods) ParseKey(string text)
    {
        var mods = ModifierKeys.None;
        var parts = text.Split('+');
        foreach (var p in parts.Take(parts.Length - 1))
        {
            mods |= p switch
            {
                "Alt" => ModifierKeys.Alt,
                "Ctrl" => ModifierKeys.Control,
                "Shift" => ModifierKeys.Shift,
                _ => ModifierKeys.None,
            };
        }
        return ((Key)Enum.Parse(typeof(Key), parts[^1]), mods);
    }

    private static void Snapshot(Window window, string path)
    {
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var rtb = new RenderTargetBitmap(
            (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        rtb.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }
}
#endif
