using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Prepared regression for native stack deliveries, partial final batches and exact assembly costs.</summary>
public sealed class AssemblyBatchQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Assembly batch qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(session.Directory, $"assembly-batch-qualification-{id}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared powered assembler, 80 iron plates and 240 cables. Native delivery and consumption test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); for _,p in pairs(game.connected_players) do assert(p.character==c) end; game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{64,64}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-64,64 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({4,-2})); c.health=c.max_health; c.get_main_inventory().clear(); f.technologies.automation.researched=true; assert(c.insert{name='iron-plate',count=80}==80); assert(c.insert{name='copper-cable',count=240}==240); local source=s.create_entity{name='electric-energy-interface',position={0,2},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=1000000; source.energy=1000000000; assert(s.create_entity{name='substation',position={4,2},force=f}); local machine=s.create_entity{name='assembling-machine-1',position={8,2},force=f}; assert(machine); machine.set_recipe('electronic-circuit'); rcon.print(helpers.table_to_json{tick=game.tick,machineId=tostring(machine.unit_number),character=c.unit_number,players=#game.connected_players})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "explicit-powered-assembler-preparation", native = setup.RootElement.Clone() });
            JsonElement before = await ReadAsync();
            var result = await new AssemblyController(game, new ControllerJournal(journalPath)).RunAsync("electronic-circuit", 80, token);
            JsonElement after = await ReadAsync();
            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var submissions = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data")).ToArray();
                int manualWork = submissions.Count(r => r.GetProperty("kind").GetString() is "mine" or "craft");
                var deliveries = submissions.Where(r => r.GetProperty("kind").GetString() == "insert")
                    .Select(r => r.GetProperty("args")).Where(r => r.GetProperty("entityId").GetString() == result.MachineId).ToArray();
                int largestCableDelivery = deliveries.Where(r => r.GetProperty("item").GetString() == "copper-cable")
                    .Select(r => r.GetProperty("count").GetInt32()).DefaultIfEmpty().Max();
                evidence.Add(new { check = "native-batch-delivery-and-costs", before, result, after, manualWork, largestCableDelivery,
                    deliveries = deliveries.Select(r => r.Clone()).ToArray() });
                Require(result.FinalStock == 80 && result.CompletedCrafts == 80 && after.GetProperty("circuits").GetInt64() == 80,
                    "Native carried stock and completed assembler cycles differ from the goal.");
                Require(Delta("circuitsProduced") == 80 && Delta("ironConsumed") == 80 && Delta("cableConsumed") == 240,
                    "Native production and recipe costs do not reconcile.");
                Require(after.GetProperty("iron").GetInt64() == 0 && after.GetProperty("cable").GetInt64() == 0
                    && after.GetProperty("inputItems").GetInt64() == 0 && after.GetProperty("outputItems").GetInt64() == 0
                    && !after.GetProperty("inProcess").GetBoolean(), "The final partial batch left unexplained ingredients or products.");
                Require(manualWork == 0 && largestCableDelivery == 198, "The native 200-cable stack did not produce a 66-cycle machine delivery.");
                Require(deliveries.Length == 4, "A supplied batch triggered rolling top-ups instead of two complete deliveries.");
                Require(after.GetProperty("energy").GetDouble() > 0 && before.GetProperty("character").GetUInt32() == after.GetProperty("character").GetUInt32(),
                    "Power or actor identity was lost during qualification.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;

            long Delta(string property) => after.GetProperty(property).GetInt64() - before.GetProperty(property).GetInt64();
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-assembly-batch-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement> ReadAsync()
        {
            const string read = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local machine=s.find_entities_filtered{name='assembling-machine-1',force=f,area={{6,0},{10,4}}}[1]; assert(c and machine); local stats=f.get_item_production_statistics(s); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,circuits=c.get_item_count('electronic-circuit'),iron=c.get_item_count('iron-plate'),cable=c.get_item_count('copper-cable'),circuitsProduced=stats.get_input_count('electronic-circuit'),ironConsumed=stats.get_output_count('iron-plate'),cableConsumed=stats.get_output_count('copper-cable'),inputItems=machine.get_inventory(defines.inventory.assembling_machine_input).get_item_count(),outputItems=machine.get_inventory(defines.inventory.assembling_machine_output).get_item_count(),inProcess=machine.is_crafting(),energy=machine.energy,players=#game.connected_players})
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
