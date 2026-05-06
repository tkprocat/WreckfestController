using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Views;

public partial class ProcessManagerTab : UserControl
{
    private readonly ServerManager _serverManager;
    private readonly ILogger<ProcessManagerTab> _logger;
    private readonly ObservableCollection<ServerProcessInfo> _processListItems = new();
    private readonly Action _updateWindowTitle;
    private readonly StringBuilder _hookLogBuffer = new();
    private const int MaxHookLogLines = 300;

    public ProcessManagerTab(ServerManager serverManager, ILogger<ProcessManagerTab> logger, Action updateWindowTitle)
    {
        InitializeComponent();

        _serverManager = serverManager;
        _logger = logger;
        _updateWindowTitle = updateWindowTitle;

        ProcessListGrid.ItemsSource = _processListItems;
        _serverManager.ConsoleHookOutput += OnConsoleHookOutput;
        Unloaded += (_, _) => _serverManager.ConsoleHookOutput -= OnConsoleHookOutput;
    }

    public void RefreshProcessList()
    {
        try
        {
            var selectedProcessId = (ProcessListGrid.SelectedItem as ServerProcessInfo)?.ProcessId;
            var processes = _serverManager.GetRunningWreckfestServers();

            // Update the observable collection
            _processListItems.Clear();
            foreach (var process in processes)
            {
                _processListItems.Add(process);
            }

            if (selectedProcessId.HasValue)
            {
                RestoreSelectedProcess(selectedProcessId.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing process list");
        }
    }

    private void RestoreSelectedProcess(int processId)
    {
        var selectedProcess = _processListItems.FirstOrDefault(process => process.ProcessId == processId);
        if (selectedProcess == null)
            return;

        ProcessListGrid.SelectedItem = selectedProcess;
        ProcessListGrid.ScrollIntoView(selectedProcess);

        Dispatcher.BeginInvoke(() =>
        {
            if (ProcessListGrid.ItemContainerGenerator.ContainerFromItem(selectedProcess) is DataGridRow row)
            {
                row.Focus();
            }
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void OnRefreshProcessListClicked(object sender, RoutedEventArgs e)
    {
        RefreshProcessList();
    }

    private void OnProcessSelected(object sender, SelectionChangedEventArgs e)
    {
        // Enable/disable buttons based on selection
        var hasSelection = ProcessListGrid.SelectedItem != null;
        AttachToProcessButton.IsEnabled = hasSelection;
        InjectIntoProcessButton.IsEnabled = hasSelection;
        KillProcessButton.IsEnabled = hasSelection;
    }

    private void OnConsoleHookOutput(string output)
    {
        Dispatcher.Invoke(() =>
        {
            _hookLogBuffer.AppendLine(output);

            var lines = _hookLogBuffer.ToString().Split('\n');
            if (lines.Length > MaxHookLogLines)
            {
                _hookLogBuffer.Clear();
                _hookLogBuffer.Append(string.Join('\n', lines.Skip(lines.Length - MaxHookLogLines)));
            }

            HookLogOutput.Text = _hookLogBuffer.ToString();
            HookLogOutput.ScrollToEnd();
        });
    }

    private void OnProcessHookOutputChanged(object sender, RoutedEventArgs e)
    {
        _serverManager.ProcessConsoleHookOutput = ProcessHookOutputCheckBox.IsChecked == true;
    }

    private async void OnAttachToProcessClicked(object sender, RoutedEventArgs e)
    {
        if (ProcessListGrid.SelectedItem is not ServerProcessInfo selectedProcess)
            return;

        try
        {
            var result = await _serverManager.AttachToProcessAsync(selectedProcess.ProcessId);

            if (result.Success)
            {
                _updateWindowTitle();
                RefreshProcessList();
                await DialogService.ShowSuccessAsync($"Successfully attached to process {selectedProcess.ProcessId}",
                    "Success");
            }
            else
            {
                await DialogService.ShowErrorAsync(result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error attaching to process");
            await DialogService.ShowErrorAsync(ex.Message);
        }
    }

    private async void OnInjectIntoProcessClicked(object sender, RoutedEventArgs e)
    {
        if (ProcessListGrid.SelectedItem is not ServerProcessInfo selectedProcess)
            return;

        try
        {
            InjectIntoProcessButton.IsEnabled = false;
            ProcessHookOutputCheckBox.IsChecked = true;
            _serverManager.ProcessConsoleHookOutput = true;

            var result = await _serverManager.InjectConsoleHookAsync(selectedProcess.ProcessId);

            if (result.Success)
            {
                await DialogService.ShowSuccessAsync(result.Message, "Injection Complete");
            }
            else
            {
                await DialogService.ShowErrorAsync(result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error injecting console hook into process");
            await DialogService.ShowErrorAsync(ex.Message);
        }
        finally
        {
            InjectIntoProcessButton.IsEnabled = ProcessListGrid.SelectedItem != null;
        }
    }

    private async void OnKillProcessClicked(object sender, RoutedEventArgs e)
    {
        if (ProcessListGrid.SelectedItem is not ServerProcessInfo selectedProcess)
            return;

        var result = await DialogService.ShowConfirmationAsync(
            $"Are you sure you want to kill process {selectedProcess.ProcessId} ({selectedProcess.ConfigFile})?",
            "Confirm Kill Process");

        if (!result)
            return;

        try
        {
            var process = System.Diagnostics.Process.GetProcessById(selectedProcess.ProcessId);
            process.Kill();
            process.WaitForExit(5000);

            RefreshProcessList();
            await DialogService.ShowSuccessAsync($"Process {selectedProcess.ProcessId} killed successfully",
                "Success");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error killing process");
            await DialogService.ShowErrorAsync(ex.Message);
        }
    }

    private void OnStartNewServerClicked(object sender, RoutedEventArgs e)
    {
        // Notify parent to start server
        var mainWindow = Window.GetWindow(this) as MainWindow;
        mainWindow?.StartServerFromProcessManagerTab();
    }
}
