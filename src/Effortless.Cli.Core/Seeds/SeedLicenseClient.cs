using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Effortless.Cli.Seeds;

/// <summary>
/// Calls the seed license routes on the Effortless Identity API
/// (`www.effortlessapi.com`, `www-rulebook-server` in the app repo):
/// POST /api/seeds/licenses/bind (cloneSeed -licenseKey) and
/// POST /api/seeds/licenses/check (every `effortless build` in a licensed
/// repository). This is a different backend from the transpiler catalog --
/// there is no catalog tool to resolve here, so the base URL is a hardcoded
/// default overridable by an environment variable, the same shape
/// SeedCatalogClient uses for the GitHub API it calls.
/// </summary>
public sealed class SeedLicenseClient
{
    public const string BaseUrlEnvironmentVariable = "EFFORTLESS_API_URL";
    public const string DefaultBaseUrl = "https://www.effortlessapi.com";

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public SeedLicenseClient(HttpClient httpClient = null, string baseUrl = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _baseUrl = (baseUrl
                    ?? Environment.GetEnvironmentVariable(BaseUrlEnvironmentVariable)
                    ?? DefaultBaseUrl).TrimEnd('/');
    }

    /// <summary>
    /// POST /api/seeds/licenses/bind. Requires a signed-in JWT: a license key
    /// is held by an account, and binding spends it, so an anonymous caller
    /// can't be allowed to claim one. Returns the bound license's state, or
    /// null with <paramref name="error"/> set to the server's error code.
    /// </summary>
    public JObject Bind(
        string jwt,
        string licenseKey,
        string seedId,
        string repoUrl,
        string projectName,
        string sourceRulebook,
        out string error)
    {
        var body = new JObject
        {
            ["licenseKey"] = licenseKey,
            ["seedId"] = seedId,
            ["repoUrl"] = repoUrl,
            ["projectName"] = projectName,
            ["sourceRulebook"] = sourceRulebook,
        };
        return TryCall(HttpMethod.Post, "/api/seeds/licenses/bind", body, jwt, out error);
    }

    /// <summary>
    /// POST /api/seeds/licenses/check. Unauthenticated: a build runs
    /// unattended with no signed-in session, and the key itself is the only
    /// credential it can present. Returns the key's current state, or null
    /// with <paramref name="error"/> set -- callers should treat a null
    /// result as "could not reach the service" and not fail the build over
    /// it, since this check is not a gate on anything but a revoked or
    /// wrong-seed key, and the service being unreachable is neither.
    /// </summary>
    public JObject Check(string licenseKey, string seedId, out string error)
    {
        var body = new JObject
        {
            ["licenseKey"] = licenseKey,
            ["seedId"] = seedId,
        };
        return TryCall(HttpMethod.Post, "/api/seeds/licenses/check", body, jwt: null, out error);
    }

    private JObject TryCall(HttpMethod method, string route, JObject body, string jwt, out string error)
    {
        error = null;
        var url = _baseUrl + route;
        try
        {
            using var request = new HttpRequestMessage(method, url)
            {
                Content = new StringContent(
                    body.ToString(Newtonsoft.Json.Formatting.None),
                    Encoding.UTF8,
                    "application/json"),
            };
            if (!string.IsNullOrEmpty(jwt))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + jwt);
            }

            using var response = _httpClient.Send(request);
            var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            JObject parsed;
            try
            {
                parsed = string.IsNullOrWhiteSpace(content) ? new JObject() : JObject.Parse(content);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                error = $"The service returned a non-JSON response for {method} {url}.";
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                error = parsed["message"]?.Value<string>()
                    ?? parsed["error"]?.Value<string>()
                    ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                return null;
            }

            return parsed["license"] as JObject ?? parsed;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            error = $"Could not reach {url}: {exception.Message}";
            return null;
        }
    }
}
