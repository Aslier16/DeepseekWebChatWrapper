using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Wasmtime;
using Module = Wasmtime.Module;

namespace DeepseekWebChatWrapper.Core;

public class DsWebChatClient : IDisposable, IAsyncDisposable
{
    private readonly Engine _engine;
    private readonly Module _module;
    private readonly Store _store;
    private readonly Linker _linker;
    private readonly Instance _instance;
    private readonly Memory _memory;

    private readonly Func<int, int> _addToStack;
    private readonly Func<int, int, int> _alloc;
    private readonly Action<int, int, int> _free;
    private readonly Function _solve;

    private readonly HttpClient _httpClient;
    private const string BaseUri = "https://chat.deepseek.com";
    private const string CreatePowChallengeUri = "/api/v0/chat/create_pow_challenge";
    private const string CreateSessionUri = "/api/v0/chat_session/create";
    private const string CompletionPath = "/api/v0/chat/completion";
    private const string PowHeaderName = "x-ds-pow-response";
    private const string AlgorithmName = "DeepSeekHashV1";

    private readonly string _authToken;
    private readonly string _dsSessionId;

    private readonly JsonSerializerOptions _sharedJsonSerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly CreatePowChallengePayload _createPowChallengePayload = new(TargetPath: "/api/v0/chat/completion");

    private readonly DsWebClientOptions _extraHeaderOptions;
    private readonly RateLimiter _rateLimiter;

    private readonly SemaphoreSlim _powChallengeSemaphore = new SemaphoreSlim(1, 1);
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatClient"/> class.
    /// </summary>
    /// <param name="authToken">The web app's Authorization token. The "Bearer " prefix is optional.</param>
    /// <param name="dsSessionId">The value of the <c>ds_session_id</c> cookie.</param>
    /// <param name="extraHeaderOptions">Optional header values and request timeouts.</param>
    /// <param name="rateLimiter">Rate limiter used for session creation. A default limiter is used when omitted.</param>
    /// <exception cref="ArgumentException"><paramref name="authToken"/> or <paramref name="dsSessionId"/> is null, empty, or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A timeout in <paramref name="extraHeaderOptions"/> is invalid.</exception>
    /// <exception cref="DsWebChatInitializationException">The embedded proof-of-work WASM runtime could not be initialized.</exception>
    public DsWebChatClient(string authToken, string dsSessionId, DsWebClientOptions? extraHeaderOptions = null,
        RateLimiter? rateLimiter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(dsSessionId);

        _extraHeaderOptions = extraHeaderOptions ?? new DsWebClientOptions();
        ValidateOptions(_extraHeaderOptions);

        _rateLimiter = rateLimiter ?? new TokenBucketRateLimiter(
            new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,
                TokensPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });

        _authToken = authToken;
        _dsSessionId = dsSessionId;

        var assembly = typeof(DsWebChatClient).Assembly;
        using var wasmStream =
            assembly.GetManifestResourceStream("DeepseekWebChatWrapper.Core.Assets.ds_webchat_wasm.wasm") ??
            throw new DsWebChatInitializationException(
                "Initialize",
                "Failed to load the embedded proof-of-work WASM resource " +
                "'DeepseekWebChatWrapper.Core.Assets.ds_webchat_wasm.wasm'. The package may be corrupted; " +
                "reinstall Aslier.DeepseekWebChatWrapper.");

        Engine? engine = null;
        Module? module = null;
        Store? store = null;
        Linker? linker = null;

