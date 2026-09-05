using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WreckfestController.Services;

namespace WreckfestController.Views;

public partial class ControllerLogTab : UserControl, IDisposable
{
    private readonly ControllerLogBuffer _buffer = new();
    private readonly DispatcherTimer _refreshTimer;

    public ControllerLogTab()
    {
        InitializeComponent();
        // One UI timer replaces one dispatcher operation per incoming entry.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();
    }

    public void AddLogEntry(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _buffer.Add($"[{timestamp}] [{level,-5}] {message}");
    }

    private void OnRefreshTick(object? sender, EventArgs e) => RefreshLog();

    private void RefreshLog()
    {
        var entries = _buffer.TakeSnapshot();
        if (entries == null)
            return;

        LogTextBox.Text = entries.Length == 0
            ? string.Empty
            : string.Join(Environment.NewLine, entries) + Environment.NewLine;
        LogCountText.Text = $"({entries.Length} entries)";

        if (AutoScrollCheckBox.IsChecked == true)
            LogScrollView.ScrollToEnd();
    }

    private async void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        var result = await DialogService.ShowConfirmationAsync(
            "Are you sure you want to clear the controller log?",
            "Clear Log");

        if (result)
        {
            _buffer.Clear();
            RefreshLog();
        }
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
        _buffer.Dispose();
    }
}
