using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record PipeConnectionResult(string SourceId, string TargetId, string Fluid, long StartTick, long EndTick,
    IReadOnlyList<string> BuiltPipeIds, PipeRoutePlan Plan);

/// <summary>Builds a C# route and requires its complete native connection graph before reporting success.</summary>
public sealed class PipeConnectionController(IGameClient game, IControllerJournal journal)
{
    /// <param name="ground">Reserves ground the route must keep off; only the plan sees it, the safety and proof checks read the engine.</param>
    public async Task<PipeConnectionResult> RunAsync(string sourceId, string targetId, string fluid, CancellationToken token = default,
        Func<SpatialSnapshot, SpatialSnapshot>? ground = null, IReadOnlyList<string>? geometryItems = null)
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
        string? tunnelItem = FluidRelayController.UndergroundItem(catalog);
        string[] geometry = new[] { pipeItem }.Concat(tunnelItem is null ? [] : new[] { tunnelItem }).Concat(geometryItems ?? [])
            .Distinct(StringComparer.Ordinal).ToArray();
        if (geometry.Length > SpatialSnapshot.MaximumItems) throw new InvalidOperationException("Pipe routing exceeds the native item geometry observation budget.");
        var spatial = new SpatialClient(game);
        SpatialSnapshot initial = await MapAsync();
        var routed = ground?.Invoke(initial) ?? initial;
        var plan = new PipeRoutePlanner().Find(routed, pipeItem, sourceId, targetId, fluid, cancellationToken: token);
        // Ordinary pipes cannot cross belts, pipes or machines. On 2026-10-07 (seed 987654321) a refinery planned a day earlier
        // had no surface route left to its pumpjack; tunnels pass under what the ground now holds.
        if (plan.Status != PipeRouteStatus.Found && tunnelItem is not null)
            plan = new PipeRoutePlanner().Find(routed, pipeItem, sourceId, targetId, fluid, cancellationToken: token, undergroundItem: tunnelItem);
        await journal.AppendAsync("pipe-route-plan", new { initial.Scope, initial.CollectedTick, sourceId, targetId, fluid, pipeItem, plan }, token);
        if (plan.Status != PipeRouteStatus.Found || plan.Source is null || plan.Target is null)
        {
            // The ports and what they already join tell an occupied endpoint from blocked ground.
            object Ports(string id) => (routed.Entities.SingleOrDefault(e => e.Id == id)?.FluidConnections ?? []).Select(p => new
                { p.BoxIndex, p.PortIndex, p.Position, p.TargetPosition, p.TargetEntityId, p.Type, p.FlowDirection, p.Filter }).ToArray();
            await journal.AppendAsync("pipe-route-ports", new { sourceId, targetId, fluid, plan.Status, tunnelItem,
                source = Ports(sourceId), target = Ports(targetId) }, token);
            throw new FluidSiteUnavailableException($"Fluid routing ended with {plan.Status}; no route is executed.");
        }
        var pieces = FluidRelayController.Pieces(plan, pipeItem, tunnelItem);
        if (pieces.Count > 200) throw new InvalidOperationException("The planned pipe route exceeds the current construction budget.");
        FactorySnapshot stock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (stock.Scope != catalog.Scope) throw new InvalidDataException("Fluid inventory scope changed before construction.");
        if (stock.FluidStockAt(sourceId, fluid, plan.Source.BoxIndex) <= 0 && !OffshoreSupplyPlanner.CanExtract(initial, sourceId, fluid))
            throw new InvalidOperationException("The selected source has no observed stock of the requested fluid.");
        if (stock.FluidRecordsAt(targetId, plan.Target.BoxIndex).Any(r => r.Data.GetProperty("contents").EnumerateObject()
                .Any(p => p.Name != fluid && p.Value.GetDouble() > 0)))
            throw new InvalidOperationException("The selected target already contains another fluid.");
        var built = new HashSet<string>(StringComparer.Ordinal);
        // One kit: tunnels are crafted from pipes and must not consume the ones carried for this route.
        if (pieces.Count > 0)
            await CarriedStock.EnsureAsync(game, journal, catalog, pieces.GroupBy(p => p.Item, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), token);
        await using var controller = new SpatialController(game, journal);
        // Procurement may fetch plates from the base. On 2026-10-07 (seed 20261106) the actor returned from a refinery 360 tiles
        // away with the pipes but rephotographed the route from the base; the planning view is rejoined before building.
        if ((await spatial.CaptureAsync(radius: 16, cancellationToken: token, light: true)).Actor.Position.DistanceTo(initial.Actor.Position) > 8)
            await controller.TravelAsync(initial.Actor.Position, 4, catalog, token);
        var construction = new PoweredMachineController(game, journal);
        for (int index = 0; index < pieces.Count; index++)
        {
            var piece = pieces[index];
            var position = piece.Position;
            var current = await MapAsync();
            if (!PipeRoutePlanner.ConnectionsSafe(current, position, plan.Source, plan.Target, built))
                throw new InvalidDataException("The next pipe would join an unplanned fluid port. Reconcile the partial route.");
            var remaining = pieces.Skip(index + 1).Select(p => p.Position).Append(plan.Source.Position).Append(plan.Target.Position).ToArray();
            string id = await construction.BuildAtAsync(piece.Item, new(position, piece.Direction, 0), catalog, controller, token, remaining);
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
            var value = await spatial.CaptureAsync(geometry, 48, token);
            if (value.Scope != catalog.Scope) throw new InvalidDataException("Actor identity changed during pipe construction.");
            if (!value.Entities.Any(e => e.Id == sourceId) || !value.Entities.Any(e => e.Id == targetId))
                throw new InvalidOperationException("Both pipe endpoints must be in the current observed construction area.");
            return value;
        }
    }
}
