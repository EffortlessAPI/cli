using System.Net;
using System.Text;
using Effortless.Cli.Project;
using Effortless.Cli.Seeds;
using Newtonsoft.Json.Linq;

namespace Effortless.Cli.Tests;

public sealed class SeedLicenseTests
{
    [Fact(DisplayName = "unit-seed-license-bind: posts to /api/seeds/licenses/bind with the JWT and body shape the server expects")]
    public void BindPostsExpectedRequest()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"ok":true,"license":{"licenseKey":"EAPI-AAAA-BBBB-CCCC","status":"bound","usable":false}}""");
        using var httpClient = new HttpClient(handler);
        var client = new SeedLicenseClient(httpClient, "https://identity.test");

        var result = client.Bind(
            "jwt-123", "EAPI-AAAA-BBBB-CCCC", "my-seed",
            "https://github.com/acme/orders.git", "orders", "rulebook.json", out var error);

        Assert.Null(error);
        Assert.NotNull(result);
        Assert.Equal("bound", result!["status"]!.Value<string>());

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://identity.test/api/seeds/licenses/bind", request.Uri.ToString());
        Assert.Equal("Bearer jwt-123", request.AuthorizationHeader);
        var body = JObject.Parse(request.Body);
        Assert.Equal("EAPI-AAAA-BBBB-CCCC", body["licenseKey"]!.Value<string>());
        Assert.Equal("my-seed", body["seedId"]!.Value<string>());
        Assert.Equal("https://github.com/acme/orders.git", body["repoUrl"]!.Value<string>());
        Assert.Equal("orders", body["projectName"]!.Value<string>());
    }

    [Fact(DisplayName = "unit-seed-license-bind: a non-2xx response surfaces the server's message and returns null")]
    public void BindSurfacesServerError()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.Conflict,
            """{"error":"license_already_bound","message":"License key cannot be used: license_already_bound"}""");
        using var httpClient = new HttpClient(handler);
        var client = new SeedLicenseClient(httpClient, "https://identity.test");

        var result = client.Bind("jwt", "KEY", "seed", "repo", "proj", null, out var error);

        Assert.Null(result);
        Assert.Equal("License key cannot be used: license_already_bound", error);
    }

    [Fact(DisplayName = "unit-seed-license-check: posts to /api/seeds/licenses/check with no auth header")]
    public void CheckPostsWithoutAuth()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """{"ok":true,"license":{"usable":true,"reason":null}}""");
        using var httpClient = new HttpClient(handler);
        var client = new SeedLicenseClient(httpClient, "https://identity.test");

        var result = client.Check("EAPI-AAAA-BBBB-CCCC", "my-seed", out var error);

        Assert.Null(error);
        Assert.True(result!["usable"]!.Value<bool>());
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://identity.test/api/seeds/licenses/check", request.Uri.ToString());
        Assert.Null(request.AuthorizationHeader);
    }

    [Fact(DisplayName = "unit-seed-license-check: an unreachable service returns null with an error, not an exception")]
    public void CheckReportsUnreachableServiceAsError()
    {
        var handler = new ThrowingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new SeedLicenseClient(httpClient, "https://identity.test");

        var result = client.Check("EAPI-AAAA-BBBB-CCCC", null, out var error);

        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact(DisplayName = "integration-build-license: a build with no seed license key does nothing new")]
    public void BuildWithoutLicenseKeyIsUnaffected()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "effortless.json"), "{}");
        var project = new EffortlessProject { RootPath = directory.Path };
        var calledCheck = false;
        var licenses = new FakeSeedLicenseClient((_, _) => { calledCheck = true; return (null, "unused"); });
        var runner = new BuildRunner(
            project,
            (_, _, _, _) => 0,
            new BuildErrorLog(),
            licenses.AsClient());

        runner.Rebuild(directory.Path, includeDisabled: true, transpilerGroup: null, isBuildLocal: true, debug: false);

        Assert.False(calledCheck);
    }

    [Fact(DisplayName = "integration-build-license: a revoked license stops the build before any transpiler runs")]
    public void BuildWithRevokedLicenseFailsFast()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "effortless.env"),
            "EFFORTLESS_SEED_LICENSE_KEY=EAPI-AAAA-BBBB-CCCC\n");
        var project = new EffortlessProject { RootPath = directory.Path };
        var licenses = new FakeSeedLicenseClient((_, _) =>
        {
            var json = JObject.Parse("""{"usable":false,"reason":"license_revoked","revokedReason":"refunded"}""");
            return (json, null);
        });
        var ranTranspiler = false;
        var runner = new BuildRunner(
            project,
            (_, _, _, _) => { ranTranspiler = true; return 0; },
            new BuildErrorLog(),
            licenses.AsClient());

        var exception = Assert.Throws<SeedLicenseRevokedException>(() =>
            runner.Rebuild(directory.Path, includeDisabled: true, transpilerGroup: null, isBuildLocal: true, debug: false));

        Assert.Contains("license_revoked", exception.Message);
        Assert.Contains("refunded", exception.Message);
        Assert.False(ranTranspiler);
    }

    [Fact(DisplayName = "integration-build-license: an available or expired license is logged but does not block the build")]
    public void BuildWithNonRevokedLicenseProceeds()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "effortless.env"),
            "EFFORTLESS_SEED_LICENSE_KEY=EAPI-AAAA-BBBB-CCCC\n");
        var project = new EffortlessProject { RootPath = directory.Path };
        var licenses = new FakeSeedLicenseClient((_, _) =>
        {
            var json = JObject.Parse("""{"usable":true,"reason":null}""");
            return (json, null);
        });
        var runner = new BuildRunner(
            project,
            (_, _, _, _) => 0,
            new BuildErrorLog(),
            licenses.AsClient());

        // No ProjectTranspilers are configured, so the real assertion is
        // that CheckSeedLicense does not throw for a usable key -- there is
        // nothing here for _runCommandLine to be invoked on either way.
        var exception = Record.Exception(() =>
            runner.Rebuild(directory.Path, includeDisabled: true, transpilerGroup: null, isBuildLocal: true, debug: false));
        Assert.Null(exception);
    }

    [Fact(DisplayName = "integration-build-license: an unreachable license service warns and does not block the build")]
    public void BuildWithUnreachableLicenseServiceProceeds()
    {
        using var directory = new TestDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "effortless.env"),
            "EFFORTLESS_SEED_LICENSE_KEY=EAPI-AAAA-BBBB-CCCC\n");
        var project = new EffortlessProject { RootPath = directory.Path };
        var licenses = new FakeSeedLicenseClient((_, _) => (null, "Could not reach the service"));
        var runner = new BuildRunner(
            project,
            (_, _, _, _) => 0,
            new BuildErrorLog(),
            licenses.AsClient());

        var exception = Record.Exception(() =>
            runner.Rebuild(directory.Path, includeDisabled: true, transpilerGroup: null, isBuildLocal: true, debug: false));
        Assert.Null(exception);
    }

    private sealed class RequestRecord
    {
        public required Uri Uri { get; init; }
        public string? AuthorizationHeader { get; init; }
        public required string Body { get; init; }
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord
            {
                Uri = request.RequestUri!,
                AuthorizationHeader = request.Headers.Authorization?.ToString(),
                Body = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? "",
            });
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken).GetAwaiter().GetResult();
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated network failure");

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("simulated network failure");
    }

    /// <summary>
    /// SeedLicenseClient is sealed with no interface, so BuildRunner tests
    /// fake it at the HTTP layer instead: a handler that runs the given
    /// delegate and shapes an OK/error response from it, exercised through
    /// a real SeedLicenseClient so BuildRunner's own JObject-reading code is
    /// covered too, not just a hand-rolled substitute.
    /// </summary>
    private sealed class FakeSeedLicenseClient(Func<string, string?, (JObject? json, string? error)> check)
    {
        public SeedLicenseClient AsClient() => new(new HttpClient(new Handler(check)), "https://identity.test");

        private sealed class Handler(Func<string, string?, (JObject? json, string? error)> check) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = JObject.Parse(
                    request.Content!.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult());
                var (json, error) = check(
                    body["licenseKey"]!.Value<string>()!, body["seedId"]?.Value<string>());
                if (error is not null)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent($$"""{"message":"{{error}}"}""", Encoding.UTF8, "application/json"),
                    });
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        $$"""{"ok":true,"license":{{json!.ToString(Newtonsoft.Json.Formatting.None)}}}""",
                        Encoding.UTF8, "application/json"),
                });
            }

            protected override HttpResponseMessage Send(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                SendAsync(request, cancellationToken).GetAwaiter().GetResult();
        }
    }
}
