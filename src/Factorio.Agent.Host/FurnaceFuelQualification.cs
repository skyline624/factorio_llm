using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Prepared cold-furnace regression: native steel production must procure additional coal through a drill.</summary>
public sealed class FurnaceFuelQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Furnace fuel qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(session.Directory, $"furnace-fuel-qualification-{id}.json");
        string journalPath = Path.Combine(session.Directory, $"furnace-fuel-qualification-{id}.jsonl");
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Cold steel furnace test: prepared ore, drill, chest with one coal, furnace and 25 iron plates. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); for _,p in pairs(game.connected_players) do assert(p.character==c) end; game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-20,-20},{25,20}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-20,25 do for y=-20,20 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({4,-2})); c.get_main_inventory().clear(); f.technologies['steel-processing'].researched=true; assert(c.insert{name='iron-plate',count=25}==25); for x=-1,0 do for y=-1,0 do assert(s.create_entity{name='coal',position={x+0.5,y+0.5},amount=200}) end end; local drill=s.create_entity{name='burner-mining-drill',position={0,0},direction=0,force=f}; local chest=s.create_entity{name='wooden-chest',position={-0.5,-1.5},force=f}; local furnace=s.create_entity{name='stone-furnace',position={8,2},force=f}; assert(drill and chest and furnace); assert(chest.insert{name='coal',count=1}==1); rcon.print(helpers.table_to_json{tick=game.tick,drillId=tostring(drill.unit_number),chestId=tostring(chest.unit_number),furnaceId=tostring(furnace.unit_number),drillDrop=drill.drop_position,players=#game.connected_players})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "explicit-cold-furnace-preparation", native = setup.RootElement.Clone() });
            JsonElement before = await ReadAsync();
            var result = await new ProductionGoalExecutor(game, new ControllerJournal(journalPath)).RunAsync("steel-plate", 5, token);
            JsonElement after = await ReadAsync();
            var receipts = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                int manualMining = receipts.Count(row => row.RootElement.GetProperty("type").GetString() == "submission"
                    && row.RootElement.GetProperty("data").GetProperty("kind").GetString() == "mine");
                evidence.Add(new { check = "native-steel-and-machine-coal", before, result, after, manualMining });
                Require(result.FinalStock == 5 && after.GetProperty("steel").GetInt64() == 5, "Native carried steel stock was not established.");
                Require(Delta("steelProduced") == 5 && Delta("ironConsumed") == 25, "Native steel recipe accounting differs from the prepared inputs.");
                Require(Delta("coalProduced") > 0 && manualMining == 0, "Additional fuel did not come exclusively from native machine extraction.");
                Require(after.GetProperty("coalStock").GetInt64() == before.GetProperty("coalStock").GetInt64()
                    + Delta("coalProduced") - Delta("coalConsumed"), "Native fuel stock, extraction and consumption do not reconcile.");
            }
            finally { foreach (var row in receipts) row.Dispose(); }
            passed = true;
            return path;

            long Delta(string property) => after.GetProperty(property).GetInt64() - before.GetProperty(property).GetInt64();
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-furnace-fuel-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement> ReadAsync()
        {
            const string read = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local stats=f.get_item_production_statistics(s); local coal=c.get_item_count('coal'); for _,e in pairs(s.find_entities_filtered{force=f,area={{-20,-20},{25,20}}}) do if e.type=='container' then coal=coal+e.get_item_count('coal') end; local fuel=e.get_fuel_inventory(); if fuel then coal=coal+fuel.get_item_count('coal') end end; rcon.print(helpers.table_to_json{tick=game.tick,steel=c.get_item_count('steel-plate'),coalStock=coal,steelProduced=stats.get_input_count('steel-plate'),ironConsumed=stats.get_output_count('iron-plate'),coalProduced=stats.get_input_count('coal'),coalConsumed=stats.get_output_count('coal'),players=#game.connected_players})
                """;
            using var value = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(read, token));
            return value.RootElement.Clone();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
