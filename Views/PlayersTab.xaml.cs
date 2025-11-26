using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using WreckfestController.Models;
using WreckfestController.Services;

namespace WreckfestController.Views;

public partial class PlayersTab : UserControl
{
    private readonly PlayerTracker _playerTracker;
    private readonly ServerManager _serverManager;
    private readonly ILogger<PlayersTab> _logger;
    private readonly ObservableCollection<PlayerViewModel> _playerViewModels = new();

    public PlayersTab(PlayerTracker playerTracker, ServerManager serverManager, ILogger<PlayersTab> logger)
    {
        InitializeComponent();

        _playerTracker = playerTracker;
        _serverManager = serverManager;
        _logger = logger;

        PlayersContainer.ItemsSource = _playerViewModels;

        // Subscribe to player updates
        _playerTracker.PlayerEvent += OnPlayerEvent;

        // Initial load
        RefreshPlayerList();
    }

    private void OnPlayerEvent(PlayerTrackerEvent playerEvent)
    {
        // Refresh the player list on any player event
        // Use BeginInvoke to prevent deadlocks when called from background threads
        Dispatcher.BeginInvoke(() => RefreshPlayerList());
    }

    private void RefreshPlayerList()
    {
        try
        {
            var onlinePlayers = _playerTracker.GetPlayers();
            var (realPlayers, bots) = _playerTracker.GetPlayerCount();

            // Update header
            PlayerCountText.Text = $"{realPlayers} player(s) online ({bots} bots)";

            // Update player list
            _playerViewModels.Clear();
            foreach (var player in onlinePlayers)
            {
                _playerViewModels.Add(new PlayerViewModel(player));
            }

            _logger.LogDebug("Player list refreshed: {Count} players", onlinePlayers.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing player list");
        }
    }

    private void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        // Request a list command from the server to get fresh data
        _playerTracker.RequestListCommand(force: true);

        // Also refresh the UI immediately with current data
        RefreshPlayerList();
    }

    private async void OnKickClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int slot)
        {
            _logger.LogInformation("Kicking player in slot {Slot}", slot);
            var result = await _serverManager.SendCommandAsync($"/kick {slot}");
            if (!result.Success)
            {
                _logger.LogWarning("Failed to kick player: {Message}", result.Message);
            }
            // Refresh player list after action
            _playerTracker.RequestListCommand(force: true);
        }
    }

    private async void OnBanClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int slot)
        {
            _logger.LogInformation("Banning player in slot {Slot}", slot);
            var result = await _serverManager.SendCommandAsync($"/ban {slot}");
            if (!result.Success)
            {
                _logger.LogWarning("Failed to ban player: {Message}", result.Message);
            }
            // Refresh player list after action
            _playerTracker.RequestListCommand(force: true);
        }
    }

    private async void OnPromoteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int slot)
        {
            _logger.LogInformation("Promoting player in slot {Slot} to moderator", slot);
            var result = await _serverManager.SendCommandAsync($"/op {slot}");
            if (!result.Success)
            {
                _logger.LogWarning("Failed to promote player: {Message}", result.Message);
            }
            // Refresh player list after action
            _playerTracker.RequestListCommand(force: true);
        }
    }

    private async void OnDemoteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is int slot)
        {
            _logger.LogInformation("Demoting player in slot {Slot}", slot);
            var result = await _serverManager.SendCommandAsync($"/demote {slot}");
            if (!result.Success)
            {
                _logger.LogWarning("Failed to demote player: {Message}", result.Message);
            }
            // Refresh player list after action
            _playerTracker.RequestListCommand(force: true);
        }
    }
}

/// <summary>
/// View model for displaying player information in the UI
/// </summary>
public class PlayerViewModel
{
    private static readonly SolidColorBrush OnlineColor = new(Color.FromRgb(0x51, 0xCF, 0x66));

    public string Name { get; set; }
    public int? Slot { get; set; }
    public string SlotInfo { get; set; }
    public string JoinTimeText { get; set; }
    public SolidColorBrush StatusColor { get; set; }
    public Visibility IsAdminVisible { get; set; }
    public Visibility IsBotVisible { get; set; }

    // Action button visibility - hide for bots, show different options based on admin status
    public Visibility CanKickVisible { get; set; }
    public Visibility CanBanVisible { get; set; }
    public Visibility CanPromoteVisible { get; set; }
    public Visibility CanDemoteVisible { get; set; }

    public PlayerViewModel(Player player)
    {
        Name = player.Name;
        Slot = player.Slot;

        // Build slot info
        if (player.Slot.HasValue)
        {
            SlotInfo = $"Slot {player.Slot.Value}";
        }
        else
        {
            SlotInfo = "Slot unknown";
        }

        // Format join time
        var timeSinceJoin = DateTime.Now - player.JoinedAt;
        if (timeSinceJoin.TotalMinutes < 1)
        {
            JoinTimeText = "Just joined";
        }
        else if (timeSinceJoin.TotalHours < 1)
        {
            JoinTimeText = $"{(int)timeSinceJoin.TotalMinutes}m ago";
        }
        else
        {
            JoinTimeText = $"{(int)timeSinceJoin.TotalHours}h {(int)timeSinceJoin.Minutes}m ago";
        }

        StatusColor = OnlineColor;

        // Show admin/bot badges
        IsAdminVisible = player.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        IsBotVisible = player.IsBot ? Visibility.Visible : Visibility.Collapsed;

        // Action buttons visibility
        var hasSlot = player.Slot.HasValue;
        var isHuman = !player.IsBot;

        // Kick is available for anyone with a slot (including bots - to make room for humans)
        CanKickVisible = hasSlot ? Visibility.Visible : Visibility.Collapsed;

        // Ban/Promote/Demote only make sense for human players
        CanBanVisible = hasSlot && isHuman ? Visibility.Visible : Visibility.Collapsed;
        CanPromoteVisible = hasSlot && isHuman && !player.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        CanDemoteVisible = hasSlot && isHuman && player.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
    }
}
