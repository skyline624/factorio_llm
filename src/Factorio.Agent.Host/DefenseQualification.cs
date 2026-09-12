using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed class DefenseQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Defense qualification requires an explicit fixture session.");
        string id = Guid.NewGuid().ToString("N");
        string reportPath = Path.Combine(session.Directory, $"defense-qualification-{id}.json");
        var evidence = new List<object>();
        using var lease = ActorControlLease.Acquire(session.Directory);
        IGameClient game = session.CreateClient(lease);
        var journal = new ControllerJournal(Path.Combine(session.Directory, $"defense-qualification-{id}.jsonl"));
        var controller = new DefenseController(game, journal);
        try
        {
            GameResponse marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new { reason = "Synthetic C# reactive defense qualification." }), token);
            Require(marked.Ok, "Fixture marking failed.");
            SafetyObservation initial = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe"), token));
            Require(initial.Alive && initial.ControlMode == "ai" && initial.Operation is not { IsTerminal: false },
                "Defense fixture requires an idle AI character.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; assert(c); for _,e in ipairs(s.find_entities_filtered{position=c.position,radius=32,force="enemy"}) do e.destroy() end; c.health=c.max_health; local g=c.get_inventory(defines.inventory.character_guns); local a=c.get_inventory(defines.inventory.character_ammo); g.clear(); a.clear(); g.insert{name="pistol",count=1}; a.insert{name="firearm-magazine",count=20}; c.selected_gun_index=1; rcon.print("defense-equipment-fixture");
                """;
            Require((await session.CreateRcon().ExecuteAsync(prepare, token)).Trim() == "defense-equipment-fixture", "Defense fixture equipment failed.");
            SafetyObservation ready = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe"), token));
            Require(ready.Weapon.Ready && ready.Weapon.Rounds == 200 && ready.Weapon.Range > 0, "Equipped native weapon telemetry is not usable.");
            DefenseNativeState before = await ReadNativeAsync(token);
            Require(before.CharacterCount == 1 && (before.ConnectedPlayers == 0 || before.PilotCharacterId == before.CharacterId),
                "The connected pilot does not control the one native agent character.");
            evidence.Add(new { check = "synthetic-defense-equipment", disqualifiedAsCampaign = true,
                setup = "Removed local fixture enemies, restored health and equipped a pistol with twenty magazines.", ready.Weapon });
            var work = OperationSubmission.Create(ready.Scope, "wait", new { ticks = 3600 }, ready.Tick + 4000);
            await journal.AppendAsync("synthetic-work-submission", work, token);
            var operations = new OperationClient(game);
            Require(!(await operations.SubmitAsync(work, token)).IsTerminal, "Synthetic production wait did not start.");
            const string attack = """
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; local p=s.find_non_colliding_position("small-biter",{c.position.x+6,c.position.y+4},2,0.5); assert(p); local b=s.create_entity{name="small-biter",position=p,force="enemy"}; assert(b and b.commandable); b.commandable.set_command{type=defines.command.attack,target=c,distraction=defines.distraction.none}; rcon.print(helpers.table_to_json({tick=game.tick,entityId=tostring(b.unit_number)}));
                """;
            using JsonDocument attacker = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(attack, token));
            long attackTick = attacker.RootElement.GetProperty("tick").GetInt64();
            evidence.Add(new { check = "synthetic-attack", disqualifiedAsCampaign = true, attacker = attacker.RootElement.Clone() });
            List<DefenseStep> steps = [];
            DefenseNativeState after = await ReadNativeAsync(token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            while (after.Enemies > 0 && after.Health > 0)
            {
                steps.Add(await controller.StepAsync(deadline.Token));
                await Task.Delay(150, deadline.Token);
                after = await ReadNativeAsync(deadline.Token);
            }
            OperationReceipt interrupted = await operations.QueryAsync(work.OperationId, token);
            Require(interrupted.Status == "cancelled" && steps.Any(s => s.State == "preempted"),
                "The C# arbiter did not preempt existing work.");
            DefenseStep? firstShot = steps.FirstOrDefault(s => s.State == "defending");
            Require(firstShot is not null && firstShot.Tick - attackTick <= 120,
                "No defense submission was observed within two simulation seconds of attack creation.");
            Require(after.Enemies == 0 && after.Health > 0 && after.Rounds < ready.Weapon.Rounds,
                "Reactive defense lacks native evidence of survival, ammunition use, and target removal.");
            Require(after.CharacterId == before.CharacterId && after.CharacterCount == 1
                && after.ConnectedPlayers == before.ConnectedPlayers
                && (after.ConnectedPlayers == 0 || after.PilotCharacterId == after.CharacterId),
                "Defense changed or duplicated the native character or lost pilot attachment.");
            evidence.Add(new { check = "native-reactive-preemption-and-defense", attackTick, firstShot,
                before, after, interrupted = interrupted.Evidence, steps,
                scope = "One synthetic attacker, no LLM dependency. Retreat and factory defense remain unqualified." });
            await controller.StopOwnedActionAsync(token);
            await SaveAsync(true, null, token);
            return reportPath;
        }
        catch (Exception error)
        {
            await SaveAsync(false, error.Message, CancellationToken.None);
            throw;
        }

        Task SaveAsync(bool passed, string? error, CancellationToken saveToken) => File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(new { kind = "synthetic-reactive-defense-qualification", passed, error,
                isAutonomousCampaign = false, seed = session.Seed, evidence },
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), saveToken);
    }

    private async Task<DefenseNativeState> ReadNativeAsync(CancellationToken token)
    {
        const string command = """
            /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; local rounds=0; if c then local a=c.get_inventory(defines.inventory.character_ammo); for i=1,#a do local v=a[i]; if v.valid_for_read then rounds=rounds+(v.count-1)*v.prototype.magazine_size+v.ammo end end end; local p=game.connected_players[1]; rcon.print(helpers.table_to_json({tick=game.tick,health=c and c.health or 0,rounds=rounds,enemies=c and s.count_entities_filtered{position=c.position,radius=32,force="enemy",type="unit"} or -1,characterId=c and c.unit_number or 0,characterCount=s.count_entities_filtered{type="character"},connectedPlayers=#game.connected_players,pilotCharacterId=p and p.character and p.character.unit_number or 0}));
            """;
        return JsonSerializer.Deserialize<DefenseNativeState>(await session.CreateRcon().ExecuteAsync(command, token), Protocol.Json)
            ?? throw new InvalidDataException("No native defense state.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}

public sealed record DefenseNativeState(long Tick, double Health, int Rounds, int Enemies,
    long CharacterId, int CharacterCount, int ConnectedPlayers, long PilotCharacterId);
