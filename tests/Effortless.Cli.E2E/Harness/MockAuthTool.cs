using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Effortless.Cli.E2E.Harness;

internal sealed record CapturedAuthToolRequest(
    string Method,
    string Route,
    string RawBody,
    string? Authorization);

/// <summary>
/// Step 13: a stand-in for the published effortless-auth tool. Answers
/// /login, /verify, /refresh, /project-login, /plan, /logout the way the tool
/// relays the Effortless Identity API, records every request, and can be
/// forced to fail with a status code (on every route, or on one).
/// </summary>
internal sealed class MockAuthTool : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentQueue<Exception> _failures = new();
    private readonly Task _listenTask;
    private bool _disposed;

    public MockAuthTool()
    {
        Port = GetFreePort();
        BaseUri = new Uri($"http://127.0.0.1:{Port}/tools/effortless-auth/v2026.09.05.0001/");
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _listenTask = ListenAsync();
    }

    public int Port { get; }

    /// <summary>The URL the catalog's head entry (or a tool_urls override) points at.</summary>
    public Uri BaseUri { get; }

    /// <summary>The access token /verify returns: {"email":"a@b.c","exp":4102444800}, unsigned.</summary>
    public const string Token = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJlbWFpbCI6ImFAYi5jIiwiZXhwIjo0MTAyNDQ0ODAwfQ.";

    /// <summary>The access token /refresh returns: {"email":"x@y.z","exp":4102444800}, unsigned.</summary>
    public const string RefreshedToken = "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0.eyJlbWFpbCI6InhAeS56IiwiZXhwIjo0MTAyNDQ0ODAwfQ.";

    public const string RefreshToken = "refresh-1";
    public const string RotatedRefreshToken = "refresh-2";

    /// <summary>The project-login preview token.</summary>
    public const string ProjectToken = "preview.eyJzdWIiOiJhQGIuYyIsInBsYW4iOiJwcmV2aWV3IiwiZW5mb3JjZWQiOmZhbHNlfQ";

    /// <summary>When non-null, routes answer with this status and body.</summary>
    public (int StatusCode, string Body)? Failure { get; set; }

    /// <summary>When set, <see cref="Failure"/> applies to this route only.</summary>
    public string? FailureRoute { get; set; }

    public ConcurrentQueue<CapturedAuthToolRequest> Requests { get; } = new();

    /// <summary>
    /// The base catalog plus an effortless/effortless/effortless-auth head
    /// entry pointing at this mock.
    /// </summary>
    public string CatalogWithAuthTool(IndexFixture index)
    {
        var root = JsonNode.Parse(index.Json)!.AsObject();
        root["transpilerVersions"]!["effortless/effortless/effortless-auth"] = new JsonObject
        {
            ["v2026.09.05.0001"] = new JsonObject
            {
                ["description"] = "Authentication seam for the Effortless CLI (preview).",
                ["metaData"] = new JsonObject
                {
                    ["isHeadVersion"] = true,
                    ["createdAt"] = "2026-09-05T00:00:00Z",
                    ["versionCount"] = 1,
                    ["requiresAPIKey"] = false,
                },
                ["urls"] = new JsonObject { ["post"] = BaseUri.ToString() },
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public void ThrowIfFaulted()
    {
        if (_failures.TryPeek(out var failure))
        {
            throw new InvalidOperationException("The mock auth tool failed.", failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _listenTask;
        }
        catch (OperationCanceledException)
        {
        }

        _stopping.Dispose();
        ThrowIfFaulted();
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception exception) when (
                _stopping.IsCancellationRequested
                && exception is HttpListenerException or ObjectDisposedException)
            {
                break;
            }
            catch (Exception exception)
            {
                _failures.Enqueue(exception);
                break;
            }

            _ = HandleSafelyAsync(context);
        }
    }

    private async Task HandleSafelyAsync(HttpListenerContext context)
    {
        try
        {
            await HandleAsync(context);
        }
        catch (Exception exception)
        {
            _failures.Enqueue(exception);
            await WriteAsync(context.Response, 500, JsonSerializer.Serialize(new { error = exception.Message }));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath ?? "/";
        var route = path.StartsWith(BaseUri.AbsolutePath, StringComparison.Ordinal)
            ? path[BaseUri.AbsolutePath.Length..].Trim('/')
            : path.Trim('/');
        string body;
        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        Requests.Enqueue(new CapturedAuthToolRequest(
            request.HttpMethod,
            route,
            body,
            request.Headers["Authorization"]));

        if (Failure is { } failure && (FailureRoute is null || FailureRoute == route))
        {
            await WriteAsync(context.Response, failure.StatusCode, failure.Body);
            return;
        }

        JsonObject? bodyJson = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                bodyJson = JsonNode.Parse(body)?.AsObject();
            }
            catch (JsonException)
            {
            }
        }

        object response = (request.HttpMethod, route) switch
        {
            ("POST", "login") => new { ok = true, code_sent = true },
            ("POST", "verify") => new
            {
                ok = true,
                registered = true,
                access_token = Token,
                token_type = "Bearer",
                expires_in = 900,
                refresh_token = RefreshToken,
                profile_incomplete = true,
            },
            ("POST", "refresh") => new
            {
                ok = true,
                access_token = RefreshedToken,
                token_type = "Bearer",
                expires_in = 900,
                refresh_token = RotatedRefreshToken,
            },
            ("POST", "project-login") => new
            {
                ok = true,
                token = ProjectToken,
                projectId = bodyJson?["projectId"]?.GetValue<string>(),
                plan = "preview",
                enforced = false,
            },
            ("GET", "plan") => new { ok = true, plan = "preview", enforced = false },
            ("POST", "logout") => new { ok = true, enforced = false },
            _ => null!,
        };

        if (response is null)
        {
            await WriteAsync(context.Response, 404, """{"error":"route not found"}""");
            return;
        }

        await WriteAsync(context.Response, 200, JsonSerializer.Serialize(response));
    }

    private static async Task WriteAsync(HttpListenerResponse response, int statusCode, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        try
        {
            await response.OutputStream.WriteAsync(bytes);
        }
        finally
        {
            response.OutputStream.Close();
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
