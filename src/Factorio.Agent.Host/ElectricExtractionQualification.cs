using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Prepared distant deposit and construction items; power generation and extraction use native controllers.</summary>
public sealed class ElectricExtractionQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Electric extraction qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"electric-extraction-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared distant iron deposit, shore, unlocked electric drill and construction items; native power and extraction." }), token);
            Require(mark.Ok, "Fixture marking rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); for _,p in pairs(game.connected_players) do assert(p.character==c) end; game.speed=4; for _,e in pairs(s.find_entities_filtered{force=f}) do if e~=c then e.destroy() end end; for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{112,64}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-64,112 do for y=-64,64 do tiles[#tiles+1]={name=x< -20 and 'water' or 'grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.get_main_inventory().clear(); f.technologies['steam-power'].researched=true; f.technologies['electric-mining-drill'].researched=true; for name,count in pairs{['offshore-pump']=1,boiler=1,['steam-engine']=1,['small-electric-pole']=50,inserter=1,['electric-mining-drill']=1,['wooden-chest']=1,coal=200} do assert(c.insert{name=name,count=count}==count) end; for x=68,72 do for y=-2,2 do assert(s.create_entity{name='iron-ore',position={x,y},amount=5000}) end end; rcon.print(helpers.table_to_json{tick=game.tick,players=#game.connected_players,characterId=tostring(c.unit_number)})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "prepared-shore-and-distant-deposit", native = setup.RootElement.Clone() });
            var power = await new SteamPowerController(game, new ControllerJournal(journalPath + ".power")).RunAsync(token);
            evidence.Add(new { check = "native-steam-construction", power });
            string beforeCommand = $"/silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{{type='character',force=f}}[1]; local b=s.find_entities_filtered{{name='boiler',force=f}}[1]; assert(tostring(b.unit_number)=='{power.Entities["boiler"]}'); b.get_fuel_inventory().clear(); assert(c.teleport({{70,0}})); rcon.print(helpers.table_to_json{{tick=game.tick,produced=f.get_item_production_statistics(s).get_input_count('iron-ore'),players=#game.connected_players}})";
            using var baseline = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(beforeCommand, token));
            JsonElement before = baseline.RootElement.Clone();
            var result = await new StoredResourceExtractionController(game, new ControllerJournal(journalPath)).RunAsync("iron-ore", 50, token);
            string afterCommand = $"/silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{{type='character',force=f}}[1]; local d=s.find_entities_filtered{{name='electric-mining-drill',force=f}}[1]; local chest=s.find_entities_filtered{{name='wooden-chest',force=f}}[1]; assert(d and chest); rcon.print(helpers.table_to_json{{tick=game.tick,stock=c.get_item_count('iron-ore'),stored=chest.get_inventory(defines.inventory.chest).get_item_count('iron-ore'),produced=f.get_item_production_statistics(s).get_input_count('iron-ore'),drillId=tostring(d.unit_number),chestId=tostring(chest.unit_number),dropId=d.drop_target and tostring(d.drop_target.unit_number),network=d.electric_network_id,energy=d.energy,players=#game.connected_players,characterId=tostring(c.unit_number)}})";
            using var final = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(afterCommand, token));
            JsonElement after = final.RootElement.Clone();
            int manual = 0, links = 0, fuelLoads = 0;
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "power-grid-link") links++;
                if (type == "submission" && data.GetProperty("kind").GetString() == "mine") manual++;
                if (type == "submission" && data.GetProperty("kind").GetString() == "insert"
                    && data.GetProperty("args").TryGetProperty("entityId", out var id) && id.GetString() == power.Entities["boiler"])
                    fuelLoads++;
            }
            evidence.Add(new { check = "native-electric-extraction", before, result, after, manual, links, fuelLoads });
            Require(after.GetProperty("stock").GetInt64() == 50 && result.FinalStock == 50, "Native ore target not reached.");
            Require(after.GetProperty("produced").GetInt64() - before.GetProperty("produced").GetInt64()
                == after.GetProperty("stock").GetInt64() + after.GetProperty("stored").GetInt64(), "Native extracted ore does not reconcile.");
            Require(after.GetProperty("drillId").GetString() == result.DrillId && after.GetProperty("dropId").GetString() == result.ChestId,
                "Native electric drill output does not match the planned storage.");
            Require(after.GetProperty("network").GetInt64() == power.NetworkId && after.GetProperty("energy").GetDouble() > 0,
                "Electric drill lacks native power on the constructed steam network.");
            Require(manual == 0 && links > 1 && fuelLoads > 0, "Missing distant grid extension or remote boiler refill without manual mining.");
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-electric-extraction-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
