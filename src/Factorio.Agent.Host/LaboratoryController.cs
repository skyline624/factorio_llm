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
        await PreparePacksAsync(initial, lab);
        await using var controller = new SpatialController(game, journal);
        var power = new PoweredMachineController(game, journal);
        if (lab is null)
        {
            string installedId = await power.InstallAsync(labItem, catalog, controller, token);
            lab = (await ReadAsync()).Labs.Single(l => l.Id == installedId);
        }
        string labId = lab.Id;
        if (lab.NetworkId is null) throw new InvalidOperationException("The laboratory is not on an electric network.");
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
            if (state.Researched || (!force && state.Labs.Single(l => l.Id == labId).Energy > 0)) return;
            double energy = technology.Count * technology.EnergyTicks * labPrototype.EnergyPerTick / labPrototype.ResearchingSpeed;
            await power.MaintainFuelAsync(labId, energy, catalog, controller, force, token);
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
