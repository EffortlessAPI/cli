using System.Net;
using System.Net.Http.Json;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Xml;
using Effortless.Cli.FileSets;
using Effortless.Cli.Options;
using Effortless.Cli.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Effortless.Cli;

public enum TranspileOutputDisposition
{
    None,
    Clean,
    Save,
}

/// <summary>
/// R11: how a POST failed at the connection level, when it never produced a
/// response. A tool that answered (even 4xx/5xx) is <see cref="None"/>; those
/// failures belong to the <see cref="RetryPolicy"/> matrix, not to a catalog
/// refresh.
/// </summary>
public enum TranspileConnectionFailure
{
    None,
    HostNotFound,
    ConnectionRefused,
    NetworkUnreachable,
    TlsHandshake,
    NoResponseWithinTimeout,
}

/// <summary>
/// The one wording for a connection-level failure, shared by the transpile
/// error text and the R11 catalog-refresh hint so the two never diverge.
/// </summary>
public static class TranspileConnectionFailureText
{
    public static string Describe(TranspileConnectionFailure failure) =>
        failure switch
        {
            TranspileConnectionFailure.HostNotFound => "DNS resolution failure",
            TranspileConnectionFailure.ConnectionRefused => "connection refused",
            TranspileConnectionFailure.NetworkUnreachable => "network unreachable",
            TranspileConnectionFailure.TlsHandshake => "TLS handshake failure",
            TranspileConnectionFailure.NoResponseWithinTimeout =>
                "no response within waitTimeout",
            _ => "a connection failure",
        };

    /// <summary>
    /// True when nothing ever accepted the connection. Waiting longer cannot
    /// turn such a failure into a result, so the step fails immediately.
    /// </summary>
    public static bool IsUnreachable(TranspileConnectionFailure failure) =>
        failure is TranspileConnectionFailure.HostNotFound
            or TranspileConnectionFailure.ConnectionRefused
            or TranspileConnectionFailure.NetworkUnreachable
            or TranspileConnectionFailure.TlsHandshake;
}

public sealed class TranspileClientResult
{
    internal TranspileClientResult(
        TranspilePayload payload,
        TranspileOutputDisposition outputDisposition,
        string transpilerKey,
        string currentDirectory,
        string inputFileSetXml,
        bool skipClean,
        bool debug,
        TranspileConnectionFailure connectionFailure = TranspileConnectionFailure.None)
    {
        Payload = payload;
        OutputDisposition = outputDisposition;
        TranspilerKey = transpilerKey;
        CurrentDirectory = currentDirectory;
        InputFileSetXml = inputFileSetXml;
        SkipClean = skipClean;
        Debug = debug;
        ConnectionFailure = connectionFailure;
    }

    public TranspilePayload Payload { get; }

    /// <summary>
    /// Non-<see cref="TranspileConnectionFailure.None"/> when the tool was
    /// completely unreachable (D29): DNS failure, connection refused, TLS
    /// failure, or no response bytes within waitTimeout.
    /// </summary>
    public TranspileConnectionFailure ConnectionFailure { get; }

    /// <summary>
    /// True when the workload never answered, so a forced catalog refresh and
    /// one retry against a moved head URL may help (R11).
    /// </summary>
    public bool IsCompleteTimeout =>
        ConnectionFailure != TranspileConnectionFailure.None;

    public TranspileOutputDisposition OutputDisposition { get; }

    public string TranspilerKey { get; }

    public string CurrentDirectory { get; }

    public string InputFileSetXml { get; }

    public bool SkipClean { get; }

    public bool Debug { get; }

    public bool Succeeded => Payload?.Exception is null;

    public byte[] ZippedOutputFileSet =>
        Payload?.TranspileRequest?.ZippedOutputFileSet;
}

public sealed class TranspileClient : IDisposable
{
    private static readonly string SenderId = Guid.NewGuid().ToString("N");
    private static readonly JsonSerializerOptions WireJsonOptions =
        CreateWireJsonOptions();
    private static readonly JsonSerializerSettings ResponseJsonSettings =
        CreateResponseJsonSettings();

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly TimeProvider _timeProvider;
    private readonly RetryPolicy _retryPolicy;
    private TranspileConnectionFailure _lastConnectionFailure;
    private readonly Func<Guid> _entityIdFactory;
    private bool _disposed;

