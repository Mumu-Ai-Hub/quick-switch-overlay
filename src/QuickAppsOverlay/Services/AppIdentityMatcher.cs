using System.IO;
using QuickAppsOverlay.Models;

namespace QuickAppsOverlay.Services;

public static class AppIdentityMatcher
{
    public static bool Matches(LauncherItem launcher, string resolvedLauncherPath, WindowInfo window)
    {
        var launcherPath = NormalizePath(resolvedLauncherPath);
        var launcherNames = new[] { FileName(resolvedLauncherPath), FileName(launcher.Path), FileName(launcher.Name) }
            .Where(name => name.Length > 0).Select(CanonicalName).Where(name => name.Length > 0).ToHashSet();
        return MatchesProcessIdentity(string.Empty, launcherPath, window.ProcessName, window.ProcessPath) ||
               MatchesNames(launcherNames, window.ProcessName, window.ProcessPath);
    }

    public static bool MatchesProcessIdentity(string firstName, string firstPath, string secondName, string secondPath)
    {
        var firstPathNormalized = NormalizePath(firstPath);
        var secondPathNormalized = NormalizePath(secondPath);
        if (firstPathNormalized.Length > 0 && secondPathNormalized.Length > 0 &&
            string.Equals(firstPathNormalized, secondPathNormalized, StringComparison.OrdinalIgnoreCase)) return true;

        var firstNames = new[] { FileName(firstName), FileName(firstPath) }
            .Where(name => name.Length > 0).Select(CanonicalName).Where(name => name.Length > 0).ToHashSet();
        return MatchesNames(firstNames, secondName, secondPath);
    }

    public static string CanonicalName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var token = new string(name.Trim().ToLowerInvariant()
            .Where(c => char.IsLetterOrDigit(c)).ToArray());
        return token switch { "微信" or "weixin" => "wechat", _ => token };
    }

    public static string NormalizePath(string? path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path.Trim()); }
        catch { return path?.Trim() ?? string.Empty; }
    }

    private static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var name = Path.GetFileName(path.Trim());
        return new[] { ".exe", ".lnk" }.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(name) : name;
    }

    private static bool MatchesNames(HashSet<string> expected, string? processName, string? processPath)
    {
        if (expected.Count == 0) return false;
        return new[] { FileName(processName), FileName(processPath) }
            .Where(name => name.Length > 0).Select(CanonicalName).Any(expected.Contains);
    }
}
