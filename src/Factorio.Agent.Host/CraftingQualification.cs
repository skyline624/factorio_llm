using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Synthetic regression for native handcraft accounting, cancellation and research triggers.</summary>
public sealed class CraftingQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Crafting qualification requires --fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        var operations = new OperationClient(game);
        var evidence = new List<object>();
        string path = Path.Combine(session.Directory, $"crafting-qualification-{Guid.NewGuid():N}.json");
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new { reason = "Synthetic handcraft accounting regression: injected ingredients and prerequisite technologies." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            await session.CreateRcon().ExecuteAsync("""
                /sc assert(#game.connected_players==0); local c=game.surfaces.nauvis.find_entities_filtered{type="character",force="factorio_agent"}[1]; assert(c and c.crafting_queue_size==0); c.force.technologies.electronics.researched=true; c.force.technologies["steam-power"].researched=true; c.force.technologies["automation-science-pack"].researched=false; c.insert{name="iron-gear-wheel",count=10}; c.insert{name="transport-belt",count=4}; c.insert{name="electronic-circuit",count=10}; c.insert{name="iron-plate",count=400}; rcon.print("ready");
                """, token);
            JsonElement before = await ReadAsync(token);
            var lab = await SubmitAsync("lab", 1);
            var receipt = await operations.WaitAsync(lab.OperationId, TimeSpan.FromSeconds(15), token);
            Require(receipt.Status == "completed", "Lab craft did not complete.");
            JsonElement after = await ReadAsync(token);
            evidence.Add(new { check = "lab-output-accounting", before, after, receipt = receipt.Evidence });
            Require(Delta(after, before, "labs") == 1 && Delta(after, before, "labProduction") == 1,
                "A completed native lab must increase both actual stock and production by exactly one.");
            Require(Delta(after, before, "gearsConsumed") == 10, "Native ingredient accounting was changed or doubled.");
            await operations.SubmitAsync(lab, token);
            JsonElement replay = await ReadAsync(token);
            Require(Delta(replay, after, "labProduction") == 0 && Delta(replay, after, "labs") == 0, "Replay credited a second product.");
            for (int attempt = 0; attempt < 30 && !replay.GetProperty("researched").GetBoolean(); attempt++)
            {
                await Task.Delay(100, token);
                replay = await ReadAsync(token);
            }
            Require(replay.GetProperty("researched").GetBoolean(), "The engine did not unlock the lab craft trigger.");
            evidence.Add(new { check = "native-trigger-and-replay", replay });

            before = await ReadAsync(token);
            var gears = await SubmitAsync("iron-gear-wheel", 100);
            for (int attempt = 0; attempt < 30; attempt++)
            {
                after = await ReadAsync(token);
                if (Delta(after, before, "gears") > 0) break;
                await Task.Delay(100, token);
            }
            receipt = await operations.CancelAsync(gears.OperationId, token);
            after = await ReadAsync(token);
            long produced = Delta(after, before, "gears");
            evidence.Add(new { check = "partial-craft-cancellation", before, after, receipt = receipt.Evidence });
            Require(receipt.Status == "cancelled" && produced is > 0 and < 100, "Fixture did not interrupt a partially completed craft.");
            Require(Delta(after, before, "gearProduction") == produced, "Partial outputs were omitted or cancelled crafts were credited.");
            Require(Delta(before, after, "plates") == 2 * produced, "Cancellation failed to refund unused ingredients.");
            await operations.CancelAsync(gears.OperationId, token);
            replay = await ReadAsync(token);
            Require(Delta(replay, after, "gearProduction") == 0, "Repeated cancellation doubled output statistics.");

            // Two products per recipe must be counted as two outputs, not two crafts.
            await session.CreateRcon().ExecuteAsync("""
                /sc local c=game.surfaces.nauvis.find_entities_filtered{type="character",force="factorio_agent"}[1]; c.insert{name="copper-plate",count=2}; rcon.print("cable-ready");
                """, token);
            before = await ReadAsync(token);
            var cable = await SubmitAsync("copper-cable", 2);
            receipt = await operations.WaitAsync(cable.OperationId, TimeSpan.FromSeconds(10), token);
            after = await ReadAsync(token);
            Require(receipt.Status == "completed" && Delta(after, before, "cables") == 4
                && Delta(after, before, "cableProduction") == 4, "Multi-output crafting accounting is incorrect.");
            evidence.Add(new { check = "two-products-per-craft", before, after, receipt = receipt.Evidence });

            // The fixture has spare iron plates, enough for native recursive gears, but no direct gears.
            await session.CreateRcon().ExecuteAsync("""
                /sc local c=game.surfaces.nauvis.find_entities_filtered{type="character",force="factorio_agent"}[1]; c.remove_item{name="iron-gear-wheel",count=c.get_item_count("iron-gear-wheel")}; rcon.print("direct-ingredient-fixture");
                """, token);
            GameResponse observed = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
            var rejected = OperationSubmission.Create(observed.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!,
                "craft", new { recipe = "transport-belt", count = 1 }, observed.Tick + 600);
            before = await ReadAsync(token);
            receipt = await operations.SubmitAsync(rejected, token);
            after = await ReadAsync(token);
            Require(receipt.Status == "failed" && receipt.Error?.Code == "direct_ingredients_missing"
                && Delta(after, before, "plates") == 0, "A hidden recursive handcraft queue was not refused before mutation.");
            evidence.Add(new { check = "recursive-craft-refused", before, after, receipt = receipt.Evidence });
            passed = true;
            return path;

            async Task<OperationSubmission> SubmitAsync(string recipe, int count)
            {
                GameResponse state = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
                var scope = state.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
                var submission = OperationSubmission.Create(scope, "craft", new { recipe, count }, state.Tick + 18000);
                var accepted = await operations.SubmitAsync(submission, token);
                Require(accepted.Status is "running" or "completed", "Craft submission rejected.");
                return submission;
            }
        }
        finally
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { kind = "synthetic-crafting-qualification", passed,
                isAutonomousCampaign = false, evidence }, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }), CancellationToken.None);
        }
    }

    private async Task<JsonElement> ReadAsync(CancellationToken token)
    {
        string json = await session.CreateRcon().ExecuteAsync("""
            /sc local c=game.surfaces.nauvis.find_entities_filtered{type="character",force="factorio_agent"}[1]; local s=c.force.get_item_production_statistics(c.surface); rcon.print(helpers.table_to_json({tick=game.tick,labs=c.get_item_count("lab"),gears=c.get_item_count("iron-gear-wheel"),plates=c.get_item_count("iron-plate"),cables=c.get_item_count("copper-cable"),labProduction=s.get_input_count("lab"),gearProduction=s.get_input_count("iron-gear-wheel"),cableProduction=s.get_input_count("copper-cable"),gearsConsumed=s.get_output_count("iron-gear-wheel"),researched=c.force.technologies["automation-science-pack"].researched,players=#game.connected_players}));
            """, token);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private static long Delta(JsonElement after, JsonElement before, string key) => after.GetProperty(key).GetInt64() - before.GetProperty(key).GetInt64();
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