    public TranspileClient()
        : this(
            new HttpClient(),
            delayAsync: null,
            timeProvider: null,
            retryPolicy: null,
            entityIdFactory: null,
            ownsHttpClient: true)
    {
    }

    public TranspileClient(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> delayAsync = null,
        TimeProvider timeProvider = null,
        RetryPolicy retryPolicy = null)
        : this(
            httpClient,
            delayAsync,
            timeProvider,
            retryPolicy,
            entityIdFactory: null,
            ownsHttpClient: false)
    {
    }

    internal TranspileClient(
        HttpClient httpClient,
        Func<Guid> entityIdFactory)
        : this(
            httpClient,
            delayAsync: null,
            timeProvider: null,
            retryPolicy: null,
            entityIdFactory: entityIdFactory,
            ownsHttpClient: false)
    {
    }

    private TranspileClient(
        HttpClient httpClient,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        TimeProvider timeProvider,
        RetryPolicy retryPolicy,
        Func<Guid> entityIdFactory,
        bool ownsHttpClient)
    {
        _httpClient = httpClient
            ?? throw new ArgumentNullException(nameof(httpClient));
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _delayAsync = delayAsync
            ?? ((delay, token) => Task.Delay(delay, token));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retryPolicy = retryPolicy ?? new RetryPolicy();
        _entityIdFactory = entityIdFactory ?? Guid.NewGuid;
        _ownsHttpClient = ownsHttpClient;
    }

    public TranspilePayload BuildPayload(CliInvocation invocation)
    {
        ValidateInvocation(invocation);

        var options = invocation.Options;
        var inputFileSetXml = invocation.InputFileSetXml;
        var transpilerKey = invocation.LedgerKey
            ?? invocation.TargetUrl.SanitizeUrlForFilename();

        return new TranspilePayload
        {
            PayloadId = NewPayloadId(),
            SenderId = SenderId,
            Settings = new Dictionary<string, string>(),
            CLIAccount = invocation.Account,
            CLIInput = options.input?.ToArray() ?? Array.Empty<string>(),
            CLIInputFileContents = string.Empty,
            CLIInputFileSetJson = null,
            CLIInputFileSetXml = inputFileSetXml,
            CLIOutput = options.output,
            CLIParams = options.parameters?.ToArray() ?? Array.Empty<string>(),
            CLITranspiler = invocation.Transpiler,
            CLIWaitTimeout = options.waitTimeout,
            CLIDebug = options.debug,
            CLIJwt = invocation.Jwt ?? string.Empty,
            Transpiler = new Transpiler
            {
                TranspilerId = _entityIdFactory(),
                Name = invocation.Transpiler,
                LowerHyphenName = transpilerKey,
            },
            TranspileRequest = new TranspileRequest
            {
                TranspileRequestId = _entityIdFactory(),
                ZippedInputFileSet = inputFileSetXml.Zip(),
            },
        };
    }

    public Task<TranspileClientResult> ExecuteAsync(
        CliInvocation invocation,
        CancellationToken cancellationToken = default) =>
        TranspileAsync(invocation, cancellationToken);

