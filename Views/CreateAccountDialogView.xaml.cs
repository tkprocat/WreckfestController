using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;
using WreckfestController.Services;

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

        CreateButton.IsEnabled = false;
        try
        {
            var result = await _accounts.CreateAccountAsync(userName, email, PasswordBox.Password);
            if (result.Succeeded)
            {
                DialogHost.Close(DialogIdentifier, userName);
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
            CreateButton.IsEnabled = true;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) =>
        DialogHost.Close(DialogIdentifier, null);

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
