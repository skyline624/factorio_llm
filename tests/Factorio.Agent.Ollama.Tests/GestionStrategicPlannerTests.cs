using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Factorio.Agent.Gestion;
using Xunit;

namespace Factorio.Agent.Ollama.Tests;

public sealed class GestionStrategicPlannerTests
{
    private const string Model = "synthetic-model-q2";
    private const string Key = "synthetic-gestion-key";
    private static readonly StrategicContext Context = new("world-a:epoch-b:snapshot-17", "Observed iron plates: 12.");
    private static GestionOptions Options => new() { BaseUrl = new Uri("https://192.168.1.33/"), Model = Model, ApiKey = Key };

    private static string Arguments(string extra = "") => JsonSerializer.Serialize(
        """{"observationId":"world-a:epoch-b:snapshot-17","description":"Keep iron flowing","category":"production","target":"iron-plate","quantity":60,"unit":"items_per_minute","priority":"normal"EXTRA}"""
            .Replace("EXTRA", extra));

    private static string Response(string argumentsLiteral, string model = Model, string finish = "tool_calls") =>
        """{"id":"chatcmpl-1","object":"chat.completion","model":"MODEL","choices":[{"index":0,"finish_reason":"FINISH","message":{"role":"assistant","content":"","reasoning_content":"thinking","tool_calls":[{"id":"c1","type":"function","function":{"name":"propose_goal","arguments":ARGS}}]}}],"usage":{"prompt_tokens":14,"completion_tokens":6,"total_tokens":20},"timings":{"predicted_ms":5}}"""
            .Replace("MODEL", model).Replace("FINISH", finish).Replace("ARGS", argumentsLiteral);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static Task<GoalProposal> Propose(Func<HttpRequestMessage, HttpResponseMessage> respond, out Handler handler)
    {
        handler = new Handler(respond);
        return new GestionStrategicPlanner(new HttpClient(handler), Options).ProposeAsync(Context);
    }

    [Fact]
    public async Task ValidToolCallWithStringArgumentsIsProposedThroughBearerPost()
    {
        var proposal = await Propose(_ => Json(Response(Arguments())), out var handler);
        Assert.Equal("iron-plate", proposal.Target);
        Assert.Equal(GoalUnit.ItemsPerMinute, proposal.Unit);
        Assert.Equal(14, proposal.Metrics.PromptTokens);
        Assert.Equal(6, proposal.Metrics.CompletionTokens);
        Assert.Equal(1, proposal.Metrics.Attempts);
        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://192.168.1.33/v1/chat/completions", handler.Request.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.Request.Headers.Authorization?.Scheme);
        Assert.Equal(Key, handler.Request.Headers.Authorization?.Parameter);
        using var sent = JsonDocument.Parse(handler.Body);
        var root = sent.RootElement;
        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("propose_goal", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("system", root.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Contains("snapshot-17", root.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.DoesNotContain(Key, handler.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherModelInTheResponseIsRefused()
    {
        var error = await Assert.ThrowsAsync<PlannerException>(() =>
            Propose(_ => Json(Response(Arguments(), model: "other-model")), out _));
        Assert.Equal(PlannerErrorKind.MissingModel, error.Kind);
    }

    [Fact]
    public async Task ObjectArgumentsAreNotTheOpenAiFormat()
    {
        const string objectArguments = """{"observationId":"world-a:epoch-b:snapshot-17","description":"d","category":"production","target":"t","quantity":1,"unit":"items","priority":"low"}""";
        var error = await Assert.ThrowsAsync<PlannerException>(() => Propose(_ => Json(Response(objectArguments)), out _));
        Assert.Equal(PlannerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task TruncatedResponseIsInvalid()
    {
        var error = await Assert.ThrowsAsync<PlannerException>(() =>
            Propose(_ => Json(Response(Arguments(), finish: "length")), out _));
        Assert.Equal(PlannerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task DuplicatePropertyInsideArgumentsIsInvalid()
    {
        var error = await Assert.ThrowsAsync<PlannerException>(() =>
            Propose(_ => Json(Response(Arguments(",\"priority\":\"high\""))), out _));
        Assert.Equal(PlannerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task MalformedArgumentsAreInvalid()
    {
        var error = await Assert.ThrowsAsync<PlannerException>(() =>
            Propose(_ => Json(Response("\"{not json\"")), out _));
        Assert.Equal(PlannerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task AuthenticationAndQuotaFailuresAreClassifiedWithoutTheBody()
    {
        var auth = await Assert.ThrowsAsync<PlannerException>(() =>
            Propose(_ => Json("echoed " + Key, HttpStatusCode.Unauthorized), out _));
        Assert.Equal(PlannerErrorKind.Authentication, auth.Kind);
        Assert.DoesNotContain(Key, auth.ToString(), StringComparison.Ordinal);

        var quota = await Assert.ThrowsAsync<PlannerException>(() => Propose(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return response;
        }, out _));
        Assert.Equal(PlannerErrorKind.Quota, quota.Kind);
        Assert.Equal(TimeSpan.FromSeconds(7), quota.RetryAfter);
    }

    [Fact]
    public async Task ServerErrorsAreNetworkAndOnlyOneAttemptIsMade()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var error = await Assert.ThrowsAsync<PlannerException>(() =>
            new GestionStrategicPlanner(new HttpClient(handler), Options).ProposeAsync(Context));
        Assert.Equal(PlannerErrorKind.Network, error.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("http://192.168.1.33/", Model, Key)]
    [InlineData("https://192.168.1.33/", "llm-actif", Key)]
    [InlineData("https://192.168.1.33/", "LLM-Actif", Key)]
    [InlineData("https://192.168.1.33/", Model, "key with space")]
    [InlineData("https://192.168.1.33/", Model, "")]
    [InlineData("https://user:pw@192.168.1.33/", Model, Key)]
    [InlineData("https://192.168.1.33/?k=v", Model, Key)]
    public void OptionsRefuseUnsafeSettings(string url, string model, string key)
    {
        var options = new GestionOptions { BaseUrl = new Uri(url), Model = model, ApiKey = key };
        Assert.Throws<ArgumentException>(options.Validate);
    }

    [Fact]
    public void OptionsDoNotExposeTheKey()
    {
        Assert.DoesNotContain(Key, Options.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(Options), StringComparison.Ordinal);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? Request { get; private set; }
        public string Body { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }
}
