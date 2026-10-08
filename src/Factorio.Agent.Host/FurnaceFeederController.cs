using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Gives each fuel-burning furnace of a ready cell a chest and inserter feeding its fuel slot, registered as a fuel-feeder cell
/// whose only input is coal, so buses can fuel it instead of the actor. On 2026-10-08, 10 to 24 furnaces per normal campaign
/// stood without fuel between hand deliveries. A furnace without room for a feeder, or without a powered cell pole, is reported
/// and keeps its hand refuelling.
/// </summary>
internal sealed class FurnaceFeederController(IGameClient game, IControllerJournal journal, string directory)
{
    public const string Kind = "fuel-feeder";
    /// <summary>Feeders built in one maintenance round.</summary>
    internal const int MaximumPerRound = 6;

    public static string FeederId(FactoryCell furnaceCell) => $"{Kind}:{furnaceCell.Id}";

    public async Task<int> RunAsync(CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Feeder photograph belongs to another actor scope.");
        var pending = Pending(state, snapshot).Take(MaximumPerRound).ToArray();
        if (pending.Length == 0) return 0;
        var equipment = FactoryCellBuilder.Equipment(catalog, pending[0].MachineItem);
        var builder = new FactoryCellBuilder(game, journal, directory);
        await builder.EnsureCarriedAsync(registry, catalog, new Dictionary<string, int>(StringComparer.Ordinal)
            { [equipment.Chest] = pending.Length, [equipment.Inserter] = pending.Length }, token);
        await using var controller = new SpatialController(game, journal);
        int built = 0;
        foreach (var cell in pending)
        {
            string furnaceId = cell.Entities["furnace"];
            var furnacePlan = cell.Plan?.GetValueOrDefault("furnace");
            if (furnacePlan is null) continue;
            try { await controller.TravelAsync(furnacePlan.Position, 4, catalog, token); }
            catch (Exception error) when (error is NavigationPlanningException or EntityUnreachableException
                or ExplorationBlockedException or ExplorationDangerException)
            {
                await journal.AppendAsync("furnace-fuel-feeder-skipped", new { cell.Id, furnaceId, reason = "unreachable" }, token);
                continue;
            }
            var map = await new SpatialClient(game).CaptureAsync([equipment.Chest, equipment.Inserter, equipment.Pole], 32, token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while fitting a furnace feeder.");
            long? network = cell.Entities.TryGetValue("pole", out var poleId) ? map.Entities.SingleOrDefault(e => e.Id == poleId)?.Power?.NetworkId : null;
            if (map.Entities.All(e => e.Id != furnaceId) || network is null)
            {
                await journal.AppendAsync("furnace-fuel-feeder-skipped", new { cell.Id, furnaceId, reason = "no-powered-pole" }, token);
                continue;
            }
            var plan = new FuelFeederPlanner().Find(map, equipment.Chest, equipment.Inserter, furnaceId, network.Value, equipment.Pole);
            if (plan is null)
            {
                await journal.AppendAsync("furnace-fuel-feeder-skipped", new { cell.Id, furnaceId, reason = "no-room" }, token);
                continue;
            }
            var entities = new Dictionary<string, string>(StringComparer.Ordinal);
            var planned = new Dictionary<string, PlannedEntity>(StringComparer.Ordinal)
            {
                ["input-chest"] = new("input-chest", equipment.Chest, plan.Container.Position, plan.Container.Direction),
                ["input-inserter"] = new("input-inserter", equipment.Inserter, plan.Inserter.Position, plan.Inserter.Direction)
            };
            if (plan.Pole is { } pole)
            {
                await builder.EnsureCarriedAsync(registry, catalog, equipment.Pole, 1, token);
                planned["pole"] = new("pole", equipment.Pole, pole.Position, pole.Direction);
            }
            var construction = new PoweredMachineController(game, journal);
            entities["input-chest"] = plan.ExistingContainerId ?? await construction.BuildAtAsync(equipment.Chest, plan.Container, catalog, controller, token);
            entities["input-inserter"] = plan.ExistingInserterId ?? await construction.BuildAtAsync(equipment.Inserter, plan.Inserter, catalog, controller, token);
            if (plan.Pole is { } extension) entities["pole"] = await construction.BuildAtAsync(equipment.Pole, extension, catalog, controller, token);
            var feeder = new FactoryCell(FeederId(cell), 0, new(0, 0, true), Kind, equipment.Chest, null, entities, "ready", map.CollectedTick, Plan: planned);
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(feeder), token);
            await journal.AppendAsync("furnace-fuel-feeder", new { feeder.Id, furnaceId, entities, map.CollectedTick }, token);
            built++;
        }
        return built;
    }

    /// <summary>Ready cells whose furnace burns fuel (a native fuel inventory) and which have no feeder yet.</summary>
    internal static IEnumerable<FactoryCell> Pending(FactoryState state, FactorySnapshot snapshot) => state.Cells.Where(c => c.Status == "ready"
        && c.Kind != Kind && c.Entities.TryGetValue("furnace", out var furnace) && Burns(snapshot, furnace)
        && state.Cells.All(f => f.Id != FeederId(c)));

    internal static bool Burns(FactorySnapshot snapshot, string entityId) => snapshot.Records.Any(r => r.Kind == "inventory"
        && r.EntityId == entityId && r.Name.Contains("fuel", StringComparison.Ordinal));

    /// <summary>Furnace entities whose feeder chest a healthy bus keeps in coal; the actor no longer refuels them.</summary>
    internal static IReadOnlySet<string> BeltFuelled(FactoryState state, IReadOnlySet<(string Chest, string Item)> connected) => state.Cells
        .Where(f => f.Kind == Kind && f.Entities.TryGetValue("input-chest", out var chest) && connected.Contains((chest, FactoryLogistics.Fuel)))
        .Select(f => state.Cells.SingleOrDefault(c => FeederId(c) == f.Id)?.Entities.GetValueOrDefault("furnace")).OfType<string>()
        .ToHashSet(StringComparer.Ordinal);
}
