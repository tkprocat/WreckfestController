using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;
using WreckfestController.Services.Auth;

namespace WreckfestController.Views;

/// <summary>
/// Creates a web UI account from the desktop. Stays open on a validation error, so
/// the user can correct the field instead of retyping everything.
/// </summary>
public partial class CreateAccountDialogView : UserControl
{
    public const string DialogIdentifier = "RootDialog";

    private readonly AccountService _accounts;

    public CreateAccountDialogView(AccountService accounts, string intro)
    {
        InitializeComponent();
        _accounts = accounts;
        IntroText.Text = intro;
        Loaded += (_, _) => UserNameTextBox.Focus();
    }

    /// <summary>Shows the dialog. Returns the new username, or null when cancelled.</summary>
    public static async Task<string?> ShowAsync(AccountService accounts, string intro)
    {
        var result = await DialogHost.Show(new CreateAccountDialogView(accounts, intro), DialogIdentifier);
        return result as string;
    }

    private async void OnCreateClicked(object sender, RoutedEventArgs e)
    {
        var userName = UserNameTextBox.Text.Trim();
        var email = EmailTextBox.Text.Trim();

        if (userName.Length == 0 || email.Length == 0)
        {
            ShowError("Enter a username and an email.");
            return;
        }

        if (PasswordBox.Password != ConfirmPasswordBox.Password)
        {
            ShowError("The passwords do not match.");
            return;
        }

        // Cancel is disabled too: closing mid-request would report "cancelled" for an
        // account that is then created anyway.
        SetButtonsEnabled(false);
        try
        {
            var result = await _accounts.CreateAccountAsync(userName, email, PasswordBox.Password);
            if (result.Succeeded)
            {
                CloseDialog(userName);
                return;
            }

            ShowError(string.Join(Environment.NewLine, result.Errors.Select(error => error.Description)));
        }
        catch (Exception ex)
        {
            ShowError($"The account could not be created: {ex.Message}");
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => CloseDialog(null);

    /// <summary>
    /// Closes the dialog unless it is already closed. A second click can arrive after the
    /// first one closed it, and DialogHost.Close then throws, which crashed the app (#190).
    /// </summary>
    private static void CloseDialog(string? result)
    {
        if (DialogHost.IsDialogOpen(DialogIdentifier))
        {
            DialogHost.Close(DialogIdentifier, result);
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        CreateButton.IsEnabled = enabled;
        CancelButton.IsEnabled = enabled;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
