using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

internal sealed record FluidRelaySource(string Id, MapPosition Position);

/// <summary>Persistent ordinary-pipe sections. Plans and each receipt survive interruptions; native stock proves every outlet.</summary>
internal sealed class FluidRelayController(IGameClient game, IControllerJournal journal, string directory)
{
    public const string Kind = "fluid-link";
    public const int MaximumSections = 12;

    public async Task ExtendAsync(string fluid, MapPosition destination, ProductionCatalog catalog, SpatialController controller,
        FactoryGround ground, CancellationToken token)
    {
        string pipeItem = catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe").OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key).FirstOrDefault() ?? throw new InvalidOperationException("No native pipe item for a remote supply.");
        var registry = new FactoryRegistry(directory);
        var builder = new FactoryCellBuilder(game, journal, directory);
        for (int section = 0; section < MaximumSections; section++)
        {
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var cell = state.Cells.FirstOrDefault(c => c.Kind == Kind && c.Recipe == fluid && c.Status == "building" && c.FluidRoute is not null);
            var stock = await StockAsync();
            if (cell is null)
            {
                var source = Sources(stock, fluid).OrderBy(p => p.Position.DistanceTo(destination)).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault()
                    ?? throw new InvalidOperationException($"No known native {fluid} source supplies a remote pipe section.");
                await controller.TravelAsync(source.Position, 6, catalog, token);
                var map = await MapAsync();
                if ((await StockAsync()).FluidStockAt(source.Id, fluid) <= 0 && !OffshoreSupplyPlanner.CanExtract(map, source.Id, fluid))
                    throw new InvalidOperationException("The observed relay source has neither stock nor a compatible native terrain intake.");
                if (source.Position.DistanceTo(destination) <= FluidRelayPlanner.Step) return;
                var site = await ControllerPlanning.RunAsync(t => new FluidRelayPlanner().Find(
                    FactoryGround.Reserve(map, ground.Boxes(map), pipeItem), pipeItem, source.Id, fluid, destination, t),
                    controller, TimeSpan.FromMinutes(2), token)
                    ?? throw new InvalidOperationException($"No safe local pipe section extends {fluid} toward its consumer.");
                var plans = new Dictionary<string, PlannedEntity>(StringComparer.Ordinal)
                    { ["outlet"] = new("outlet", pipeItem, site.Outlet.Position, site.Outlet.Direction) };
                for (int i = 0; i < site.Route.Pipes.Count; i++) plans[$"pipe-{i}"] = new($"pipe-{i}", pipeItem, site.Route.Pipes[i], 0);
                cell = new($"fluid-link-{Guid.NewGuid():N}", 0, new(0, 0, true), Kind, pipeItem, fluid,
                    new Dictionary<string, string>(), "building", map.CollectedTick, Plan: plans, FluidRoute: site.Route);
                await SaveAsync();
                await journal.AppendAsync("fluid-relay-plan", new { cell.Id, fluid, destination, site, map.CollectedTick }, token);
            }
            else await controller.TravelAsync(cell.Plan!["outlet"].Position, 6, catalog, token);

            var initial = await MapAsync();
            var route = cell.FluidRoute!;
            var original = route.Source ?? throw new InvalidDataException("The persisted fluid relay has no source endpoint.");
            // Maintenance may have rebuilt the source. Its native port geometry and stock must still match the original plan.
            var sourceEntity = initial.Entities.SingleOrDefault(e => (e.FluidConnections ?? []).Any(p =>
                p.Position == original.Position && p.TargetPosition == original.TargetPosition && p.BoxIndex == original.BoxIndex
                && p.PortIndex == original.PortIndex && p.Type == "normal" && (p.Filter is null || p.Filter == fluid)
                && p.FlowDirection is "output" or "input-output"))
                ?? throw new InvalidOperationException("The planned relay source is no longer observed; preserve its unfinished section.");
            route = route with { Source = original with { EntityId = sourceEntity.Id } };
            if ((await StockAsync()).FluidStockAt(sourceEntity.Id, fluid, original.BoxIndex) <= 0
                && !OffshoreSupplyPlanner.CanExtract(initial, sourceEntity.Id, fluid))
                throw new InvalidOperationException("The planned relay source has no native stock; preserve its unfinished section.");

            var present = FactoryMaintenance.Present(await StockAsync());
            cell = cell with { Entities = cell.Entities.Where(p => present.Contains(p.Value)).ToDictionary(), FluidRoute = route };
            await SaveAsync();
            // Observe all planned positions first, including receipts applied just before an interruption.
            var adopted = ObserveParts(initial, cell);
            var parts = adopted.Values.ToHashSet(StringComparer.Ordinal);
            RequireIsolated(initial, parts, sourceEntity.Id, RegisteredDownstreamParts(initial, parts, state, fluid));
            RequireCompatibleStock(await StockAsync(), adopted.Values, fluid);
            cell = cell with { Entities = adopted };
            await SaveAsync();
            string[] roles = new[] { "outlet" }.Concat(Enumerable.Range(0, route.Pipes.Count).Select(i => $"pipe-{i}")).ToArray();
            int missing = roles.Count(r => !cell.Entities.ContainsKey(r));
            if (missing > 0) await builder.EnsureCarriedAsync(registry, catalog, pipeItem, missing, token);
            await controller.TravelAsync(cell.Plan!["outlet"].Position, 6, catalog, token);
            for (int index = 0; index < roles.Length; index++)
            {
                string role = roles[index];
                if (cell.Entities.ContainsKey(role)) continue;
                var plan = cell.Plan![role];
                var current = await MapAsync();
                if (role == "outlet")
                {
                    if (current.Entities.SelectMany(e => e.FluidConnections ?? []).Any(p => p.TargetPosition == plan.Position))
                        throw new InvalidDataException("The relay outlet would join an unplanned native port.");
                }
                else
                {
                    var target = route.Target! with { EntityId = cell.Entities["outlet"] };
                    if (!PipeRoutePlanner.ConnectionsSafe(current, plan.Position, route.Source!, target,
                        cell.Entities.Values.ToHashSet(StringComparer.Ordinal)))
                        throw new InvalidDataException("The next relay pipe would join an unplanned native port; preserve the partial section.");
                }
                string id = await new PoweredMachineController(game, journal).BuildAtAsync(pipeItem,
                    new(plan.Position, plan.Direction, 0), catalog, controller, token,
                    roles.Skip(index + 1).Select(r => cell.Plan![r].Position).ToArray());
                cell = cell with { Entities = new Dictionary<string, string>(cell.Entities) { [role] = id } };
                await SaveAsync();
                await journal.AppendAsync("fluid-relay-built", new { cell.Id, role, entityId = id, plan }, token);
            }
            var proof = await MapAsync();
            route = route with { Target = route.Target! with { EntityId = cell.Entities["outlet"] } };
            if (!FluidNetwork.IsConnected(proof, route.Source!, route.Target!, fluid))
                throw new InvalidDataException("The native graph does not prove the entire relay section; preserve its partial plan.");
            double amount = 0;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                amount = (await StockAsync()).FluidStockAt(cell.Entities["outlet"], fluid);
                if (amount > 0) break;
                var waited = await controller.WorkAsync("wait", new { ticks = 120 }, 420, token: token);
                if (waited.Status != "completed") throw new InvalidOperationException("The relay stock wait did not complete.");
            }
            if (amount <= 0) throw new InvalidOperationException("The connected relay outlet has no native stock within its wait budget.");
            cell = cell with { Status = "ready", Tick = proof.CollectedTick, FluidRoute = route };
            await SaveAsync();
            await journal.AppendAsync("fluid-relay-ready", new { cell.Id, fluid, amount, outlet = cell.Entities["outlet"], destination,
                position = cell.Plan!["outlet"].Position, proof.CollectedTick }, token);

            async Task SaveAsync() => await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(cell!), token);
        }
        if (Sources(await StockAsync(), fluid).Any(p => p.Position.DistanceTo(destination) <= FluidRelayPlanner.Step)) return;
        throw new InvalidOperationException($"The remote {fluid} supply exhausted its section budget; completed sections remain registered.");

        async Task<SpatialSnapshot> MapAsync()
        {
            var map = await new SpatialClient(game).CaptureAsync(new[] { pipeItem }.Concat(ground.Items).Distinct(StringComparer.Ordinal).ToArray(), 48, token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during fluid relay construction.");
            return map;
        }
        async Task<FactorySnapshot> StockAsync()
        {
            var stock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (stock.Scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during fluid relay stock observation.");
            return stock;
        }
    }

    // An unused pump can have an empty output box. It is only a candidate until its local native terrain intake is checked.
    internal static IEnumerable<FluidRelaySource> Sources(FactorySnapshot stock, string fluid) =>
        stock.Records.Where(r => r.Kind == "fluid" && r.Data.GetProperty("contents").TryGetProperty(fluid, out var amount) && amount.GetDouble() > 0)
            .SelectMany(r => r.Data.GetProperty("sourceBoxes").EnumerateArray().Select(b => b.GetProperty("entityId").GetString()!))
            .Concat(stock.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("type").GetString() == "offshore-pump").Select(r => r.EntityId))
            .Distinct(StringComparer.Ordinal).Select(id => stock.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == id))
            .OfType<FactoryRecord>().Where(r => r.Data.GetProperty("type").GetString() is "pipe" or "pipe-to-ground" or "offshore-pump" or "storage-tank")
            .Select(r => new FluidRelaySource(r.EntityId, r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!));

    internal static Dictionary<string, string> ObserveParts(SpatialSnapshot map, FactoryCell cell) => cell.Plan!.ToDictionary(p => p.Key,
        p => map.Entities.SingleOrDefault(e => e.Name == map.Items[p.Value.Item].EntityName && e.Position.DistanceTo(p.Value.Position) < .01)?.Id)
        .Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

    internal static IReadOnlySet<string> RegisteredDownstreamParts(SpatialSnapshot map, IReadOnlySet<string> parts, FactoryState state, string fluid)
    {
        var neighbors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var downstream in state.Cells.Where(c => c.Kind == Kind && c.Recipe == fluid && c.Plan is not null && c.FluidRoute?.Source is not null))
        {
            var endpoint = downstream.FluidRoute!.Source!;
            var sourcePort = map.Entities.Where(e => parts.Contains(e.Id)).SelectMany(e => e.FluidConnections ?? [])
                .SingleOrDefault(p => p.Position == endpoint.Position && p.TargetPosition == endpoint.TargetPosition
                    && p.BoxIndex == endpoint.BoxIndex && p.PortIndex == endpoint.PortIndex);
            if (sourcePort?.TargetEntityId is not { } neighbor) continue;
            var observed = ObserveParts(map, downstream);
            if (observed.Values.Contains(neighbor, StringComparer.Ordinal)) neighbors.Add(neighbor);
        }
        return neighbors;
    }

    internal static void RequireIsolated(SpatialSnapshot map, IReadOnlySet<string> parts, string sourceId,
        IReadOnlySet<string>? registeredDownstream = null)
    {
        if (map.Entities.Where(e => parts.Contains(e.Id)).SelectMany(e => e.FluidConnections ?? [])
            .Any(p => p.TargetEntityId is { } id && id != sourceId && !parts.Contains(id)
                && registeredDownstream?.Contains(id) != true))
            throw new InvalidDataException("A persisted relay part has joined an unplanned native network.");
    }

    internal static void RequireCompatibleStock(FactorySnapshot stock, IEnumerable<string> parts, string fluid)
    {
        if (parts.Any(id => stock.FluidRecordsAt(id).Any(r => r.Data.GetProperty("contents").EnumerateObject()
            .Any(p => p.Name != fluid && p.Value.GetDouble() > 0))))
            throw new InvalidDataException("A persisted relay part contains another native fluid.");
    }
}
