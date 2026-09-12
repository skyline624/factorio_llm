using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Shared native installation and local steam maintenance for powered production machines.</summary>
public sealed class PoweredMachineController(IGameClient game, IControllerJournal journal)
{
    public async Task<string> InstallAsync(string item, ProductionCatalog catalog, SpatialController controller, CancellationToken token)
    {
        var production = new ProductionController(game, journal);
        await new ProductionGoalExecutor(game, journal).RunAsync(item, 1, token);
        ProductionState factory = await production.ObserveAsync(token);
        RequireScope(factory.Scope, catalog);
        var poleNames = catalog.Items.Values.Where(i => i.PlaceEntityType == "electric-pole").Select(i => i.PlaceEntity).ToHashSet(StringComparer.Ordinal);
        if (!factory.Entities.Any(e => poleNames.Contains(e.Name)))
        {
            await new SteamPowerController(game, journal).RunAsync(token);
            factory = await production.ObserveAsync(token);
            RequireScope(factory.Scope, catalog);
        }
        ProductionEntity source = factory.Entities.Where(e => poleNames.Contains(e.Name)).OrderBy(e => e.Id, StringComparer.Ordinal).First();
        string poleItem = catalog.Items.First(p => p.Value.PlaceEntity == source.Name).Key;
        var spatial = new SpatialClient(game);
        await controller.TravelAsync(source.Position, 8, catalog, token);
        SpatialSnapshot map = await MapAsync();
        SpatialEntity pole = map.Entities.Single(e => e.Id == source.Id);
        var planner = new PoweredMachinePlanner();
        PlacementCandidate? placement = planner.Place(map, item, pole);
        if (placement is null)
        {
            PoweredMachineExtension extension = planner.Extend(map, item, poleItem, pole)
                ?? throw new InvalidOperationException("No clear powered placement or bounded pole extension on observed terrain.");
            await journal.AppendAsync("powered-machine-extension", new { map.Scope, map.CollectedTick, sourceId = pole.Id, item, poleItem, extension }, token);
            await new ProductionGoalExecutor(game, journal).RunAsync(poleItem, 1, token);
            string poleId = await BuildAsync(poleItem, extension.Pole);
            map = await MapAsync();
            SpatialEntity connected = map.Entities.Single(e => e.Id == poleId);
            if (connected.Power?.NetworkId is null || connected.Power.NetworkId != map.Entities.Single(e => e.Id == pole.Id).Power?.NetworkId)
                throw new InvalidOperationException("The added pole did not join the source network. Reconcile partial construction.");
            pole = connected;
            placement = extension.Machine;
        }
        await journal.AppendAsync("powered-machine-placement", new { map.Scope, map.CollectedTick, item, poleId = pole.Id, placement }, token);
        string id = await BuildAsync(item, placement);
        map = await MapAsync();
        SpatialEntity machine = map.Entities.Single(e => e.Id == id);
        if (machine.Power?.NetworkId is null || machine.Power.NetworkId != map.Entities.Single(e => e.Id == pole.Id).Power?.NetworkId)
            throw new InvalidOperationException("The constructed machine did not join the planned electric network.");
        return id;

        async Task<SpatialSnapshot> MapAsync()
        {
            var value = await spatial.CaptureAsync([item, poleItem], 48, token);
            RequireScope(value.Scope, catalog);
            return value;
        }
        Task<string> BuildAsync(string buildingItem, PlacementCandidate candidate) =>
            BuildAtAsync(buildingItem, candidate, catalog, controller, token);
    }

    public async Task<string> BuildAtAsync(string item, PlacementCandidate candidate, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token)
    {
        var spatial = new SpatialClient(game);
        await controller.TravelAsync(candidate.Position, 8, catalog, token);
        var current = await spatial.CaptureAsync([item], radius: 48, cancellationToken: token);
        RequireScope(current.Scope, catalog);
        MapPosition approach = new PlacementPlanner().FindApproach(new(current), item, candidate)
            ?? throw new InvalidOperationException("No reachable approach outside the planned footprint.");
        await controller.NavigateAsync(approach, .2, token);
        var validation = await spatial.ValidateAsync(catalog.Scope, item, [candidate], token);
        if (!validation.Candidates[0].Allowed || !validation.Candidates[0].InReach)
            throw new InvalidOperationException("The engine refused the calculated powered-machine placement.");
        var built = await controller.WorkAsync("build", new { item, candidate.Position, candidate.Direction }, 600, token: token);
        Completed(built);
        return built.Effects.GetProperty("entityId").GetString()!;
    }

