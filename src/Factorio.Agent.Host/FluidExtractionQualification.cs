using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepared distant oil deposit; grid extension and research use native power and extraction.</summary>
public sealed class FluidExtractionQualification(RuntimeSession session, bool reuse = false, bool stationaryThreat = false,
    bool factoryAdoption = false, bool expansion = false, bool deferExpansion = false)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Fluid extraction qualification requires an explicit fixture session.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(15));
        token = deadline.Token;
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"fluid-extraction-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared distant crude oil, shore, oil-gathering research and equipment; native grid and oil-processing trigger. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and c.crafting_queue_size==0);for _,p in pairs(game.connected_players) do assert(p.character==c) end;game.speed=4;for _,e in pairs(s.find_entities_filtered{force=f}) do if e~=c then e.destroy() end end;for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{112,64}}}) do if e~=c then e.destroy() end end;local tiles={};for x=-64,112 do for y=-64,64 do tiles[#tiles+1]={name=x< -20 and 'water' or 'grass-1',position={x,y}} end end;s.set_tiles(tiles);assert(c.teleport({0,0}));c.health=c.max_health;c.get_main_inventory().clear();f.technologies['steam-power'].researched=true;f.technologies['oil-gathering'].researched=true;f.technologies['oil-processing'].researched=false;for name,count in pairs{['offshore-pump']=1,boiler=1,['steam-engine']=1,['small-electric-pole']=50,inserter=1,pumpjack=1,coal=200} do assert(c.insert{name=name,count=count}==count) end;assert(s.create_entity{name='crude-oil',position={70,0},amount=100000});rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "prepared-distant-fluid-deposit", native = setup.RootElement.Clone(), reuse });
            var power = await new SteamPowerController(game, new ControllerJournal(journalPath + ".power")).RunAsync(token);
            evidence.Add(new { check = "native-steam-construction", power });
            string install = reuse ? "assert(c.remove_item{name='pumpjack',count=1}==1);assert(s.create_entity{name='pumpjack',position={70,0},force=f});" : "";
            string beforeCommand = "/silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];local b=s.find_entities_filtered{name='boiler',force=f}[1];assert(b);b.get_fuel_inventory().clear();b.burner.remaining_burning_fuel=0;b.energy=0;for i=1,#b.fluidbox do b.fluidbox[i]=nil end;for _,g in pairs(s.find_entities_filtered{type='generator',force=f}) do g.energy=0;for i=1,#g.fluidbox do g.fluidbox[i]=nil end end;" + install
                + "assert(c.teleport({65,0}));local d=s.find_entities_filtered{name='pumpjack',force=f}[1];rcon.print(helpers.table_to_json{tick=game.tick,researched=f.technologies['oil-processing'].researched,produced=f.get_fluid_production_statistics(s).get_input_count('crude-oil'),pumpjack=d and tostring(d.unit_number),carried=c.get_item_count('pumpjack'),character=c.unit_number,players=#game.connected_players})";
            using var beforeDocument = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(beforeCommand, token));
            var before = beforeDocument.RootElement.Clone();
            Require(!before.GetProperty("researched").GetBoolean(), "The resource trigger was already researched before extraction.");
            if (stationaryThreat)
            {
                // Observe the distant deposit normally before returning to the prepared blocked site.
                // Its history may guide exploration, but it cannot prove current local buildability.
                var remembered = await new SpatialClient(game).CaptureAsync(["pumpjack"], 48, token);
                Require(remembered.Entities.Any(e => e.Name == "crude-oil" && e.Position.DistanceTo(new(70, 0)) < 1),
                    "The safe deposit was not observed before the search.");
                const string danger = """
                    /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c.teleport({0,0}));assert(s.create_entity{name='crude-oil',position={0,25},amount=100000});local w=s.create_entity{name='small-worm-turret',position={0,35},force=game.forces.enemy};assert(w);rcon.print(helpers.table_to_json{tick=game.tick,worm=tostring(w.unit_number)})
                    """;
                using var dangerDocument = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(danger, token));
                var start = await new SpatialClient(game).CaptureAsync(["pumpjack"], 48, token);
                Require(start.Entities.Count(e => e.Name == "crude-oil") == 1
                    && start.StationaryThreats!.Any(t => t.Id == dangerDocument.RootElement.GetProperty("worm").GetString())
                    && new ResourceExtractionPlanner().FindSite(start, "crude-oil", "pumpjack", new HashSet<string>()) is null,
                    "The prepared search did not start with only an observed enemy-protected deposit.");
                evidence.Add(new { check = "protected-local-deposit-and-historical-safe-site", native = dangerDocument.RootElement.Clone(),
                    start.Actor.Position, start.CollectedTick, start.StationaryThreats });
            }
            var result = await new ResourceResearchController(game, journal).RunAsync("oil-processing", token);
            const string afterCommand = """
                /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];local ds=s.find_entities_filtered{name='pumpjack',force=f};assert(#ds==1);local d=ds[1];rcon.print(helpers.table_to_json{tick=game.tick,researched=f.technologies['oil-processing'].researched,produced=f.get_fluid_production_statistics(s).get_input_count('crude-oil'),pumpjack=tostring(d.unit_number),carried=c.get_item_count('pumpjack'),network=d.electric_network_id,energy=d.energy,character=c.unit_number,players=#game.connected_players})
                """;
            using var afterDocument = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(afterCommand, token));
            var after = afterDocument.RootElement.Clone();
            int links = 0, mines = 0, refills = 0, pumpBuilds = 0, deferredSearches = 0;
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "power-grid-link") links++;
                if (type == "resource-research-search" && data.GetProperty("deferredObservedResources").GetArrayLength() > 0) deferredSearches++;
                if (type != "submission") continue;
                string? kind = data.GetProperty("kind").GetString();
                var args = data.GetProperty("args");
                if (kind == "mine") mines++;
                if (kind == "build" && args.GetProperty("item").GetString() == "pumpjack") pumpBuilds++;
                if (kind == "insert" && args.GetProperty("entityId").GetString() == power.Entities["boiler"]) refills++;
            }
            evidence.Add(new { check = "native-distant-extraction", before, result, after, links, mines, refills, pumpBuilds, deferredSearches });
            if (stationaryThreat)
            {
                var final = await new SpatialClient(game).CaptureAsync(cancellationToken: token);
                var observed = await game.ExecuteAsync(GameRequest.Create("observe"), token);
                Require(deferredSearches > 0 && final.Entities.Single(e => e.Id == result.MachineId).Position.DistanceTo(new(70, 0)) < 1
                    && observed.Ok && observed.Data.GetProperty("agent").GetProperty("health").GetDouble() == 250,
                    "The protected deposit was not deferred before safe extraction without injury.");
            }
            Require(after.GetProperty("researched").GetBoolean() && result.ConnectedFluidStock > 0 && result.PoweredSamples > 0
                && after.GetProperty("produced").GetDouble() > before.GetProperty("produced").GetDouble(),
                "Native oil production and research have not both been proven.");
            Require(after.GetProperty("network").GetInt64() == power.NetworkId && links > 1 && refills > 0,
                "The distant pump lacks a verified extended steam network and boiler refill.");
            Require(mines == 0 && pumpBuilds == (reuse ? 0 : 1) && after.GetProperty("carried").GetInt32() == 0,
                "Unexpected manual mining or pump construction cost.");
            Require(after.GetProperty("pumpjack").GetString() == result.MachineId
                && (!reuse || before.GetProperty("pumpjack").GetString() == result.MachineId)
                && after.GetProperty("character").GetUInt32() == before.GetProperty("character").GetUInt32(),
                "The native extractor or controlled character changed unexpectedly.");
            if (factoryAdoption || expansion || deferExpansion) await VerifyFactoryAdoptionAsync(game, journal, result.MachineId, evidence, token);
            if (expansion) await VerifyExpansionAsync(game, journal, journalPath, result.MachineId, evidence, token);
            if (deferExpansion) await VerifyDeferredExpansionAsync(game, journal, journalPath, result.MachineId, evidence, token);
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-fluid-extraction-qualification", passed,
                isAutonomousCampaign = false, reuse, stationaryThreat, factoryAdoption, expansion, deferExpansion, journalPath, evidence }, CancellationToken.None);
        }
    }

    private async Task VerifyFactoryAdoptionAsync(IGameClient game, ControllerJournal journal, string drillId,
        List<object> evidence, CancellationToken token)
    {
        var registry = new FactoryRegistry(session.Directory);
        var before = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        var state = await registry.LoadAsync(before.Scope.WorldId, token);
        Require(state.Cells.All(c => c.Kind != FluidCellBuilder.ExtractorKind), "The prepared pump was already registered.");
        var builder = new FluidCellBuilder(game, journal, session.Directory);
        var rates = await builder.ExtractorRatesAsync("crude-oil", token);
        state = await registry.LoadAsync(before.Scope.WorldId, token);
        var adopted = state.Cells.Single(c => c.Kind == FluidCellBuilder.ExtractorKind);
        Require(adopted.Entities["drill"] == drillId && adopted.Plan?.ContainsKey("drill") == true
            && adopted.Entities.Count > 1 && rates.GetValueOrDefault(adopted.Id) > 0,
            "The native free extractor and grid plans were not adopted.");
        // A second observation must preserve the registry and native identity, even after the actor leaves the drill.
        var repeated = await builder.ExtractorRatesAsync("crude-oil", token);
        Require((await registry.LoadAsync(before.Scope.WorldId, token)).Cells.Count(c => c.Kind == FluidCellBuilder.ExtractorKind) == 1
            && repeated.GetValueOrDefault(adopted.Id) > 0, "Repeated observation duplicated the extractor cell.");
        evidence.Add(new { check = "native-existing-extractor-adopted-once", drillId, adopted, rates, repeated });
        const string kit = """
            /silent-command local f=game.forces.factorio_agent;local s=game.surfaces.nauvis;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and #game.connected_players==0);f.technologies['electronics'].researched=true;f.technologies['fluid-handling'].researched=true;for name,count in pairs{['oil-refinery']=1,pipe=100,['small-electric-pole']=40} do assert(c.insert{name=name,count=count}==count) end;rcon.print(helpers.table_to_json{tick=game.tick,providedRefineries=1,providedPipes=100,providedPoles=40,providedPumpjacks=0})
            """;
        using var supplied = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(kit, token));
        evidence.Add(new { check = "explicit-refinery-kit-electronics-and-fluid-handling-research", native = supplied.RootElement.Clone() });
        var damagedLink = adopted.Entities.First(p => p.Key.StartsWith("link-", StringComparison.Ordinal));
        string damage = "/silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local e;for _,p in pairs(s.find_entities_filtered{type='electric-pole',force=f}) do if tostring(p.unit_number)=='"
            + damagedLink.Value + "' then e=p;break end end;assert(e);local at=e.position;e.destroy();rcon.print(helpers.table_to_json{tick=game.tick,destroyed='"
            + damagedLink.Value + "',position=at})";
        using var destroyed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(damage, token));
        await using var controller = new SpatialController(game, journal);
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var repaired = await new FactoryMaintenance(game, journal, session.Directory).RunAsync(controller, catalog, token);
        state = await registry.LoadAsync(before.Scope.WorldId, token);
        var restored = state.Cells.Single(c => c.Id == adopted.Id);
        string rebuiltLink = restored.Entities[damagedLink.Key];
        var repairProof = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        Require(repaired.Rebuilt.Contains(rebuiltLink) && rebuiltLink != damagedLink.Value && FactoryPower.IsFed(repairProof, drillId) == true,
            "The adopted native power link was not rebuilt at its stored plan.");
        evidence.Add(new { check = "native-adopted-link-destroyed-and-rebuilt", native = destroyed.RootElement.Clone(), repaired,
            restored.Id, role = damagedLink.Key, oldId = damagedLink.Value, rebuiltLink, repairProof.CollectedTick });
        var refinery = await builder.BuildMachineAsync("oil-refinery", "basic-oil-processing", token);
        // The resource trigger reserved fuel for a small extraction proof only. Like the production executor,
        // reserve real carried coal for the new consumer before waiting for its output on this legacy steam supply.
        var recipe = catalog.Recipes.Single(r => r.Name == "basic-oil-processing");
        var machine = catalog.Assemblers!["oil-refinery"];
        double expectedEnergy = 10 * recipe.EnergySeconds * 60 * machine.EnergyPerTick / machine.CraftingSpeed;
        await new PoweredMachineController(game, journal).MaintainFuelAsync(refinery.Entities["machine"], expectedEnergy,
            catalog, controller, reserve: true, token: token);
        evidence.Add(new { check = "native-refinery-fuel-reserve", expectedEnergy, suppliedFrom = "original prepared actor coal through native transfers" });
        var current = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        for (int attempt = 0; current.FluidStockAt(refinery.Entities["machine"], "petroleum-gas") <= 0 && attempt < 30; attempt++)
        {
            var waited = await controller.WorkAsync("wait", new { ticks = 60 }, 180, token: token);
            Require(waited.Status == "completed", "The bounded native refinery observation wait did not complete.");
            current = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        }
        Require(current.Scope == before.Scope && current.FluidStockAt(refinery.Entities["machine"], "petroleum-gas") > 0,
            "The refinery did not produce native petroleum from the adopted extractor.");
        int extractors = current.Records.Count(r => r.Kind == "entity" && r.Name == "pumpjack");
        Require(extractors == 1 && current.Records.Any(r => r.Kind == "entity" && r.EntityId == drillId && r.Name == "pumpjack"),
            "The persistent refinery duplicated or replaced the initial extractor.");
        // Once connected, the adoption path must not claim this producer again or change its established pipe link.
        var connectedRates = await builder.ExtractorRatesAsync("crude-oil", token);
        state = await registry.LoadAsync(before.Scope.WorldId, token);
        Require(state.Cells.Count(c => c.Kind == FluidCellBuilder.ExtractorKind) == 1 && connectedRates.GetValueOrDefault(adopted.Id) > 0,
            "Connected extractor identity or capacity was lost.");
        evidence.Add(new { check = "native-refinery-reuses-initial-extractor", drillId, refinery, extractors,
            petroleum = current.FluidStockAt(refinery.Entities["machine"], "petroleum-gas"), current.CollectedTick, connectedRates });
    }

    private async Task VerifyExpansionAsync(IGameClient game, ControllerJournal journal, string journalPath, string originalDrill,
        List<object> evidence, CancellationToken token)
    {
        const string prepare = """
            /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and #game.connected_players==0);for _,e in pairs(s.find_entities_filtered{area={{-64,-160},{448,160}}}) do if e~=c and e.force~=f and not(e.name=='crude-oil' and math.abs(e.position.x-70)<1 and math.abs(e.position.y)<1) then e.destroy() end end;local tiles={};for x=-64,448 do for y=-160,160 do tiles[#tiles+1]={name=x< -20 and 'water' or 'grass-1',position={x,y}} end end;s.set_tiles(tiles);assert(c.teleport({65,0}));local at;for x=192,384,32 do local chunk={math.floor(x/32),0};if not f.is_chunk_charted(s,chunk) and not f.is_chunk_requested_for_charting(s,chunk) then at={x=x+0.5,y=0.5};break end end;assert(at,'No unread prepared expansion chunk');assert(s.create_entity{name='crude-oil',position=at,amount=300000});for name,count in pairs{pumpjack=1,['oil-refinery']=1,pipe=150,['small-electric-pole']=40,coal=200,['iron-plate']=100,['copper-plate']=100,['steel-plate']=50,['submachine-gun']=1,['firearm-magazine']=40,['heavy-armor']=1} do assert(c.insert{name=name,count=count}==count) end;rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,health=c.health,preparedDeposit=at,providedPumpjacks=1,providedRefineries=1,providedPipes=150,providedPoles=40})
            """;
        using var prepared = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
        var deposit = prepared.RootElement.GetProperty("preparedDeposit");
        var destination = new MapPosition(deposit.GetProperty("x").GetDouble(), deposit.GetProperty("y").GetDouble());
        evidence.Add(new { check = "explicit-expansion-terrain-deposit-kit-and-actor-reset", native = prepared.RootElement.Clone(),
            isAutonomousCampaign = false });
        var map = await new SpatialClient(game).CaptureAsync(["pumpjack", "pipe", "small-electric-pole"], 48, token);
        var charted = await new ChartedResourceClient(game).CaptureAsync(["crude-oil"], cancellationToken: token);
        evidence.Add(new { check = "expansion-start-resource-visibility", map.Actor.Position, map.CollectedTick,
            localOil = map.Entities.Where(e => e.Name == "crude-oil").ToArray(), charted.Deposits, charted.Coverage });
        Require(map.Entities.Any(e => e.Id == originalDrill) && map.Entities.Where(e => e.Name == "crude-oil")
            .All(e => e.Position.DistanceTo(destination) > 1)
            && charted.Deposits.All(d => d.Sample.Position.DistanceTo(destination) > 1),
            "The expansion must start with the occupied original deposit and no local or force-map destination for the new one.");
        var builder = new FluidCellBuilder(game, journal, session.Directory);
        var ratesBefore = await builder.ExtractorRatesAsync("crude-oil", token);
        var before = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        var registry = new FactoryRegistry(session.Directory);
        var state = await registry.LoadAsync(map.Scope.WorldId, token);
        Require(state.Cells.Count(c => c.Kind == FluidCellBuilder.ExtractorKind) == 1
            && before.Records.Count(r => r.Kind == "entity" && r.Name == "pumpjack") == 1,
            "Expansion must begin with exactly one registered native extractor.");
        var added = await builder.BuildExtractorAsync("crude-oil", "crude-oil", token);
        var discovered = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        Require(added.Status == "ready" && added.Entities["drill"] != originalDrill && added.Plan!["drill"].Position.DistanceTo(destination) < 1
            && discovered.Scope == map.Scope && FactoryPower.IsFed(discovered, added.Entities["drill"]) == true
            && discovered.FluidStockAt(added.Entities["drill"], "crude-oil") > 0,
            "The discovered deposit has no new powered native extractor with real crude oil.");
        var ratesAfter = await builder.ExtractorRatesAsync("crude-oil", token);
        Require(ratesAfter.Count == 2 && ratesAfter.Values.Sum() > ratesBefore.Values.Sum(), "Native extraction capacity did not increase.");
        var refinery = await builder.BuildMachineAsync("oil-refinery", "basic-oil-processing", token);
        await using var controller = new SpatialController(game, journal);
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        var recipe = catalog.Recipes.Single(r => r.Name == "basic-oil-processing");
        var machine = catalog.Assemblers!["oil-refinery"];
        double expectedEnergy = 10 * recipe.EnergySeconds * 60 * machine.EnergyPerTick / machine.CraftingSpeed;
        await new PoweredMachineController(game, journal).MaintainFuelAsync(refinery.Entities["machine"], expectedEnergy,
            catalog, controller, reserve: true, token: token);
        var produced = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        for (int attempt = 0; produced.FluidStockAt(refinery.Entities["machine"], "petroleum-gas") <= 0 && attempt < 30; attempt++)
        {
            Require((await controller.WorkAsync("wait", new { ticks = 60 }, 180, token: token)).Status == "completed", "Expansion production wait failed.");
            produced = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        }
        var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        var searches = rows.Where(r => r.GetProperty("type").GetString() == "fluid-extractor-exploration")
            .Select(r => r.GetProperty("data")).ToArray();
        var builds = rows.Where(r => r.GetProperty("type").GetString() == "submission"
            && r.GetProperty("data").GetProperty("kind").GetString() == "build"
            && r.GetProperty("data").GetProperty("args").GetProperty("item").GetString() == "pumpjack").ToArray();
        var refills = rows.Where(r => r.GetProperty("type").GetString() == "powered-machine-fuel"
            && r.GetProperty("data").GetProperty("machineId").GetString() == added.Entities["drill"])
            .Select(r => r.GetProperty("data")).ToArray();
        Require(searches.Any(r => r.GetProperty("remembered").ValueKind == JsonValueKind.Null)
            && searches.Length < FluidCellBuilder.MaximumExtractorSearchSteps && builds.Length == (reuse ? 1 : 2) && refills.Length > 0
            && produced.Scope == map.Scope && produced.FluidStockAt(refinery.Entities["machine"], "petroleum-gas") > 0
            && produced.Records.Count(r => r.Kind == "entity" && r.Name == "pumpjack") == 2
            && produced.Records.Any(r => r.Kind == "entity" && r.EntityId == originalDrill)
            && FactoryPower.IsFed(produced, refinery.Entities["machine"]) == true,
            "Exploration, extractor fuel transfer, exact additional cost, original identity or native second-refinery production is not proven.");
        evidence.Add(new { check = "native-fluid-expansion-after-occupied-known-site", map.Scope, startTick = map.CollectedTick,
            charted.Coverage, originalDrill, added, refinery, searches, refills, pumpBuilds = builds.Length, ratesBefore, ratesAfter,
            petroleum = produced.FluidStockAt(refinery.Entities["machine"], "petroleum-gas"), produced.CollectedTick });
    }

    private async Task VerifyDeferredExpansionAsync(IGameClient game, ControllerJournal journal, string journalPath, string originalDrill,
        List<object> evidence, CancellationToken token)
    {
        // Only this explicit fixture removes other deposits and supplies downstream equipment. Future lab terrain has
        // no autogenerated entities, so the bounded search must genuinely finish without finding a second oil site.
        const string prepare = """
            /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and #game.connected_players==0);local settings=s.map_gen_settings;settings.autoplace_settings={entity={treat_missing_as_default=false,settings={}}};s.map_gen_settings=settings;s.generate_with_lab_tiles=true;for _,e in pairs(s.find_entities_filtered{}) do if e~=c and e.force~=f and not(e.name=='crude-oil' and math.abs(e.position.x-70)<1 and math.abs(e.position.y)<1) then e.destroy() end end;f.technologies['plastics'].researched=true;for name,count in pairs{['chemical-plant']=1,inserter=8,['iron-chest']=8,pipe=200,['small-electric-pole']=100,coal=400,['iron-plate']=400,['copper-plate']=200,['steel-plate']=100,['stone-brick']=100,boiler=2,['steam-engine']=4,['submachine-gun']=1,['firearm-magazine']=40,['heavy-armor']=1} do assert(c.insert{name=name,count=count}==count) end;rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,providedChemicalPlants=1,providedCoal=400,futureEntitiesDisabled=true,futureLabTiles=true,crudeDeposits=#s.find_entities_filtered{name='crude-oil'},plasticProduced=f.get_item_production_statistics(s).get_input_count('plastic-bar')})
            """;
        using var prepared = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
        var before = prepared.RootElement.Clone();
        Require(before.GetProperty("crudeDeposits").GetInt32() == 1 && before.GetProperty("plasticProduced").GetDouble() == 0,
            "The deferred expansion fixture needs exactly its original oil deposit and no manufactured target.");
        evidence.Add(new { check = "explicit-missing-additional-oil-and-plastic-equipment", native = before, isAutonomousCampaign = false });
        var snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        var registry = new FactoryRegistry(session.Directory);
        var original = (await registry.LoadAsync(snapshot.Scope.WorldId, token)).Cells.Single(c => c.Kind == FluidCellBuilder.MachineKind);
        const double target = 120;
        await registry.SaveAsync((await registry.LoadAsync(snapshot.Scope.WorldId, token)).WithTarget("plastic-bar", target), token);
        var plan = await new FluidChainDirector(game, journal, session.Directory).AutomateAsync("plastic-bar", target, token);
        var state = await registry.LoadAsync(snapshot.Scope.WorldId, token);
        var plastics = state.Cells.Where(c => c.Kind == FluidCellBuilder.MachineKind && c.Recipe == "plastic-bar" && c.Status == "ready").ToArray();
        Require(plastics.Length >= plan.Stages.Single(s => s.Recipe == "plastic-bar").Machines,
            "The planned downstream plastic machines were not all constructed.");
        Require(plan.PerMinute == target && state.Targets?.GetValueOrDefault("plastic-bar") == target
            && state.Cells.Count(c => c.Kind == FluidCellBuilder.ExtractorKind) == 1
            && state.Cells.Single(c => c.Id == original.Id).Entities["machine"] == original.Entities["machine"],
            "Deferred expansion lowered its complete target, duplicated extraction or replaced the original refinery.");
        await using var controller = new SpatialController(game, journal);
        var logistics = new FactoryLogistics(game, journal, session.Directory);
        long collected = 0;
        for (int round = 0; collected == 0 && round < 12; round++)
        {
            var service = await logistics.ServiceAsync(5, token);
            collected += service.Collected.GetValueOrDefault("plastic-bar");
            if (collected == 0)
                Require((await controller.WorkAsync("wait", new { ticks = 600 }, 900, token: token)).Status == "completed",
                    "The bounded downstream production wait did not complete.");
        }
        const string read = """
            /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];local js=s.find_entities_filtered{name='pumpjack',force=f};assert(#js==1);rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,pumpjack=tostring(js[1].unit_number),plasticProduced=f.get_item_production_statistics(s).get_input_count('plastic-bar'),gasConsumed=f.get_fluid_production_statistics(s).get_output_count('petroleum-gas')})
            """;
        using var observed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(read, token));
        var after = observed.RootElement.Clone();
        snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
        var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var deferred = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "fluid-chain-stage-short")
                .Select(r => r.RootElement.GetProperty("data"))
                .Single(d => d.TryGetProperty("reason", out var reason) && reason.GetString() == "extractor-search-exhausted");
            Require(deferred.GetProperty("observations").GetInt32() == FluidCellBuilder.MaximumExtractorSearchSteps
                && deferred.GetProperty("source").GetProperty("unitsPerMinute").GetDouble() > deferred.GetProperty("extractionBeforeSearch").GetDouble()
                && deferred.GetProperty("targetRetained").GetBoolean() && collected > 0
                && after.GetProperty("plasticProduced").GetDouble() > 0 && after.GetProperty("gasConsumed").GetDouble() > 0
                && after.GetProperty("pumpjack").GetString() == originalDrill
                && after.GetProperty("character").GetUInt32() == before.GetProperty("character").GetUInt32()
                && after.GetProperty("players").GetInt32() == 0 && plastics.All(c => FactoryPower.IsFed(snapshot, c.Entities["machine"]) == true),
                "Bounded missing-site refusal, retained deficit, original identities or native downstream production was not proven.");
            evidence.Add(new { check = "native-downstream-production-after-exhausted-oil-search", plan, deferred = deferred.Clone(),
                plastics, originalDrill, originalRefinery = original.Entities["machine"], collected, before, after,
                registeredTarget = state.Targets!["plastic-bar"], provesSustainedTargetRate = false });
        }
        finally { foreach (var row in rows) row.Dispose(); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
