using System.Runtime.InteropServices;

namespace QuickAppsOverlay.Services;

internal sealed class WindowEvents : IDisposable
{
    private delegate void EventCallback(IntPtr hook, uint type, IntPtr window, int objectId,
        int childId, uint thread, uint time);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
        EventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);
    private readonly EventCallback _callback;
    private readonly List<IntPtr> _hooks = new();
    public bool IsAvailable => _hooks.Count == 3;

    public WindowEvents(Action changed)
    {
        _callback = (_, type, window, objectId, childId, _, _) =>
        {
            if (window == IntPtr.Zero) return;
            if (type == 3 || type is 0x16 or 0x17 ||
                (objectId == 0 && childId == 0 && type is 0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x800C))
                changed();
        };
        foreach (var range in new[] { (3u, 3u), (0x16u, 0x17u), (0x8000u, 0x800Cu) })
        {
            var hook = SetWinEventHook(range.Item1, range.Item2, IntPtr.Zero, _callback, 0, 0, 2);
            if (hook != IntPtr.Zero) _hooks.Add(hook);
        }
    }

    public void Dispose()
    {
        foreach (var hook in _hooks) UnhookWinEvent(hook);
        _hooks.Clear();
        GC.KeepAlive(_callback);
    }
}
