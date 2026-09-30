using System.Text.Json;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Host;

public sealed record ShadowQuestion(string State, IReadOnlyDictionary<string, DecisionQuestion> Questions, string? ProposedOption);
public sealed record ShadowVerdict(string? ProposedOption, string? ModelChoice, bool? Agrees, double? ProposedProbability,
    IReadOnlyDictionary<string, double> Top, string? ProposalSound, double? SoundProbability, long InputTokens, double Milliseconds,
    string? Error = null);

/// <summary>
/// Shadow evaluation of the strategic proposal by a typed decision model. C# computes every option from the recorded
/// facts; the model only ranks them and its answer never reaches an executor.
/// </summary>
public static class DecisionShadow
{
    private const int MaxOptions = 26;

    public static ShadowQuestion Build(string factsJson, string? previousResult, GoalProposal goal)
    {
        using var document = JsonDocument.Parse(factsJson);
        var facts = document.RootElement;
        var recipes = Strings(facts, "availableSolidRecipes").ToHashSet(StringComparer.Ordinal);
        var siloPath = SiloPath(facts);
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (Bool(facts, "automatedFactory", "available"))
            foreach (var pack in recipes.Where(r => r.EndsWith("-science-pack", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Take(3))
                options[$"automate:{pack}"] = $"Build persistent assembler cells that produce {pack} continuously";
        int exposed = Int(facts, "knownDefenses", "exposedIndustrialAnchors"), enemies = Int(facts, "visibleEnemyCount");
        foreach (var turret in Strings(facts, "nativeDefenseItems").Where(recipes.Contains).Take(1))
            options[$"defense:{turret}"] = $"Install and supply {turret} near exposed industry ({exposed} exposed places, {enemies} visible enemies)";
        var research = facts.TryGetProperty("availableResearch", out var available) && available.ValueKind == JsonValueKind.Array
            ? available.EnumerateArray().Select(t => (Name: t.GetProperty("name").GetString()!, Count: t.GetProperty("count").GetInt64(),
                Packs: t.GetProperty("ingredients").EnumerateArray().Select(i => i.GetProperty("name").GetString()!).ToArray())).ToArray()
            : [];
        foreach (var tech in research.OrderByDescending(t => siloPath.Contains(t.Name)).ThenBy(t => t.Count).ThenBy(t => t.Name, StringComparer.Ordinal)
                     .Take(MaxOptions - options.Count - 1))
            options[$"research:{tech.Name}"] = $"Research {tech.Name}: {tech.Count} units of {string.Join(", ", tech.Packs)}"
                + (siloPath.Contains(tech.Name) ? "; on the rocket silo path" : "");
        options["none"] = "None of these goals fits the current state";

        string? proposed = goal.Category switch
        {
            GoalCategory.Research => $"research:{goal.Target}",
            GoalCategory.Defense => $"defense:{goal.Target}",
            GoalCategory.Production when goal.Unit == GoalUnit.ItemsPerMinute => $"automate:{goal.Target}",
            _ => null
        };
        if (proposed is not null && !options.ContainsKey(proposed)) proposed = null;

        string state = JsonSerializer.Serialize(new
        {
            tick = Long(facts, "observedTick"),
            health = facts.TryGetProperty("agent", out var agent) ? agent.GetProperty("health").GetDouble() : 0,
            carried = agent.ValueKind == JsonValueKind.Object && agent.TryGetProperty("inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Object
                ? inventory.EnumerateObject().OrderByDescending(p => p.Value.GetInt64()).Take(12).ToDictionary(p => p.Name, p => p.Value.GetInt64())
                : [],
            visibleEnemies = enemies,
            exposedIndustrialAnchors = exposed,
            researched = Strings(facts, "researchedTechnologies").TakeLast(20).ToArray(),
            factory = facts.TryGetProperty("automatedFactory", out var factory) ? factory.Clone() : default(JsonElement?),
            previousOutcome = previousResult is { Length: <= 1500 } ? previousResult : previousResult?[..1500]
        });
        var questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
        {
            ["next_goal"] = new("choice", "An autonomous agent plays Factorio toward launching a rocket while enemies attack. " +
                "Choose the single best next goal: stay safe first (defend when enemies are visible or industry is exposed), " +
                "then research on the rocket silo path, then persistent automation. Choose none if nothing fits.", options),
            ["proposal_sound"] = new("noul", $"The planner proposes: {Trim(goal.Description, 300)} (category {goal.Category}, target {goal.Target}). " +
                "Is this a sound next goal for this state: safe, useful toward the rocket, and not a repeat of a recent failure?")
        };
        return new(state, questions, proposed);
    }

    public static async Task<ShadowVerdict> AskAsync(DecisionModelClient client, ShadowQuestion question, CancellationToken token)
    {
        // Recovery and other narrow contexts offer no alternative to rank.
        if (question.Questions["next_goal"].Criteria!.Count < 2)
            return new(question.ProposedOption, null, null, null, new Dictionary<string, double>(), null, null, 0, 0,
                "The recorded context offers fewer than two feasible options.");
        try
        {
            var result = await client.DecideAsync(question.State, question.Questions, token);
            var next = result.Answers["next_goal"];
            var sound = result.Answers["proposal_sound"];
            return new(question.ProposedOption, next.Choice, question.ProposedOption is null ? null : next.Choice == question.ProposedOption,
                question.ProposedOption is null ? null : next.Probabilities[question.ProposedOption],
                next.Probabilities.OrderByDescending(p => p.Value).Take(5).ToDictionary(p => p.Key, p => Math.Round(p.Value, 4)),
                sound.Choice, sound.Probabilities.GetValueOrDefault("true"), result.InputTokens, result.Elapsed.TotalMilliseconds);
        }
        catch (DecisionModelException error)
        {
            return new(question.ProposedOption, null, null, null, new Dictionary<string, double>(), null, null, 0, 0, error.Message);
        }
    }

    private static HashSet<string> SiloPath(JsonElement facts)
    {
        var path = new HashSet<string>(StringComparer.Ordinal) { "rocket-silo" };
        if (!facts.TryGetProperty("rocketResearchDependencies", out var dependencies) || !dependencies.TryGetProperty("prerequisites", out var edges)
            || edges.ValueKind != JsonValueKind.Object) return path;
        var graph = edges.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(v => v.GetString()!).ToArray());
        var stack = new Stack<string>(path);
        while (stack.TryPop(out var node))
            foreach (var parent in graph.GetValueOrDefault(node, []))
                if (path.Add(parent)) stack.Push(parent);
        return path;
    }

    private static IEnumerable<string> Strings(JsonElement facts, string name) =>
        facts.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!) : [];
    private static bool Bool(JsonElement facts, string parent, string name) => facts.TryGetProperty(parent, out var node)
        && node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static int Int(JsonElement facts, string parent, string name) => facts.TryGetProperty(parent, out var node)
        && node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.TryGetInt32(out int n) ? n : 0;
    private static int Int(JsonElement facts, string name) => facts.TryGetProperty(name, out var value) && value.TryGetInt32(out int n) ? n : 0;
    private static long Long(JsonElement facts, string name) => facts.TryGetProperty(name, out var value) && value.TryGetInt64(out long n) ? n : 0;
    private static string Trim(string text, int length) => text.Length <= length ? text : text[..length];
}
