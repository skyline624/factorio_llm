using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: injected iron and coal deposits, an energy interface, researched electric drills and supplied
/// construction items. Proves native resource rows (two iron smelter cells and a coal miner), their power link, and
/// chest logistics that refuel the cell furnaces with mined coal; not a campaign.
/// </summary>
public sealed class ResourceCellQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Resource cell qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"resource-cell-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Injected ores, energy interface, drill research, items and trees. Resource cell test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); for _,t in pairs{'steam-power','electronics','electric-mining-drill'} do f.technologies[t].researched=true end; for name,count in pairs{['electric-mining-drill']=3,['stone-furnace']=2,inserter=2,['iron-chest']=3,['small-electric-pole']=20} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=2000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); for x=4,15 do for y=-6,5 do assert(s.create_entity{name='iron-ore',position={x+0.5,y+0.5},amount=5000}) end end; for x=4,11 do for y=14,21 do assert(s.create_entity{name='coal',position={x+0.5,y+0.5},amount=5000}) end end; for _,p in pairs{{6.5,1.5},{0.5,1.5},{9.5,-4.5}} do assert(s.create_entity{name='tree-01',position=p}) end; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,coal=c.get_item_count('coal'),plates=c.get_item_count('iron-plate')})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            evidence.Add(new { check = "explicit-resource-preparation", native = setup.RootElement.Clone() });
            Require(setup.RootElement.GetProperty("coal").GetInt32() == 0, "The actor must start without coal so furnace fuel can only come from the miner.");

            var director = new FactoryDirector(game, journal, session.Directory);
            var iron = await director.EnsureRawAsync("iron-plate", 30, token, explorationBudget: 2);
            var coal = await director.EnsureRawAsync("coal", 20, token, explorationBudget: 2);
            var state = await new FactoryRegistry(session.Directory).LoadAsync((await ScopeAsync()).WorldId, token);
            evidence.Add(new { check = "resource-cells-ready", iron, coal, rows = state.Rows, cells = state.Cells });
            var smelters = state.Cells.Where(c => c.Kind == "smelter" && c.Recipe == "iron-plate" && c.Status == "ready").ToArray();
            var miners = state.Cells.Where(c => c.Kind == "miner" && c.Recipe == "coal" && c.Status == "ready").ToArray();
            Require(smelters.Length >= 2 && miners.Length >= 1, "Two iron smelter cells and one coal miner were not completed.");
            Require(smelters.All(c => c.Entities.Keys.Order().SequenceEqual(["drill", "furnace", "output-chest", "output-inserter", "pole"])),
                "A smelter cell lacks one of its native entities.");

            var logistics = new FactoryLogistics(game, journal, session.Directory);
            await WaitAsync(3600);
            var first = await logistics.ServiceAsync(40, token);
            await WaitAsync(3600);
            var second = await logistics.ServiceAsync(40, token);
            evidence.Add(new { check = "resource-logistics", first, second });
            Require(first.Collected.GetValueOrDefault("coal") > 0 && first.Supplied.GetValueOrDefault("coal") > 0,
                "Mined coal was not collected and loaded into burners.");
            Require(second.Collected.GetValueOrDefault("iron-plate") >= 10, "The smelter cells did not deliver plates to their chests.");

            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var submissions = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data")).ToArray();
                string[] kinds = submissions.Select(s => s.GetProperty("kind").GetString()!).ToArray();
                var furnaces = smelters.Select(c => c.Entities["furnace"]).ToHashSet(StringComparer.Ordinal);
                var fuelled = submissions.Where(s => s.GetProperty("kind").GetString() == "insert"
                        && s.GetProperty("args").GetProperty("inventory").GetString() == "fuel"
                        && furnaces.Contains(s.GetProperty("args").GetProperty("entityId").GetString()!))
                    .Select(s => s.GetProperty("args").GetProperty("entityId").GetString()!).Distinct().ToArray();
                int cleared = rows.Count(r => r.RootElement.GetProperty("type").GetString() is "resource-clearance" or "navigation-tree-cleared");
                evidence.Add(new { check = "no-manual-production", crafts = kinds.Count(k => k == "craft"), mines = kinds.Count(k => k == "mine"),
                    cleared, fuelledFurnaces = fuelled });
                Require(fuelled.Length == furnaces.Count, "Not every smelter furnace was refuelled with the collected coal.");
                Require(kinds.Count(k => k == "craft") == 0 && kinds.Count(k => k == "mine") == cleared,
                    "Supplied construction triggered manual crafting or mining beyond clearing.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-resource-cell-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }

        async Task WaitAsync(int ticks)
        {
            await using var controller = new SpatialController(game, journal);
            Require((await controller.WorkAsync("wait", new { ticks }, ticks + 300, token: token)).Status == "completed", "Wait failed.");
        }

        async Task<ActorScope> ScopeAsync()
        {
            var observed = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
            return observed.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
