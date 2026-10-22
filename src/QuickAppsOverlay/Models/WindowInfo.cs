using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickAppsOverlay.Models;

public class WindowInfo
{
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = string.Empty;
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string ProcessPath { get; set; } = string.Empty;
    public ImageSource? Icon { get; set; }
    public string IconKey { get; set; } = string.Empty;
    public bool IsSelected { get; set; }
    // Runtime-only state; this value is never written to settings.
    public bool IsAttentionNeeded { get; set; }

    /// <summary>
    /// Short display name (truncated title for the overlay buttons).
    /// </summary>
    public string ShortTitle =>
        Title.Length > 25 ? Title[..22] + "..." : Title;

    /// <summary>
    /// Unique key for matching across refreshes (process name + partial title).
    /// </summary>
    public string MatchKey => $"{ProcessName}|{Title}";

    public override bool Equals(object? obj) =>
        obj is WindowInfo other && Handle == other.Handle;

    public override int GetHashCode() => Handle.GetHashCode();
}
