using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WreckfestController.Services;

namespace WreckfestController.WebSockets
{
    public class PlayerTrackerWebSocketHandler
    {
        private readonly WebSocket _webSocket;
        private readonly PlayerTracker _playerTracker;

        public PlayerTrackerWebSocketHandler(WebSocket webSocket, PlayerTracker playerTracker)
        {
            _webSocket = webSocket;
            _playerTracker = playerTracker;
        }

        public async Task HandleAsync()
        {
            var buffer = new byte[1024 * 4];
            Action<PlayerTrackerEvent>? playerTrackerCallback = null;

            try
            {
                // Subscribe to server output
                playerTrackerCallback = async (playerTrackerEvent) =>
                {
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        string message = JsonSerializer.Serialize(playerTrackerEvent);
                        var messageBytes = Encoding.UTF8.GetBytes(message);
                        await _webSocket.SendAsync(
                            new ArraySegment<byte>(messageBytes),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                    }
                };

                _playerTracker.SubscribeToPlayerTracker(playerTrackerCallback);

                // Keep connection alive and listen for close
                while (_webSocket.State == WebSocketState.Open)
                {
                    var result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                // WebSocket handlers are instantiated directly without DI, so we can't use ILogger here
                Console.WriteLine($"WebSocket error: {ex.Message}");
            }
            finally
            {
                if (playerTrackerCallback != null)
                {
                    _playerTracker.UnsubscribeFromPlayerTracker(playerTrackerCallback);
                }
            }
        }
    }
}
