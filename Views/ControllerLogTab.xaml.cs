using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WreckfestController.Views;

public partial class ControllerLogTab : UserControl
{
    private readonly ObservableCollection<LogEntry> _logEntries = new();
    private const int MaxLogEntries = 500; // Limit to prevent memory issues

    public ControllerLogTab()
    {
        InitializeComponent();
        LogContainer.ItemsSource = _logEntries;
    }

    /// <summary>
    /// Adds a log entry to the display
    /// </summary>
    public void AddLogEntry(string level, string message)
    {
        Dispatcher.Invoke(() =>
        {
            // Limit log entries to prevent memory bloat
            if (_logEntries.Count >= MaxLogEntries)
            {
                _logEntries.RemoveAt(0);
            }

            var entry = new LogEntry
            {
                Timestamp = DateTime.Now.ToString("HH:mm:ss.fff"),
                Level = level,
                Message = message,
                LevelColor = GetColorForLevel(level)
            };

            _logEntries.Add(entry);
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
        LogCountText.Text = $"({_logEntries.Count} entries)";
    }

    private string GetColorForLevel(string level)
    {
        return level switch
        {
            "ERROR" => "#FF6B6B",      // Red
            "WARN" => "#FFD43B",       // Yellow
            "INFO" => "#74C0FC",       // Blue
            "DEBUG" => "#868E96",      // Gray
            _ => "#C9D1D9"             // Default text color
        };
    }

    private void OnClearLogClicked(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Are you sure you want to clear the controller log?",
            "Clear Log",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            _logEntries.Clear();
            UpdateLogCount();
        }
    }
}

public class LogEntry
{
    public string Timestamp { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string LevelColor { get; set; } = string.Empty;
}
