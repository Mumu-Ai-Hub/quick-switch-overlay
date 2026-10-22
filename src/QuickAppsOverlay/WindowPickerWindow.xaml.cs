using System.Windows;
using QuickAppsOverlay.Models;

namespace QuickAppsOverlay;

public partial class WindowPickerWindow : Window
{
    private List<WindowInfo> _allWindows;
    private readonly HashSet<string> _previouslySelectedKeys;

    public List<WindowInfo> SelectedWindows { get; private set; } = new();

    public WindowPickerWindow(List<WindowInfo> availableWindows, List<string> previouslySelectedKeys)
    {
        InitializeComponent();
        _allWindows = availableWindows;
        _previouslySelectedKeys = new HashSet<string>(previouslySelectedKeys);

        // Pre-check previously selected windows
        foreach (var win in _allWindows)
        {
            win.IsSelected = _previouslySelectedKeys.Contains(win.MatchKey);
        }

        WindowList.ItemsSource = _allWindows;
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var filter = SearchBox.Text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(filter))
        {
            WindowList.ItemsSource = _allWindows;
        }
        else
        {
            WindowList.ItemsSource = _allWindows
                .Where(w => w.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            w.ProcessName.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // Preserve current selections
        var selectedKeys = _allWindows.Where(w => w.IsSelected).Select(w => w.MatchKey).ToHashSet();

        // Re-enumerate
        var windowService = new Services.WindowService();
        _allWindows = windowService.GetAllWindows();

        foreach (var win in _allWindows)
        {
            win.IsSelected = selectedKeys.Contains(win.MatchKey);
        }

        SearchBox.Text = string.Empty;
        WindowList.ItemsSource = _allWindows;
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        SelectedWindows = _allWindows.Where(w => w.IsSelected).ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
