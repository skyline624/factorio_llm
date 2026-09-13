using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record StrategicReconciliationResult(string ReportPath, int Operations, long Tick);

/// <summary>Read-only native reconciliation under the caller's actor lease; never resubmits an operation.</summary>
public sealed class StrategicReconciliationController(IGameClient game, string memoryPath)
{
    public async Task<StrategicReconciliationResult> ReconcileAsync(string journalPath, CancellationToken token = default)
    {
        string original = await File.ReadAllTextAsync(memoryPath, token);
        var memory = JsonSerializer.Deserialize<StrategicMemory>(original, Protocol.Json)
            ?? throw new InvalidDataException("Missing strategic memory.");
        if (!memory.Pending || memory.Version != 1 || memory.Tick < 0 || memory.Scope is null)
            throw new InvalidDataException("A valid pending strategic attempt is required.");
        journalPath = Path.GetFullPath(journalPath);
        if (memory.PendingJournal is { } expected && Path.GetFullPath(expected) != journalPath)
            throw new InvalidDataException("This journal does not belong to the pending strategic attempt.");
        string contents = File.Exists(journalPath)
            ? new FileInfo(journalPath).Length <= 64 * 1024 * 1024 ? await File.ReadAllTextAsync(journalPath, token)
                : throw new InvalidDataException("Strategic journal exceeds the 64 MiB reconciliation budget.")
            : memory.PendingJournal is not null ? "" : throw new FileNotFoundException("The legacy attempt requires its existing journal.");
        var audit = ReadJournal(contents, memory);
        GameResponse before = await ObserveAsync(memory, token);
        var beforeScope = before.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        var operations = new OperationClient(game);
        var queried = new List<OperationReceipt>();
        var unresolved = audit.Submissions.Values.Where(s => !audit.Receipts.TryGetValue(s.OperationId, out var r) || !r.IsTerminal)
            .Select(s => s.OperationId).ToHashSet(StringComparer.Ordinal);
        if (before.Data.TryGetProperty("operation", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            string id = last.GetProperty("operationId").GetString()!;
            if (audit.Submissions.ContainsKey(id)) unresolved.Add(id);
            else if (audit.Submissions.Count > 0 || last.GetProperty("updatedTick").GetInt64() >= memory.Tick)
                throw new InvalidDataException("The last native operation is outside the pending journal.");
        }
        else if (audit.Submissions.Count > 0) throw new InvalidDataException("The native last operation is missing.");
        foreach (string id in unresolved)
        {
            OperationReceipt receipt = await operations.QueryAsync(id, token);
            ValidateReceipt(audit.Submissions[id], receipt, memory.Tick);
            if (!receipt.IsTerminal) throw new InvalidDataException("A pending native operation is still active.");
            if (audit.Receipts.TryGetValue(id, out var recorded) && recorded.IsTerminal && !Equivalent(recorded, receipt))
                throw new InvalidDataException("The native receipt conflicts with the durable terminal receipt.");
            audit.Receipts[id] = receipt;
            queried.Add(receipt);
        }
        if (audit.Submissions.Keys.Any(id => !audit.Receipts.TryGetValue(id, out var r) || !r.IsTerminal))
            throw new InvalidDataException("Not every submitted operation has a terminal outcome.");
        GameResponse after = await ObserveAsync(memory, token);
        var scope = after.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        if (scope != beforeScope || after.Tick < before.Tick
            || audit.Receipts.Values.Any(r => r.UpdatedTick > after.Tick)
            || !SameLastOperation(before, after))
            throw new InvalidDataException("Native state changed during reconciliation.");
        var failures = audit.Receipts.Values.Where(r => r.Status != "completed").ToArray();
        string feedback = JsonSerializer.Serialize(new
        {
            outcome = "interrupted-goal-reconciled", observedTick = after.Tick, goal = audit.Goal,
            submittedOperations = audit.Submissions.Count, nonCompletedOperations = failures.Length,
            executionFailure = audit.FailureCode,
            recentOutcomes = failures.TakeLast(3).Select(r => new { r.Kind, r.Status, error = r.Error?.Code, r.UpdatedTick }),
            interpretation = "The previous goal is not certified complete. Partial products remain in the native world. Observe current stocks and research before choosing the next goal; never replay prior mutations."
        }, Protocol.Json);
        if (feedback.Length > 4000) throw new InvalidDataException("Reconciliation feedback exceeds the strategic context budget.");
        string reportPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(memoryPath))!, $"strategic-reconciliation-{Guid.NewGuid():N}.json");
        await LocalJson.WriteAsync(reportPath, new
        {
            kind = "read-only-strategic-reconciliation", previousMemory = memory, journalPath,
            journalSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contents))),
            operations = audit.Submissions.Count, queriedReceipts = queried, observation = after.Data,
            executionDiagnostic = audit.Diagnostic, feedback
        }, token);
        if (await File.ReadAllTextAsync(memoryPath, token) != original
            || (File.Exists(journalPath) ? await File.ReadAllTextAsync(journalPath, token) : "") != contents)
            throw new InvalidDataException("Strategic memory or journal changed during reconciliation.");
        await LocalJson.WriteAsync(memoryPath, new StrategicMemory(1, scope, after.Tick, false, feedback), token);
        return new(reportPath, audit.Submissions.Count, after.Tick);
    }

    private async Task<GameResponse> ObserveAsync(StrategicMemory memory, CancellationToken token)
    {
        var response = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
        if (!response.Ok) throw new GameRpcException(response.Error!);
        var scope = response.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        var actor = response.Data.GetProperty("agent");
        if (scope.WorldId != memory.Scope.WorldId || scope.ActorId != memory.Scope.ActorId
            || scope.Incarnation != memory.Scope.Incarnation || scope.Generation < memory.Scope.Generation
            || response.Tick < memory.Tick || !actor.GetProperty("alive").GetBoolean()
            || actor.GetProperty("controlMode").GetString() != "ai" || actor.GetProperty("stopUnconfirmed").GetBoolean()
            || actor.GetProperty("walking").GetBoolean() || actor.GetProperty("mining").GetBoolean()
            || actor.GetProperty("shooting").GetBoolean() || actor.GetProperty("craftingQueueSize").GetInt32() != 0)
            throw new InvalidDataException("Reconciliation requires the same living actor and a confirmed idle native state.");
        if (response.Data.TryGetProperty("operation", out var node) && node.ValueKind == JsonValueKind.Object)
        {
            var receipt = OperationReceipt.Parse(node, node.GetProperty("operationId").GetString()!);
            if (!receipt.IsTerminal || receipt.Error?.Code == "stop_unconfirmed")
                throw new InvalidDataException("The current native operation is active or its stop is unconfirmed.");
        }
        return response;
    }

    private static JournalAudit ReadJournal(string contents, StrategicMemory memory)
    {
        var audit = new JournalAudit();
        bool selected = false;
        foreach (string line in contents.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            var row = document.RootElement;
            string? type = row.GetProperty("type").GetString();
            var data = row.GetProperty("data");
            if (type == "strategic-context")
            {
                using var facts = JsonDocument.Parse(data.GetProperty("facts").GetString()!);
                if (facts.RootElement.GetProperty("observedTick").GetInt64() < memory.Tick) continue;
                if (selected) throw new InvalidDataException("More than one unresolved strategic context exists in this journal.");
                selected = true;
            }
            if (!selected) continue;
            if (type == "strategic-execution-error")
            {
                string code = data.GetProperty("failureCode").GetString() ?? throw new InvalidDataException("Missing execution failure category.");
                if (code.Length is < 1 or > 128 || code.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                    throw new InvalidDataException("Invalid execution failure category.");
                audit.FailureCode = code;
                audit.Diagnostic = data.Clone();
            }
            if (type == "strategic-goal") audit.Goal = Protocol.ToElement(new
            {
                category = data.GetProperty("category"), target = data.GetProperty("target"),
                quantity = data.GetProperty("quantity"), unit = data.GetProperty("unit")
            });
            if (type == "submission")
            {
                var submission = data.Deserialize<OperationSubmission>(Protocol.Json)!;
                if (submission.Scope != memory.Scope || submission.DeadlineTick <= memory.Tick
                    || !audit.Submissions.TryAdd(submission.OperationId, submission))
                    throw new InvalidDataException("Journal submission identity, scope or time is inconsistent.");
            }
            if (type is "receipt" or "cancel-receipt" or "final-receipt" or "reconciled")
            {
                string id = data.GetProperty("operationId").GetString()!;
                if (!audit.Submissions.TryGetValue(id, out var submission))
                    throw new InvalidDataException("Receipt has no matching pending submission.");
                var receipt = OperationReceipt.Parse(data, id);
                if (data.TryGetProperty("evidence", out var nativeEvidence))
                {
                    var native = OperationReceipt.Parse(nativeEvidence, id);
                    if (!Equivalent(receipt, native)) throw new InvalidDataException("Journal receipt conflicts with its native evidence.");
                    receipt = native;
                }
                ValidateReceipt(submission, receipt, memory.Tick);
                if (audit.Receipts.TryGetValue(id, out var prior)
                    && (receipt.UpdatedTick < prior.UpdatedTick || prior.IsTerminal && !Equivalent(prior, receipt)))
                    throw new InvalidDataException("Journal receipts conflict or regress.");
                audit.Receipts[id] = receipt;
            }
        }
        if (!selected && (contents.Length > 0 || memory.PendingJournal is null))
            throw new InvalidDataException("The journal does not establish the pending strategic context.");
        return audit;
    }

    private static void ValidateReceipt(OperationSubmission submission, OperationReceipt receipt, long startTick)
    {
        if (receipt.Kind != submission.Kind || receipt.AcceptedTick is not { } accepted || accepted < startTick
            || receipt.UpdatedTick < accepted || receipt.Error?.Code == "stop_unconfirmed"
            || receipt.Evidence.TryGetProperty("measurementError", out _) || receipt.Evidence.TryGetProperty("stopError", out _))
            throw new InvalidDataException("Operation receipt lacks consistent identity, time or confirmed effects.");
    }

    private static bool Equivalent(OperationReceipt a, OperationReceipt b) => a.OperationId == b.OperationId
        && a.Kind == b.Kind && a.Status == b.Status && a.AcceptedTick == b.AcceptedTick && a.UpdatedTick == b.UpdatedTick
        && a.Error == b.Error && JsonElement.DeepEquals(a.Effects, b.Effects);
    private static bool SameLastOperation(GameResponse a, GameResponse b)
    {
        bool first = a.Data.TryGetProperty("operation", out var x), second = b.Data.TryGetProperty("operation", out var y);
        return first == second && (!first || JsonElement.DeepEquals(x, y));
    }
    private sealed class JournalAudit
    {
        public Dictionary<string, OperationSubmission> Submissions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, OperationReceipt> Receipts { get; } = new(StringComparer.Ordinal);
        public JsonElement? Goal { get; set; }
        public string? FailureCode { get; set; }
        public JsonElement? Diagnostic { get; set; }
    }
}
