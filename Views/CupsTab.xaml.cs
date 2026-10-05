using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Logging;
using WreckfestController.Data.Cups;
using WreckfestController.Services.Cups;
using WreckfestController.Services.Desktop;

namespace WreckfestController.Views;

/// <summary>
/// Lists cups and activates one on demand. Cups are created and edited in the web UI;
/// activation goes through the same <see cref="CupActivator"/> as the API and the
/// scheduler.
/// </summary>
public partial class CupsTab : UserControl
{
    private readonly CupStore _store;
    private readonly CupActivator _activator;
    private readonly ILogger<CupsTab> _logger;
    private readonly ObservableCollection<CupViewModel> _cups = new();
    private Cup? _selectedCup;

    public CupsTab(
        CupStore store,
        CupActivator activator,
        ILogger<CupsTab> logger)
    {
        InitializeComponent();

        _store = store;
        _activator = activator;
        _logger = logger;

        CupsDataGrid.ItemsSource = _cups;

        _ = LoadCupsAsync();
    }

    private async Task LoadCupsAsync()
    {
        try
        {
            var selectedCupId = _selectedCup?.Id;
            var cups = await _store.ListAsync();

            _cups.Clear();
            foreach (var cup in cups)
            {
                _cups.Add(new CupViewModel(cup));
            }

            _logger.LogDebug("Loaded {Count} cups", _cups.Count);

            if (selectedCupId is { } id && _cups.FirstOrDefault(c => c.Cup.Id == id) is { } reselect)
            {
                CupsDataGrid.SelectedItem = reselect;
            }
        }
        catch (Exception ex)
        {
            // Recovery mode: the database banner already says why.
            _logger.LogError(ex, "Could not load cups");
        }
    }

    private void OnCupSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CupsDataGrid.SelectedItem is CupViewModel selected)
        {
            _selectedCup = selected.Cup;
            ShowCupDetails(selected);
            ActivateButton.IsEnabled = true;
        }
        else
        {
            _selectedCup = null;
            HideCupDetails();
            ActivateButton.IsEnabled = false;
        }
    }

    private void ShowCupDetails(CupViewModel selected)
    {
        NoSelectionText.Visibility = Visibility.Collapsed;
        DetailsContent.Visibility = Visibility.Visible;

        var cup = selected.Cup;
        CupNameText.Text = cup.IsActive
            ? $"{cup.Name} ({(cup.Phase == CupPhase.Warmup ? "warming up" : "active")})"
            : cup.Name;
        var window = string.Join(", ", new[]
        {
            cup.WarmupTime is not null ? $"warmup from {Controllers.CupRules.Format(cup.WarmupTime)}" : null,
            cup.EndTime is not null ? $"ends {Controllers.CupRules.Format(cup.EndTime)}" : null,
        }.Where(part => part is not null));
        StartTimeText.Text = window.Length == 0 ? selected.Next : $"{selected.Next} ({window}, {cup.TimeZone})";
        ServerNameText.Text = cup.ServerConfig?.ServerName ?? "N/A";
        ScoringText.Text =
            $"Session mode: {cup.SessionMode ?? "server's own"}\nGrid order: {cup.GridOrder ?? "server's own"}";

        var deployed = CupStore.ToRestartEvent(cup);
        TracksText.Text = deployed.Tracks.Count > 0
            ? string.Join("\n", deployed.Tracks.Select((t, i) => $"{i + 1}. {t.Track}"))
            : "No tracks defined";

        RecurringText.Text = selected.RepeatSchedule;
    }

    private void HideCupDetails()
    {
        NoSelectionText.Visibility = Visibility.Visible;
        DetailsContent.Visibility = Visibility.Collapsed;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await LoadCupsAsync();

    private async void OnActivateClicked(object sender, RoutedEventArgs e)
    {
        var cupToActivate = _selectedCup;
        if (cupToActivate == null)
            return;

        try
        {
            var confirmed = await DialogService.ShowConfirmationAsync(
                $"This will initiate a smart restart to activate cup:\n\n" +
                $"'{cupToActivate.Name}'\n\n" +
                $"The server will send countdown warnings to players and restart at the next lobby.\n\n" +
                $"Continue?",
                "Confirm Cup Activation");

            if (!confirmed)
                return;

            ActivateButton.IsEnabled = false;
            _logger.LogInformation("Manually activating cup: {CupName} (ID: {CupId})", cupToActivate.Name, cupToActivate.Id);

            var result = await _activator.ActivateAsync(
                cupToActivate.Id,
                _ => Dispatcher.InvokeAsync(LoadCupsAsync));

            switch (result)
            {
                case ActivationResult.Started:
                    await DialogService.ShowSuccessAsync(
                        "Cup activation initiated!\n\n" +
                        "The server will begin the countdown process and restart at the next opportunity.",
                        "Activation Started");
                    break;
                case ActivationResult.Busy:
                    await DialogService.ShowWarningAsync(
                        "A server restart is already in progress. Please wait for it to complete.",
                        "Cannot Activate");
                    break;
                case ActivationResult.AlreadyActive:
                    await DialogService.ShowWarningAsync($"'{cupToActivate.Name}' is already active.", "Cannot Activate");
                    break;
                case ActivationResult.NotFound:
                    await DialogService.ShowWarningAsync($"'{cupToActivate.Name}' has been deleted.", "Cannot Activate");
                    break;
            }

            await LoadCupsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error activating cup");
            await DialogService.ShowErrorAsync($"Failed to activate cup: {ex.Message}");
        }
        finally
        {
            ActivateButton.IsEnabled = _selectedCup != null;
        }
    }
}

public class CupViewModel
{
    public CupViewModel(Cup cup)
    {
        Cup = cup;
        Name = cup.IsActive ? $"{cup.Name} (active)" : cup.Name;
        Next = cup.NextOccurrence is { } next ? next.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "Finished";
        Last = cup.LastOccurrence is { } last
            ? $"{cup.LastOutcome} {last.ToLocalTime():yyyy-MM-dd HH:mm}"
            : "-";
        TrackCount = CupStore.ToRestartEvent(cup).Tracks.Count;
        RepeatSchedule = cup.Repeat is null
            ? "One-time"
            : $"{CupRecurrence.Describe(cup.Repeat)} ({cup.TimeZone})";
    }

    public Cup Cup { get; }
    public string Name { get; }

    /// <summary>The next occurrence in this machine's local time.</summary>
    public string Next { get; }

    /// <summary>
    /// How the last occurrence ended, such as "Missed 2026-10-02 20:00". A missed, failed
    /// or cancelled occurrence is not retried; activate the cup to run it anyway.
    /// </summary>
    public string Last { get; }

    public int TrackCount { get; }
    public string RepeatSchedule { get; }
}
