using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Codex;

/// <summary>Account-backed inference through the official Codex app-server. No game or desktop executor.</summary>
public sealed class CodexStrategicPlanner : IStrategicPlanner
{
    private readonly CodexOptions options;
    private readonly string directory;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string Instructions = GoalContract.SystemPrompt
        .Replace("using propose_goal", "as a JSON object")
        .Replace("return exactly one tool call", "return exactly one JSON object matching the output schema")
        + "\nNo tools are needed or permitted. Use only the supplied factual context.";

    public CodexStrategicPlanner(CodexOptions options, string directory)
    {
        options.Validate();
        this.options = options;
        this.directory = Path.GetFullPath(directory);
    }

    public async Task<GoalProposal> ProposeAsync(StrategicContext context, CancellationToken cancellationToken = default)
    {
        GoalContract.ValidateContext(context);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var started = Stopwatch.GetTimestamp();
        using var process = new Process { StartInfo = CreateStartInfo(options, directory) };
        Task? stderr = null;
        bool processStarted = false;
        try
        {
            if (!process.Start()) throw Failure(PlannerErrorKind.Network);
            processStarted = true;
            // Drain diagnostics without retaining or logging them: providers/config errors can echo secrets.
            stderr = DrainAsync(process.StandardError, linked.Token);
            var rpc = new CodexRpc(process.StandardOutput, process.StandardInput);
            await rpc.RequestAsync("initialize", new
            {
                clientInfo = new { name = "factorio_llm", title = "Factorio LLM", version = "1.0.0" },
                capabilities = new { experimentalApi = true }
            }, linked.Token);
            await rpc.NotifyAsync("initialized", new { }, linked.Token);
            var account = await rpc.RequestAsync("account/read", new { refreshToken = false }, linked.Token);
            if (!account.TryGetProperty("account", out var identity) || identity.ValueKind != JsonValueKind.Object ||
                identity.GetProperty("type").GetString() != "chatgpt")
                throw Failure(PlannerErrorKind.Authentication);

            var thread = await rpc.RequestAsync("thread/start", new
            {
                model = options.Model, modelProvider = "openai", allowProviderModelFallback = false,
                cwd = directory, ephemeral = true, approvalPolicy = "never", sandbox = "read-only",
                environments = Array.Empty<object>(), dynamicTools = Array.Empty<object>(),
                baseInstructions = Instructions, developerInstructions = "Return only the strategic goal JSON. Never call tools.",
                config = new Dictionary<string, object>
                {
                    ["model_reasoning_effort"] = options.ThinkingEffort,
                    ["project_doc_max_bytes"] = 0,
                    ["web_search"] = "disabled"
                }
            }, linked.Token);
            if (thread.GetProperty("model").GetString() != options.Model || thread.GetProperty("modelProvider").GetString() != "openai")
                throw Failure(PlannerErrorKind.MissingModel);
            string threadId = thread.GetProperty("thread").GetProperty("id").GetString()!;
            var schema = JsonSerializer.SerializeToElement(GoalContract.ToolSchema, Json)
                .GetProperty("function").GetProperty("parameters");
            var turn = await rpc.RequestAsync("turn/start", new
            {
                threadId, model = options.Model, effort = options.ThinkingEffort,
                environments = Array.Empty<object>(), outputSchema = schema,
                input = new[] { new { type = "text", text = JsonSerializer.Serialize(context, Json), text_elements = Array.Empty<object>() } }
            }, linked.Token);
            string turnId = turn.GetProperty("turn").GetProperty("id").GetString()!;
            return await ReadGoalAsync(rpc, threadId, turnId, context.ObservationId, started, linked.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw Failure(PlannerErrorKind.Timeout); }
        catch (Win32Exception error) when (error.NativeErrorCode is 2 or 3)
        {
            throw new PlannerException(PlannerErrorKind.RequestRejected,
                "Configured Codex executable was not found; update Codex.Executable or PATH.", 1);
        }
        catch (Win32Exception) { throw Failure(PlannerErrorKind.Network); }
        catch (IOException) { throw Failure(PlannerErrorKind.Network); }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw Failure(PlannerErrorKind.InvalidResponse); }
        finally
        {
            if (processStarted && !process.HasExited) process.Kill(entireProcessTree: true);
            linked.Cancel();
            if (stderr is not null)
                try { await stderr; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(CodexOptions options, string directory)
    {
        var info = new ProcessStartInfo(options.Executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in new[] { "app-server", "--listen", "stdio://" }) info.ArgumentList.Add(argument);
        foreach (string feature in new[] { "shell_tool", "unified_exec", "code_mode_host", "apps", "plugins", "hooks",
            "multi_agent", "multi_agent_v2", "computer_use", "browser_use", "in_app_browser", "image_generation", "goals" })
        { info.ArgumentList.Add("--disable"); info.ArgumentList.Add(feature); }
        foreach (string setting in new[] { "forced_login_method=\"chatgpt\"", "model_provider=\"openai\"", "web_search=\"disabled\"",
            "project_doc_max_bytes=0", "mcp_servers={}", "history.persistence=\"none\"" })
        { info.ArgumentList.Add("-c"); info.ArgumentList.Add(setting); }
        // Account selection is the existing stored ChatGPT login, never a silently inherited API credential.
        foreach (string variable in new[] { "OPENAI_API_KEY", "CODEX_API_KEY", "CODEX_ACCESS_TOKEN", "OPENAI_BASE_URL",
            "OPENAI_IDENTITY_TOKEN_FILE", "OPENAI_WORKLOAD_IDENTITY_CONFIG" }) info.Environment.Remove(variable);
        return info;
    }

    internal static async Task<GoalProposal> ReadGoalAsync(CodexRpc rpc, string threadId, string turnId,
        string observationId, long started, CancellationToken token)
    {
        string? text = null;
        long? inputTokens = null, outputTokens = null;
        while (true)
        {
            var message = await rpc.NextNotificationAsync(token);
            string? method = message.GetProperty("method").GetString();
            var data = message.GetProperty("params");
            if (!data.TryGetProperty("threadId", out var owner) || owner.GetString() != threadId) continue;
            if (data.TryGetProperty("turnId", out var id) && id.GetString() != turnId) continue;
            if (method is "item/started" or "item/completed")
            {
                var item = data.GetProperty("item");
                string? type = item.GetProperty("type").GetString();
                if (type is not ("userMessage" or "agentMessage" or "reasoning")) throw Failure(PlannerErrorKind.RequestRejected);
                if (method == "item/completed" && type == "agentMessage" &&
                    (!item.TryGetProperty("phase", out var phase) || phase.ValueKind == JsonValueKind.Null || phase.GetString() == "final_answer"))
                {
                    if (text is not null) throw Failure(PlannerErrorKind.InvalidResponse);
                    text = item.GetProperty("text").GetString();
                }
            }
            if (method == "thread/tokenUsage/updated")
            {
                var usage = data.GetProperty("tokenUsage").GetProperty("total");
                inputTokens = usage.GetProperty("inputTokens").GetInt64();
                outputTokens = usage.GetProperty("outputTokens").GetInt64();
                if (inputTokens < 0 || outputTokens < 0) throw Failure(PlannerErrorKind.InvalidResponse);
            }
            if (method != "turn/completed") continue;
            var turn = data.GetProperty("turn");
            if (turn.GetProperty("id").GetString() != turnId) continue;
            if (turn.GetProperty("status").GetString() != "completed") throw Failure(PlannerErrorKind.RequestRejected);
            if (string.IsNullOrWhiteSpace(text) || text.Length > 16384) throw Failure(PlannerErrorKind.InvalidResponse);
            using var goal = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            return GoalContract.ParseArguments(goal.RootElement, observationId,
                new PlannerMetrics(Stopwatch.GetElapsedTime(started), 1, inputTokens, outputTokens, null));
        }
    }

    private static async Task DrainAsync(TextReader reader, CancellationToken token)
    {
        var chunk = new char[4096];
        while (await reader.ReadAsync(chunk, token) > 0) { }
    }

    internal static PlannerException Failure(PlannerErrorKind kind) => new(kind,
        $"Codex ChatGPT inference failed ({kind}); check the local Codex login, model access and limits. No fallback or retry was performed.", 1);
}