    public async Task MaintainFuelAsync(string machineId, double expectedEnergy, ProductionCatalog catalog,
        SpatialController controller, bool reserve, CancellationToken token)
    {
        if (!double.IsFinite(expectedEnergy) || expectedEnergy < 0) throw new InvalidDataException("Invalid production energy estimate.");
        var production = new ProductionController(game, journal);
        ProductionState owned = await production.ObserveAsync(token);
        RequireScope(owned.Scope, catalog);
        ProductionEntity machine = owned.Entities.Single(e => e.Id == machineId);
        await controller.TravelAsync(machine.Position, 4, catalog, token);
        SpatialSnapshot map = await new SpatialClient(game).CaptureAsync(radius: 48, cancellationToken: token);
        RequireScope(map.Scope, catalog);
        owned = await production.ObserveAsync(token);
        RequireScope(owned.Scope, catalog);
        ObservedPower power = map.Entities.Single(e => e.Id == machineId).Power
            ?? throw new InvalidDataException("Missing power observation for the consumer.");
        if (!reserve && power.Energy > 0) return;
        var generators = map.Entities.Where(e => map.Prototypes[e.Name].Type == "generator" && e.Power?.NetworkId == power.NetworkId).ToArray();
        SpatialEntity? boiler = map.Entities.FirstOrDefault(e => map.Prototypes[e.Name].Type == "boiler" && owned.Entities.Any(o => o.Id == e.Id)
            && generators.Any(g => e.FluidConnections?.Any(f => f.TargetEntityId == g.Id) == true || g.FluidConnections?.Any(f => f.TargetEntityId == e.Id) == true));
        if (boiler is null)
        {
            if (power.Energy > 0) return;
            throw new InvalidOperationException("No observed maintainable steam supply for the unpowered consumer.");
        }
        var categories = map.Prototypes[boiler.Name].FuelCategories;
        string fuel = catalog.Items.Where(p => p.Value.FuelValue > 0 && p.Value.FuelCategory is { } category && categories?.ContainsKey(category) == true
                && catalog.Mining.Values.Any(products => products.Any(m => m.Name == p.Key && m.DeterministicItem)))
            .OrderByDescending(p => owned.Inventory.GetValueOrDefault(p.Key) > 0).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).First();
        int capacity = Math.Min(100, catalog.Items[fuel].StackSize);
        int target = (int)Math.Clamp(Math.Ceiling(expectedEnergy * 1.25 / catalog.Items[fuel].FuelValue), Math.Min(2, capacity), capacity);
        int missing = (int)Math.Max(0, target - owned.Entities.Single(e => e.Id == boiler.Id).Count("fuel", fuel));
        if (missing == 0) return;
        await new ProductionGoalExecutor(game, journal).RunAsync(fuel, missing, token);
        await controller.TravelAsync(boiler.Position, 3, catalog, token);
        var inserted = await controller.WorkAsync("insert", new { entityId = boiler.Id, inventory = "fuel", item = fuel, count = missing }, 600, token: token);
        Completed(inserted);
        await journal.AppendAsync("powered-machine-fuel", new { machineId, boilerId = boiler.Id, fuel, count = missing, expectedEnergy }, token);
    }

    private static void RequireScope(ActorScope scope, ProductionCatalog catalog)
    {
        if (scope != catalog.Scope) throw new InvalidDataException("Actor identity changed during powered installation; reconcile partial effects.");
    }
    private static void Completed(OperationReceipt receipt)
    {
        if (receipt.Status != "completed") throw new InvalidOperationException($"Power action ended with {receipt.Status}: {receipt.Error?.Code}.");
    }
}
