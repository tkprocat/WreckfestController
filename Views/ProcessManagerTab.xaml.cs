using System.Collections.ObjectModel;
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

    public ProcessManagerTab(ServerManager serverManager, ILogger<ProcessManagerTab> logger, Action updateWindowTitle)
    {
        InitializeComponent();

        _serverManager = serverManager;
        _logger = logger;
        _updateWindowTitle = updateWindowTitle;

        ProcessListGrid.ItemsSource = _processListItems;
    }

    public void RefreshProcessList()
    {
        try
        {
            var processes = _serverManager.GetRunningWreckfestServers();

            // Update the observable collection
            _processListItems.Clear();
            foreach (var process in processes)
            {
                _processListItems.Add(process);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing process list");
        }
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
        KillProcessButton.IsEnabled = hasSelection;
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
