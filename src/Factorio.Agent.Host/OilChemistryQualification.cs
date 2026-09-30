using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: an injected crude oil deposit, a shore, an energy interface with one pole, researched oil processing,
/// plastics and sulfur processing, and supplied construction items and coal. Proves that automating a fluid-chain product
/// builds a pumpjack extractor, a refinery and a chemical cell with C#-routed pipes, that the engine refines pumped crude oil
/// into petroleum gas, that logistics delivers the solid inputs and collects the product, and that maintenance rebuilds a
/// destroyed pipe of the chain; not a campaign.
/// </summary>
public sealed class OilChemistryQualification(RuntimeSession session, string item = "plastic-bar")
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Oil chemistry qualification requires an explicit fixture session.");
        if (item is not ("plastic-bar" or "sulfur")) throw new ArgumentException("The oil chemistry qualification covers plastic-bar and sulfur.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"oil-chemistry-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Injected crude oil, shore, power, oil research, construction items and coal. Oil chemistry cell test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(Prepare + Statistics, token));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            var before = setup.RootElement.Clone();
            evidence.Add(new { check = "explicit-oil-preparation", item, native = before });
            Require(before.GetProperty("refineryCycles").GetInt64() == 0 && before.GetProperty("chemicalCycles").GetInt64() == 0,
                "The fixture must start without refineries or chemical plants.");

            var plan = await new FactoryDirector(game, journal, session.Directory).AutomateAsync(item, 12, token);
            var state = await LoadAsync();
            evidence.Add(new { check = "fluid-chain-built", plan, cells = state.Cells });
            var extractor = state.Cells.SingleOrDefault(c => c.Kind == FluidCellBuilder.ExtractorKind && c.Status == "ready" && c.Recipe == "crude-oil");
            var refinery = state.Cells.SingleOrDefault(c => c.Kind == FluidCellBuilder.MachineKind && c.Status == "ready" && c.Recipe == "basic-oil-processing");
            var chemical = state.Cells.SingleOrDefault(c => c.Kind == FluidCellBuilder.MachineKind && c.Status == "ready" && c.Recipe == item);
            Require(extractor is not null && refinery is not null && chemical is not null, "The extractor, refinery and chemical cells were not all completed.");
            Require(new[] { extractor!, refinery!, chemical! }.All(c => c.Plan is not null && c.Entities.Keys.All(c.Plan.ContainsKey)),
                "A fluid cell role lacks the plan maintenance needs to rebuild it.");
            Require(refinery!.Entities.Keys.Any(r => r.StartsWith("pipe-", StringComparison.Ordinal))
                && chemical!.Entities.Keys.Any(r => r.StartsWith("pipe-", StringComparison.Ordinal)), "The fluid routes were not registered with their cells.");

            var logistics = new FactoryLogistics(game, journal, session.Directory);
            var first = await logistics.ServiceAsync(40, token);
            await WaitAsync(3600);
            var second = await logistics.ServiceAsync(40, token);
            evidence.Add(new { check = "chemical-logistics", first, second });
            Require(second.Collected.GetValueOrDefault(item) > 0, $"No {item} reached the chemical cell's output chest.");

            // A destroyed gas pipe is rebuilt at its planned tile by the next logistics round.
            string lost = chemical!.Entities.Where(p => p.Key.StartsWith("pipe-", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal).First().Value;
            using var destroyed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(DestroyPipe.Replace("PIPE_ID", lost, StringComparison.Ordinal), token));
            var repair = await logistics.ServiceAsync(40, token);
            var repaired = (await LoadAsync()).Cells.Single(c => c.Id == chemical.Id);
            await WaitAsync(1800);
            var third = await logistics.ServiceAsync(40, token);
            evidence.Add(new { check = "destroyed-pipe-rebuilt", lost, native = destroyed.RootElement.Clone(), repair, repaired, third });
            Require(repair.Maintenance?.Rebuilt.Count == 1 && !repaired.Entities.Values.Contains(lost)
                && repaired.Entities.Values.Contains(repair.Maintenance.Rebuilt[0]), "Maintenance did not rebuild the destroyed pipe in place.");
            Require(third.Collected.GetValueOrDefault(item) > 0, $"The chain stopped delivering {item} after the pipe repair.");

            using var after = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("/silent-command " + Statistics, token));
            var native = after.RootElement.Clone();
            // Force statistics are cumulative; only this run's growth counts.
            double Delta(string name) => native.GetProperty(name).GetDouble() - before.GetProperty(name).GetDouble();
            string[] inputChests = chemical.Entities.TryGetValue("input-chest", out var chest) ? [chest] : [];
            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var submissions = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data")).ToArray();
                string[] crafted = submissions.Where(s => s.GetProperty("kind").GetString() == "craft")
                    .Select(s => s.GetProperty("args").GetProperty("recipe").GetString()!).ToArray();
                int mines = submissions.Count(s => s.GetProperty("kind").GetString() == "mine");
                long delivered = submissions.Where(s => s.GetProperty("kind").GetString() == "insert"
                        && inputChests.Contains(s.GetProperty("args").GetProperty("entityId").GetString()))
                    .Sum(s => s.GetProperty("args").GetProperty("count").GetInt64());
                var growth = new[] { "crudeProduced", "crudeConsumed", "gasProduced", "gasConsumed", "waterConsumed", "plasticProduced",
                    "sulfurProduced", "coalConsumed" }.ToDictionary(n => n, Delta);
                evidence.Add(new { check = "native-oil-chemistry", native, growth, crafted, mines, deliveredToInputChest = delivered });
                Require(growth["crudeProduced"] > 0 && growth["crudeConsumed"] > 0, "The pumpjack did not extract crude oil that the refinery consumed.");
                Require(growth["gasProduced"] > 0 && native.GetProperty("refineryCycles").GetInt64() > 0, "The refinery did not produce petroleum gas natively.");
                Require(growth[item == "plastic-bar" ? "plasticProduced" : "sulfurProduced"] > 0 && native.GetProperty("chemicalCycles").GetInt64() > 0,
                    $"The chemical plant did not produce {item} natively.");
                Require(crafted.All(r => r != item), $"{item} was crafted by hand.");
                Require(mines == 0, "The prepared chain triggered manual mining.");
                if (item == "plastic-bar")
                    Require(first.Supplied.GetValueOrDefault("coal") > 0 && delivered > 0 && growth["coalConsumed"] > 0,
                        "Logistics did not deliver the coal the chemical plant consumed.");
                else
                    Require(growth["waterConsumed"] > 0 && chemical.Entities.ContainsKey("pump"),
                        "The sulfur cell did not draw water from its own offshore pump.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-oil-chemistry-qualification", item, passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }

        async Task WaitAsync(int ticks)
        {
            await using var controller = new SpatialController(game, journal);
            Require((await controller.WorkAsync("wait", new { ticks }, ticks + 300, token: token)).Status == "completed", "Wait failed.");
        }

        async Task<FactoryState> LoadAsync()
        {
            var observed = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
            var scope = observed.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
            return await new FactoryRegistry(session.Directory).LoadAsync(scope.WorldId, token);
        }
    }

    // Crude oil east of the injected power, a shore beyond it; exactly the chain's machines, generous pipes, poles and coal.
    private const string Prepare = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name=(x>=30 and 'water' or 'grass-1'),position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); for _,t in pairs{'steam-power','electronics','automation','oil-gathering','oil-processing','plastics','sulfur-processing'} do f.technologies[t].researched=true end; for name,count in pairs{pumpjack=1,['oil-refinery']=1,['chemical-plant']=1,['offshore-pump']=1,pipe=150,inserter=4,['iron-chest']=4,['small-electric-pole']=30,coal=100} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=3000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); assert(s.create_entity{name='crude-oil',position={12,6},amount=600000});
        """;

    private const string DestroyPipe = """
        /silent-command local e; for _,p in pairs(game.surfaces.nauvis.find_entities_filtered{type='pipe',force=game.forces.factorio_agent}) do if tostring(p.unit_number)=='PIPE_ID' then e=p; break end end; assert(e and e.valid); local at=e.position; e.destroy(); rcon.print(helpers.table_to_json{tick=game.tick,x=at.x,y=at.y})
        """;

    // Cumulative force statistics and the cycles of every refinery and chemical plant; appended to a silent command.
    private const string Statistics = """
        local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local fs=f.get_fluid_production_statistics(s); local is=f.get_item_production_statistics(s); local r={tick=game.tick,crudeProduced=fs.get_input_count('crude-oil'),crudeConsumed=fs.get_output_count('crude-oil'),gasProduced=fs.get_input_count('petroleum-gas'),gasConsumed=fs.get_output_count('petroleum-gas'),waterConsumed=fs.get_output_count('water'),plasticProduced=is.get_input_count('plastic-bar'),sulfurProduced=is.get_input_count('sulfur'),coalConsumed=is.get_output_count('coal'),refineryCycles=0,chemicalCycles=0,players=#game.connected_players}; for _,e in pairs(s.find_entities_filtered{name='oil-refinery',force=f}) do r.refineryCycles=r.refineryCycles+e.products_finished end; for _,e in pairs(s.find_entities_filtered{name='chemical-plant',force=f}) do r.chemicalCycles=r.chemicalCycles+e.products_finished end; rcon.print(helpers.table_to_json(r))
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
