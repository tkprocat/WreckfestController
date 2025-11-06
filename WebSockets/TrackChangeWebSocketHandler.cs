using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using WreckfestController.Services;

namespace WreckfestController.WebSockets
{
    public class TrackChangeWebSocketHandler
    {
        private readonly WebSocket _webSocket;
        private readonly TrackChangeTracker _trackChangeTracker;

        public TrackChangeWebSocketHandler(WebSocket webSocket, TrackChangeTracker trackChangeTracker)
        {
            _webSocket = webSocket;
            _trackChangeTracker = trackChangeTracker;
        }

        public async Task HandleAsync()
        {
            var buffer = new byte[1024 * 4];
            Action<TrackChangeEvent>? trackChangeCallback = null;

            try
            {
                // Subscribe to track changes
                trackChangeCallback = async (trackChangeEvent) =>
                {
                    if (_webSocket.State == WebSocketState.Open)
                    {
                        string message = JsonSerializer.Serialize(trackChangeEvent);
                        var messageBytes = Encoding.UTF8.GetBytes(message);
                        await _webSocket.SendAsync(
                            new ArraySegment<byte>(messageBytes),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                    }
                };

                _trackChangeTracker.SubscribeToTrackChange(trackChangeCallback);

                // Send current track immediately upon connection
                var currentTrack = _trackChangeTracker.GetCurrentTrack();
                if (!string.IsNullOrEmpty(currentTrack))
                {
                    var initialEvent = new TrackChangeEvent(currentTrack);
                    string message = JsonSerializer.Serialize(initialEvent);
                    var messageBytes = Encoding.UTF8.GetBytes(message);
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(messageBytes),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }

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
                if (trackChangeCallback != null)
                {
                    _trackChangeTracker.UnsubscribeFromTrackChange(trackChangeCallback);
                }
            }
        }
    }
}
