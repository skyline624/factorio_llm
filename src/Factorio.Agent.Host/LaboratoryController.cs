using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record LaboratoryResult(string Technology, string LabId, long StartTick, long EndTick,
    int PoweredSamples, IReadOnlyDictionary<string, double> Consumed);

/// <summary>Executes one available lab technology with native stock, power and completion evidence.</summary>
public sealed class LaboratoryController(IGameClient game, IControllerJournal journal)
{
    public async Task<LaboratoryResult> RunAsync(string technologyName, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        var production = new ProductionController(game, journal);
        var executor = new ProductionGoalExecutor(game, journal);
        var technologies = await new TechnologyClient(game).ReadDependenciesAsync(technologyName, token);
        var initialScope = technologies.Scope;
        NativeTechnology technology = technologies.Technologies[technologyName];
        TechnologyStep next = new TechnologyPlanner().Next(technologyName, technologies.Technologies);
        if (next.Kind != "research" || next.Technology != technologyName)
            throw new InvalidOperationException("Prepare this technology's prerequisites before laboratory research.");
        ResearchSnapshot initial = await ReadAsync();
        RequireSelection(initial);
        ProductionCatalog catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        RequireScope(catalog.Scope);
        var compatible = initial.Prototypes.Where(p => technology.Ingredients.All(i => p.Value.Inputs.Contains(i.Name)))
            .OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
        if (compatible.Length == 0) throw new InvalidOperationException("No native lab accepts the required science packs.");
        ObservedLaboratory? lab = initial.Labs.FirstOrDefault(l => compatible.Any(p => p.Value.EntityName == l.Name) && l.NetworkId is not null);
        string labItem = lab is null ? compatible[0].Key : compatible.First(p => p.Value.EntityName == lab.Name).Key;
        LaboratoryPrototype labPrototype = initial.Prototypes[labItem];
        await journal.AppendAsync("laboratory-start", new { initial, technology, labItem }, token);
        if (lab is null) await executor.RunAsync(labItem, 1, token);
        await PreparePacksAsync(initial, lab);
        ProductionState factory = await production.ObserveAsync(token);
        RequireScope(factory.Scope);
        if (!factory.Entities.Any(e => catalog.Items.Values.Any(i => i.PlaceEntity == e.Name && i.PlaceEntityType == "electric-pole")))
        {
            await new SteamPowerController(game, journal).RunAsync(token);
            factory = await production.ObserveAsync(token);
            RequireScope(factory.Scope);
        }
        var spatial = new SpatialClient(game);
        await using var controller = new SpatialController(game, journal);
        var poleNames = catalog.Items.Values.Where(i => i.PlaceEntityType == "electric-pole").Select(i => i.PlaceEntity).ToHashSet(StringComparer.Ordinal);
        ProductionEntity poleEntity = factory.Entities.Where(e => poleNames.Contains(e.Name))
            .OrderBy(e => lab is null ? 0 : e.Position.DistanceTo(lab.Position)).ThenBy(e => e.Id, StringComparer.Ordinal).First();
        string poleItem = catalog.Items.First(p => p.Value.PlaceEntity == poleEntity.Name).Key;
        await controller.TravelAsync(lab?.Position ?? poleEntity.Position, 4, catalog, token);
        SpatialSnapshot map = await MapAsync();
        SpatialEntity pole = map.Entities.Single(e => e.Id == poleEntity.Id);
        if (lab is null)
        {
            PlacementCandidate? placement = new LaboratoryPlanner().Place(map, labItem, pole);
            if (placement is null)
            {
                LaboratoryExtension extension = new LaboratoryPlanner().Extend(map, labItem, poleItem, pole)
                    ?? throw new InvalidOperationException("No clear lab placement or bounded pole extension on the observed terrain.");
                await journal.AppendAsync("laboratory-pole-extension", new { map.Scope, map.CollectedTick, sourceId = pole.Id, poleItem, extension }, token);
                await executor.RunAsync(poleItem, 1, token);
                string poleId = await BuildAtAsync(poleItem, extension.Pole);
                map = await MapAsync();
                SpatialEntity connected = map.Entities.Single(e => e.Id == poleId);
                if (connected.Power?.NetworkId is null || connected.Power.NetworkId != map.Entities.Single(e => e.Id == pole.Id).Power?.NetworkId)
                    throw new InvalidOperationException("The added pole did not join the source network. Reconcile before constructing the lab.");
                pole = connected;
                placement = extension.Lab;
            }
            await journal.AppendAsync("laboratory-placement", new { map.Scope, map.CollectedTick, labItem, pole.Id, placement }, token);
            string id = await BuildAtAsync(labItem, placement);
            lab = (await ReadAsync()).Labs.Single(l => l.Id == id);
        }
        string labId = lab.Id;
        if (lab.NetworkId != pole.Power?.NetworkId || lab.NetworkId is null)
            throw new InvalidOperationException("The laboratory does not share the planned electric network.");
        await MaintainFuelAsync(force: true);
        await SupplyAsync();
        ResearchSnapshot beforeSelection = await ReadAsync();
        RequireSelection(beforeSelection);
        if (beforeSelection.Selected != technologyName)
            await ActAsync("research", new { technology = technologyName });
        int powered = 0;
        for (int attempt = 0; attempt < 1800; attempt++)
        {
            ResearchSnapshot state = await ReadAsync();
            ObservedLaboratory currentLab = state.Labs.Single(l => l.Id == labId);
            if (currentLab.NetworkId == lab.NetworkId && currentLab.Energy > 0) powered++;
            await journal.AppendAsync("laboratory-measurement", state, token);
            if (state.Researched)
            {
                if (powered == 0) throw new InvalidDataException("Research completed without a powered laboratory observation.");
                var consumed = state.Consumed.ToDictionary(p => p.Key, p => p.Value - initial.Consumed.GetValueOrDefault(p.Key), StringComparer.Ordinal);
                if (consumed.Values.Any(v => v < 0)) throw new InvalidDataException("Native science consumption regressed.");
                var result = new LaboratoryResult(technologyName, labId, initial.CollectedTick, state.CollectedTick, powered, consumed);
                await journal.AppendAsync("laboratory-result", result, token);
                return result;
            }
            RequireSelection(state);
            if (state.Selected != technologyName) throw new InvalidOperationException("The selected native research changed; reconcile before continuing.");
            if (attempt % 10 == 0)
            {
                await MaintainFuelAsync(force: false);
                await SupplyAsync();
            }
            await ActAsync("wait", new { ticks = 60 });
        }
        throw new TimeoutException("Laboratory research exhausted its observation budget.");

        async Task<ResearchSnapshot> ReadAsync()
        {
            ResearchSnapshot value = ResearchSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("research_state", new { technology = technologyName }), token), technologyName);
            RequireScope(value.Scope);
            return value;
        }
        async Task<SpatialSnapshot> MapAsync()
        {
            var value = await spatial.CaptureAsync([labItem, poleItem], 48, token);
            RequireScope(value.Scope);
            return value;
        }
        async Task<string> BuildAtAsync(string item, PlacementCandidate placement)
        {
            await controller.TravelAsync(placement.Position, 8, catalog, token);
            SpatialSnapshot current = await MapAsync();
            MapPosition approach = new PlacementPlanner().FindApproach(new(current), item, placement)
                ?? throw new InvalidOperationException("No reachable approach outside the planned construction footprint.");
            await controller.NavigateAsync(approach, .2, token);
            PlacementValidation validation = await spatial.ValidateAsync(initialScope, item, [placement], token);
            if (!validation.Candidates[0].Allowed || !validation.Candidates[0].InReach)
                throw new InvalidOperationException("The engine refused the calculated placement; reconcile partial construction.");
            OperationReceipt built = await ActAsync("build", new { item, placement.Position, placement.Direction });
            return built.Effects.GetProperty("entityId").GetString()!;
        }
        async Task<OperationReceipt> ActAsync(string kind, object args)
        {
            OperationReceipt receipt = await controller.WorkAsync(kind, args, 36000, token: token);
            if (receipt.Status != "completed") throw new InvalidOperationException($"Research action {kind} ended with {receipt.Status}: {receipt.Error?.Code}. Reconcile partial effects.");
            return receipt;
        }
        async Task PreparePacksAsync(ResearchSnapshot state, ObservedLaboratory? selectedLab)
        {
            var available = new Dictionary<string, double>(state.ActorScienceUnits, StringComparer.Ordinal);
            if (selectedLab is not null)
                foreach (var pack in selectedLab.ScienceUnits) available[pack.Key] = available.GetValueOrDefault(pack.Key) + pack.Value;
            var required = LaboratoryPlanner.RequiredPacks(technology, state.Progress, available);
            foreach (var pack in required.Where(p => p.Value > 0))
                await executor.RunAsync(pack.Key, checked((int)state.ActorItems.GetValueOrDefault(pack.Key) + pack.Value), token);
        }
        async Task SupplyAsync()
        {
            ResearchSnapshot state = await ReadAsync();
            if (state.Researched) return;
            ObservedLaboratory currentLab = state.Labs.Single(l => l.Id == labId);
            var required = LaboratoryPlanner.RequiredPacks(technology, state.Progress, currentLab.ScienceUnits);
            ProductionState carried = await production.ObserveAsync(token);
            RequireScope(carried.Scope);
            foreach (var pack in required.Where(p => p.Value > 0))
            {
                int count = (int)Math.Min(pack.Value, Math.Min(carried.Inventory.GetValueOrDefault(pack.Key),
                    catalog.Items[pack.Key].StackSize - currentLab.Items.GetValueOrDefault(pack.Key)));
                if (count <= 0) continue;
                await controller.TravelAsync(currentLab.Position, 3, catalog, token);
                await ActAsync("insert", new { entityId = labId, inventory = "lab", item = pack.Key, count });
            }
        }
        async Task MaintainFuelAsync(bool force)
        {
            ResearchSnapshot state = await ReadAsync();
            var currentLab = state.Labs.Single(l => l.Id == labId);
            if (state.Researched || (!force && currentLab.Energy > 0)) return;
            await controller.TravelAsync(currentLab.Position, 4, catalog, token);
            map = await MapAsync();
            var generators = map.Entities.Where(e => map.Prototypes[e.Name].Type == "generator" && e.Power?.NetworkId == currentLab.NetworkId).ToArray();
            ProductionState owned = await production.ObserveAsync(token);
            RequireScope(owned.Scope);
            SpatialEntity? boiler = map.Entities.FirstOrDefault(e => map.Prototypes[e.Name].Type == "boiler" && owned.Entities.Any(o => o.Id == e.Id)
                && generators.Any(g => e.FluidConnections?.Any(f => f.TargetEntityId == g.Id) == true || g.FluidConnections?.Any(f => f.TargetEntityId == e.Id) == true));
            if (boiler is null)
            {
                if (currentLab.Energy > 0) return;
                throw new InvalidOperationException("No observed fuel-maintainable steam supply for the unpowered lab.");
            }
            var categories = map.Prototypes[boiler.Name].FuelCategories;
            string fuel = catalog.Items.Where(p => p.Value.FuelValue > 0 && p.Value.FuelCategory is { } category && categories?.ContainsKey(category) == true
                    && catalog.Mining.Values.Any(products => products.Any(p2 => p2.Name == p.Key && p2.DeterministicItem)))
                .OrderByDescending(p => owned.Inventory.GetValueOrDefault(p.Key) > 0).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).First();
            double energy = technology.Count * technology.EnergyTicks * labPrototype.EnergyPerTick / labPrototype.ResearchingSpeed;
            int reserve = (int)Math.Clamp(Math.Ceiling(energy * 1.25 / catalog.Items[fuel].FuelValue), 2, 100);
            long installedFuel = owned.Entities.Single(e => e.Id == boiler.Id).Count("fuel", fuel);
            int missing = Math.Max(0, reserve - (int)Math.Min(int.MaxValue, installedFuel));
            if (missing == 0) return;
            await executor.RunAsync(fuel, missing, token);
            await controller.TravelAsync(boiler.Position, 3, catalog, token);
            await ActAsync("insert", new { entityId = boiler.Id, inventory = "fuel", item = fuel, count = missing });
            await journal.AppendAsync("laboratory-fuel", new { boiler.Id, fuel, count = missing, energyEstimate = energy }, token);
        }
        void RequireScope(ActorScope scope)
        {
            if (scope != initialScope) throw new InvalidDataException("Actor identity changed during laboratory research; reconcile partial effects.");
        }
        void RequireSelection(ResearchSnapshot state)
        {
            if (state.Selected is not null && state.Selected != technologyName)
                throw new InvalidOperationException("Another technology is selected. Reconcile research before replacing it.");
        }
    }
}
