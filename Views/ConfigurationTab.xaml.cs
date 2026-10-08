using WreckfestController.Services.Hosting;
using WreckfestController.Services.Hosting.Https;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WreckfestController.Data;
using WreckfestController.Models;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Config;
using WreckfestController.Services.Desktop;
using WreckfestController.Services.Hook;
using WreckfestController.Services.ServerControl;
using WreckfestController.Services.Voting;

namespace WreckfestController.Views;

public partial class ConfigurationTab : UserControl
{
    private readonly SettingsService _settingsService;
    private readonly AccountService _accountService;
    private readonly DatabaseState _databaseState;
    private string? _lastBackupPath;
    private readonly ILogger<ConfigurationTab> _logger;
    private UserSettings _currentSettings;

    /// <summary>
    /// What the form was filled from: values and versions taken together. A save sends
    /// only what differs from it, at its versions.
    /// </summary>
    private SettingsSnapshot _loaded = new(new UserSettings(), new SettingsVersions(0, 0, 0));

    public ConfigurationTab(
        SettingsService settingsService,
        AccountService accountService,
        DatabaseState databaseState,
        IApiServer apiServer,
        ControllerInstance controllerInstance,
        ILogger<ConfigurationTab> logger)
    {
        InitializeComponent();

        // Matches the -wfc_controller argument on the servers this controller starts (#201),
        // so they can be told apart in Task Manager when several controllers share a PC.
        ControllerIdText.Text =
            $"Controller id: {controllerInstance.Id}. Servers started here carry {controllerInstance.Argument} on their command line.";

        _settingsService = settingsService;
        _accountService = accountService;
        _databaseState = databaseState;
        _apiServer = apiServer;

        // The web API's state, HTTPS included: broken HTTPS locks the browser out, so this is
        // where it has to be visible. Refreshed while the tab exists.
        ShowApiStatus();
        _apiStatusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _apiStatusTimer.Tick += (_, _) => ShowApiStatus();
        _apiStatusTimer.Start();
        _logger = logger;
        _currentSettings = new UserSettings();

        // Settings live in the database; the file only holds startup settings.
        SettingsPathText.Text =
            $"Saved in the controller database: {_settingsService.GetDatabasePath()}\n" +
            $"Startup settings (API, database path), edited by hand: {_settingsService.GetUserSettingsPath()}";

        // Load current settings
        LoadSettings();
        _ = RefreshAccountsAsync();
        ShowBackupState();

        // Loaded in recovery mode, the form holds the shipped defaults (version 0): load the
        // stored settings once the database is back. A form holding stored settings is left
        // alone, so an edit in progress is not thrown away.
        _settingsService.DatabaseChanged += () => Dispatcher.InvokeAsync(() =>
        {
            if (_loaded.Versions == new SettingsVersions(0, 0, 0))
            {
                LoadSettings();
            }
        });
    }

    private readonly IApiServer _apiServer;
    private readonly System.Windows.Threading.DispatcherTimer _apiStatusTimer;

    /// <summary>Shows where the API listens and how HTTPS stands, or why the API is not running.</summary>
    private void ShowApiStatus()
    {
        ApiStatusText.Text = ApiStatusDescription.Describe(_apiServer.IsRunning, _apiServer.BaseUrl, _apiServer.StartError, _apiServer.HttpsStatus, DateTimeOffset.UtcNow);
        var colour = _apiServer.StartError is not null ? "RedColor"
            : _apiServer.HttpsStatus is { Error: not null } or { ExpiresSoon: true } ? "YellowColor"
            : "TextSecondary";
        ApiStatusText.Foreground = (System.Windows.Media.Brush)FindResource(colour);
    }

    /// <summary>Re-reads the account count. Also called when the database becomes ready.</summary>
    public async Task RefreshAccountsAsync()
    {
        if (!_accountService.IsDatabaseReady)
        {
            AccountsStatusText.Text = "Unavailable until the database is ready.";
            CreateAccountButton.IsEnabled = false;
            return;
        }

        try
        {
            var count = await _accountService.CountUsersAsync();
            const string recovery = "Create another here if every account is locked out or its password is lost.";
            AccountsStatusText.Text = count switch
            {
                0 => "No accounts yet. Create one to sign in to the web UI.",
                1 => $"1 account can sign in to the web UI. {recovery}",
                _ => $"{count} accounts can sign in to the web UI. {recovery}",
            };
            CreateAccountButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not count web accounts");
            AccountsStatusText.Text = "Could not read the accounts. See the Controller Log.";
            CreateAccountButton.IsEnabled = false;
        }
    }

