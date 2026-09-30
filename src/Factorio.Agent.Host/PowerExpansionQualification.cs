using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: a shore, supplied steam construction items and coal, and a script-configured electric load.
/// Proves that measured demand grows the first steam supply to several boilers and engines, that a unit interrupted
/// after its boiler is completed with that dry boiler lit and all its engines joined, that a factory band
/// placed beside the installation leaves its reserved growth free, that every boiler and engine produces under load,
/// and that factory logistics refills the boiler feeder chests. Not a campaign.
/// </summary>
public sealed class PowerExpansionQualification(RuntimeSession session)
{
    /// <summary>2.4 MW: more than one boiler can heat, less than 80 % of two complete boilers.</summary>
    public const double LoadPerTick = 40000;

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Power expansion qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"power-expansion-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Shore tiles, steam and cell construction items, 400 coal and a configured electric load. Power expansion test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name=(y<=-9) and 'water' or 'grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); for _,t in pairs{'steam-power','electronics','automation'} do f.technologies[t].researched=true end; for name,count in pairs{['offshore-pump']=1,boiler=3,['steam-engine']=6,['small-electric-pole']=40,inserter=6,['iron-chest']=6,['assembling-machine-1']=1,coal=400} do assert(c.insert{name=name,count=count}==count) end; local load=s.create_entity{name='electric-energy-interface',position={24,20},force=f}; assert(load); load.power_production=0; load.power_usage=0; load.electric_buffer_size=0; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,load=load.unit_number,position=load.position})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            evidence.Add(new { check = "explicit-power-preparation", native = setup.RootElement.Clone() });
            string loadId = setup.RootElement.GetProperty("load").GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var loadPosition = setup.RootElement.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;

            var steam = await new SteamPowerController(game, journal).RunAsync(token);
            evidence.Add(new { check = "first-steam-supply", steam });

