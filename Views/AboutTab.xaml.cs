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
