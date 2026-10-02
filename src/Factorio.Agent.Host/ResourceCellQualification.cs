using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: injected iron and coal deposits, an energy interface, researched electric drills, exactly the
/// cells' construction items and the materials of their link poles. Proves native resource rows (two iron smelter cells
/// and a coal miner), link poles procured from carried stock, chest logistics that refuel the cell furnaces with mined
/// coal, the in-place repair of a destroyed cell pole and the retirement of an exhausted miner; not a campaign.
/// </summary>
public sealed class ResourceCellQualification(RuntimeSession session, bool burner = false)
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
            { reason = "Injected ores, power, research, items, trees; destroyed pole and coal. Resource cell test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            // Exactly one pole per cell: every link pole must be procured from the supplied wood and cables.
            string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); for _,t in pairs{'steam-power','electronics','electric-mining-drill'} do f.technologies[t].researched=true end; for name,count in pairs{['electric-mining-drill']=3,['stone-furnace']=2,inserter=2,['iron-chest']=3,['small-electric-pole']=3,wood=10,['copper-cable']=20} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=2000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); for x=4,15 do for y=-6,5 do assert(s.create_entity{name='iron-ore',position={x+0.5,y+0.5},amount=5000}) end end; for x=4,11 do for y=14,21 do assert(s.create_entity{name='coal',position={x+0.5,y+0.5},amount=5000}) end end; for _,p in pairs{{6.5,1.5},{0.5,1.5},{9.5,-4.5}} do assert(s.create_entity{name='tree-01',position=p}) end; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,coal=c.get_item_count('coal'),plates=c.get_item_count('iron-plate')})
                """;
            if (burner)
                prepare = prepare.Replace("['electric-mining-drill']=3", "['burner-mining-drill']=3", StringComparison.Ordinal)
                    .Replace("for _,t in pairs{'steam-power','electronics','electric-mining-drill'} do f.technologies[t].researched=true end",
                        "for _,t in pairs{'steam-power','electronics'} do f.technologies[t].researched=true end; f.technologies['electric-mining-drill'].researched=false", StringComparison.Ordinal);
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            evidence.Add(new { check = "explicit-resource-preparation", native = setup.RootElement.Clone() });
            Require(setup.RootElement.GetProperty("coal").GetInt32() == 0, "The actor must start without coal so furnace fuel can only come from the miner.");

            var director = new FactoryDirector(game, journal, session.Directory);
            var iron = await director.EnsureRawAsync("iron-plate", 30, token, explorationBudget: 2);
            var coal = await director.EnsureRawAsync("coal", burner ? 10 : 20, token, explorationBudget: 2);
            var state = await new FactoryRegistry(session.Directory).LoadAsync((await ScopeAsync()).WorldId, token);
            evidence.Add(new { check = "resource-cells-ready", iron, coal, rows = state.Rows, cells = state.Cells });
            var smelters = state.Cells.Where(c => c.Kind == "smelter" && c.Recipe == "iron-plate" && c.Status == "ready").ToArray();
            var miners = state.Cells.Where(c => c.Kind == "miner" && c.Recipe == "coal" && c.Status == "ready").ToArray();
            Require(smelters.Length >= 2 && miners.Length >= 1, "Two iron smelter cells and one coal miner were not completed.");
            Require(smelters.All(c => c.Entities.Keys.Where(role => !role.StartsWith("link-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
                .SequenceEqual(["drill", "furnace", "output-chest", "output-inserter", "pole"]) && c.Entities.Keys.All(c.Plan!.ContainsKey)),
                "A smelter cell lacks one of its native entities.");

            var logistics = new FactoryLogistics(game, journal, session.Directory);
            await WaitAsync(3600);
            var first = await logistics.ServiceAsync(40, token);
            await WaitAsync(3600);
            var second = await logistics.ServiceAsync(40, token);
            for (int round = 2; burner && second.Collected.GetValueOrDefault("iron-plate") < 10 && round < 8; round++)
            {
                evidence.Add(new { check = "burner-startup-logistics-round", round, service = second });
                await WaitAsync(3600);
                second = await logistics.ServiceAsync(40, token);
            }
            evidence.Add(new { check = "resource-logistics", first, second });
            Require(first.Collected.GetValueOrDefault("coal") > 0 && first.Supplied.GetValueOrDefault("coal") > 0,
                "Mined coal was not collected and loaded into burners.");
            Require(second.Collected.GetValueOrDefault("iron-plate") >= 10, "The smelter cells did not deliver plates to their chests.");

            // A destroyed pole reopens its cell; the builder rebuilds that part only, in place.
            var damaged = smelters.OrderBy(c => c.Slot.Index).First();
            string lostPole = damaged.Entities["pole"];
            using var destroyed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(DestroyPole.Replace("POLE_ID", lostPole, StringComparison.Ordinal), token));
            var reopened = await logistics.ServiceAsync(40, token);
            var open = (await LoadAsync()).Cells.Single(c => c.Id == damaged.Id);
            // Either maintenance rebuilds the recorded pole in place at once, or resource health reopens the cell for the builder.
            bool reopenedForBuilder = open.Status == "building" && !open.Entities.ContainsKey("pole") && open.Entities.Count == damaged.Entities.Count - 1;
            bool rebuiltByMaintenance = open.Status == "ready" && open.Entities.TryGetValue("pole", out var newPole) && newPole != lostPole
                && reopened.Maintenance?.Rebuilt.Contains(newPole) == true;
            Require(reopenedForBuilder || rebuiltByMaintenance, "Logistics neither reopened nor rebuilt the damaged cell's destroyed pole.");
            var repaired = await director.EnsureRawAsync("iron-plate", 30, token, explorationBudget: 0);
            var rebuilt = (await LoadAsync()).Cells.Single(c => c.Id == damaged.Id);
            evidence.Add(new { check = "destroyed-pole-repaired", lostPole, native = destroyed.RootElement.Clone(), reopened, open, repaired, rebuilt });
            Require(rebuilt.Status == "ready" && rebuilt.Entities["pole"] != lostPole && rebuilt.Slot == damaged.Slot
                && rebuilt.Entities.Where(p => p.Key != "pole").All(p => damaged.Entities[p.Key] == p.Value), "The damaged cell was not repaired in place.");

            // Destroying the coal under the miner exhausts it natively; logistics retires the cell from capacity.
            var exhaustedMiner = miners[0];
            using var exhaustion = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                ExhaustDrill.Replace("DRILL_ID", exhaustedMiner.Entities["drill"], StringComparison.Ordinal), token));
            await WaitAsync(120);
            var retiring = await logistics.ServiceAsync(40, token);
            var retired = await LoadAsync();
            var (coalCells, coalPerMinute) = FactoryDirector.RawCapacity(retired, "coal");
            evidence.Add(new { check = "exhausted-miner-retired", native = exhaustion.RootElement.Clone(), retiring,
                miner = retired.Cells.Single(c => c.Id == exhaustedMiner.Id), coalCells, coalPerMinute });
            Require(exhaustion.RootElement.GetProperty("destroyed").GetInt32() > 0
                && retired.Cells.Single(c => c.Id == exhaustedMiner.Id).Status == ResourceCellHealth.Depleted
                && coalCells == miners.Length - 1, "The exhausted coal miner still counts as capacity.");

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
                string[] crafted = submissions.Where(s => s.GetProperty("kind").GetString() == "craft")
                    .Select(s => s.GetProperty("args").GetProperty("recipe").GetString()!).ToArray();
                int links = rows.Count(r => r.RootElement.GetProperty("type").GetString() == "resource-power-pole");
                evidence.Add(new { check = "no-manual-production", crafted, links, mines = kinds.Count(k => k == "mine"), cleared, fuelledFurnaces = fuelled });
                Require(fuelled.Length == furnaces.Count, "Not every smelter furnace was refuelled with the collected coal.");
                Require(links > 0 && crafted.Length > 0 && crafted.All(r => r == "small-electric-pole"),
                    "Link poles were not procured from the supplied materials, or something else was crafted.");
                int coldStarts = submissions.Count(s => s.GetProperty("kind").GetString() == "mine"
                    && s.GetProperty("args").TryGetProperty("name", out var mined) && mined.GetString() == "coal"
                    && s.GetProperty("args").GetProperty("count").GetInt32() == 1);
                if (burner)
                {
                    Require(coldStarts <= 1 && rows.Any(r => r.RootElement.GetProperty("type").GetString() == "coal-producer-started"),
                        "The cold burner miner did not start with at most one manual starter coal.");
                    evidence.Add(new { check = "coal-producer-startup", coldStarts });
                }
                Require(kinds.Count(k => k == "mine") == cleared + (burner ? coldStarts : 0), "Supplied construction triggered manual mining beyond clearing or one cold-start fuel.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-resource-cell-qualification", burner, passed, isAutonomousCampaign = false, journalPath, evidence },
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

        async Task<FactoryState> LoadAsync() => await new FactoryRegistry(session.Directory).LoadAsync((await ScopeAsync()).WorldId, token);
    }

    private const string DestroyPole = """
        /silent-command local e; for _,p in pairs(game.surfaces.nauvis.find_entities_filtered{type='electric-pole',force=game.forces.factorio_agent}) do if p.unit_number==POLE_ID then e=p; break end end; assert(e and e.valid); e.destroy(); rcon.print(helpers.table_to_json{tick=game.tick})
        """;

    private const string ExhaustDrill = """
        /silent-command local d; for _,m in pairs(game.surfaces.nauvis.find_entities_filtered{type='mining-drill',force=game.forces.factorio_agent}) do if m.unit_number==DRILL_ID then d=m; break end end; assert(d and d.valid); local n=0; for _,r in pairs(d.surface.find_entities_filtered{type='resource',area=d.mining_area}) do r.destroy(); n=n+1 end; rcon.print(helpers.table_to_json{tick=game.tick,destroyed=n})
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
