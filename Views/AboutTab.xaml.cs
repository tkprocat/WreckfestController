using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;
using WreckfestController.Services.Desktop;

namespace WreckfestController.Views;

public partial class AboutTab : UserControl
{
    public AboutTab()
    {
        InitializeComponent();

        VersionText.Text = AppInfo.Version;
        BuiltText.Text = AppInfo.BuiltUtc is { } built
            ? built.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "unknown";
        RuntimeText.Text = AppInfo.Runtime;

        RepositoryLink.NavigateUri = new Uri(AppInfo.RepositoryUrl);
        RepositoryLinkText.Text = AppInfo.RepositoryUrl;
        IssuesLink.NavigateUri = new Uri(AppInfo.IssuesUrl);

        // Checked each time the tab is shown: a crash folder appears with the first crash.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                ShowCrashStatus();
            }
        };
    }

    private void ShowCrashStatus()
    {
        var reports = CrashLog.Default.Reports();
        var folder = CrashLog.Default.Folder;
        CrashStatusText.Text = reports.Count == 0
            ? $"None recorded. A crash is written to {folder}."
            : $"{reports.Count} report{(reports.Count == 1 ? "" : "s")}, the newest from " +
              $"{reports[0].LastWriteTime:yyyy-MM-dd HH:mm}. In {folder}.";
        OpenCrashFolderButton.IsEnabled = reports.Count > 0;
    }

    // Like the links: a folder the shell will not open must not escape the handler.
    private void OnOpenCrashFolderClicked(object sender, System.Windows.RoutedEventArgs e)
    {
        var folder = CrashLog.Default.Folder;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _ = DialogService.ShowErrorAsync($"The crash folder could not be opened: {ex.Message}\n{folder}");
        }

        ShowCrashStatus();
    }

    // A WPF Hyperlink outside a navigation host does nothing on its own: open the
    // default browser. A launch Windows refuses must not escape the handler, which
    // would close the app; show the address instead.
    private void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _ = DialogService.ShowErrorAsync($"The browser could not be opened. The address is:\n{e.Uri.AbsoluteUri}");
        }
    }
}
