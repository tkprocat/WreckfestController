using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Views;

public partial class ConfigurationTab : UserControl
{
    private readonly SettingsService _settingsService;
    private readonly ILogger<ConfigurationTab> _logger;
    private UserSettings _currentSettings;

    public ConfigurationTab(SettingsService settingsService, ILogger<ConfigurationTab> logger)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _logger = logger;
        _currentSettings = new UserSettings();

        // Display settings file path
        SettingsPathText.Text = _settingsService.GetUserSettingsPath();

        // Load current settings
        LoadSettings();
    }

    private void LoadSettings()
    {
        try
        {
            _currentSettings = _settingsService.LoadSettings();
            PopulateForm(_currentSettings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading settings");
            ShowStatusMessage("Error loading settings", isError: true);
        }
    }

    private void PopulateForm(UserSettings settings)
    {
        // Server settings
        ServerPathTextBox.Text = settings.WreckfestServer?.ServerPath ?? "";
        WorkingDirectoryTextBox.Text = settings.WreckfestServer?.WorkingDirectory ?? "";
        ServerArgumentsTextBox.Text = settings.WreckfestServer?.ServerArguments ?? "";
        LogFilePathTextBox.Text = settings.WreckfestServer?.LogFilePath ?? "";
        EnableOcrCheckBox.IsChecked = settings.WreckfestServer?.EnableOcrPlayerTracking ?? false;

        // Network settings
        WreckfestWebWebhookUrlTextBox.Text = settings.WreckfestWeb?.WebhookBaseUrl ?? "";
        KestrelUrlsTextBox.Text = settings.Kestrel?.Urls ?? "";
    }

    private UserSettings GatherFormData()
    {
        return new UserSettings
        {
            WreckfestServer = new WreckfestServerSettings
            {
                ServerPath = ServerPathTextBox.Text,
                WorkingDirectory = WorkingDirectoryTextBox.Text,
                ServerArguments = ServerArgumentsTextBox.Text,
                LogFilePath = LogFilePathTextBox.Text,
                EnableOcrPlayerTracking = EnableOcrCheckBox.IsChecked ?? false
            },
            WreckfestWeb = new WreckfestWebSettings
            {
                WebhookBaseUrl = WreckfestWebWebhookUrlTextBox.Text
            },
            Kestrel = new KestrelSettings
            {
                Urls = KestrelUrlsTextBox.Text
            }
        };
    }

    private void OnBrowseServerPathClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Wreckfest Server Executable",
            Filter = "Wreckfest Executables (Wreckfest*.exe)|Wreckfest*.exe|All Executables (*.exe)|*.exe|All Files (*.*)|*.*",
            FileName = "Wreckfest_x64.exe"
        };

        // Open in the directory of the current path if it exists
        var currentPath = ServerPathTextBox.Text;
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            if (File.Exists(currentPath))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
                dialog.FileName = Path.GetFileName(currentPath);
            }
            else if (Directory.Exists(currentPath))
            {
                dialog.InitialDirectory = currentPath;
            }
        }

        if (dialog.ShowDialog() == true)
        {
            ServerPathTextBox.Text = dialog.FileName;

            // Auto-populate working directory if empty
            if (string.IsNullOrWhiteSpace(WorkingDirectoryTextBox.Text))
            {
                var directory = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(directory))
                {
                    WorkingDirectoryTextBox.Text = directory;
                }
            }
        }
    }

    private void OnBrowseWorkingDirectoryClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Wreckfest Server Working Directory"
        };

        // Open in the current directory if it exists
        var currentPath = WorkingDirectoryTextBox.Text;
        if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath))
        {
            dialog.InitialDirectory = currentPath;
        }

        if (dialog.ShowDialog() == true)
        {
            WorkingDirectoryTextBox.Text = dialog.FolderName;
        }
    }

    private void OnBrowseLogFilePathClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Log File",
            Filter = "Log Files (*.txt;*.log)|*.txt;*.log|All Files (*.*)|*.*",
            FileName = "log.txt"
        };

        // Open in the directory of the current path if it exists
        var currentPath = LogFilePathTextBox.Text;
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            if (File.Exists(currentPath))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
                dialog.FileName = Path.GetFileName(currentPath);
            }
            else if (Directory.Exists(currentPath))
            {
                dialog.InitialDirectory = currentPath;
            }
        }
        // Fallback to working directory if log path is empty
        else if (!string.IsNullOrWhiteSpace(WorkingDirectoryTextBox.Text) &&
                 Directory.Exists(WorkingDirectoryTextBox.Text))
        {
            dialog.InitialDirectory = WorkingDirectoryTextBox.Text;
        }

        if (dialog.ShowDialog() == true)
        {
            LogFilePathTextBox.Text = dialog.FileName;
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            // Validate required fields
            if (string.IsNullOrWhiteSpace(ServerPathTextBox.Text))
            {
                MessageBox.Show("Server Path is required.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(WorkingDirectoryTextBox.Text))
            {
                MessageBox.Show("Working Directory is required.", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Validate paths exist
            if (!File.Exists(ServerPathTextBox.Text))
            {
                var result = MessageBox.Show(
                    "Server executable not found at specified path. Save anyway?",
                    "File Not Found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            if (!Directory.Exists(WorkingDirectoryTextBox.Text))
            {
                var result = MessageBox.Show(
                    "Working directory not found. Save anyway?",
                    "Directory Not Found",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return;
            }

            // Gather and save settings
            var settings = GatherFormData();
            _settingsService.SaveSettings(settings);
            _currentSettings = settings;

            ShowStatusMessage("Settings saved successfully! Restart the application for changes to take effect.", isError: false);

            MessageBox.Show(
                "Settings saved successfully!\n\nPlease restart the application for changes to take effect.",
                "Settings Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
            ShowStatusMessage($"Error saving settings: {ex.Message}", isError: true);
            MessageBox.Show($"Error saving settings: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnResetClicked(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Reset all settings to defaults? This will clear your current configuration.",
            "Confirm Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            // Load defaults from service
            var defaults = new UserSettings
            {
                WreckfestServer = new WreckfestServerSettings
                {
                    ServerPath = "",
                    ServerArguments = "-s server_config=server_config.cfg",
                    WorkingDirectory = "",
                    LogFilePath = "",
                    EnableOcrPlayerTracking = false
                },
                WreckfestWeb = new WreckfestWebSettings
                {
                    WebhookBaseUrl = "http://localhost:8000/api/webhooks"
                },
                Kestrel = new KestrelSettings
                {
                    Urls = "http://0.0.0.0:5100;https://0.0.0.0:5101"
                }
            };

            PopulateForm(defaults);
            ShowStatusMessage("Settings reset to defaults (not saved yet)", isError: false);
        }
    }

    private void ShowStatusMessage(string message, bool isError)
    {
        StatusMessageText.Text = message;
        StatusMessageText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("ButtonRed")
            : (System.Windows.Media.Brush)FindResource("ButtonGreen");
    }
}
