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
    // default browser.
    private void OnLinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
