using System.Diagnostics;
using System.Text.Json;
using Factorio.Agent.Codex;
using Factorio.Agent.Ollama;
using Xunit;

namespace Factorio.Agent.Ollama.Tests;

public sealed class CodexStrategicPlannerTests
{
    private const string Goal = """{"observationId":"obs","description":"Bootstrap iron plates","category":"production","target":"iron-plate","quantity":20,"unit":"items","priority":"normal"}""";

    private static string Event(string method, object data) => JsonSerializer.Serialize(new { method, @params = data });
    private static string Final(string text) => Event("item/completed", new
    { threadId = "thread", turnId = "turn", item = new { type = "agentMessage", phase = "final_answer", text } });
    private static string Completed(string status = "completed") => Event("turn/completed", new
    { threadId = "thread", turn = new { id = "turn", status } });
    private static Task<GoalProposal> ParseAsync(params string[] events) => CodexStrategicPlanner.ReadGoalAsync(
        new CodexRpc(new StringReader(string.Join('\n', events) + "\n"), new StringWriter()),
        "thread", "turn", "obs", Stopwatch.GetTimestamp(), CancellationToken.None);

    [Fact]
    public async Task CompletedGoalUsesTheSameSemanticValidatorAndUsageMetrics()
    {
        var goal = await ParseAsync(Event("item/started", new { threadId = "thread", turnId = "turn", item = new { type = "userMessage" } }),
            Event("thread/tokenUsage/updated", new
        { threadId = "thread", turnId = "turn", tokenUsage = new { total = new { inputTokens = 1200, outputTokens = 110 } } }),
            Final(Goal), Completed());
        Assert.Equal("iron-plate", goal.Target);
        Assert.Equal(20, goal.Quantity);
        Assert.Equal(1200, goal.Metrics.PromptTokens);
        Assert.Equal(110, goal.Metrics.CompletionTokens);
        Assert.Equal(1, goal.Metrics.Attempts);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("interrupted")]
    public async Task FailureCannotUseAnAlreadyProducedGoal(string status)
    {
        await Assert.ThrowsAsync<PlannerException>(() => ParseAsync(Final(Goal), Completed(status)));
    }

    [Theory]
    [InlineData("commandExecution")]
    [InlineData("mcpToolCall")]
    [InlineData("dynamicToolCall")]
    public async Task UnexpectedToolItemsAreRejected(string type)
    {
        var error = await Assert.ThrowsAsync<PlannerException>(() => ParseAsync(
            Event("item/started", new { threadId = "thread", turnId = "turn", item = new { type } }), Final(Goal), Completed()));
        Assert.Equal(PlannerErrorKind.RequestRejected, error.Kind);
    }

    [Theory]
    [InlineData("\"obs\"", "\"stale\"")]
    [InlineData("\"quantity\":20", "\"quantity\":0")]
    [InlineData("\"quantity\":20", "\"quantity\":1.5")]
    [InlineData("\"category\":\"production\"", "\"category\":\"Lua\"")]
    [InlineData("\"observationId\":\"obs\"", "\"observationId\":\"obs\",\"observationId\":\"obs\"")]
    public async Task UnsafeOrStaleGoalIsRejected(string oldValue, string newValue)
    {
        var error = await Assert.ThrowsAsync<PlannerException>(() => ParseAsync(Final(Goal.Replace(oldValue, newValue)), Completed()));
        Assert.Equal(PlannerErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task CommentaryAndOtherTurnsDoNotBecomeTheGoal()
    {
        var goal = await ParseAsync(Event("item/completed", new
        { threadId = "other", turnId = "turn", item = new { type = "agentMessage", text = "bad" } }),
            Event("item/completed", new { threadId = "thread", turnId = "turn",
                item = new { type = "agentMessage", phase = "commentary", text = "Thinking" } }), Final(Goal), Completed());
        Assert.Equal("iron-plate", goal.Target);
    }

    [Fact]
    public async Task DuplicateFinalMessagesAreRejected()
    {
        await Assert.ThrowsAsync<PlannerException>(() => ParseAsync(Final(Goal), Final(Goal), Completed()));
    }

    [Fact]
    public async Task NotificationsArrivingBeforeTheReplyArePreserved()
    {
        var output = new StringWriter();
        var rpc = new CodexRpc(new StringReader(Final(Goal) + "\n" + """{"id":1,"result":{"ok":true}}""" + "\n"), output);
        var result = await rpc.RequestAsync("turn/start", new { }, CancellationToken.None);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("item/completed", (await rpc.NextNotificationAsync(CancellationToken.None)).GetProperty("method").GetString());
    }

    [Fact]
    public async Task ServerRequestsAreNeverDispatchedOrEchoed()
    {
        var output = new StringWriter();
        var rpc = new CodexRpc(new StringReader("""{"id":42,"method":"item/commandExecution/requestApproval","params":{"secret":"synthetic-secret"}}""" + "\n"), output);
        var error = await Assert.ThrowsAsync<PlannerException>(() => rpc.RequestAsync("turn/start", new { }, CancellationToken.None));
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.DoesNotContain("synthetic-secret", output.ToString());
        Assert.Equal(PlannerErrorKind.RequestRejected, error.Kind);
    }

    [Fact]
    public async Task OversizedProtocolMessageAndDuplicateFieldsAreRejected()
    {
        foreach (string line in new[] { new string(' ', 262145), """{"method":"a","method":"b"}""" })
        {
            var rpc = new CodexRpc(new StringReader(line + "\n"), new StringWriter());
            await Assert.ThrowsAnyAsync<Exception>(() => rpc.NextNotificationAsync(CancellationToken.None));
        }
    }

    [Fact]
    public void ChildHasNoWindowAndDisablesDesktopShellAndPlugins()
    {
        var info = CodexStrategicPlanner.CreateStartInfo(new CodexOptions(), Path.GetTempPath());
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Contains("computer_use", info.ArgumentList);
        Assert.Contains("browser_use", info.ArgumentList);
        Assert.Contains("shell_tool", info.ArgumentList);
        Assert.Contains("hooks", info.ArgumentList);
        Assert.Contains("plugins", info.ArgumentList);
        Assert.Contains("forced_login_method=\"chatgpt\"", info.ArgumentList);
        Assert.False(info.Environment.ContainsKey("OPENAI_API_KEY"));
    }

    [Fact]
    public async Task MissingExecutableIsReportedWithoutLeakingTheConfiguredPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), "factorio-codex-" + Guid.NewGuid().ToString("N"));
        string executable = Path.Combine(directory, "synthetic-private-missing.exe");
        var planner = new CodexStrategicPlanner(new CodexOptions { Executable = executable }, directory);
        try
        {
            var error = await Assert.ThrowsAsync<PlannerException>(() => planner.ProposeAsync(
                new StrategicContext("obs", "Synthetic offline context; no game or model call.")));
            Assert.Equal(PlannerErrorKind.RequestRejected, error.Kind);
            Assert.Equal(1, error.Attempts);
            Assert.Contains("Codex.Executable", error.Message);
            Assert.DoesNotContain(directory, error.ToString());
            Assert.DoesNotContain("synthetic-private-missing", error.ToString());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory); }
    }

    [Fact]
    public void NoModelAliasOrUnboundedTimeoutIsAccepted()
    {
        Assert.Throws<ArgumentException>(() => new CodexOptions { Model = "gpt-6-astra" }.Validate());
        Assert.Throws<ArgumentException>(() => new CodexOptions { RequestTimeoutSeconds = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new CodexOptions { ThinkingEffort = "ultra" }.Validate());
    }
}
