using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Fixture-only comparison of stop gaps on the same native corridor and unchanged character.</summary>
public sealed class NavigationFlowQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Navigation flow qualification requires a fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string id = Guid.NewGuid().ToString("N");
        string reportPath = Path.Combine(session.Directory, $"navigation-flow-{id}.json");
        var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
        {
            reason = "Synthetic open-corridor comparison; terrain cleared and actor reset between trials. Not a campaign."
        }), token);
        Require(marked.Ok, "Fixture marking failed.");
        const string setup = """
            /silent-command local s=game.surfaces.nauvis;local c=s.find_entities_filtered{type='character',force='factorio_agent'}[1];assert(c);for _,p in pairs(game.connected_players) do assert(p.character==c) end;game.speed=1;for _,e in pairs(s.find_entities_filtered{area={{-40,-40},{40,40}}}) do if e~=c then e.destroy() end end;local tiles={};for x=-40,40 do for y=-40,40 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end;s.set_tiles(tiles);assert(c.teleport({0,0}));rcon.print('flow-fixture-ready')
            """;
        Require((await native.ExecuteAsync(setup, token)).Trim() == "flow-fixture-ready", "Fixture preparation failed.");
        var trials = new List<object>();
        bool passed = false;
        string? errorText = null;
        try
        {
            var baseline = await TrialAsync(8);
            Require((await native.ExecuteAsync("/silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force='factorio_agent'}[1];assert(c.teleport({0,0}));rcon.print('fixture-reset')", token)).Trim() == "fixture-reset",
                "Fixture reset failed.");
            var continuous = await TrialAsync(24);
            Require(baseline.Result.Receipts.Count >= 3 && continuous.Result.Receipts.Count == 1
                && continuous.GapTicks == 0 && baseline.GapTicks > 0,
                "The open corridor did not eliminate the intermediate native stops.");
            Require(baseline.Before.Character == continuous.After.Character,
                "The comparison replaced the actor.");
            passed = true;
        }
        catch (Exception error)
        {
            errorText = error.Message;
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                kind = "synthetic-navigation-flow-comparison", passed, error = errorText,
                isAutonomousCampaign = false, trials,
                interpretation = "Native movement receipts measure stops and tick gaps, not client frame smoothness or latency hiding."
            }, new JsonSerializerOptions(Protocol.Json) { WriteIndented = true }), CancellationToken.None);
        }
        return reportPath;

        async Task<FlowTrial> TrialAsync(int limit)
        {
            var before = await ReadAsync();
            await using var controller = new SpatialController(game,
                new ControllerJournal(Path.Combine(session.Directory, $"navigation-flow-{id}-{limit}.jsonl")), limit);
            var result = await controller.NavigateAsync(new(24, 0), cancellationToken: token);
            var after = await ReadAsync();
            Require(result.Receipts.All(r => r.Status == "completed") && after.Position.DistanceTo(new(24, 0)) <= .4,
                "The trial did not complete through native walking.");
            Require(before.Character == after.Character
                && before.Health == after.Health && before.Players == after.Players
                && (after.Players == 0 || after.PilotCharacter == after.Character)
                && before.Inventory == after.Inventory && !after.Walking,
                "Actor, health, inventory, pilot identity, or final stopped state changed unexpectedly.");
            long gaps = result.Receipts.Zip(result.Receipts.Skip(1), (a, b) => b.AcceptedTick!.Value - a.UpdatedTick).Sum();
            long active = result.Receipts.Sum(r => r.UpdatedTick - r.AcceptedTick!.Value);
            var trial = new FlowTrial(limit, before, after, result, gaps, active, after.Tick - before.Tick);
            trials.Add(trial);
            return trial;
        }

        async Task<FlowState> ReadAsync()
        {
            const string read = """
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force='factorio_agent'}[1];local p=game.connected_players[1];rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,position=c.position,health=c.health,walking=c.walking_state.walking,players=#game.connected_players,pilotCharacter=p and p.character and p.character.unit_number or 0,inventory=helpers.table_to_json(c.get_main_inventory().get_contents())})
                """;
            return JsonSerializer.Deserialize<FlowState>(await native.ExecuteAsync(read, token), Protocol.Json)
                ?? throw new InvalidDataException("Missing native movement measurement.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record FlowState(long Tick, long Character, MapPosition Position,
        double Health, bool Walking, int Players, long PilotCharacter, string Inventory);
    private sealed record FlowTrial(int Limit, FlowState Before, FlowState After, NavigationResult Result,
        long GapTicks, long ActiveTicks, long TotalTicks);
}
