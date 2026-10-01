using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture mirroring the live geometry: an energy interface east, an iron ore patch injected about sixty tiles west of it,
/// researched electric drills, electronics and assemblers, and supplied drills, stone furnaces, inserters, chests, belts, poles, one
/// assembler, coal and a worn survival kit. Builds a gear band cell beside the interface and a two-cell iron row on the patch, measures
/// one direct collection round, lets the director build the row's supply line, then measures a depot collection round over the same
/// wait in the same world. The line must conserve plates natively over a quiet window (furnace output equals the growth of row chests,
/// transit and depot), logistics must feed the gear cell from the depot and gears must be produced, and a destroyed trunk belt must be
/// rebuilt by maintenance at its plan, all without hand crafting. Not a campaign.
/// </summary>
public sealed class SupplyLineQualification(RuntimeSession session)
{
    private const string Plate = "iron-plate", Gear = "iron-gear-wheel";
    /// <summary>Ticks of production before each measured round: two minutes, about 75 plates from two electric-drill cells.</summary>
    private const int MeasuredWait = 7200;

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Supply line qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"supply-line-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        var artificial = new List<string>();
        object? travel = null;
        bool passed = false;
        try
        {
            // The mod accepts at most 128 bytes of reason.
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Injected ore, power, research, items, kit; destroyed trunk belt. Supply line test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            artificial.Add("mark_fixture before any preparation");
            var setup = await NativeAsync(Prepare);
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            artificial.AddRange([
                "chunks generated around the area; area (-96,-48)-(48,48) emptied of every entity and paved with grass, except a water stripe "
                    + "x -32..-29, y -6..13 across the straight path between the patch and the band; actor teleported to (24,0)",
                "electric-energy-interface injected at (20,0) with a small pole at (18.5,0.5)",
                "steam-power, electronics, automation and electric-mining-drill researched",
                "iron ore 14x16 injected at x -62..-48, y -8..8, 5000 per tile",
                "actor inventory cleared, then given 3 electric drills, 3 stone furnaces, 10 inserters, 6 iron chests, 40 small poles, "
                    + "150 belts, 1 assembler, 200 coal and 20 magazines; light armor worn, pistol and 10 magazines loaded so the survival kit crafts nothing",
                "factory registry deleted"]);
            evidence.Add(new { check = "explicit-supply-line-preparation", native = setup });
            Require(setup.GetProperty("ore").GetInt32() == 14 * 16, "The iron patch was not injected.");

            // The band first, beside the interface: then the row, sixty tiles west, links its poles to that network.
            var gearCell = await new FactoryCellBuilder(game, journal, session.Directory).BuildAsync("assembler", "assembling-machine-1", Gear, token);
            await TravelAsync(new(-55, 0));
            var director = new FactoryDirector(game, journal, session.Directory);
            var row = await director.EnsureRawAsync(Plate, 37.5, token, maximumNewCells: 2, explorationBudget: 0);
            var state = await LoadAsync();
            var smelters = state.Cells.Where(c => c.Kind == "smelter" && c.Recipe == Plate && c.Status == "ready").OrderBy(c => c.Slot.Index).ToArray();
            evidence.Add(new { check = "row-and-band-ready", gearCell, row, rows = state.Rows, smelters, state.Zones });
            Require(gearCell.Status == "ready" && smelters.Length == 2 && smelters.Select(c => c.Slot.Band).Distinct().Count() == 1,
                "The gear cell and a two-cell iron row were not completed.");
            var zone = state.Zones.Single(z => z.Id == gearCell.Zone);
            var walkway = FactoryBandPlanner.Walkway(zone.Origin, zone.Slots, zone.Pitch, zone.BandHeight);
            var resourceRow = state.Rows!.Single(r => r.Id == smelters[0].Slot.Band);
            var prototypes = await new SpatialClient(game).CaptureAsync(new[] { resourceRow.Equipment.Drill, resourceRow.Equipment.Chest,
                resourceRow.Equipment.Furnace, resourceRow.Equipment.Inserter, resourceRow.Equipment.Pole }.OfType<string>().ToArray(), 4, token);
            var rowWalkway = ResourceCellPlanner.Access(prototypes, resourceRow).Walkway;
            var bandEnd = SupplyLinePlanner.End(walkway, new((rowWalkway.Min.X + rowWalkway.Max.X) / 2, (rowWalkway.Min.Y + rowWalkway.Max.Y) / 2));

            // Priming: the first round fuels the furnaces from the bag; then a direct round is measured from the band end.
            var furnaces = smelters.Select(c => c.Entities["furnace"]).ToArray();
            var logistics = new FactoryLogistics(game, journal, session.Directory);
            var prime = await logistics.ServiceAsync(40, token);
            var direct = await MeasuredRoundAsync(Finished(await SnapshotAsync(), furnaces));
            evidence.Add(new { check = "direct-collection", prime, direct.Round, direct.Travel });
            Require(direct.Round.Collected.GetValueOrDefault(Plate) >= 20, "The direct round collected too few plates to measure.");

            // The director, not the fixture, decides and builds the line: the row is beyond the threshold and the band consumes plates.
            var built = await director.EnsureSupplyLineAsync(token);
            state = await LoadAsync();
            var gearsBefore = await NativeAsync(Read.Replace("ENTITY_ID", gearCell.Entities["machine"], StringComparison.Ordinal));
            evidence.Add(new { check = "supply-line-built", line = built, decision = await RowsAsync("supply-line-decision"), gearsBefore });
            if (built is not { Status: "ready", Kind: SupplyLinePlanner.Kind } line || line.Slot.Band != resourceRow.Id)
                throw new InvalidDataException("The director did not build a ready supply line for the iron row.");
            Require(smelters.All(c => line.Entities.ContainsKey(SupplyLinePlanner.FeederRole(c.Slot.Index))), "A ready row cell has no feeder.");
            var linePlan = line.Plan ?? throw new InvalidDataException("The line has no plan for maintenance.");
            Require(line.Entities.Keys.All(linePlan.ContainsKey), "A line part has no plan for maintenance.");
            // The trunk follows observed terrain: around the water stripe, longer than the straight run between its ends.
            var trunk = SupplyLines.FlowRoles(linePlan.Keys).Where(SupplyLines.IsTrunk).Select(r => linePlan[r].Position).ToArray();
            int straight = (int)(Math.Abs(trunk[^1].X - trunk[0].X) + Math.Abs(trunk[^1].Y - trunk[0].Y)) + 1;
            evidence.Add(new { check = "trunk-route", belts = trunk.Length, straight, from = trunk[0], to = trunk[^1],
                minY = trunk.Min(p => p.Y), maxY = trunk.Max(p => p.Y), onWater = trunk.Count(p => p.X is >= -32 and < -28 && p.Y is >= -6 and < 14) });
            Require(trunk.Length > straight && !trunk.Any(p => p.X is >= -32 and < -28 && p.Y is >= -6 and < 14),
                "The trunk did not route around the water stripe.");

            // Drain: the chests' backlog from the construction time first rides the belt to the depot (64 tiles take about 34 s at
            // native yellow belt speed), then one round empties the depot, so the measured round covers the same wait as the direct one.
            await WaitAsync(3600);
            var drain = await logistics.ServiceAsync(40, token);
            var drained = await SnapshotAsync();
            var collected = FactoryLogistics.CollectedChests(state.Cells.Where(c => c.Status == "ready").ToArray(), drained);
            evidence.Add(new { check = "depot-replaces-row-chests", drain, collected, depot = line.Entities[SupplyLinePlanner.DepotChestRole] });
            Require(collected.Contains(line.Entities[SupplyLinePlanner.DepotChestRole])
                && smelters.All(c => !collected.Contains(c.Entities["output-chest"])), "Logistics still empties the served row chests or skips the depot.");

            // Quiet window: no actor transfer; what the furnaces finish must reappear in row chests, transit and the depot.
            var before = await SnapshotAsync();
            await WaitAsync(1800);
            var after = await SnapshotAsync();
            var ledger = Ledger(before, after, smelters, line);
            evidence.Add(new { check = "native-line-conservation", ledger });
            Require(ledger.Produced > 0 && ledger.Produced == ledger.Held && ledger.Depot > 0,
                "Plates finished by the furnaces do not reappear exactly in the row chests, the line and the depot.");

            var served = await MeasuredRoundAsync(Finished(drained, furnaces));
            evidence.Add(new { check = "depot-collection", served.Round, served.Travel, served.Produced });
            Require(served.Round.Collected.GetValueOrDefault(Plate) >= 20, "The depot round collected too few plates to measure.");
            double directPerPlate = direct.Travel.Distance / direct.Round.Collected[Plate];
            double servedPerPlate = served.Travel.Distance / served.Round.Collected[Plate];
            // Plates collected also hold what was in transit when the window began, so travel is also given per plate the furnaces
            // finished in the window, read from their native counters.
            object Summary((LogisticsResult Round, Travel Travel, long Produced) measured, long since) => new
            {
                measured.Travel, plates = measured.Round.Collected[Plate], measured.Produced, productionTicks = measured.Round.Tick - since,
                tilesPerPlate = Math.Round(measured.Travel.Distance / measured.Round.Collected[Plate], 3),
                tilesPerProducedPlate = Math.Round(measured.Travel.Distance / Math.Max(1, measured.Produced), 3),
                plateTakesPerPlate = Math.Round((double)measured.Travel.PlateTakes / measured.Round.Collected[Plate], 4), actions = measured.Round.Actions
            };
            travel = new
            {
                waitTicks = MeasuredWait, start = bandEnd,
                direct = Summary(direct, prime.Tick), line = Summary(served, drain.Tick),
                ratio = Math.Round(servedPerPlate / directPerPlate, 3)
            };
            Require(servedPerPlate < directPerPlate / 2, "The depot round did not halve the actor's travel per collected plate.");

            // Gears: the gear cell is fed from depot plates by ordinary logistics and produces.
            await WaitAsync(1800);
            var gears = await logistics.ServiceAsync(40, token);
            var assembler = await NativeAsync(Read.Replace("ENTITY_ID", gearCell.Entities["machine"], StringComparison.Ordinal));
            evidence.Add(new { check = "gears-from-depot-plates", gears, gearsBefore, assembler });
            Require(assembler.GetProperty("finished").GetInt64() > gearsBefore.GetProperty("finished").GetInt64() && gears.Collected.GetValueOrDefault(Gear) > 0,
                "The gear cell produced no gear from depot plates.");

            // An attack stand-in: one trunk belt destroyed; the next round's maintenance rebuilds it from the line's plan.
            string trunkRole = line.Entities.Keys.Where(SupplyLines.IsTrunk).Order(StringComparer.Ordinal).ElementAt(line.Entities.Keys.Count(SupplyLines.IsTrunk) / 2);
            string lost = line.Entities[trunkRole];
            var destroyed = await NativeAsync(Destroy.Replace("ENTITY_ID", lost, StringComparison.Ordinal));
            artificial.Add($"after the measured rounds, the trunk belt {trunkRole} destroyed by fixture command");
            var repair = await logistics.ServiceAsync(40, token);
            var repaired = (await LoadAsync()).Cells.Single(c => c.Id == line.Id);
            string rebuilt = repaired.Entities[trunkRole];
            var standing = await NativeAsync(Read.Replace("ENTITY_ID", rebuilt, StringComparison.Ordinal));
            evidence.Add(new { check = "trunk-belt-rebuilt", trunkRole, lost, destroyed, maintenance = repair.Maintenance, rebuilt, standing });
            Require(rebuilt != lost && repair.Maintenance?.Rebuilt.Contains(rebuilt) == true
                && standing.GetProperty("x").GetDouble() == repaired.Plan![trunkRole].Position.X
                && standing.GetProperty("y").GetDouble() == repaired.Plan[trunkRole].Position.Y
                && standing.GetProperty("direction").GetInt32() == repaired.Plan[trunkRole].Direction, "Maintenance did not rebuild the trunk belt at its plan.");

            var submissions = (await RowsAsync("submission")).ToArray();
            int crafts = submissions.Count(s => s.GetProperty("kind").GetString() == "craft");
            int mines = submissions.Count(s => s.GetProperty("kind").GetString() == "mine");
            evidence.Add(new { check = "no-hand-crafting", crafts, mines, submissions = submissions.Length });
            Require(crafts == 0, "Something was crafted by hand.");
            passed = true;
            return path;

            // From the band end, after the same wait: the round's walking and transfers, and the plates finished since the given count.
            async Task<(LogisticsResult Round, Travel Travel, long Produced)> MeasuredRoundAsync(long since)
            {
                await TravelAsync(bandEnd);
                await WaitAsync(MeasuredWait);
                long finished = Finished(await SnapshotAsync(), furnaces);
                int start = (await File.ReadAllLinesAsync(journalPath, token)).Length;
                var round = await logistics.ServiceAsync(40, token);
                var rows = (await File.ReadAllLinesAsync(journalPath, token)).Skip(start).Select(Parse).ToArray();
                return (round, Measure(rows), finished - since);
            }
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-supply-line-qualification", passed, isAutonomousCampaign = false, travel, artificial,
                journalPath, evidence }, CancellationToken.None);
        }

        async Task TravelAsync(MapPosition destination)
        {
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            await using var controller = new SpatialController(game, journal);
            await controller.TravelAsync(destination, 2, catalog, token);
        }

        async Task WaitAsync(int ticks)
        {
            await using var controller = new SpatialController(game, journal);
            Require((await controller.WorkAsync("wait", new { ticks }, ticks + 300, token: token)).Status == "completed", "Wait failed.");
        }

        async Task<FactorySnapshot> SnapshotAsync() => await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);

        async Task<FactoryState> LoadAsync() => await new FactoryRegistry(session.Directory).LoadAsync((await SnapshotAsync()).Scope.WorldId, token);

        async Task<IEnumerable<JsonElement>> RowsAsync(string type) => (await File.ReadAllLinesAsync(journalPath, token)).Select(Parse)
            .Where(r => r.GetProperty("type").GetString() == type).Select(r => r.GetProperty("data"));

        async Task<JsonElement> NativeAsync(string command)
        {
            using var read = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(command, token));
            return read.RootElement.Clone();
        }
    }

    /// <summary>Walking measured from move receipts, the takes of plates and every chest transfer in one logistics round.</summary>
    public sealed record Travel(double Distance, int Moves, int PlateTakes, int Transfers);

    /// <summary>
    /// Plates the row's furnaces finished between two photographs, and the growth of every place a plate can be between a furnace and
    /// the depot: furnace outputs, output inserter hands, row chests, both lanes of every line belt, feeder and depot inserter hands,
    /// and the depot chest. Without actor transfers the two must be equal.
    /// </summary>
    public sealed record LineLedger(long Produced, long Held, long Depot, long Transit, long RowChests);

    internal static LineLedger Ledger(FactorySnapshot before, FactorySnapshot after, IReadOnlyList<FactoryCell> smelters, FactoryCell line)
    {
        var furnaces = smelters.Select(c => c.Entities["furnace"]).ToArray();
        var chests = smelters.Select(c => c.Entities["output-chest"]).ToArray();
        string depot = line.Entities[SupplyLinePlanner.DepotChestRole];
        var movers = smelters.Select(c => c.Entities["output-inserter"])
            .Concat(line.Entities.Where(e => !e.Key.StartsWith("link-", StringComparison.Ordinal) && e.Key != SupplyLinePlanner.DepotChestRole).Select(e => e.Value))
            .ToHashSet(StringComparer.Ordinal);
        long Stock(FactorySnapshot s, IEnumerable<string> ids) => ids.Sum(id => FactoryLogistics.Items(s, id).GetValueOrDefault(Plate));
        long Moving(FactorySnapshot s) => s.Records.Where(r => r.Kind == "transit" && movers.Contains(r.EntityId))
            .Sum(r => r.Data.GetProperty("items").TryGetProperty(Plate, out var count) ? count.GetInt64() : 0);
        long rowChests = Stock(after, chests) - Stock(before, chests), transit = Moving(after) - Moving(before);
        long depotGrowth = Stock(after, [depot]) - Stock(before, [depot]);
        long furnaceOutputs = Stock(after, furnaces) - Stock(before, furnaces);
        return new(Finished(after, furnaces) - Finished(before, furnaces), furnaceOutputs + rowChests + transit + depotGrowth, depotGrowth, transit, rowChests);
    }

    /// <summary>Products the furnaces finished in their lifetime, from the native counters of one photograph.</summary>
    internal static long Finished(FactorySnapshot snapshot, IEnumerable<string> furnaces) => furnaces.Sum(id =>
        snapshot.Records.Single(r => r.Kind == "work" && r.EntityId == id).Data.GetProperty("productsFinished").GetInt64());

    /// <summary>Distance walked from each move's submission position to its receipt position, plate takes and all transfers.</summary>
    internal static Travel Measure(IReadOnlyList<JsonElement> rows)
    {
        var starts = new Dictionary<string, MapPosition>(StringComparer.Ordinal);
        double distance = 0;
        int moves = 0, plateTakes = 0, transfers = 0;
        foreach (var row in rows)
        {
            var data = row.GetProperty("data");
            switch (row.GetProperty("type").GetString())
            {
                case "submission":
                    string kind = data.GetProperty("kind").GetString()!;
                    if (kind == "move")
                        starts[data.GetProperty("operationId").GetString()!] = data.GetProperty("preconditions").GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
                    transfers += kind is "take" or "insert" ? 1 : 0;
                    plateTakes += kind == "take" && data.GetProperty("args").GetProperty("item").GetString() == Plate ? 1 : 0;
                    break;
                case "receipt" when starts.Remove(data.GetProperty("operationId").GetString()!, out var start)
                    && data.GetProperty("effects").TryGetProperty("position", out var end):
                    distance += start.DistanceTo(end.Deserialize<MapPosition>(Protocol.Json)!);
                    moves++;
                    break;
            }
        }
        return new(Math.Round(distance, 2), moves, plateTakes, transfers);
    }

    private static JsonElement Parse(string line)
    {
        using var row = JsonDocument.Parse(line);
        return row.RootElement.Clone();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    // Generated chunks paved with grass, an energy interface east, an iron patch sixty tiles west and the construction items. The
    // worn light armor, loaded pistol and twenty spare magazines leave the survival kit nothing to craft from collected plates.
    private const string Prepare = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; s.request_to_generate_chunks({-24,0},5); s.force_generate_chunk_requests(); for _,e in pairs(s.find_entities_filtered{area={{-96,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-96,48 do for y=-48,48 do tiles[#tiles+1]={name=(x>=-32 and x<=-29 and y>=-6 and y<=13) and 'water' or 'grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({24,0})); c.health=c.max_health; c.get_main_inventory().clear(); for _,t in pairs{'steam-power','electronics','automation','electric-mining-drill'} do f.technologies[t].researched=true end; local armor=c.get_inventory(defines.inventory.character_armor); armor.clear(); assert(armor.insert{name='light-armor',count=1}==1); local guns=c.get_inventory(defines.inventory.character_guns); guns.clear(); assert(guns.insert{name='pistol',count=1}==1); local ammo=c.get_inventory(defines.inventory.character_ammo); ammo.clear(); assert(ammo.insert{name='firearm-magazine',count=10}==10); local main=c.get_main_inventory(); for name,count in pairs{['electric-mining-drill']=3,['stone-furnace']=3,inserter=10,['iron-chest']=6,['small-electric-pole']=40,['transport-belt']=150,['assembling-machine-1']=1,coal=200,['firearm-magazine']=20} do assert(main.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=2000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={18.5,0.5},force=f}); local ore=0; for x=-62,-49 do for y=-8,7 do assert(s.create_entity{name='iron-ore',position={x+0.5,y+0.5},amount=5000}); ore=ore+1 end end; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,ore=ore,plates=c.get_item_count('iron-plate'),players=#game.connected_players})
        """;

    // Where an entity stands, its direction and, for a crafting machine, its native finished products.
    private const string Read = """
        /silent-command local e; for _,x in pairs(game.surfaces.nauvis.find_entities_filtered{force=game.forces.factorio_agent}) do if x.unit_number and tostring(x.unit_number)=='ENTITY_ID' then e=x; break end end; assert(e and e.valid); local r={tick=game.tick,name=e.name,x=e.position.x,y=e.position.y,direction=e.direction}; if e.type=='assembling-machine' then r.finished=e.products_finished end; rcon.print(helpers.table_to_json(r))
        """;

    // One registered entity destroyed as an attack would.
    private const string Destroy = """
        /silent-command local e; for _,x in pairs(game.surfaces.nauvis.find_entities_filtered{force=game.forces.factorio_agent}) do if x.unit_number and tostring(x.unit_number)=='ENTITY_ID' then e=x; break end end; assert(e and e.valid); local r={tick=game.tick,name=e.name,x=e.position.x,y=e.position.y}; e.destroy(); rcon.print(helpers.table_to_json(r))
        """;
}
