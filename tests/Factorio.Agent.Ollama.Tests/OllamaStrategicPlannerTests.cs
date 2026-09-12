using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Factorio.Agent.Ollama.Tests;

public sealed class OllamaStrategicPlannerTests
{
    private static readonly StrategicContext Context = new("world-a:epoch-b:snapshot-17",
        "Observed iron plates: 12. One damaged furnace. No reliable information on hidden enemies.",
        "Establish iron production", "Previous proposal could not be grounded: no accessible copper patch.");
    private static OllamaOptions FastOptions => new() { MaxAttempts = 1, InitialRetryDelay = TimeSpan.Zero };

    [Fact]
    public async Task SendsCloudToolContractAndAcceptsNovelSemanticGoal()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(JsonResponse(ValidResponse())));
        using var http = new HttpClient(handler);
        var result = await new OllamaStrategicPlanner(http, FastOptions).ProposeAsync(Context);

        Assert.Equal(GoalCategory.Other, result.Category);
        Assert.Equal("Preserve the last viable iron supply during recovery", result.Description);
        Assert.Equal("last viable iron supply", result.Target);
        Assert.Equal(1, result.Quantity);
        Assert.Equal(Context.ObservationId, result.ObservationId);
        Assert.Null(result.Metrics.PromptTokens);
        Assert.Null(result.Metrics.CompletionTokens);
        Assert.Null(result.Metrics.ProviderTotalDuration);
        Assert.Equal(1, result.Metrics.Attempts);
        Assert.True(result.Metrics.Elapsed >= TimeSpan.Zero);
        Assert.Equal("http://localhost:11434/api/chat", handler.LastUrl?.AbsoluteUri);

        using var sent = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var body = sent.RootElement;
        Assert.Equal(OllamaOptions.DefaultModel, body.GetProperty("model").GetString());
        Assert.Equal("low", body.GetProperty("think").GetString());
        Assert.False(body.GetProperty("stream").GetBoolean());
        Assert.False(body.TryGetProperty("format", out _));
        Assert.False(body.TryGetProperty("options", out _));
        Assert.Equal("propose_goal", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        var messages = body.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        using var supplied = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal(Context.PreviousResult, supplied.RootElement.GetProperty("previousResult").GetString());
        Assert.Equal(Context.CurrentGoal, supplied.RootElement.GetProperty("currentGoal").GetString());
    }

    [Theory]
    [MemberData(nameof(InvalidResponses))]
    public async Task RejectsUntrustedResponsesWithoutRetry(string _, string response)
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(JsonResponse(response)));
        using var http = new HttpClient(handler);
        var planner = new OllamaStrategicPlanner(http, FastOptions with { MaxAttempts = 3 });
        var error = await Assert.ThrowsAsync<PlannerException>(() => planner.ProposeAsync(Context));
        Assert.Equal(PlannerErrorKind.InvalidResponse, error.Kind);
        Assert.Equal(1, handler.Calls);
    }

    public static IEnumerable<object[]> InvalidResponses()
    {
        yield return ["malformed JSON", "{"];
        yield return ["array envelope", "[]"];
        yield return ["text only", """{"done":true,"message":{"role":"assistant","content":"Build a rocket"}}"""];
        yield return ["provider error", """{"error":"unexpected provider error"}"""];
        yield return Case("unfinished", r => r["done"] = false);
        yield return Case("truncated", r => r["done_reason"] = "length");
        yield return Case("missing field", r => Args(r).Remove("target"));
        yield return Case("unknown field", r => Args(r)["lua"] = "game.print('unsafe')");
        yield return Case("coordinate field", r => Args(r)["position"] = new JsonObject { ["x"] = 1, ["y"] = 2 });
        yield return Case("unknown tool", r => Function(r)["name"] = "execute_lua");
        yield return Case("multiple tools", r => r["message"]!["tool_calls"]!.AsArray().Add(r["message"]!["tool_calls"]![0]!.DeepClone()));
        yield return Case("tool arguments string", r => Function(r)["arguments"] = Args(r).ToJsonString());
        yield return Case("stale observation", r => Args(r)["observationId"] = "snapshot-16");
        yield return Case("unknown category", r => Args(r)["category"] = "cheat");
        yield return Case("numeric enum", r => Args(r)["priority"] = 2);
        yield return Case("unknown unit", r => Args(r)["unit"] = "tiles");
        yield return Case("zero quantity", r => Args(r)["quantity"] = 0);
        yield return Case("negative quantity", r => Args(r)["quantity"] = -1);
        yield return Case("large quantity", r => Args(r)["quantity"] = 1_000_000_001L);
        yield return Case("text quantity", r => Args(r)["quantity"] = "1");
        yield return Case("fractional item", r => { Args(r)["unit"] = "items"; Args(r)["quantity"] = 1.5m; });
        yield return Case("completion quantity", r => Args(r)["quantity"] = 2);
        yield return Case("empty target", r => Args(r)["target"] = "  ");
        yield return Case("null description", r => Args(r)["description"] = null);
        yield return Case("oversize description", r => Args(r)["description"] = new string('a', 1001));
        yield return Case("oversize target", r => Args(r)["target"] = new string('a', 129));
        yield return Case("control character", r => Args(r)["description"] = "bad\u0000description");
        yield return Case("unknown function field", r => Function(r)["execute"] = true);
        yield return Case("invalid tool type", r => r["message"]!["tool_calls"]![0]!["type"] = "shell");
        yield return Case("invalid index", r => Function(r)["index"] = -1);
        yield return Case("invalid metric", r => r["eval_count"] = -2);
        yield return ["duplicate field", ValidResponse().Replace("\"category\":\"other\"", "\"category\":\"other\",\"category\":\"production\"", StringComparison.Ordinal)];
        yield return ["duplicate envelope", ValidResponse().Replace("\"done\":true", "\"done\":false,\"done\":true", StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, PlannerErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, PlannerErrorKind.Authentication)]
    [InlineData(HttpStatusCode.NotFound, PlannerErrorKind.MissingModel)]
    [InlineData(HttpStatusCode.TooManyRequests, PlannerErrorKind.Quota)]
    [InlineData(HttpStatusCode.BadGateway, PlannerErrorKind.Network)]
    [InlineData(HttpStatusCode.BadRequest, PlannerErrorKind.RequestRejected)]
    public async Task ClassifiesProviderFailuresWithoutEchoingResponseBody(HttpStatusCode status, PlannerErrorKind expected)
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent("provider-secret-must-not-appear")
        }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<PlannerException>(() => new OllamaStrategicPlanner(http, FastOptions).ProposeAsync(Context));
        Assert.Equal(expected, error.Kind);
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("provider-secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuotaExhaustsOnlyConfiguredNumberOfAttempts()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<PlannerException>(() => new OllamaStrategicPlanner(http,
            FastOptions with { MaxAttempts = 3 }).ProposeAsync(Context));
        Assert.Equal(PlannerErrorKind.Quota, error.Kind);
        Assert.Equal(3, error.Attempts);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task LongRetryAfterSuspendsWithoutRetryingEarly()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(QuotaResponse(TimeSpan.FromHours(1))));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<PlannerException>(() => new OllamaStrategicPlanner(http,
            FastOptions with { MaxAttempts = 3, MaxRetryDelay = TimeSpan.FromSeconds(2) }).ProposeAsync(Context));
        Assert.Equal(TimeSpan.FromHours(1), error.RetryAfter);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RetryAfterHttpDateAlsoSuspendsWhenOutsideTheBudget()
    {
        using var handler = new ControlledHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddHours(1));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<PlannerException>(() => new OllamaStrategicPlanner(http,
            FastOptions with { MaxAttempts = 3 }).ProposeAsync(Context));
        Assert.True(error.RetryAfter > TimeSpan.FromMinutes(59));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task DoesNotRetryAuthenticationModelOrRequestFailures(HttpStatusCode status)
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<PlannerException>(() => new OllamaStrategicPlanner(http,
            FastOptions with { MaxAttempts = 3 }).ProposeAsync(Context));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RetryAfterIsRespectedBeforeSuccessfulSecondAttempt()
    {
        var calls = 0;
        using var handler = new ControlledHandler((_, _) => Task.FromResult(++calls == 1
            ? QuotaResponse(TimeSpan.FromSeconds(1)) : JsonResponse(ValidResponse())));
        using var http = new HttpClient(handler);
        var stopwatch = Stopwatch.StartNew();
        var result = await new OllamaStrategicPlanner(http,
            FastOptions with { MaxAttempts = 2, MaxRetryDelay = TimeSpan.FromSeconds(2) }).ProposeAsync(Context);
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(950));
        Assert.Equal(2, result.Metrics.Attempts);
        Assert.Equal(2, handler.Calls);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
    }

    [Fact]
    public async Task CallerCanCancelDuringQuotaBackoff()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(QuotaResponse(TimeSpan.FromSeconds(1))));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var planner = new OllamaStrategicPlanner(http, FastOptions with { MaxAttempts = 3 });
        var pending = planner.ProposeAsync(Context, cancellation.Token);
        await handler.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PerRequestTimeoutIsDistinctFromCallerCancellation()
    {
        using var handler = new ControlledHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler);
        var planner = new OllamaStrategicPlanner(http, FastOptions with { RequestTimeout = TimeSpan.FromMilliseconds(30) });
        var error = await Assert.ThrowsAsync<PlannerException>(() => planner.ProposeAsync(Context));
        Assert.Equal(PlannerErrorKind.Timeout, error.Kind);
        Assert.Contains("consumption may still have occurred", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellationDoesNotBecomeTimeoutOrRetry()
    {
        using var handler = new ControlledHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("Unreachable");
        });
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        var pending = new OllamaStrategicPlanner(http, FastOptions with { MaxAttempts = 3 }).ProposeAsync(Context, cancellation.Token);
        await handler.Started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedWithoutRetries()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(JsonResponse(new string(' ', 2 * 1024 * 1024 + 1))));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<PlannerException>(() => new OllamaStrategicPlanner(http,
            FastOptions with { MaxAttempts = 3 }).ProposeAsync(Context));
        Assert.Equal(PlannerErrorKind.InvalidResponse, error.Kind);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task NetworkFailureCanRecoverWithinBudget()
    {
        var calls = 0;
        using var handler = new ControlledHandler((_, _) => ++calls == 1
            ? throw new HttpRequestException("simulated unreachable cloud")
            : Task.FromResult(JsonResponse(ValidResponse())));
        using var http = new HttpClient(handler);
        var result = await new OllamaStrategicPlanner(http, FastOptions with { MaxAttempts = 2 }).ProposeAsync(Context);
        Assert.Equal(2, result.Metrics.Attempts);
    }

    [Fact]
    public async Task ReadsAvailableMetricsIncludingZeroWithoutInventingMissingCounts()
    {
        var response = JsonNode.Parse(ValidResponse())!.AsObject();
        response["prompt_eval_count"] = 0;
        response["eval_count"] = 123;
        response["total_duration"] = 1_234_567_800L;
        using var handler = new ControlledHandler((_, _) => Task.FromResult(JsonResponse(response.ToJsonString())));
        using var http = new HttpClient(handler);
        var result = await new OllamaStrategicPlanner(http, FastOptions).ProposeAsync(Context);
        Assert.Equal(0, result.Metrics.PromptTokens);
        Assert.Equal(123, result.Metrics.CompletionTokens);
        Assert.Equal(TimeSpan.FromTicks(12_345_678), result.Metrics.ProviderTotalDuration);
    }

    [Fact]
    public async Task BoundedContextIsCheckedBeforeAnyHttpCall()
    {
        using var handler = new ControlledHandler((_, _) => Task.FromResult(JsonResponse(ValidResponse())));
        using var http = new HttpClient(handler);
        var planner = new OllamaStrategicPlanner(http, FastOptions);
        await Assert.ThrowsAsync<ArgumentException>(() => planner.ProposeAsync(Context with { Facts = new string('x', 24_001) }));
        await Assert.ThrowsAsync<ArgumentException>(() => planner.ProposeAsync(Context with { PreviousResult = new string('x', 4_001) }));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("https://ollama.com/")]
    [InlineData("http://user:password@localhost:11434/")]
    [InlineData("http://localhost:11434/?key=secret")]
    public void RefusesNonGatewayOrCredentialBearingUrls(string url)
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new OllamaStrategicPlanner(http, new OllamaOptions { BaseUrl = new Uri(url) }));
    }

    [Fact]
    public void RefusesUnboundedRetriesWrongModelAndUnsupportedReasoning()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentOutOfRangeException>(() => new OllamaStrategicPlanner(http, new OllamaOptions { MaxAttempts = 4 }));
        Assert.Throws<ArgumentException>(() => new OllamaStrategicPlanner(http, new OllamaOptions { Model = "different-model" }));
        Assert.Throws<ArgumentException>(() => new OllamaStrategicPlanner(http, new OllamaOptions { ThinkingEffort = "false" }));
    }

    private static object[] Case(string name, Action<JsonObject> mutate)
    {
        var response = JsonNode.Parse(ValidResponse())!.AsObject();
        mutate(response);
        return [name, response.ToJsonString()];
    }

    private static JsonObject Function(JsonObject root) => root["message"]!["tool_calls"]![0]!["function"]!.AsObject();
    private static JsonObject Args(JsonObject root) => Function(root)["arguments"]!.AsObject();

    private static string ValidResponse() => """
        {"done":true,"message":{"role":"assistant","tool_calls":[{"function":{"name":"propose_goal","arguments":{"observationId":"world-a:epoch-b:snapshot-17","description":"Preserve the last viable iron supply during recovery","category":"other","target":"last viable iron supply","quantity":1,"unit":"completion","priority":"high"}}}]}}
        """;

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage QuotaResponse(TimeSpan delay)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        return response;
    }

    private sealed class ControlledHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Bodies { get; } = [];
        public Uri? LastUrl { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Started.TrySetResult();
            return await handler(request, cancellationToken);
        }
    }
}
