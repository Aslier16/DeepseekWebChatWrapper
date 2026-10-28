# DeepSeek WebChat Wrapper

English | [简体中文](README.zh-CN.md)

A .NET implementation for calling DeepSeek's web chat (`chat.deepseek.com`)

> **Status: developing, `0.0.1`.** The API surface is not stable and the endpoints/headers it targets
> are the ones used by the web app, so they can change without notice.

## What it does

The purpose of this package is to provide a .NET implementation to call DeepSeek's web chat. It talks
to the same internal endpoints as the web app instead of the official API. Those endpoints require a
proof-of-work (PoW) challenge to be solved for each completion request. The library does that for you
and parses the streamed response into two strings: the model's reasoning (`Thinking`) and its answer
(`Response`).

## Use case

You want to send prompts to DeepSeek without going through the official API, for whatever reason —
typically lightweight, low-frequency usage such as a personal chatbot, a script or a small side
project.

It is not a general-purpose SDK: no streaming callbacks (yet), no conversation history helper, no
function calling.

## Mechanism

1. `CreatePowChallengeAsync()` requests a challenge from `/api/v0/chat/create_pow_challenge`.
2. The challenge is solved locally by running DeepSeek's own `DeepSeekHashV1` WebAssembly module
   (embedded in the assembly as `Assets/ds_webchat_wasm.wasm`) through Wasmtime.
3. The solution is base64-encoded into the `x-ds-pow-response` header.
4. `CreateSessionAsync()` creates a chat session, whose id is sent in the completion payload.
5. `RequestCompletionAsync(prompt)` does all of the above, POSTs to `/api/v0/chat/completion`
   and parses the SSE stream into a `DsCompletionResponse`.

Authentication is passed as the `Authorization` header (the web app's user token) and the
`ds_session_id` cookie. Both come from your browser session; the library does not log in for you.

## Dependencies

- .NET 10.0
- A DeepSeek account and a browser session
- [Wasmtime](https://www.nuget.org/packages/Wasmtime) `48.0.2` (the only runtime dependency, restored
  automatically)
- Tests use xUnit (`Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`)

## Installation

```bash
dotnet add package Aslier.DeepseekWebChatWrapper --version 0.0.1
```

## Usage

All the data needed to construct the client is in the headers of the **completion request**: open
`chat.deepseek.com`, log in, send a message to the AI, then in DevTools(F12) → Network open the
`/api/v0/chat/completion` request and copy

- `Authorization` → the web app's user token → `authToken`("Bearer XXX" including "Bearer" or not is ok)
- `Cookie` → the `ds_session_id=<value>` part → `dsSessionId`

Token expiry has not been fully tested yet — use at your own risk.

```csharp
using DeepseekWebChatWrapper.Core;

await using var client = new DsWebChatClient(authToken, dsSessionId);

var result = await client.RequestCompletionAsync("Explain proof-of-work in two sentences.");

Console.WriteLine(result.Thinking); // reasoning trace, may be empty
Console.WriteLine(result.Response); // final answer
```

Both `Thinking` and `Response` are empty-safe strings. The built-in request has deep thinking and
web search enabled; there is currently no parameter to turn them off (`CompletionRequest` is only a
DTO — you would have to send the request yourself).

Steps are also callable individually:

```csharp
string sessionId = await client.CreateSessionAsync();
var challenge = await client.CreatePowChallengeAsync();
double answer = await client.SolvePowChallengeAsync(challenge);
```

`DsWebChatClient.ParseSseAsync(stream, ct)` is public if you already have a completion stream.

The header set sent to the server (`User-Agent`, `x-client-*`) and the per-request timeouts
(`RequestTimeout`, `CompletionTimeout`) are configurable via `DsWebClientOptions`. The streamed
completion has no timeout by default; short calls default to 60 seconds.

Note that there is no restriction of concurrency yet, control the request frequency on your own

## Project layout

```
DeepseekWebChatWrapper.Core/   the library (client, PoW solver, SSE parser, DTOs)
DeepseekWebChatWrapper.Test/   xUnit tests that call the real server
```

## Tests

`TestVars.json` is required and is not committed. Copy the example and fill in your credentials:

```bash
cp DeepseekWebChatWrapper.Test/TestVars_example.json DeepseekWebChatWrapper.Test/TestVars.json
dotnet test
```

## Limitations

- Not an official API; server changes can break it at any time.
- A new chat session is created per `RequestCompletionAsync` call. Continuing an existing
  conversation is not implemented yet.
- The WASM instance is not thread-safe, so solving is serialized per client.

## License

GPL-3.0, see [LICENSE](LICENSE). Not affiliated with DeepSeek.
