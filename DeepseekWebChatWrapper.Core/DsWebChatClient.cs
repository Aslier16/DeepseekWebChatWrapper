using System.Data;
using System.Net.Http.Json;
using System.Reflection;
using System.Resources;
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


    private readonly HttpClient _httpClient = new HttpClient();
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

    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatClient"/> class.
    /// </summary>
    /// <param name="authToken"></param>
    /// <param name="dsSessionId"></param>
    /// <param name="extraHeaderOptions"></param>
    /// <param name="rateLimiter">RateLimiter to limit Completion Requests' frequency
    /// will create a default TokenBucketRateLimiter with TokenLimit 20, TokenPerPeriod 10,AutoReplenishment true(must), ReplenishmentPeriod 1s, QueueLimit 0</param>
    /// <exception cref="DataException"></exception>
    /// <exception cref="Exception"></exception>
    public DsWebChatClient(string authToken, string dsSessionId, DsWebClientOptions? extraHeaderOptions = null,
        RateLimiter? rateLimiter = null)
    {
        _extraHeaderOptions = extraHeaderOptions ?? new DsWebClientOptions();
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
        _httpClient.BaseAddress = new Uri(BaseUri);
        // Timeouts are enforced per request instead: a single streamed answer can take minutes, which the
        // default 100 second HttpClient.Timeout would cut off.
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;

        var assembly = Assembly.Load("DeepseekWebChatWrapper.Core");
        var wasmStream =
            assembly.GetManifestResourceStream("DeepseekWebChatWrapper.Core.Assets.ds_webchat_wasm.wasm") ??
            throw new DataException(
                "Failed to load embedded resource: DeepseekWebChatWrapper.Core.Assets.ds_webchat_wasm.wasm");

        _engine = new Engine();
        _module = Module.FromStream(_engine, "pow_solver", wasmStream);
        _store = new Store(_engine);
        _linker = new Linker(_engine);
        _instance = _linker.Instantiate(_store, _module);
        _memory = _instance.GetMemory("memory") ?? throw new Exception("Failed to get memory from Wasmtime instance");
        try
        {
            _addToStack = _instance.GetFunction<int, int>("__wbindgen_add_to_stack_pointer") ??
                          throw new Exception(
                              "Failed to get __wbindgen_add_to_stack_pointer function from Wasmtime instance");
            _alloc = _instance.GetFunction<int, int, int>("__wbindgen_export_0") ??
                     throw new Exception("Failed to get __wbindgen_export_0 function from Wasmtime instance");
            _free = _instance.GetAction<int, int, int>("__wbindgen_export_2") ??
                    throw new Exception("Failed to get __wbindgen_export_2 function from Wasmtime instance");
            _solve = _instance.GetFunction("wasm_solve") ??
                     throw new Exception("Failed to get wasm_solve function from Wasmtime instance");
        }
        catch (Exception e)
        {
            throw new Exception($"Failed to get wasm function:{e.Message}", e);
        }
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
    private async Task<HttpResponseMessage> SendBufferedAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_extraHeaderOptions.RequestTimeout);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token);
    }

    public async Task<ChallengeResponse> CreatePowChallengeAsync(CancellationToken cancellationToken = default)
    {
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

        using var response = await SendBufferedAsync(request, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        try
        {
            return doc.RootElement.GetProperty("data").GetProperty("biz_data").GetProperty("challenge")
                       .Deserialize<ChallengeResponse>(_sharedJsonSerializerOptions) ??
                   throw new DataException($"Failed to deserialize challenge response{doc.RootElement.ToString()}");
        }
        catch (Exception e)
        {
            throw new Exception($"Failed to parse challenge from response: {doc.RootElement}", e);
        }
    }

    private readonly SemaphoreSlim _powChallengeSemaphore = new SemaphoreSlim(1, 1);

    /// <summary>
    /// Async wrapper for SolvePowChallenge to prevent concurrent access to the Wasm module, which is not thread-safe.
    /// Wrap challenge solving in a semaphore to prevent concurrent access to the Wasm module, which is not thread-safe.
    /// </summary>
    /// <param name="challengeResponse"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<double> SolvePowChallengeAsync(ChallengeResponse challengeResponse,
        CancellationToken cancellationToken = default)
    {
        await _powChallengeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => SolvePowChallenge(challengeResponse), cancellationToken);
        }
        finally
        {
            _powChallengeSemaphore.Release();
        }
    }

    private double SolvePowChallenge(ChallengeResponse challengeResponse)
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
        _free(challengePtr, challengeBytes.Length, 1);
        _free(prefixPtr, prefixBytes.Length, 1);

        if (status == 0)
            throw new Exception("No solution found for the challenge");
        return answer;
    }

    public async Task<string> CreateSessionAsync(CancellationToken cancellationToken = default)
    {
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
        using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);
        if (!lease.IsAcquired)
            throw new Exception("Rate limit exceeded for CreateSessionAsync");
        var request = CreateDsRequest();
        request.RequestUri = new Uri(CreateSessionUri, UriKind.Relative);
        request.Content = JsonContent.Create(new { }, options: _sharedJsonSerializerOptions);

        using var response = await SendBufferedAsync(request, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        try
        {
            return doc.RootElement.GetProperty("data").GetProperty("biz_data").GetProperty("chat_session")
                       .GetProperty("id").GetString() ??
                   throw new DataException(
                       $"Failed to deserialize chat session id from response{doc.RootElement.ToString()}");
        }
        catch (Exception e)
        {
            throw new Exception($"Failed to parse chat session id from response: {doc.RootElement}", e);
        }
    }

    /// <summary>
    /// Simple wrapper for the completion request. It creates a new chat session and sends the prompt to the server.
    /// </summary>
    /// <param name="prompt"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<DsCompletionResponse> RequestCompletionAsync(string prompt,
        CancellationToken cancellationToken = default)
    {
        var payload = new CompletionRequest
        {
            ChatSessionId = await CreateSessionAsync(cancellationToken),
            Prompt = prompt
        };

        var challengeResponse = await CreatePowChallengeAsync(cancellationToken);
        var powResponse = new PowHeader()
        {
            Algorithm = AlgorithmName,
            Challenge = challengeResponse.Challenge,
            Salt = challengeResponse.Salt,
            Answer = (long)await SolvePowChallengeAsync(challengeResponse, cancellationToken),
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

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token);

        // Consume Sse response
        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        return await ParseSseAsync(stream, timeoutCts.Token);
    }

    public static async Task<DsCompletionResponse> ParseSseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new StreamReader(stream);
        var thinkingBuilder = new StringBuilder();
        var responseBuilder = new StringBuilder();
        StringBuilder? currentBuilder = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (line.StartsWith("data: {"))
            {
                var json = line.Substring("data: ".Length);
                using var doc = JsonDocument.Parse(json);
                // Response start
                if (line[8] == 'v')
                {
                    if (line[11] == '{')
                    {
                        var fragment = doc.RootElement.GetProperty("v").GetProperty("response")
                            .GetProperty("fragments")
                            .EnumerateArray().ElementAt(0);
                        ParseFragment(fragment);
                    }
                    else if (line[11] == '"')
                    {
                        try
                        {
                            var content = doc.RootElement.GetProperty("v").GetString() ?? "";

                            if (currentBuilder is null)
                                throw new DataException(
                                    "Received SSE fragment before the first fragment type was determined");
                            currentBuilder.Append(content);
                        }
                        catch (Exception e)
                        {
                            throw new Exception($"Failed to parse SSE fragment from response: {doc.RootElement}", e);
                        }
                    }
                }
                else if (line[8] == 'p')
                {
                    if (currentBuilder is null)
                        throw new DataException("Received SSE fragment before the first fragment type was determined");
                    try
                    {
                        var type = doc.RootElement.GetProperty("p").GetString();
                        switch (type)
                        {
                            case "response/fragments/-1/content":
                                currentBuilder.Append(doc.RootElement.GetProperty("v").GetString());
                                break;
                            case "response/fragments":
                                ParseFragment(doc.RootElement.GetProperty("v").EnumerateArray().ElementAt(0));
                                break;
                            default:
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                        throw new Exception($"Failed to parse SSE fragment from response: {doc.RootElement}", e);
                    }
                }
            }
        }

        return new DsCompletionResponse
        {
            Thinking = thinkingBuilder.ToString(),
            Response = responseBuilder.ToString()
        };

        void ParseFragment(JsonElement fragment)
        {
            try
            {
                var firstFragmentType = fragment.GetProperty("type").GetString();
                switch (firstFragmentType)
                {
                    case "THINK":
                        currentBuilder = thinkingBuilder; break;
                    case "RESPONSE": currentBuilder = responseBuilder; break;
                    case "TIP": return;
                    default:
                        return; // Ignore unknown fragment types, since the server may add new types in the future
                }

                if (currentBuilder is null)
                    throw new DataException(
                        "Received SSE fragment before the first fragment type was determined");
                currentBuilder.Append(fragment.GetProperty("content").GetString());
            }
            catch (Exception e)
            {
                throw new Exception($"Failed to parse SSE fragment from response: {fragment}", e);
            }
        }
    }

    public void Dispose()
    {
        _engine.Dispose();
        _module.Dispose();
        _store.Dispose();
        _linker.Dispose();
        _httpClient.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
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