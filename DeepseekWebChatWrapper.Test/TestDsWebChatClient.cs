using System.Text.Json;
using DeepseekWebChatWrapper.Core;
using Xunit.Abstractions;

namespace DeepseekWebChatWrapper.Test;

public class TestDsWebChatClient
{
    private readonly DsWebChatClient _dsWebChatClient;
    private readonly ITestOutputHelper _output;

    public TestDsWebChatClient(ITestOutputHelper output)
    {
        _output = output;
        var doc = JsonDocument.Parse(File.ReadAllText("TestVars.json"));
        _dsWebChatClient = new DsWebChatClient(doc.RootElement.GetProperty("authToken").GetString(),
            doc.RootElement.GetProperty("dsSessionId").GetString());
    }

    [Fact]
    public async Task TestCreatePowChallenge()
    {
        var challengeResponse = await _dsWebChatClient.CreatePowChallengeAsync();
        _output.WriteLine("Challenge Response: " + JsonSerializer.Serialize(challengeResponse));
        Assert.NotNull(challengeResponse);
        Assert.NotNull(challengeResponse.Algorithm);
        Assert.NotNull(challengeResponse.Challenge);
        Assert.NotNull(challengeResponse.Salt);
        Assert.NotNull(challengeResponse.Signature);
        Assert.True(challengeResponse.Difficulty > 0);
        Assert.True(challengeResponse.ExpireAt > 0);
        Assert.True(challengeResponse.ExpireAfter > 0);
    }

    [Fact]
    public async Task TestSolvePowChallenge()
    {
        var challengeForTest = new ChallengeResponse
        {
            Algorithm = "DeepSeekHashV1",
            Challenge = "49712f006b11453e726c575d030d88a670ec59dba653a6d6e6b530731db8f827",
            Salt = "f9c8f7da607baec0dce2",
            Signature = "2e45fb2515d44a83d6bd821a616095497bad42ff345330f37bb45a5272a31591",
            Difficulty = 144000,
            ExpireAt = 1793115765299,
            ExpireAfter = 300000,
            TargetPath = "/api/v0/chat/completions"
        };

        var answer = await _dsWebChatClient.SolvePowChallengeAsync(challengeForTest);
        _output.WriteLine("Answer: " + answer);
        ;
    }

    [Fact]
    public async Task TestCreateSession()
    {
        var sessionId = await _dsWebChatClient.CreateSessionAsync();
        _output.WriteLine("Session id: " + sessionId);
        Assert.NotNull(sessionId);
    }

    [Fact]
    public async Task TestRequestCompletion_Simple()
    {
        var response = await _dsWebChatClient.RequestCompletionAsync("Hello, how are you?");
        Assert.NotNull(response);
        Assert.NotEmpty(response.Thinking);
        Assert.NotEmpty(response.Response);
        _output.WriteLine("Response:\nThinking:\n" + response.Thinking + "\nResponse:\n" + response.Response);
    }

    [Fact]
    public async Task TestParseSseAsync()
    {
        var sse = await File.ReadAllTextAsync("ThinkAndSearchSseSample.txt");
        MemoryStream stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sse));
        var result = await DsWebChatClient.ParseSseAsync(stream, CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotNull(result.Thinking);
        _output.WriteLine("Thinking:\n" + result.Thinking);
        Assert.NotNull(result.Response);
        _output.WriteLine("Response:\n" + result.Response);
    }
}