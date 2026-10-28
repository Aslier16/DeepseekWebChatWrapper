using System.Text;
using DeepseekWebChatWrapper.Core;

namespace DeepseekWebChatWrapper.Test;

public class ExceptionTests
{
    [Fact]
    public void Constructor_Throws_For_Blank_AuthToken()
    {
        Assert.Throws<ArgumentException>(() => new DsWebChatClient(" ", "session"));
    }

    [Fact]
    public void Constructor_Throws_For_Blank_SessionId()
    {
        Assert.Throws<ArgumentException>(() => new DsWebChatClient("token", " "));
    }

    [Fact]
    public void Constructor_Throws_For_Invalid_RequestTimeout()
    {
        var options = new DsWebClientOptions { RequestTimeout = TimeSpan.Zero };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DsWebChatClient("token", "session", options));
        Assert.Contains(nameof(DsWebClientOptions.RequestTimeout), exception.Message);
    }

    [Fact]
    public void Constructor_Throws_For_Invalid_CompletionTimeout()
    {
        var options = new DsWebClientOptions { CompletionTimeout = TimeSpan.FromSeconds(-2) };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DsWebChatClient("token", "session", options));
        Assert.Contains(nameof(DsWebClientOptions.CompletionTimeout), exception.Message);
    }

    [Fact]
    public void Constructor_Throws_For_Blank_Header_Option()
    {
        var options = new DsWebClientOptions { UserAgent = " " };

        Assert.Throws<ArgumentException>(() => new DsWebChatClient("token", "session", options));
    }

    [Fact]
    public async Task ParseSseAsync_Throws_For_Null_Stream()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            DsWebChatClient.ParseSseAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task ParseSseAsync_Throws_ProtocolException_For_Invalid_Json()
    {
        await using var stream = CreateStream("data: {not-json}\n");

        var exception = await Assert.ThrowsAsync<DsWebChatProtocolException>(() =>
            DsWebChatClient.ParseSseAsync(stream, CancellationToken.None));

        Assert.Equal(nameof(DsWebChatClient.ParseSseAsync), exception.Operation);
        Assert.Equal(1, exception.SseLineNumber);
        Assert.NotNull(exception.ResponseSnippet);
    }

    [Fact]
    public async Task ParseSseAsync_Throws_ProtocolException_For_Content_Before_Fragment_Type()
    {
        await using var stream = CreateStream("data: {\"v\":\"hello\"}\n");

        var exception = await Assert.ThrowsAsync<DsWebChatProtocolException>(() =>
            DsWebChatClient.ParseSseAsync(stream, CancellationToken.None));

        Assert.Equal(1, exception.SseLineNumber);
    }

    [Fact]
    public async Task ParseSseAsync_Throws_ProtocolException_For_Empty_Fragments()
    {
        await using var stream = CreateStream("data: {\"v\":{\"response\":{\"fragments\":[]}}}\n");

        var exception = await Assert.ThrowsAsync<DsWebChatProtocolException>(() =>
            DsWebChatClient.ParseSseAsync(stream, CancellationToken.None));

        Assert.Equal(1, exception.SseLineNumber);
    }

    [Fact]
    public async Task ParseSseAsync_Throws_ProtocolException_When_No_Data_Events_Exist()
    {
        await using var stream = CreateStream("event: ready\n\n");

        var exception = await Assert.ThrowsAsync<DsWebChatProtocolException>(() =>
            DsWebChatClient.ParseSseAsync(stream, CancellationToken.None));

        Assert.Contains("did not contain any SSE", exception.Message);
    }

    [Fact]
    public async Task ParseSseAsync_Throws_ApiException_For_Error_Event()
    {
        await using var stream = CreateStream("data: {\"code\":123,\"msg\":\"boom\"}\n");

        var exception = await Assert.ThrowsAsync<DsWebChatApiException>(() =>
            DsWebChatClient.ParseSseAsync(stream, CancellationToken.None));

        Assert.Equal(123, exception.ApiCode);
        Assert.Equal(nameof(DsWebChatClient.ParseSseAsync), exception.Operation);
    }

    [Fact]
    public async Task ParseSseAsync_Parses_Thinking_And_Response()
    {
        const string sse =
            "data: {\"v\":{\"response\":{\"fragments\":[{\"type\":\"THINK\",\"content\":\"a\"}]}}}\n" +
            "data: {\"v\":\"b\"}\n" +
            "data: {\"p\":\"response/fragments\",\"v\":[{\"type\":\"RESPONSE\",\"content\":\"c\"}]}\n" +
            "data: {\"v\":\"d\"}\n";
        await using var stream = CreateStream(sse);

        var response = await DsWebChatClient.ParseSseAsync(stream, CancellationToken.None);

        Assert.Equal("ab", response.Thinking);
        Assert.Equal("cd", response.Response);
    }

    [Fact]
    public async Task RequestCompletionAsync_Throws_For_Blank_Prompt()
    {
        await using var client = new DsWebChatClient("token", "session");

        await Assert.ThrowsAsync<ArgumentException>(() => client.RequestCompletionAsync(" "));
    }

    [Fact]
    public async Task SolvePowChallengeAsync_Throws_For_Null_Challenge()
    {
        await using var client = new DsWebChatClient("token", "session");

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.SolvePowChallengeAsync(null!));
    }

    [Fact]
    public async Task Dispose_Is_Idempotent_And_Operations_Throw_ObjectDisposedException()
    {
        var client = new DsWebChatClient("token", "session");
        client.Dispose();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.CreateSessionAsync());
    }

    private static MemoryStream CreateStream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }
}
