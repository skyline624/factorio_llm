using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Prepared cold smelter whose powered output inserter drains it into a chest; five existing plates are explicit supplies.</summary>
public sealed class SmeltingCollectionQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Smelting collection qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        token = deadline.Token;
        string path = Path.Combine(session.Directory, $"smelting-collection-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared copper drill, cold furnace, powered output inserter, five chest plates and coal. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-20,-20},{20,20}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-20,20 do for y=-20,20 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({-3,-3})); c.get_main_inventory().clear(); assert(c.insert{name='coal',count=40}==40); for x=1,2 do for y=-1,0 do assert(s.create_entity{name='copper-ore',position={x+.5,y+.5},amount=1000}) end end; local drill=s.create_entity{name='burner-mining-drill',position={2,0},direction=12,force=f}; local furnace=s.create_entity{name='stone-furnace',position={0,0},force=f}; local arm=s.create_entity{name='fast-inserter',position={-1.5,-.5},direction=4,force=f}; local chest=s.create_entity{name='iron-chest',position={-2.5,-.5},force=f}; local source=s.create_entity{name='electric-energy-interface',position={-4,2},force=f}; assert(drill and furnace and arm and chest and source); source.electric_buffer_size=1000000000; source.power_production=3000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-1.5,.5},force=f}); assert(chest.insert{name='copper-plate',count=5}==5); rcon.print(helpers.table_to_json{tick=game.tick,drill=tostring(drill.unit_number),furnace=tostring(furnace.unit_number),chest=tostring(chest.unit_number),initialChest=chest.get_item_count('copper-plate')})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "explicit-cold-smelter-preparation", native = setup.RootElement.Clone() });
            JsonElement before = await ReadAsync();
            Require(before.GetProperty("produced").GetDouble() == 0, "Use a fresh fixture for native copper production accounting.");
            var result = await new ProductionGoalExecutor(game, journal).RunAsync("copper-plate", 20, token);
            JsonElement after = await ReadAsync();
            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var submissions = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data")).ToArray();
                int manual = submissions.Count(r => r.GetProperty("kind").GetString() is "mine" or "craft" or "build");
                string chest = setup.RootElement.GetProperty("chest").GetString()!;
                int chestTakes = submissions.Count(r => r.GetProperty("kind").GetString() == "take"
                    && r.GetProperty("args").GetProperty("entityId").GetString() == chest);
                evidence.Add(new { check = "native-output-collected-after-inserter", before, result, after, manual, chestTakes });
                Require(result.FinalStock >= 20 && result.InitialStock == 0 && result.Method == "automated-smelting",
                    "Partial stored stock did not leave the remaining demand to native automated smelting.");
                Require(chestTakes >= 2 && after.GetProperty("produced").GetDouble() - before.GetProperty("produced").GetDouble() >= 15,
                    "The inserter's chest did not supply both initial stock and newly manufactured copper.");
                Require(manual == 0, "The prepared extraction chain required mining, crafting or new construction.");
                Require(after.GetProperty("carried").GetInt64() + after.GetProperty("stored").GetInt64()
                    + after.GetProperty("furnaceOutput").GetInt64() + after.GetProperty("transit").GetInt64()
                    == 5 + after.GetProperty("produced").GetDouble(),
                    "Prepared and natively manufactured copper do not reconcile with remaining stocks.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-smelting-collection-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }
        return path;

        async Task<JsonElement> ReadAsync()
        {
            const string read = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local stored=0; local output=0; local transit=0; for _,e in pairs(s.find_entities_filtered{force=f,area={{-20,-20},{20,20}}}) do if e.type=='container' then stored=stored+e.get_item_count('copper-plate') end; if e.type=='furnace' then output=output+e.get_output_inventory().get_item_count('copper-plate') end; if e.type=='inserter' and e.held_stack.valid_for_read and e.held_stack.name=='copper-plate' then transit=transit+e.held_stack.count end end; rcon.print(helpers.table_to_json{tick=game.tick,carried=c.get_item_count('copper-plate'),stored=stored,furnaceOutput=output,transit=transit,produced=f.get_item_production_statistics(s).get_input_count('copper-plate'),players=#game.connected_players})
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
