using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Explicit prepared native test: exhausts a built cell, protects its circuits and stock, then reuses its paid machinery on fresh ore.</summary>
public sealed class ResourceEquipmentReuseQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Equipment reuse qualification requires an explicit fixture.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        token = deadline.Token;
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"resource-equipment-reuse-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        var registry = new FactoryRegistry(session.Directory);
        var reader = new FactorySnapshotClient(game);
        bool passed = false;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared reuse: injected ore, power, research and kit; exhausted ore and circuit fault. Not a campaign." }), token);
            if (!marked.Ok) throw new GameRpcException(marked.Error ?? new("invalid_response", "Fixture marker rejected."));
            const string preparation = """
                /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and c.crafting_queue_size==0 and #game.connected_players==0);game.speed=1;for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{64,64}}}) do if e~=c then e.destroy() end end;local tiles={};for x=-64,64 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end;s.set_tiles(tiles);assert(c.teleport({0,0}));c.health=c.max_health;c.get_main_inventory().clear();for _,t in pairs{'steam-power','electronics','electric-mining-drill','advanced-material-processing'} do f.technologies[t].researched=true end;for name,count in pairs{['electric-mining-drill']=1,['steel-furnace']=1,inserter=2,['iron-chest']=2,['small-electric-pole']=30,coal=48} do assert(c.insert{name=name,count=count}==count) end;local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f};assert(source);source.electric_buffer_size=1000000000;source.power_production=2000000;source.energy=1000000000;assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f});for x=4,15 do for y=-6,5 do assert(s.create_entity{name='iron-ore',position={x+0.5,y+0.5},amount=5000}) end end;for x=28,39 do for y=-6,5 do assert(s.create_entity{name='iron-ore',position={x+0.5,y+0.5},amount=5000}) end end;rcon.print(helpers.table_to_json{tick=game.tick,fixture=true,players=#game.connected_players,providedDrills=1,providedSteelFurnaces=1,providedCoal=48})
                """;
            using var prepared = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(preparation, token));
            evidence.Add(new { check = "explicit-reuse-fixture", native = prepared.RootElement.Clone() });
            File.Delete(registry.Path);
            var builder = new ResourceCellBuilder(game, journal, session.Directory);
            var old = await builder.BuildNextAsync("iron-plate", 30, token, explorationBudget: 0);
            Require(old.Plan!["furnace"].Item == "steel-furnace", "Prepared initial cell must use the steel furnace.");
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            await using var controller = new SpatialController(game, journal);
            await WaitAsync(600);
            using var exhausted = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                $"/silent-command local d;for _,e in pairs(game.surfaces.nauvis.find_entities_filtered{{type='mining-drill',force='factorio_agent'}}) do if e.unit_number=={old.Entities["drill"]} then d=e end end;assert(d);local p=d.position;local n=0;for _,e in pairs(d.surface.find_entities_filtered{{type='resource',area={{{{p.x-6,p.y-6}},{{p.x+6,p.y+6}}}}}}) do e.destroy();n=n+1 end;rcon.print(helpers.table_to_json{{fixture=true,removedOreEntities=n,tick=game.tick}})", token));
            for (int round = 0; round < 10; round++)
            {
                await WaitAsync(300);
                var stock = await CaptureAsync("exhaustion-" + round);
                if (stock.Records.Single(r => r.Kind == "work" && r.EntityId == old.Entities["drill"]).Data.GetProperty("statusName").GetString() != "no_minable_resources") continue;
                if (stock.Records.Single(r => r.Kind == "work" && r.EntityId == old.Entities["furnace"]).Data.GetProperty("inProcess").GetBoolean()) continue;
                if (FactoryLogistics.Items(stock, old.Entities["furnace"]).Any(p => p.Key != "coal" && p.Value > 0)) continue;
                var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                var changed = ResourceCellHealth.Inspect(stock, [old]).Single();
                Require(changed.Status == ResourceCellHealth.Depleted, "Native drill exhaustion did not retire its cell.");
                await registry.SaveAsync(state.With(changed), token);
                break;
            }
            Require((await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells.Single(c => c.Id == old.Id).Status == ResourceCellHealth.Depleted,
                "Initial cell never exhausted within the fixture budget.");
            var before = await CaptureAsync("before-reuse");
            long fuelBefore = before.SummarizeStocks().InventoryItems.GetValueOrDefault("coal");
            long retainedIron = FactoryLogistics.Items(before, old.Entities["output-chest"]).GetValueOrDefault("iron-plate");
            Require(retainedIron > 0 && FactoryLogistics.Carried(before).GetValueOrDefault("electric-mining-drill") == 0
                && FactoryLogistics.Carried(before).GetValueOrDefault("steel-furnace") == 0, "Fixture must need the installed machinery and retain its produced plates.");
            using var wired = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                $"/silent-command local s=game.surfaces.nauvis;local d,p;for _,e in pairs(s.find_entities_filtered{{force='factorio_agent'}}) do if e.unit_number=={old.Entities["drill"]} then d=e end;if e.unit_number=={old.Entities["pole"]} then p=e end end;assert(d and p);assert(d.get_wire_connector(defines.wire_connector_id.circuit_green,true).connect_to(p.get_wire_connector(defines.wire_connector_id.circuit_green,true),false,defines.wire_origin.player));rcon.print(helpers.table_to_json{{fixture=true,tick=game.tick}})", token));
            var recovery = new ResourceEquipmentReuse(game, journal);
            await recovery.RecoverAsync(registry, catalog, new Dictionary<string, int> { ["electric-mining-drill"] = 1 }, token);
            var protectedWire = await CaptureAsync("green-wire-protected");
            Require(protectedWire.Records.Any(r => r.Kind == "entity" && r.EntityId == old.Entities["drill"])
                && FactoryLogistics.Carried(protectedWire).GetValueOrDefault("electric-mining-drill") == 0, "Connected drill was dismantled.");
            evidence.Add(new { check = "native-green-wire-refuses-recovery", drill = old.Entities["drill"], protectedWire.CollectedTick });
            using var disconnected = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                $"/silent-command local d;for _,e in pairs(game.surfaces.nauvis.find_entities_filtered{{type='mining-drill',force='factorio_agent'}}) do if e.unit_number=={old.Entities["drill"]} then d=e end end;assert(d);assert(d.get_wire_connector(defines.wire_connector_id.circuit_green,false).disconnect_all(defines.wire_origin.player));rcon.print(helpers.table_to_json{{fixture=true,tick=game.tick}})", token));
            var needed = new Dictionary<string, int> { ["electric-mining-drill"] = 1, ["steel-furnace"] = 1 };
            await recovery.RecoverAsync(registry, catalog, needed, token);
            var recovered = await CaptureAsync("recovered");
            var retired = (await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells.Single(c => c.Id == old.Id);
            Require(FactoryLogistics.Carried(recovered).GetValueOrDefault("electric-mining-drill") == 1
                && FactoryLogistics.Carried(recovered).GetValueOrDefault("steel-furnace") == 1
                && recovered.SummarizeStocks().InventoryItems.GetValueOrDefault("coal") == fuelBefore
                && FactoryLogistics.Items(recovered, old.Entities["output-chest"]).GetValueOrDefault("iron-plate") == retainedIron,
                "Equipment, coal or retained plates were lost during recovery.");
            Require(retired.Status == ResourceCellHealth.Depleted && !retired.Entities.ContainsKey("drill") && !retired.Entities.ContainsKey("furnace")
                && old.Entities.Where(p => p.Key is not ("drill" or "furnace")).All(p => retired.Entities.GetValueOrDefault(p.Key) == p.Value
                    && recovered.Records.Any(r => r.Kind == "entity" && r.EntityId == p.Value)), "Recovery changed retained stock, arms or grid entities.");
            evidence.Add(new { check = "two-native-parts-and-fuel-recovered", recovered.CollectedTick, fuelBefore, retainedIron, retired });
            await recovery.RecoverAsync(registry, catalog, needed, token);
            var replacement = await builder.BuildNextAsync("iron-plate", 30, token, explorationBudget: 0);
            await WaitAsync(1800);
            var after = await CaptureAsync("replacement-production");
            Require(replacement.Id != old.Id && replacement.Plan!["furnace"].Item == "steel-furnace"
                && replacement.Entities["drill"] != old.Entities["drill"] && replacement.Entities["furnace"] != old.Entities["furnace"]
                && FactoryLogistics.Items(after, replacement.Entities["output-chest"]).GetValueOrDefault("iron-plate") >= 5,
                "Recovered kit did not become a producing cell on fresh native ore.");
            var events = File.ReadLines(journalPath).Select(line => JsonSerializer.Deserialize<JsonElement>(line, Protocol.Json)).ToArray();
            var submits = events.Where(e => e.GetProperty("type").GetString() == "submission").Select(e => e.GetProperty("data")).ToArray();
            var mines = submits.Where(s => s.GetProperty("kind").GetString() == "mine").ToArray();
            Require(mines.Length == 2 && mines.Select(s => s.GetProperty("args").GetProperty("entityId").GetString()).ToHashSet()
                .SetEquals([old.Entities["drill"], old.Entities["furnace"]]) && !submits.Any(s => s.GetProperty("kind").GetString() == "craft"),
                "Replacement crafted equipment, mined raw resources, or repeated recovery.");
            evidence.Add(new { check = "reused-kit-paid-rebuild-and-native-production", replacement, after.CollectedTick,
                plates = FactoryLogistics.Items(after, replacement.Entities["output-chest"]), equipmentMines = mines.Length, crafted = 0 });
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-resource-equipment-reuse", passed, isAutonomousCampaign = false,
                normalRocketProof = false, cloudCalls = false, journalPath, evidence }, CancellationToken.None);
        }

        async Task WaitAsync(int ticks) => Require((await controllerWork(ticks)).Status == "completed", "Native fixture wait failed.");
        async Task<OperationReceipt> controllerWork(int ticks)
        {
            await using var controller = new SpatialController(game, journal);
            return await controller.WorkAsync("wait", new { ticks }, ticks + 600, token: token);
        }
        async Task<FactorySnapshot> CaptureAsync(string name)
        {
            var snapshot = await reader.CaptureAsync(cancellationToken: token);
            await LocalJson.WriteAsync(Path.Combine(session.Directory, name + ".json"), snapshot, token);
            return snapshot;
        }
        static void Require(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
    }
}
