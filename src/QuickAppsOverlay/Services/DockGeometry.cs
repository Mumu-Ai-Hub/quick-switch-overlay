using System.Windows;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace QuickAppsOverlay.Services;

public static class DockGeometry
{
    public static string FindEdge(Rect area, Rect window, double threshold = 16)
    {
        var distances = new[] { ("Left", Math.Abs(window.Left - area.Left)),
            ("Right", Math.Abs(window.Right - area.Right)),
            ("Top", Math.Abs(window.Top - area.Top)),
            ("Bottom", Math.Abs(window.Bottom - area.Bottom)) };
        var nearest = distances.OrderBy(item => item.Item2).First();
        return nearest.Item2 <= threshold ? nearest.Item1 : "Floating";
    }

    public static Point Place(Rect area, Size size, string edge, double offset)
    {
        double x = area.Left + Math.Clamp(offset, 0, Math.Max(0, area.Width - size.Width - 8)) + 4;
        double y = area.Top + Math.Clamp(offset, 0, Math.Max(0, area.Height - size.Height - 8)) + 4;
        return edge switch
        {
            "Left" => new(area.Left + 4, y),
            "Right" => new(Math.Max(area.Left + 4, area.Right - size.Width - 4), y),
            "Top" => new(x, area.Top + 4),
            _ => new(x, Math.Max(area.Top + 4, area.Bottom - size.Height - 4))
        };
    }
}
