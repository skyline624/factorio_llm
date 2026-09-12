using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Explicit synthetic fixture. Its material injections disqualify it as an autonomous campaign.</summary>
public sealed class NativeQualification(RuntimeSession session)
{
    private readonly List<object> evidence = [];
    private readonly IGameClient game = session.CreateClient();

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Native qualification requires a session started with --fixture.");
        string report = Path.Combine(session.Directory, "native-qualification-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await InitializeFixtureAsync(token);
            NativeState before = await ReadStateAsync(token);
            Require(!before.Peaceful && before.Pollution && before.Expansion, "Hostile world settings must stay active.");
            Require(before.PlayersConnected == 0, "Headless qualification must start without a connected player.");

            var move = await ExecuteAsync("move", new { position = new MapPosition(0, 4), tolerance = 0.2 }, 600, token);
            NativeState moved = await ReadStateAsync(token);
            Require(move.Receipt.Status == "completed", "Native movement did not complete.");
            Require(moved.Position.DistanceTo(new(0, 4)) <= 0.3, "Receipt reported arrival without native position evidence.");
            Require(moved.Tick - before.Tick >= 15, "Movement completed implausibly fast.");
            evidence.Add(new { check = "native-walk", before, after = moved, receipt = move.Receipt.Evidence });

            var craft = await ExecuteAsync("craft", new { recipe = "iron-gear-wheel", count = 2 }, 600, token);
            NativeState crafted = await ReadStateAsync(token);
            Require(craft.Receipt.Status == "completed", "Native crafting did not complete.");
            Require(crafted.Count("iron-gear-wheel") - moved.Count("iron-gear-wheel") == 2, "Crafted products missing.");
            Require(moved.Count("iron-plate") - crafted.Count("iron-plate") == 4, "Crafting ingredient cost is incorrect.");
            evidence.Add(new { check = "native-craft", before = moved, after = crafted, receipt = craft.Receipt.Evidence });

            var client = new OperationClient(game);
            OperationReceipt repeated = await client.SubmitAsync(craft.Submission, token);
            NativeState replayed = await ReadStateAsync(token);
            Require(repeated.Status == "completed" && replayed.Count("iron-gear-wheel") == crafted.Count("iron-gear-wheel"), "Duplicate id replayed a mutation.");
            GameResponse conflict = await game.ExecuteAsync(GameRequest.Create("submit", craft.Submission with { Args = Protocol.ToElement(new { recipe = "iron-gear-wheel", count = 3 }) }), token);
            Require(!conflict.Ok && conflict.Error?.Code == "operation_conflict", "Changed payload reused an operation id.");
            evidence.Add(new { check = "deduplication-and-conflict", repeated = repeated.Evidence, conflict });

            await ExecuteAsync("move", new { position = new MapPosition(0, 0), tolerance = 0.2 }, 600, token);
            var tooFar = await ExecuteAsync("mine", new { position = new MapPosition(3.5, 0.5), name = "iron-ore", count = 1 }, 600, token);
            Require(tooFar.Receipt.Status == "failed" && tooFar.Receipt.Error?.Code == "out_of_reach", "Mining outside the native resource reach was not refused.");
            await ExecuteAsync("move", new { position = new MapPosition(1.5, 0), tolerance = 0.2 }, 600, token);
            NativeState miningBefore = await ReadStateAsync(token);
            var mine = await ExecuteAsync("mine", new { position = new MapPosition(3.5, 0.5), name = "iron-ore", count = 3 }, 1800, token);
            NativeState mined = await ReadStateAsync(token);
            Require(mine.Receipt.Status == "completed", "Native mining did not complete.");
            Require(mined.Count("iron-ore") - miningBefore.Count("iron-ore") == 3, "Mining output does not match count.");
            Require(mined.Tick - miningBefore.Tick >= 60, "Mining bypassed normal elapsed time.");
            evidence.Add(new { check = "native-mine", before = miningBefore, after = mined, receipt = mine.Receipt.Evidence });

            GameResponse hello = await session.HelloAsync(token);
            ActorScope scope = hello.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
            var wait = OperationSubmission.Create(scope, "wait", new { ticks = 600 }, hello.Tick + 1200);
            OperationReceipt waiting = await client.SubmitAsync(wait, token);
            Require(!waiting.IsTerminal, "Long wait ended before cancellation.");
            OperationReceipt cancelled = await client.CancelAsync(wait.OperationId, token);
            Require(cancelled.Status == "cancelled", "Cancellation did not stop the game operation.");
            OperationReceipt queried = await client.QueryAsync(wait.OperationId, token);
            Require(queried.Status == "cancelled", "Cancelled state was not retained.");
            evidence.Add(new { check = "explicit-cancellation", receipt = queried.Evidence });
            await SaveReportAsync(report, true, null, token);
            return report;
        }
        catch (Exception error)
        {
            await SaveReportAsync(report, false, error.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task<(OperationSubmission Submission, OperationReceipt Receipt)> ExecuteAsync(string kind, object args, long ticks, CancellationToken token)
    {
        GameResponse hello = await session.HelloAsync(token);
        ActorScope scope = hello.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
        var submission = OperationSubmission.Create(scope, kind, args, hello.Tick + ticks);
        string journal = Path.Combine(session.Directory, "qualification-operations.jsonl");
        await File.AppendAllTextAsync(journal, JsonSerializer.Serialize(submission, Protocol.Json) + "\n", token);
        var client = new OperationClient(game);
        OperationReceipt receipt = await client.SubmitAsync(submission, token);
        if (!receipt.IsTerminal) receipt = await client.WaitAsync(submission.OperationId, TimeSpan.FromSeconds(ticks / 60.0 + 10), token);
        evidence.Add(new { operation = submission, receipt = receipt.Evidence });
        return (submission, receipt);
    }

    private async Task InitializeFixtureAsync(CancellationToken token)
    {
        GameResponse marker = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new { reason = "Synthetic native qualification; not an autonomous campaign" }), token);
        Require(marker.Ok && marker.Data.GetProperty("fixture").GetBoolean(), "Fixture marker was not persisted before setup.");
        const string command = """
            /silent-command local s=game.surfaces["nauvis"]; local chars=s.find_entities_filtered{type="character"}; assert(#chars==1,"Expected one agent character"); local c=chars[1]; c.teleport({0,0}); for _,e in pairs(s.find_entities_filtered{area={{-8,-8},{12,8}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-8,12 do for y=-8,8 do tiles[#tiles+1]={name="grass-1",position={x,y}} end end; s.set_tiles(tiles); c.get_inventory(defines.inventory.character_main).clear(); c.insert{name="iron-plate",count=12}; s.create_entity{name="iron-ore",position={3.5,0.5},amount=1000}; rcon.print("fixture-initialized");
            """;
        string output = await session.CreateRcon().ExecuteAsync(command, token);
        Require(output.Trim() == "fixture-initialized", "Fixture initialization was not acknowledged.");
        evidence.Add(new { check = "synthetic-fixture-initialized", disqualifiedAsCampaign = true, setup = "Cleared test area, positioned actor, inserted 12 iron plates and ore patch. Actions after initialization use the public mod protocol." });
    }

