using System.Text;
using System.Text.Json;
using Effortless.Cli.E2E.Harness;

namespace Effortless.Cli.E2E.Suites;

public sealed class AuthTests
{
    private const string Email = "a@b.c";
    private const string Code = "123456";
    private const string ExistingEmail = "x@y.z";
    private static readonly string EmailAndCode = $"{Email}{Environment.NewLine}{Code}{Environment.NewLine}";

    [Fact(DisplayName = "auth-login-flow: login signs in with an emailed code")]
    public async Task LoginCallsTheAuthTool()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;

        var result = await cli.Run(
            ["login"],
            sandbox.ProjectPath,
            sandbox,
            stdin: EmailAndCode);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Email: ", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("A sign-in code was sent to a@b.c.", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Code: ", result.Stdout, StringComparison.Ordinal);
        Assert.Contains(
            "Signed in (no tool enforces accounts yet). Signed in as a@b.c.",
            result.Stdout,
            StringComparison.Ordinal);
        var requests = auth.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(("POST", "login"), (requests[0].Method, requests[0].Route));
        Assert.Contains("\"email\":\"a@b.c\"", requests[0].RawBody, StringComparison.Ordinal);
        Assert.Equal(("POST", "verify"), (requests[1].Method, requests[1].Route));
        Assert.Contains("\"code\":\"123456\"", requests[1].RawBody, StringComparison.Ordinal);
        Assert.Equal(MockAuthTool.Token, File.ReadAllText(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
        Assert.Contains(Email, File.ReadAllText(HomeConfigPath(sandbox, "effortlessapi_token_info.json")), StringComparison.Ordinal);
        auth.ThrowIfFaulted();
    }

    [Fact(DisplayName = "auth-login-calls-tool-and-stores-token: login POSTs /login then /verify on the catalog-resolved auth tool and stores both tokens")]
    public async Task LoginResolvesTheToolThroughTheCatalog()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;

        var result = await cli.Run(["-login"], sandbox.ProjectPath, sandbox, stdin: EmailAndCode);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("CLOUD-BRIDGE CALL TRIGGERED", result.Stdout, StringComparison.Ordinal);
        Assert.Equal(["login", "verify"], auth.Requests.Select(request => request.Route));
        Assert.True(File.Exists(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
        Assert.Contains(
            MockAuthTool.RefreshToken,
            File.ReadAllText(HomeConfigPath(sandbox, "effortlessapi_token_info.json")),
            StringComparison.Ordinal);
        AssertSecretFileMode(HomeConfigPath(sandbox, "effortlessapi_token.txt"));
    }

    [Fact(DisplayName = "auth-login-already: login when already authenticated")]
    public async Task LoginCanBeCancelledWhenAlreadyAuthenticated()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        var token = CreateJwt(ExistingEmail);
        WriteGlobalToken(sandbox, token, ExistingEmail);

        var result = await cli.Run(
            ["login"],
            sandbox.ProjectPath,
            sandbox,
            stdin: $"n{Environment.NewLine}");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"You are already authenticated as {ExistingEmail}.", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Do you want to re-authenticate? (y/N): ", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("Authentication cancelled.", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(auth.Requests);
        Assert.Equal(token, File.ReadAllText(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
    }

    [Fact(DisplayName = "auth-project-login: projectLogin writes the env file")]
    public async Task ProjectLoginWritesTheEnvFile()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        WriteMinimalProject(sandbox);
        sandbox.WriteFile("effortless.env", $"EXISTING_KEY=keep{Environment.NewLine}");

        var result = await cli.Run(["projectlogin"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        var env = sandbox.ReadFile("effortless.env");
        Assert.Contains("EXISTING_KEY=keep", env, StringComparison.Ordinal);
        Assert.Contains($"EFFORTLESS_JWT={MockAuthTool.ProjectToken}", env, StringComparison.Ordinal);
        Assert.Contains("Project signed in (preview: the authentication service does not enforce accounts yet).", result.Stdout, StringComparison.Ordinal);
        var request = Assert.Single(auth.Requests);
        Assert.Equal(("POST", "project-login"), (request.Method, request.Route));
        Assert.Contains("projectId", request.RawBody, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "auth-project-login-writes-env: projectLogin POSTs /project-login and writes EFFORTLESS_JWT")]
    public async Task ProjectLoginSendsTheProjectId()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        sandbox.WriteFile(
            "effortless.json",
            """{"Name":"auth-project","SSoTmeProjectId":"6a1e2d51-1111-4bbb-8ccc-0123456789ab","ProjectSettings":[],"ProjectTranspilers":[]}""");

        var result = await cli.Run(["-projectLogin"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("6a1e2d51-1111-4bbb-8ccc-0123456789ab", Assert.Single(auth.Requests).RawBody, StringComparison.Ordinal);
        Assert.StartsWith("EFFORTLESS_JWT=", sandbox.ReadFile("effortless.env"), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "auth-logout: logout")]
    public async Task LogoutConfirmsDeletesCancelsAndHandlesNoSession()
    {
        var cli = new CliUnderTest();

        using (var confirmed = Sandbox.Create(cli))
        {
            WriteMinimalProject(confirmed);
            WriteGlobalToken(confirmed, CreateJwt(ExistingEmail), ExistingEmail);

            var result = await cli.Run(
                ["logout"],
                confirmed.ProjectPath,
                confirmed,
                stdin: $"y{Environment.NewLine}");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains($"You're logged in as {ExistingEmail}.", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("Logged out successfully.", result.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("CLOUD-BRIDGE CALL TRIGGERED", result.Stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(HomeConfigPath(confirmed, "effortlessapi_token.txt")));
            Assert.False(File.Exists(HomeConfigPath(confirmed, "effortlessapi_token_info.json")));
        }

        using (var cancelled = Sandbox.Create(cli))
        {
            WriteMinimalProject(cancelled);
            var token = CreateJwt(ExistingEmail);
            WriteGlobalToken(cancelled, token, ExistingEmail);

            var result = await cli.Run(
                ["logout"],
                cancelled.ProjectPath,
                cancelled,
                stdin: $"n{Environment.NewLine}");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Logout cancelled.", result.Stdout, StringComparison.Ordinal);
            Assert.Equal(
                token,
                File.ReadAllText(HomeConfigPath(cancelled, "effortlessapi_token.txt")));
        }

        using (var noSession = Sandbox.Create(cli))
        {
            WriteMinimalProject(noSession);

            var result = await cli.Run(["logout"], noSession.ProjectPath, noSession);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("No active session found.", result.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "auth-logout-clears-then-calls: logout clears local tokens then tells the service best-effort")]
    public async Task LogoutClearsThenCallsTheService()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        WriteGlobalToken(sandbox, CreateJwt(ExistingEmail), ExistingEmail, "refresh-to-revoke");

        var result = await cli.Run(["logout"], sandbox.ProjectPath, sandbox, stdin: $"y{Environment.NewLine}");

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
        var request = Assert.Single(auth.Requests);
        Assert.Equal(("POST", "logout"), (request.Method, request.Route));
        Assert.Contains("\"refresh_token\":\"refresh-to-revoke\"", request.RawBody, StringComparison.Ordinal);

        // Unreachable service: still exit 0, still no error text.
        WriteGlobalToken(sandbox, CreateJwt(ExistingEmail), ExistingEmail);
        await auth.DisposeAsync();
        var offline = await cli.Run(["logout"], sandbox.ProjectPath, sandbox, stdin: $"y{Environment.NewLine}");

        Assert.Equal(0, offline.ExitCode);
        Assert.Contains("Logged out successfully.", offline.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("authentication service", offline.Combined, StringComparison.Ordinal);
        Assert.False(File.Exists(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
    }

    [Fact(DisplayName = "auth-subscription-no-token: subscription without login")]
    public async Task PlanWorksWithoutAToken()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        WriteMinimalProject(sandbox);

        foreach (var argument in new[] { "plan", "-subscription" })
        {
            var result = await cli.Run([argument], sandbox.ProjectPath, sandbox);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Plan: preview (not enforced yet", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("Not signed in; the service does not require it yet.", result.Stdout, StringComparison.Ordinal);
        }

        Assert.Equal(2, auth.Requests.Count);
        Assert.All(auth.Requests, request => Assert.Null(request.Authorization));
    }

    [Fact(DisplayName = "auth-subscription: subscription command sends the stored token")]
    public async Task PlanSendsTheStoredToken()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        WriteMinimalProject(sandbox);
        var token = CreateJwt(ExistingEmail);
        WriteGlobalToken(sandbox, token, ExistingEmail);

        var result = await cli.Run(["plan"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Plan: preview", result.Stdout, StringComparison.Ordinal);
        Assert.Contains($"Signed in as {ExistingEmail}.", result.Stdout, StringComparison.Ordinal);
        var request = Assert.Single(auth.Requests);
        Assert.Equal(("GET", "plan"), (request.Method, request.Route));
        Assert.Equal("Bearer " + token, request.Authorization);
    }

    [Fact(DisplayName = "auth-plan-prints-preview: plan GETs /plan and prints the preview plan with and without a token")]
    public async Task PlanPrintsPreviewWithAndWithoutToken()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;

        var anonymous = await cli.Run(["plan"], sandbox.ProjectPath, sandbox);
        WriteGlobalToken(sandbox, CreateJwt(ExistingEmail), ExistingEmail);
        var signedIn = await cli.Run(["plan"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, anonymous.ExitCode);
        Assert.Equal(0, signedIn.ExitCode);
        Assert.Contains("Not signed in; the service does not require it yet.", anonymous.Stdout, StringComparison.Ordinal);
        Assert.Contains($"Signed in as {ExistingEmail}.", signedIn.Stdout, StringComparison.Ordinal);
        Assert.Equal(2, auth.Requests.Count);
    }

    [Fact(DisplayName = "auth-tool-missing-from-catalog-is-clear-error: a catalog without effortless-auth gives a clear error and writes nothing")]
    public async Task MissingAuthToolIsAClearError()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        await using var toolServer = new MockToolServer();
        var index = IndexFixture.Load(toolServer);
        using var sandbox = Sandbox.Create(cli);
        sandbox.SeedHome(index, withToolUrls: false);
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string> { ["cli-cloud-bridge"] = toolServer.BridgeUri.ToString() });
        toolServer.IndexJson = index.Json;

        var login = await cli.Run(["login"], sandbox.ProjectPath, sandbox, stdin: $"{Email}{Environment.NewLine}");
        var plan = await cli.Run(["plan"], sandbox.ProjectPath, sandbox);

        Assert.True(login.Failed);
        Assert.True(plan.Failed);
        Assert.Contains(
            "The authentication tool 'effortless/effortless/effortless-auth' is not in the remote tools catalog",
            login.Stderr,
            StringComparison.Ordinal);
        Assert.Contains("is not in the remote tools catalog", plan.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
        Assert.Empty(auth.Requests);
    }

    [Fact(DisplayName = "auth-tool-5xx-is-clear-error: a 5xx from the auth tool is a clear error and no token is written")]
    public async Task AuthTool5xxIsAClearError()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        auth.Failure = (503, """{"error":"warming up"}""");

        var result = await cli.Run(["login"], sandbox.ProjectPath, sandbox, stdin: $"{Email}{Environment.NewLine}");

        Assert.True(result.Failed);
        Assert.Contains("The authentication service returned 503", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
        Assert.False(File.Exists(HomeConfigPath(sandbox, "effortlessapi_token_info.json")));
    }

    [Fact(DisplayName = "tools-still-run-without-login: tool execution never calls the auth tool")]
    public async Task ToolsRunWithoutLogin()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        WorkflowTestSupport.WriteProject(sandbox, new WorkflowStep("Root", "", "to-uppercase"));
        toolServer.Enqueue(
            "to-uppercase",
            ToolBehavior.Files(FileSetEntry.TextFile("root.txt", "root", alwaysOverwrite: true)));

        var result = await cli.Run(["build"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("root", sandbox.ReadFile("root.txt"));
        Assert.Empty(auth.Requests);
        Assert.False(File.Exists(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
    }

    [Fact(DisplayName = "auth-local-override-used-for-dev: a tool_urls.json override for effortless-auth wins over the catalog")]
    public async Task LocalOverrideWinsForDev()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        await using var toolServer = new MockToolServer();
        var index = IndexFixture.Load(toolServer);
        toolServer.IndexJson = index.Json;
        using var sandbox = Sandbox.Create(cli);
        sandbox.SeedHome(index, withToolUrls: false);
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string>
            {
                ["cli-cloud-bridge"] = toolServer.BridgeUri.ToString(),
                ["effortless-auth"] = auth.BaseUri.ToString(),
            });

        var result = await cli.Run(["login"], sandbox.ProjectPath, sandbox, stdin: EmailAndCode);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["login", "verify"], auth.Requests.Select(request => request.Route));
    }

    [Fact(DisplayName = "auth-login-bad-code-is-clear-error: a rejected sign-in code is a clear error and no token is written")]
    public async Task RejectedCodeIsAClearError()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        auth.Failure = (401, """{"error":"invalid_or_expired_code","message":"invalid_or_expired_token"}""");
        auth.FailureRoute = "verify";

        var result = await cli.Run(["login"], sandbox.ProjectPath, sandbox, stdin: EmailAndCode);

        Assert.True(result.Failed);
        Assert.Contains("The authentication service returned 401", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("invalid_or_expired_code", result.Stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
        Assert.False(File.Exists(HomeConfigPath(sandbox, "effortlessapi_token_info.json")));
    }

    [Fact(DisplayName = "auth-plan-refreshes-expired-token: plan refreshes an expired stored access token first")]
    public async Task PlanRefreshesAnExpiredToken()
    {
        var cli = new CliUnderTest();
        await using var auth = new MockAuthTool();
        using var sandbox = SeedAuthCatalog(cli, auth, out var toolServer);
        await using var _ = toolServer;
        WriteGlobalToken(sandbox, CreateJwt(ExistingEmail, exp: 1_000_000_000L), ExistingEmail, MockAuthTool.RefreshToken);

        var result = await cli.Run(["plan"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        var requests = auth.Requests.ToArray();
        Assert.Equal(2, requests.Length);
        Assert.Equal(("POST", "refresh"), (requests[0].Method, requests[0].Route));
        Assert.Contains($"\"refresh_token\":\"{MockAuthTool.RefreshToken}\"", requests[0].RawBody, StringComparison.Ordinal);
        Assert.Equal(("GET", "plan"), (requests[1].Method, requests[1].Route));
        Assert.Equal("Bearer " + MockAuthTool.RefreshedToken, requests[1].Authorization);
        Assert.Equal(MockAuthTool.RefreshedToken, File.ReadAllText(HomeConfigPath(sandbox, "effortlessapi_token.txt")));
        var info = File.ReadAllText(HomeConfigPath(sandbox, "effortlessapi_token_info.json"));
        Assert.Contains(MockAuthTool.RotatedRefreshToken, info, StringComparison.Ordinal);
        Assert.Contains(ExistingEmail, info, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "auth-set-api-key: setAccountAPIKey")]
    public async Task SetAccountApiKeyWritesReplacesAndRejectsBadSyntax()
    {
        var cli = new CliUnderTest();
        using var sandbox = Sandbox.Create(cli);

        var initial = await cli.Run(
            ["-setAccountAPIKey", "acme/k1"],
            sandbox.ProjectPath,
            sandbox);
        var replacement = await cli.Run(
            ["-api", "acme=k2"],
            sandbox.ProjectPath,
            sandbox);
        var baserow = await cli.Run(
            ["-api", "baserow/u/p"],
            sandbox.ProjectPath,
            sandbox);
        var invalid = await cli.Run(["-api", "bad"], sandbox.ProjectPath, sandbox);
        var invalidBaserow = await cli.Run(
            ["-api", "baserow/u"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, initial.ExitCode);
        Assert.Equal(0, replacement.ExitCode);
        Assert.Equal(0, baserow.ExitCode);
        Assert.True(invalid.Failed);
        Assert.Contains(
            "Default Syntax: -setAccountAPIKey=account/KEY",
            invalid.Stdout,
            StringComparison.Ordinal);
        Assert.True(invalidBaserow.Failed);
        Assert.Contains(
            "Syntax for \"baserow\": -setAccountAPIKey=baserow/username/password",
            invalidBaserow.Stdout,
            StringComparison.Ordinal);

        var keyPath = HomeConfigPath(sandbox, "effortless.key");
        using var key = JsonDocument.Parse(File.ReadAllText(keyPath));
        var apiKeys = key.RootElement.GetProperty("APIKeys");
        Assert.Equal("k2", apiKeys.GetProperty("acme").GetString());
        using var baserowValue = JsonDocument.Parse(
            apiKeys.GetProperty("baserow").GetString()
            ?? throw new InvalidDataException("The baserow key was JSON null."));
        Assert.Equal("u", baserowValue.RootElement.GetProperty("username").GetString());
        Assert.Equal("p", baserowValue.RootElement.GetProperty("password").GetString());
        AssertSecretFileMode(keyPath);
    }

    [Fact(DisplayName = "auth-set-api-key-runas: setAccountAPIKey -runAs")]
    public async Task SetAccountApiKeyRunAsWritesOnlyNamedKeyFile()
    {
        var cli = new CliUnderTest();
        using var sandbox = Sandbox.Create(cli);

        var result = await cli.Run(
            ["-api", "acme/k", "-runAs", "bob"],
            sandbox.ProjectPath,
            sandbox);

        Assert.Equal(0, result.ExitCode);
        var namedPath = HomeConfigPath(sandbox, "effortless.bob.key");
        Assert.True(File.Exists(namedPath));
        Assert.False(File.Exists(HomeConfigPath(sandbox, "effortless.key")));
        using var key = JsonDocument.Parse(File.ReadAllText(namedPath));
        Assert.Equal(
            "k",
            key.RootElement.GetProperty("APIKeys").GetProperty("acme").GetString());
        AssertSecretFileMode(namedPath);
    }

    [Fact(DisplayName = "auth-key-permissions: the config dir and key file are locked down")]
    public async Task ConfigDirAndKeyFileAreLockedDown()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cli = new CliUnderTest();
        using var sandbox = Sandbox.Create(cli);

        var result = await cli.Run(["-api", "acme/k"], sandbox.ProjectPath, sandbox);

        Assert.Equal(0, result.ExitCode);
        var configDir = Path.Combine(sandbox.HomePath, ".effortless");
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(configDir));
        AssertSecretFileMode(HomeConfigPath(sandbox, "effortless.key"));
    }

    /// <summary>
    /// A sandbox whose fresh catalog carries effortless/effortless/effortless-auth
    /// pointing at the mock auth tool, with the mock tool server as the bridge.
    /// </summary>
    private static Sandbox SeedAuthCatalog(CliUnderTest cli, MockAuthTool auth, out MockToolServer toolServer)
    {
        toolServer = new MockToolServer();
        var index = IndexFixture.Load(toolServer);
        var catalog = auth.CatalogWithAuthTool(index);
        toolServer.IndexJson = catalog;
        var sandbox = Sandbox.Create(cli);
        sandbox.SeedHome(index, withToolUrls: false);
        var root = System.Text.Json.Nodes.JsonNode.Parse(catalog)!.AsObject();
        root["fetchedAt"] = CliUnderTest.TestUtcNow.ToString("O");
        sandbox.WriteHomeFile(".effortless/remote_tools/effortless-tools.json", root.ToJsonString());
        ResolutionTestSupport.WriteToolUrls(
            sandbox,
            new Dictionary<string, string> { ["cli-cloud-bridge"] = toolServer.BridgeUri.ToString() });
        return sandbox;
    }

    private static void WriteMinimalProject(Sandbox sandbox)
    {
        sandbox.WriteFile(
            "effortless.json",
            """
            {
              "Name": "auth-characterization",
              "ProjectSettings": [],
              "ProjectTranspilers": []
            }
            """);
    }

    private static void WriteGlobalToken(Sandbox sandbox, string token, string email, string? refreshToken = null)
    {
        sandbox.WriteHomeFile(".effortless/effortlessapi_token.txt", token);
        sandbox.WriteHomeFile(
            ".effortless/effortlessapi_token_info.json",
            JsonSerializer.Serialize(new { Token = token, Email = email, RefreshToken = refreshToken }));
    }

    private static string HomeConfigPath(Sandbox sandbox, string fileName) =>
        Path.Combine(sandbox.HomePath, ".effortless", fileName);

    private static string CreateJwt(string email, long exp = 4_102_444_800L)
    {
        var header = Base64Url("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64Url(JsonSerializer.Serialize(new { email, exp }));
        return $"{header}.{payload}.";
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void AssertSecretFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(path));
    }
}
