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
        WorkingDirectoryTextBox.Text = settings.WreckfestServer?.WorkingDirectory ?? "";

        // For ServerPath and LogFilePath, if they're absolute paths, extract just the filename
        var serverPath = settings.WreckfestServer?.ServerPath ?? "";
        ServerPathTextBox.Text = Path.IsPathRooted(serverPath) ? Path.GetFileName(serverPath) : serverPath;

        var logPath = settings.WreckfestServer?.LogFilePath ?? "";
        LogFilePathTextBox.Text = Path.IsPathRooted(logPath) ? Path.GetFileName(logPath) : logPath;

        ServerArgumentsTextBox.Text = settings.WreckfestServer?.ServerArguments ?? "";

        // SteamCmd settings
        SteamCmdPathTextBox.Text = settings.SteamCmd?.SteamCmdPath ?? "";
        WreckfestAppIdTextBox.Text = settings.SteamCmd?.WreckfestAppId ?? "";

        // Network settings
        WreckfestWebWebhookUrlTextBox.Text = settings.WreckfestWeb?.WebhookBaseUrl ?? "";
        WreckfestWebApiKeyTextBox.Text = settings.WreckfestWeb?.WebhookApiKey ?? "";

        // Voting settings
        SelectVoteMode(VoteModes.Normalize(settings.Vote?.Mode, settings.Vote?.Enabled));
    }

    private UserSettings GatherFormData()
    {
        var workingDir = WorkingDirectoryTextBox.Text;
        var serverExe = ServerPathTextBox.Text;
        var logFile = LogFilePathTextBox.Text;

        // Combine paths if working directory is specified and paths aren't already absolute
        var serverPath = string.IsNullOrWhiteSpace(serverExe) ? "" :
            (!string.IsNullOrWhiteSpace(workingDir) && !Path.IsPathRooted(serverExe)
                ? Path.Combine(workingDir, serverExe)
                : serverExe);

        var logFilePath = string.IsNullOrWhiteSpace(logFile) ? "" :
            (!string.IsNullOrWhiteSpace(workingDir) && !Path.IsPathRooted(logFile)
                ? Path.Combine(workingDir, logFile)
                : logFile);

        return new UserSettings
        {
            WreckfestServer = new WreckfestServerSettings
            {
                ServerPath = serverPath,
                WorkingDirectory = workingDir,
                ServerArguments = ServerArgumentsTextBox.Text,
                LogFilePath = logFilePath,
                OutputMode = ServerOutputModes.InjectedHook
            },
            SteamCmd = new SteamCmdSettings
            {
                SteamCmdPath = SteamCmdPathTextBox.Text,
                WreckfestAppId = WreckfestAppIdTextBox.Text
            },
            WreckfestWeb = new WreckfestWebSettings
            {
                WebhookBaseUrl = WreckfestWebWebhookUrlTextBox.Text,
                WebhookApiKey = WreckfestWebApiKeyTextBox.Text
            },
            Vote = new VoteSettings
            {
                Mode = GetSelectedVoteMode(),
                // Legacy flag mirrors Mode so older readers stay consistent.
                Enabled = GetSelectedVoteMode() != VoteModes.Off,
                // Carried over: these have no UI control, and SaveSettings rewrites the
                // whole file, so anything not set here would be dropped.
                DirectCooldownSeconds = _currentSettings.Vote?.DirectCooldownSeconds ?? 30,
                VoteTimeoutSeconds = _currentSettings.Vote?.VoteTimeoutSeconds ?? 30,
                MaxLapsAllowed = _currentSettings.Vote?.MaxLapsAllowed ?? 10,
                AllowedTracks = _currentSettings.Vote?.AllowedTracks ?? new()
            }
        };
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

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            // Validate required fields
            if (string.IsNullOrWhiteSpace(WorkingDirectoryTextBox.Text))
            {
                await DialogService.ShowWarningAsync("Working Directory is required.", "Validation Error");
                return;
            }

            if (string.IsNullOrWhiteSpace(ServerPathTextBox.Text))
            {
                await DialogService.ShowWarningAsync("Server Executable is required.", "Validation Error");
                return;
            }

            // Validate working directory exists
            if (!Directory.Exists(WorkingDirectoryTextBox.Text))
            {
                var result = await DialogService.ShowConfirmationAsync(
                    "Working directory not found. Save anyway?",
                    "Directory Not Found");

                if (!result)
                    return;
            }

            // Validate server executable exists (combine with working directory)
            var serverExePath = Path.Combine(WorkingDirectoryTextBox.Text, ServerPathTextBox.Text);
            if (!File.Exists(serverExePath))
            {
                var result = await DialogService.ShowConfirmationAsync(
                    $"Server executable not found at:\n{serverExePath}\n\nSave anyway?",
                    "File Not Found");

                if (!result)
                    return;
            }

            // Gather and save settings
            var settings = GatherFormData();
            _settingsService.SaveSettings(settings);
            _currentSettings = settings;

            ShowStatusMessage("Settings saved successfully!", isError: false);

            await DialogService.ShowSuccessAsync(
                "Settings saved successfully!\n\nMost changes will take effect immediately.",
                "Settings Saved");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
            ShowStatusMessage($"Error saving settings: {ex.Message}", isError: true);
            await DialogService.ShowErrorAsync($"Error saving settings: {ex.Message}");
        }
    }

    private async void OnResetClicked(object sender, RoutedEventArgs e)
    {
        var result = await DialogService.ShowConfirmationAsync(
            "Reset all settings to defaults? This will clear your current configuration.",
            "Confirm Reset");

        if (result)
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
                    OutputMode = ServerOutputModes.InjectedHook
                },
                SteamCmd = new SteamCmdSettings
                {
                    SteamCmdPath = "",
                    WreckfestAppId = "361580"
                },
                WreckfestWeb = new WreckfestWebSettings
                {
                    WebhookBaseUrl = "http://localhost:8000/api/webhooks",
                    WebhookApiKey = ""
                },
                Vote = new VoteSettings
                {
                    Enabled = true,
                    Mode = VoteModes.Voting,
                    DirectCooldownSeconds = 30,
                    VoteTimeoutSeconds = 30,
                    MaxLapsAllowed = 10
                }
            };

            PopulateForm(defaults);
            ShowStatusMessage("Settings reset to defaults (not saved yet)", isError: false);
        }
    }

    private void SelectVoteMode(string mode)
    {
        foreach (var item in VoteModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), mode, StringComparison.OrdinalIgnoreCase))
            {
                VoteModeComboBox.SelectedItem = item;
                return;
            }
        }

        // Voting is the middle item; select by index rather than recursing.
        VoteModeComboBox.SelectedIndex = 1;
    }

    private string GetSelectedVoteMode()
    {
        if (VoteModeComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string mode &&
            !string.IsNullOrWhiteSpace(mode))
        {
            return VoteModes.Normalize(mode);
        }

        return VoteModes.Voting;
    }

    private void ShowStatusMessage(string message, bool isError)
    {
        StatusMessageText.Text = message;
        StatusMessageText.Foreground = isError
            ? (System.Windows.Media.Brush)FindResource("ButtonRed")
            : (System.Windows.Media.Brush)FindResource("ButtonGreen");
    }

}
