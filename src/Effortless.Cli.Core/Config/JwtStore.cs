using System.Diagnostics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Effortless.Cli.Config;

public class JwtStore
{
    /// <param name="refreshToken">
    /// The identity service's refresh token. Null keeps the one already
    /// stored: a refresh that raced another one returns no new refresh token.
    /// </param>
    public bool StoreJWTToken(string jwtToken, string email = null, string refreshToken = null)
    {
        try
        {
            string configDirectory = UserConfigDir.EffortlessDir.FullName;

            string tokenFile = Path.Combine(
                configDirectory,
                "effortlessapi_token.txt");
            File.WriteAllText(tokenFile, jwtToken);
            SetFilePermissions600(tokenFile);

            string tokenInfoFile = Path.Combine(
                configDirectory,
                "effortlessapi_token_info.json");
            refreshToken ??= GetStoredRefreshToken();
            email ??= GetStoredEmail() ?? GetEmailFromJwt(jwtToken);
            var exp = GetExpFromJwt(jwtToken);
            var tokenInfo = new
            {
                Token = jwtToken,
                Email = email,
                RefreshToken = refreshToken,
                CreatedAt = DateTime.UtcNow.ToString(
                    "yyyy-MM-ddTHH:mm:ssZ"),
                ExpiresAt = exp == 0
                    ? null
                    : DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime.ToString(
                        "yyyy-MM-ddTHH:mm:ssZ")
            };
            File.WriteAllText(
                tokenInfoFile,
                JsonConvert.SerializeObject(
                    tokenInfo,
                    Formatting.Indented));
            SetFilePermissions600(tokenInfoFile);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to store token: {ex.Message}");
            return false;
        }
    }

    public string GetStoredJWTToken()
    {
        try
        {
            string tokenFile = Path.Combine(
                UserConfigDir.EffortlessDir.FullName,
                "effortlessapi_token.txt");

            if (File.Exists(tokenFile))
            {
                return File.ReadAllText(tokenFile).Trim();
            }

            return "";
        }
        catch
        {
            return "";
        }
    }

    public string GetStoredRefreshToken() => ReadTokenInfo("RefreshToken");

    public string GetStoredEmail() => ReadTokenInfo("Email");

    private string ReadTokenInfo(string property)
    {
        try
        {
            string tokenInfoFile = Path.Combine(
                UserConfigDir.EffortlessDir.FullName,
                "effortlessapi_token_info.json");
            if (File.Exists(tokenInfoFile))
            {
                var info = JsonConvert.DeserializeObject<JObject>(
                    File.ReadAllText(tokenInfoFile));
                var value = info?[property]?.ToString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch
        {
        }

        return null;
    }

    public bool IsAuthenticated()
    {
        string token = GetStoredJWTToken();
        return !string.IsNullOrWhiteSpace(token);
    }

    public bool IsTokenExpired()
    {
        try
        {
            string token = GetStoredJWTToken();
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return true;
            }

            var payloadBase64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payloadBase64.Length % 4)
            {
                case 2:
                    payloadBase64 += "==";
                    break;
                case 3:
                    payloadBase64 += "=";
                    break;
            }

            var payload = JsonConvert.DeserializeObject<dynamic>(
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(payloadBase64)));
            long exp = (long)(payload?.exp ?? 0);
            return exp == 0 ||
                   DateTimeOffset.FromUnixTimeSeconds(exp) <=
                   DateTimeOffset.UtcNow;
        }
        catch
        {
            return true;
        }
    }

    public bool ClearAuthToken()
    {
        try
        {
            string configDirectory = UserConfigDir.EffortlessDir.FullName;
            string tokenFile = Path.Combine(
                configDirectory,
                "effortlessapi_token.txt");
            string tokenInfoFile = Path.Combine(
                configDirectory,
                "effortlessapi_token_info.json");

            if (File.Exists(tokenFile))
            {
                File.Delete(tokenFile);
            }

            if (File.Exists(tokenInfoFile))
            {
                File.Delete(tokenInfoFile);
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"Failed to clear auth token: {ex.Message}");
            return false;
        }
    }

    public static string GetEmailFromJwt(string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            var payloadBase64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payloadBase64.Length % 4)
            {
                case 2:
                    payloadBase64 += "==";
                    break;
                case 3:
                    payloadBase64 += "=";
                    break;
            }

            var payload = JsonConvert.DeserializeObject<dynamic>(
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(payloadBase64)));
            return payload?.email?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static long GetExpFromJwt(string token)
    {
        try
        {
            var parts = (token ?? "").Split('.');
            if (parts.Length != 3)
            {
                return 0;
            }

            var payloadBase64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payloadBase64.Length % 4)
            {
                case 2:
                    payloadBase64 += "==";
                    break;
                case 3:
                    payloadBase64 += "=";
                    break;
            }

            var payload = JsonConvert.DeserializeObject<dynamic>(
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(payloadBase64)));
            return (long)(payload?.exp ?? 0);
        }
        catch
        {
            return 0;
        }
    }

    public static bool IsJwtExpired(string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return true;
            }

            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return true;
            }

            var payloadBase64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payloadBase64.Length % 4)
            {
                case 2:
                    payloadBase64 += "==";
                    break;
                case 3:
                    payloadBase64 += "=";
                    break;
            }

            var payload = JsonConvert.DeserializeObject<dynamic>(
                Encoding.UTF8.GetString(
                    Convert.FromBase64String(payloadBase64)));
            long exp = (long)(payload?.exp ?? 0);
            return exp == 0 ||
                   DateTimeOffset.FromUnixTimeSeconds(exp) <=
                   DateTimeOffset.UtcNow;
        }
        catch
        {
            return true;
        }
    }

    private static void SetFilePermissions600(string path)
    {
        if (Environment.OSVersion.Platform == PlatformID.Unix ||
            Environment.OSVersion.Platform == PlatformID.MacOSX)
        {
            try
            {
                Process.Start(
                    new ProcessStartInfo(
                        "chmod",
                        $"600 \"{path}\"")
                    {
                        CreateNoWindow = true
                    })?.WaitForExit();
            }
            catch
            {
                // Best effort.
            }
        }
    }
}
