using System.Net;

namespace DeepseekWebChatWrapper.Core;

/// <summary>
/// Base exception for errors raised by <see cref="DsWebChatClient"/>.
/// </summary>
public class DsWebChatException : Exception
{
    /// <summary>
    /// Name of the operation that failed, for example <c>RequestCompletionAsync</c>.
    /// </summary>
    public string Operation { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatException"/> class.
    /// </summary>
    /// <param name="operation">Name of the operation that failed.</param>
    /// <param name="message">A description of the error.</param>
    /// <param name="innerException">The exception that caused this error, if any.</param>
    public DsWebChatException(string operation, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
    }
}

/// <summary>
/// The embedded proof-of-work WASM runtime could not be loaded or initialized.
/// This usually indicates a corrupted package installation or an incompatible Wasmtime version.
/// </summary>
public class DsWebChatInitializationException : DsWebChatException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatInitializationException"/> class.
    /// </summary>
    public DsWebChatInitializationException(string operation, string message, Exception? innerException = null)
        : base(operation, message, innerException)
    {
    }
}

/// <summary>
/// The HTTP request or the response stream could not be read, for example because of a network,
/// DNS, TLS, or connection failure.
/// </summary>
public class DsWebChatTransportException : DsWebChatException
{
    /// <summary>
    /// The absolute request URI, when available.
    /// </summary>
    public Uri? RequestUri { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatTransportException"/> class.
    /// </summary>
    public DsWebChatTransportException(string operation, Uri? requestUri, string message,
        Exception? innerException = null)
        : base(operation, message, innerException)
    {
        RequestUri = requestUri;
    }
}

/// <summary>
/// A request made by the client exceeded <see cref="DsWebClientOptions.RequestTimeout"/> or
/// <see cref="DsWebClientOptions.CompletionTimeout"/>.
/// </summary>
/// <remarks>
/// Cancellation requested through the <see cref="CancellationToken"/> passed to a client method is
/// reported as <see cref="OperationCanceledException"/> instead.
/// </remarks>
public class DsWebChatTimeoutException : DsWebChatException
{
    /// <summary>
    /// The timeout that elapsed.
    /// </summary>
    public TimeSpan Timeout { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatTimeoutException"/> class.
    /// </summary>
    public DsWebChatTimeoutException(string operation, TimeSpan timeout, string message,
        Exception? innerException = null)
        : base(operation, message, innerException)
    {
        Timeout = timeout;
    }
}

/// <summary>
/// DeepSeek returned a non-success HTTP status code, or an HTTP 200 response containing a business
/// error code.
/// </summary>
public class DsWebChatApiException : DsWebChatException
{
    /// <summary>
    /// The HTTP status code returned by DeepSeek, when available. This is
    /// <see cref="HttpStatusCode.OK"/> for business errors returned with HTTP 200.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>
    /// The top-level DeepSeek API error code, when available.
    /// </summary>
    public int? ApiCode { get; }

    /// <summary>
    /// The DeepSeek business error code (<c>data.biz_code</c>), when available.
    /// </summary>
    public int? BizCode { get; }

    /// <summary>
    /// A truncated snippet of the response body. May be <see langword="null"/> when the body could
    /// not be read. Credentials are never included.
    /// </summary>
    public string? ResponseBodySnippet { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatApiException"/> class.
    /// </summary>
    public DsWebChatApiException(string operation, string message, HttpStatusCode? statusCode = null,
        int? apiCode = null, int? bizCode = null, string? responseBodySnippet = null,
        Exception? innerException = null)
        : base(operation, message, innerException)
    {
        StatusCode = statusCode;
        ApiCode = apiCode;
        BizCode = bizCode;
        ResponseBodySnippet = responseBodySnippet;
    }
}

/// <summary>
/// DeepSeek rejected the request because the supplied auth token or <c>ds_session_id</c> cookie is
/// missing, invalid, or expired.
/// </summary>
public class DsWebChatAuthenticationException : DsWebChatApiException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatAuthenticationException"/> class.
    /// </summary>
    public DsWebChatAuthenticationException(string operation, string message, HttpStatusCode? statusCode = null,
        string? responseBodySnippet = null, Exception? innerException = null)
        : base(operation, message, statusCode, responseBodySnippet: responseBodySnippet,
            innerException: innerException)
    {
    }
}

/// <summary>
/// A request was rejected by the client's local rate limiter or by DeepSeek with HTTP 429.
/// </summary>
public class DsWebChatRateLimitException : DsWebChatApiException
{
    /// <summary>
    /// <see langword="true"/> when DeepSeek rejected the request with HTTP 429;
    /// <see langword="false"/> when the client's local <see cref="System.Threading.RateLimiting.RateLimiter"/>
    /// rejected it before sending.
    /// </summary>
    public bool IsRemote { get; }

    /// <summary>
    /// The delay requested by the server's <c>Retry-After</c> header, when present.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatRateLimitException"/> class.
    /// </summary>
    public DsWebChatRateLimitException(string operation, string message, bool isRemote,
        TimeSpan? retryAfter = null, HttpStatusCode? statusCode = null, string? responseBodySnippet = null,
        Exception? innerException = null)
        : base(operation, message, statusCode, responseBodySnippet: responseBodySnippet,
            innerException: innerException)
    {
        IsRemote = isRemote;
        RetryAfter = retryAfter;
    }
}

/// <summary>
/// DeepSeek returned data that does not match the expected response or SSE format. This usually
/// means the web endpoint or streaming protocol changed.
/// </summary>
public class DsWebChatProtocolException : DsWebChatException
{
    /// <summary>
    /// The line number in the SSE stream at which parsing failed, when applicable.
    /// </summary>
    public int? SseLineNumber { get; }

    /// <summary>
    /// A truncated snippet of the response or SSE line that could not be parsed.
    /// </summary>
    public string? ResponseSnippet { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatProtocolException"/> class.
    /// </summary>
    public DsWebChatProtocolException(string operation, string message, string? responseSnippet = null,
        int? sseLineNumber = null, Exception? innerException = null)
        : base(operation, message, innerException)
    {
        ResponseSnippet = responseSnippet;
        SseLineNumber = sseLineNumber;
    }
}

/// <summary>
/// The proof-of-work challenge could not be solved by the embedded <c>DeepSeekHashV1</c> WASM solver.
/// </summary>
public class DsWebChatPowException : DsWebChatException
{
    /// <summary>
    /// The difficulty of the challenge that could not be solved.
    /// </summary>
    public int Difficulty { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="DsWebChatPowException"/> class.
    /// </summary>
    public DsWebChatPowException(string operation, string message, int difficulty,
        Exception? innerException = null)
        : base(operation, message, innerException)
    {
        Difficulty = difficulty;
    }
}
