using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: an injected energy interface, researched electronics, automation and rocket-silo technologies, and a supplied
/// silo, inserters, chests and poles. Proves that automating rocket parts plans the silo as a stage and builds its persistent cell;
/// then, once the fixture has artificially set the built silo one part short of a rocket and given the actor exactly the last part's
/// ingredients, that factory logistics feeds them through the cell's inserter, the silo finishes the rocket and C# launches it, with
/// an independent read of the engine's counter and nothing inserted into the silo by hand. The fixture destroys the cell's inserter
/// once logistics has stocked the chest, and the launch loop itself rebuilds it at its plan. With the registry kept and only the silo
/// destroyed, the next launch rebuilds that silo in the same cell instead of a second cell; with the cell and the registry removed, it
/// builds a cell itself. Without ingredients, both stop at procurement. Not a campaign.
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
            { reason = "Injected power, research, items; parts one short, ingredients given; arm, silo, cell destroyed; silos given. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            artificial.Add("mark_fixture before any preparation");
            var setup = await NativeAsync(Prepare);
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            artificial.AddRange([
                "area (-48,-48)-(48,48) emptied and paved with grass, actor teleported to (0,0) with an emptied inventory",
                "electric-energy-interface injected at (-20,0) with a small pole at (-18.5,0.5)",
                "every technology set unresearched, then only electronics, automation and rocket-silo researched and their effects reapplied",
                "1 rocket-silo, 2 inserters, 2 iron chests and 12 small poles inserted into the actor",
                "factory registry deleted"]);
            evidence.Add(new { check = "explicit-silo-preparation", native = setup });
            Require(setup.GetProperty("partRecipe").GetBoolean(), "The rocket-part recipe is not enabled.");

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
            var set = await NativeAsync(Ids(SetParts, silo, chest, arm));
            artificial.AddRange([
                $"after the cell was built, the silo's rocket_parts set from 0 to {set.GetProperty("parts").GetInt32()} (native requirement {set.GetProperty("required").GetInt32()})",
                "the actor, carrying none of them, given exactly the native ingredients of one rocket-part"]);
            evidence.Add(new { check = "artificial-last-part", native = set });
            Require(set.GetProperty("parts").GetInt32() == set.GetProperty("required").GetInt32() - 1, "The silo was not set one part short of a rocket.");
            Require(Feeds(set, "armDrop", "armPickup", silo, chest), "The cell inserter does not take from its chest and drop into its silo natively.");
            var given = set.GetProperty("given").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);

            // Artificial attack inside the launch loop: once logistics has put the ingredients into the chest, the cell's inserter is
            // destroyed with the chest still holding the batch; the spare inserter from the preparation is still carried.
            var attack = new ArmCutClient(game, session, Ids(CutArm, silo, chest, arm), chest);
            var result = await new RocketLaunchController(attack, journal, session.Directory).RunAsync(SiloItem, token);
            artificial.Add("during the launch, at the first wait after logistics stocked the cell chest, the cell's input inserter destroyed by fixture " +
                "command, its hand's item first put back into the chest; one spare inserter still carried");
            string rebuilt = (await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token)).Cells.Single(c => c.Id == cell.Id)
                .Entities["input-inserter"];
            var native = await NativeAsync(Ids(Proof, silo, chest, rebuilt));
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

            // The launch loop, not a separate logistics call, rebuilt the inserter at its plan while the chest held the batch.
            JsonElement? rebuild = Between(rows, "rocket-start", "rocket-result").Where(r => r.GetProperty("type").GetString() == "factory-rebuild")
                .Select(r => r.GetProperty("data")).Where(d => d.GetProperty("role").GetString() == "input-inserter").Select(d => (JsonElement?)d).SingleOrDefault();
            var standing = await NativeAsync(Ids(Arm, silo, chest, rebuilt));
            var planned = cell.Plan!["input-inserter"];
            evidence.Add(new { check = "inserter-rebuilt-during-launch", cut = attack.Cut, rebuild, rebuilt, planned, native = standing });
            Require(attack.Cut is { } cut && cut.GetProperty("tick").GetInt64() > result.StartTick && cut.GetProperty("tick").GetInt64() < result.EndTick,
                "The fixture did not destroy the cell inserter during the launch.");
            Require(rebuilt != arm && rebuild is { } done && done.GetProperty("entityId").GetString() == rebuilt,
                "The launch loop did not rebuild the destroyed cell inserter.");
            Require(Feeds(standing, "drop", "pickup", silo, chest) && standing.GetProperty("x").GetDouble() == planned.Position.X
                && standing.GetProperty("y").GetDouble() == planned.Position.Y && standing.GetProperty("direction").GetInt32() == planned.Direction,
                "The rebuilt inserter does not feed the silo from the chest at its planned place.");

            // Artificial: only the silo is destroyed and a second one given; the registry is kept. The next launch must rebuild that
            // silo in the same cell from its plan, never plan a second cell beside it, and stop at procurement without ingredients.
            var razed = await NativeAsync(Ids(DestroySilo, silo, chest, rebuilt));
            artificial.Add("after the launch, the cell's silo alone destroyed by fixture command with the registry kept, and a second rocket-silo inserted into the actor");
            int restoring = (await RowsAsync()).Length;
            string? halt = null;
            try { await new RocketLaunchController(game, journal, session.Directory).RunAsync(SiloItem, token); }
            catch (InvalidOperationException error) { halt = error.Message; }
            var kept = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            var restored = kept.Cells.SingleOrDefault(c => c.Id == cell.Id);
            string? replacement = restored?.Entities.GetValueOrDefault("machine");
            JsonElement? placed = replacement is null || replacement == silo ? null : await NativeAsync(Ids(SiloAt, replacement, chest, rebuilt));
            var phase = (await RowsAsync()).Skip(restoring).ToArray();
            JsonElement? restart = phase.Where(r => r.GetProperty("type").GetString() == "rocket-start").Select(r => (JsonElement?)r.GetProperty("data")).LastOrDefault();
            int restoredFed = replacement is null ? -1 : Submissions(phase).Count(s => Into(s, replacement));
            int planning = phase.Count(r => r.GetProperty("type").GetString() is "factory-cell-plan" or "factory-zone");
            var plannedSilo = cell.Plan!["machine"];
            evidence.Add(new { check = "launch-restores-cell-silo", destroyed = razed, halt, restored, kept.Zones, native = placed, restart, restoredFed, planning });
            Require(kept.Cells.Count(c => c.Kind == SiloCellPlanner.Kind) == 1 && kept.Zones.Count == registered.Zones.Count && planning == 0,
                "The launch planned a second silo cell or band instead of restoring its own.");
            Require(restored is { Status: "ready" } && Complete(restored) && placed is { } stands
                && stands.GetProperty("x").GetDouble() == plannedSilo.Position.X && stands.GetProperty("y").GetDouble() == plannedSilo.Position.Y,
                "Maintenance did not rebuild the destroyed silo in its cell at its plan.");
            Require(restart is { } begun && begun.GetProperty("cell").GetString() == cell.Id && begun.GetProperty("siloId").GetString() == replacement,
                "The launch did not adopt the cell's rebuilt silo.");
            Require(halt?.StartsWith("unsupported:", StringComparison.Ordinal) == true && restoredFed == 0,
                "Without ingredients the launch did not stop at procurement, or fed the rebuilt silo by hand.");

            // Artificial: the whole cell and the registry are removed and a third silo given. With no silo standing and no cell
            // registered, the launch path builds a cell itself and, again without ingredients, stops at procurement.
            var removed = await NativeAsync(Ids(RemoveCell, replacement!, chest, rebuilt).Replace("POLE_ID", restored!.Entities["pole"], StringComparison.Ordinal));
            File.Delete(new FactoryRegistry(session.Directory).Path);
            artificial.AddRange([
                "after the silo was restored, the cell's silo, chest, inserter and pole destroyed by fixture command and the registry deleted",
                "a third rocket-silo and one inserter inserted into the actor"]);
            string? stop = null;
            try { await new RocketLaunchController(game, journal, session.Directory).RunAsync(SiloItem, token); }
            catch (InvalidOperationException error) { stop = error.Message; }
            var own = (await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token)).Cells
                .SingleOrDefault(c => c.Kind == SiloCellPlanner.Kind && c.Status == "ready");
            rows = await RowsAsync();
            var start = rows.Last(r => r.GetProperty("type").GetString() == "rocket-start").GetProperty("data");
            int handFed = own is null ? -1 : Submissions(rows).Count(s => Into(s, own.Entities["machine"]));
            evidence.Add(new { check = "launch-built-cell", removed, stop, own, start, handFed });
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

        async Task<JsonElement> NativeAsync(string command)
        {
            using var read = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(command, token));
            return read.RootElement.Clone();
        }
    }

    /// <summary>Exactly a silo, its chest, inserter and pole, besides power links, each with the plan maintenance rebuilds it from.</summary>
    private static bool Complete(FactoryCell cell) => cell.Plan is not null && cell.Entities.Keys.All(cell.Plan.ContainsKey)
        && cell.Entities.Keys.Where(role => !role.StartsWith("link-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
            .SequenceEqual(["input-chest", "input-inserter", "machine", "pole"]);

    private static IEnumerable<JsonElement> Submissions(IEnumerable<JsonElement> rows) =>
        rows.Where(r => r.GetProperty("type").GetString() == "submission").Select(r => r.GetProperty("data"));

    /// <summary>Rows from the last <paramref name="first"/> row through the following <paramref name="last"/> row.</summary>
    private static IEnumerable<JsonElement> Between(IReadOnlyList<JsonElement> rows, string first, string last)
    {
        int from = rows.Select((row, index) => (row, index)).Last(p => p.row.GetProperty("type").GetString() == first).index;
        return rows.Skip(from).TakeWhile((row, index) => index == 0 || rows[from + index - 1].GetProperty("type").GetString() != last);
    }

    private static bool Into(JsonElement submission, string entityId) => submission.GetProperty("kind").GetString() == "insert"
        && submission.GetProperty("args").GetProperty("entityId").GetString() == entityId;

    // Lua drops nil targets from the JSON.
    private static bool Feeds(JsonElement arm, string drop, string pickup, string silo, string chest) =>
        arm.TryGetProperty(drop, out var dropping) && dropping.GetString() == silo && arm.TryGetProperty(pickup, out var picking) && picking.GetString() == chest;

    private static string Ids(string command, string silo, string chest, string arm) => command.Replace("SILO_ID", silo, StringComparison.Ordinal)
        .Replace("CHEST_ID", chest, StringComparison.Ordinal).Replace("ARM_ID", arm, StringComparison.Ordinal);

    /// <summary>
    /// Fixture-only attack inside the launch loop: on the first wait submitted after an insertion into the cell chest, the cell's inserter
    /// is destroyed before the wait goes out. The production controllers contain no such mutation.
    /// </summary>
    private sealed class ArmCutClient(IGameClient inner, RuntimeSession runtime, string command, string chest) : IGameClient
    {
        private bool stocked;
        public JsonElement? Cut { get; private set; }

        public async Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            if (Cut is null && request.Action == "submit")
            {
                string? kind = request.Arguments.GetProperty("kind").GetString();
                if (kind == "insert" && request.Arguments.GetProperty("args").GetProperty("entityId").GetString() == chest) stocked = true;
                else if (kind == "wait" && stocked)
                {
                    using var cut = JsonDocument.Parse(await runtime.CreateRcon().ExecuteAsync(command, cancellationToken));
                    Cut = cut.RootElement.Clone();
                }
            }
            return await inner.ExecuteAsync(request, cancellationToken);
        }
    }

    // Grass around the actor, an energy interface west of it, both silo recipes and the cell's parts; no rocket ingredient. Research is
    // set explicitly so earlier fixtures on the same server (verify-rocket researches everything) cannot add chains to the plan.
    private const string Prepare = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); for _,p in pairs(game.connected_players) do assert(p.character==c) end; game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); f.cancel_current_research(); for _,t in pairs(f.technologies) do t.researched=false end; for _,t in pairs{'electronics','automation','rocket-silo'} do f.technologies[t].researched=true end; f.reset_technology_effects(); for name,count in pairs{['rocket-silo']=1,inserter=2,['iron-chest']=2,['small-electric-pole']=12} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=100000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,rocketsLaunched=f.rockets_launched,siloRecipe=f.recipes['rocket-silo'].enabled,partRecipe=f.recipes['rocket-part'].enabled,players=#game.connected_players,gameSpeed=game.speed})
        """;

    // The built silo one part short of a rocket, and the actor given exactly one part's native ingredients.
    private const string SetParts = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local function find(id) for _,e in pairs(s.find_entities_filtered{force=f}) do if e.unit_number and tostring(e.unit_number)==id then return e end end end; local silo,chest,arm=find('SILO_ID'),find('CHEST_ID'),find('ARM_ID'); assert(silo and chest and arm and silo.rocket_parts==0 and silo.rocket_silo_status==defines.rocket_silo_status.building_rocket); local required=silo.prototype.rocket_parts_required; silo.rocket_parts=required-1; local given={}; for _,i in pairs(f.recipes[silo.prototype.fixed_recipe].ingredients) do assert(i.type=='item' and c.get_item_count(i.name)==0 and chest.get_item_count(i.name)==0 and c.insert{name=i.name,count=i.amount}==i.amount); given[i.name]=i.amount end; rcon.print(helpers.table_to_json{tick=game.tick,required=required,parts=silo.rocket_parts,finished=silo.products_finished,rocketsLaunched=f.rockets_launched,given=given,armDrop=arm.drop_target and tostring(arm.drop_target.unit_number),armPickup=arm.pickup_target and tostring(arm.pickup_target.unit_number),siloNetwork=silo.electric_network_id,players=#game.connected_players})
        """;

    // The cell's inserter destroyed as an attack would, its hand's item first put back into the chest so the last part stays possible.
    private const string CutArm = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local function find(id) for _,e in pairs(s.find_entities_filtered{force=f}) do if e.unit_number and tostring(e.unit_number)==id then return e end end end; local silo,chest,arm=find('SILO_ID'),find('CHEST_ID'),find('ARM_ID'); assert(silo and chest and arm); local held=arm.held_stack; local returned=0; if held.valid_for_read then returned=held.count; assert(chest.insert{name=held.name,count=held.count}==held.count); held.clear() end; local r={tick=game.tick,x=arm.position.x,y=arm.position.y,returned=returned,parts=silo.rocket_parts,chest={},silo={}}; for _,i in pairs(f.recipes[silo.prototype.fixed_recipe].ingredients) do r.chest[i.name]=chest.get_item_count(i.name); r.silo[i.name]=silo.get_item_count(i.name) end; arm.destroy(); rcon.print(helpers.table_to_json(r))
        """;

    // Independent native read after the launch: counter, silo, chest, inserter hand (-1 without an inserter) and actor.
    private const string Proof = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local function find(id) for _,e in pairs(s.find_entities_filtered{force=f}) do if e.unit_number and tostring(e.unit_number)==id then return e end end end; local silo,chest,arm=find('SILO_ID'),find('CHEST_ID'),find('ARM_ID'); local r={tick=game.tick,rocketsLaunched=f.rockets_launched,parts=silo.rocket_parts,finished=silo.products_finished,hand=arm and (arm.held_stack.valid_for_read and arm.held_stack.count or 0) or -1,actor={},chest={},silo={},players=#game.connected_players}; for _,i in pairs(f.recipes[silo.prototype.fixed_recipe].ingredients) do r.actor[i.name]=c.get_item_count(i.name); r.chest[i.name]=chest.get_item_count(i.name); r.silo[i.name]=silo.get_item_count(i.name) end; rcon.print(helpers.table_to_json(r))
        """;

    // Where an inserter stands and what it natively takes from and drops into.
    private const string Arm = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local e; for _,i in pairs(s.find_entities_filtered{type='inserter',force=f}) do if tostring(i.unit_number)=='ARM_ID' then e=i end end; assert(e and e.valid); rcon.print(helpers.table_to_json{tick=game.tick,x=e.position.x,y=e.position.y,direction=e.direction,drop=e.drop_target and tostring(e.drop_target.unit_number),pickup=e.pickup_target and tostring(e.pickup_target.unit_number)})
        """;

    // The cell's silo alone destroyed, emptied after its launch, and a second silo given; the chest, inserter and pole stay.
    private const string DestroySilo = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local silo; for _,e in pairs(s.find_entities_filtered{type='rocket-silo',force=f}) do if tostring(e.unit_number)=='SILO_ID' then silo=e end end; assert(silo and silo.rocket_parts==0); local at=silo.position; silo.destroy(); assert(c.insert{name='rocket-silo',count=1}==1); rcon.print(helpers.table_to_json{tick=game.tick,x=at.x,y=at.y,silos=#s.find_entities_filtered{type='rocket-silo',force=f},carried=c.get_item_count('rocket-silo'),rocketsLaunched=f.rockets_launched})
        """;

    // Where a silo stands and its native phase.
    private const string SiloAt = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local silo; for _,e in pairs(s.find_entities_filtered{type='rocket-silo',force=f}) do if tostring(e.unit_number)=='SILO_ID' then silo=e end end; assert(silo and silo.valid); rcon.print(helpers.table_to_json{tick=game.tick,x=silo.position.x,y=silo.position.y,parts=silo.rocket_parts,network=silo.electric_network_id,silos=#s.find_entities_filtered{type='rocket-silo',force=f}})
        """;

    // The whole cell destroyed, and a third silo and an inserter given for the launch path to build its own cell.
    private const string RemoveCell = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local wanted={[SILO_ID]=true,[CHEST_ID]=true,[ARM_ID]=true,[POLE_ID]=true}; local gone=0; for _,e in pairs(s.find_entities_filtered{force=f}) do if e.unit_number and wanted[e.unit_number] then e.destroy(); gone=gone+1 end end; assert(gone==4); for name,count in pairs{['rocket-silo']=1,inserter=1} do assert(c.insert{name=name,count=count}==count) end; rcon.print(helpers.table_to_json{tick=game.tick,destroyed=gone,silos=#s.find_entities_filtered{type='rocket-silo',force=f},rocketsLaunched=f.rockets_launched})
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
