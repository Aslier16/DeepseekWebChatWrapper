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
        var sse = """
                  DeepseekWebChatWrapper.Test.TestDsWebChatClient.TestRequestCompletion_Simple

                  Response:
                  event: ready
                  data: {"request_message_id":1,"response_message_id":2,"model_type":"default"}

                  event: update_session
                  data: {"updated_at":1793153076.786095}

                  data: {"v":{"response":{"message_id":2,"parent_id":1,"model":"","role":"ASSISTANT","thinking_enabled":true,"ban_edit":false,"ban_regenerate":false,"status":"WIP","incomplete_message":null,"accumulated_token_usage":0,"feedback":null,"inserted_at":1793153076.773787,"search_enabled":true,"fragments":[{"id":2,"type":"THINK","content":"The","elapsed_secs":null,"references":[],"stage_id":1}],"conversation_mode":"DEFAULT","has_pending_fragment":false,"auto_continue":false,"search_triggered":false,"extra_search_providers":[]}}}

                  data: {"p":"response/fragments/-1/content","o":"APPEND","v":" user"}

                  data: {"v":" is"}

                  data: {"v":" greeting"}

                  data: {"v":" me"}

                  data: {"v":" in"}

                  data: {"v":" English"}

                  data: {"v":"."}

                  data: {"v":" This"}

                  data: {"v":" is"}

                  data: {"v":" a"}

                  data: {"v":" simple"}

                  data: {"v":" social"}

                  data: {"v":" interaction"}

                  data: {"v":" with"}

                  data: {"v":" no"}

                  data: {"v":" technical"}

                  data: {"v":" or"}

                  data: {"v":" substantive"}

                  data: {"v":" request"}

                  data: {"v":","}

                  data: {"v":" so"}

                  data: {"v":" I"}

                  data: {"v":" should"}

                  data: {"v":" respond"}

                  data: {"v":" warmly"}

                  data: {"v":" and"}

                  data: {"v":" naturally"}

                  data: {"v":"."}

                  data: {"v":" I"}

                  data: {"v":"'ll"}

                  data: {"v":" acknowledge"}

                  data: {"v":" the"}

                  data: {"v":" greeting"}

                  data: {"v":","}

                  data: {"v":" briefly"}

                  data: {"v":" express"}

                  data: {"v":" that"}

                  data: {"v":" I"}

                  data: {"v":"'m"}

                  data: {"v":" doing"}

                  data: {"v":" well"}

                  data: {"v":","}

                  data: {"v":" and"}

                  data: {"v":" leave"}

                  data: {"v":" the"}

                  data: {"v":" door"}

                  data: {"v":" open"}

                  data: {"v":" for"}

                  data: {"v":" the"}

                  data: {"v":" user"}

                  data: {"v":" to"}

                  data: {"v":" share"}

                  data: {"v":" what"}

                  data: {"v":" they"}

                  data: {"v":" need"}

                  data: {"v":"."}

                  data: {"v":" Since"}

                  data: {"v":" the"}

                  data: {"v":" user"}

                  data: {"v":" wrote"}

                  data: {"v":" in"}

                  data: {"v":" English"}

                  data: {"v":","}

                  data: {"v":" I"}

                  data: {"v":"'ll"}

                  data: {"v":" reply"}

                  data: {"v":" in"}

                  data: {"v":" English"}

                  data: {"v":" to"}

                  data: {"v":" match"}

                  data: {"v":"."}

                  data: {"v":" I"}

                  data: {"v":"'ll"}

                  data: {"v":" keep"}

                  data: {"v":" it"}

                  data: {"v":" friendly"}

                  data: {"v":" and"}

                  data: {"v":" concise"}

                  data: {"v":","}

                  data: {"v":" then"}

                  data: {"v":" invite"}

                  data: {"v":" them"}

                  data: {"v":" to"}

                  data: {"v":" continue"}

                  data: {"v":"."}

                  data: {"p":"response/fragments/-1/elapsed_secs","o":"SET","v":0.932027441}

                  data: {"p":"response/fragments","o":"APPEND","v":[{"id":3,"type":"RESPONSE","content":"Hello","references":[],"stage_id":1}]}

                  data: {"p":"response/fragments/-1/content","v":"!"}

                  data: {"v":" I"}

                  data: {"v":"'m"}

                  data: {"v":" doing"}

                  data: {"v":" well"}

                  data: {"v":","}

                  data: {"v":" thanks"}

                  data: {"v":" for"}

                  data: {"v":" asking"}

                  data: {"v":"."}

                  data: {"v":" How"}

                  data: {"v":" can"}

                  data: {"v":" I"}

                  data: {"v":" help"}

                  data: {"v":" you"}

                  data: {"v":" today"}

                  data: {"v":"?"}

                  data: {"p":"response","o":"BATCH","v":[{"p":"accumulated_token_usage","v":81},{"p":"quasi_status","v":"FINISHED"}]}

                  data: {"p":"response/status","o":"SET","v":"FINISHED"}

                  event: update_session
                  data: {"updated_at":1793153077.485468}

                  event: title
                  data: {"content":"Greeting reply"}

                  event: close
                  data: {"click_behavior":"none","auto_resume":false}
                  """;
        MemoryStream stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(sse));
        var result = await DsWebChatClient.ParseSseAsync(stream, CancellationToken.None);
        Assert.NotNull(result);
        Assert.NotNull(result.Thinking);
        _output.WriteLine("Thinking:\n" + result.Thinking);
        Assert.NotNull(result.Response);
        _output.WriteLine("Response:\n" + result.Response);
    }
}