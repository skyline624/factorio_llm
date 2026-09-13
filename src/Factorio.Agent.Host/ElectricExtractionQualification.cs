using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Prepared distant deposit and construction items; power generation and extraction use native controllers.</summary>
public sealed class ElectricExtractionQualification(RuntimeSession session, string item = "iron-ore")
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Electric extraction qualification requires an explicit fixture session.");
        if (item is not ("iron-ore" or "iron-plate")) throw new ArgumentException("Electric fixture supports iron-ore or iron-plate.", nameof(item));
        bool smelt = item == "iron-plate";
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
            if (smelt) await session.CreateRcon().ExecuteAsync("/silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; assert(c.insert{name='stone-furnace',count=1}==1); rcon.print('prepared-furnace')", token);
            var power = await new SteamPowerController(game, new ControllerJournal(journalPath + ".power")).RunAsync(token);
            evidence.Add(new { check = "native-steam-construction", power });
            string beforeCommand = $"/silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{{type='character',force=f}}[1]; local b=s.find_entities_filtered{{name='boiler',force=f}}[1]; assert(tostring(b.unit_number)=='{power.Entities["boiler"]}'); b.get_fuel_inventory().clear(); assert(c.teleport({{70,0}})); local stats=f.get_item_production_statistics(s); rcon.print(helpers.table_to_json{{tick=game.tick,produced=stats.get_input_count('iron-ore'),plates=stats.get_input_count('iron-plate'),oreConsumed=stats.get_output_count('iron-ore'),players=#game.connected_players}})";
            using var baseline = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(beforeCommand, token));
            JsonElement before = baseline.RootElement.Clone();
            object result;
            string drillId, receiverId;
            long finalStock;
            var journal = new ControllerJournal(journalPath);
            if (smelt)
            {
                await new SmeltingPreparationController(game, journal).PrepareAsync(item, token);
                var produced = await new AutomatedSmeltingController(game, journal).RunAsync(item, 50, token);
                result = produced; drillId = produced.DrillId; receiverId = produced.FurnaceId; finalStock = produced.FinalStock;
            }
            else
            {
                var extracted = await new StoredResourceExtractionController(game, journal).RunAsync(item, 50, token);
                result = extracted; drillId = extracted.DrillId; receiverId = extracted.ChestId; finalStock = extracted.FinalStock;
            }
            string receiver = smelt ? "stone-furnace" : "wooden-chest";
            string output = smelt ? "furnace_result" : "chest";
            string process = smelt ? "rawInput=r.get_inventory(defines.inventory.furnace_source).get_item_count('iron-ore'),inProcess=r.is_crafting()," : "rawInput=0,inProcess=false,";
            string afterCommand = $"/silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{{type='character',force=f}}[1]; local d=s.find_entities_filtered{{name='electric-mining-drill',force=f}}[1]; local r=s.find_entities_filtered{{name='{receiver}',force=f}}[1]; assert(d and r); local stats=f.get_item_production_statistics(s); rcon.print(helpers.table_to_json{{tick=game.tick,stock=c.get_item_count('{item}'),carriedOre=c.get_item_count('iron-ore'),stored=r.get_inventory(defines.inventory.{output}).get_item_count('{item}'),{process}produced=stats.get_input_count('iron-ore'),plates=stats.get_input_count('iron-plate'),oreConsumed=stats.get_output_count('iron-ore'),drillId=tostring(d.unit_number),receiverId=tostring(r.unit_number),dropId=d.drop_target and tostring(d.drop_target.unit_number),network=d.electric_network_id,energy=d.energy,players=#game.connected_players,characterId=tostring(c.unit_number)}})";
            using var final = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(afterCommand, token));
            JsonElement after = final.RootElement.Clone();
            if (smelt)
            {
                evidence.Add(new { check = "live-production-before-drain", native = after,
                    interpretation = "Drill output can be internally pending and is not exposed by Lua inventories; it is not available stock." });
                // After proving delivery, stop new mining in this fixture and free receiver space.
                // This closes the accounting without treating an unexposed internal buffer as zero or tolerating a discrepancy.
                const string drain = """
                    /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local d=s.find_entities_filtered{name='electric-mining-drill',force=f}[1]; local r=s.find_entities_filtered{name='stone-furnace',force=f}[1]; assert(d and r); r.active=false; local removed=0; for _,e in pairs(s.find_entities_filtered{type='resource',area=d.mining_area}) do e.destroy(); removed=removed+1 end; local input=r.get_inventory(defines.inventory.furnace_source); local n=input.remove{name='iron-ore',count=math.min(10,input.get_item_count('iron-ore'))}; if n>0 then assert(c.insert{name='iron-ore',count=n}==n) end; local stats=f.get_item_production_statistics(s); rcon.print(helpers.table_to_json{tick=game.tick,removedDeposits=removed,transferredOre=n,produced=stats.get_input_count('iron-ore'),oreConsumed=stats.get_output_count('iron-ore'),plates=stats.get_input_count('iron-plate')})
                    """;
                using var preparation = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(drain, token));
                await using var controller = new SpatialController(game, journal);
                var waited = await controller.WorkAsync("wait", new { ticks = 60 }, 600, token: token);
                Require(waited.Status == "completed", "Fixture output drain did not finish its observation wait.");
                using var drained = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(afterCommand, token));
                after = drained.RootElement.Clone();
                foreach (string counter in new[] { "produced", "oreConsumed", "plates" })
                    Require(after.GetProperty(counter).GetInt64() == preparation.RootElement.GetProperty(counter).GetInt64(),
                        "The fixture drain changed production instead of only releasing pending output.");
                evidence.Add(new { check = "prepared-output-drain", preparation = preparation.RootElement.Clone(), native = after });
            }
            int manual = 0, links = 0, fuelLoads = 0, poweredSamples = 0;
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "smelting-electric-supply" && data.GetProperty("drillId").GetString() == drillId
                    && data.TryGetProperty("power", out var observedPower) && observedPower.ValueKind == JsonValueKind.Object
                    && observedPower.GetProperty("networkId").GetInt64() == power.NetworkId
                    && observedPower.GetProperty("energy").GetDouble() > 0) poweredSamples++;
                if (type == "power-grid-link") links++;
                if (type == "submission" && data.GetProperty("kind").GetString() == "mine") manual++;
                if (type == "submission" && data.GetProperty("kind").GetString() == "insert"
                    && data.GetProperty("args").TryGetProperty("entityId", out var id) && id.GetString() == power.Entities["boiler"])
                    fuelLoads++;
            }
            evidence.Add(new { check = "native-electric-extraction", item, before, result, after, manual, links, fuelLoads, poweredSamples });
            Require(after.GetProperty("stock").GetInt64() == 50 && finalStock == 50, "Native product target not reached.");
            long delivered = after.GetProperty("stock").GetInt64() + after.GetProperty("stored").GetInt64();
            if (smelt)
            {
                Require(Delta("plates") == delivered && Delta("oreConsumed") == delivered + (after.GetProperty("inProcess").GetBoolean() ? 1 : 0),
                    "Native plate output and engaged ore do not reconcile.");
                Require(Delta("produced") == Delta("oreConsumed") + after.GetProperty("rawInput").GetInt64() + after.GetProperty("carriedOre").GetInt64(),
                    "Native mined ore does not reconcile with consumed and loaded ore.");
            }
            else Require(Delta("produced") == delivered, "Native extracted ore does not reconcile.");
            Require(after.GetProperty("drillId").GetString() == drillId && after.GetProperty("dropId").GetString() == receiverId,
                "Native electric drill output does not match the planned storage.");
            Require(after.GetProperty("network").GetInt64() == power.NetworkId
                && (smelt ? poweredSamples > 0 : after.GetProperty("energy").GetDouble() > 0),
                "Electric drill lacks native power on the constructed steam network.");
            Require(manual == 0 && links > 1 && fuelLoads > 0, "Missing distant grid extension or remote boiler refill without manual mining.");
            passed = true;
            return path;
            long Delta(string key) => after.GetProperty(key).GetInt64() - before.GetProperty(key).GetInt64();
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
