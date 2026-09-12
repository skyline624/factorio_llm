using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record PipeConnectionResult(string SourceId, string TargetId, string Fluid, long StartTick, long EndTick,
    IReadOnlyList<string> BuiltPipeIds, PipeRoutePlan Plan);

/// <summary>Builds a C# route and requires its complete native connection graph before reporting success.</summary>
public sealed class PipeConnectionController(IGameClient game, IControllerJournal journal)
{
    public async Task<PipeConnectionResult> RunAsync(string sourceId, string targetId, string fluid, CancellationToken token = default)
    {
        if (sourceId == targetId) throw new ArgumentException("Fluid routing requires distinct source and target entities.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var own = await new ProductionController(game, journal).ObserveAsync(token);
        if (own.Scope != catalog.Scope || !own.Entities.Any(e => e.Id == sourceId) || !own.Entities.Any(e => e.Id == targetId))
            throw new InvalidDataException("Fluid endpoints are not in the same known own factory scope.");
        string pipeItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe").OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key).FirstOrDefault() ?? throw new InvalidOperationException("No ordinary pipe item is available.");
        var spatial = new SpatialClient(game);
        SpatialSnapshot initial = await MapAsync();
        var plan = new PipeRoutePlanner().Find(initial, pipeItem, sourceId, targetId, fluid);
        await journal.AppendAsync("pipe-route-plan", new { initial.Scope, initial.CollectedTick, sourceId, targetId, fluid, pipeItem, plan }, token);
        if (plan.Status != PipeRouteStatus.Found || plan.Source is null || plan.Target is null)
            throw new InvalidOperationException($"Fluid routing ended with {plan.Status}; no route is executed.");
        if (plan.Pipes.Count > 200) throw new InvalidOperationException("The planned pipe route exceeds the current construction budget.");
        FactorySnapshot stock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (stock.Scope != catalog.Scope) throw new InvalidDataException("Fluid inventory scope changed before construction.");
        if (stock.FluidStockAt(sourceId, fluid, plan.Source.BoxIndex) <= 0)
            throw new InvalidOperationException("The selected source has no observed stock of the requested fluid.");
        if (stock.FluidRecordsAt(targetId, plan.Target.BoxIndex).Any(r => r.Data.GetProperty("contents").EnumerateObject()
                .Any(p => p.Name != fluid && p.Value.GetDouble() > 0)))
            throw new InvalidOperationException("The selected target already contains another fluid.");
        var built = new HashSet<string>(StringComparer.Ordinal);
        if (plan.Pipes.Count > 0) await new ProductionGoalExecutor(game, journal).RunAsync(pipeItem, plan.Pipes.Count, token);
        await using var controller = new SpatialController(game, journal);
        var construction = new PoweredMachineController(game, journal);
        for (int index = 0; index < plan.Pipes.Count; index++)
        {
            var position = plan.Pipes[index];
            var current = await MapAsync();
            if (!PipeRoutePlanner.ConnectionsSafe(current, position, plan.Source, plan.Target, built))
                throw new InvalidDataException("The next pipe would join an unplanned fluid port. Reconcile the partial route.");
            var remaining = plan.Pipes.Skip(index + 1).Append(plan.Source.Position).Append(plan.Target.Position).ToArray();
            string id = await construction.BuildAtAsync(pipeItem, new(position, 0, 0), catalog, controller, token, remaining);
            built.Add(id);
            await journal.AppendAsync("pipe-built", new { id, position, sourceId, targetId, fluid }, token);
        }
        SpatialSnapshot proof = await MapAsync();
        if (!FluidNetwork.IsConnected(proof, plan.Source, plan.Target, fluid))
            throw new InvalidDataException("The native fluid graph does not prove the complete planned connection. Preserve and reconcile partial construction.");
        var result = new PipeConnectionResult(sourceId, targetId, fluid, initial.CollectedTick, proof.CollectedTick, built.ToArray(), plan);
        await journal.AppendAsync("pipe-connection-result", result, token);
        return result;

        async Task<SpatialSnapshot> MapAsync()
        {
            var value = await spatial.CaptureAsync([pipeItem], 48, token);
            if (value.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed during pipe construction.");
            if (!value.Entities.Any(e => e.Id == sourceId) || !value.Entities.Any(e => e.Id == targetId))
                throw new InvalidOperationException("Both pipe endpoints must be in the current observed construction area.");
            return value;
        }
    }
}