        try
        {
            engine = new Engine();
            module = Module.FromStream(engine, "pow_solver", wasmStream);
            store = new Store(engine);
            linker = new Linker(engine);
            var instance = linker.Instantiate(store, module);
            var memory = instance.GetMemory("memory") ??
                         throw new DsWebChatInitializationException(
                             "Initialize",
                             "The embedded proof-of-work WASM module did not expose the required 'memory' export. " +
                             "The package may be corrupted or incompatible with this version of the wrapper.");
            var addToStack = instance.GetFunction<int, int>("__wbindgen_add_to_stack_pointer") ??
                             throw new DsWebChatInitializationException(
                                 "Initialize",
                                 "The embedded proof-of-work WASM module did not expose the required " +
                                 "'__wbindgen_add_to_stack_pointer' function. The package may be corrupted or " +
                                 "incompatible with this version of the wrapper.");
            var alloc = instance.GetFunction<int, int, int>("__wbindgen_export_0") ??
                        throw new DsWebChatInitializationException(
                            "Initialize",
                            "The embedded proof-of-work WASM module did not expose the required " +
                            "'__wbindgen_export_0' function. The package may be corrupted or incompatible with " +
                            "this version of the wrapper.");
            var free = instance.GetAction<int, int, int>("__wbindgen_export_2") ??
                       throw new DsWebChatInitializationException(
                           "Initialize",
                           "The embedded proof-of-work WASM module did not expose the required " +
                           "'__wbindgen_export_2' function. The package may be corrupted or incompatible with " +
                           "this version of the wrapper.");
            var solve = instance.GetFunction("wasm_solve") ??
                        throw new DsWebChatInitializationException(
                            "Initialize",
                            "The embedded proof-of-work WASM module did not expose the required 'wasm_solve' " +
                            "function. The package may be corrupted or incompatible with this version of the wrapper.");

            _engine = engine;
            _module = module;
            _store = store;
            _linker = linker;
            _instance = instance;
            _memory = memory;
            _addToStack = addToStack;
            _alloc = alloc;
            _free = free;
            _solve = solve;
        }
        catch (DsWebChatInitializationException)
        {
            DisposePartials(linker, store, module, engine);
            throw;
        }
        catch (Exception ex)
        {
            DisposePartials(linker, store, module, engine);
            throw new DsWebChatInitializationException(
                "Initialize",
                "Failed to initialize the embedded proof-of-work WASM runtime. The package may be corrupted or " +
                "the installed Wasmtime version may be incompatible. See the inner exception for details.",
                ex);
        }

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUri),
            // Timeouts are enforced per request instead: a single streamed answer can take minutes, which the
            // default 100 second HttpClient.Timeout would cut off.
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static void ValidateOptions(DsWebClientOptions options)
    {
        ValidateTimeout(options.RequestTimeout, nameof(DsWebClientOptions.RequestTimeout));
        ValidateTimeout(options.CompletionTimeout, nameof(DsWebClientOptions.CompletionTimeout));

        ArgumentException.ThrowIfNullOrWhiteSpace(options.UserAgent, nameof(DsWebClientOptions.UserAgent));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.XClientBundleId,
            nameof(DsWebClientOptions.XClientBundleId));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.XClientLocale,
            nameof(DsWebClientOptions.XClientLocale));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.XClientPlatform,
            nameof(DsWebClientOptions.XClientPlatform));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.XClientVersion,
            nameof(DsWebClientOptions.XClientVersion));
    }

    private static void ValidateTimeout(TimeSpan timeout, string paramName)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
            return;

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(paramName, timeout,
                "The timeout must be a positive TimeSpan or Timeout.InfiniteTimeSpan.");
        }

        var maxTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
        if (timeout > maxTimeout)
        {
            throw new ArgumentOutOfRangeException(paramName, timeout,
                $"The timeout must not exceed {maxTimeout} because CancellationTokenSource.CancelAfter " +
                "does not support larger values.");
        }
    }

    private static void DisposePartials(Linker? linker, Store? store, Module? module, Engine? engine)
    {
        linker?.Dispose();
        store?.Dispose();
        module?.Dispose();
        engine?.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(DsWebChatClient));
    }

    protected HttpRequestMessage CreateDsRequest()
    {
        var request = new HttpRequestMessage();
        request.Method = HttpMethod.Post;
        request.Headers.Add("Authorization",
            _authToken.StartsWith("Bearer ") ? _authToken : $"Bearer {_authToken}");
        request.Headers.Add("Cookie", $"ds_session_id={_dsSessionId}");
        request.Headers.Add("User-Agent", _extraHeaderOptions.UserAgent);
        request.Headers.Add("x-client-bundle-id", _extraHeaderOptions.XClientBundleId);
        request.Headers.Add("x-client-locale", _extraHeaderOptions.XClientLocale);
        request.Headers.Add("x-client-platform", _extraHeaderOptions.XClientPlatform);
        request.Headers.Add("x-client-timezone-offset", _extraHeaderOptions.XClientTimeZoneOffset.ToString());
        request.Headers.Add("x-client-version", _extraHeaderOptions.XClientVersion);
        if (!string.IsNullOrEmpty(_extraHeaderOptions.XDeviceId))
            request.Headers.Add("x-device-id", _extraHeaderOptions.XDeviceId);

        return request;
    }

    /// <summary>
    /// Sends a request that is expected to be small and fully buffered, bounded by
    /// <see cref="DsWebClientOptions.RequestTimeout"/>.
    /// </summary>
    private async Task<HttpResponseMessage> SendBufferedAsync(HttpRequestMessage request, string operation,
        CancellationToken cancellationToken)
    {
        var requestUri = GetAbsoluteRequestUri(request);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_extraHeaderOptions.RequestTimeout);

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            await EnsureSuccessStatusCodeAsync(response, operation, requestUri, timeoutCts.Token)
                .ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw new DsWebChatTimeoutException(operation, _extraHeaderOptions.RequestTimeout,
                $"{operation} failed: the request timed out after " +
                $"{FormatTimeout(_extraHeaderOptions.RequestTimeout)} (DsWebClientOptions.RequestTimeout). " +
                "Increase RequestTimeout or pass a CancellationToken for caller-controlled cancellation.",
                ex);
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            throw;
        }
        catch (DsWebChatException)
        {
            response?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            response?.Dispose();
            throw new DsWebChatTransportException(operation, requestUri,
                $"{operation} failed: could not reach DeepSeek at {requestUri?.ToString() ?? BaseUri}. " +
                "See the inner exception for network details.",
                ex);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    private static async Task EnsureSuccessStatusCodeAsync(HttpResponseMessage response, string operation,
        Uri? requestUri, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var snippet = await DsErrorHelper.ReadSnippetAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        throw DsErrorHelper.CreateHttpStatusException(operation, response, requestUri, snippet);
    }

    private static async Task<JsonDocument> ReadJsonDocumentAsync(HttpResponseMessage response, string operation,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            var requestUri = response.RequestMessage is null
                ? null
                : GetAbsoluteRequestUri(response.RequestMessage);
            throw new DsWebChatTransportException(operation, requestUri,
                $"{operation} failed: the response body could not be read. See the inner exception for details.",
                ex);
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            var snippet = DsErrorHelper.Truncate(body);
            throw new DsWebChatProtocolException(operation,
                $"{operation} failed: DeepSeek returned invalid JSON. " +
                $"Response snippet: {DsErrorHelper.DescribeSnippet(snippet)}",
                snippet, innerException: ex);
        }
    }

    private static Uri? GetAbsoluteRequestUri(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null)
            return null;

        return uri.IsAbsoluteUri ? uri : new Uri(new Uri(BaseUri), uri);
    }

    private static string FormatTimeout(TimeSpan timeout)
    {
        return timeout == Timeout.InfiniteTimeSpan ? "an infinite timeout" : timeout.ToString("c");
    }

    /// <summary>
    /// Requests a proof-of-work challenge from DeepSeek.
    /// </summary>
    /// <exception cref="DsWebChatTransportException">The request could not reach DeepSeek or its response could not be read.</exception>
    /// <exception cref="DsWebChatTimeoutException">The request exceeded <see cref="DsWebClientOptions.RequestTimeout"/>.</exception>
    /// <exception cref="DsWebChatAuthenticationException">The auth token or session cookie was rejected.</exception>
    /// <exception cref="DsWebChatRateLimitException">DeepSeek rate limited the request.</exception>
    /// <exception cref="DsWebChatApiException">DeepSeek returned an error status or business error code.</exception>
    /// <exception cref="DsWebChatProtocolException">The response did not match the expected format.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<ChallengeResponse> CreatePowChallengeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        const string operation = nameof(CreatePowChallengeAsync);

        // Typical response from the server looks like this:
        //{
        //     "code": 0,
        //     "msg": "",
        //     "data": {
        //         "biz_code": 0,
        //         "biz_msg": "",
        //         "biz_data": {
        //             "challenge": {
        //                 "algorithm": "DeepSeekHashV1",
        //                 "challenge": "83ffcc37d51b3d315084ff0950055b6f8039c15e7a733853cd3fdebc3d0acdb2", 
        //                 "salt": "156c3912bacae9c108c1",
        //                 "signature": "25a125ea6dfb9781a6f33c14ead9de8443d111289683bf0cbb95a0ffab3d49f6",
        //                 "difficulty": 144000,
        //                 "expire_at": 1293113295022,
        //                 "expire_after": 300000,
        //                 "target_path": "/api/v0/chat/completion"
        //             }
        //         }
        //     }
        // }
        var request = CreateDsRequest();
        request.RequestUri = new Uri(CreatePowChallengeUri, UriKind.Relative);
        request.Content = JsonContent.Create(_createPowChallengePayload, options: _sharedJsonSerializerOptions);

        using var response = await SendBufferedAsync(request, operation, cancellationToken).ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, operation, cancellationToken).ConfigureAwait(false);

        var snippet = DsErrorHelper.Truncate(doc.RootElement.GetRawText());
        DsErrorHelper.ThrowIfApiError(doc.RootElement, operation);

        var challengeElement = DsErrorHelper.GetRequiredProperty(doc.RootElement, operation, "data", "biz_data",
            "challenge");

        ChallengeResponse challenge;
        try
        {
            challenge = challengeElement.Deserialize<ChallengeResponse>(_sharedJsonSerializerOptions) ??
                        throw new DsWebChatProtocolException(operation,
                            $"{operation} failed: the response field 'data.biz_data.challenge' was null. " +
                            $"Response snippet: {DsErrorHelper.DescribeSnippet(snippet)}",
                            snippet);
        }
        catch (DsWebChatException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new DsWebChatProtocolException(operation,
                $"{operation} failed: the response field 'data.biz_data.challenge' could not be deserialized. " +
                $"Response snippet: {DsErrorHelper.DescribeSnippet(snippet)}",
                snippet, innerException: ex);
        }

        ValidateChallengeResponse(challenge, operation, snippet);
        return challenge;
    }

    private static void ValidateChallengeResponse(ChallengeResponse challenge, string operation, string snippet)
    {
        RequireNonEmpty(challenge.Algorithm, "data.biz_data.challenge.algorithm");
        RequireNonEmpty(challenge.Challenge, "data.biz_data.challenge.challenge");
        RequireNonEmpty(challenge.Salt, "data.biz_data.challenge.salt");
        RequireNonEmpty(challenge.Signature, "data.biz_data.challenge.signature");
        RequireNonEmpty(challenge.TargetPath, "data.biz_data.challenge.target_path");

        if (challenge.Difficulty <= 0)
        {
            ThrowInvalidField("data.biz_data.challenge.difficulty",
                $"must be positive, but was {challenge.Difficulty}");
        }

        if (challenge.ExpireAt <= 0)
        {
            ThrowInvalidField("data.biz_data.challenge.expire_at",
                $"must be positive, but was {challenge.ExpireAt}");
        }

        if (challenge.ExpireAfter <= 0)
        {
            ThrowInvalidField("data.biz_data.challenge.expire_after",
                $"must be positive, but was {challenge.ExpireAfter}");
        }

        return;

        void RequireNonEmpty(string? value, string field)
        {
            if (string.IsNullOrWhiteSpace(value))
                ThrowInvalidField(field, "is empty");
        }

        void ThrowInvalidField(string field, string reason)
        {
            throw new DsWebChatProtocolException(operation,
                $"{operation} failed: the response field '{field}' {reason}. " +
                $"Response snippet: {DsErrorHelper.DescribeSnippet(snippet)}",
                snippet);
        }
    }

    /// <summary>
    /// Async wrapper for SolvePowChallenge to prevent concurrent access to the Wasm module, which is not thread-safe.
    /// Wrap challenge solving in a semaphore to prevent concurrent access to the Wasm module, which is not thread-safe.
    /// </summary>
    /// <param name="challengeResponse"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"><paramref name="challengeResponse"/> is null.</exception>
    /// <exception cref="ArgumentException">A required challenge field is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The challenge difficulty or expiration is not positive.</exception>
    /// <exception cref="DsWebChatPowException">The embedded solver could not solve the challenge.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<double> SolvePowChallengeAsync(ChallengeResponse challengeResponse,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(challengeResponse);
        ValidateChallengeInput(challengeResponse);

        await _powChallengeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SolvePowChallenge(challengeResponse), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _powChallengeSemaphore.Release();
        }
    }

    private static void ValidateChallengeInput(ChallengeResponse challenge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge.Challenge, nameof(challenge.Challenge));
        ArgumentException.ThrowIfNullOrWhiteSpace(challenge.Salt, nameof(challenge.Salt));

        if (challenge.Difficulty <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(challenge.Difficulty), challenge.Difficulty,
                "The challenge difficulty must be positive.");
        }

        if (challenge.ExpireAt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(challenge.ExpireAt), challenge.ExpireAt,
                "The challenge expiration timestamp must be positive.");
        }
    }

    private double SolvePowChallenge(ChallengeResponse challengeResponse)
    {
        const string operation = nameof(SolvePowChallengeAsync);

        try
        {
            byte[] challengeBytes = Encoding.UTF8.GetBytes(challengeResponse.Challenge);
            int challengePtr = _alloc.Invoke(challengeBytes.Length, 1);
            _memory.WriteString(challengePtr, challengeResponse.Challenge);

            string prefix = $"{challengeResponse.Salt}_{challengeResponse.ExpireAt}_";
            byte[] prefixBytes = Encoding.UTF8.GetBytes(prefix);
            int prefixPtr = _alloc.Invoke(prefixBytes.Length, 1);
            _memory.WriteString(prefixPtr, prefix);

            int returnPtr = _addToStack.Invoke(-16);
            _solve.Invoke(returnPtr, challengePtr, challengeBytes.Length, prefixPtr, prefixBytes.Length,
                challengeResponse.Difficulty);
            int status = _memory.ReadInt32(returnPtr);
            double answer = _memory.ReadDouble(returnPtr + 8);
            _addToStack.Invoke(16);

            if (status == 0)
            {
                throw new DsWebChatPowException(operation,
                    $"{operation} failed: the DeepSeekHashV1 solver did not find a solution for difficulty " +
                    $"{challengeResponse.Difficulty}. The challenge may be malformed or the embedded WASM module " +
                    "may be incompatible with this version of the wrapper.",
                    challengeResponse.Difficulty);
            }

            return answer;
        }
        catch (DsWebChatPowException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new DsWebChatPowException(operation,
                $"{operation} failed while executing the embedded DeepSeekHashV1 solver for difficulty " +
                $"{challengeResponse.Difficulty}. The WASM module may be incompatible with this package version. " +
                "See the inner exception for details.",
                challengeResponse.Difficulty, ex);
        }
    }

    /// <summary>
    /// Creates a new DeepSeek chat session and returns its id.
    /// </summary>
    /// <exception cref="DsWebChatRateLimitException">The client's local rate limiter rejected the request, or DeepSeek rate limited it.</exception>
    /// <exception cref="DsWebChatTransportException">The request could not reach DeepSeek or its response could not be read.</exception>
    /// <exception cref="DsWebChatTimeoutException">The request exceeded <see cref="DsWebClientOptions.RequestTimeout"/>.</exception>
    /// <exception cref="DsWebChatAuthenticationException">The auth token or session cookie was rejected.</exception>
    /// <exception cref="DsWebChatApiException">DeepSeek returned an error status or business error code.</exception>
    /// <exception cref="DsWebChatProtocolException">The response did not match the expected format.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<string> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        const string operation = nameof(CreateSessionAsync);

        // Typical response from the server looks like this:
        // {
        //     "code": 0,
        //     "msg": "",
        //     "data": {
        //         "biz_code": 0,
        //         "biz_msg": "",
        //         "biz_data": {
        //             "chat_session": {
        //                 "id": "374bc632-a53b-4861-ac71-a5719d91293c",
        //                 "title": null,
        //                 "title_type": "WIP",
        //                 "model_type": "default",
        //                 "pinned": false,
        //                 "updated_at": 1793122295.013,
        //                 "seq_id": 265276195,
        //                 "agent": "chat",
        //                 "version": 0,
        //                 "is_empty": true,
        //                 "current_message_id": null,
        //                 "inserted_at": 1793112925.013
        //             },
        //             "ttl_seconds": 259200
        //         }
        //     }
        // }
        using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
        {
            throw new DsWebChatRateLimitException(operation,
                $"{operation} failed: the client's local rate limiter rejected the request. " +
                "Wait before retrying, or construct DsWebChatClient with a RateLimiter whose limits fit your " +
                "workload (the default allows 20 tokens, replenishes 10 per second, and does not queue).",
                isRemote: false);
        }

        var request = CreateDsRequest();
        request.RequestUri = new Uri(CreateSessionUri, UriKind.Relative);
        request.Content = JsonContent.Create(new { }, options: _sharedJsonSerializerOptions);

        using var response = await SendBufferedAsync(request, operation, cancellationToken).ConfigureAwait(false);
        using var doc = await ReadJsonDocumentAsync(response, operation, cancellationToken).ConfigureAwait(false);

        var snippet = DsErrorHelper.Truncate(doc.RootElement.GetRawText());
        DsErrorHelper.ThrowIfApiError(doc.RootElement, operation);

        var idElement = DsErrorHelper.GetRequiredProperty(doc.RootElement, operation, "data", "biz_data",
            "chat_session", "id");
        var sessionId = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new DsWebChatProtocolException(operation,
                $"{operation} failed: the response field 'data.biz_data.chat_session.id' is missing, not a " +
                $"string, or empty. Response snippet: {DsErrorHelper.DescribeSnippet(snippet)}",
                snippet);
        }

        return sessionId;
    }

    /// <summary>
    /// Simple wrapper for the completion request. It creates a new chat session and sends the prompt to the server.
    /// </summary>
    /// <param name="prompt"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"><paramref name="prompt"/> is null, empty, or whitespace.</exception>
    /// <exception cref="DsWebChatTransportException">The request could not reach DeepSeek or its response stream could not be read.</exception>
    /// <exception cref="DsWebChatTimeoutException">The streamed completion exceeded <see cref="DsWebClientOptions.CompletionTimeout"/>.</exception>
    /// <exception cref="DsWebChatAuthenticationException">The auth token or session cookie was rejected.</exception>
    /// <exception cref="DsWebChatRateLimitException">DeepSeek rate limited the request.</exception>
    /// <exception cref="DsWebChatApiException">DeepSeek returned an error status, business error code, or an SSE error event.</exception>
    /// <exception cref="DsWebChatProtocolException">The completion stream did not match the expected format.</exception>
    /// <exception cref="DsWebChatPowException">The proof-of-work challenge could not be solved.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<DsCompletionResponse> RequestCompletionAsync(string prompt,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        const string operation = nameof(RequestCompletionAsync);

        var payload = new CompletionRequest
        {
            ChatSessionId = await CreateSessionAsync(cancellationToken).ConfigureAwait(false),
            Prompt = prompt
        };

        var challengeResponse = await CreatePowChallengeAsync(cancellationToken).ConfigureAwait(false);
        var powResponse = new PowHeader()
        {
            Algorithm = AlgorithmName,
            Challenge = challengeResponse.Challenge,
            Salt = challengeResponse.Salt,
            Answer = (long)await SolvePowChallengeAsync(challengeResponse, cancellationToken).ConfigureAwait(false),
            Signature = challengeResponse.Signature,
            TargetPath = CompletionPath
        };
        var powResponseBase64 =
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(powResponse, _sharedJsonSerializerOptions)));
        var request = CreateDsRequest();
        request.RequestUri = new Uri(CompletionPath, UriKind.Relative);
        request.Headers.Add(PowHeaderName, powResponseBase64);
        request.Content = JsonContent.Create(payload, options: _sharedJsonSerializerOptions);

        // Read the SSE body as it arrives instead of buffering the whole answer, so the request returns
        // as soon as headers arrive and is not limited by the (former) fixed HttpClient timeout.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_extraHeaderOptions.CompletionTimeout);
        var requestUri = GetAbsoluteRequestUri(request);

        try
        {
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            await EnsureSuccessStatusCodeAsync(response, operation, requestUri, timeoutCts.Token)
                .ConfigureAwait(false);

            await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token)
                .ConfigureAwait(false);
            return await ParseSseAsync(stream, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DsWebChatTimeoutException(operation, _extraHeaderOptions.CompletionTimeout,
                $"{operation} failed: the completion request timed out after " +
                $"{FormatTimeout(_extraHeaderOptions.CompletionTimeout)} " +
                "(DsWebClientOptions.CompletionTimeout). Increase CompletionTimeout if long thinking/web-search " +
                "answers are expected, or pass a CancellationToken for caller-controlled cancellation.",
                ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new DsWebChatTransportException(operation, requestUri,
                $"{operation} failed: the request to DeepSeek failed while streaming the completion. " +
                "See the inner exception for details.",
                ex);
        }
    }

    /// <summary>
    /// Parses a DeepSeek completion SSE stream into its thinking and response text.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="DsWebChatProtocolException">The stream does not contain valid DeepSeek SSE events.</exception>
    /// <exception cref="DsWebChatApiException">The stream contains an SSE error event.</exception>
    /// <exception cref="DsWebChatTransportException">The stream could not be read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<DsCompletionResponse> ParseSseAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        try
        {
            return await ParseSseCoreAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (DsWebChatException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new DsWebChatTransportException(nameof(ParseSseAsync), null,
                $"{nameof(ParseSseAsync)} failed: the completion stream could not be read. " +
                "See the inner exception for details.",
                ex);
        }
    }

    private static async Task<DsCompletionResponse> ParseSseCoreAsync(Stream stream,
        CancellationToken cancellationToken)
    {
        const string operation = nameof(ParseSseAsync);
        using var reader = new StreamReader(stream, leaveOpen: true);
        var thinkingBuilder = new StringBuilder();
        var responseBuilder = new StringBuilder();
        StringBuilder? currentBuilder = null;
        var lineNumber = 0;
        var sawDataEvent = false;

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
                break;

            lineNumber++;

            if (!line.StartsWith("data: {", StringComparison.Ordinal))
                continue;

            sawDataEvent = true;
            try
            {
                var json = line["data: ".Length..];
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                ThrowIfSseApiError(root, operation, lineNumber, line);

                if (line.Length > 8 && line[8] == 'v')
                {
                    if (line.Length > 11 && line[11] == '{')
                    {
                        var fragments = root.GetProperty("v").GetProperty("response").GetProperty("fragments");
                        ParseFragment(GetFirstFragment(fragments, operation, lineNumber, line), line);
                    }
                    else if (line.Length > 11 && line[11] == '"')
                    {
                        var content = root.GetProperty("v").GetString() ?? string.Empty;

                        if (currentBuilder is null)
                        {
                            throw CreateSseProtocolException(operation, lineNumber, line,
                                "a content delta was received before any THINK or RESPONSE fragment was declared.");
                        }

                        currentBuilder.Append(content);
                    }
                }
                else if (line.Length > 8 && line[8] == 'p')
                {
                    if (currentBuilder is null)
                    {
                        throw CreateSseProtocolException(operation, lineNumber, line,
                            "a content delta was received before any THINK or RESPONSE fragment was declared.");
                    }

                    var type = root.GetProperty("p").GetString();
                    switch (type)
                    {
                        case "response/fragments/-1/content":
                            currentBuilder.Append(root.GetProperty("v").GetString());
                            break;
                        case "response/fragments":
                            ParseFragment(GetFirstFragment(root.GetProperty("v"), operation, lineNumber, line), line);
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (DsWebChatException)
            {
                throw;
            }
            catch (JsonException ex)
            {
                throw CreateSseProtocolException(operation, lineNumber, line,
                    "the line contains invalid JSON.", ex);
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException
                                           or ArgumentException or IndexOutOfRangeException)
            {
                throw CreateSseProtocolException(operation, lineNumber, line,
                    "the SSE payload has an unexpected shape.", ex);
            }
        }

        if (!sawDataEvent)
        {
            throw new DsWebChatProtocolException(operation,
                $"{operation} failed: the completion stream did not contain any SSE 'data:' events. " +
                "DeepSeek may have returned an error or changed its streaming format.");
        }

        return new DsCompletionResponse
        {
            Thinking = thinkingBuilder.ToString(),
            Response = responseBuilder.ToString()
        };

        void ParseFragment(JsonElement fragment, string line)
        {
            var firstFragmentType = fragment.GetProperty("type").GetString();
            switch (firstFragmentType)
            {
                case "THINK":
                    currentBuilder = thinkingBuilder;
                    break;
                case "RESPONSE":
                    currentBuilder = responseBuilder;
                    break;
                case "TIP":
                    return;
                default:
                    return; // Ignore unknown fragment types, since the server may add new types in the future
            }

            if (currentBuilder is null)
            {
                throw CreateSseProtocolException(operation, lineNumber, line,
                    "a response fragment was received before its target buffer was determined.");
            }

            currentBuilder.Append(fragment.GetProperty("content").GetString());
        }
    }

    private static JsonElement GetFirstFragment(JsonElement fragments, string operation, int lineNumber,
        string line)
    {
        if (fragments.ValueKind != JsonValueKind.Array || fragments.GetArrayLength() == 0)
        {
            throw CreateSseProtocolException(operation, lineNumber, line,
                "the SSE payload contains an empty response fragments array.");
        }

        return fragments[0];
    }

    private static DsWebChatProtocolException CreateSseProtocolException(string operation, int lineNumber,
        string line, string details, Exception? innerException = null)
    {
        var snippet = DsErrorHelper.Truncate(line);
        return new DsWebChatProtocolException(operation,
            $"{operation} failed at SSE line {lineNumber}: {details} " +
            $"Line snippet: {DsErrorHelper.DescribeSnippet(snippet)}",
            snippet, lineNumber, innerException);
    }

    private static void ThrowIfSseApiError(JsonElement root, string operation, int lineNumber, string line)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;

        // Error envelopes do not contain the usual "v"/"p" streaming properties.
        if (root.TryGetProperty("v", out _) || root.TryGetProperty("p", out _))
            return;

        var apiCode = DsErrorHelper.TryGetInt32(root, "code");
        if (apiCode is null || apiCode.Value == 0)
            return;

        var message = DsErrorHelper.TryGetString(root, "msg");
        var details = string.IsNullOrWhiteSpace(message) ? string.Empty : $": {message.Trim()}";
        var snippet = DsErrorHelper.Truncate(line);

        throw new DsWebChatApiException(operation,
            $"{operation} failed at SSE line {lineNumber}: DeepSeek returned error code " +
            $"{apiCode.Value}{details}.",
            HttpStatusCode.OK, apiCode, responseBodySnippet: snippet);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _engine.Dispose();
        _module.Dispose();
        _store.Dispose();
        _linker.Dispose();
        _httpClient.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await CastAndDispose(_engine);
        await CastAndDispose(_module);
        await CastAndDispose(_store);
        await CastAndDispose(_linker);
        await CastAndDispose(_httpClient);

        return;

        static async ValueTask CastAndDispose(IDisposable resource)
        {
            if (resource is IAsyncDisposable resourceAsyncDisposable)
                await resourceAsyncDisposable.DisposeAsync();
            else
                resource.Dispose();
        }
    }
}

