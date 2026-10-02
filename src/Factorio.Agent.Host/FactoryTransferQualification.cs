using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Explicit kit/power/research/plates/science fixture: native gear output growth and scarce lab distribution.</summary>
public sealed class FactoryTransferQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture || File.Exists(new FactoryRegistry(session.Directory).Path))
            throw new InvalidOperationException("Factory transfer qualification requires a fresh explicit fixture.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
        token = deadline.Token;
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"factory-transfer-qualification-{Guid.NewGuid():N}.json");
        var journal = new ControllerJournal(Path.ChangeExtension(path, ".jsonl"));
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared transfers: kit,power,research,81 iron plates,8 red/8 green packs. No gears. Not a campaign." }), token);
            if (!marked.Ok) throw new GameRpcException(marked.Error!);
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0 and #game.connected_players==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{64,64}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-64,64 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); for _,name in pairs{'steam-power','electronics','automation','automation-science-pack','logistics','logistic-science-pack','steel-processing'} do f.technologies[name].researched=true end; assert(not f.technologies['automation-2'].researched); local supplies={['assembling-machine-1']=1,lab=2,inserter=2,['iron-chest']=3,['small-electric-pole']=30,['iron-plate']=81,['automation-science-pack']=8,['logistic-science-pack']=8}; for name,count in pairs(supplies) do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=3000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,supplied=supplies,gears=f.get_item_production_statistics(s).get_input_count('iron-gear-wheel')})
                """, token));
            Require(setup.RootElement.GetProperty("gears").GetDouble() == 0, "The fixture must begin with zero gear production.");
            evidence.Add(new { check = "explicit-preparation", native = setup.RootElement.Clone() });
            var builder = new FactoryCellBuilder(game, journal, session.Directory);
            var producer = await builder.BuildAsync("assembler", "assembling-machine-1", "iron-gear-wheel", token);
            var labs = new[] { await builder.BuildAsync("lab", "lab", null, token), await builder.BuildAsync("lab", "lab", null, token) };
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var snapshots = new FactorySnapshotClient(game);
            await using var controller = new SpatialController(game, journal);
            var known = await snapshots.CaptureAsync(cancellationToken: token);
            var sourcePosition = Position(known, producer.Entities["output-chest"]);
            var remotePosition = new MapPosition(sourcePosition.X + 40, sourcePosition.Y + 18);
            string remoteId = await new PoweredMachineController(game, journal).BuildAtAsync("iron-chest", new(remotePosition, 0, 0), catalog, controller, token);
            var buffer = new FactoryCell("remote-buffer", 0, new(0, 0, true), "buffer", "iron-chest", null,
                new Dictionary<string, string> { ["output-chest"] = remoteId }, "ready", known.CollectedTick,
                Plan: new Dictionary<string, PlannedEntity> { ["output-chest"] = new("output-chest", "iron-chest", remotePosition, 0) });
            var registry = new FactoryRegistry(session.Directory);
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            await registry.SaveAsync(state with { Cells = [buffer, .. state.Cells] }, token);
            await Insert(remoteId, 1);
            await Insert(producer.Entities["input-chest"], 80);
            Require((await controller.WorkAsync("wait", new { ticks = 300 }, 600, token: token)).Status == "completed", "Initial gear wait failed.");
            var first = await snapshots.CaptureAsync(cancellationToken: token);
            Require(FactoryLogistics.Items(first, producer.Entities["output-chest"]).GetValueOrDefault("iron-gear-wheel") > 0,
                "The source has no initial native gear output.");
            var service = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(5, token);
            JsonElement? collection = null;
            foreach (string line in await File.ReadAllLinesAsync(Path.ChangeExtension(path, ".jsonl"), token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "factory-output-collection-intent" && data.GetProperty("item").GetString() == "iron-gear-wheel") collection = data.Clone();
                if (type == "submission") Require(data.GetProperty("kind").GetString() is not ("craft" or "mine"), "Prepared transfers must not craft or mine manually.");
            }
            Require(collection is not null && service.Collected.GetValueOrDefault("iron-gear-wheel") > collection.Value.GetProperty("observed").GetInt64()
                && collection.Value.GetProperty("requested").GetInt64() == 200, "The actor left newly produced output behind or exceeded its carrying allowance.");
            evidence.Add(new { check = "native-output-growth-during-collection", collection, service });
            var loaded = await Research();
            var installed = labs.Select(c => loaded.Labs.Single(l => l.Id == c.Entities["machine"])).ToArray();
            Require(service.Supplied.GetValueOrDefault("automation-science-pack") == 8 && service.Supplied.GetValueOrDefault("logistic-science-pack") == 8
                && installed.All(l => l.Items.GetValueOrDefault("automation-science-pack") == 4 && l.Items.GetValueOrDefault("logistic-science-pack") == 4 && l.Energy > 0),
                "Scarce prepared science did not reach both powered labs equally.");
            evidence.Add(new { check = "native-balanced-lab-supply", loaded.CollectedTick, installed });
            var selection = await controller.WorkAsync("research", new { technology = "automation-2" }, 600, token: token);
            evidence.Add(new { check = "native-research-selection", selection });
            Require(selection.Status == "completed", "Native research selection failed.");
            Require((await controller.WorkAsync("wait", new { ticks = 900 }, 1200, token: token)).Status == "completed", "Parallel research wait failed.");
            var researching = await Research();
            var working = labs.Select(c => researching.Labs.Single(l => l.Id == c.Entities["machine"])).ToArray();
            Require(researching.Progress > 0 && working.All(l => l.ScienceUnits.GetValueOrDefault("logistic-science-pack") < 4
                && l.ScienceUnits.GetValueOrDefault("automation-science-pack") < 4 && l.Energy > 0), "Both labs did not actually consume their science in parallel.");
            using var native = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,gears=f.get_item_production_statistics(s).get_input_count('iron-gear-wheel'),craftingQueue=c.crafting_queue_size})
                """, token));
            Require(native.RootElement.GetProperty("character").GetInt64() == setup.RootElement.GetProperty("character").GetInt64()
                && native.RootElement.GetProperty("players").GetInt32() == 0 && native.RootElement.GetProperty("craftingQueue").GetInt32() == 0
                && native.RootElement.GetProperty("gears").GetDouble() >= service.Collected.GetValueOrDefault("iron-gear-wheel"), "Native production or actor identity changed.");
            evidence.Add(new { check = "native-parallel-research-consumption", researching.CollectedTick, researching.Progress, working, native = native.RootElement.Clone() });
            passed = true;
            return path;

            async Task<ResearchSnapshot> Research() => ResearchSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("research_state", new { technology = "automation-2" }), token), "automation-2");
            async Task Insert(string chest, int count)
            {
                var current = await snapshots.CaptureAsync(cancellationToken: token);
                await controller.ApproachEntityAsync(chest, Position(current, chest), catalog, token);
                var receipt = await controller.WorkAsync("insert", new { entityId = chest, item = "iron-plate", count, inventory = "chest" }, 600, token: token);
                Require(receipt.Status == "completed" && receipt.Effects.GetProperty("transferred").GetInt32() == count, "Prepared plate transfer failed.");
            }
        }
        finally { await LocalJson.WriteAsync(path, new { kind = "prepared-factory-transfer-qualification", passed, isAutonomousCampaign = false, evidence }, CancellationToken.None); }
    }

    private static MapPosition Position(FactorySnapshot snapshot, string id) => snapshot.Records.Single(r => r.Kind == "entity" && r.EntityId == id)
        .Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