    private async void OnCreateAccountClicked(object sender, RoutedEventArgs e)
    {
        var created = await CreateAccountDialogView.ShowAsync(
            _accountService,
            "Every account is an admin: it can control the server, change settings and manage other accounts from the web UI.");
        if (created is not null)
        {
            _logger.LogInformation("Web account {UserName} created from the desktop app", created);
            ShowStatusMessage($"Account {created} created.", isError: false);
        }

        await RefreshAccountsAsync();
    }

    /// <summary>
    /// Where backups go, and whether one can be made. Also called when the database
    /// becomes ready or fails.
    /// </summary>
    public void ShowBackupState()
    {
        var folder = DatabaseBackup.FolderFor(_databaseState.DatabasePath);
        BackupButton.IsEnabled = _databaseState.IsReady;
        OpenBackupFolderButton.IsEnabled = Directory.Exists(folder);
        BackupStatusText.Text = !_databaseState.IsReady
            ? "Unavailable until the database is ready."
            : _lastBackupPath is not null
                ? $"Backed up to {_lastBackupPath}"
                : $"A consistent copy, made while the controller runs, in {folder}. It holds every account's password hash: keep it private.";
    }

    private async void OnBackupClicked(object sender, RoutedEventArgs e)
    {
        BackupButton.IsEnabled = false;
        try
        {
            var path = _databaseState.DatabasePath;
            _lastBackupPath = await Task.Run(() => DatabaseBackup.Create(path, "manual", DateTimeOffset.Now));
            _logger.LogInformation("Backed up the database to {BackupPath}", _lastBackupPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database backup failed");
            await DialogService.ShowErrorAsync($"The backup failed: {ex.Message}");
        }
        finally
        {
            ShowBackupState();
        }
    }

    private void OnOpenBackupFolderClicked(object sender, RoutedEventArgs e)
    {
        var folder = DatabaseBackup.FolderFor(_databaseState.DatabasePath);
        if (!Directory.Exists(folder))
        {
            ShowBackupState();
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private void LoadSettings()
    {
        try
        {
            _loaded = _settingsService.LoadForEdit();
            _currentSettings = _loaded.Settings;
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
            Vote = new VoteSettings
            {
                Mode = GetSelectedVoteMode(),
                // Legacy flag mirrors Mode so older readers stay consistent.
                Enabled = GetSelectedVoteMode() != VoteModes.Off,
                // Carried over: these have no control here (the web Settings page has them),
                // and the section is saved whole, so anything not set here would be reset.
                DirectCooldownSeconds = _currentSettings.Vote?.DirectCooldownSeconds ?? 30,
                VoteTimeoutSeconds = _currentSettings.Vote?.VoteTimeoutSeconds ?? 30,
                MaxLapsAllowed = _currentSettings.Vote?.MaxLapsAllowed ?? 10,
                MessageDelayMs = _currentSettings.Vote?.MessageDelayMs ?? 250,
                SuppressCommandsDuringRace = _currentSettings.Vote?.SuppressCommandsDuringRace ?? false,
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
            // The form keeps what it saved, at the new versions. Not a fresh read: another
            // editor's newer values would come with versions the form's values do not match.
            _loaded = _settingsService.SaveSettings(settings, _loaded);
            _currentSettings = _loaded.Settings;

            ShowStatusMessage("Settings saved successfully!", isError: false);

            await DialogService.ShowSuccessAsync(
                "Settings saved successfully!\n\nMost changes will take effect immediately.",
                "Settings Saved");
        }
        catch (SettingsConflictException ex)
        {
            // Someone else saved first. Show their values, and let this user redo the change.
            _logger.LogWarning("Settings save refused: {Reason}", ex.Message);
            LoadSettings();
            ShowStatusMessage("Not saved: the settings were changed elsewhere. The form now shows them.", isError: true);
            await DialogService.ShowWarningAsync(
                $"{ex.Message}\n\nThe form now shows the current settings. Make your change again and save.",
                "Changed Elsewhere");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving settings");
            ShowStatusMessage($"Error saving settings: {ex.Message}", isError: true);
            await DialogService.ShowErrorAsync($"Error saving settings: {ex.Message}");
        }
    }

    private void OnReloadClicked(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        ShowStatusMessage("Showing the saved settings.", isError: false);
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
                Vote = new VoteSettings
                {
                    Enabled = true,
                    Mode = VoteModes.Voting,
                    DirectCooldownSeconds = 30,
                    VoteTimeoutSeconds = 30,
                    MaxLapsAllowed = 10
                }
            };

            _currentSettings = defaults;
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
