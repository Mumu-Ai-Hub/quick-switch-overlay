using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using QuickAppsOverlay.Models;
using QuickAppsOverlay.Services;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Ellipse = System.Windows.Shapes.Ellipse;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Path = System.IO.Path;
using Brush = System.Windows.Media.Brush;
using Point = System.Windows.Point;

namespace QuickAppsOverlay;

public partial class OverlayWindow : Window
{
    private readonly WindowService _windowService = new();
    private readonly SettingsService _settingsService;
    private readonly List<WindowInfo> _trackedWindows = new();
    private readonly Dictionary<IntPtr, AttentionTarget> _attentionWindows = new();
    private readonly Dictionary<Button, Ellipse> _attentionBadges = new();
    private DispatcherTimer? _topmostTimer;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _attentionTimer;
    private DispatcherTimer? _cursorRepairTimer;
    private WindowEvents? _windowEvents;
    private DispatcherTimer? _eventRefreshTimer;
    private bool _refreshRunning, _refreshPending, _cleaned, _dragging;
    private List<WindowInfo> _allWindows = new();
    private string _lastPinnedSignature = "";
    private readonly Dictionary<string, ImageSource?> _launcherIconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _shortcutCache = new(StringComparer.OrdinalIgnoreCase);

    private string ResolvedLauncherPath(string path)
    {
        if (_shortcutCache.TryGetValue(path, out var target)) return target;
        return _shortcutCache[path] = ResolveLauncherPath(path);
    }
    private IntPtr _hwnd;
    private int _shellHookMessage;
    private bool _isCollapsed;
    private bool _shellHookRegistered;
    private bool _shellHookReceivedFlash;
    private bool _attentionBlinkPhase;

    private readonly record struct AttentionTarget(uint ProcessId, string ProcessName, string ProcessPath)
    {
        public WindowInfo AsWindowInfo() => new()
        {
            ProcessId = ProcessId,
            ProcessName = ProcessName,
            ProcessPath = ProcessPath
        };
    }

    public event Action? OnExitRequested;
    public event Action? OnHideToTrayRequested;

