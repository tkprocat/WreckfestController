using System.Windows;
using MaterialDesignThemes.Wpf;

namespace WreckfestController.Services;

/// <summary>
/// Service for displaying Material Design themed dialogs instead of standard MessageBox
/// </summary>
public class DialogService
{
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

        var result = await DialogHost.Show(view, "RootDialog");
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

        await DialogHost.Show(view, "RootDialog");
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
