using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using QuickAppsOverlay;
using QuickAppsOverlay.Models;
using QuickAppsOverlay.Services;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

internal class Program
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static int _failures;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    private static void Check(bool value, string name)
    {
        Console.WriteLine($"{(value ? "PASS" : "FAIL")} {name}");
        if (!value) _failures++;
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Contains("--probe")) { RunProbe(); return; }
        var area = new Rect(-1920, 0, 1920, 1080);
        foreach (string edge in new[] { "Left", "Right", "Top", "Bottom" })
        {
            var position = DockGeometry.Place(area, new Size(200, 44), edge, 100);
            var window = new Rect(position, new Size(200, 44));
            Check(DockGeometry.FindEdge(area, window) == edge, $"snap {edge} on negative-coordinate monitor");
            Check(area.Contains(window), $"clamp {edge}");
        }
        Check(DockGeometry.FindEdge(area, new Rect(-1500, 400, 200, 44)) == "Floating", "detach from edges");
        Check(DockGeometry.FindEdge(new Rect(0, 0, 1000, 800), new Rect(17, 300, 200, 44)) == "Floating", "16 DIP threshold");
        Check(DockGeometry.FindEdge(new Rect(0, 0, 1000, 800), new Rect(16, 300, 200, 44)) == "Left", "threshold inclusive");

        var wxShortcut = new LauncherItem { Name = "微信", Path = @"C:\Users\Admin\Desktop\WeChat.lnk" };
        var wxWindow = new WindowInfo { ProcessName = "WeChat", ProcessPath = @"C:\Program Files\WeChat.exe" };
        Check(AppIdentityMatcher.Matches(wxShortcut, @"C:\Program Files\WeChat.exe", wxWindow), "pinned target path match");
        Check(AppIdentityMatcher.Matches(wxShortcut, @"C:\Program Files\WeChat.exe",
            new WindowInfo { ProcessName = "WeChat", ProcessPath = @"c:\PROGRAM FILES\WECHAT.EXE" }), "path comparison ignores case");
        Check(AppIdentityMatcher.Matches(wxShortcut, @"C:\Old\WeChat.exe",
            new WindowInfo { ProcessName = "WeChat", ProcessPath = @"D:\Apps\WeChat.exe" }), "shortcut and runtime path differ, executable name matches");
        Check(AppIdentityMatcher.Matches(wxShortcut, @"C:\Old\WeChat.exe",
            new WindowInfo { ProcessName = "WeChat", ProcessPath = "" }), "missing process path falls back to process name");
        Check(AppIdentityMatcher.MatchesProcessIdentity("微信", "", "WeChat", ""),
            "flash event process identity matches Chinese WeChat launcher name");
        var wxWindows = new[] { wxWindow, new WindowInfo { ProcessName = "WeChat", ProcessPath = @"D:\WeChat.exe" } };
        Check(wxWindows.Count(w => AppIdentityMatcher.Matches(wxShortcut, @"C:\Program Files\WeChat.exe", w)) == 2,
            "multiple running windows merge under one pinned icon");
        Check(!AppIdentityMatcher.Matches(wxShortcut, @"C:\Program Files\WeChat.exe",
            new WindowInfo { ProcessName = "WeCom", ProcessPath = @"D:\WeCom.exe" }), "different application remains distinct");
        var edgeMatcher = typeof(WindowService).GetMethod("IsEdgeTaskWindow", BindingFlags.NonPublic | BindingFlags.Static)!;
        bool edgeWithToolStyle = (bool)edgeMatcher.Invoke(null,
            new object[] { "msedge", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe", "Chrome_WidgetWin_1", 0x80 })!;
        bool edgeNoActivate = (bool)edgeMatcher.Invoke(null,
            new object[] { "msedge", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe", "Chrome_WidgetWin_1", 0x08000000 })!;
        Check(edgeWithToolStyle, "Edge browser window survives owner/tool-window style filtering");
        Check(!edgeNoActivate, "non-activating Edge helper window remains excluded");
        var orderMethod = typeof(WindowService).GetMethod("OrderForegroundFirst", BindingFlags.NonPublic | BindingFlags.Static)!;
        var activeEdge = new WindowInfo { Handle = new IntPtr(0x40A72), ProcessName = "msedge" };
        var olderApp = new WindowInfo { Handle = new IntPtr(0x70E22), ProcessName = "other" };
        var foregroundOrder = (List<WindowInfo>)orderMethod.Invoke(null,
            new object[] { new[] { olderApp, activeEdge }, activeEdge.Handle })!;
        Check(foregroundOrder[0].Handle == activeEdge.Handle, "foreground app is first in dynamic task list");
        Check(JsonOptionsForLegacy().LayoutOrientation == "Auto", "legacy settings use auto orientation");

        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = new SettingsService();
        settings.Settings.DockEdge = "Top";
        var overlay = new OverlayWindow(settings);
        overlay.Show();
        application.Dispatcher.BeginInvoke(async () =>
        {
            Process? probe = null;
            DispatcherTimer? heartbeat = null;
            try
            {
                if (args.Contains("--edge-audit"))
                {
                    await Task.Delay(700);
                    var edgePids = Process.GetProcessesByName("msedge").Select(p => (uint)p.Id).ToHashSet();
                    var edgeService = (WindowService)typeof(OverlayWindow).GetField("_windowService", Private)!.GetValue(overlay)!;
                    var edgeWindows = Snapshot(overlay).Where(w => edgePids.Contains(w.ProcessId)).ToList();
                    var trackedWindows = (List<WindowInfo>)typeof(OverlayWindow).GetField("_trackedWindows", Private)!.GetValue(overlay)!;
                    Check(edgeWindows.Count > 0, "live Edge windows enumerated on this machine");
                    Check(edgeWindows.All(w => w.Icon != null && w.IconKey != "none"), "live Edge windows have icons");
                    Check(edgeWindows.All(w => trackedWindows.Any(mapped => mapped.Handle == w.Handle)),
                        "live Edge windows appear in the dynamic task list");
                    using var edgeDiagnostic = System.Text.Json.JsonDocument.Parse(edgeService.DiagnosticsJson);
                    Console.WriteLine("EDGE_WINDOWS=" + System.Text.Json.JsonSerializer.Serialize(
                        Snapshot(overlay).Where(w => w.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase))
                            .Select(w => new { Handle = "0x" + w.Handle.ToInt64().ToString("X"), w.ProcessId, w.Title, w.ProcessPath, w.IconKey })));
                    Console.WriteLine("EDGE_FIRST_INDEX=" + System.Text.Json.JsonSerializer.Serialize(
                        Snapshot(overlay).Select((window, index) => new { window.ProcessName, Index = index })
                            .Where(window => window.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase))));
                    Console.WriteLine("EDGE_DIAGNOSTICS=" + System.Text.Json.JsonSerializer.Serialize(
                        edgeDiagnostic.RootElement.EnumerateArray().Where(w => edgePids.Contains(w.GetProperty("ProcessId").GetUInt32()))));
                    overlay.Cleanup();
                    application.Shutdown(0);
                    return;
                }
                var info = new ProcessStartInfo("dotnet", $"\"{Assembly.GetExecutingAssembly().Location}\" --probe")
                { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                var watch = Stopwatch.StartNew();
                probe = Process.Start(info)!;
                await probe.StandardOutput.ReadLineAsync();
                watch.Restart();
                bool present = await WaitFor(() => Snapshot(overlay).Any(w => w.ProcessId == probe.Id), 1500);
                Console.WriteLine($"mapping after probe ready: {watch.ElapsedMilliseconds}ms");
                Check(present && watch.ElapsedMilliseconds <= 500, "event refresh within 500ms");
                var windows = Snapshot(overlay).Where(w => w.ProcessId == probe.Id).ToList();
                Check(windows.Any(w => w.Title == "P049ProbeOwnedTask"), "owned APPWINDOW included");
                Check(!windows.Any(w => w.Title == "P049ProbeOwnedDialog"), "owned ordinary dialog excluded");
                Check(!windows.Any(w => w.Title == "P049ProbeTool"), "tool window excluded");
                var main = windows.First(w => w.Title == "P049ProbeMain");
                string iconKey = main.IconKey;
                await Command(probe, "icon");
                Check(await WaitFor(() => Snapshot(overlay).Any(w => w.Handle == main.Handle && w.IconKey != iconKey), 1800),
                    "same HWND icon change detected by fallback scan");
                await Command(probe, "minimize");
                await Task.Delay(300);
                Check(Snapshot(overlay).Any(w => w.Handle == main.Handle), "minimized task remains mapped");
                await Command(probe, "restore");
                await Command(probe, "title");
                Check(await WaitFor(() => Snapshot(overlay).Any(w => w.Title == "P049ProbeRenamed"), 500), "title event mapped");
                await Command(probe, "hide");
                Check(await WaitFor(() => !Snapshot(overlay).Any(w => w.Handle == main.Handle), 500), "hidden task removed");
                await Command(probe, "show");
                Check(await WaitFor(() => Snapshot(overlay).Any(w => w.Handle == main.Handle), 500), "shown task restored");
                await Command(probe, "empty");
                Check(await WaitFor(() => Snapshot(overlay).Any(w => w.Handle == main.Handle && w.Title != "P049ProbeRenamed"), 500), "empty title does not drop task");
                var runningMain = Snapshot(overlay).First(w => w.Handle == main.Handle);
                string processExe = System.IO.Path.GetFileName(runningMain.ProcessPath);
                string processName = System.IO.Path.GetFileNameWithoutExtension(processExe);
                string alternateFolderShortcut = System.IO.Path.Combine(System.IO.Path.GetTempPath(), processExe);
                settings.Settings.LauncherItems.Add(new LauncherItem { Name = processName, Path = alternateFolderShortcut });
                typeof(OverlayWindow).GetMethod("RefreshTrackedWindows", Private)!.Invoke(overlay, null);
                Check(await WaitFor(() =>
                    !((List<WindowInfo>)typeof(OverlayWindow).GetField("_trackedWindows", Private)!.GetValue(overlay)!)
                        .Any(w => w.ProcessId == probe.Id), 700), "pinned application reuses running entry by executable identity");
                var mappedPanels = (System.Windows.Controls.StackPanel)typeof(OverlayWindow)
                    .GetField("WindowButtonsPanel", Private)!.GetValue(overlay)!;
                var pinnedPanels = (System.Windows.Controls.StackPanel)typeof(OverlayWindow)
                    .GetField("LauncherButtonsPanel", Private)!.GetValue(overlay)!;
                Check(mappedPanels.Children.OfType<System.Windows.Controls.Button>().Count(b =>
                    b.Tag is WindowInfo w && w.ProcessId == probe.Id) == 0 && pinnedPanels.Children.Count == 1,
                    "two task windows collapse to one pinned icon when shortcut folder differs");
                typeof(OverlayWindow).GetMethod("HandleShellHookMessage", Private)!.Invoke(overlay, new object[] { 0x8006, main.Handle });
                var launchers = (System.Windows.Controls.StackPanel)typeof(OverlayWindow)
                    .GetField("LauncherButtonsPanel", Private)!.GetValue(overlay)!;
                var launcherButton = launchers.Children.OfType<System.Windows.Controls.Button>().First();
                var launcherGrid = (System.Windows.Controls.Grid)launcherButton.Content;
                overlay.Activate();
                await Task.Delay(100);
                var flashRequest = new FlashWindowInfo
                {
                    Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                    Window = main.Handle,
                    Flags = 0x00000002,
                    Count = 3,
                    Timeout = 100
                };
                FlashWindowEx(ref flashRequest);
                var attentionMap = (System.Collections.IDictionary)typeof(OverlayWindow)
                    .GetField("_attentionWindows", Private)!.GetValue(overlay)!;
                bool shellEventReceived = await WaitFor(() => attentionMap.Contains(main.Handle), 1000);
                Check(shellEventReceived, "real FlashWindowEx taskbar request reaches the registered Shell Hook");
                if (!shellEventReceived)
                    typeof(OverlayWindow).GetMethod("HandleShellHookMessage", Private)!.Invoke(overlay, new object[] { 0x8006, main.Handle });
                Check(launcherGrid.Children.OfType<System.Windows.Shapes.Ellipse>().First().Visibility == Visibility.Visible,
                    "flash event from alternate executable path lights the pinned icon");
                typeof(OverlayWindow).GetMethod("ClearAttentionForWindow", Private)!.Invoke(overlay, new object[] { main.Handle });
                launcherButton = launchers.Children.OfType<System.Windows.Controls.Button>().First();
                launcherGrid = (System.Windows.Controls.Grid)launcherButton.Content;
                Check(launcherGrid.Children.OfType<System.Windows.Shapes.Ellipse>().First().Visibility == Visibility.Collapsed,
                    "runtime attention clear preserved");
                settings.Settings.LauncherItems.Clear();
                typeof(OverlayWindow).GetMethod("RefreshTrackedWindows", Private)!.Invoke(overlay, null);
                Check(await WaitFor(() => mappedPanels.Children.OfType<System.Windows.Controls.Button>().Count(b =>
                    b.Tag is WindowInfo w && w.ProcessId == probe.Id) == 2, 700), "unpin restores both dynamic task windows");
                settings.Settings.LauncherItems.Add(new LauncherItem { Name = "微信", Path = @"C:\Unavailable\微信.exe" });
                typeof(OverlayWindow).GetMethod("RefreshTrackedWindows", Private)!.Invoke(overlay, null);
                await Task.Delay(150);
                Check(pinnedPanels.Children.Count == 1, "non-running pinned app remains visible");
                settings.Settings.LauncherItems.Clear();
                typeof(OverlayWindow).GetMethod("RefreshTrackedWindows", Private)!.Invoke(overlay, null);
                await Task.Delay(150);
                bool activated = WindowService.SwitchToWindow(main.Handle);
                if (activated) Check(true, "window activation retained");
                else Console.WriteLine("UNVERIFIED foreground activation: synthetic test has no physical user click; real click acceptance required");
                int beats = 0;
                heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
                heartbeat.Tick += (_, _) => beats++;
                heartbeat.Start();
                await Command(probe, "hang");
                typeof(OverlayWindow).GetMethod("RefreshTrackedWindows", Private)!.Invoke(overlay, null);
                await Task.Delay(500);
                Check(beats >= 10, "hung application does not block overlay dispatcher");
                heartbeat.Stop();
                var service = (WindowService)typeof(OverlayWindow).GetField("_windowService", Private)!.GetValue(overlay)!;
                using var diagnostic = System.Text.Json.JsonDocument.Parse(service.DiagnosticsJson);
                var reasons = diagnostic.RootElement.EnumerateArray().Select(w => w.GetProperty("Reason").GetString()).ToList();
                Check(reasons.Contains("工具窗口") && reasons.Contains("有属主且未声明任务栏入口"), "diagnostic exclusion reasons recorded");
                Check(!overlay.ShowInTaskbar && overlay.IsVisible, "tray-only identity with visible overlay");
                var rootPanel = (System.Windows.Controls.StackPanel)typeof(OverlayWindow).GetField("RootPanel", Private)!.GetValue(overlay)!;
                Check(rootPanel.Children[0] == typeof(OverlayWindow).GetField("HomeButton", Private)!.GetValue(overlay) &&
                      !rootPanel.Children.OfType<System.Windows.Controls.Button>().Any(b => b.Name == "BackButton"),
                    "home first, previous-task control removed");
                foreach (string edge in new[] { "Top", "Left", "Right", "Bottom" })
                {
                    typeof(OverlayWindow).GetMethod("SetDockEdge", Private)!.Invoke(overlay, new object[] { edge });
                    await Task.Delay(100);
                    var native = Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(overlay).Handle).WorkingArea;
                    var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(overlay);
                    double gap = edge switch { "Top" => overlay.Top - native.Top / dpi.DpiScaleY,
                        "Bottom" => native.Bottom / dpi.DpiScaleY - overlay.Top - overlay.ActualHeight,
                        "Left" => overlay.Left - native.Left / dpi.DpiScaleX,
                        _ => native.Right / dpi.DpiScaleX - overlay.Left - overlay.ActualWidth };
                    Check(Math.Abs(gap - 4) < 1, $"actual {edge} gap 4 DIP ({gap:F1})");
                }
                settings.Settings.DockEdge = "Floating";
                overlay.Left = 12; overlay.Top = 200;
                typeof(OverlayWindow).GetMethod("SnapToNearestEdge", Private)!.Invoke(overlay, null);
                Check(settings.Settings.DockEdge == "Left" && Math.Abs(overlay.Top - 200) < 1,
                    "drag snap preserves position along edge");
                overlay.Left = 300; overlay.Top = 300;
                typeof(OverlayWindow).GetMethod("SnapToNearestEdge", Private)!.Invoke(overlay, null);
                Check(settings.Settings.DockEdge == "Floating", "drag away restores floating layout");
                settings.Settings.LayoutOrientation = "Vertical";
                overlay.Left = 12; overlay.Top = 200;
                typeof(OverlayWindow).GetMethod("SnapToNearestEdge", Private)!.Invoke(overlay, null);
                Check(settings.Settings.DockEdge == "Left" && settings.Settings.LayoutOrientation == "Vertical" &&
                    rootPanel.Orientation == System.Windows.Controls.Orientation.Vertical,
                    "manual vertical layout preserved while moving and snapping");
                overlay.Left = 300; overlay.Top = 300;
                typeof(OverlayWindow).GetMethod("SnapToNearestEdge", Private)!.Invoke(overlay, null);
                Check(settings.Settings.DockEdge == "Floating" && rootPanel.Orientation == System.Windows.Controls.Orientation.Vertical,
                    "manual vertical layout preserved while floating");
                typeof(OverlayWindow).GetMethod("OrientationButton_Click", Private)!.Invoke(overlay,
                    new object[] { overlay, new System.Windows.RoutedEventArgs() });
                Check(settings.Settings.LayoutOrientation == "Horizontal" &&
                    rootPanel.Orientation == System.Windows.Controls.Orientation.Horizontal,
                    "orientation toggle switches layout");
                Check(System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>(
                    System.Text.Json.JsonSerializer.Serialize(settings.Settings))?.LayoutOrientation == "Horizontal",
                    "manual orientation persists in settings");
                settings.Settings.IsPositionLocked = true;
                overlay.Left = 10;
                typeof(OverlayWindow).GetMethod("SnapToNearestEdge", Private)!.Invoke(overlay, null);
                Check(settings.Settings.DockEdge == "Floating", "position lock suppresses snapping");
                probe.Kill();
                Check(await WaitFor(() => !Snapshot(overlay).Any(w => w.ProcessId == probe.Id), 700), "destroyed tasks removed");
            }
            catch (Exception ex) { Check(false, ex.ToString()); }
            finally
            {
                heartbeat?.Stop();
                if (probe is { HasExited: false }) probe.Kill();
                overlay.Cleanup();
                application.Shutdown(_failures == 0 ? 0 : 1);
            }
        });
        application.Run();
        Environment.ExitCode = _failures == 0 ? 0 : 1;
    }

    private static List<WindowInfo> Snapshot(OverlayWindow overlay) =>
        (List<WindowInfo>)typeof(OverlayWindow).GetField("_allWindows", Private)!.GetValue(overlay)!;
    private static OverlaySettings JsonOptionsForLegacy() =>
        System.Text.Json.JsonSerializer.Deserialize<OverlaySettings>("{\"DockEdge\":\"Left\"}")!;
    private static async Task<bool> WaitFor(Func<bool> predicate, int timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeout) { if (predicate()) return true; await Task.Delay(20); }
        return predicate();
    }
    private static async Task Command(Process process, string command)
    {
        await process.StandardInput.WriteLineAsync(command);
        await process.StandardInput.FlushAsync();
        await process.StandardOutput.ReadLineAsync();
    }

    private class TaskForm : Forms.Form
    {
        protected override Forms.CreateParams CreateParams
        { get { var p = base.CreateParams; p.ExStyle |= 0x40000; return p; } }
    }
    private static void RunProbe()
    {
        var main = new Forms.Form { Text = "P049ProbeMain", Icon = System.Drawing.SystemIcons.Information, ShowInTaskbar = true };
        var task = new TaskForm { Text = "P049ProbeOwnedTask" };
        var dialog = new Forms.Form { Text = "P049ProbeOwnedDialog", ShowInTaskbar = false };
        var tool = new Forms.Form { Text = "P049ProbeTool", ShowInTaskbar = false, FormBorderStyle = Forms.FormBorderStyle.FixedToolWindow };
        main.Shown += (_, _) =>
        {
            task.Show(main); dialog.Show(main); tool.Show();
            Console.WriteLine("ready"); Console.Out.Flush();
            Task.Run(() =>
            {
                string? command;
                while ((command = Console.ReadLine()) != null)
                {
                    string action = command;
                    main.BeginInvoke(() =>
                    {
                        switch (action)
                        {
                            case "icon": main.Icon = System.Drawing.SystemIcons.Warning; break;
                            case "minimize": main.WindowState = Forms.FormWindowState.Minimized; break;
                            case "restore": main.WindowState = Forms.FormWindowState.Normal; break;
                            case "title": main.Text = "P049ProbeRenamed"; break;
                            case "empty": main.Text = ""; break;
                            case "hide": main.Hide(); break;
                            case "show": main.Show(); break;
                        }
                        Console.WriteLine("done"); Console.Out.Flush();
                        if (action == "hang") Thread.Sleep(3000);
                    });
                }
            });
        };
        Forms.Application.Run(main);
    }
}