    public async Task<TranspileClientResult> TranspileAsync(
        CliInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateInvocation(invocation);

        var targetUrl = new Uri(invocation.TargetUrl, UriKind.Absolute);
        var payload = BuildPayload(invocation);
        var startedAt = _timeProvider.GetTimestamp();
        var spinner = new BootSpinner(
            invocation.RawTranspilerArg ?? invocation.Transpiler);
        _lastConnectionFailure = TranspileConnectionFailure.None;

        if (invocation.Options.debug)
        {
            Console.WriteLine($"DEBUG: POST {targetUrl}");
        }

        TranspilePayload responsePayload;
        try
        {
            responsePayload = await SendAndParseAsync(
                invocation,
                targetUrl,
                payload,
                startedAt,
                spinner,
                cancellationToken);

            // A tool that never accepted a connection does not start answering
            // because the CLI waited longer: once the retries are spent, fail
            // now instead of spinning the boot spinner through the rest of
            // waitTimeout and then blaming a cook that never started. Only a
            // reachable tool that owes a result is still worth waiting for.
            if (responsePayload is null
                && !TranspileConnectionFailureText.IsUnreachable(
                    _lastConnectionFailure))
            {
                await WaitForCookTimeoutAsync(
                    invocation.Options.waitTimeout,
                    startedAt,
                    spinner,
                    cancellationToken);
            }
        }
        finally
        {
            spinner.Clear();
        }

        responsePayload ??= FailurePayload(NoResultException(targetUrl));

        FixResponseNames(responsePayload, invocation, targetUrl);
        PrintLogsAndPromoteErrors(responsePayload, invocation);
        responsePayload.CLIDebug = invocation.Options.debug;
        ValidateOutputForDebug(responsePayload, invocation.Options.debug);

        return CreateResult(invocation, responsePayload);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>
    /// The failure a run ends with when no payload ever came back: an
    /// unreachable tool reports the connection failure that stopped it, and
    /// only a tool that could be reached reports a cook timeout.
    /// </summary>
    private Exception NoResultException(Uri targetUrl) =>
        TranspileConnectionFailureText.IsUnreachable(_lastConnectionFailure)
            ? new Exception(
                $"Could not reach the transpiler at {targetUrl} "
                + $"({TranspileConnectionFailureText.Describe(_lastConnectionFailure)}); it never answered.")
            : new TimeoutException("Timed out waiting for cook");

    private async Task<TranspilePayload> SendAndParseAsync(
        CliInvocation invocation,
        Uri targetUrl,
        TranspilePayload payload,
        long startedAt,
        BootSpinner spinner,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        var connectionRefusedCount = 0;
        var serverAnswered = false;
        var toolLabel = FirstNonEmpty(
            invocation.RawTranspilerArg,
            invocation.Transpiler,
            targetUrl.ToString());

        while (ElapsedMilliseconds(startedAt) < invocation.Options.waitTimeout
               && retryCount < RetryPolicy.MaxRetries)
        {
            HttpResponseMessage response;
            try
            {
                response = await PostWithWaitAsync(
                    targetUrl,
                    payload,
                    invocation.Options.waitTimeout,
                    startedAt,
                    spinner,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                if (!serverAnswered
                    && _lastConnectionFailure == TranspileConnectionFailure.None)
                {
                    _lastConnectionFailure =
                        TranspileConnectionFailure.NoResponseWithinTimeout;
                }

                return null;
            }
            catch (HttpRequestException exception)
            {
                var socketError = FindInnerException<System.Net.Sockets.SocketException>(
                    exception);
                var nextConnectionRefusedCount =
                    socketError?.SocketErrorCode
                        == System.Net.Sockets.SocketError.ConnectionRefused
                        ? connectionRefusedCount + 1
                        : connectionRefusedCount;
                var decision = _retryPolicy.Classify(
                    exception,
                    targetUrl,
                    toolLabel,
                    retryCount + 1,
                    nextConnectionRefusedCount);

                if (decision.Kind == TranspileRetryKind.None)
                {
                    return FailurePayload(exception);
                }

                if (!serverAnswered)
                {
                    _lastConnectionFailure = ToConnectionFailure(decision.Kind);
                }

                retryCount++;
                connectionRefusedCount = nextConnectionRefusedCount;

                if (decision.ShouldAbort)
                {
                    WriteRaw(decision.Message, decision.MessageColor);
                    return null;
                }

                WriteRetry(decision);
                if (invocation.Options.debug
                    && decision.Kind == TranspileRetryKind.SslException)
                {
                    Console.WriteLine($"DEBUG: {exception.Message}");
                }

                if (!await DelayWithinBudgetAsync(
                        decision.Delay,
                        invocation.Options.waitTimeout,
                        startedAt,
                        cancellationToken,
                        spinner))
                {
                    return null;
                }

                continue;
            }

            using (response)
            {
                // Any HTTP response, even a gateway status, means the workload
                // is reachable: this is never a complete timeout (D29).
                serverAnswered = true;
                _lastConnectionFailure = TranspileConnectionFailure.None;
                var responseContent = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                var decision = _retryPolicy.Classify(
                    response.StatusCode,
                    responseContent,
                    toolLabel,
                    retryCount + 1);
                if (decision.ShouldRetry)
                {
                    retryCount++;
                    WriteRetry(decision);
                    if (invocation.Options.debug)
                    {
                        Console.WriteLine(
                            $"DEBUG: Response body: {responseContent}");
                    }

                    if (!await DelayWithinBudgetAsync(
                            decision.Delay,
                            invocation.Options.waitTimeout,
                            startedAt,
                            cancellationToken,
                            spinner))
                    {
                        return null;
                    }

                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    return FailurePayload(
                        new Exception(
                            $"Proxy request failed with status {response.StatusCode}: {responseContent}"));
                }

                return await ParseResponseAsync(
                    invocation,
                    targetUrl,
                    response,
                    responseContent,
                    startedAt,
                    cancellationToken);
            }
        }

        if (retryCount >= RetryPolicy.MaxRetries)
        {
            WriteRaw(
                RetryPolicy.RetriesExhaustedMessage(targetUrl),
                ConsoleColor.Red);
        }

        return null;
    }

    private async Task<HttpResponseMessage> PostWithWaitAsync(
        Uri targetUrl,
        TranspilePayload payload,
        int waitTimeout,
        long startedAt,
        BootSpinner spinner,
        CancellationToken cancellationToken)
    {
        var remaining = Remaining(waitTimeout, startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            return await Task.FromCanceled<HttpResponseMessage>(
                new CancellationToken(canceled: true));
        }

        using var requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(remaining);
        var postTask = _httpClient.PostAsJsonAsync(
            targetUrl,
            payload,
            WireJsonOptions,
            requestCancellation.Token);

        while (!postTask.IsCompleted)
        {
            remaining = Remaining(waitTimeout, startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                requestCancellation.Cancel();
                break;
            }

            var tick = remaining < TimeSpan.FromMilliseconds(500)
                ? remaining
                : TimeSpan.FromMilliseconds(500);
            await _delayAsync(tick, cancellationToken);
            spinner.Tick(ElapsedMilliseconds(startedAt));
        }

        return await postTask;
    }

    private async Task<TranspilePayload> ParseResponseAsync(
        CliInvocation invocation,
        Uri targetUrl,
        HttpResponseMessage response,
        string responseContent,
        long startedAt,
        CancellationToken cancellationToken)
    {
        var trimmedContent = responseContent?.TrimStart() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(responseContent)
            || (!trimmedContent.StartsWith("{", StringComparison.Ordinal)
                && !trimmedContent.StartsWith("[", StringComparison.Ordinal)))
        {
            var statusText = responseContent?.Trim() ?? string.Empty;
            var isStatusMessage =
                statusText.Equals("True", StringComparison.OrdinalIgnoreCase)
                || statusText.Equals("False", StringComparison.OrdinalIgnoreCase)
                || statusText.Contains("transpiler", StringComparison.Ordinal)
                || statusText.Contains("inactive", StringComparison.Ordinal);
            return FailurePayload(
                new Exception(
                    isStatusMessage
                        ? $"Proxy server returned transpiler status message: {statusText}"
                        : $"Proxy server returned non-JSON response: {responseContent}"));
        }

        TranspilePayload responsePayload;
        try
        {
            responsePayload =
                JsonConvert.DeserializeObject<TranspilePayload>(
                    responseContent,
                    ResponseJsonSettings);
        }
        catch (Exception exception)
            when (exception is Newtonsoft.Json.JsonException
                  or SerializationException)
        {
            return JsonParseFailure(response, responseContent, exception);
        }

        if (responsePayload is null)
        {
            return JsonParseFailure(
                response,
                responseContent,
                new JsonSerializationException(
                    "The proxy response deserialized to null."));
        }

        if (!string.IsNullOrEmpty(responsePayload.TaskId)
            && string.Equals(
                responsePayload.TaskStatus,
                "pending",
                StringComparison.OrdinalIgnoreCase))
        {
            responsePayload = await PollAsyncTaskAsync(
                invocation,
                targetUrl,
                responsePayload,
                startedAt,
                cancellationToken);
        }

        return responsePayload;
    }

    private async Task<TranspilePayload> PollAsyncTaskAsync(
        CliInvocation invocation,
        Uri targetUrl,
        TranspilePayload pendingPayload,
        long startedAt,
        CancellationToken cancellationToken)
    {
        var pollUrl = new Uri(
            $"{targetUrl.ToString().TrimEnd('/')}/task/{pendingPayload.TaskId}");

        if (invocation.Options.debug)
        {
            Console.WriteLine(
                $"DEBUG: Transpiler returned TaskId={pendingPayload.TaskId}, switching to poll mode");
        }

        CliLog.LogLine(
            "Transpiler processing asynchronously, polling for result...",
            ConsoleColor.Cyan);

        while (ElapsedMilliseconds(startedAt) < invocation.Options.waitTimeout)
        {
            if (!await DelayWithinBudgetAsync(
                    RetryPolicy.AsyncPollDelay,
                    invocation.Options.waitTimeout,
                    startedAt,
                    cancellationToken))
            {
                break;
            }

            try
            {
                using var pollCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                var remaining = Remaining(
                    invocation.Options.waitTimeout,
                    startedAt);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                pollCancellation.CancelAfter(remaining);
                using var pollResponse = await _httpClient.GetAsync(
                    pollUrl,
                    pollCancellation.Token);

                if (pollResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    return FailurePayload(
                        new Exception(
                            "Async task not found on transpiler (container may have restarted). Please retry."));
                }

                if (!pollResponse.IsSuccessStatusCode)
                {
                    if (invocation.Options.debug)
                    {
                        Console.WriteLine(
                            $"DEBUG: Poll returned {pollResponse.StatusCode}, retrying...");
                    }

                    continue;
                }

                var pollContent = await pollResponse.Content.ReadAsStringAsync(
                    cancellationToken);
                TranspilePayload pollPayload;
                try
                {
                    pollPayload =
                        JsonConvert.DeserializeObject<TranspilePayload>(
                            pollContent,
                            ResponseJsonSettings);
                }
                catch (Exception exception)
                    when (exception is Newtonsoft.Json.JsonException
                          or SerializationException)
                {
                    return JsonParseFailure(
                        pollResponse,
                        pollContent,
                        exception);
                }

                if (string.Equals(
                        pollPayload?.TaskStatus,
                        "completed",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        pollPayload?.TaskStatus,
                        "failed",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return pollPayload;
                }

                if (invocation.Options.debug)
                {
                    Console.WriteLine(
                        $"DEBUG: Task still pending (elapsed: {ElapsedMilliseconds(startedAt):0}ms)");
                }
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            catch (HttpRequestException exception)
            {
                if (invocation.Options.debug)
                {
                    Console.WriteLine($"DEBUG: Poll error: {exception.Message}");
                }
            }
        }

        return FailurePayload(
            new Exception(
                "Timed out waiting for async transpiler task to complete"));
    }

    private async Task<bool> DelayWithinBudgetAsync(
        TimeSpan requestedDelay,
        int waitTimeout,
        long startedAt,
        CancellationToken cancellationToken,
        BootSpinner spinner = null)
    {
        var remaining = Remaining(waitTimeout, startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            return false;
        }

        var actualDelay = requestedDelay < remaining
            ? requestedDelay
            : remaining;
        await _delayAsync(actualDelay, cancellationToken);
        spinner?.Tick(ElapsedMilliseconds(startedAt));
        return ElapsedMilliseconds(startedAt) < waitTimeout;
    }

    private async Task WaitForCookTimeoutAsync(
        int waitTimeout,
        long startedAt,
        BootSpinner spinner,
        CancellationToken cancellationToken)
    {
        var remaining = Remaining(waitTimeout, startedAt);
        while (remaining > TimeSpan.Zero)
        {
            var tick = remaining < TimeSpan.FromMilliseconds(500)
                ? remaining
                : TimeSpan.FromMilliseconds(500);
            await _delayAsync(tick, cancellationToken);
            spinner.Tick(ElapsedMilliseconds(startedAt));
            remaining = Remaining(waitTimeout, startedAt);
        }
    }

    private static void FixResponseNames(
        TranspilePayload payload,
        CliInvocation invocation,
        Uri targetUrl)
    {
        payload.Transpiler ??= new Transpiler();
        if (string.IsNullOrEmpty(payload.Transpiler.Name))
        {
            payload.Transpiler.Name = invocation.Transpiler;
            if (invocation.Options.debug)
            {
                Console.WriteLine(
                    $"DEBUG: Remote transpiler returned empty name, setting to: {invocation.Transpiler}");
            }
        }

        payload.Transpiler.LowerHyphenName =
            invocation.LedgerKey
            ?? targetUrl.ToString().SanitizeUrlForFilename();
        if (invocation.Options.debug)
        {
            Console.WriteLine(
                $"DEBUG: Setting response transpiler .zfs name to SANITIZED URL: {payload.Transpiler.LowerHyphenName}");
        }
    }

    private static void PrintLogsAndPromoteErrors(
        TranspilePayload payload,
        CliInvocation invocation)
    {
        if (invocation.SuppressTranspilerErrorOutput || payload.Logs is null)
        {
            return;
        }

        var logLabel = payload.Transpiler?.Name ?? invocation.Transpiler;
        var versionKey = !string.IsNullOrEmpty(invocation.ResolvedVersionKey)
            ? invocation.ResolvedVersionKey
            : ExtractVersionKey(invocation.ResolvedVersionLabel);
        if (!string.IsNullOrEmpty(versionKey))
        {
            logLabel = $"{logLabel} {versionKey}";
        }

        string firstErrorMessage = null;
        foreach (var log in payload.Logs)
        {
            ToolLogPrinter.DisplayLogEntry(
                log,
                invocation.Options.debug,
                logLabel);
            if (firstErrorMessage is null
                && string.Equals(
                    log?.Level,
                    "error",
                    StringComparison.OrdinalIgnoreCase))
            {
                firstErrorMessage = log.Text?.Trim();
            }
        }

        if (firstErrorMessage is not null && payload.Exception is null)
        {
            payload.Exception = new Exception(firstErrorMessage);
        }
    }

    private static TranspileConnectionFailure ToConnectionFailure(
        TranspileRetryKind kind) =>
        kind switch
        {
            TranspileRetryKind.HostNotFound =>
                TranspileConnectionFailure.HostNotFound,
            TranspileRetryKind.ConnectionRefused =>
                TranspileConnectionFailure.ConnectionRefused,
            TranspileRetryKind.ConnectionResetOrUnreachable =>
                TranspileConnectionFailure.NetworkUnreachable,
            TranspileRetryKind.SslException =>
                TranspileConnectionFailure.TlsHandshake,
            _ => TranspileConnectionFailure.None,
        };

    private TranspileClientResult CreateResult(
        CliInvocation invocation,
        TranspilePayload payload)
    {
        var disposition = TranspileOutputDisposition.None;
        if (payload.Exception is null)
        {
            if (invocation.Options.clean)
            {
                disposition = TranspileOutputDisposition.Clean;
            }
            else if (payload.TranspileRequest?.ZippedOutputFileSet?.Length > 0)
            {
                disposition = TranspileOutputDisposition.Save;
            }
        }

        return new TranspileClientResult(
            payload,
            disposition,
            payload.Transpiler?.LowerHyphenName
                ?? invocation.LedgerKey
                ?? invocation.TargetUrl.SanitizeUrlForFilename(),
            invocation.CurrentDirectory,
            invocation.InputFileSetXml,
            invocation.Options.skipClean,
            invocation.Options.debug,
            payload.Exception is null
                ? TranspileConnectionFailure.None
                : _lastConnectionFailure);
    }

    private static void ValidateOutputForDebug(
        TranspilePayload payload,
        bool debug)
    {
        if (!debug)
        {
            return;
        }

        var zippedFileSet = payload.TranspileRequest?.ZippedOutputFileSet;
        if (zippedFileSet is null || zippedFileSet.Length == 0)
        {
            Console.WriteLine(
                "WARNING: Remote server did not return expected output files.");
            Console.WriteLine(
                "  JSON Key: Response should set 'TranspileRequest.ZippedOutputFileSet' with generated files");
            Console.WriteLine(
                "  This will prevent files from being written to the project.");
            return;
        }

        try
        {
            var fileSetXml = zippedFileSet.UnzipToString();
            if (string.IsNullOrEmpty(fileSetXml) || !fileSetXml.Contains('<'))
            {
                return;
            }

            fileSetXml = fileSetXml[fileSetXml.IndexOf('<')..]
                .Replace(
                    "FileContents><?xml ",
                    "FileContents>&lt;?xml",
                    StringComparison.Ordinal);
            var document = new XmlDocument();
            document.LoadXml(fileSetXml);
            var filesWithNoContent = new List<string>();

            foreach (XmlElement fileElement in
                     document.SelectNodes("//FileSetFile"))
            {
                var relativePath =
                    fileElement.SelectSingleNode("RelativePath")?.InnerText;
                if (string.IsNullOrEmpty(relativePath))
                {
                    continue;
                }

                var contentNodes = new[]
                {
                    fileElement.SelectSingleNode("FileContents"),
                    fileElement.SelectSingleNode("ZippedTextFileContents"),
                    fileElement.SelectSingleNode("ZippedFileContents"),
                    fileElement.SelectSingleNode("BinaryFileContents"),
                    fileElement.SelectSingleNode("ZippedBinaryFileContents"),
                };
                if (!contentNodes.Any(
                        node => node is not null
                                && !string.IsNullOrEmpty(node.InnerText)))
                {
                    filesWithNoContent.Add(relativePath);
                }
            }

            if (filesWithNoContent.Count > 0)
            {
                Console.WriteLine(
                    "WARNING: ZFS contains file entries with no content:");
                foreach (var file in filesWithNoContent)
                {
                    Console.WriteLine($"  - {file}");
                }

                Console.WriteLine(
                    "  Files without content cannot be properly cleaned during 'effortless clean'");
                Console.WriteLine(
                    "  This may indicate the transpiler is not generating complete file entries");
            }
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                $"WARNING: Error validating ZFS content: {exception.Message}");
        }
    }

    private static TranspilePayload JsonParseFailure(
        HttpResponseMessage response,
        string responseContent,
        Exception exception) =>
        FailurePayload(
            new Exception(
                $"Failed to parse proxy response as JSON. Status: {response.StatusCode}, "
                + $"Content-Type: {response.Content.Headers.ContentType}, "
                + $"Response content: {responseContent}",
                exception));

    private static TranspilePayload FailurePayload(Exception exception) =>
        new()
        {
            Exception = exception,
        };

    private static string ExtractVersionKey(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return null;
        }

        return label
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(
                part => part.Length > 1
                        && part[0] == 'v'
                        && char.IsDigit(part[1]));
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.First(value => !string.IsNullOrEmpty(value));

    private TimeSpan Remaining(int waitTimeout, long startedAt)
    {
        var remainingMilliseconds =
            waitTimeout - ElapsedMilliseconds(startedAt);
        return remainingMilliseconds <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(remainingMilliseconds);
    }

    private double ElapsedMilliseconds(long startedAt) =>
        _timeProvider.GetElapsedTime(startedAt).TotalMilliseconds;

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

    private static void WriteRetry(TranspileRetryDecision decision)
    {
        if (decision.MessageColor.HasValue)
        {
            CliLog.LogLine(decision.Message, decision.MessageColor.Value);
        }
        else
        {
            CliLog.LogLine(decision.Message);
        }
    }

    private static void WriteRaw(string message, ConsoleColor? color)
    {
        var previousColor = Console.ForegroundColor;
        if (color.HasValue)
        {
            Console.ForegroundColor = color.Value;
        }

        Console.WriteLine(message);
        Console.ForegroundColor = previousColor;
    }

    private static string NewPayloadId() =>
        Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private static JsonSerializerOptions CreateWireJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(
            typeInfo =>
            {
                if (typeInfo.Type != typeof(TranspilePayload))
                {
                    return;
                }

                RemoveProperty(typeInfo, "senderName");
                RemoveProperty(typeInfo, "logs");
                RemoveProperty(typeInfo, "taskId");
                RemoveProperty(typeInfo, "taskStatus");
                RemoveProperty(typeInfo, "exception");
                RemoveProperty(typeInfo, "errorMessage");
                RemoveProperty(typeInfo, "sSoTmeProject");
                RemoveProperty(typeInfo, "sSoTmeKey");
            });

        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = resolver,
        };
    }

