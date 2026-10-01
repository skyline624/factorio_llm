using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: an injected energy interface, researched electronics, automation and rocket-silo technologies, and a supplied
/// silo, inserters, chests and poles. Proves that automating rocket parts plans the silo as a stage and builds its persistent cell;
/// then, once the fixture has artificially set the built silo one part short of a rocket and given the actor exactly the last part's
/// ingredients, that factory logistics feeds them through the cell's inserter, the silo finishes the rocket and C# launches it, with
/// an independent read of the engine's counter and nothing inserted into the silo by hand. The next logistics round rebuilds a cell
/// inserter the fixture destroyed, at its plan. Finally, with the cell removed and a second silo given, the launch path builds a
/// cell itself and, without any ingredient, stops at procurement. Not a campaign.
/// </summary>
public sealed class SiloCellQualification(RuntimeSession session)
{
    private const string Part = "rocket-part", SiloItem = "rocket-silo";

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Silo cell qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"silo-cell-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        var artificial = new List<string>();
        bool passed = false;
        try
        {
            // The mod accepts at most 128 bytes of reason.
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Injected power, research, cell items; parts set one short, ingredients given, cell removed, 2nd silo. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            artificial.Add("mark_fixture before any preparation");
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(Prepare, token));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            artificial.AddRange([
                "area (-48,-48)-(48,48) emptied and paved with grass, actor teleported to (0,0) with an emptied inventory",
                "electric-energy-interface injected at (-20,0) with a small pole at (-18.5,0.5)",
                "every technology set unresearched, then only electronics, automation and rocket-silo researched and their effects reapplied",
                "1 rocket-silo, 2 inserters, 2 iron chests and 12 small poles inserted into the actor",
                "factory registry deleted"]);
            evidence.Add(new { check = "explicit-silo-preparation", native = setup.RootElement.Clone() });
            Require(setup.RootElement.GetProperty("partRecipe").GetBoolean(), "The rocket-part recipe is not enabled.");

            var plan = await new FactoryDirector(game, journal, session.Directory).AutomateAsync(Part, 1, token);
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var registered = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            evidence.Add(new { check = "silo-automation", plan, registered.Cells, registered.Zones, registered.Targets });
            var stage = plan.Stages.SingleOrDefault();
            // Only the silo is researched here: every ingredient stays raw for procurement, and is reported so.
            Require(stage is { Kind: SiloCellPlanner.Kind, Recipe: Part, MachineItem: SiloItem, Machines: 1 },
                "Rocket parts were not planned as one silo stage.");
            Require(plan.RawPerMinute.Keys.Order(StringComparer.Ordinal).SequenceEqual(["low-density-structure", "processing-unit", "rocket-fuel"])
                && plan.RawPerMinute.Values.All(rate => Math.Abs(rate - 10) < 1e-9), "The part ingredients were not reported raw at ten a minute.");
            var cell = registered.Cells.SingleOrDefault(c => c.Kind == SiloCellPlanner.Kind && c.Status == "ready")
                ?? throw new InvalidDataException("No ready silo cell was built.");
            Require(Complete(cell), "The silo cell is not exactly a planned silo, chest, inserter and pole.");
            Require(registered.Zones.Single(z => z.Id == cell.Zone).Slots == SiloCellPlanner.MaximumCells, "The silo does not own a one-slot band.");
            string silo = cell.Entities["machine"], chest = cell.Entities["input-chest"], arm = cell.Entities["input-inserter"];

