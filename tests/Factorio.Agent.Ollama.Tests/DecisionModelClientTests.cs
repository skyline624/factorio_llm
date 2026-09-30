using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Factorio.Agent.Ollama.Tests;

public sealed class DecisionModelClientTests
{
    private static readonly DecisionQuestion Next = new("choice", "Which goal next?",
        new Dictionary<string, string?> { ["research:automation"] = "Research automation", ["none"] = "Nothing fits" });

    [Fact]
    public async Task SendsASystemOneChoiceAndReturnsTheValidatedAnswer()
    {
        JsonElement? sent = null;
        string? path = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            path = request.RequestUri!.AbsolutePath;
            sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
            return Json("""{"model":"nimble:9b-q4_K_M","answers":{"next":{"type":"choice","choice":"research:automation","probabilities":{"research:automation":0.9,"none":0.1},"confidence":0.53}},"usage":{"input_tokens":120,"output_tokens":1}}""");
        }));
        var result = await new DecisionModelClient(http, new DecisionModelOptions()).DecideAsync("{\"tick\":1}",
            new Dictionary<string, DecisionQuestion> { ["next"] = Next });
        Assert.Equal("/v1/systemone", path);
        Assert.Equal("nimble:9b-q4_K_M", sent!.Value.GetProperty("model").GetString());
        Assert.Equal("choice", sent.Value.GetProperty("questions").GetProperty("next").GetProperty("type").GetString());
        Assert.Equal("Research automation", sent.Value.GetProperty("questions").GetProperty("next").GetProperty("criteria").GetProperty("research:automation").GetString());
        var answer = result.Answers["next"];
        Assert.Equal("research:automation", answer.Choice);
        Assert.Equal(0.9, answer.Probabilities["research:automation"], 6);
        Assert.Equal(120, result.InputTokens);
    }

    [Theory]
    [InlineData("""{"model":"m","answers":{"next":{"type":"choice","choice":"invented","probabilities":{"research:automation":0.5,"none":0.5},"confidence":0}},"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"model":"m","answers":{"next":{"type":"choice","choice":"none","probabilities":{"research:automation":0.9,"none":0.9},"confidence":0}},"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"model":"m","answers":{"other":{"type":"choice","choice":"none","probabilities":{"research:automation":0.1,"none":0.9},"confidence":0}},"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("""{"model":"m","answers":{},"usage":{"input_tokens":1,"output_tokens":1}}""")]
    [InlineData("not json")]
    public async Task RejectsAnswersOutsideTheSuppliedOptionsOrInconsistentProbabilities(string body)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(body))));
        await Assert.ThrowsAsync<DecisionModelException>(() => new DecisionModelClient(http, new DecisionModelOptions())
            .DecideAsync("state", new Dictionary<string, DecisionQuestion> { ["next"] = Next }));
    }

    [Fact]
    public async Task NoulAnswersAreTheNativeProbabilityOfYes()
    {
        // Shape observed from Ollama 0.35.0 with nimble:9b-q4_K_M on 2026-09-30.
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(
            """{"model":"nimble:9b-q4_K_M","answers":{"sound":{"type":"noul","noul":0.6575212600703428}},"usage":{"input_tokens":764,"output_tokens":3}}"""))));
        var result = await new DecisionModelClient(http, new DecisionModelOptions())
            .DecideAsync("state", new Dictionary<string, DecisionQuestion> { ["sound"] = new("noul", "Is it sound?") });
        var answer = result.Answers["sound"];
        Assert.Equal("true", answer.Choice);
        Assert.Equal(0.6575212600703428, answer.Probabilities["true"], 9);
        Assert.Equal(1 - 0.6575212600703428, answer.Probabilities["false"], 9);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("true")]
    public async Task NoulOutsideTheUnitIntervalIsRejected(string value)
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json(
            "{\"model\":\"m\",\"answers\":{\"sound\":{\"type\":\"noul\",\"noul\":" + value + "}},\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}"))));
        await Assert.ThrowsAsync<DecisionModelException>(() => new DecisionModelClient(http, new DecisionModelOptions())
            .DecideAsync("state", new Dictionary<string, DecisionQuestion> { ["sound"] = new("noul", "Is it sound?") }));
    }

    [Fact]
    public async Task ServerErrorsAreReportedOnceWithoutRetry()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)); }));
        var error = await Assert.ThrowsAsync<DecisionModelException>(() => new DecisionModelClient(http, new DecisionModelOptions())
            .DecideAsync("state", new Dictionary<string, DecisionQuestion> { ["next"] = Next }));
        Assert.Equal(1, calls);
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
    }

    [Fact]
    public async Task OversizedRequestsAreRefusedBeforeSending()
    {
        int calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Task.FromResult(Json("{}")); }));
        await Assert.ThrowsAsync<DecisionModelException>(() => new DecisionModelClient(http, new DecisionModelOptions())
            .DecideAsync(new string('x', 70_000), new Dictionary<string, DecisionQuestion> { ["next"] = Next }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("https://ollama.com/")]
    [InlineData("http://192.168.1.5:11434/")]
    public void OnlyALocalDecisionServerIsAccepted(string url)
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new DecisionModelClient(http, new DecisionModelOptions { BaseUrl = new(url) }));
    }

    [Fact]
    public async Task QuestionsNeedTwoToTwentySixOptions()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Json("{}"))));
        var client = new DecisionModelClient(http, new DecisionModelOptions());
        var single = Next with { Criteria = new Dictionary<string, string?> { ["only"] = null } };
        var many = Next with { Criteria = Enumerable.Range(0, 27).ToDictionary(i => $"o{i}", _ => (string?)null) };
        await Assert.ThrowsAsync<ArgumentException>(() => client.DecideAsync("s", new Dictionary<string, DecisionQuestion> { ["q"] = single }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DecideAsync("s", new Dictionary<string, DecisionQuestion> { ["q"] = many }));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
