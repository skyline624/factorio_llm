using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record ResearchPreparationResult(string Target, string Status, TechnologyStep Next, long StartTick, long EndTick,
    IReadOnlyList<string> VerifiedTriggers);

/// <summary>Executes supported craft prerequisites and requires the native technology flag as proof.</summary>
public sealed class ResearchPrerequisiteController(IGameClient game, IStockGoalExecutor production, IControllerJournal journal)
{
    public async Task<ResearchPreparationResult> RunAsync(string target, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        var technologies = new TechnologyClient(game);
        var planner = new TechnologyPlanner();
        TechnologyObservation initial = await technologies.ReadDependenciesAsync(target, token);
        var verified = new List<string>();
        for (int attempt = 0; attempt < 32; attempt++)
        {
            TechnologyObservation current = attempt == 0 ? initial : await technologies.ReadDependenciesAsync(target, token);
            RequireScope(current.Scope);
            TechnologyStep next = planner.Next(target, current.Technologies);
            await journal.AppendAsync("research-preparation-step", new { target, current, next }, token);
            if (next.Kind is "completed" or "research")
            {
                var result = new ResearchPreparationResult(target, next.Kind == "completed" ? "researched" : "ready-for-lab",
                    next, initial.StartTick, current.EndTick, verified.ToArray());
                await journal.AppendAsync("research-preparation-result", result, token);
                return result;
            }
            if (next.Kind != "craft-trigger") throw new InvalidOperationException(next.Reason ?? "Unsupported research prerequisite.");
            GameResponse state = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
            if (!state.Ok) throw new GameRpcException(state.Error!);
            RequireScope(state.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!);
            var inventory = state.Data.GetProperty("agent").GetProperty("inventory").Deserialize<Dictionary<string, long>>(Protocol.Json)
                ?? throw new InvalidDataException("Missing actor inventory for research prerequisite.");
            long stock = inventory.GetValueOrDefault(next.Item!);
            long requested = checked(stock + next.Count);
            if (stock < 0 || requested is < 1 or > 1000)
                throw new InvalidOperationException("The craft trigger exceeds the supported carried-stock budget.");
            await production.RunAsync(next.Item!, (int)requested, token);
            TechnologyObservation proof = await technologies.ReadDependenciesAsync(next.Technology, token);
            RequireScope(proof.Scope);
            // Trigger technologies are evaluated asynchronously by the engine.
            // Re-observe only; never manufacture a second item on an unconfirmed result.
            for (int poll = 0; poll < 30 && !proof.Technologies[next.Technology].Researched; poll++)
            {
                await Task.Delay(100, token);
                proof = await technologies.ReadDependenciesAsync(next.Technology, token);
                RequireScope(proof.Scope);
            }
            if (!proof.Technologies[next.Technology].Researched)
                throw new InvalidOperationException("Production finished but the native research trigger remains unmet. Reconcile instead of repeating production.");
            verified.Add(next.Technology);
            await journal.AppendAsync("research-trigger-verified", new { next, proof }, token);
        }
        throw new InvalidOperationException("Research preparation exhausted its dependency-step budget.");

        void RequireScope(ActorScope scope)
        {
            if (scope != initial.Scope) throw new InvalidDataException("Actor identity changed during research preparation; reconcile partial effects.");
        }
    }
}
