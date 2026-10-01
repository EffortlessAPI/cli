using Effortless.Cli.Auth;
using Effortless.Cli.Config;
using Effortless.Cli.Options;
using Newtonsoft.Json;

namespace Effortless.Cli.Commands;

public sealed class AuthCommands
{
    private readonly MagicLinkAuth _auth;
    private readonly JwtStore _jwtStore;

    public AuthCommands(
        MagicLinkAuth auth,
        JwtStore jwtStore = null)
    {
        _auth = auth;
        _jwtStore = jwtStore ?? new JwtStore();
    }

    public int Login()
    {
        return _auth.Login();
    }

    public int ProjectLogin(CliInvocation invocation)
    {
        var project = invocation?.Project;
        if (project is null
            && !string.IsNullOrWhiteSpace(invocation?.CurrentDirectory)
            && Directory.Exists(invocation.CurrentDirectory))
        {
            project = Project.ProjectLocator.TryToLoad(
                new DirectoryInfo(invocation.CurrentDirectory),
                updateCurrent: false);
        }

        return _auth.ProjectLogin(project);
    }

    public int Plan(string jwt)
    {
        return _auth.Subscription(jwt);
    }

    public int Logout()
    {
        if (!_jwtStore.IsAuthenticated())
        {
            Console.WriteLine("No active session found.");
            return 0;
        }

        Console.WriteLine(
            $"You're logged in as {_jwtStore.GetStoredEmail()}.");
        Console.Write("Are you sure you want to log out? (y/N): ");
        var response = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (response is "y" or "yes")
        {
            var jwt = _jwtStore.GetStoredJWTToken();
            var refreshToken = _jwtStore.GetStoredRefreshToken();
            _jwtStore.ClearAuthToken();
            Console.WriteLine("Logged out successfully.");
            // Local tokens are gone regardless; tell the service best-effort.
            _auth.LogoutBestEffort(jwt, refreshToken);
        }
        else
        {
            Console.WriteLine("Logout cancelled.");
        }

        return 0;
    }

    public int SetAccountApiKey(CliInvocation invocation)
    {
        var key = KeyFile.GetKey(
            invocation.Options.runAs);
        var apiKey = (invocation.Options.setAccountAPIKey ?? string.Empty)
            .Replace("=", "/", StringComparison.Ordinal);
        var values = apiKey.Split('/');
        if (values[0].Equals(
                "baserow",
                StringComparison.OrdinalIgnoreCase))
        {
            if (values.Length < 3)
            {
                WriteError(
                    "Syntax for \"baserow\": -setAccountAPIKey=baserow/username/password");
                return -1;
            }

            key.APIKeys["baserow"] = JsonConvert.SerializeObject(
                new
                {
                    username = values[1],
                    password = values[2],
                });
        }
        else
        {
            if (values.Length < 2)
            {
                WriteError(
                    "Default Syntax: -setAccountAPIKey=account/KEY");
                return -1;
            }

            key.APIKeys[values[0]] = values[1];
        }

        KeyFile.SetKey(
            key,
            invocation.Options.runAs);
        return 0;
    }

    private static void WriteError(string message)
    {
        var color = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ForegroundColor = color;
    }
}
