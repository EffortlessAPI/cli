using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Effortless.Cli;

public enum TranspileRetryKind
{
    None,
    HostNotFound,
    ConnectionRefused,
    ConnectionResetOrUnreachable,
    SslException,
    GatewayStatus,
    BadRequestSslBody,
}

public sealed record TranspileRetryDecision(
    TranspileRetryKind Kind,
    bool ShouldRetry,
    bool ShouldAbort,
    TimeSpan Delay,
    string Message,
    ConsoleColor? MessageColor = null);

public sealed class RetryPolicy
{
    public const int MaxRetries = 10;
    public const int MaxConnectionRefused = 3;

    public static readonly TimeSpan ConnectionDelay = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan GatewayDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AsyncPollDelay = TimeSpan.FromSeconds(3);

    public TranspileRetryDecision Classify(
        HttpRequestException exception,
        Uri targetUrl,
        string toolLabel,
        int retryAttempt,
        int connectionRefusedAttempt)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(targetUrl);

        var socketException = FindInnerException<SocketException>(exception);
        if (socketException?.SocketErrorCode == SocketError.HostNotFound
            || exception.Message.Contains("No such host", StringComparison.OrdinalIgnoreCase))
        {
            // A host that does not resolve has no workload to boot up, so
            // retrying only delays the failure.
            return new TranspileRetryDecision(
                TranspileRetryKind.HostNotFound,
                ShouldRetry: false,
                ShouldAbort: true,
                TimeSpan.Zero,
                $"[cli] [{toolLabel}] Host not found: {targetUrl.Host}. Not retrying: the host does not exist.",
                ConsoleColor.Red);
        }

        if (socketException?.SocketErrorCode == SocketError.ConnectionRefused)
        {
            if (connectionRefusedAttempt >= MaxConnectionRefused)
            {
                return new TranspileRetryDecision(
                    TranspileRetryKind.ConnectionRefused,
                    ShouldRetry: false,
                    ShouldAbort: true,
                    TimeSpan.Zero,
                    $"\nERROR: Connection refused {MaxConnectionRefused} times for: {targetUrl}\n"
                    + "The transpiler URL may be incorrect or the service is not deployed.\n"
                    + "If this tool has no PinnedVersion, add one to your project config or use -id to specify a version.",
                    ConsoleColor.Red);
            }

            return Retry(
                TranspileRetryKind.ConnectionRefused,
                ConnectionDelay,
                $"[{toolLabel}] Connection error ({socketException.SocketErrorCode}). Retrying in 6 seconds... (attempt {retryAttempt}/{MaxRetries})");
        }

        if (socketException?.SocketErrorCode is SocketError.ConnectionReset
            or SocketError.NetworkUnreachable)
        {
            return Retry(
                TranspileRetryKind.ConnectionResetOrUnreachable,
                ConnectionDelay,
                $"[{toolLabel}] Connection error ({socketException.SocketErrorCode}). Retrying in 6 seconds... (attempt {retryAttempt}/{MaxRetries})");
        }

        if (FindInnerException<AuthenticationException>(exception) is not null
            || exception.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase))
        {
            return Retry(
                TranspileRetryKind.SslException,
                ConnectionDelay,
                $"[{toolLabel}] SSL connection error. Retrying in 6 seconds... (attempt {retryAttempt}/{MaxRetries})",
                ConsoleColor.Yellow);
        }

        return NoRetry();
    }

    public TranspileRetryDecision Classify(
        HttpStatusCode statusCode,
        string responseBody,
        string toolLabel,
        int retryAttempt)
    {
        if (statusCode is HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout)
        {
            return Retry(
                TranspileRetryKind.GatewayStatus,
                GatewayDelay,
                $"[{toolLabel}] Remote transpiler not ready ({statusCode}). Retrying... (attempt {retryAttempt}/{MaxRetries})",
                ConsoleColor.Yellow);
        }

        if (statusCode == HttpStatusCode.BadRequest
            && (responseBody.Contains(
                    "SSL connection could not be established",
                    StringComparison.OrdinalIgnoreCase)
                || (responseBody.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                    && responseBody.Contains("error", StringComparison.OrdinalIgnoreCase))))
        {
            return Retry(
                TranspileRetryKind.BadRequestSslBody,
                ConnectionDelay,
                $"[{toolLabel}] Remote transpiler SSL error. Retrying in 6 seconds... (attempt {retryAttempt}/{MaxRetries})",
                ConsoleColor.Yellow);
        }

        return NoRetry();
    }

    public static string RetriesExhaustedMessage(Uri targetUrl) =>
        $"\nERROR: Failed to connect to transpiler after {MaxRetries} attempts: {targetUrl}";

    private static TranspileRetryDecision Retry(
        TranspileRetryKind kind,
        TimeSpan delay,
        string message,
        ConsoleColor? color = null) =>
        new(
            kind,
            ShouldRetry: true,
            ShouldAbort: false,
            delay,
            message,
            color);

    private static TranspileRetryDecision NoRetry() =>
        new(
            TranspileRetryKind.None,
            ShouldRetry: false,
            ShouldAbort: false,
            TimeSpan.Zero,
            string.Empty);

    private static T FindInnerException<T>(Exception exception)
        where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}
