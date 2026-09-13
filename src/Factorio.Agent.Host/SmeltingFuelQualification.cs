using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Prepared wood-shortfall regression: direct smelting must obtain coal through native extraction.</summary>
public sealed class SmeltingFuelQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Smelting fuel qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"smelting-fuel-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared iron and coal drill connections, cold furnace, one coal in storage and one carried wood. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); for _,p in pairs(game.connected_players) do assert(p.character==c) end; game.speed=4; for _,e in pairs(s.find_entities_filtered{force=f}) do if e~=c then e.destroy() end end; for _,e in pairs(s.find_entities_filtered{area={{-20,-20},{25,20}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-20,25 do for y=-20,20 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({4,-2})); c.get_main_inventory().clear(); assert(c.insert{name='wood',count=1}==1); for x=-1,0 do for y=-1,0 do assert(s.create_entity{name='coal',position={x+0.5,y+0.5},amount=5000}); assert(s.create_entity{name='iron-ore',position={x+10.5,y+0.5},amount=5000}) end end; local coal=s.create_entity{name='burner-mining-drill',position={0,0},direction=0,force=f}; local chest=s.create_entity{name='wooden-chest',position={-0.5,-1.5},force=f}; local iron=s.create_entity{name='burner-mining-drill',position={10,0},direction=0,force=f}; local furnace=s.create_entity{name='stone-furnace',position={10,-2},force=f}; assert(coal and chest and iron and furnace); assert(chest.insert{name='coal',count=1}==1); assert(s.create_entity{name='tree-01',position={-5,4}}); rcon.print(helpers.table_to_json{tick=game.tick,coalDrillId=tostring(coal.unit_number),chestId=tostring(chest.unit_number),ironDrillId=tostring(iron.unit_number),furnaceId=tostring(furnace.unit_number),players=#game.connected_players,characterId=tostring(c.unit_number)})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "explicit-preparation", native = setup.RootElement.Clone() });
            var journal = new ControllerJournal(journalPath);
            var collection = await new ProductionController(game, journal).CollectAvailableAsync("wood", 5, token);
            Require(collection.FinalStock == 1 && collection.Receipts.Count == 0, "Stock-only collection manufactured or harvested a shortfall.");
            evidence.Add(new { check = "stock-only-shortfall", collection });
            JsonElement before = await ReadAsync();
            var result = await new AutomatedSmeltingController(game, journal).RunAsync("iron-plate", 50, token);
            JsonElement after = await ReadAsync();
            int manual = 0, coalChoices = 0;
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "submission" && data.GetProperty("kind").GetString() == "mine") manual++;
                if (type == "smelting-fuel-reassessment" && data.GetProperty("fuelPlan").GetProperty("fuel").GetString() == "coal") coalChoices++;
            }
            evidence.Add(new { check = "native-smelting-with-mechanical-fuel", before, result, after, manual, coalChoices });
            Require(result.FinalStock == 50 && after.GetProperty("plates").GetInt64() == 50, "Native carried plate target was not reached.");
            Require(Delta("platesProduced") == 50 && Delta("ironConsumed") == 50 + (after.GetProperty("inProcess").GetBoolean() ? 1 : 0),
                "Native plate production and engaged input do not reconcile.");
            Require(manual == 0 && coalChoices > 0 && Delta("coalProduced") > 0, "Fuel did not come from native machine extraction.");
            Require(after.GetProperty("coalStock").GetInt64() == before.GetProperty("coalStock").GetInt64() + Delta("coalProduced") - Delta("coalConsumed"),
                "Native coal extraction, consumption and stocks do not reconcile.");
            Require(after.GetProperty("woodStock").GetInt64() == 1 && Delta("woodConsumed") == 0,
                "The insufficient wood stock was consumed instead of choosing machine coal.");
            passed = true;
            return path;
            long Delta(string key) => after.GetProperty(key).GetInt64() - before.GetProperty(key).GetInt64();
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-smelting-fuel-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement> ReadAsync()
        {
            const string command = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local furnace=s.find_entities_filtered{name='stone-furnace',force=f}[1]; local stats=f.get_item_production_statistics(s); local coal=c.get_item_count('coal'); local wood=c.get_item_count('wood'); for _,e in pairs(s.find_entities_filtered{force=f}) do if e.type=='container' then coal=coal+e.get_item_count('coal'); wood=wood+e.get_item_count('wood') end; local fuel=e.get_fuel_inventory(); if fuel then coal=coal+fuel.get_item_count('coal'); wood=wood+fuel.get_item_count('wood') end end; rcon.print(helpers.table_to_json{tick=game.tick,plates=c.get_item_count('iron-plate'),inProcess=furnace.is_crafting(),coalStock=coal,woodStock=wood,platesProduced=stats.get_input_count('iron-plate'),ironConsumed=stats.get_output_count('iron-ore'),coalProduced=stats.get_input_count('coal'),coalConsumed=stats.get_output_count('coal'),woodConsumed=stats.get_output_count('wood'),players=#game.connected_players,characterId=tostring(c.unit_number)})
                """;
            using var value = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(command, token));
            return value.RootElement.Clone();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
