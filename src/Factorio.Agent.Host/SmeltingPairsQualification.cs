using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// One prepared burner drill feeding a cold furnace on a wide iron patch, a long plate demand and explicit bag stock for
/// helper kits (stone in a nearby chest): further pairs must be crafted from that stock, placed by the planner and shorten the native wait.
/// </summary>
public sealed class SmeltingPairsQualification(RuntimeSession session)
{
    private const int Target = 200, Carried = 30;

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Smelting pair qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        token = deadline.Token;
        string path = Path.Combine(session.Directory, $"smelting-pairs-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Iron patch, burner drill into cold furnace, 150 coal and 30 plates carried, 30 stone in a chest. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-40,-40},{40,40}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-40,40 do for y=-40,40 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({-3,-3})); c.get_main_inventory().clear(); assert(c.insert{name='coal',count=150}==150); assert(c.insert{name='iron-plate',count=30}==30); local stoneChest=s.create_entity{name='wooden-chest',position={-6.5,-3.5},force=f}; assert(stoneChest and stoneChest.insert{name='stone',count=30}==30); for x=1,12 do for y=-6,6 do assert(s.create_entity{name='iron-ore',position={x+.5,y+.5},amount=2000}) end end; local drill=s.create_entity{name='burner-mining-drill',position={2,0},direction=12,force=f}; local furnace=s.create_entity{name='stone-furnace',position={0,0},force=f}; assert(drill and furnace); rcon.print(helpers.table_to_json{tick=game.tick,drill=tostring(drill.unit_number),furnace=tostring(furnace.unit_number)})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "explicit-single-pair-preparation", native = setup.RootElement.Clone() });
            JsonElement before = await ReadAsync();
            Require(before.GetProperty("produced").GetDouble() == 0, "Use a fresh fixture for native iron production accounting.");
            var result = await new ProductionGoalExecutor(game, journal).RunAsync("iron-plate", Target, token);
            JsonElement after = await ReadAsync();
            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                string Type(JsonDocument r) => r.RootElement.GetProperty("type").GetString()!;
                var submissions = rows.Where(r => Type(r) == "submission").Select(r => r.RootElement.GetProperty("data")).ToArray();
                int mined = submissions.Count(r => r.GetProperty("kind").GetString() == "mine");
                int built = rows.Count(r => Type(r) == "smelting-pair-built");
                var failures = rows.Where(r => Type(r) is "smelting-pair-error" or "smelting-pair-no-site")
                    .Select(r => r.RootElement.GetProperty("data").Clone()).ToArray();
                // One burner pair delivers 0.25 plates a second: the remaining demand alone would take this many ticks.
                double singlePairTicks = (Target - Carried) / .25 * 60;
                long elapsed = result.EndTick - result.StartTick;
                evidence.Add(new { check = "helper-pairs-shorten-the-native-wait", before, result, after, mined, built, failures,
                    elapsed, singlePairTicks });
                Require(result.Method == "automated-smelting" && result.FinalStock >= Target, "The stock goal did not complete by automated smelting.");
                Require(built >= 2, "Fewer than two helper pairs were built from the carried stock.");
                Require(after.GetProperty("drills").GetInt32() == 1 + built && after.GetProperty("furnaces").GetInt32() == 1 + built,
                    "Native drills and furnaces do not match the journaled helper pairs.");
                Require(mined == 0, "Helper kits required manual mining.");
                Require(elapsed < singlePairTicks * .75, "Helper pairs did not shorten the wait below three quarters of one pair's time.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-smelting-pairs-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }
        return path;

        async Task<JsonElement> ReadAsync()
        {
            const string read = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; rcon.print(helpers.table_to_json{tick=game.tick,carried=c.get_item_count('iron-plate'),drills=s.count_entities_filtered{force=f,name='burner-mining-drill'},furnaces=s.count_entities_filtered{force=f,name='stone-furnace'},produced=f.get_item_production_statistics(s).get_input_count('iron-plate')})
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
