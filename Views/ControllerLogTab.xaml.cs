using System.Text;
using System.Windows;
using System.Windows.Controls;
using WreckfestController.Services;

namespace WreckfestController.Views;

public partial class ControllerLogTab : UserControl
{
    private readonly StringBuilder _logText = new();
    private int _logEntryCount = 0;
    private const int MaxLogEntries = 500; // Limit to prevent memory issues

    public ControllerLogTab()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Adds a log entry to the display
    /// </summary>
    public void AddLogEntry(string level, string message)
    {
        // Use BeginInvoke to prevent deadlocks when called from background threads
        Dispatcher.BeginInvoke(() =>
        {
            // Limit log entries to prevent memory bloat
            if (_logEntryCount >= MaxLogEntries)
            {
                // Remove first line from StringBuilder
                var text = _logText.ToString();
                var firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    _logText.Clear();
                    _logText.Append(text.Substring(firstNewline + 1));
                    _logEntryCount--;
                }
            }

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _logText.AppendLine($"[{timestamp}] [{level,-5}] {message}");
            _logEntryCount++;

            LogTextBox.Text = _logText.ToString();
            UpdateLogCount();

            // Auto-scroll to bottom if enabled
            if (AutoScrollCheckBox.IsChecked == true)
            {
                LogScrollView.ScrollToEnd();
            }
        });
    }

    private void UpdateLogCount()
    {
        LogCountText.Text = $"({_logEntryCount} entries)";
    }

    private async void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        var result = await DialogService.ShowConfirmationAsync(
            "Are you sure you want to clear the controller log?",
            "Clear Log");

        if (result)
        {
            _logText.Clear();
            _logEntryCount = 0;
            LogTextBox.Text = string.Empty;
            UpdateLogCount();
        }
    }
}
