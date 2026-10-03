using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: injected power source, construction items including four bus belts, and plates. Proves that a
/// technology is completed by sized science cells, a laboratory and chest logistics, without hand-crafted packs.
/// A separate warm-lab phase supplies packs explicitly and delays preparation to verify overlapping native research.
/// </summary>
public sealed class FactoryResearchQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Factory research qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"factory-research-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new StartupJournal(new ControllerJournal(journalPath), game);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Injected energy interface, early technologies, construction items and plates. Factory research test, not a campaign." }), token);
            if (!mark.Ok) throw new InvalidDataException("Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); f.cancel_current_research(); for _,t in pairs{'steam-power','electronics','automation-science-pack','automation'} do f.technologies[t].researched=true end; f.technologies['gun-turret'].researched=false; f.technologies['electric-mining-drill'].researched=false; assert(f.add_research('electric-mining-drill')); for name,count in pairs{['assembling-machine-1']=4,inserter=8,['iron-chest']=8,['small-electric-pole']=16,lab=2,['transport-belt']=4,['iron-plate']=400,['copper-plate']=100} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=2000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,researched=f.technologies['gun-turret'].researched,staleResearched=f.technologies['electric-mining-drill'].researched,stale=f.current_research and f.current_research.name,suppliedBelts=4})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            evidence.Add(new { check = "explicit-factory-research-preparation", native = setup.RootElement.Clone() });
            if (setup.RootElement.GetProperty("staleResearched").GetBoolean()
                || setup.RootElement.GetProperty("stale").GetString() != "electric-mining-drill")
                throw new InvalidDataException("The prepared stale selection must be unresearched, including after a resource cell fixture.");

            var result = await new FactoryResearchController(game, journal, session.Directory).RunAsync("gun-turret", token);
            var state = await new FactoryRegistry(session.Directory).LoadAsync((await ScopeAsync()).WorldId, token);
            evidence.Add(new { check = "factory-research", result, cells = state.Cells });
            evidence.Add(new { check = "supplier-produces-during-consumer-construction", journal.SupplierDuringConstruction });
            if (journal.SupplierDuringConstruction is not { ProductsFinished: > 0 })
                throw new InvalidDataException("The gear supplier did not produce before the science consumer's startup.");
            if (result.Technology != "gun-turret" || state.Cells.Count(c => c.Kind == "lab") < 1
                || !state.Cells.Any(c => c.Recipe == "automation-science-pack") || !state.Cells.Any(c => c.Recipe == "iron-gear-wheel"))
                throw new InvalidDataException("Research did not use the expected science cells and laboratory.");

            var recent = await new FactoryLogistics(game, journal, session.Directory)
                .ServiceAsync(40, token, FactoryLogistics.BetweenGoalsFreshnessTicks, usePlannedBuffers: true);
            evidence.Add(new { check = "recent-tour-keeps-fresh-upkeep", result = recent });
            if (recent.Actions != 0 || recent.Collected.Count != 0 || recent.Supplied.Count != 0 || recent.Maintenance is null
                || !(await File.ReadAllLinesAsync(journalPath, token)).Any(line => line.Contains("\"type\":\"factory-logistics-recent-tour\"", StringComparison.Ordinal)))
                throw new InvalidDataException("A just-completed healthy research tour must allow fresh upkeep without another transport tour.");

            // Explicit fixture damage and spare item: native repairs must remain active despite the recent-tour receipt.
            var damagedCell = state.Cells.First(c => c.Status == "ready" && c.Entities.ContainsKey("pole") && c.Plan?.ContainsKey("pole") == true);
            string poleId = damagedCell.Entities["pole"];
            if (!long.TryParse(poleId, out _)) throw new InvalidDataException("Expected a native pole unit number.");
            string damage = $$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c); local pole; for _,e in pairs(s.find_entities_filtered{type='electric-pole',force=f}) do if tostring(e.unit_number)=='{{poleId}}' then pole=e end end; assert(pole); pole.destroy(); assert(c.insert{name='small-electric-pole',count=1}==1); rcon.print(helpers.table_to_json{tick=game.tick,destroyed='{{poleId}}',fixtureSparePoles=1})
                """;
            using var damaged = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(damage, token));
            var repaired = await new FactoryLogistics(game, journal, session.Directory)
                .ServiceAsync(40, token, FactoryLogistics.BetweenGoalsFreshnessTicks, usePlannedBuffers: true);
            var repairedState = await new FactoryRegistry(session.Directory).LoadAsync(state.WorldId, token);
            string newPole = repairedState.Cells.Single(c => c.Id == damagedCell.Id).Entities["pole"];
            var photograph = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            evidence.Add(new { check = "recent-tour-does-not-skip-native-repair", prepared = damaged.RootElement.Clone(), result = repaired,
                previousPole = poleId, newPole, photograph.CollectedTick });
            if (newPole == poleId || repaired.Maintenance?.Rebuilt.Contains(newPole) != true
                || !photograph.Records.Any(r => r.Kind == "entity" && r.EntityId == newPole))
                throw new InvalidDataException("The damaged native pole was not rebuilt during fresh maintenance.");

            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var kinds = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data").GetProperty("kind").GetString()).ToArray();
                evidence.Add(new { check = "no-hand-crafted-science", crafts = kinds.Count(k => k == "craft"), mines = kinds.Count(k => k == "mine"),
                    transfers = kinds.Count(k => k is "insert" or "take") });
                if (kinds.Contains("craft")) throw new InvalidDataException("Science or cells were hand-crafted despite supplied items.");
                // The fixture leaves another technology selected, as a goal that reached its deadline does.
                var replaced = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "research-selection-replaced")
                    .Select(r => r.RootElement.GetProperty("data").GetProperty("previous").GetString()).ToArray();
                evidence.Add(new { check = "stale-selection-replaced", replaced });
                if (!replaced.SequenceEqual(["electric-mining-drill"])) throw new InvalidDataException("The stale research selection was not replaced exactly once.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            // The normal chemical-science goal spent over two minutes preparing an already loaded factory before
            // selecting research. Supply only this separate fixture phase's packs, then make preparation equally slow.
            const string warmTarget = "gun-turret";
            var warmTechnology = (await new TechnologyClient(game).ReadDependenciesAsync(warmTarget, token)).Technologies[warmTarget];
            double delaySeconds = warmTechnology.Count * warmTechnology.EnergyTicks / 60 + 20;
            evidence.Add(new { check = "loaded-lab-preparation-cost", technology = warmTechnology, delaySeconds });
            if (delaySeconds is < 20 or > 180 || warmTechnology.Ingredients.Any(i => i.Name != "automation-science-pack")
                || warmTechnology.Ingredients.Sum(i => i.Amount * warmTechnology.Count) > 100)
                throw new InvalidDataException("Warm fixture requires a short red-science technology covered by its declared packs.");
            string labId = repairedState.Cells.First(c => c.Kind == "lab" && c.Status == "ready").Entities["machine"];
            if (!long.TryParse(labId, out _)) throw new InvalidDataException("Expected a native laboratory unit number.");
            string warmPreparation = $$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local lab; for _,e in pairs(s.find_entities_filtered{type='lab',force=f}) do if tostring(e.unit_number)=='{{labId}}' then lab=e end end; assert(lab); f.cancel_current_research(); f.technologies['{{warmTarget}}'].researched=false; local inv=lab.get_inventory(defines.inventory.lab_input); local discarded=inv.get_contents(); inv.clear(); assert(inv.insert{name='automation-science-pack',count=100}==100); rcon.print(helpers.table_to_json{tick=game.tick,lab='{{labId}}',fixtureTechnology='{{warmTarget}}',fixtureReset=true,fixturePacks=100,discarded=discarded,researched=f.technologies['{{warmTarget}}'].researched})
                """;
            using var warmSetup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(warmPreparation, token));
            var delayed = new SlowPreparationGame(game, TimeSpan.FromSeconds(delaySeconds), warmTarget);
            var warm = await new FactoryResearchController(delayed, journal, session.Directory).RunAsync(warmTarget, token);
            var warmNative = ResearchSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("research_state", new { technology = warmTarget }), token), warmTarget);
            evidence.Add(new { check = "loaded-lab-finishes-during-preparation", prepared = warmSetup.RootElement.Clone(), delaySeconds,
                result = warm, delayedResearch = delayed.StateAfterDelay, native = warmNative });
            if (!warmNative.Researched || delayed.StateAfterDelay?.Researched != true || warm.Rounds != 0 || warm.Procured.Count != 0)
                throw new InvalidDataException("Loaded laboratory did not finish research before factory preparation returned.");
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-factory-research-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }

        async Task<ActorScope> ScopeAsync()
        {
            var observed = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
            return observed.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        }
    }

    private sealed record SupplierObservation(long Tick, string EntityId, long ProductsFinished);

    private sealed class StartupJournal(IControllerJournal inner, IGameClient game) : IControllerJournal
    {
        public SupplierObservation? SupplierDuringConstruction { get; private set; }

        public async Task AppendAsync(string type, object data, CancellationToken token)
        {
            await inner.AppendAsync(type, data, token);
            if (SupplierDuringConstruction is not null || type != "factory-cell-ready"
                || data is not FactoryCell { Recipe: "automation-science-pack" }) return;
            // This is before the consumer's startup tour. No prepared gears were supplied or hand-crafted.
            var photo = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            var gear = photo.Records.FirstOrDefault(r => r.Kind == "work"
                && r.Data.TryGetProperty("recipe", out var recipe) && recipe.GetString() == "iron-gear-wheel");
            if (gear is not null)
                SupplierDuringConstruction = new(photo.CollectedTick, gear.EntityId, gear.Data.GetProperty("productsFinished").GetInt64());
        }
    }

    private sealed class SlowPreparationGame(IGameClient game, TimeSpan delay, string technology) : IGameClient
    {
        private bool waited;
        public ResearchSnapshot? StateAfterDelay { get; private set; }

        public async Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            if (!waited && request.Action == "factory_snapshot")
            {
                waited = true;
                await Task.Delay(delay, cancellationToken);
                StateAfterDelay = ResearchSnapshot.Parse(await game.ExecuteAsync(
                    GameRequest.Create("research_state", new { technology }), cancellationToken), technology);
            }
            return await game.ExecuteAsync(request, cancellationToken);
        }
    }
}