            // Artificial: the silo already holds all but one part, and the actor exactly what the last part consumes.
            using var shortcut = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(Ids(SetParts, silo, chest, arm), token));
            var set = shortcut.RootElement.Clone();
            artificial.AddRange([
                $"after the cell was built, the silo's rocket_parts set from 0 to {set.GetProperty("parts").GetInt32()} (native requirement {set.GetProperty("required").GetInt32()})",
                "the actor, carrying none of them, given exactly the native ingredients of one rocket-part"]);
            evidence.Add(new { check = "artificial-last-part", native = set });
            Require(set.GetProperty("parts").GetInt32() == set.GetProperty("required").GetInt32() - 1, "The silo was not set one part short of a rocket.");
            Require(Feeds(set, "armDrop", "armPickup", silo, chest), "The cell inserter does not take from its chest and drop into its silo natively.");
            var given = set.GetProperty("given").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);

            var result = await new RocketLaunchController(game, journal, session.Directory).RunAsync(SiloItem, token);
            using var after = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(Ids(Proof, silo, chest, arm), token));
            var native = after.RootElement.Clone();
            var rows = await RowsAsync();
            int intoSilo = Submissions(rows).Count(s => Into(s, silo));
            var intoChest = Submissions(rows).Where(s => Into(s, chest)).GroupBy(s => s.GetProperty("args").GetProperty("item").GetString()!)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.GetProperty("args").GetProperty("count").GetInt64()), StringComparer.Ordinal);
            int crafts = Submissions(rows).Count(s => s.GetProperty("kind").GetString() == "craft");
            int mines = Submissions(rows).Count(s => s.GetProperty("kind").GetString() == "mine");
            evidence.Add(new { check = "native-silo-cell-launch", result, native, given, intoChest, intoSilo, crafts, mines });
            Require(result.SiloId == silo && result.RocketsAfter == set.GetProperty("rocketsLaunched").GetInt64() + 1
                && native.GetProperty("rocketsLaunched").GetInt64() == result.RocketsAfter, "Exactly one more launch from the cell's silo was not independently confirmed.");
            Require(native.GetProperty("finished").GetInt64() == set.GetProperty("finished").GetInt64() + 1, "The silo did not craft exactly the last part natively.");
            Require(intoSilo == 0, "An ingredient was inserted into the cell's silo by hand.");
            Require(given.All(p => intoChest.GetValueOrDefault(p.Key) == p.Value), "Logistics did not deliver exactly the given ingredients to the cell chest.");
            Require(given.Keys.All(item => native.GetProperty("actor").GetProperty(item).GetInt64() == 0
                && native.GetProperty("chest").GetProperty(item).GetInt64() == 0 && native.GetProperty("silo").GetProperty(item).GetInt64() == 0)
                && native.GetProperty("hand").GetInt64() == 0, "The given ingredients were not all consumed through the cell.");
            Require(crafts == 0 && mines == 0, "The silo cell run crafted or mined by hand.");
            // The chest keeps ten minutes of the planned part a minute; what the bag lacked for it is reported short.
            int buffered = FactoryLogistics.BufferCrafts(FactoryLogistics.CellShares(catalog, registered), Part, 40);
            var round = rows.First(r => r.GetProperty("type").GetString() == "silo-cell-supply").GetProperty("data");
            long Count(string property, string item) => round.GetProperty(property).TryGetProperty(item, out var value) ? value.GetInt64() : 0;
            evidence.Add(new { check = "planned-silo-buffer", buffered, round });
            Require(buffered == 10 && given.All(p => Count("supplied", p.Key) == p.Value && Count("shortfall", p.Key) == (buffered - 1) * p.Value),
                "The silo chest was not refilled toward its planned buffer with the shortfall reported.");

            // Artificial: the cell's inserter is destroyed; the next logistics round rebuilds it at its plan from the carried spare.
            using var cut = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(Ids(DestroyArm, silo, chest, arm), token));
            artificial.Add("after the launch, the cell's input inserter destroyed by fixture command, one spare inserter still carried");
            var repair = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(40, token);
            string rebuilt = (await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token)).Cells.Single(c => c.Id == cell.Id)
                .Entities["input-inserter"];
            using var rewired = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(Ids(Arm, silo, chest, rebuilt), token));
            var planned = cell.Plan!["input-inserter"];
            var standing = rewired.RootElement.Clone();
            evidence.Add(new { check = "destroyed-inserter-rebuilt", destroyed = cut.RootElement.Clone(), repair, rebuilt, planned, native = standing });
            Require(rebuilt != arm && repair.Maintenance?.Rebuilt.SequenceEqual([rebuilt]) == true, "Maintenance did not rebuild the destroyed cell inserter.");
            Require(Feeds(standing, "drop", "pickup", silo, chest) && standing.GetProperty("x").GetDouble() == planned.Position.X
                && standing.GetProperty("y").GetDouble() == planned.Position.Y && standing.GetProperty("direction").GetInt32() == planned.Direction,
                "The rebuilt inserter does not feed the silo from the chest at its planned place.");
            Require(repair.Collected.Count == 0, "Logistics collected an item from the silo cell.");

            // Artificial: the whole cell and the registry are removed and a second silo given. With no silo standing, the launch path
            // builds a cell itself; with no ingredient anywhere it stops at procurement rather than feeding the silo by hand.
            using var removed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                Ids(RemoveCell, silo, chest, rebuilt).Replace("POLE_ID", cell.Entities["pole"], StringComparison.Ordinal), token));
            File.Delete(new FactoryRegistry(session.Directory).Path);
            artificial.AddRange([
                "after the rebuild, the cell's silo, chest, inserter and pole destroyed by fixture command and the registry deleted",
                "a second rocket-silo and one inserter inserted into the actor"]);
            string? stop = null;
            try { await new RocketLaunchController(game, journal, session.Directory).RunAsync(SiloItem, token); }
            catch (InvalidOperationException error) { stop = error.Message; }
            var own = (await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token)).Cells
                .SingleOrDefault(c => c.Kind == SiloCellPlanner.Kind && c.Status == "ready");
            rows = await RowsAsync();
            var start = rows.Last(r => r.GetProperty("type").GetString() == "rocket-start").GetProperty("data");
            int handFed = own is null ? -1 : Submissions(rows).Count(s => Into(s, own.Entities["machine"]));
            evidence.Add(new { check = "launch-built-cell", removed = removed.RootElement.Clone(), stop, own, start, handFed });
            Require(own is not null && Complete(own) && start.GetProperty("cell").GetString() == own.Id
                && start.GetProperty("siloId").GetString() == own.Entities["machine"], "The launch path did not build and adopt its own silo cell.");
            Require(stop?.StartsWith("unsupported:", StringComparison.Ordinal) == true && handFed == 0,
                "Without ingredients the launch did not stop at procurement, or fed its cell's silo by hand.");
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-silo-cell-qualification", passed, isAutonomousCampaign = false, artificial,
                journalPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement[]> RowsAsync() => (await File.ReadAllLinesAsync(journalPath, token)).Select(line =>
        {
            using var row = JsonDocument.Parse(line);
            return row.RootElement.Clone();
        }).ToArray();
    }

    /// <summary>Exactly a silo, its chest, inserter and pole, besides power links, each with the plan maintenance rebuilds it from.</summary>
    private static bool Complete(FactoryCell cell) => cell.Plan is not null && cell.Entities.Keys.All(cell.Plan.ContainsKey)
        && cell.Entities.Keys.Where(role => !role.StartsWith("link-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .SequenceEqual(["input-chest", "input-inserter", "machine", "pole"]);

    private static IEnumerable<JsonElement> Submissions(IEnumerable<JsonElement> rows) =>
        rows.Where(r => r.GetProperty("type").GetString() == "submission").Select(r => r.GetProperty("data"));

    private static bool Into(JsonElement submission, string entityId) => submission.GetProperty("kind").GetString() == "insert"
        && submission.GetProperty("args").GetProperty("entityId").GetString() == entityId;

    // Lua drops nil targets from the JSON.
    private static bool Feeds(JsonElement arm, string drop, string pickup, string silo, string chest) =>
        arm.TryGetProperty(drop, out var dropping) && dropping.GetString() == silo && arm.TryGetProperty(pickup, out var picking) && picking.GetString() == chest;

    private static string Ids(string command, string silo, string chest, string arm) => command.Replace("SILO_ID", silo, StringComparison.Ordinal)
        .Replace("CHEST_ID", chest, StringComparison.Ordinal).Replace("ARM_ID", arm, StringComparison.Ordinal);

    // Grass around the actor, an energy interface west of it, both silo recipes and the cell's parts; no rocket ingredient. Research is
    // set explicitly so earlier fixtures on the same server (verify-rocket researches everything) cannot add chains to the plan.
    private const string Prepare = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); for _,p in pairs(game.connected_players) do assert(p.character==c) end; game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); f.cancel_current_research(); for _,t in pairs(f.technologies) do t.researched=false end; for _,t in pairs{'electronics','automation','rocket-silo'} do f.technologies[t].researched=true end; f.reset_technology_effects(); for name,count in pairs{['rocket-silo']=1,inserter=2,['iron-chest']=2,['small-electric-pole']=12} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=100000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,rocketsLaunched=f.rockets_launched,siloRecipe=f.recipes['rocket-silo'].enabled,partRecipe=f.recipes['rocket-part'].enabled,players=#game.connected_players,gameSpeed=game.speed})
        """;

    // The built silo one part short of a rocket, and the actor given exactly one part's native ingredients.
    private const string SetParts = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local function find(id) for _,e in pairs(s.find_entities_filtered{force=f}) do if e.unit_number and tostring(e.unit_number)==id then return e end end end; local silo,chest,arm=find('SILO_ID'),find('CHEST_ID'),find('ARM_ID'); assert(silo and chest and arm and silo.rocket_parts==0 and silo.rocket_silo_status==defines.rocket_silo_status.building_rocket); local required=silo.prototype.rocket_parts_required; silo.rocket_parts=required-1; local given={}; for _,i in pairs(f.recipes[silo.prototype.fixed_recipe].ingredients) do assert(i.type=='item' and c.get_item_count(i.name)==0 and chest.get_item_count(i.name)==0 and c.insert{name=i.name,count=i.amount}==i.amount); given[i.name]=i.amount end; rcon.print(helpers.table_to_json{tick=game.tick,required=required,parts=silo.rocket_parts,finished=silo.products_finished,rocketsLaunched=f.rockets_launched,given=given,armDrop=arm.drop_target and tostring(arm.drop_target.unit_number),armPickup=arm.pickup_target and tostring(arm.pickup_target.unit_number),siloNetwork=silo.electric_network_id,players=#game.connected_players})
        """;

    // Independent native read after the launch: counter, silo, chest, inserter hand and actor.
    private const string Proof = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local function find(id) for _,e in pairs(s.find_entities_filtered{force=f}) do if e.unit_number and tostring(e.unit_number)==id then return e end end end; local silo,chest,arm=find('SILO_ID'),find('CHEST_ID'),find('ARM_ID'); local r={tick=game.tick,rocketsLaunched=f.rockets_launched,parts=silo.rocket_parts,finished=silo.products_finished,hand=arm.held_stack.valid_for_read and arm.held_stack.count or 0,actor={},chest={},silo={},players=#game.connected_players}; for _,i in pairs(f.recipes[silo.prototype.fixed_recipe].ingredients) do r.actor[i.name]=c.get_item_count(i.name); r.chest[i.name]=chest.get_item_count(i.name); r.silo[i.name]=silo.get_item_count(i.name) end; rcon.print(helpers.table_to_json(r))
        """;

    // The cell's inserter destroyed as an attack would.
    private const string DestroyArm = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local e; for _,i in pairs(s.find_entities_filtered{type='inserter',force=f}) do if tostring(i.unit_number)=='ARM_ID' then e=i end end; assert(e and e.valid); local at=e.position; e.destroy(); rcon.print(helpers.table_to_json{tick=game.tick,x=at.x,y=at.y})
        """;

    // Where an inserter stands and what it natively takes from and drops into.
    private const string Arm = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local e; for _,i in pairs(s.find_entities_filtered{type='inserter',force=f}) do if tostring(i.unit_number)=='ARM_ID' then e=i end end; assert(e and e.valid); rcon.print(helpers.table_to_json{tick=game.tick,x=e.position.x,y=e.position.y,direction=e.direction,drop=e.drop_target and tostring(e.drop_target.unit_number),pickup=e.pickup_target and tostring(e.pickup_target.unit_number)})
        """;

    // The whole cell destroyed, and a second silo and inserter given for the launch path to build its own cell.
    private const string RemoveCell = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local wanted={[SILO_ID]=true,[CHEST_ID]=true,[ARM_ID]=true,[POLE_ID]=true}; local gone=0; for _,e in pairs(s.find_entities_filtered{force=f}) do if e.unit_number and wanted[e.unit_number] then e.destroy(); gone=gone+1 end end; assert(gone==4); for name,count in pairs{['rocket-silo']=1,inserter=1} do assert(c.insert{name=name,count=count}==count) end; rcon.print(helpers.table_to_json{tick=game.tick,destroyed=gone,silos=#s.find_entities_filtered{type='rocket-silo',force=f},rocketsLaunched=f.rockets_launched})
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