public record CreatePowChallengePayload(string TargetPath);

public record ChallengeResponse
{
    public required string Algorithm { get; init; }
    public required string Challenge { get; init; }
    public required string Salt { get; init; }
    public required string Signature { get; init; }
    public required int Difficulty { get; init; }
    public required long ExpireAt { get; init; }
    public required int ExpireAfter { get; init; }
    public required string TargetPath { get; init; }
}

public record CompletionRequest
{
    public string? Action { get; init; } = null;
    public required string ChatSessionId { get; init; }
    public string? ModelType { get; init; } = null;
    public int? ParentMessageId { get; init; } = null;
    public bool Preempt { get; init; } = false;
    public required string Prompt { get; init; }
    public List<string> RefFileIds { get; init; } = new List<string>();
    public bool SearchEnabled { get; init; } = true;
    public bool ThinkingEnabled { get; init; } = true;
}

public record PowHeader
{
    public required string Algorithm { get; init; }
    public required string Challenge { get; init; }
    public required string Salt { get; init; }
    public required long Answer { get; init; }
    public required string Signature { get; init; }
    public required string TargetPath { get; init; }
}

public record DsWebClientOptions
{
    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36";

    public string XClientBundleId { get; init; } = "com.deepseek.chat";
    public string XClientLocale { get; init; } = "zh_CN";
    public string XClientPlatform { get; init; } = "web";
    public long XClientTimeZoneOffset { get; init; } = 28800;
    public string XClientVersion { get; init; } = "2.7.0";
    public string? XDeviceId { get; init; } = null;

    /// <summary>
    /// Timeout for the short, buffered calls (PoW challenge request, session creation).
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Timeout for the streamed completion request. Defaults to no timeout, since thinking + web search
    /// answers can run for several minutes; set a value if you need an upper bound.
    /// </summary>
    public TimeSpan CompletionTimeout { get; init; } = Timeout.InfiniteTimeSpan;
}

public record DsCompletionResponse
{
    public string Thinking { get; init; } = string.Empty;
    public string Response { get; init; } = string.Empty;
}

// internal record DsSseFragment
// {
//     // {
//     //     "id": 2,
//     //     "type": "THINK",
//     //     "content": "The",
//     //     "elapsed_secs": null,
//     //     "references": [],
//     //     "stage_id": 1
//     // }
//     public int Id { get; init; }
//     public string Type { get; init; } = string.Empty;
//     public string Content { get; init; } = string.Empty;
//     public double? ElapsedSecs { get; init; } = null;
//     public List<string> References { get; init; } = new List<string>();
//     public int StageId { get; init; }
// }
