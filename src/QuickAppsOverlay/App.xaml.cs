using System.Drawing;
using System.Threading;
using System.Windows;
using QuickAppsOverlay.Services;
using WinForms = System.Windows.Forms;

namespace QuickAppsOverlay;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private WinForms.NotifyIcon? _trayIcon;
    private Icon? _applicationIcon;
    private OverlayWindow? _overlayWindow;
    private SettingsService? _settingsService;
    private bool _isExiting;

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Single-instance enforcement
        _mutex = new Mutex(true, "QuickAppsOverlay_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("快捷栏已经在运行。",
                "屏幕快捷切换栏", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settingsService = new SettingsService();
        _settingsService.Load();

        // Create overlay window
        _overlayWindow = new OverlayWindow(_settingsService);
        _overlayWindow.OnExitRequested += ExitApp;
        _overlayWindow.OnHideToTrayRequested += HideToTray;
        _overlayWindow.Closing += (_, e) =>
        {
            if (_isExiting) return;
            e.Cancel = true;
            _overlayWindow.Hide();
        };
        _overlayWindow.Show();

        // Create system tray icon
        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        var iconStream = typeof(App).Assembly.GetManifestResourceStream("QuickAppsOverlay.AppIcon");
        if (iconStream is null)
        {
            // 仓库未附带二进制图标时回退到系统应用图标；运行 tools\make-icon.ps1 可生成 Assetspp.ico。
            _applicationIcon = (Icon)SystemIcons.Application.Clone();
        }
        else
        {
            using var resourceIcon = new Icon(iconStream);
            _applicationIcon = (Icon)resourceIcon.Clone();
        }
        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = _applicationIcon,
            Visible = true,
            Text = "屏幕快捷切换栏\nCtrl+Alt+空格显示"
        };

        var contextMenu = new WinForms.ContextMenuStrip();
        contextMenu.Items.Add("显示/隐藏快捷栏", null, (_, _) => ToggleOverlay());
        contextMenu.Items.Add(new WinForms.ToolStripSeparator());
        contextMenu.Items.Add("完全退出", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = contextMenu;
        _trayIcon.DoubleClick += (_, _) => ToggleOverlay();
    }

    private void ToggleOverlay()
    {
        if (_overlayWindow == null) return;

        if (_overlayWindow.Visibility == Visibility.Visible && _overlayWindow.WindowState != WindowState.Minimized)
            _overlayWindow.Hide();
        else
        {
            _overlayWindow.Show();
            _overlayWindow.WindowState = WindowState.Normal;
            _overlayWindow.Visibility = Visibility.Visible;
            WindowService.ReassertTopmost(new System.Windows.Interop.WindowInteropHelper(_overlayWindow).Handle);
        }
    }

    private void HideToTray() => _overlayWindow?.Hide();

    private void ExitApp()
    {
        if (_isExiting) return;
        _isExiting = true;
        _overlayWindow?.Cleanup();
        _trayIcon?.Dispose();
        _mutex?.ReleaseMutex();
        Shutdown();
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _mutex?.Dispose();
        _applicationIcon?.Dispose();
    }

    private void Application_SessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _isExiting = true;
    }
}

