# Console Log Webhook Implementation Guide

## Overview
Replace the WebSocket server for console logs with a webhook-based approach that sends batched log lines to Laravel every second.

## Architecture
- **Old**: WebSocket server on port 5100 streaming logs in real-time
- **New**: HTTP webhook that sends batched logs every 1 second to Laravel
- **Benefits**: Simpler, more reliable, less network traffic, no SSL/TLS complexity

## Implementation Steps

### 1. Remove WebSocket Server
Remove or comment out the existing WebSocket server code that listens on `/ws/console`.

### 2. Create Console Log Buffer Class

```csharp
public class ConsoleLogWebhookSender
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;
    private readonly List<string> _logBuffer;
    private readonly object _bufferLock = new object();
    private readonly Timer _flushTimer;

    public ConsoleLogWebhookSender(string webhookUrl)
    {
        _httpClient = new HttpClient();
        _webhookUrl = webhookUrl;
        _logBuffer = new List<string>();

        // Flush logs every 1 second
        _flushTimer = new Timer(FlushLogs, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void AddLog(string logLine)
    {
        lock (_bufferLock)
        {
            _logBuffer.Add(logLine);
        }
    }

    private async void FlushLogs(object state)
    {
        List<string> logsToSend;

        lock (_bufferLock)
        {
            if (_logBuffer.Count == 0)
                return;

            logsToSend = new List<string>(_logBuffer);
            _logBuffer.Clear();
        }

        try
        {
            var payload = new { logs = logsToSend };
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(_webhookUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Failed to send console logs: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error sending console logs webhook: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _flushTimer?.Dispose();
        _httpClient?.Dispose();
    }
}
```

### 3. Initialize the Sender

In your main application startup or wherever you initialize services:

```csharp
// Replace with your actual Laravel URL
var webhookUrl = "https://wreckfest.home.procat.dk/api/webhooks/console-logs";
var consoleLogSender = new ConsoleLogWebhookSender(webhookUrl);
```

### 4. Hook Into Console Output

Wherever you capture console output from the Wreckfest server process, add the log lines to the buffer:

```csharp
// Example: when reading from process output
process.OutputDataReceived += (sender, e) =>
{
    if (!string.IsNullOrEmpty(e.Data))
    {
        // Send to webhook
        consoleLogSender.AddLog(e.Data);

        // Also log locally if needed
        Console.WriteLine(e.Data);
    }
};
```

### 5. Clean Up on Shutdown

Make sure to dispose of the sender when your application shuts down:

```csharp
consoleLogSender.Dispose();
```

## Webhook Endpoint Details

**URL**: `https://wreckfest.home.procat.dk/api/webhooks/console-logs`

**Method**: POST

**Content-Type**: application/json

**Payload Format**:
```json
{
  "logs": [
    "Log line 1",
    "Log line 2",
    "Log line 3"
  ]
}
```

**Expected Response**:
```json
{
  "success": true
}
```

## Configuration Options

You can adjust the flush interval based on your needs:

- **1 second** (recommended): Good balance of real-time feel and network efficiency
- **500ms**: More real-time, slightly more network traffic
- **2 seconds**: Less traffic, still feels responsive for log viewing

To change the interval, modify the Timer initialization:
```csharp
// For 500ms
_flushTimer = new Timer(FlushLogs, null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));

// For 2 seconds
_flushTimer = new Timer(FlushLogs, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
```

## Testing

1. Start your C# WreckfestController
2. Start the Wreckfest server (which generates console output)
3. Open the Laravel admin panel at Server Control page
4. You should see logs appearing in real-time (with ~1 second batching)

## Troubleshooting

**Logs not appearing**:
- Check that the webhook URL is correct and accessible
- Verify Laravel Reverb is running: `php artisan reverb:start`
- Check Laravel logs: `storage/logs/laravel.log`
- Check C# console for any error messages

**SSL/TLS errors**:
- Ensure your webhook URL uses `https://` (not `http://`)
- Your domain should have a valid SSL certificate

**High network traffic**:
- Increase the flush interval (e.g., to 2 seconds)
- Consider implementing a max batch size to prevent huge payloads

## Notes

- The webhook approach is stateless - no persistent connections to manage
- Laravel will broadcast the logs to all connected browsers via Reverb
- The 1-second debounce significantly reduces network requests compared to WebSocket
- No need to worry about WebSocket SSL certificates on the C# side