            var power = new PowerExpansionController(game, journal, session.Directory);
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            await using (var controller = new SpatialController(game, journal))
            {
                await controller.TravelAsync(loadPosition, 8, catalog, token);
                await power.LinkAsync(loadId, steam.Entities["engine"], await power.ItemsAsync(token), catalog, controller, token);
            }
            string configure = FormattableString.Invariant($"/silent-command local e=game.surfaces.nauvis.find_entity('electric-energy-interface',{{{loadPosition.X},{loadPosition.Y}}}); assert(e and e.valid and e.unit_number=={loadId}); e.power_usage={LoadPerTick}; e.electric_buffer_size={LoadPerTick}; rcon.print(helpers.table_to_json{{tick=game.tick,network=e.electric_network_id,usage=e.power_usage}})");
            using (var load = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(configure, token)))
                evidence.Add(new { check = "configured-load", native = load.RootElement.Clone() });

            var expansion = await power.EnsureCapacityAsync(0, token)
                ?? throw new InvalidDataException("No steam network was observed for expansion.");
            var grown = await power.ObserveAsync(token);
            var network = grown.Main()!;
            var generators = network.Sources.Where(s => s.Type == "generator").ToArray();
            var boilers = grown.Boilers.Where(b => b.GeneratorIds.Any(id => generators.Any(g => g.Id == id))).ToArray();
            evidence.Add(new { check = "demand-driven-expansion", expansion, budget = grown.Budget(network), boilers = boilers.Length, engines = generators.Length });
            Require(expansion.Before.Exceeded() && !expansion.After.Exceeded(), "Measured demand did not drive expansion to enough capacity.");
            Require(boilers.Length >= 2 && generators.Length >= 4, "The installation did not grow to at least two boilers and four engines.");
            Require(expansion.PowerCells.Count >= boilers.Length, "Not every boiler became a chest-fed power cell.");

            // An interrupted unit: only its boiler is built, never fuelled nor fed, since no engine of it reaches the network.
            var items = await power.ItemsAsync(token);
            var partial = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var controller = new SpatialController(game, journal))
            {
                var (map, force) = await CaptureNearAsync(controller, boilers.OrderBy(b => b.Id, StringComparer.Ordinal).First().Id, grown);
                var zones = (await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token)).Zones;
                var unit = new PowerExpansionPlanner().Next(PowerExpansionController.Planning(map, zones, items.Pole), items.Boiler, items.Engine, force);
                Require(unit?.Kind == "unit", "The next expansion step is not a new unit.");
                partial["boiler"] = await new PoweredMachineController(game, journal).BuildAtAsync(unit!.Machines[0].Item, unit.Machines[0].Placement,
                    catalog, controller, token, unit.Machines.Skip(1).Select(m => m.Placement.Position).ToArray());
                evidence.Add(new { check = "interrupted-unit", unit, partial });
            }

            // The factory director asks the same question before building cells: enough planned assemblers to pass 80 % must grow power first.
            grown = await power.ObserveAsync(token);
            network = grown.Main()!;
            var budget = grown.Budget(network);
            var cellMap = await new SpatialClient(game).CaptureAsync(["assembling-machine-1", "inserter"], 4, token);
            double perCell = PowerExpansionController.CellDemand(cellMap, FactoryCellBuilder.Equipment(catalog, "assembling-machine-1"), io: true);
            int cells = (int)Math.Floor((PowerBudget.Headroom * budget.CapacityPerTick - budget.DemandPerTick) / perCell) + 1;
            var projected = await power.EnsureCapacityForCellsAsync("assembling-machine-1", cells, true, token)
                ?? throw new InvalidDataException("No steam network was observed for projected cells.");
            evidence.Add(new { check = "projected-cell-demand", cells, perCell, projected });
            Require(projected.Before.Exceeded(projected.AdditionalPerTick) && projected.Steps.Count >= 1 && !projected.After.Exceeded(projected.AdditionalPerTick),
                "Projected cell demand did not grow power before the cells were built.");
            var completed = projected.Steps[0];
            Require(completed.Kind == "complete" && completed.AnchorBoilerId == partial["boiler"] && completed.Engines.Count == 2,
                "The interrupted unit's dry boiler was not completed with generating engines.");
            grown = await power.ObserveAsync(token);
            network = grown.Main()!;
            generators = network.Sources.Where(s => s.Type == "generator").ToArray();
            boilers = grown.Boilers.Where(b => b.GeneratorIds.Any(id => generators.Any(g => g.Id == id))).ToArray();
            string[] chests = projected.PowerCells.Select(c => c.Entities["input-chest"]).ToArray();
            Require(chests.Length >= boilers.Length, "Not every boiler became a chest-fed power cell after projected growth.");

            // A factory band placed next to the installation must leave all its reserved units buildable.
            var cell = await new FactoryCellBuilder(game, journal, session.Directory).BuildAsync("assembler", "assembling-machine-1", "iron-gear-wheel", token);
            await using (var controller = new SpatialController(game, journal))
            {
                var (map, force) = await CaptureNearAsync(controller, boilers.OrderBy(b => b.Id, StringComparer.Ordinal).First().Id, grown);
                var zones = (await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token)).Zones;
                var growth = new PowerExpansionPlanner().Growth(PowerExpansionController.Planning(map, zones, items.Pole), items.Boiler, items.Engine, force,
                    PowerExpansionController.ReservedUnits);
                int units = growth.Count(e => map.Prototypes[e.Name].Type == "boiler");
                evidence.Add(new { check = "band-leaves-steam-growth", cell, zones, units, growth = growth.Select(e => new { e.Name, e.Position, e.Direction }) });
                Require(units == PowerExpansionController.ReservedUnits, "The factory band blocks the installation's reserved growth.");
            }

            var logistics = new FactoryLogistics(game, journal, session.Directory);
            var fill = await logistics.ServiceAsync(40, token);
            var filled = await ChestsAsync();
            await using (var controller = new SpatialController(game, journal))
                Require((await controller.WorkAsync("wait", new { ticks = 3600 }, 3900, token: token)).Status == "completed", "Wait failed.");
            var running = await power.ObserveAsync(token);
            var drained = await ChestsAsync();
            var refill = await logistics.ServiceAsync(40, token);
            var refilled = await ChestsAsync();
            var runningNetwork = running.Main()!;
            var runningBoilers = running.Boilers.Where(b => boilers.Any(o => o.Id == b.Id)).ToArray();
            evidence.Add(new { check = "generation-under-load", runningNetwork, runningBoilers });
            evidence.Add(new { check = "feeder-logistics", fill, filled, drained, refill, refilled });
            Require(runningNetwork.Sources.Where(s => s.Type == "generator").All(s => s.GeneratedLastTick > 0), "An engine did not generate under load.");
            Require(runningBoilers.Length == boilers.Length && runningBoilers.All(b => b.Status == "working"), "A boiler was not working under load.");
            // One boiler heats at most 1.8 MW; a larger measured production needs several boilers at once.
            Require(runningNetwork.Statistics?.Production > boilers.Max(b => b.EnergyPerTick * b.Effectivity), "Production did not exceed one boiler's heat.");
            Require(chests.All(c => drained[c] < filled[c] && refilled[c] > drained[c]) && refill.Supplied.GetValueOrDefault("coal") > 0,
                "Feeders did not draw from their chests or logistics did not refill them.");

            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var kinds = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data").GetProperty("kind").GetString()).ToArray();
                evidence.Add(new { check = "no-manual-production", crafts = kinds.Count(k => k == "craft"), mines = kinds.Count(k => k == "mine"),
                    builds = kinds.Count(k => k == "build"), transfers = kinds.Count(k => k is "insert" or "take") });
                Require(kinds.Count(k => k == "craft") == 0, "Supplied construction triggered manual crafting.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;

            async Task<(SpatialSnapshot Map, string Force)> CaptureNearAsync(SpatialController controller, string boilerId, PowerState state)
            {
                await controller.TravelAsync(state.Boilers.Single(b => b.Id == boilerId).Position, 6, catalog, token);
                var map = await new SpatialClient(game).CaptureAsync(items.All, 48, token);
                return (map, map.Entities.Single(e => e.Id == map.Actor.Id).Force);
            }

            async Task<Dictionary<string, long>> ChestsAsync()
            {
                var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                return chests.ToDictionary(c => c, c => FactoryLogistics.Items(snapshot, c).GetValueOrDefault("coal"), StringComparer.Ordinal);
            }
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-power-expansion-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
