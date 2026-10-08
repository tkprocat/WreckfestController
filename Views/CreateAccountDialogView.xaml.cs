using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;
using Microsoft.AspNetCore.Identity;
using WreckfestController.Services.Auth;
using WreckfestController.Services.Desktop;

namespace WreckfestController.Views;

/// <summary>
/// Creates a web UI account from the desktop. Stays open on a validation error, so
/// the user can correct the field instead of retyping everything.
/// </summary>
public partial class CreateAccountDialogView : UserControl
{
    private readonly AccountService _accounts;

    /// <summary>This dialog's own session on the shared host, so closing never ends another dialog.</summary>
    private DialogSession? _session;

    /// <summary>The create request in flight, and the username it is for.</summary>
    private (Task<IdentityResult> Request, string UserName)? _pending;

    public CreateAccountDialogView(AccountService accounts, string intro)
    {
        InitializeComponent();
        _accounts = accounts;
        IntroText.Text = intro;
        Loaded += (_, _) => UserNameTextBox.Focus();
    }

    /// <summary>
    /// Shows the dialog. Returns the new username, or null when no account was created.
    /// Left with LATER while a create is still running, it waits for that request, so the
    /// caller confirms and refreshes an account that was created after all.
    /// </summary>
    public static async Task<string?> ShowAsync(AccountService accounts, string intro)
    {
        var view = new CreateAccountDialogView(accounts, intro);
        var result = await DialogService.ShowAsync(view, new DialogOpenedEventHandler((_, args) => view._session = args.Session));
        if (result is string userName)
        {
            return userName;
        }

        if (view._pending is not { } pending)
        {
            return null;
        }

        try
        {
            return (await pending.Request).Succeeded ? pending.UserName : null;
        }
        catch
        {
            // Nothing was created; the error was the dialog's to show, and it is closed.
            return null;
        }
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
        // leave meanwhile, ShowAsync reports what the request did.
        CreateButton.IsEnabled = false;
        try
        {
            var request = _accounts.CreateAccountAsync(userName, email, PasswordBox.Password);
            _pending = (request, userName);
            var result = await request;
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
