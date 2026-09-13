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
        var spatial = new SpatialClient(game);
        var local = await spatial.CaptureAsync(radius: 48, cancellationToken: token);
        RequireScope(local.Scope, catalog);
        var planner = new PoweredMachinePlanner();
        var sources = factory.Entities.Where(e => poleNames.Contains(e.Name)).ToArray();
        var nearest = planner.NearestSupply(local, sources.Select(e => e.Id).ToHashSet(StringComparer.Ordinal));
        ProductionEntity source = nearest is not null ? sources.Single(e => e.Id == nearest.Id)
            : sources.OrderBy(e => e.Position.DistanceTo(local.Actor.Position)).ThenBy(e => e.Id, StringComparer.Ordinal).First();
        string poleItem = catalog.Items.First(p => p.Value.PlaceEntity == source.Name).Key;
        string[] geometryItems = [item, poleItem, .. catalog.Items.Where(p => p.Value.PlaceEntityType == "pipe")
            .OrderBy(p => p.Key, StringComparer.Ordinal).Take(1).Select(p => p.Key)];
        await controller.TravelAsync(source.Position, 8, catalog, token);
        SpatialSnapshot map = await MapAsync();
        SpatialEntity pole = map.Entities.Single(e => e.Id == source.Id);
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
            var value = await spatial.CaptureAsync(geometryItems, 48, token);
            RequireScope(value.Scope, catalog);
            return value;
        }
        Task<string> BuildAsync(string buildingItem, PlacementCandidate candidate) =>
            BuildAtAsync(buildingItem, candidate, catalog, controller, token);
    }

    public async Task<string> BuildAtAsync(string item, PlacementCandidate candidate, ProductionCatalog catalog,
        SpatialController controller, CancellationToken token, IReadOnlyList<MapPosition>? remainingTargets = null)
    {
        var spatial = new SpatialClient(game);
        var current = await spatial.CaptureAsync([item], radius: 48, cancellationToken: token);
        RequireScope(current.Scope, catalog);
        if (current.Actor.Position.DistanceTo(candidate.Position) > 24)
        {
            await controller.TravelAsync(candidate.Position, 8, catalog, token);
            current = await spatial.CaptureAsync([item], radius: 48, cancellationToken: token);
            RequireScope(current.Scope, catalog);
        }
        RequireScope(current.Scope, catalog);
        MapPosition approach = new PlacementPlanner().FindApproach(new(current), item, candidate, remainingTargets)
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
        await controller.ApproachEntityAsync(machineId, machine.Position, catalog, token);
        SpatialSnapshot map = await new SpatialClient(game).CaptureAsync(radius: 48, cancellationToken: token);
        RequireScope(map.Scope, catalog);
        owned = await production.ObserveAsync(token);
        RequireScope(owned.Scope, catalog);
        ObservedPower power = map.Entities.Single(e => e.Id == machineId).Power
            ?? throw new InvalidDataException("Missing power observation for the consumer.");
        SpatialEntity? boiler = FindBoiler(map);
        bool remote = false;
        if (boiler is null && (reserve || power.Energy <= 0))
        {
            var generatorNames = catalog.Items.Values.Where(i => i.PlaceEntityType == "generator").Select(i => i.PlaceEntity)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var generator in owned.Entities.Where(e => generatorNames.Contains(e.Name))
                .OrderBy(e => e.Position.DistanceTo(machine.Position)).Take(16))
            {
                await controller.TravelAsync(generator.Position, 8, catalog, token);
                remote = true;
                map = await new SpatialClient(game).CaptureAsync(radius: 48, cancellationToken: token);
                RequireScope(map.Scope, catalog);
                owned = await production.ObserveAsync(token);
                RequireScope(owned.Scope, catalog);
                boiler = FindBoiler(map);
                if (boiler is not null) break;
            }
        }
        await MaintainSupplyAsync();
        if (remote) await controller.ApproachEntityAsync(machineId, machine.Position, catalog, token);

        SpatialEntity? FindBoiler(SpatialSnapshot current)
        {
            if (power.NetworkId is null) return null;
            var generators = current.Entities.Where(e => current.Prototypes[e.Name].Type == "generator"
                && e.Power?.NetworkId == power.NetworkId && owned.Entities.Any(o => o.Id == e.Id)).ToArray();
            return current.Entities.FirstOrDefault(e => current.Prototypes[e.Name].Type == "boiler" && owned.Entities.Any(o => o.Id == e.Id)
                && generators.Any(g => e.FluidConnections?.Any(f => f.TargetEntityId == g.Id) == true
                    || g.FluidConnections?.Any(f => f.TargetEntityId == e.Id) == true));
        }

        async Task MaintainSupplyAsync()
        {
            if (boiler is null)
            {
                if (power.Energy > 0) return;
                throw new InvalidOperationException("No observed maintainable steam supply for the unpowered consumer.");
            }
            if (power.NetworkId is { } networkId && await new FuelReserveController(game, journal)
                .TryMaintainAsync(boiler.Id, networkId, map, catalog, controller, expectedEnergy, reserve, power.Energy <= 0, token)) return;
            if (!reserve && power.Energy > 0) return;
            var categories = map.Prototypes[boiler.Name].FuelCategories;
            string fuel = catalog.Items.Where(p => p.Value.FuelValue > 0 && p.Value.FuelCategory is { } category && categories?.ContainsKey(category) == true
                    && catalog.Mining.Values.Any(products => products.Any(m => m.Name == p.Key && m.DeterministicItem)))
                .OrderByDescending(p => owned.Inventory.GetValueOrDefault(p.Key) > 0).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).First();
            int capacity = Math.Min(100, catalog.Items[fuel].StackSize);
            int target = (int)Math.Clamp(Math.Ceiling(expectedEnergy * 1.25 / catalog.Items[fuel].FuelValue), Math.Min(2, capacity), capacity);
            int missing = (int)Math.Max(0, target - owned.Entities.Single(e => e.Id == boiler.Id).Count("fuel", fuel));
            if (missing == 0) return;
            await new ProductionGoalExecutor(game, journal).RunAsync(fuel, missing, token);
            await controller.ApproachEntityAsync(boiler.Id, boiler.Position, catalog, token);
            var inserted = await controller.WorkAsync("insert", new { entityId = boiler.Id, inventory = "fuel", item = fuel, count = missing }, 600, token: token);
            Completed(inserted);
            await journal.AppendAsync("powered-machine-fuel", new { machineId, boilerId = boiler.Id, fuel, count = missing, expectedEnergy }, token);
        }
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
