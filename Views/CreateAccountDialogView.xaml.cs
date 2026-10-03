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

    /// <summary>This dialog's own session on the shared host, so closing never ends another dialog.</summary>
    private DialogSession? _session;

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
        var view = new CreateAccountDialogView(accounts, intro);
        var result = await DialogHost.Show(view, DialogIdentifier, new DialogOpenedEventHandler((_, args) => view._session = args.Session));
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

        // LATER stays enabled: a request that never answers must not trap the user. If they
        // leave meanwhile and the account is still created, the account list shows it once
        // it next refreshes.
        CreateButton.IsEnabled = false;
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
            CreateButton.IsEnabled = true;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => CloseDialog(null);

    /// <summary>
    /// Closes this dialog unless it has already closed. A second click can arrive after the
    /// first one closed it, and DialogHost.Close then threw, which crashed the app (#190) -
    /// or, with another dialog open on the same host by then, would have closed that one.
    /// </summary>
    private void CloseDialog(string? result)
    {
        if (_session is { IsEnded: false })
        {
            _session.Close(result);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
