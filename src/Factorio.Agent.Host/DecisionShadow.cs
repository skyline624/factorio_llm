using System.Globalization;
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
    // The d3 Ollama packages have a 4,096-token context and the options plus questions already cost 1,000 to 2,500 tokens,
    // so the serialized state is held to about 3,000 characters.
    private const int MaxStateCharacters = 3000;
    private static readonly string[] DropOrder = ["researched", "carried", "rawSupplyPerMinute", "assemblerCells", "unlockResearch", "missingEquipment"];

    public static ShadowQuestion Build(string factsJson, string? previousResult, GoalProposal goal)
    {
        using var document = JsonDocument.Parse(factsJson);
        var facts = document.RootElement;
        var recipes = Strings(facts, "availableSolidRecipes").ToHashSet(StringComparer.Ordinal);
        var siloPath = SiloPath(facts);
        int exposed = Int(facts, "knownDefenses", "exposedIndustrialAnchors"), enemies = Int(facts, "visibleEnemyCount");
        int attacks = Array(facts, "defenseSituation", "recentAttacks").Count();
        var cells = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cell in Array(facts, "automatedFactory", "assemblerCells"))
            if (Text(cell, "recipe") is { } recipe) cells[recipe] = (int)(Num(cell, "cells") ?? 0);
        var raw = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var supply in Array(facts, "automatedFactory", "rawSupply"))
            if (Text(supply, "item") is { } item) raw[item] = Num(supply, "perMinute") ?? 0;
        // A proposal aimed at an already automated item keeps its option, so the shadow can still compare against it.
        string? proposedTarget = goal.Category == GoalCategory.Production && goal.Unit == GoalUnit.ItemsPerMinute ? goal.Target : null;

        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (Bool(facts, "automatedFactory", "available"))
        {
            foreach (var pack in recipes.Where(r => r.EndsWith("-science-pack", StringComparison.Ordinal) && (!cells.ContainsKey(r) || r == proposedTarget))
                         .Order(StringComparer.Ordinal).Take(3))
                options[$"automate:{pack}"] = cells.TryGetValue(pack, out int existing)
                    ? $"Increase the existing {pack} automation ({existing} cells)"
                    : $"Build persistent assembler cells that produce {pack} continuously";
            var resources = Strings(facts, "knownResources").ToHashSet(StringComparer.Ordinal);
            var basics = new[] { ("iron-plate", "iron-ore"), ("copper-plate", "copper-ore"), ("coal", "coal"), ("stone", "stone") }
                .Where(c => recipes.Contains(c.Item1) || resources.Contains(c.Item2)).Select(c => c.Item1);
            var rawItems = new[] { proposedTarget }.OfType<string>().Concat(raw.Keys).Concat(basics)
                .Where(i => !i.EndsWith("-science-pack", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).Take(4);
            foreach (var item in rawItems)
                options[$"automate:{item}"] = $"Raise persistent {item} supply (now {raw.GetValueOrDefault(item).ToString("0.##", CultureInfo.InvariantCulture)}/min)";
        }
        foreach (var turret in Strings(facts, "nativeDefenseItems").Where(recipes.Contains).Take(1))
            options[$"defense:{turret}"] = $"Install and supply {turret} ({enemies} visible enemies, {attacks} recent attacks, {exposed} uncovered industrial places)";
        var research = Array(facts, "availableResearch").Where(t => Text(t, "name") is not null)
            .Select(t => (Name: Text(t, "name")!, Count: (long)(Num(t, "count") ?? 0),
                Packs: Array(t, "ingredients").Select(i => Text(i, "name")).OfType<string>().ToArray(),
                Unlocks: Array(t, "unlocks").Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToArray())).ToArray();
        foreach (var tech in research.OrderByDescending(t => siloPath.Contains(t.Name)).ThenBy(t => t.Count).ThenBy(t => t.Name, StringComparer.Ordinal)
                     .Take(MaxOptions - options.Count - 1))
        {
            string cost = $"{tech.Count} units of {string.Join(", ", tech.Packs)}";
            string unlocks = string.Join(", ", tech.Unlocks.Take(5)) + (tech.Unlocks.Length > 5 ? ", ..." : "");
            options[$"research:{tech.Name}"] = $"Research {tech.Name}: " + (tech.Unlocks.Length == 0 ? cost : $"unlocks {unlocks} ({cost})")
                + (siloPath.Contains(tech.Name) ? "; on the rocket silo path" : "");
        }
        options["none"] = "None: every listed option is impossible or harmful right now";

        string? proposed = goal.Category switch
        {
            GoalCategory.Research => $"research:{goal.Target}",
            GoalCategory.Defense => $"defense:{goal.Target}",
            GoalCategory.Production when proposedTarget is not null => $"automate:{proposedTarget}",
            _ => null
        };
        if (proposed is not null && !options.ContainsKey(proposed)) proposed = null;

        var agent = facts.TryGetProperty("agent", out var agentNode) && agentNode.ValueKind == JsonValueKind.Object ? agentNode : default;
        var state = new Dictionary<string, object?>
        {
            ["tick"] = Long(facts, "observedTick"),
            ["health"] = Num(agent, "health") ?? 0,
            ["visibleEnemies"] = enemies,
            ["recentAttacks"] = attacks,
            ["laboratories"] = (int)(Num(facts, "automatedFactory", "laboratories") ?? 0),
            ["missingEquipment"] = Strings(facts, "automatedFactory", "prerequisites", "missingEquipment").Take(8).ToArray(),
            ["unlockResearch"] = Strings(facts, "automatedFactory", "prerequisites", "unlockResearch").Take(8).ToArray(),
            ["assemblerCells"] = cells.Take(12).ToDictionary(p => p.Key, p => p.Value),
            ["rawSupplyPerMinute"] = raw.Take(6).ToDictionary(p => p.Key, p => Math.Round(p.Value, 1)),
            ["previous"] = Previous(previousResult),
            ["carried"] = agent.ValueKind == JsonValueKind.Object && agent.TryGetProperty("inventory", out var inventory) && inventory.ValueKind == JsonValueKind.Object
                ? inventory.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out _))
                    .OrderByDescending(p => p.Value.GetInt64()).Take(12).ToDictionary(p => p.Name, p => p.Value.GetInt64())
                : [],
            ["researched"] = Strings(facts, "researchedTechnologies").TakeLast(20).ToArray()
        };
        string serialized = JsonSerializer.Serialize(state);
        foreach (string key in DropOrder)
        {
            if (serialized.Length <= MaxStateCharacters) break;
            state.Remove(key);
            serialized = JsonSerializer.Serialize(state);
        }
        var questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
        {
            ["next_goal"] = new("choice", "An autonomous agent plays Factorio toward launching a rocket while enemies attack. " +
                "Choose the single best next goal. Priorities in order: (1) survive and answer real threats, meaning visible enemies or recent attacks; " +
                "uncovered industry alone is not a threat; (2) remove blockers to progress such as missing equipment, power or research prerequisites; " +
                "(3) research on the rocket silo path; (4) persistent automation of what is not yet automated. " +
                "Choose none only if every option is impossible or harmful right now.", options),
            ["proposal_sound"] = new("noul", $"The planner proposes: {Trim(goal.Description, 300)} (category {goal.Category}, target {goal.Target}). " +
                "Is this a sound next goal for this state: it answers a real threat if one exists (visible enemies or recent attacks; " +
                "uncovered industry alone is not a threat), is useful toward the rocket, and is not a repeat of a recent failure?")
        };
        return new(serialized, questions, proposed);
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

    /// <summary>Compact view of the last outcome. The journaled result is free-form (success, failure or reconciliation), so every field is optional.</summary>
    private static Dictionary<string, object?>? Previous(string? previousResult)
    {
        if (previousResult is null) return null;
        try
        {
            using var document = JsonDocument.Parse(previousResult);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var summary = new Dictionary<string, object?>();
            if (Child(root, "goal") is { ValueKind: JsonValueKind.Object } goal)
                summary["goal"] = new { category = Child(goal, "category")?.ToString(), target = Child(goal, "target")?.ToString(), quantity = Child(goal, "quantity")?.ToString() };
            if (Child(root, "unsupportedReason") is { ValueKind: JsonValueKind.String } reason) summary["unsupportedReason"] = Trim(reason.GetString()!, 200);
            if (Child(root, "executionFailure") is { ValueKind: JsonValueKind.String } failure) summary["executionFailure"] = Trim(failure.GetString()!, 120);
            if (Child(root, "consecutiveFailures") is { ValueKind: JsonValueKind.Number } streak && streak.TryGetInt32(out int n)) summary["consecutiveFailures"] = n;
            if (Child(root, "logistics") is { ValueKind: JsonValueKind.Object } logistics)
            {
                if (Child(logistics, "shortfall") is { ValueKind: JsonValueKind.Object } shortfall)
                    summary["shortfall"] = shortfall.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number).Take(8)
                        .ToDictionary(p => p.Name, p => p.Value.GetDouble());
                if (Child(logistics, "powerStarved") is { ValueKind: JsonValueKind.True or JsonValueKind.False } starved)
                    summary["powerStarved"] = starved.GetBoolean();
            }
            return summary;
        }
        catch (JsonException) { return null; }
    }

    private static HashSet<string> SiloPath(JsonElement facts)
    {
        var path = new HashSet<string>(StringComparer.Ordinal) { "rocket-silo" };
        if (!facts.TryGetProperty("rocketResearchDependencies", out var dependencies) || !dependencies.TryGetProperty("prerequisites", out var edges)
            || edges.ValueKind != JsonValueKind.Object) return path;
        var graph = edges.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array)
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray());
        var stack = new Stack<string>(path);
        while (stack.TryPop(out var node))
            foreach (var parent in graph.GetValueOrDefault(node, []))
                if (path.Add(parent)) stack.Push(parent);
        return path;
    }

    // Journals mix camelCase and PascalCase depending on the writer.
    private static JsonElement? Child(JsonElement node, string name)
    {
        foreach (var property in node.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static IEnumerable<JsonElement> Array(JsonElement node, params string[] path)
    {
        foreach (string step in path)
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out node)) return [];
        return node.ValueKind == JsonValueKind.Array ? node.EnumerateArray() : [];
    }
    private static IEnumerable<string> Strings(JsonElement facts, params string[] path) =>
        Array(facts, path).Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!);
    private static string? Text(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static double? Num(JsonElement node, params string[] path)
    {
        foreach (string step in path)
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(step, out node)) return null;
        return node.ValueKind == JsonValueKind.Number && node.TryGetDouble(out double n) ? n : null;
    }
    private static bool Bool(JsonElement facts, string parent, string name) => facts.TryGetProperty(parent, out var node)
        && node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static int Int(JsonElement facts, string parent, string name) => facts.TryGetProperty(parent, out var node)
        && node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.TryGetInt32(out int n) ? n : 0;
    private static int Int(JsonElement facts, string name) => facts.TryGetProperty(name, out var value) && value.TryGetInt32(out int n) ? n : 0;
    private static long Long(JsonElement facts, string name) => facts.TryGetProperty(name, out var value) && value.TryGetInt64(out long n) ? n : 0;
    private static string Trim(string text, int length) => text.Length <= length ? text : text[..length];
}
