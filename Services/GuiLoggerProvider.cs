using Microsoft.Extensions.Logging;
using WreckfestController.Views;

namespace WreckfestController.Services;

/// <summary>
/// Custom logger provider that sends log messages to the GUI Controller Log tab
/// </summary>
public class GuiLoggerProvider : ILoggerProvider
{
    private ControllerLogTab? _logTab;

    public void SetLogTab(ControllerLogTab logTab)
    {
        _logTab = logTab;
    }

    public ILogger CreateLogger(string categoryName)
    {
        return new GuiLogger(categoryName, this);
    }

    public void Dispose()
    {
        // Nothing to dispose
    }

    internal void Log(string level, string categoryName, string message)
    {
        if (_logTab != null)
        {
            // Format: [Category] Message
            var formattedMessage = $"[{GetShortCategoryName(categoryName)}] {message}";
            _logTab.AddLogEntry(level, formattedMessage);
        }
    }

    private static string GetShortCategoryName(string categoryName)
    {
        // Shorten category names for readability
        // "WreckfestController.Services.ServerManager" -> "ServerManager"
        var parts = categoryName.Split('.');
        return parts.Length > 0 ? parts[^1] : categoryName;
    }
}

/// <summary>
/// Custom logger implementation that forwards to GuiLoggerProvider
/// </summary>
public class GuiLogger : ILogger
{
    private readonly string _categoryName;
    private readonly GuiLoggerProvider _provider;

    public GuiLogger(string categoryName, GuiLoggerProvider provider)
    {
        _categoryName = categoryName;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        // Log everything except Trace
        return logLevel >= LogLevel.Debug;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var message = formatter(state, exception);

        // Append exception details if present
        if (exception != null)
        {
            message += $"\nException: {exception.Message}";
        }

        var level = logLevel switch
        {
            LogLevel.Critical => "ERROR",
            LogLevel.Error => "ERROR",
            LogLevel.Warning => "WARN",
            LogLevel.Information => "INFO",
            LogLevel.Debug => "DEBUG",
            _ => "INFO"
        };

        _provider.Log(level, _categoryName, message);
    }
}