    public OverlayWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        InitializeComponent();
        System.Windows.Application.Current.MainWindow = this;
        Loaded += OverlayWindow_Loaded;
    }

    private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _windowService.SetOwnHandle(_hwnd);

        if (_settingsService.Settings.DockEdge == "Floating")
        {
            Left = _settingsService.Settings.Left;
            Top = _settingsService.Settings.Top;
        }
        OpacitySlider.Value = _settingsService.Settings.Opacity;
        MainBorder.Opacity = _settingsService.Settings.Opacity;
        _isCollapsed = _settingsService.Settings.IsCollapsed;
        LockPositionMenuItem.IsChecked = _settingsService.Settings.IsPositionLocked;

        UpdateCollapsedState();
        RefreshTrackedWindows();
        ApplyDockLayout();
        if (_settingsService.Settings.DockEdge != "Floating") PlaceAtDockEdge();
        RegisterGlobalHotkey();
        StartTimers();
        _eventRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _eventRefreshTimer.Tick += (_, _) => { _eventRefreshTimer.Stop(); RefreshTrackedWindows(); };
        _windowEvents = new WindowEvents(() =>
        {
            if (!_cleaned && !_eventRefreshTimer.IsEnabled) _eventRefreshTimer.Start();
        });
        SizeChanged += (_, _) => { if (!_dragging) PlaceAtDockEdge(); };

        var source = HwndSource.FromHwnd(_hwnd);
        source?.AddHook(WndProc);
        _shellHookMessage = NativeMethods.RegisterWindowMessage("SHELLHOOK");
        _shellHookRegistered = _shellHookMessage != 0 && NativeMethods.RegisterShellHookWindow(_hwnd);
        if (!_shellHookRegistered)
        {
            SetAttentionSyncStatus("!", Brushes.OrangeRed,
                "通知镜像不可用：无法注册 Windows Shell Hook，不能接收任务栏闪烁状态");
        }
        else
        {
            SetAttentionSyncStatus("?", Brushes.Goldenrod,
                "通知镜像未验证：Shell Hook 已注册，等待首次 Windows 任务栏闪烁事件；不读取消息内容");
        }
    }

    private void SetAttentionSyncStatus(string text, Brush foreground, string toolTip)
    {
        AttentionSyncStatusText.Text = text;
        AttentionSyncStatusText.Foreground = foreground;
        AttentionSyncStatusText.ToolTip = toolTip;
    }

    private void StartTimers()
    {
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _topmostTimer.Tick += (_, _) => WindowService.ReassertTopmost(_hwnd);
        _topmostTimer.Start();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshTrackedWindows();
        _refreshTimer.Start();

        _attentionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _attentionTimer.Tick += (_, _) =>
        {
            if (_attentionWindows.Count == 0) return;
            _attentionBlinkPhase = !_attentionBlinkPhase;
            UpdateAttentionPresentation();
        };
        _attentionTimer.Start();
    }

    private void GoHome()
    {
        WindowService.MinimizeAllWindowsExcept(_hwnd);
        Dispatcher.BeginInvoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            WindowService.ReassertTopmost(_hwnd);
        }, DispatcherPriority.ApplicationIdle);
    }

    private void RegisterGlobalHotkey()
    {
        NativeMethods.RegisterHotKey(_hwnd, NativeMethods.HOTKEY_ID,
            NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
            NativeMethods.VK_SPACE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)NativeMethods.WM_HOTKEY && wParam.ToInt32() == NativeMethods.HOTKEY_ID)
        {
            Show();
            WindowService.ReassertTopmost(_hwnd);
            handled = true;
        }
        else if (_shellHookRegistered && msg == _shellHookMessage)
        {
            HandleShellHookMessage(wParam.ToInt32(), lParam);
            handled = true;
        }
        else if (msg is 0x007E or 0x02E0)
        {
            Dispatcher.BeginInvoke(() => { if (!_cleaned) { UpdateScrollConstraints(); PlaceAtDockEdge(); } });
        }
        return IntPtr.Zero;
    }

    private void HandleShellHookMessage(int eventType, IntPtr window)
    {
        if (window == IntPtr.Zero) return;
        if (eventType == NativeMethods.HSHELL_FLASH)
        {
            if (window == _hwnd) return;
            var target = GetAttentionTarget(window);
            if (target.ProcessId == (uint)Environment.ProcessId) return;
            if (!_shellHookReceivedFlash)
            {
                _shellHookReceivedFlash = true;
            }
            if (_attentionWindows.TryGetValue(window, out var existing) && existing == target) return;
            _attentionWindows[window] = target;
            bool mapped = IsAttentionTargetMapped(target);
            SetAttentionSyncStatus(mapped ? "●" : "!", mapped ? Brushes.LightGreen : Brushes.OrangeRed,
                mapped
                    ? $"已收到并匹配任务栏闪烁事件：{target.ProcessName}；不读取消息内容或未读数"
                    : $"已收到任务栏闪烁事件，但没有匹配到快捷栏图标；进程名：{target.ProcessName}。请导出应用同步诊断定位。未读取消息内容");
            RebuildButtons();
        }
        else if (eventType is NativeMethods.HSHELL_WINDOWACTIVATED or NativeMethods.HSHELL_RUDEAPPACTIVATED)
        {
            ClearAttentionForWindow(window);
        }
        else if (eventType == NativeMethods.HSHELL_WINDOWDESTROYED && _attentionWindows.Remove(window))
        {
            RebuildButtons();
        }
    }

    private void ClearAttentionForWindow(IntPtr window)
    {
        var target = GetAttentionTarget(window);
        var remove = _attentionWindows
            .Where(entry => entry.Key == window || SameApplication(entry.Value, target.AsWindowInfo()))
            .Select(entry => entry.Key)
            .ToList();
        if (remove.Count == 0) return;
        foreach (var handle in remove) _attentionWindows.Remove(handle);
        RebuildButtons();
    }

    private AttentionTarget GetAttentionTarget(IntPtr window)
    {
        var known = _allWindows.FirstOrDefault(item => item.Handle == window);
        if (known != null)
            return new AttentionTarget(known.ProcessId, known.ProcessName, NormalizePath(known.ProcessPath));

        NativeMethods.GetWindowThreadProcessId(window, out uint processId);
        var identity = WindowService.GetProcessIdentityForWindow(window);
        return new AttentionTarget(processId, identity.Name, NormalizePath(identity.Path));
    }

    private static bool SameApplication(AttentionTarget target, WindowInfo window) =>
        target.ProcessId != 0 && target.ProcessId == window.ProcessId ||
        AppIdentityMatcher.MatchesProcessIdentity(target.ProcessName, target.ProcessPath,
            window.ProcessName, window.ProcessPath);

    private bool IsAttentionTargetMapped(AttentionTarget target)
    {
        var identity = target.AsWindowInfo();
        return _settingsService.Settings.LauncherItems.Any(launcher =>
                   AppIdentityMatcher.Matches(launcher, ResolvedLauncherPath(launcher.Path), identity)) ||
               _trackedWindows.Any(window => SameApplication(target, window));
    }

    private bool IsWindowAttentionNeeded(WindowInfo window) =>
        _attentionWindows.Any(entry => entry.Key == window.Handle || SameApplication(entry.Value, window));

    private bool IsLauncherAttentionNeeded(LauncherItem launcher)
    {
        var resolvedPath = ResolvedLauncherPath(launcher.Path);
        return _attentionWindows.Values.Any(target =>
            AppIdentityMatcher.Matches(launcher, resolvedPath, target.AsWindowInfo()));
    }

    public List<WindowInfo> GetAvailableWindows() => _windowService.GetAllWindows();

    private async void RefreshTrackedWindows()
    {
        if (_cleaned) return;
        if (_refreshRunning) { _refreshPending = true; return; }
        _refreshRunning = true;
        try
        {
            var allWindows = await Task.Run(() => _windowService.GetAllWindows());
            if (_cleaned) return;
            _allWindows = allWindows;
            ApplyWindowSnapshot(allWindows);
        }
        catch (Exception ex) { Debug.WriteLine($"Window refresh failed: {ex.Message}"); }
        finally
        {
            _refreshRunning = false;
            if (_refreshPending && !_cleaned) { _refreshPending = false; RefreshTrackedWindows(); }
        }
    }

    private void ApplyWindowSnapshot(List<WindowInfo> allWindows)
    {
        foreach (var handle in _attentionWindows.Keys.Where(handle => !WindowService.IsWindow(handle)).ToList())
            _attentionWindows.Remove(handle);

        var pinnedLaunchers = _settingsService.Settings.LauncherItems.ToList();
        var current = allWindows.Where(window =>
                window.Handle != _hwnd &&
                !pinnedLaunchers.Any(item => AppIdentityMatcher.Matches(item, ResolvedLauncherPath(item.Path), window)))
            .ToList();
        foreach (var window in allWindows)
            window.IsAttentionNeeded = IsWindowAttentionNeeded(window);

        var pinnedSignature = string.Join("|", pinnedLaunchers.Select(item =>
                $"{NormalizePath(ResolvedLauncherPath(item.Path))}:{item.Name}").OrderBy(p => p)) + ":" +
            string.Join("|", allWindows.Where(w => pinnedLaunchers.Any(item =>
                    AppIdentityMatcher.Matches(item, ResolvedLauncherPath(item.Path), w)))
                .Select(w => $"{w.Handle}:{w.IconKey}:{w.IsAttentionNeeded}"));
        bool changed = pinnedSignature != _lastPinnedSignature || current.Count != _trackedWindows.Count || current.Where((window, index) =>
            window.Handle != _trackedWindows[index].Handle || window.Title != _trackedWindows[index].Title ||
            window.IsAttentionNeeded != _trackedWindows[index].IsAttentionNeeded ||
            window.ProcessId != _trackedWindows[index].ProcessId ||
            window.ProcessPath != _trackedWindows[index].ProcessPath ||
            window.IconKey != _trackedWindows[index].IconKey).Any();
        _lastPinnedSignature = pinnedSignature;
        if (!changed) return;

        _trackedWindows.Clear();
        _trackedWindows.AddRange(current);
        RebuildButtons();
    }

    private static string NormalizePath(string path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path); }
        catch { return path.Trim(); }
    }

    private static string ResolveLauncherPath(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase)) return path;
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return path;
            shell = Activator.CreateInstance(shellType);
            if (shell == null) return path;
            shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                null, shell, new object[] { path });
            if (shortcut == null) return path;
            var targetPath = shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty,
                null, shortcut, null) as string;
            return string.IsNullOrWhiteSpace(targetPath) ? path : targetPath;
        }
        catch { return path; }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private void RebuildButtons()
    {
        var previousButtons = WindowButtonsPanel.Children.OfType<Button>()
            .Where(b => b.Tag is WindowInfo).ToDictionary(b => ((WindowInfo)b.Tag).Handle);
        WindowButtonsPanel.Children.Clear();
        _attentionBadges.Clear();
        RebuildLauncherButtons();
        Divider.Visibility = _trackedWindows.Count > 0 && _settingsService.Settings.LauncherItems.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

        foreach (var window in _trackedWindows)
        {
            window.IsAttentionNeeded = IsWindowAttentionNeeded(window);
            if (previousButtons.TryGetValue(window.Handle, out var existing) && existing.Tag is WindowInfo old &&
                old.ProcessPath == window.ProcessPath && old.IconKey == window.IconKey)
            {
                existing.Tag = window;
                existing.ToolTip = $"{window.Title}\n{window.ProcessName}\n左键切换；右键固定";
                var grid = (Grid)existing.Content;
                _attentionBadges[existing] = grid.Children.OfType<Ellipse>().Last();
                WindowButtonsPanel.Children.Add(existing);
            }
            else WindowButtonsPanel.Children.Add(CreateWindowButton(window));
        }

        PlaceholderText.Visibility = _trackedWindows.Count == 0 && _settingsService.Settings.LauncherItems.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateCollapsedState();
        UpdateScrollConstraints();
        UpdateAttentionPresentation();
    }

    private void RebuildLauncherButtons()
    {
        LauncherButtonsPanel.Children.Clear();
        foreach (var launcher in _settingsService.Settings.LauncherItems.ToList())
        {
            var path = ResolvedLauncherPath(launcher.Path);
            var running = _allWindows.FirstOrDefault(w => AppIdentityMatcher.Matches(launcher, path, w));
            if (!_launcherIconCache.TryGetValue(launcher.Path, out var icon))
            {
                icon = GetIconSource(launcher.Path) ?? GetIconSource(path);
                _launcherIconCache[launcher.Path] = icon;
            }
            icon = running?.Icon ?? icon;
            var name = Path.GetFileNameWithoutExtension(launcher.Name);
            var content = CreateIconContent(icon, name[..Math.Min(2, name.Length)], out var badge);
            var button = new Button
            {
                Content = content,
                Style = (Style)FindResource("WindowButtonStyle"),
                ToolTip = $"切换到或启动 {launcher.Name}",
                Tag = launcher,
                Width = 32,
                Height = 32,
                Padding = new Thickness(4)
            };
            _attentionBadges[button] = badge;
            button.Click += LauncherButton_Click;
            var menu = new ContextMenu();
            var remove = new MenuItem { Header = $"取消固定“{launcher.Name}”" };
            remove.Click += (_, _) =>
            {
                _settingsService.Settings.LauncherItems.Remove(launcher);
                _settingsService.Save();
                RefreshTrackedWindows();
            };
            menu.Items.Add(remove);
            button.ContextMenu = menu;
            LauncherButtonsPanel.Children.Add(button);
        }
    }

    private static ImageSource? GetIconSource(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private static Grid CreateIconContent(ImageSource? source, string fallback, out Ellipse badge)
    {
        var grid = new Grid { Width = 20, Height = 20 };
        if (source != null)
        {
            grid.Children.Add(new Image { Source = source, Width = 20, Height = 20 });
        }
        else
        {
            grid.Children.Add(new TextBlock
            {
                Text = fallback,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        badge = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Brushes.OrangeRed,
            Stroke = Brushes.White,
            StrokeThickness = 0.75,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed
        };
        grid.Children.Add(badge);
        return grid;
    }

    private void UpdateAttentionPresentation()
    {
        foreach (var pair in _attentionBadges)
        {
            bool attention = pair.Key.Tag switch
            {
                LauncherItem launcher => IsLauncherAttentionNeeded(launcher),
                WindowInfo window => IsWindowAttentionNeeded(window),
                _ => false
            };
            if (pair.Key.Tag is WindowInfo info) info.IsAttentionNeeded = attention;
            pair.Value.Visibility = attention ? Visibility.Visible : Visibility.Collapsed;
            pair.Value.Opacity = _attentionBlinkPhase ? 1.0 : 0.25;
            pair.Key.BorderBrush = attention
                ? (_attentionBlinkPhase ? Brushes.OrangeRed : Brushes.DarkOrange)
                : new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            pair.Key.Background = attention
                ? new SolidColorBrush(Color.FromArgb(_attentionBlinkPhase ? (byte)0x55 : (byte)0x22, 0xFF, 0x45, 0x20))
                : new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
            pair.Key.BorderThickness = attention ? new Thickness(2) : new Thickness(1);
        }
    }

    private void LauncherButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: LauncherItem launcher }) return;
        var resolvedPath = ResolvedLauncherPath(launcher.Path);
        var running = _allWindows.FirstOrDefault(window =>
            AppIdentityMatcher.Matches(launcher, resolvedPath, window));
        if (running != null)
        {
            ClearAttentionForWindow(running.Handle);
            SwitchToWindowAndRepairCursor(running.Handle);
            return;
        }
        try { Process.Start(new ProcessStartInfo(launcher.Path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"无法打开快捷方式：{launcher.Path}\n{ex.Message}", "屏幕快捷切换栏",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddLauncher()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择要固定到快捷栏的应用或快捷方式",
            Filter = "应用和快捷方式 (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        AddPinnedLauncher(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName));
    }

    private void AddPinnedLauncher(string path, string name)
    {
        if (_settingsService.Settings.LauncherItems.Any(item =>
                string.Equals(NormalizePath(item.Path), NormalizePath(path), StringComparison.OrdinalIgnoreCase))) return;
        _settingsService.Settings.LauncherItems.Add(new LauncherItem { Name = name, Path = path });
        _settingsService.Save();
        RefreshTrackedWindows();
        RebuildButtons();
    }

    private Button CreateWindowButton(WindowInfo window)
    {
        var fallback = window.ProcessName.Length > 0 ? window.ProcessName[..1].ToUpperInvariant() : "•";
        var content = CreateIconContent(window.Icon, fallback, out var badge);
        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource("WindowButtonStyle"),
            ToolTip = $"{window.Title}\n{window.ProcessName}\n左键切换；右键固定",
            Tag = window,
            Width = 32,
            Height = 32,
            Padding = new Thickness(4)
        };
        _attentionBadges[button] = badge;
        button.Click += WindowButton_Click;
        if (!string.IsNullOrWhiteSpace(window.ProcessPath))
        {
            var menu = new ContextMenu();
            var pin = new MenuItem { Header = $"固定“{window.ProcessName}”到快捷栏" };
            pin.Click += (_, _) => AddPinnedLauncher(window.ProcessPath, window.ProcessName);
            menu.Items.Add(pin);
            button.ContextMenu = menu;
        }
        return button;
    }

    private void WindowButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WindowInfo window })
        {
            ClearAttentionForWindow(window.Handle);
            SwitchToWindowAndRepairCursor(window.Handle);
        }
    }

    private void SwitchToWindowAndRepairCursor(IntPtr handle)
    {
        var cursorBefore = WindowService.CaptureCursor();
        bool switched = WindowService.SwitchToWindow(handle);
        if (!switched) return;
        _cursorRepairTimer?.Stop();
        int attempts = 0;
        _cursorRepairTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _cursorRepairTimer.Tick += (_, _) =>
        {
            attempts++;
            if (WindowService.GetForegroundWindow() == handle)
            {
                WindowService.RepairCursorAfterSwitch(cursorBefore);
                WindowService.ReassertTopmost(_hwnd);
                _cursorRepairTimer?.Stop();
            }
            else if (attempts >= 20)
            {
                _cursorRepairTimer?.Stop();
            }
        };
        _cursorRepairTimer.Start();
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e) => GoHome();

    private bool IsVerticalLayout => _settingsService.Settings.LayoutOrientation switch
    {
        "Vertical" => true,
        "Horizontal" => false,
        _ => _settingsService.Settings.DockEdge is "Left" or "Right"
    };

    private void OrientationButton_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.Settings.LayoutOrientation = IsVerticalLayout ? "Horizontal" : "Vertical";
        ApplyDockLayout();
        if (_settingsService.Settings.DockEdge != "Floating") PlaceAtDockEdge();
        _settingsService.Save();
    }

    private void HideToTray_Click(object sender, RoutedEventArgs e) => OnHideToTrayRequested?.Invoke();

    private void GripButton_Click(object sender, RoutedEventArgs e)
    {
        _isCollapsed = !_isCollapsed;
        _settingsService.Settings.IsCollapsed = _isCollapsed;
        _settingsService.Save();
        UpdateCollapsedState();
        ApplyDockLayout();
        if (_settingsService.Settings.DockEdge != "Floating") PlaceAtDockEdge();
    }

    private void UpdateCollapsedState()
    {
        WindowButtonsPanel.Visibility = _isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        WindowScrollViewer.Visibility = _isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LauncherButtonsPanel.Visibility = _isCollapsed ? Visibility.Collapsed : Visibility.Visible;
        Divider.Visibility = !_isCollapsed && _trackedWindows.Count > 0 && _settingsService.Settings.LauncherItems.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderText.Visibility = !_isCollapsed && _trackedWindows.Count == 0 && _settingsService.Settings.LauncherItems.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
        OrientationButton.Content = IsVerticalLayout ? "↔" : "↕";
        OrientationButton.ToolTip = IsVerticalLayout ? "切换为横向排列" : "切换为竖向排列";
        GripButton.Content = _isCollapsed ? "▶" : "☰";
        GripButton.ToolTip = _isCollapsed ? "展开窗口和固定应用" : "收起窗口；右键打开设置";
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_settingsService.Settings.IsPositionLocked && e.ClickCount == 1 &&
            e.OriginalSource is not Button && e.OriginalSource is not TextBlock && e.OriginalSource is not Image)
        {
            _dragging = true;
            bool wasVertical = IsVerticalLayout;
            try { DragMove(); } finally { _dragging = false; }
            if (_settingsService.Settings.LayoutOrientation == "Auto")
                _settingsService.Settings.LayoutOrientation = wasVertical ? "Vertical" : "Horizontal";
            SnapToNearestEdge();
            _settingsService.Settings.Left = Left;
            _settingsService.Settings.Top = Top;
            _settingsService.Save();
        }
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MainBorder == null) return;
        MainBorder.Opacity = e.NewValue;
        _settingsService.Settings.Opacity = e.NewValue;
        _settingsService.Save();
    }

    private void LockPosition_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.Settings.IsPositionLocked = LockPositionMenuItem.IsChecked;
        _settingsService.Save();
    }

    private void AddApp_Click(object sender, RoutedEventArgs e) => AddLauncher();

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshTrackedWindows();

    private void DockBottom_Click(object sender, RoutedEventArgs e) => SetDockEdge("Bottom");

    private void DockLeft_Click(object sender, RoutedEventArgs e) => SetDockEdge("Left");

    private void DockTop_Click(object sender, RoutedEventArgs e) => SetDockEdge("Top");

    private void DockRight_Click(object sender, RoutedEventArgs e) => SetDockEdge("Right");

    private void Float_Click(object sender, RoutedEventArgs e)
    {
        _settingsService.Settings.DockEdge = "Floating";
        ApplyDockLayout();
        Left = _settingsService.Settings.Left;
        Top = _settingsService.Settings.Top;
        _settingsService.Save();
    }

    private void SetDockEdge(string edge)
    {
        _settingsService.Settings.DockEdge = edge;
        _settingsService.Settings.LayoutOrientation = "Auto";
        _settingsService.Settings.DockScreen = System.Windows.Forms.Screen.FromHandle(_hwnd).DeviceName;
        _settingsService.Settings.DockOffset = null;
        ApplyDockLayout();
        PlaceAtDockEdge();
        _settingsService.Save();
    }

    private void ApplyDockLayout()
    {
        bool vertical = IsVerticalLayout;
        RootPanel.Orientation = vertical ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal;
        AppButtonsPanel.Orientation = vertical ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal;
        LauncherButtonsPanel.Orientation = vertical ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal;
        WindowButtonsPanel.Orientation = vertical ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal;
        WindowScrollViewer.HorizontalScrollBarVisibility = vertical ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        WindowScrollViewer.VerticalScrollBarVisibility = vertical ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        WindowScrollViewer.Width = double.NaN;
        WindowScrollViewer.Height = double.NaN;
        Divider.Width = vertical ? double.NaN : 1;
        Divider.Height = vertical ? 1 : 20;
        Divider.Margin = vertical ? new Thickness(4, 3, 4, 3) : new Thickness(3, 4, 3, 4);
        Divider.HorizontalAlignment = vertical ? System.Windows.HorizontalAlignment.Stretch : System.Windows.HorizontalAlignment.Center;
        Divider.VerticalAlignment = VerticalAlignment.Center;
        UpdateScrollConstraints();
        UpdateLayout();
    }

    private void UpdateScrollConstraints()
    {
        if (_hwnd == IntPtr.Zero) return;
        var workArea = DockScreen().WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        int staticLength = 4 * 32 + 20;
        bool vertical = IsVerticalLayout;
        WindowScrollViewer.MaxWidth = vertical
            ? double.PositiveInfinity
            : Math.Max(48, workArea.Width / dpi.DpiScaleX - staticLength);
        WindowScrollViewer.MaxHeight = vertical
            ? Math.Max(48, workArea.Height / dpi.DpiScaleY - staticLength)
            : double.PositiveInfinity;
    }

    private System.Windows.Forms.Screen DockScreen() =>
        System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _settingsService.Settings.DockScreen)
        ?? System.Windows.Forms.Screen.FromHandle(_hwnd);

    private Rect WorkArea(System.Windows.Forms.Screen screen)
    {
        var area = screen.WorkingArea;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var origin = transform.Transform(new Point(area.Left, area.Top));
        var end = transform.Transform(new Point(area.Right, area.Bottom));
        return new Rect(origin, end);
    }

    private void SnapToNearestEdge()
    {
        if (_settingsService.Settings.IsPositionLocked) return;
        bool wasVertical = IsVerticalLayout;
        if (_settingsService.Settings.LayoutOrientation == "Auto")
            _settingsService.Settings.LayoutOrientation = wasVertical ? "Vertical" : "Horizontal";
        var screen = System.Windows.Forms.Screen.FromHandle(_hwnd);
        var area = WorkArea(screen);
        string edge = DockGeometry.FindEdge(area, new Rect(Left, Top, ActualWidth, ActualHeight));
        _settingsService.Settings.DockEdge = edge;
        _settingsService.Settings.DockScreen = screen.DeviceName;
        _settingsService.Settings.DockOffset = edge is "Left" or "Right" ? Top - area.Top - 4 : Left - area.Left - 4;
        ApplyDockLayout();
        if (edge != "Floating") PlaceAtDockEdge();
        else { Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - ActualWidth));
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - ActualHeight)); }
    }

    private void PlaceAtDockEdge()
    {
        if (!IsLoaded || _dragging || _settingsService.Settings.DockEdge == "Floating") return;
        var area = WorkArea(DockScreen());
        string edge = _settingsService.Settings.DockEdge;
        double offset = _settingsService.Settings.DockOffset ??
            (edge is "Left" or "Right" ? (area.Height - ActualHeight) / 2 - 4 : (area.Width - ActualWidth) / 2 - 4);
        var point = DockGeometry.Place(area, new System.Windows.Size(ActualWidth, ActualHeight), edge, offset);
        Left = point.X; Top = point.Y;
    }

    private async void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出应用同步诊断", Filter = "JSON (*.json)|*.json",
            FileName = "应用同步诊断.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await Task.Run(() => _windowService.GetAllWindows());
            var attentionWindows = _attentionWindows.Select(entry => new
            {
                Handle = entry.Key.ToInt64(),
                entry.Value.ProcessId,
                entry.Value.ProcessName,
                entry.Value.ProcessPath,
                MatchedPinnedApps = _settingsService.Settings.LauncherItems
                    .Where(launcher => AppIdentityMatcher.Matches(launcher,
                        ResolvedLauncherPath(launcher.Path), entry.Value.AsWindowInfo()))
                    .Select(launcher => launcher.Name).ToArray(),
                MatchedMappedWindow = _trackedWindows.Any(window => SameApplication(entry.Value, window))
            }).ToArray();
            await File.WriteAllTextAsync(dialog.FileName,
                "{\"EventHookAvailable\":" + (_windowEvents?.IsAvailable == true ? "true" : "false") +
                ",\"ShellHookRegistered\":" + (_shellHookRegistered ? "true" : "false") +
                ",\"ShellFlashReceived\":" + (_shellHookReceivedFlash ? "true" : "false") +
                ",\"AttentionWindows\":" + System.Text.Json.JsonSerializer.Serialize(attentionWindows) +
                ",\"Windows\":" + _windowService.DiagnosticsJson + "}");
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "诊断导出失败"); }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => OnExitRequested?.Invoke();

    public void Cleanup()
    {
        _cleaned = true;
        _windowEvents?.Dispose();
        _eventRefreshTimer?.Stop();
        _topmostTimer?.Stop();
        _refreshTimer?.Stop();
        _attentionTimer?.Stop();
        _cursorRepairTimer?.Stop();
        if (_hwnd != IntPtr.Zero) NativeMethods.UnregisterHotKey(_hwnd, NativeMethods.HOTKEY_ID);
        if (_shellHookRegistered && _hwnd != IntPtr.Zero)
            NativeMethods.DeregisterShellHookWindow(_hwnd);
    }
}
