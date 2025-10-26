using System.Net.WebSockets;
using System.Text;
using WreckfestController.Services;

namespace WreckfestController.WebSockets;

public class ConsoleWebSocketHandler
{
    private readonly WebSocket _webSocket;
    private readonly ServerManager _serverManager;

    public ConsoleWebSocketHandler(WebSocket webSocket, ServerManager serverManager)
    {
        _webSocket = webSocket;
        _serverManager = serverManager;
    }

    public async Task HandleAsync()
    {
        var buffer = new byte[1024 * 4];
        Action<string>? outputCallback = null;

        try
        {
            // Subscribe to server output
            outputCallback = async (message) =>
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    var messageBytes = Encoding.UTF8.GetBytes(message);
                    await _webSocket.SendAsync(
                        new ArraySegment<byte>(messageBytes),
                        WebSocketMessageType.Text,
                        true,
                        CancellationToken.None);
                }
            };

            _serverManager.SubscribeToConsoleOutput(outputCallback);

            // Send initial status
            var status = _serverManager.GetStatus();
            var statusMessage = $"[SYSTEM] Connected to Wreckfest Server Controller. Server is {(status.IsRunning ? "running" : "stopped")}";
            var statusBytes = Encoding.UTF8.GetBytes(statusMessage);
            await _webSocket.SendAsync(
                new ArraySegment<byte>(statusBytes),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);

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
            // Log error if needed
            Console.WriteLine($"WebSocket error: {ex.Message}");
        }
        finally
        {
            if (outputCallback != null)
            {
                _serverManager.UnsubscribeFromConsoleOutput(outputCallback);
            }
        }
    }
}
