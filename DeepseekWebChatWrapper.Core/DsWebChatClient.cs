using System.Data;
using System.Net.Http.Json;
using System.Reflection;
using System.Resources;
using System.Text;
using System.Text.Json;
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
    private const string CreatePowChallengeUri = "/api/v0/chat/create_pow_challenge";
    private const string CreateSessionUri = "/api/v0/chat_session/create";
    private const string CompletionPath = "/api/v0/chat/completion";
    private const string PowHeaderName = "x-ds-pow-response";
    private const string AlgorithmName = "DeepSeekHashV1";

    private readonly JsonSerializerOptions _sharedJsonSerializerOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly CreatePowChallengePayload _createPowChallengePayload = new(TargetPath: "/api/v0/chat/completion");
    // private HttpRequestMessage NoPowMessage;
    private readonly HttpRequestMessage _powMessage;

    public DsWebChatClient(string authToken, string dsSessionId)
    {
        _httpClient.BaseAddress = new Uri("https://chat.deepseek.com");
        _httpClient.DefaultRequestHeaders.Add("Authorization",
            authToken.StartsWith("Bearer ") ? authToken : $"Bearer {authToken}");
        _httpClient.DefaultRequestHeaders.Add("Cookie", $"ds_session_id={dsSessionId}");

        // NoPowMessage = new HttpRequestMessage();
        // NoPowMessage.Method = HttpMethod.Post;
        // NoPowMessage.Headers.Add("Authorization",
        //     authToken.StartsWith("Bearer ") ? authToken : $"Bearer {authToken}");
        // NoPowMessage.Headers.Add("Cookie", $"ds_session_id={dsSessionId}");

        _powMessage = new HttpRequestMessage();
        _powMessage.Method = HttpMethod.Post;
        _powMessage.Headers.Add("Authorization",
            authToken.StartsWith("Bearer ") ? authToken : $"Bearer {authToken}");
        _powMessage.Headers.Add("Cookie", $"ds_session_id={dsSessionId}");
        _powMessage.Headers.Add("x-ds-pow-response", ""); // Placeholder for the PoW response header

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
        var response =
            await _httpClient.PostAsync(CreatePowChallengeUri,
                JsonContent.Create(_createPowChallengePayload, options: _sharedJsonSerializerOptions),
                cancellationToken);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
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
            return await Task.Run(() => SolvePowChallenge(challengeResponse));
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

        if (status == 0)
            throw new Exception("No solution found for the challenge");
        return answer;
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
        var response = await _httpClient.PostAsync(CreateSessionUri, null, cancellationToken);
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
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
    public async Task<string> RequestCompletionAsync(string prompt, CancellationToken cancellationToken = default)
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
        _powMessage.RequestUri = new Uri(CompletionPath, UriKind.Relative);
        _powMessage.Headers.Remove(PowHeaderName);
        _powMessage.Headers.Add(PowHeaderName, powResponseBase64);
        _powMessage.Content = JsonContent.Create(payload, options: _sharedJsonSerializerOptions);
        var response = await _httpClient.SendAsync(_powMessage, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
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