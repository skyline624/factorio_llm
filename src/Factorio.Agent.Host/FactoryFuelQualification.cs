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
        deadline.CancelAfter(TimeSpan.FromMinutes(8));
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
