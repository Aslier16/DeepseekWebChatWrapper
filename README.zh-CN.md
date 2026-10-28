# DeepSeek WebChat Wrapper

[English](README.md) | 简体中文

一个用于调用 DeepSeek 网页版聊天（`chat.deepseek.com`）的 .NET 实现。

> **状态：预发布 `0.0.1`。** API 尚不稳定，且本项目对接的是网页端使用的内部接口与请求头，
> 服务端随时可能变更。

## 功能说明

本包的目的是提供一个 .NET 实现来调用 DeepSeek 的网页版聊天。它不走官方 API，而是和网页端
使用同一套内部接口。这些接口要求每次补全请求都先解出一道工作量证明（PoW）挑战，库会替你完成
这一步，并把流式响应解析成两段字符串：模型的思考过程（`Thinking`）和最终回答（`Response`）。

## 适用场景

你出于某些原因想直接给 DeepSeek 发消息而不走官方 API —— 通常用于轻量、低频的场景，
例如个人聊天机器人、脚本或小型项目。

它不是通用 SDK：没有流式回调（暂时）、没有会话历史管理、不支持 function calling。

## 实现机制

1. `CreatePowChallengeAsync()` 向 `/api/v0/chat/create_pow_challenge` 申请挑战。
2. 挑战由 DeepSeek 自己的 `DeepSeekHashV1` WebAssembly 模块在本地求解（该模块以
   `Assets/ds_webchat_wasm.wasm` 的形式内嵌在程序集中），通过 Wasmtime 执行。
3. 求解结果 Base64 编码后写入 `x-ds-pow-response` 请求头。
4. `CreateSessionAsync()` 创建聊天会话，会话 id 会放进补全请求的负载中。
5. `RequestCompletionAsync(prompt)` 完成以上全部步骤，POST 到 `/api/v0/chat/completion`，
   并把 SSE 流解析为 `DsCompletionResponse`。

身份认证使用 `Authorization` 请求头（网页端的用户 token）和 `ds_session_id` Cookie。两者都来自
你的浏览器会话，库不会替你登录。

## 依赖

- .NET 10.0
- 一个 DeepSeek 账号和有效的浏览器会话
- [Wasmtime](https://www.nuget.org/packages/Wasmtime) `48.0.2`（唯一的运行时依赖，会自动还原）
- 测试使用 xUnit（`Microsoft.NET.Test.Sdk`、`xunit`、`xunit.runner.visualstudio`、`coverlet.collector`）

## 安装

```bash
dotnet add package Aslier.DeepseekWebChatWrapper --version 0.0.1
```

## 使用方法

构造客户端所需的全部数据都在**补全请求**的请求头里：打开并登录 `chat.deepseek.com`，给 AI 发一条
消息，然后在开发者工具（F12）→ Network 中找到 `/api/v0/chat/completion` 请求，复制

- `Authorization` → 网页端的用户 token → `authToken`（带不带 `Bearer ` 前缀都可以）
- `Cookie` → 其中的 `ds_session_id=<value>` → `dsSessionId`

token 的有效期尚未充分测试，请自行承担风险。

```csharp
using DeepseekWebChatWrapper.Core;

await using var client = new DsWebChatClient(authToken, dsSessionId);

var result = await client.RequestCompletionAsync("用两句话解释什么是工作量证明。");

Console.WriteLine(result.Thinking); // 思考过程，可能为空
Console.WriteLine(result.Response); // 最终回答
```

`Thinking` 和 `Response` 都是空安全的字符串。内置请求会开启深度思考和联网搜索，目前没有参数可以
关闭它们（`CompletionRequest` 只是 DTO，需要你自己发送请求才能改）。

各个步骤也可以单独调用：

```csharp
string sessionId = await client.CreateSessionAsync();
var challenge = await client.CreatePowChallengeAsync();
double answer = await client.SolvePowChallengeAsync(challenge);
```

如果你已经拿到了补全响应流，可以直接使用公开的 `DsWebChatClient.ParseSseAsync(stream, ct)`。

发送给服务端的请求头（`User-Agent`、`x-client-*`）和单次请求超时（`RequestTimeout`、
`CompletionTimeout`）可以通过 `DsWebClientOptions` 配置。流式补全默认不设超时，短请求默认 60 秒。

目前还没有并发限制，请自行控制请求频率。

## 项目结构

```
DeepseekWebChatWrapper.Core/   库（客户端、PoW 求解、SSE 解析、DTO）
DeepseekWebChatWrapper.Test/   调用真实服务端的 xUnit 测试
```

## 测试

测试需要 `TestVars.json`，该文件不纳入版本库。复制示例文件并填入你的凭证：

```bash
cp DeepseekWebChatWrapper.Test/TestVars_example.json DeepseekWebChatWrapper.Test/TestVars.json
dotnet test
```

## 已知限制

- 非官方 API，服务端改动随时可能导致不可用。
- 每次 `RequestCompletionAsync` 都会新建一个聊天会话，续接已有对话尚未实现。
- WASM 实例不是线程安全的，因此每个客户端内的求解是串行的。

## 许可证

GPL-3.0，见 [LICENSE](LICENSE)。本项目与 DeepSeek 官方无关。
