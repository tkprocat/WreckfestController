using System.Windows;
using MaterialDesignThemes.Wpf;
using WreckfestController.Views;

namespace WreckfestController.Services.Desktop;

/// <summary>
/// Service for displaying Material Design themed dialogs instead of standard MessageBox
/// </summary>
public class DialogService
{
    /// <summary>The main window's dialog host, which every desktop dialog shares.</summary>
    public const string RootDialog = "RootDialog";

    private static readonly TimeSpan OpenDialogPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Shows <paramref name="content"/> on the root dialog host once no other dialog is
    /// open there. The host holds one dialog at a time, and DialogHost.Show throws
    /// "DialogHost is already open" for a second one, which crashed the app: an error
    /// raised while a confirmation or the first-admin dialog was up. A dialog that
    /// arrives then now waits its turn. Call it on the UI thread.
    /// </summary>
    public static Task<object?> ShowAsync(object content, DialogOpenedEventHandler? opened = null) =>
        ShowWhenFreeAsync(
            () => DialogHost.IsDialogOpen(RootDialog),
            () => opened == null ? DialogHost.Show(content, RootDialog) : DialogHost.Show(content, RootDialog, opened),
            OpenDialogPollInterval);

    /// <summary>
    /// Waits until <paramref name="isOpen"/> is false, then calls <paramref name="show"/>.
    /// On the UI thread nothing runs between the last check and the show, so a dialog
    /// cannot open in between.
    /// </summary>
    internal static async Task<object?> ShowWhenFreeAsync(Func<bool> isOpen, Func<Task<object?>> show, TimeSpan pollInterval)
    {
        while (isOpen())
        {
            await Task.Delay(pollInterval);
        }

        return await show();
    }

    /// <summary>
    /// Shows an error dialog with Material Design styling
    /// </summary>
    public static async Task ShowErrorAsync(string message, string title = "Error")
    {
        await ShowDialogAsync(message, title, PackIconKind.AlertCircle, "#E53935");
    }

    /// <summary>
    /// Shows a success dialog with Material Design styling
    /// </summary>
    public static async Task ShowSuccessAsync(string message, string title = "Success")
    {
        await ShowDialogAsync(message, title, PackIconKind.CheckCircle, "#51CF66");
    }

    /// <summary>
    /// Shows an info dialog with Material Design styling
    /// </summary>
    public static async Task ShowInfoAsync(string message, string title = "Information")
    {
        await ShowDialogAsync(message, title, PackIconKind.Information, "#1E88E5");
    }

    /// <summary>
    /// Shows a warning dialog with Material Design styling
    /// </summary>
    public static async Task ShowWarningAsync(string message, string title = "Warning")
    {
        await ShowDialogAsync(message, title, PackIconKind.AlertOutline, "#FFA726");
    }

    /// <summary>
    /// Shows a confirmation dialog with Yes/No buttons
    /// </summary>
    public static async Task<bool> ShowConfirmationAsync(string message, string title = "Confirm")
    {
        var viewModel = new ConfirmationDialogViewModel
        {
            Title = title,
            Message = message,
            Icon = PackIconKind.HelpCircle,
            IconColor = "#FFA726"
        };

        var view = new ConfirmationDialogView
        {
            DataContext = viewModel
        };

        var result = await ShowAsync(view);
        return result is true;
    }

    /// <summary>
    /// Shows a generic dialog with custom icon and color
    /// </summary>
    private static async Task ShowDialogAsync(string message, string title, PackIconKind icon, string iconColor)
    {
        var viewModel = new MessageDialogViewModel
        {
            Title = title,
            Message = message,
            Icon = icon,
            IconColor = iconColor
        };

        var view = new MessageDialogView
        {
            DataContext = viewModel
        };

        await ShowAsync(view);
    }
}

/// <summary>
/// View model for message dialogs
/// </summary>
public class MessageDialogViewModel
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public PackIconKind Icon { get; set; }
    public string IconColor { get; set; } = "#1E88E5";
}

/// <summary>
/// View model for confirmation dialogs
/// </summary>
public class ConfirmationDialogViewModel
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public PackIconKind Icon { get; set; }
    public string IconColor { get; set; } = "#1E88E5";
}
