using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuickAppsOverlay.Models;

namespace QuickAppsOverlay.Services;

public readonly record struct CursorSnapshot(int X, int Y, bool IsShowing, bool IsSuppressed, uint LastInputTime);

public class WindowService
{
    private IntPtr _ownHandle = IntPtr.Zero;
    private readonly object _scanLock = new();
    private readonly Dictionary<uint, (DateTime Time, string Name, string Path)> _processCache = new();
    private readonly Dictionary<string, ImageSource?> _exeIcons = new(StringComparer.OrdinalIgnoreCase);
    public string DiagnosticsJson { get; private set; } = "[]";

    private (string Name, string Path) GetProcessIdentity(uint pid)
    {
        if (_processCache.TryGetValue(pid, out var entry) && (DateTime.UtcNow - entry.Time).TotalSeconds < 30)
            return (entry.Name, entry.Path);
        string name = "", path = "";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
            var handle = OpenProcess(0x1000, false, pid);
            if (handle != IntPtr.Zero)
            {
                try { var buffer = new StringBuilder(32768); int length = buffer.Capacity;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref length)) path = buffer.ToString(); }
                finally { CloseHandle(handle); }
            }
        }
        catch { }
        if (path.Length > 0) _processCache[pid] = (DateTime.UtcNow, name, path);
        return (name, path);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder name, int size);

    private ImageSource? CachedExecutableIcon(string path)
    {
        if (path.Length == 0) return null;
        if (_exeIcons.TryGetValue(path, out var image)) return image;
        image = GetExecutableIcon(path);
        if (image != null) _exeIcons[path] = image;
        return image;
    }

    private static string Fingerprint(ImageSource? image)
    {
        if (image is not BitmapSource bitmap) return "none";
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
    }


    public void SetOwnHandle(IntPtr handle)
    {
        _ownHandle = handle;
    }

    public List<WindowInfo> GetAllWindows()
    {
        lock (_scanLock)
        {
            var windows = new List<WindowInfo>();
            var diagnostics = new List<object>();
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                uint pid = 0;
                string className = "";
                int exStyle = 0;
                IntPtr owner = IntPtr.Zero;
                bool visible = false;
                bool cloaked = false;
                (string Name, string Path) identity = ("", "");
                string title = "";
                bool iconAvailable = false;
                string error = "";
                string reason = "";
                try
                {
                    NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                    className = GetWindowClassName(hWnd);
                    exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
                    owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
                    visible = NativeMethods.IsWindowVisible(hWnd);
                    cloaked = IsCloaked(hWnd);

                    bool ownWindow = hWnd == _ownHandle || pid == Environment.ProcessId;
                    bool needsIdentity = !ownWindow && (visible && !cloaked) &&
                        (IsEdgeBrowserClass(className) || IsPotentialTaskWindow(className, exStyle, owner));
                    if (needsIdentity) identity = GetProcessIdentity(pid);

                    reason = ownWindow ? "本工具" : ExclusionReason(visible, cloaked, className, exStyle, owner);
                    if (reason.Length > 0 && IsEdgeTaskWindow(identity.Name, identity.Path, className, exStyle))
                        reason = "";

                    if (reason.Length == 0)
                    {
                        try { title = GetWindowTitle(hWnd); }
                        catch (Exception ex) { error = ex.GetType().Name; }
                        ImageSource? icon = null;
                        try { icon = GetWindowIcon(hWnd) ?? CachedExecutableIcon(identity.Path); }
                        catch (Exception ex) { error = ex.GetType().Name; }
                        iconAvailable = icon != null;
                        windows.Add(new WindowInfo { Handle = hWnd, Title = title.Length > 0 ? title : identity.Name,
                            ProcessId = pid, ProcessName = identity.Name, ProcessPath = identity.Path,
                            Icon = icon, IconKey = SafeFingerprint(icon) });
                    }
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name;
                    // Isolate a failing window so one unusual app cannot abort the full task scan.
                    if (reason.Length == 0) reason = "窗口信息读取失败";
                }

                diagnostics.Add(new
                {
                    Handle = hWnd.ToInt64(),
                    ProcessId = pid,
                    ProcessName = identity.Name,
                    ProcessPath = identity.Path,
                    WindowClass = className,
                    ExtendedStyle = $"0x{unchecked((uint)exStyle):X8}",
                    Owner = owner.ToInt64(),
                    Visible = visible,
                    Cloaked = cloaked,
                    Included = reason.Length == 0,
                    Reason = reason,
                    HasTitle = title.Length > 0,
                    HasIcon = iconAvailable,
                    Error = error
                });
                return true;
            }, IntPtr.Zero);
            var activePids = windows.Select(w => w.ProcessId).ToHashSet();
            foreach (var pid in _processCache.Keys.Where(p => !activePids.Contains(p)).ToList()) _processCache.Remove(pid);
            DiagnosticsJson = System.Text.Json.JsonSerializer.Serialize(diagnostics,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var activePaths = windows.Select(w => w.ProcessPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _exeIcons.Keys.Where(p => !activePaths.Contains(p)).ToList()) _exeIcons.Remove(path);
            return OrderForegroundFirst(windows, NativeMethods.GetForegroundWindow());
        }
    }

    internal static List<WindowInfo> OrderForegroundFirst(IEnumerable<WindowInfo> windows, IntPtr foregroundWindow) =>
        windows.OrderBy(w => w.Handle == foregroundWindow ? 0 : 1)
            .ThenBy(w => w.Handle.ToInt64()).ToList();

    internal static bool IsEdgeTaskWindow(string processName, string processPath, string className, int exStyle)
    {
        if (!IsEdgeBrowserClass(className) || (exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0) return false;
        var name = Path.GetFileNameWithoutExtension(processName);
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(name, "msedge", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEdgeBrowserClass(string className) =>
        string.Equals(className, "Chrome_WidgetWin_1", StringComparison.Ordinal);

    private static bool IsPotentialTaskWindow(string className, int exStyle, IntPtr owner) =>
        (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0 ||
        ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) == 0 &&
         (exStyle & NativeMethods.WS_EX_NOACTIVATE) == 0 && owner == IntPtr.Zero) ||
        IsEdgeBrowserClass(className);

    private static string SafeFingerprint(ImageSource? image)
    {
        try { return Fingerprint(image); }
        catch { return "none"; }
    }

    public static string GetProcessPathForWindow(IntPtr hWnd)
    {
        return GetProcessIdentityForWindow(hWnd).Path;
    }

    public static (string Name, string Path) GetProcessIdentityForWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return (string.Empty, string.Empty);
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        return GetProcessIdentityStatic(pid);
    }

    public static CursorSnapshot? CaptureCursor()
    {
        var info = new NativeMethods.CursorInfo { Size = Marshal.SizeOf<NativeMethods.CursorInfo>() };
        if (!NativeMethods.GetCursorInfo(ref info)) return null;
        var lastInput = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        uint lastInputTime = NativeMethods.GetLastInputInfo(ref lastInput) ? lastInput.Time : 0;
        return new CursorSnapshot(info.ScreenPosition.X, info.ScreenPosition.Y,
            (info.Flags & NativeMethods.CURSOR_SHOWING) != 0,
            (info.Flags & NativeMethods.CURSOR_SUPPRESSED) != 0, lastInputTime);
    }

    public static bool RepairCursorAfterSwitch(CursorSnapshot? before)
    {
        if (before is null || !before.Value.IsShowing) return false;
        var after = CaptureCursor();
        if (after is null || after.Value.IsSuppressed) return false;

        var currentInput = new NativeMethods.LastInputInfo { Size = (uint)Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        bool noNewUserInput = NativeMethods.GetLastInputInfo(ref currentInput) &&
            currentInput.Time == before.Value.LastInputTime;
        if (noNewUserInput && (after.Value.X != before.Value.X || after.Value.Y != before.Value.Y))
            NativeMethods.SetCursorPos(before.Value.X, before.Value.Y);

        after = CaptureCursor();
        if (after is null || after.Value.IsShowing || after.Value.IsSuppressed) return after?.IsShowing ?? false;

        // Only touch the shared ShowCursor count after GetCursorInfo confirms that the
        // cursor is actually hidden. Raising a negative count to zero makes it visible.
        for (int i = 0; i < 128; i++)
        {
            if (NativeMethods.ShowCursor(true) >= 0) break;
        }
        return CaptureCursor()?.IsShowing ?? false;
    }

    /// <summary>
    /// Filters to only "real" Alt-Tab style windows (what the user sees in task switcher).
    /// </summary>
    private static bool IsAltTabWindow(IntPtr hWnd)
    {
        if (!NativeMethods.IsWindowVisible(hWnd) || IsCloaked(hWnd)) return false;
        var className = GetWindowClassName(hWnd);
        var exStyle = NativeMethods.GetWindowLong(hWnd, NativeMethods.GWL_EXSTYLE);
        var owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
        if (ExclusionReason(true, false, className, exStyle, owner).Length == 0) return true;
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        var identity = GetProcessIdentityStatic(pid);
        return IsEdgeTaskWindow(identity.Name, identity.Path, className, exStyle);
    }

    private static string ExclusionReason(bool visible, bool cloaked, string className, int style, IntPtr owner)
    {
        if (!visible) return "隐藏窗口";
        if (cloaked) return "系统遮蔽窗口";
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return "桌面或系统任务栏";
        if ((style & NativeMethods.WS_EX_APPWINDOW) != 0) return "";
        if ((style & NativeMethods.WS_EX_TOOLWINDOW) != 0) return "工具窗口";
        if ((style & NativeMethods.WS_EX_NOACTIVATE) != 0) return "非任务激活窗口";
        if (owner != IntPtr.Zero) return "有属主且未声明任务栏入口";
        return "";
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        var className = new StringBuilder(256);
        GetClassName(hWnd, className, className.Capacity);
        return className.ToString();
    }

    private static (string Name, string Path) GetProcessIdentityStatic(uint pid)
    {
        string name = "", path = "";
        try
        {
            using var process = Process.GetProcessById((int)pid);
            name = process.ProcessName;
            var handle = OpenProcess(0x1000, false, pid);
            if (handle != IntPtr.Zero)
            {
                try { var buffer = new StringBuilder(32768); int length = buffer.Capacity;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref length)) path = buffer.ToString(); }
                finally { CloseHandle(handle); }
            }
        }
        catch { }
        return (name, path);
    }

    private static bool IsCloaked(IntPtr hWnd)
    {
        int cloaked;
        int hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED,
            out cloaked, Marshal.SizeOf<int>());
        return hr == 0 && cloaked != 0;
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        int length = NativeMethods.GetWindowTextLength(hWnd);
        if (length == 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public static ImageSource? GetWindowIcon(IntPtr hWnd)
    {
        try
        {
            // Try WM_GETICON with small icon first
            IntPtr iconHandle = SendIconMessage(hWnd, NativeMethods.ICON_SMALL2);

            if (iconHandle == IntPtr.Zero)
                iconHandle = SendIconMessage(hWnd, NativeMethods.ICON_SMALL);

            if (iconHandle == IntPtr.Zero)
                iconHandle = SendIconMessage(hWnd, NativeMethods.ICON_BIG);

            // Try class icon
            if (iconHandle == IntPtr.Zero)
                iconHandle = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCL_HICONSM);

            if (iconHandle == IntPtr.Zero)
                iconHandle = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCL_HICON);

            if (iconHandle != IntPtr.Zero)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(
                    iconHandle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
        }
        catch { }

        return null;
    }

    private static IntPtr SendIconMessage(IntPtr hWnd, int iconType)
    {
        var result = IntPtr.Zero;
        NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_GETICON, (IntPtr)iconType,
            IntPtr.Zero, NativeMethods.SMTO_ABORTIFHUNG, 20, out result);
        return result;
    }

    private static ImageSource? GetExecutableIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon == null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch { return null; }
    }

    /// <summary>
    /// Switches focus to the target window, handling minimized windows
    /// and using AttachThreadInput for reliable foreground activation.
    /// </summary>
    public static bool SwitchToWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        try
        {
            // Restore if minimized
            if (NativeMethods.IsIconic(hWnd))
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_RESTORE);
            }

            // Get the foreground window's thread
            var foregroundHwnd = NativeMethods.GetForegroundWindow();
            var foreThread = NativeMethods.GetWindowThreadProcessId(foregroundHwnd, out _);
            var appThread = NativeMethods.GetCurrentThreadId();

            bool attached = false;
            try
            {
                if (foreThread != 0 && foreThread != appThread)
                    attached = NativeMethods.AttachThreadInput(foreThread, appThread, true);
                return NativeMethods.SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) NativeMethods.AttachThreadInput(foreThread, appThread, false);
            }
        }
        catch { return false; }
    }

    /// <summary>
    /// Forces the given window to HWND_TOPMOST.
    /// </summary>
    public static void ReassertTopmost(IntPtr hWnd)
    {
        NativeMethods.SetWindowPos(hWnd, NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    public static IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public static bool IsWindow(IntPtr hWnd) => hWnd != IntPtr.Zero && NativeMethods.IsWindow(hWnd);

    public static void MinimizeAllWindowsExcept(IntPtr keepHandle)
    {
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (hWnd != keepHandle && IsAltTabWindow(hWnd))
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_MINIMIZE);
            return true;
        }, IntPtr.Zero);
    }
}