    public async Task<NativeState> ReadStateAsync(CancellationToken token)
    {
        const string command = """
            /silent-command local s=game.surfaces["nauvis"]; local chars=s.find_entities_filtered{type="character"}; assert(#chars==1,"Expected one agent character"); local c=chars[1]; local items={}; for _,v in pairs(c.get_inventory(defines.inventory.character_main).get_contents()) do items[v.name]=(items[v.name] or 0)+v.count end; rcon.print(helpers.table_to_json{tick=game.tick,position=c.position,items=items,health=c.health,playersConnected=#game.connected_players,peaceful=s.peaceful_mode,pollution=game.map_settings.pollution.enabled,expansion=game.map_settings.enemy_expansion.enabled});
            """;
        string json = await session.CreateRcon().ExecuteAsync(command, token);
        return JsonSerializer.Deserialize<NativeState>(json, Protocol.Json) ?? throw new InvalidDataException("No native evidence returned.");
    }

    private Task SaveReportAsync(string path, bool passed, string? error, CancellationToken token) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { kind = "synthetic-native-qualification", passed,
            error, seed = session.Seed, isAutonomousCampaign = false, recordedUtc = DateTime.UtcNow, evidence },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), token);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

public sealed record NativeState(long Tick, MapPosition Position, Dictionary<string, int> Items,
    double Health, int PlayersConnected, bool Peaceful, bool Pollution, bool Expansion)
{
    public int Count(string item) => Items.GetValueOrDefault(item);
}
