using System.Text;
using System.Text.Json;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Codex;

/// <summary>Single in-flight request, bounded messages, and no dispatch of server tool/approval requests.</summary>
internal sealed class CodexRpc(TextReader reader, TextWriter writer)
{
    private int nextId;
    private readonly Queue<JsonElement> notifications = new();
    private int receivedCharacters;

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token)
    {
        int id = ++nextId;
        await WriteAsync(new { id, method, @params = parameters }, token);
        while (true)
        {
            var message = await ReadAsync(token);
            if (!message.TryGetProperty("id", out var responseId))
            {
                if (notifications.Count >= 1024) throw CodexStrategicPlanner.Failure(PlannerErrorKind.InvalidResponse);
                notifications.Enqueue(message);
                continue;
            }
            if (message.TryGetProperty("method", out _)) throw CodexStrategicPlanner.Failure(PlannerErrorKind.RequestRejected);
            if (message.TryGetProperty("error", out var error))
            {
                string? detail = error.TryGetProperty("message", out var errorMessage) ? errorMessage.GetString() : null;
                string hint = string.Join(", ", new[] { "environments", "sandbox", "initialize", "text_elements", "model", "config", "project_doc_max_bytes", "approval", "clientInfo", "baseInstructions", "dynamicTools", "not found", "disabled" }
                    .Where(field => detail?.Contains(field, StringComparison.OrdinalIgnoreCase) == true));
                int? code = error.TryGetProperty("code", out var errorCode) && errorCode.TryGetInt32(out var value) ? value : null;
                throw new PlannerException(PlannerErrorKind.RequestRejected,
                    $"Codex rejected {method} (protocol code {code}; related fields: {hint}). Provider content is not logged; no fallback or retry was performed.", 1);
            }
            if (responseId.GetInt32() != id)
                throw CodexStrategicPlanner.Failure(PlannerErrorKind.RequestRejected);
            return message.GetProperty("result");
        }
    }

    public Task NotifyAsync(string method, object parameters, CancellationToken token) =>
        WriteAsync(new { method, @params = parameters }, token);

    public async Task<JsonElement> NextNotificationAsync(CancellationToken token)
    {
        var message = notifications.Count > 0 ? notifications.Dequeue() : await ReadAsync(token);
        if (message.TryGetProperty("id", out _)) throw CodexStrategicPlanner.Failure(PlannerErrorKind.RequestRejected);
        return message;
    }

    private async Task WriteAsync(object message, CancellationToken token)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
        await writer.FlushAsync(token);
    }

    private async Task<JsonElement> ReadAsync(CancellationToken token)
    {
        var line = new StringBuilder();
        var character = new char[1];
        while (true)
        {
            if (await reader.ReadAsync(character, token) == 0) throw new IOException("Codex closed its protocol stream.");
            if (++receivedCharacters > 2 * 1024 * 1024 || line.Length >= 262144)
                throw CodexStrategicPlanner.Failure(PlannerErrorKind.InvalidResponse);
            if (character[0] == '\n') break;
            line.Append(character[0]);
        }
        using var document = JsonDocument.Parse(line.ToString(), new JsonDocumentOptions { MaxDepth = 32, AllowDuplicateProperties = false });
        return document.RootElement.Clone();
    }
}