    private static JsonSerializerSettings CreateResponseJsonSettings()
    {
        var settings = new JsonSerializerSettings();
        settings.Converters.Add(new ExceptionJsonConverter());
        return settings;
    }

    private static void RemoveProperty(JsonTypeInfo typeInfo, string jsonName)
    {
        var property = typeInfo.Properties.FirstOrDefault(
            candidate => string.Equals(
                candidate.Name,
                jsonName,
                StringComparison.Ordinal));
        if (property is not null)
        {
            typeInfo.Properties.Remove(property);
        }
    }

    private static void ValidateInvocation(CliInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (invocation.Options is null)
        {
            throw new ArgumentException(
                "The CLI invocation has no parsed options.",
                nameof(invocation));
        }

        if (string.IsNullOrWhiteSpace(invocation.TargetUrl))
        {
            throw new ArgumentException(
                "The CLI invocation has no target URL.",
                nameof(invocation));
        }

        if (!Uri.TryCreate(
                invocation.TargetUrl,
                UriKind.Absolute,
                out var targetUri)
            || targetUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException(
                $"The CLI invocation target URL is invalid: {invocation.TargetUrl}",
                nameof(invocation));
        }

        if (string.IsNullOrWhiteSpace(invocation.Transpiler))
        {
            throw new ArgumentException(
                "The CLI invocation has no resolved transpiler name.",
                nameof(invocation));
        }

        if (invocation.InputFileSetXml is null)
        {
            throw new ArgumentException(
                "The CLI invocation has no input FileSet XML.",
                nameof(invocation));
        }

        if (invocation.Options.waitTimeout <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(invocation),
                invocation.Options.waitTimeout,
                "The CLI wait timeout must be positive.");
        }
    }

    private sealed class BootSpinner
    {
        private static readonly string[] Dots = ["   ", ".  ", ".. ", "..."];
        private readonly string _toolName;
        private bool _shown;
        private int _messageLength;
        private int _dotIndex;

        public BootSpinner(string toolName)
        {
            _toolName = toolName;
        }

        public void Tick(double elapsedMilliseconds)
        {
            if (elapsedMilliseconds < 15_000)
            {
                return;
            }

            if (!_shown)
            {
                var message =
                    $"Waiting for the remote transpiler '{_toolName}' to boot up    ";
                Console.Write(message);
                _messageLength = message.Length;
                _shown = true;
            }

            Console.Write("\b\b\b" + Dots[_dotIndex % Dots.Length]);
            _dotIndex++;
        }

        public void Clear()
        {
            if (!_shown)
            {
                return;
            }

            _shown = false;
            Console.Write("\r" + new string(' ', _messageLength) + "\r");
            Console.WriteLine();
            Console.WriteLine();
        }
    }

    private sealed class ExceptionJsonConverter : JsonConverter<Exception>
    {
        public override bool CanWrite => false;

        public override Exception ReadJson(
            JsonReader reader,
            Type objectType,
            Exception existingValue,
            bool hasExistingValue,
            Newtonsoft.Json.JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            var token = JToken.Load(reader);
            return ReadException(token);
        }

        public override void WriteJson(
            JsonWriter writer,
            Exception value,
            Newtonsoft.Json.JsonSerializer serializer) =>
            throw new NotSupportedException();

        private static Exception ReadException(JToken token)
        {
            if (token.Type == JTokenType.String)
            {
                return new RemoteTranspilerException(
                    token.Value<string>(),
                    remoteStackTrace: null,
                    innerException: null);
            }

            if (token is not JObject exceptionObject)
            {
                return new RemoteTranspilerException(
                    token.ToString(Newtonsoft.Json.Formatting.None),
                    remoteStackTrace: null,
                    innerException: null);
            }

            var message = GetValue(exceptionObject, "Message")?.Value<string>()
                ?? "Remote transpiler exception";
            var stackTrace =
                GetValue(exceptionObject, "StackTrace")?.Value<string>()
                ?? GetValue(
                    exceptionObject,
                    "StackTraceString")?.Value<string>();
            var innerToken = GetValue(exceptionObject, "InnerException");
            var innerException =
                innerToken is null || innerToken.Type == JTokenType.Null
                    ? null
                    : ReadException(innerToken);
            return new RemoteTranspilerException(
                message,
                stackTrace,
                innerException);
        }

        private static JToken GetValue(JObject value, string name) =>
            value.Properties()
                .FirstOrDefault(
                    property => property.Name.Equals(
                        name,
                        StringComparison.OrdinalIgnoreCase))
                ?.Value;
    }

    private sealed class RemoteTranspilerException : Exception
    {
        private readonly string _remoteStackTrace;

        public RemoteTranspilerException(
            string message,
            string remoteStackTrace,
            Exception innerException)
            : base(message, innerException)
        {
            _remoteStackTrace = remoteStackTrace;
        }

        public override string StackTrace =>
            _remoteStackTrace ?? base.StackTrace;
    }
}
