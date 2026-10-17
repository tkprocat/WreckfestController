using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace WreckfestPortal.Accounts;

/// <summary>
/// <c>WreckfestPortal.exe create-admin &lt;username&gt; &lt;email&gt;</c>: creates a global
/// admin, asking for the password. Nothing reachable over HTTP creates an account without a
/// signed-in admin, so sitting at the portal's PC is the proof of ownership. It also works
/// as recovery when every admin is locked out or has lost their password.
/// </summary>
public static class CreateAdminCommand
{
    public const string Name = "create-admin";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], Name, StringComparison.OrdinalIgnoreCase);

    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output)
    {
        if (args.Length != 3)
        {
            await output.WriteLineAsync($"Usage: WreckfestPortal.exe {Name} <username> <email>");
            await output.WriteLineAsync("Asks for the password, or reads it from the first line of input.");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddOptions<PortalOptions>().Bind(builder.Configuration.GetSection(PortalOptions.Section));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddPortalDatabase();
        // Its own messages are the output; EF's, at appsettings.json's Information level, would bury them.
        builder.Logging.ClearProviders();
        using var host = builder.Build();

        var password = ReadPassword(input, output);
        if (string.IsNullOrEmpty(password))
        {
            await output.WriteLineAsync("No password given.");
            return 1;
        }

        return await CreateAsync(host.Services, args[1], args[2], password, output);
    }

    /// <summary>Creates the admin in the database the services point at. Split out for tests.</summary>
    public static async Task<int> CreateAsync(IServiceProvider services, string userName, string email, string password, TextWriter output)
    {
        await PortalDatabase.MigrateAsync(services);
        using var scope = services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PortalUser>>();
        var user = new PortalUser { UserName = userName.Trim(), Email = email.Trim(), IsAdmin = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                await output.WriteLineAsync(error.Description);
            }

            return 1;
        }

        var database = services.GetRequiredService<PortalDatabase>();
        await output.WriteLineAsync($"Created admin {user.UserName} in {database.Path}.");
        return 0;
    }

    /// <summary>Without echo at a console; the first line otherwise, so a script can pipe it in.</summary>
    private static string? ReadPassword(TextReader input, TextWriter output)
    {
        if (!ReferenceEquals(input, Console.In) || Console.IsInputRedirected)
        {
            return input.ReadLine();
        }

        output.Write("Password: ");
        var password = new StringBuilder();
        for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (password.Length > 0)
                {
                    password.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
            }
        }

        output.WriteLine();
        return password.ToString();
    }
}
