using System.Net;
using System.Text;
using System.Text.Json;

namespace DeepseekWebChatWrapper.Core;

internal static class DsErrorHelper
{
    internal const int SnippetLength = 1024;
    private const int SnippetByteLength = 4096;

    internal static string Truncate(string? value, int maxLength = SnippetLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Length <= maxLength ? value : value[..maxLength] + "…";
    }

    internal static string DescribeSnippet(string? value)
    {
        var snippet = Truncate(value);
        return snippet.Length == 0 ? "<empty>" : snippet;
    }

    internal static async Task<string?> ReadSnippetAsync(HttpContent? content, CancellationToken cancellationToken)
    {
        if (content is null)
            return null;

        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[SnippetByteLength];
            var totalRead = 0;

            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                totalRead += read;
            }

            if (totalRead == 0)
                return null;

            return Truncate(Encoding.UTF8.GetString(buffer, 0, totalRead));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Reading an error body is best-effort; never hide the original HTTP failure.
            return null;
        }
    }

    internal static int? TryGetInt32(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt32(out var result) ? result : null;
    }

    internal static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    internal static JsonElement GetRequiredProperty(JsonElement root, string operation, params string[] path)
    {
        var current = root;
        var fullPath = string.Join(".", path);

        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                var snippet = Truncate(root.GetRawText());
                throw new DsWebChatProtocolException(operation,
                    $"{operation} failed: the response is missing '{fullPath}'. Response snippet: {DescribeSnippet(snippet)}",
                    snippet);
            }

            current = next;
        }

        return current;
    }

    internal static void ThrowIfApiError(JsonElement root, string operation)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return;

        var apiCode = TryGetInt32(root, "code");
        var apiMessage = TryGetString(root, "msg");

        int? bizCode = null;
        string? bizMessage = null;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            bizCode = TryGetInt32(data, "biz_code");
            bizMessage = TryGetString(data, "biz_msg");
        }

        var codeParts = new List<string>();
        if (apiCode is not null and not 0)
            codeParts.Add($"code {apiCode.Value}");
        if (bizCode is not null and not 0)
            codeParts.Add($"biz_code {bizCode.Value}");

        if (codeParts.Count == 0)
            return;

        var serverMessage = string.IsNullOrWhiteSpace(bizMessage) ? apiMessage : bizMessage;
        var details = string.IsNullOrWhiteSpace(serverMessage) ? string.Empty : $": {serverMessage.Trim()}";

        throw new DsWebChatApiException(operation,
            $"{operation} failed: DeepSeek rejected the request ({string.Join(", ", codeParts)}){details}.",
            HttpStatusCode.OK,
            apiCode is 0 ? null : apiCode,
            bizCode is 0 ? null : bizCode,
            Truncate(root.GetRawText()));
    }

    internal static DsWebChatException CreateHttpStatusException(string operation,
        HttpResponseMessage response, Uri? requestUri, string? responseBodySnippet)
    {
        var status = response.StatusCode;
        var path = requestUri?.AbsolutePath ?? response.RequestMessage?.RequestUri?.AbsolutePath ?? "/";
        var snippet = Truncate(responseBodySnippet);

        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new DsWebChatAuthenticationException(operation,
                $"{operation} failed: DeepSeek returned HTTP {(int)status} ({status}) for {path}. " +
                "The auth token or ds_session_id cookie is invalid or expired. Copy fresh values from a " +
                "chat.deepseek.com completion request in the browser DevTools and create a new DsWebChatClient.",
                status, snippet);
        }

        if (status == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = GetRetryAfter(response);
            var wait = retryAfter is null
                ? "Wait a bit before retrying."
                : $"Wait {retryAfter.Value.ToString("c")} before retrying.";

            return new DsWebChatRateLimitException(operation,
                $"{operation} failed: DeepSeek returned HTTP 429 (TooManyRequests) for {path}. " +
                $"The server is rate limiting this account. {wait}",
                isRemote: true, retryAfter, status, snippet);
        }

        string guidance;
        if (status == HttpStatusCode.NotFound)
        {
            guidance =
                "The web endpoint may have changed; check whether a newer version of Aslier.DeepseekWebChatWrapper is available.";
        }
        else if ((int)status >= 500)
        {
            guidance = "DeepSeek returned a server-side error; retry later.";
        }
        else if (status == HttpStatusCode.BadRequest)
        {
            guidance = "The request was rejected; check the prompt and client options.";
        }
        else
        {
            guidance = "The request was rejected.";
        }

        var serverMessage = snippet.Length == 0 ? string.Empty : $" Server response: {snippet}";
        return new DsWebChatApiException(operation,
            $"{operation} failed: DeepSeek returned HTTP {(int)status} ({status}) for {path}. {guidance}{serverMessage}",
            status, responseBodySnippet: snippet);
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta;

        if (retryAfter.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }
}
