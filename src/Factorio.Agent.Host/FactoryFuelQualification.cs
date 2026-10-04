using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepared coal stock and running furnaces; verifies collection and preventive native refills, never a campaign.</summary>
public sealed class FactoryFuelQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture || File.Exists(new FactoryRegistry(session.Directory).Path))
            throw new InvalidOperationException("Factory fuel qualification requires a fresh explicit fixture.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        token = deadline.Token;
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"factory-fuel-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared furnace fuel: terrain,10 furnaces,chest,1000 source coal,20 coal and50 ore per furnace. Not a campaign." }), token);
            if (!mark.Ok) throw new GameRpcException(mark.Error!);
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0 and #game.connected_players==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{64,64}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-64,64 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); assert(c.insert{name='stone-furnace',count=10}==10); assert(c.insert{name='iron-chest',count=1}==1); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,furnaces=10,chests=1})
                """, token));
            evidence.Add(new { check = "explicit-construction-kit", native = setup.RootElement.Clone() });
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var registry = new FactoryRegistry(session.Directory);
            await using var controller = new SpatialController(game, journal);
            var builder = new PoweredMachineController(game, journal);
            var furnaces = new List<string>();
            for (int index = 0; index < 10; index++)
            {
                // This scattered row is explicit fixture geometry; normal production uses the native resource planners.
                var at = new MapPosition(-22 + 5 * index, -10);
                string id = await builder.BuildAtAsync("stone-furnace", new(at, 0, 0), catalog, controller, token);
                Require(long.TryParse(id, out _), "Expected a native furnace id.");
                furnaces.Add(id);
                var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                await registry.SaveAsync(state.With(new FactoryCell("fuel-" + id, 0, new(0, 0, true), "smelter", "stone-furnace", "iron-plate",
                    new Dictionary<string, string> { ["furnace"] = id }, "ready", catalog.CollectedTick,
                    Plan: new Dictionary<string, PlannedEntity> { ["furnace"] = new("furnace", "stone-furnace", at, 0) })), token);
            }
            var sourcePosition = new MapPosition(-35.5, 32.5);
            string chest = await builder.BuildAtAsync("iron-chest", new(sourcePosition, 0, 0), catalog, controller, token);
            Require(long.TryParse(chest, out _), "Expected a native coal chest id.");
            var current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            await registry.SaveAsync(current.With(new FactoryCell("coal-stock", 0, new(0, 0, true), "buffer", "iron-chest", null,
                new Dictionary<string, string> { ["output-chest"] = chest }, "ready", catalog.CollectedTick,
                Plan: new Dictionary<string, PlannedEntity> { ["output-chest"] = new("output-chest", "iron-chest", sourcePosition, 0) })), token);
            string ids = string.Join(",", furnaces);
            string reply = await session.CreateRcon().ExecuteAsync($$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local ids={}; for _,id in pairs{ {{ids}} } do ids[id]=true end; local supplied=0; for _,e in pairs(s.find_entities_filtered{type='furnace',force=f}) do if ids[e.unit_number] then assert(e.get_fuel_inventory().insert{name='coal',count=20}==20); assert(e.get_inventory(defines.inventory.furnace_source).insert{name='iron-ore',count=50}==50); supplied=supplied+1 end end; assert(supplied==10); local chest; for _,e in pairs(s.find_entities_filtered{type='container',force=f}) do if e.unit_number=={{chest}} then chest=e end end; assert(chest and chest.insert{name='coal',count=1000}==1000); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,furnaces=supplied,sourceCoal=1000,fuelPerFurnace=20,orePerFurnace=50})
                """, token);
            await journal.AppendAsync("prepared-fuel-inputs-reply", new { reply }, token);
            using var supplied = JsonDocument.Parse(reply);
            evidence.Add(new { check = "explicit-running-furnace-inputs", native = supplied.RootElement.Clone() });
            var snapshots = new FactorySnapshotClient(game);
            var before = await snapshots.CaptureAsync(cancellationToken: token);
            Require(furnaces.All(id => Coal(before, id) is >= 12 and < 25), "Prepared furnaces must run above ignition but below the preventive threshold.");
            var first = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(token: token);
            var after = await snapshots.CaptureAsync(cancellationToken: token);
            var nativeBurners = furnaces.Select(id => new { id, before = Coal(before, id), after = Coal(after, id),
                crafts = after.Records.Single(r => r.Kind == "work" && r.EntityId == id).Data.GetProperty("productsFinished").GetInt64() }).ToArray();
            var refuelled = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "submission") Require(data.GetProperty("kind").GetString() is not ("craft" or "mine"), "Prepared fuel service must not manufacture or mine manually.");
                if (type == "receipt" && data.GetProperty("kind").GetString() == "insert"
                    && data.GetProperty("effects").GetProperty("item").GetString() == "coal"
                    && data.GetProperty("effects").GetProperty("transferred").GetInt64() > 0)
                    refuelled.Add(data.GetProperty("effects").GetProperty("targetId").GetString()!);
            }
            evidence.Add(new { check = "native-full-fuel-tour", startTick = before.CollectedTick, endTick = after.CollectedTick, first,
                nativeBurners, refuelled, carriedCoal = FactoryLogistics.Carried(after).GetValueOrDefault("coal") });
            Require(first.Collected.GetValueOrDefault("coal") >= 300 && first.Supplied.GetValueOrDefault("coal") >= 300
                && refuelled.SetEquals(furnaces) && nativeBurners.All(b => b.after >= 12) && nativeBurners.Sum(b => b.crafts) > 0
                && first.Shortfall.GetValueOrDefault("coal") == 0 && !first.PowerStarved && after.Scope == before.Scope,
                "Collected native coal did not refill every running furnace while maintaining production.");
            var recent = await new FactoryLogistics(game, journal, session.Directory)
                .ServiceAsync(token: token, minimumIntervalTicks: FactoryLogistics.BetweenGoalsFreshnessTicks);
            evidence.Add(new { check = "healthy-native-tour-reused", recent });
            Require(recent.Actions == 0 && (await File.ReadAllLinesAsync(journalPath, token))
                .Any(line => line.Contains("\"type\":\"factory-logistics-recent-tour\"", StringComparison.Ordinal)), "Healthy fuel reserves did not permit recent-tour reuse.");

            // Reproduce a normal-world bootstrap stall: a healthy feeder must not consume the ignition share of dry producers.
            string feederKitReply = await session.CreateRcon().ExecuteAsync("""
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; assert(c.insert{name='boiler',count=1}==1); assert(c.insert{name='iron-chest',count=1}==1); assert(c.insert{name='inserter',count=1}==1); rcon.print(helpers.table_to_json{tick=game.tick,boilers=1,chests=1,inserters=1})
                """, token);
            await journal.AppendAsync("prepared-feeder-kit-reply", new { reply = feederKitReply }, token);
            using var feederKit = JsonDocument.Parse(feederKitReply);
            string boiler = await builder.BuildAtAsync("boiler", new(new(32.5, 20), 0, 0), catalog, controller, token);
            string feeder = await builder.BuildAtAsync("iron-chest", new(new(29.5, 20.5), 0, 0), catalog, controller, token);
            string inserter = await builder.BuildAtAsync("inserter", new(new(30.5, 20.5), 12, 0), catalog, controller, token);
            Require(long.TryParse(boiler, out _) && long.TryParse(feeder, out _), "Expected native feeder ids.");
            current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            await registry.SaveAsync(current.With(new FactoryCell("power-scarce-coal", 0, new(0, 0, true), "power", "boiler", null,
                new Dictionary<string, string> { ["boiler"] = boiler, ["input-chest"] = feeder, ["input-inserter"] = inserter },
                "ready", catalog.CollectedTick)), token);
            string scarceReply = await session.CreateRcon().ExecuteAsync($$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; c.get_main_inventory().clear(); assert(c.insert{name='coal',count=120}==120); local ids={}; for _,id in pairs{ {{ids}} } do ids[id]=true end; local dry=0; for _,e in pairs(s.find_entities_filtered{type='furnace',force=f}) do if ids[e.unit_number] then e.get_fuel_inventory().clear(); e.burner.remaining_burning_fuel=0; e.get_inventory(defines.inventory.furnace_source).clear(); assert(e.get_inventory(defines.inventory.furnace_source).insert{name='iron-ore',count=50}==50); dry=dry+1 end end; assert(dry==10); for _,e in pairs(s.find_entities_filtered{type='container',force=f}) do if e.unit_number=={{chest}} then e.get_inventory(defines.inventory.chest).clear() elseif e.unit_number=={{feeder}} then assert(e.insert{name='coal',count=130}==130) end end; for _,e in pairs(s.find_entities_filtered{type='boiler',force=f}) do if e.unit_number=={{boiler}} then assert(e.get_fuel_inventory().insert{name='coal',count=20}==20) end end; rcon.print(helpers.table_to_json{tick=game.tick,dryFurnaces=dry,orePerFurnace=50,carriedCoal=120,feederCoal=130,boilerCoal=20,sourceCoal=0})
                """, token);
            await journal.AppendAsync("prepared-scarce-fuel-inputs-reply", new { reply = scarceReply }, token);
            using var scarceSetup = JsonDocument.Parse(scarceReply);
            evidence.Add(new { check = "explicit-scarce-fuel-inputs", kit = feederKit.RootElement.Clone(), native = scarceSetup.RootElement.Clone() });
            var scarceBefore = await snapshots.CaptureAsync(cancellationToken: token);
            var scarceResult = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(token: token);
            var productionWait = await controller.WorkAsync("wait", new { ticks = 600 }, 900, token: token);
            Require(productionWait.Status == "completed", "The restarted producers could not be observed through native craft cycles.");
            var scarceAfter = await snapshots.CaptureAsync(cancellationToken: token);
            long Crafts(FactorySnapshot photo, string id) => photo.Records.Single(r => r.Kind == "work" && r.EntityId == id)
                .Data.GetProperty("productsFinished").GetInt64();
            var restarted = furnaces.Select(id => new { id, before = Coal(scarceBefore, id), after = Coal(scarceAfter, id),
                craftsBefore = Crafts(scarceBefore, id), craftsAfter = Crafts(scarceAfter, id) }).ToArray();
            evidence.Add(new { check = "native-scarce-fuel-restarts-producers", scarceResult, restarted,
                feederBefore = Coal(scarceBefore, feeder), feederAfter = Coal(scarceAfter, feeder), scarceAfter.CollectedTick });
            Require(restarted.All(b => b.before == 0 && b.after > 0 && b.craftsAfter > b.craftsBefore)
                && Coal(scarceAfter, feeder) == Coal(scarceBefore, feeder) && scarceResult.Supplied.GetValueOrDefault("coal") == 120
                && scarceResult.Shortfall.GetValueOrDefault("coal") == 0 && !scarceResult.PowerStarved,
                "A healthy feeder consumed scarce ignition coal while native producers stayed dry.");
            // A separate prepared startup: two dry furnaces, a low boiler, no carried/stored coal, and a real coal drill.
            // Keep the other eight fixtures physically present but outside this phase's explicit service registry.
            string startupReply = await session.CreateRcon().ExecuteAsync($$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; c.get_main_inventory().clear(); assert(c.insert{name='burner-mining-drill',count=1}==1); assert(c.teleport({-35,36})); local ids={}; for _,id in pairs{ {{ids}} } do ids[id]=true end; for _,e in pairs(s.find_entities_filtered{type='furnace',force=f}) do if ids[e.unit_number] then e.get_fuel_inventory().clear(); e.burner.remaining_burning_fuel=0; e.get_inventory(defines.inventory.furnace_source).clear(); e.get_inventory(defines.inventory.furnace_result).clear(); assert(e.get_inventory(defines.inventory.furnace_source).insert{name='iron-ore',count=50}==50) end end; for _,e in pairs(s.find_entities_filtered{type='container',force=f}) do if e.unit_number=={{chest}} or e.unit_number=={{feeder}} then e.get_inventory(defines.inventory.chest).clear() end end; for _,e in pairs(s.find_entities_filtered{type='boiler',force=f}) do if e.unit_number=={{boiler}} then e.get_fuel_inventory().clear(); e.burner.remaining_burning_fuel=0; assert(e.get_fuel_inventory().insert{name='coal',count=3}==3) end end; for x=-37,-32 do for y=33,38 do for _,e in pairs(s.find_entities_filtered{area={ {x,y}, {x+1,y+1} },type='resource'}) do e.destroy() end; assert(s.create_entity{name='coal',position={x+.5,y+.5},amount=10000}) end end; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,carriedCoal=c.get_item_count('coal'),sourceCoal=0,coalPatch=true,boilerCoal=3,selectedFurnaces=2,orePerFurnace=50})
                """, token);
            using var startupSetup = JsonDocument.Parse(startupReply);
            evidence.Add(new { check = "explicit-cold-stage-and-coal-patch", native = startupSetup.RootElement.Clone() });
            var drillAt = new MapPosition(-35, 34);
            string coalDrill = await builder.BuildAtAsync("burner-mining-drill", new(drillAt, 0, 0), catalog, controller, token);
            string drillReply = await session.CreateRcon().ExecuteAsync($$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local d; for _,e in pairs(s.find_entities_filtered{type='mining-drill',force=f}) do if e.unit_number=={{coalDrill}} then d=e; break end end; assert(d and d.name=='burner-mining-drill' and d.get_fuel_inventory().insert{name='coal',count=2}==2); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,drill=d.unit_number,starterCoal=2,carriedCoal=c.get_item_count('coal')})
                """, token);
            await journal.AppendAsync("prepared-drill-starter-reply", new { reply = drillReply }, token);
            using var drillSetup = JsonDocument.Parse(drillReply);
            evidence.Add(new { check = "explicit-drill-starter", native = drillSetup.RootElement.Clone() });
            var selected = furnaces.Take(2).Select(id => "fuel-" + id).ToHashSet(StringComparer.Ordinal);
            current = await registry.LoadAsync(catalog.Scope.WorldId, token);
            current = current with { Cells = current.Cells.Where(c => selected.Contains(c.Id) || c.Kind == "power" || c.Id == "coal-stock").ToArray() };
            current = current.With(new FactoryCell("coal-stock", 0, new(0, 0, true), "miner", "burner-mining-drill", "coal",
                new Dictionary<string, string> { ["drill"] = coalDrill, ["output-chest"] = chest }, "ready", catalog.CollectedTick,
                Plan: new Dictionary<string, PlannedEntity> { ["drill"] = new("drill", "burner-mining-drill", drillAt, 0),
                    ["output-chest"] = new("output-chest", "iron-chest", sourcePosition, 0) }));
            await registry.SaveAsync(current, token);
            var stageBefore = await snapshots.CaptureAsync(cancellationToken: token);
            int journalStart = (await File.ReadAllLinesAsync(journalPath, token)).Length;
            await new FactoryDirector(game, journal, session.Directory).StartStageAsync(
                new AutomationStage("iron-plate", "iron-plate", "stone-furnace", 6, 2, "smelter"), catalog, selected, token);
            var stageWait = await controller.WorkAsync("wait", new { ticks = 600 }, 900, token: token);
            Require(stageWait.Status == "completed", "Cannot observe restarted stage production.");
            var stageAfter = await snapshots.CaptureAsync(cancellationToken: token);
            JsonElement stageWitness = default;
            foreach (string line in (await File.ReadAllLinesAsync(journalPath, token)).Skip(journalStart))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "submission") Require(data.GetProperty("kind").GetString() is not ("craft" or "mine" or "build"), "Fuel startup must reuse native coal extraction without crafting, mining or additional construction.");
                if (type == "factory-stage-startup") stageWitness = data.Clone();
            }
            Require(stageWitness.ValueKind == JsonValueKind.Object, "Missing stage startup witness.");
            long demanded = stageWitness.GetProperty("initialStartup").GetProperty("fuelShortfall").GetInt64();
            long targetCoal = stageWitness.GetProperty("fuelProcurement").GetProperty("targetStock").GetInt64();
            var stageBurners = furnaces.Take(2).Select(id => new { id, before = Coal(stageBefore, id), after = Coal(stageAfter, id),
                craftsBefore = Crafts(stageBefore, id), craftsAfter = Crafts(stageAfter, id) }).ToArray();
            evidence.Add(new { check = "native-stage-procures-ignition-and-restarts-before-returning", stageWitness, stageBurners,
                targetCoal, demanded, drillCoal = Coal(stageAfter, coalDrill), boilerCoal = Coal(stageAfter, boiler),
                feederCoal = Coal(stageAfter, feeder), startTick = stageBefore.CollectedTick, endTick = stageAfter.CollectedTick });
            Require(demanded is > 0 and <= 60 && targetCoal is > 0 and <= 60 && stageBurners.All(b => b.before == 0 && b.after > 0 && b.craftsAfter > b.craftsBefore)
                && Coal(stageAfter, coalDrill) > 0 && Coal(stageAfter, boiler) + Coal(stageAfter, feeder) >= 12 && stageAfter.Scope == stageBefore.Scope,
                "Bounded native stage ignition did not restart coal and both furnaces while preserving boiler supply.");
            using var final = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,craftingQueue=c.crafting_queue_size})
                """, token));
            Require(final.RootElement.GetProperty("character").GetInt64() == setup.RootElement.GetProperty("character").GetInt64()
                && final.RootElement.GetProperty("players").GetInt32() == 0 && final.RootElement.GetProperty("craftingQueue").GetInt32() == 0, "Headless actor identity changed.");
            evidence.Add(new { check = "native-headless-identity", native = final.RootElement.Clone() });
            passed = true;
            return path;
        }
        finally { await LocalJson.WriteAsync(path, new { kind = "prepared-factory-fuel-qualification", passed, isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None); }
    }

    private static long Coal(FactorySnapshot snapshot, string id) => FactoryLogistics.Items(snapshot, id).GetValueOrDefault("coal");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
