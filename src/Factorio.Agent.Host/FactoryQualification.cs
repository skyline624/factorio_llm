using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed class FactoryQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Factory qualification requires an explicit fixture session.");
        string reportPath = Path.Combine(session.Directory, $"factory-qualification-{Guid.NewGuid():N}.json");
        List<object> evidence = [];
        using var lease = ActorControlLease.Acquire(session.Directory);
        IGameClient game = session.CreateClient(lease);
        try
        {
            GameResponse marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new { reason = "Synthetic immutable factory stock qualification." }), token);
            Require(marked.Ok, "Fixture marking failed.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; assert(c and not c.player); c.teleport({0,0}); for _,e in ipairs(s.find_entities_filtered{area={{-28,-28},{28,28}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-25,25 do for y=-25,25 do tiles[#tiles+1]={name="grass-1",position={x,y}} end end; s.set_tiles(tiles); local chests={}; for x=0,14 do for y=0,15 do if #chests<230 then local p={x=-14.5+x*2,y=-14.5+y*2}; if math.abs(p.x)>2 or math.abs(p.y)>2 then local e=s.create_entity{name="wooden-chest",position=p,force=c.force}; assert(e); chests[#chests+1]=e end end end end; assert(#chests==230); chests[1].get_inventory(defines.inventory.chest).insert{name="iron-plate",count=5}; chests[1].get_inventory(defines.inventory.chest).set_bar(2); chests[2].get_inventory(defines.inventory.chest).insert{name="copper-plate",count=2}; for x=5,7 do local b=s.create_entity{name="transport-belt",position={x+0.5,-18.5},direction=4,force=c.force}; b.active=false; b.get_transport_line(1).force_insert_at(0.2,{name="iron-plate",count=1}) end; local u=s.create_entity{name="underground-belt",position={9.5,-18.5},direction=4,type="input",force=c.force}; local v=s.create_entity{name="underground-belt",position={13.5,-18.5},direction=4,type="output",force=c.force}; u.active=false; v.active=false; u.get_transport_line(3).force_insert_at(0.6,{name="copper-plate",count=1}); local split=s.create_entity{name="splitter",position={16,-18.5},direction=4,force=c.force}; split.active=false; split.get_transport_line(1).force_insert_at(0.2,{name="iron-gear-wheel",count=1}); local hand=s.create_entity{name="inserter",position={0.5,-18.5},force=c.force}; hand.active=false; hand.held_stack.set_stack{name="iron-plate",count=4}; local p=s.create_entity{name="pipe",position={-7.5,-18.5},force=c.force}; s.create_entity{name="pipe",position={-6.5,-18.5},force=c.force}; p.fluidbox[1]={name="water",amount=123.75}; local isolated=s.create_entity{name="pipe",position={-4.5,-18.5},force=c.force}; isolated.fluidbox[1]={name="water",amount=10.125}; local a=s.create_entity{name="assembling-machine-1",position={-2,-22},force=c.force}; a.set_recipe("iron-gear-wheel"); a.get_inventory(defines.inventory.assembling_machine_input).insert{name="iron-plate",count=2}; a.energy=100000; rcon.print(helpers.table_to_json({chestId=tostring(chests[1].unit_number),firstChest=chests[1].position,secondChest=chests[2].position,actorPlates=c.get_main_inventory().get_item_count("iron-plate"),chestCount=#chests}));
                """;
            using JsonDocument setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            string chestId = setup.RootElement.GetProperty("chestId").GetString()!;
            long actorPlates = setup.RootElement.GetProperty("actorPlates").GetInt64();
            const string freeze = """
                /silent-command local s=game.surfaces.nauvis; local a=s.find_entities_filtered{name="assembling-machine-1",force="factorio_agent"}[1]; a.active=false; local hand=s.find_entities_filtered{type="inserter",force="factorio_agent"}[1]; rcon.print(helpers.table_to_json({inProcess=a.is_crafting(),progress=a.crafting_progress,inputPlates=a.get_inventory(defines.inventory.assembling_machine_input).get_item_count("iron-plate"),outputGears=a.get_inventory(defines.inventory.assembling_machine_output).get_item_count("iron-gear-wheel"),inserterHeld=hand.held_stack.count}));
                """;
            using JsonDocument machine = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(freeze, token));
            evidence.Add(new { check = "synthetic-factory-layout", disqualifiedAsCampaign = true,
                setup = "Created 230 chests, loaded belts/underground/splitter, a paused inserter, connected and isolated water pipes, and a powered gear assembler. Four requested items in the standard inserter are capped natively at one.",
                native = setup.RootElement.Clone(), machine = machine.RootElement.Clone() });
            var intercepted = new MutateAfterFirstPage(game, async () =>
            {
                const string mutation = """
                    /silent-command local s=game.surfaces.nauvis; local first=s.find_entities_filtered{position={-14.5,-14.5},name="wooden-chest"}[1]; local second=s.find_entities_filtered{position={-14.5,-12.5},name="wooden-chest"}[1]; assert(first and second); assert(first.get_inventory(defines.inventory.chest).insert{name="iron-plate",count=50}==50); second.destroy(); rcon.print("factory-mutated-between-pages");
                    """;
                Require((await session.CreateRcon().ExecuteAsync(mutation, token)).Trim() == "factory-mutated-between-pages",
                    "Between-page fixture mutation failed.");
            });
            FactorySnapshot frozen = await new FactorySnapshotClient(intercepted).CaptureAsync(["iron-plate"], pageSize: 37,
                cancellationToken: token);
            FactoryStockSummary oldStock = frozen.SummarizeStocks();
            int machineInput = machine.RootElement.GetProperty("inputPlates").GetInt32();
            Require(machine.RootElement.GetProperty("inserterHeld").GetInt32() == 1,
                "The fixture did not measure the standard inserter's actual holding capacity.");
            Require(machine.RootElement.GetProperty("inProcess").GetBoolean()
                && machine.RootElement.GetProperty("progress").GetDouble() > 0 && machineInput == 0
                && machine.RootElement.GetProperty("outputGears").GetInt32() == 0,
                "The fixture did not capture consumed ingredients in an unfinished craft.");
            evidence.Add(new { check = "captured-stock-before-assertions", oldStock, intercepted.PageCount });
            Require(intercepted.PageCount > 2 && frozen.Records.Count > 400, "The fixture did not exercise multiple stock pages.");
            Require(oldStock.InventoryItems["iron-plate"] == actorPlates + 5 + machineInput
                && oldStock.InventoryItems["copper-plate"] == 2, "Immutable inventory stock changed between pages.");
            Require(oldStock.TransitItems["iron-plate"] == 4 && oldStock.TransitItems["copper-plate"] == 1
                && oldStock.TransitItems["iron-gear-wheel"] == 1, "Belt, underground, splitter or inserter stock was lost or counted twice.");
            Require(Math.Abs(oldStock.Fluids["water"] - 133.875) < 0.000001, "Shared fluid stock was rounded or counted twice.");
            FactoryRecord chest = frozen.Records.Single(r => r.Kind == "inventory" && r.EntityId == chestId);
            JsonElement hint = chest.Data.GetProperty("capacityHints").GetProperty("iron-plate");
            Require(chest.Data.GetProperty("slots").GetInt32() == 16 && chest.Data.GetProperty("usableSlots").GetInt32() == 1
                && hint.GetProperty("insertable").GetInt32() == 95 && hint.GetProperty("canInsertOne").GetBoolean()
                && hint.GetProperty("certainty").GetString() == "native-estimate", "Inventory bar or capacity confidence is wrong.");
            FactoryRecord work = frozen.Records.Single(r => r.Kind == "work" && r.Name == "machine-craft");
            Require(work.Data.GetProperty("inProcess").GetBoolean() == machine.RootElement.GetProperty("inProcess").GetBoolean()
                && work.Data.GetProperty("progress").GetDouble() == machine.RootElement.GetProperty("progress").GetDouble(),
                "The in-process craft does not match the stopped native machine state.");
            FactorySnapshot fresh = await new FactorySnapshotClient(game).CaptureAsync(["iron-plate"], cancellationToken: token);
            FactoryStockSummary newStock = fresh.SummarizeStocks();
            Require(fresh.SnapshotId != frozen.SnapshotId && fresh.CollectedTick > frozen.CollectedTick
                && newStock.InventoryItems["iron-plate"] == oldStock.InventoryItems["iron-plate"] + 50
                && !newStock.InventoryItems.ContainsKey("copper-plate"), "Fresh snapshot did not reflect insertion and destroyed chest.");
            evidence.Add(new { check = "native-immutable-pages-stock-and-capacity", intercepted.PageCount,
                frozen.SnapshotId, frozen.CollectedTick, recordCount = frozen.Records.Count, oldStock, newStock,
                chest = chest.Data, work = work.Data, fluidRecords = frozen.Records.Where(r => r.Kind == "fluid").ToArray() });
            await File.WriteAllTextAsync(Path.Combine(session.Directory, $"qualified-{frozen.SnapshotId.Split(':')[0]}-factory.json"),
                JsonSerializer.Serialize(frozen, Protocol.Json), token);
            await SaveAsync(true, null, token);
            return reportPath;
        }
        catch (Exception error)
        {
            await SaveAsync(false, error.Message, CancellationToken.None);
            throw;
        }
        Task SaveAsync(bool passed, string? error, CancellationToken saveToken) => File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(new { kind = "synthetic-factory-snapshot-qualification", passed, error,
                isAutonomousCampaign = false, evidence }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), saveToken);
    }

    private sealed class MutateAfterFirstPage(IGameClient inner, Func<Task> mutate) : IGameClient
    {
        public int PageCount { get; private set; }
        public async Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            GameResponse response = await inner.ExecuteAsync(request, cancellationToken);
            if (request.Action == "factory_snapshot" && response.Ok && ++PageCount == 1) await mutate();
            return response;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
