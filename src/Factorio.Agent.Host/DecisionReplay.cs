using System.Text.Json;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Host;

/// <summary>
/// Offline shadow evaluation: replays journaled strategic contexts and GLM proposals through the decision model.
/// It reads private journals only and never connects to the game.
/// </summary>
public static class DecisionReplay
{
    public static async Task<string> RunAsync(string directory, DecisionModelClient client, int limit, CancellationToken token)
    {
        var rows = new List<object>();
        var verdicts = new List<ShadowVerdict>();
        foreach (string path in Directory.GetFiles(directory, "strategic-production-*.jsonl").OrderBy(File.GetLastWriteTimeUtc))
        {
            string? facts = null, previous = null;
            foreach (string line in await File.ReadAllLinesAsync(path, token))
            {
                if (verdicts.Count >= limit) break;
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "strategic-context")
                {
                    facts = data.GetProperty("facts").GetString();
                    previous = data.TryGetProperty("previousResult", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                }
                // Recovery goals are recorded with textual categories; they are not model proposals.
                else if (type == "strategic-goal" && facts is not null && data.GetProperty("category").ValueKind == JsonValueKind.Number)
                {
                    var goal = Proposal(data);
                    var verdict = await DecisionShadow.AskAsync(client, DecisionShadow.Build(facts, previous, goal), token);
                    verdicts.Add(verdict);
                    rows.Add(new { journal = Path.GetFileName(path), goal = new { goal.Category, goal.Target, goal.Quantity, goal.Unit }, verdict });
                    facts = null;
                }
            }
        }
        var answered = verdicts.Where(v => v.Error is null).ToArray();
        var comparable = answered.Where(v => v.Agrees is not null).ToArray();
        var latencies = answered.Select(v => v.Milliseconds).Order().ToArray();
        var summary = new
        {
            decisions = verdicts.Count,
            answered = answered.Length,
            errors = verdicts.Count - answered.Length,
            glmProposalOffered = comparable.Length,
            agreements = comparable.Count(v => v.Agrees == true),
            agreementRate = comparable.Length == 0 ? (double?)null : Math.Round(comparable.Count(v => v.Agrees == true) / (double)comparable.Length, 3),
            meanProbabilityOfGlmChoice = comparable.Length == 0 ? (double?)null : Math.Round(comparable.Average(v => v.ProposedProbability!.Value), 3),
            judgedSound = answered.Count(v => v.ProposalSound == "true"),
            modelChoices = answered.GroupBy(v => v.ModelChoice!).OrderByDescending(g => g.Count()).ToDictionary(g => g.Key, g => g.Count()),
            latencyP50Ms = latencies.Length == 0 ? 0 : latencies[latencies.Length / 2],
            latencyP95Ms = latencies.Length == 0 ? 0 : latencies[(int)Math.Min(latencies.Length - 1, Math.Ceiling(latencies.Length * 0.95) - 1)],
            interpretation = "Shadow only: the decision model never selected or executed any goal. Agreement with GLM is not correctness."
        };
        string report = Path.Combine(directory, $"decision-replay-{Guid.NewGuid():N}.json");
        await LocalJson.WriteAsync(report, new { kind = "offline-decision-shadow-replay", summary, rows }, token);
        return report;
    }

    /// <summary>Journaled proposals keep numeric enums; only the fields the shadow question uses are restored.</summary>
    private static GoalProposal Proposal(JsonElement data) => new(
        data.GetProperty("observationId").GetString() ?? "", data.GetProperty("description").GetString() ?? "",
        (GoalCategory)data.GetProperty("category").GetInt32(), data.GetProperty("target").GetString() ?? "",
        data.GetProperty("quantity").GetDecimal(), (GoalUnit)data.GetProperty("unit").GetInt32(),
        (GoalPriority)data.GetProperty("priority").GetInt32(), new(TimeSpan.Zero, 1, null, null, null));
}
