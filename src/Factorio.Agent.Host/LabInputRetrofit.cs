using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Gives each laboratory built without an input side the chest and inserter its band slot now plans, so buses can feed it. Until
/// 2026-10-08 laboratories had no input side and the actor's tours filled them by hand. A slot whose planned tiles are occupied
/// is left as it is and reported.
/// </summary>
internal sealed class LabInputRetrofit(IGameClient game, IControllerJournal journal, string directory)
{
    private static readonly string[] Parts = ["input-chest", "input-inserter"];

    public async Task<int> RunAsync(CancellationToken token)
    {
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var registry = new FactoryRegistry(directory);
        var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
        var labs = Pending(state);
        if (labs.Count == 0) return 0;
        var equipment = FactoryCellBuilder.Equipment(catalog, labs[0].MachineItem);
        await new FactoryCellBuilder(game, journal, directory).EnsureCarriedAsync(registry, catalog,
            new Dictionary<string, int>(StringComparer.Ordinal) { [equipment.Inserter] = labs.Count, [equipment.Chest] = labs.Count }, token);
        await using var controller = new SpatialController(game, journal);
        int fitted = 0;
        foreach (var lab in labs)
        {
            var machine = lab.Plan!["machine"];
            var zone = state.Zones.Single(z => z.Id == lab.Zone);
            try { await controller.TravelAsync(machine.Position, 4, catalog, token); }
            catch (Exception error) when (error is NavigationPlanningException or EntityUnreachableException)
            {
                await journal.AppendAsync("lab-input-retrofit-skipped", new { lab.Id, reason = "unreachable" }, token);
                continue;
            }
            var map = await new SpatialClient(game).CaptureAsync([lab.MachineItem, equipment.Inserter, equipment.Chest, equipment.Pole], 32, token);
            if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor changed while fitting a laboratory.");
            var layout = new FactoryBandPlanner().Layout(map, equipment with { Machine = lab.MachineItem }, zone.Origin, lab.Slot, input: true, output: false,
                zone.TransportAccess);
            var parts = layout.Entities.Where(e => Parts.Contains(e.Role)).ToArray();
            var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
            if (layout.Role("machine")!.Position != machine.Position || parts.Length != Parts.Length
                || !parts.All(p => field.PlacementClear(map.Prototypes[map.Items[p.Item].EntityName], p.Position, p.Direction)))
            {
                await journal.AppendAsync("lab-input-retrofit-skipped", new { lab.Id, reason = "slot-occupied", parts }, token);
                continue;
            }
            var entities = new Dictionary<string, string>(lab.Entities, StringComparer.Ordinal);
            var plan = new Dictionary<string, PlannedEntity>(lab.Plan!, StringComparer.Ordinal);
            foreach (var part in parts.OrderBy(p => Array.IndexOf(Parts, p.Role)))
            {
                entities[part.Role] = await new PoweredMachineController(game, journal).BuildAtAsync(part.Item, new(part.Position, part.Direction, 0),
                    catalog, controller, token);
                plan[part.Role] = part;
                await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).With(lab with { Entities = entities, Plan = plan }), token);
            }
            await journal.AppendAsync("lab-input-retrofit", new { lab.Id, parts = parts.Select(p => new { p.Role, entityId = entities[p.Role], p.Position }) },
                token);
            fitted++;
        }
        return fitted;
    }

    /// <summary>Ready band laboratories with a recorded plan but no input chest yet.</summary>
    internal static IReadOnlyList<FactoryCell> Pending(FactoryState state) => state.Cells.Where(c => c.Kind == "lab" && c.Status == "ready"
        && c.Zone > 0 && !c.Entities.ContainsKey("input-chest") && c.Plan?.ContainsKey("machine") == true).ToArray();
}
