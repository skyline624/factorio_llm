using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Host;

/// <summary>Prepared native crafts prove per-goal journals and reconciliation without repeating a completed craft.</summary>
public sealed class CampaignJournalQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Campaign journal qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(session.Directory, $"campaign-journal-qualification-{id}.json");
        string indexPath = Path.ChangeExtension(path, ".jsonl");
        string memoryPath = Path.Combine(session.Directory, $"campaign-journal-memory-{id}.json");
        using var journal = new CampaignJournal(indexPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared six iron plates; three native crafts with a deliberately missing second terminal journal receipt. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and c.crafting_queue_size==0);for _,p in pairs(game.connected_players) do assert(p.character==c) end;game.speed=1;for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{64,64}}}) do if e~=c then e.destroy() end end;local tiles={};for x=-8,8 do for y=-8,8 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end;s.set_tiles(tiles);assert(c.teleport({0,0}));c.health=c.max_health;c.get_main_inventory().clear();assert(c.insert{name='iron-plate',count=6}==6);rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "explicit-native-craft-preparation", native = setup.RootElement.Clone() });
            JsonElement before = await ReadAsync();
            var runner = new PreparedRunner(game, journal);
            var result = await new StrategicCampaignController(game, runner, memoryPath, indexPath, campaignJournal: journal).RunAsync(3, token);
            JsonElement after = await ReadAsync();
            var memory = JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(memoryPath, token), Protocol.Json)!;
            var indexRows = (await File.ReadAllLinesAsync(indexPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
            string[] segments;
            try
            {
                segments = indexRows.Where(r => r.RootElement.GetProperty("type").GetString() == "goal-journal")
                    .Select(r => r.RootElement.GetProperty("data").GetProperty("journalPath").GetString()!).ToArray();
            }
            finally { foreach (var row in indexRows) row.Dispose(); }
            evidence.Add(new { check = "three-crafts-without-replay", before, result, after, segments,
                runner.StartingStocks, runner.Operations, runner.Feedback, memory });
            Require(segments.Length == 3 && segments.Distinct(StringComparer.Ordinal).Count() == 3,
                "The three goal attempts do not have separate journals.");
            Require(runner.StartingStocks.SequenceEqual(new long[] { 0, 1, 2 }) && runner.Operations.Distinct().Count() == 3,
                "The interrupted native craft was repeated or its stock was lost.");
            Require(runner.Feedback[2]?.Contains("interrupted-goal-reconciled", StringComparison.Ordinal) == true && !memory.Pending,
                "The missing journal receipt was not reconciled before the next goal.");
            Require(after.GetProperty("gears").GetInt64() == 3 && after.GetProperty("iron").GetInt64() == 0
                && before.GetProperty("iron").GetInt64() == 6 && after.GetProperty("queue").GetInt32() == 0,
                "Native outputs, costs or queue do not reconcile.");
            Require(before.GetProperty("character").GetUInt32() == after.GetProperty("character").GetUInt32(),
                "The actor changed during the native qualification.");
            for (int index = 0; index < segments.Length; index++)
            {
                var rows = (await File.ReadAllLinesAsync(segments[index], token)).Select(line => JsonDocument.Parse(line)).ToArray();
                try
                {
                    Require(rows.Count(r => r.RootElement.GetProperty("type").GetString() == "submission") == 1,
                        "A segment contains another goal's submitted operation.");
                    Require(rows.Count(r => r.RootElement.GetProperty("type").GetString() == "receipt") == (index == 1 ? 0 : 1),
                        "The missing receipt scenario or completed receipt evidence is incorrect.");
                }
                finally { foreach (var row in rows) row.Dispose(); }
            }
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-campaign-journal-qualification", passed,
                isAutonomousCampaign = false, indexPath, memoryPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement> ReadAsync()
        {
            const string read = """
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force='factorio_agent'}[1];assert(c);rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,iron=c.get_item_count('iron-plate'),gears=c.get_item_count('iron-gear-wheel'),queue=c.crafting_queue_size,players=#game.connected_players})
                """;
            using var value = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(read, token));
            return value.RootElement.Clone();
        }
    }

    private sealed class PreparedRunner(IGameClient game, CampaignJournal journal) : IStrategicGoalRunner
    {
        public List<long> StartingStocks { get; } = [];
        public List<string> Operations { get; } = [];
        public List<string?> Feedback { get; } = [];

        public async Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var before = await new ProductionController(game, journal).ObserveAsync(deadline.Token);
            long initial = before.Inventory.GetValueOrDefault("iron-gear-wheel");
            StartingStocks.Add(initial);
            Feedback.Add(previousResult);
            int target = StartingStocks.Count;
            await journal.AppendAsync("strategic-context", new { observationId = $"fixture:{before.Tick}",
                facts = JsonSerializer.Serialize(new { observedTick = before.Tick }, Protocol.Json) }, deadline.Token);
            var goal = new GoalProposal($"fixture:{before.Tick}", "Prepared journal qualification craft", GoalCategory.Production,
                "iron-gear-wheel", target, GoalUnit.Items, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null));
            await journal.AppendAsync("strategic-goal", goal, deadline.Token);
            var submission = OperationSubmission.Create(before.Scope, "craft", new { recipe = "iron-gear-wheel", count = 1 }, before.Tick + 600);
            await journal.AppendAsync("submission", submission, deadline.Token);
            Operations.Add(submission.OperationId);
            var operations = new OperationClient(game);
            var receipt = await operations.SubmitAsync(submission, deadline.Token);
            while (!receipt.IsTerminal)
            {
                await Task.Delay(100, deadline.Token);
                receipt = await operations.QueryAsync(submission.OperationId, deadline.Token);
            }
            Require(receipt.Status == "completed", "The prepared native craft did not complete.");
            if (target == 2) throw new IOException("Synthetic missing terminal journal receipt after a completed native craft.");
            await journal.AppendAsync("receipt", receipt, deadline.Token);
            var after = await new ProductionController(game, journal).ObserveAsync(deadline.Token);
            Require(after.Scope == before.Scope && after.Inventory.GetValueOrDefault("iron-gear-wheel") == target,
                "The completed native craft has inconsistent stock or actor scope.");
            return new(goal, Production: new("native-fixture-craft", "iron-gear-wheel", target, initial, target, before.Tick, after.Tick));
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
