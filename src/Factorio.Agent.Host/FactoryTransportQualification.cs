using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Explicit prepared fixture: construction kit, plates, research and power; no gears or science are supplied.</summary>
public sealed class FactoryTransportQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Persistent transport qualification requires a fresh explicit fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"factory-transport-qualification-{Guid.NewGuid():N}.json");
        var journal = new ControllerJournal(Path.ChangeExtension(path, ".jsonl"));
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared belt fixture: power/research, equipment/plates; no gears/science. Explicit destruction for repair. Not a campaign." }), token);
            if (!marked.Ok) throw new GameRpcException(marked.Error!);
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0 and #game.connected_players==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); f.technologies['steam-power'].researched=true; f.technologies.electronics.researched=true; f.technologies.automation.researched=true; f.technologies['automation-science-pack'].researched=true; for name,count in pairs{['assembling-machine-1']=3,inserter=12,['iron-chest']=6,['small-electric-pole']=30,['transport-belt']=200,['iron-plate']=400,['copper-plate']=100} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=3000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,gears=f.get_item_production_statistics(s).get_input_count('iron-gear-wheel'),science=f.get_item_production_statistics(s).get_input_count('automation-science-pack')})
                """, token));
            Require(setup.RootElement.GetProperty("gears").GetDouble() == 0 && setup.RootElement.GetProperty("science").GetDouble() == 0,
                "The prepared world must start with zero native intermediate/output production.");
            File.Delete(new FactoryRegistry(session.Directory).Path);
            evidence.Add(new { check = "explicit-preparation", native = setup.RootElement.Clone() });
            var builder = new FactoryCellBuilder(game, journal, session.Directory);
            var gears = await builder.BuildAsync("assembler", "assembling-machine-1", "iron-gear-wheel", token);
            var first = await builder.BuildAsync("assembler", "assembling-machine-1", "automation-science-pack", token);
            var second = await builder.BuildAsync("assembler", "assembling-machine-1", "automation-science-pack", token);
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            await using var controller = new SpatialController(game, journal);
            var transports = new FactoryTransportBuilder(game, journal, session.Directory);
            Require(await transports.LinkAsync(gears.Id, first.Id, "iron-gear-wheel", 3, catalog, controller, token), "First persistent bus has no observed route.");
            var registry = new FactoryRegistry(session.Directory);
            var before = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var original = before.Cells.Single(c => c.Id == before.Transports!.Single().CellId);
            Require(await transports.LinkAsync(gears.Id, second.Id, "iron-gear-wheel", 3, catalog, controller, token), "The existing bus cannot extend to its second consumer.");
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var bus = state.Transports!.Single();
            var transportCell = state.Cells.Single(c => c.Id == bus.CellId);
            Require(bus.Consumers.Count == 2 && transportCell.Entities["source-inserter"] == original.Entities["source-inserter"],
                "The second consumer must reuse the original extractor and belt identities.");
            Require(original.Entities.All(p => transportCell.Entities.GetValueOrDefault(p.Key) == p.Value), "Extension replaced an existing bus part.");
            evidence.Add(new { check = "persistent-bus-extension", bus, transportCell });
            await Insert(gears.Entities["input-chest"], "iron-plate", 300);
            await Insert(first.Entities["input-chest"], "copper-plate", 40);
            await Insert(second.Entities["input-chest"], "copper-plate", 40);
            FactorySnapshot snapshot;
            for (int attempt = 0; ; attempt++)
            {
                snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                Require(FactoryTransportHealth.Healthy(state, snapshot, bus), "The persistent native bus lost its graph or controls.");
                if (FactoryLogistics.Items(snapshot, first.Entities["output-chest"]).GetValueOrDefault("automation-science-pack") >= 5
                    && FactoryLogistics.Items(snapshot, second.Entities["output-chest"]).GetValueOrDefault("automation-science-pack") >= 5) break;
                Require(attempt < 12, "Native transport did not feed both science consumers within its three-minute budget.");
                Require((await controller.WorkAsync("wait", new { ticks = 900 }, 1200, token: token)).Status == "completed", "Transport wait failed.");
            }
            evidence.Add(new { check = "native-two-consumer-production", tick = snapshot.CollectedTick,
                first = FactoryLogistics.Items(snapshot, first.Entities["output-chest"]), second = FactoryLogistics.Items(snapshot, second.Entities["output-chest"]) });
            var logistics = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(5, token);
            Require(logistics.Collected.GetValueOrDefault("iron-gear-wheel") == 0 && logistics.Supplied.GetValueOrDefault("iron-gear-wheel") == 0
                && logistics.Shortfall.GetValueOrDefault("iron-gear-wheel") == 0, "Actor logistics duplicated a healthy native bus flow.");
            evidence.Add(new { check = "factory-logistics-preserves-native-bus", logistics });
            // Freeze only fixture consumer machines to prove finite native chest limits while gears continue being produced.
            string freeze = $"/silent-command local s=game.surfaces.nauvis; local found=0; for _,e in pairs(s.find_entities_filtered{{force='factorio_agent'}}) do if e.unit_number=={first.Entities["machine"]} or e.unit_number=={second.Entities["machine"]} then e.active=false; found=found+1 end end; assert(found==2); rcon.print(helpers.table_to_json{{fixtureFault='consumer-pause',tick=game.tick}})";
            using var paused = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(freeze, token));
            Require((await controller.WorkAsync("wait", new { ticks = 1800 }, 2100, token: token)).Status == "completed", "Limit wait failed.");
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            long[] stocks = [FactoryLogistics.Items(snapshot, first.Entities["input-chest"]).GetValueOrDefault("iron-gear-wheel"),
                FactoryLogistics.Items(snapshot, second.Entities["input-chest"]).GetValueOrDefault("iron-gear-wheel")];
            Require(stocks.All(n => n is >= 3 and <= 4), "Native chest stock limits did not stop each receiver.");
            evidence.Add(new { check = "native-stock-limits", maximum = 3, stocks, fixtureFault = paused.RootElement.Clone() });
            string role = FactoryTransportHealth.Belts(transportCell)[FactoryTransportHealth.Belts(transportCell).Length / 2];
            string armRole = bus.Consumers[1].InserterRole;
            using var fault = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                $"/silent-command local found=0; for _,e in pairs(game.surfaces.nauvis.find_entities_filtered{{force='factorio_agent'}}) do if e.unit_number=={transportCell.Entities[role]} or e.unit_number=={transportCell.Entities[armRole]} then e.destroy(); found=found+1 end end; assert(found==2); rcon.print(helpers.table_to_json{{fixtureFault='belt-and-receiver-destroyed',tick=game.tick}})", token));
            var maintenance = await new FactoryMaintenance(game, journal, session.Directory).RunAsync(controller, catalog, token);
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            await transports.RepairControlsAsync(state, snapshot, catalog, controller, token);
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            Require(maintenance.Rebuilt.Count == 2 && FactoryTransportHealth.Healthy(state, snapshot, bus), "Rebuilt bus lost native filtering, wiring or belt continuity.");
            Require(new[] { first, second }.All(c => FactoryLogistics.Items(snapshot, c.Entities["input-chest"]).GetValueOrDefault("iron-gear-wheel") <= 4),
                "Reconstruction admitted uncontrolled items before the receiver stock control was restored.");
            evidence.Add(new { check = "persistent-native-repair", maintenance, tick = snapshot.CollectedTick, fixtureFault = fault.RootElement.Clone() });
            state = await transports.ApplyPausesAsync(state, new HashSet<string>([first.Id, second.Id], StringComparer.Ordinal), token);
            await transports.RepairControlsAsync(state, snapshot, catalog, controller, token);
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            Require(FactoryTransportHealth.Healthy(state, snapshot, state.Transports!.Single()), "Demand pause lost native bus control.");
            evidence.Add(new { check = "native-demand-pause", bus = state.Transports!.Single(), tick = snapshot.CollectedTick });
            using var counters = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local p=f.get_item_production_statistics(s); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,gears=p.get_input_count('iron-gear-wheel'),science=p.get_input_count('automation-science-pack')})
                """, token));
            Require(counters.RootElement.GetProperty("gears").GetDouble() > 10 && counters.RootElement.GetProperty("science").GetDouble() >= 10
                && counters.RootElement.GetProperty("character").GetInt64() == setup.RootElement.GetProperty("character").GetInt64()
                && counters.RootElement.GetProperty("players").GetInt32() == 0, "Native production or headless actor identity changed.");
            var rows = (await File.ReadAllLinesAsync(Path.ChangeExtension(path, ".jsonl"), token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var submitted = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data")).ToArray();
                Require(!submitted.Any(r => r.GetProperty("kind").GetString() is "craft" or "mine"), "Prepared transport must not fabricate or mine manually.");
                Require(!submitted.Any(r => r.GetProperty("kind").GetString() is "take" or "insert"
                    && r.GetProperty("args").TryGetProperty("item", out var item) && item.GetString() == "iron-gear-wheel"),
                    "The avatar transported the bus intermediate.");
                evidence.Add(new { check = "native-production-without-actor-intermediate-transfers", native = counters.RootElement.Clone() });
            }
            finally { foreach (var row in rows) row.Dispose(); }
            // A third, unconnected consumer must share scarce source output with the native bus. No gears are injected.
            using var sharingKit = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and #game.connected_players==0); for name,count in pairs{['assembling-machine-1']=1,inserter=3,['iron-chest']=2,['small-electric-pole']=3,['iron-plate']=80} do assert(c.insert{name=name,count=count}==count) end; rcon.print(helpers.table_to_json{fixture='additional-sharing-consumer-equipment-and-plates',tick=game.tick})
                """, token));
            var actorConsumer = await builder.BuildAsync("assembler", "assembling-machine-1", "transport-belt", token);
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            state = await transports.ApplyPausesAsync(state, new HashSet<string>(StringComparer.Ordinal), token);
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            await transports.RepairControlsAsync(state, snapshot, catalog, controller, token);
            long collectedForActor = 0, suppliedForActor = 0;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var shared = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(5, token);
                collectedForActor += shared.Collected.GetValueOrDefault("iron-gear-wheel");
                suppliedForActor += shared.Supplied.GetValueOrDefault("iron-gear-wheel");
                Require((await controller.WorkAsync("wait", new { ticks = 900 }, 1200, token: token)).Status == "completed", "Source sharing wait failed.");
                state = await registry.LoadAsync(catalog.Scope.WorldId, token);
                snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                Require(FactoryTransportHealth.Healthy(state, snapshot, state.Transports!.Single()), "Source sharing changed the native bus graph.");
                if (FactoryLogistics.Items(snapshot, actorConsumer.Entities["output-chest"]).GetValueOrDefault("transport-belt") >= 2) break;
            }
            Require(collectedForActor > 0 && suppliedForActor > 0
                && FactoryLogistics.Items(snapshot, actorConsumer.Entities["output-chest"]).GetValueOrDefault("transport-belt") >= 2,
                "The native bus monopolized the source and starved its unconnected consumer.");
            Require(state.Transports!.Single().ActorReserve is > 0 and <= 25, "Actor sharing must keep only a bounded quarter-stack lot.");
            evidence.Add(new { check = "native-source-sharing-with-actor-consumer", collectedForActor, suppliedForActor,
                bus = state.Transports!.Single(), output = FactoryLogistics.Items(snapshot, actorConsumer.Entities["output-chest"]),
                snapshot.CollectedTick, preparation = sharingKit.RootElement.Clone() });
            state = await transports.ApplyActorReservationsAsync(state, snapshot, new Dictionary<string, long>(),
                new Dictionary<string, long>(), catalog, token);
            await transports.RepairControlsAsync(state, snapshot, catalog, controller, token);
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            Require(state.Transports!.Single().ActorReserve == 0 && FactoryTransportHealth.Healthy(state, snapshot, state.Transports!.Single()),
                "An actor consumer with no remaining demand must release its source reserve through native control.");
            evidence.Add(new { check = "native-source-reserve-release", bus = state.Transports!.Single(), snapshot.CollectedTick });
            var sharedBus = state.Transports!.Single();
            var sharedCell = state.Cells.Single(c => c.Id == sharedBus.CellId);
            string sourceId = gears.Entities["output-chest"], sourceArm = sharedCell.Entities["source-inserter"];
            using var foreign = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                $"/silent-command local s=game.surfaces.nauvis; local chest; for _,e in pairs(s.find_entities_filtered{{force='factorio_agent'}}) do if e.unit_number=={sourceId} then chest=e end end; assert(chest); local p=s.find_non_colliding_position('iron-chest',chest.position,6,0.5); assert(p); local foreign=s.create_entity{{name='iron-chest',position=p,force=chest.force}}; assert(foreign); local a=chest.get_wire_connector(defines.wire_connector_id.circuit_red,true); local b=foreign.get_wire_connector(defines.wire_connector_id.circuit_red,true); assert(a.connect_to(b,true,defines.wire_origin.player)); rcon.print(helpers.table_to_json{{fixtureFault='foreign-source-wire',entityId=foreign.unit_number,tick=game.tick}})", token));
            var rejected = await controller.WorkAsync("configure_inserter", new { entityId = sourceArm, item = sharedBus.Item,
                chestEntityId = sourceId, maximum = 5, comparator = ">" }, 600, token: token);
            Require(rejected.Status == "failed" && rejected.Error?.Code == "foreign_circuit", "Source reservation accepted an unrelated native circuit.");
            using var removed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                $"/silent-command local found=0; for _,e in pairs(game.surfaces.nauvis.find_entities_filtered{{force='factorio_agent'}}) do if e.unit_number=={foreign.RootElement.GetProperty("entityId").GetInt64()} then e.destroy(); found=found+1 end end; assert(found==1); rcon.print(helpers.table_to_json{{fixtureFault='foreign-wire-removed',tick=game.tick}})", token));
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            Require(FactoryTransportHealth.Healthy(state, snapshot, sharedBus), "Rejected source configuration mutated its previous native control.");
            evidence.Add(new { check = "native-foreign-source-circuit-rejected", rejected, preparation = foreign.RootElement.Clone() });
            state = await transports.ApplyActorReservationsAsync(state, snapshot, new Dictionary<string, long> { [sharedBus.Item] = 5 },
                new Dictionary<string, long>(), catalog, token);
            await transports.RepairControlsAsync(state, snapshot, catalog, controller, token);
            using var sourceFault = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                $"/silent-command local found=0; for _,e in pairs(game.surfaces.nauvis.find_entities_filtered{{force='factorio_agent'}}) do if e.unit_number=={sourceArm} then e.destroy(); found=found+1 end end; assert(found==1); rcon.print(helpers.table_to_json{{fixtureFault='reserved-source-arm-destroyed',tick=game.tick}})", token));
            var sourceRepair = await new FactoryMaintenance(game, journal, session.Directory).RunAsync(controller, catalog, token);
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            await transports.RepairControlsAsync(state, snapshot, catalog, controller, token);
            snapshot = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            Require(sourceRepair.Rebuilt.Count == 1 && state.Transports!.Single().ActorReserve == 5
                && FactoryTransportHealth.Healthy(state, snapshot, state.Transports!.Single()), "Source reconstruction lost its persisted actor reserve.");
            evidence.Add(new { check = "native-reserved-source-reconstruction", sourceRepair, bus = state.Transports!.Single(),
                snapshot.CollectedTick, preparation = sourceFault.RootElement.Clone() });
            passed = true;
            return path;

            async Task Insert(string chest, string item, int count)
            {
                var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                var at = known.Records.Single(r => r.Kind == "entity" && r.EntityId == chest).Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
                await controller.ApproachEntityAsync(chest, at, catalog, token);
                var receipt = await controller.WorkAsync("insert", new { entityId = chest, item, count, inventory = "chest" }, 600, token: token);
                Require(receipt.Status == "completed" && receipt.Effects.GetProperty("transferred").GetInt32() == count, "Fixture plate transfer failed.");
            }
        }
        finally { await LocalJson.WriteAsync(path, new { kind = "prepared-persistent-transport-qualification", passed, isAutonomousCampaign = false, evidence }, CancellationToken.None); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
