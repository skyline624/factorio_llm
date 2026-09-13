using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepared injured character escapes a native worm range without turret support.</summary>
public sealed class UncoveredRetreatQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Uncovered retreat requires an explicit fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string path = Path.Combine(session.Directory, $"uncovered-retreat-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var defense = new DefenseController(game, journal);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared injured armed character and active worm, no local defensive refuge. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            var setup = await CommandAsync("""
                /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and c.crafting_queue_size==0);for _,p in pairs(game.connected_players) do assert(p.character==c) end;game.speed=1;for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end;local tiles={};for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end;s.set_tiles(tiles);assert(c.teleport({0,0}));c.health=80;c.get_main_inventory().clear();c.get_inventory(defines.inventory.character_guns).clear();c.get_inventory(defines.inventory.character_ammo).clear();assert(c.get_inventory(defines.inventory.character_guns).insert{name='pistol',count=1}==1);assert(c.get_inventory(defines.inventory.character_ammo).insert{name='firearm-magazine',count=20}==20);c.selected_gun_index=1;local w=s.create_entity{name='small-worm-turret',position={20,0},force=game.forces.enemy};assert(w);rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,worm=tostring(w.unit_number),players=#game.connected_players})
                """);
            var before = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe"), token));
            evidence.Add(new { check = "native-initial-condition", setup, before });
            Require(before.Health <= before.MaxHealth * .4 && before.Health > 0 && before.Weapon.Ready && before.Defenses!.Count == 0,
                "Prepared injured actor without turret support was not established.");
            var map = await new SpatialClient(game).CaptureAsync(cancellationToken: token);
            var worm = map.StationaryThreats!.Single(t => t.Id == setup.GetProperty("worm").GetString());
            Require(before.Position!.DistanceTo(worm.Position) < worm.Range, "The character is already outside native worm range.");
            evidence.Add(new { check = "prepared-active-worm-without-refuge", setup, before, worm });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            SafetyObservation after;
            do
            {
                await defense.StepAsync(deadline.Token);
                await Task.Delay(50, deadline.Token);
                after = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe"), deadline.Token));
                Require(after.Alive && after.Scope == before.Scope && after.Health > 0, "The character died or changed during retreat.");
            } while (after.Position!.DistanceTo(worm.Position) < worm.Range + SpatialCollisionField.StationaryThreatMargin + .5);
            await defense.StopOwnedActionAsync(token);
            after = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe"), token));
            int plans = 0, moves = 0, shots = 0;
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                var type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "retreat-plan" && data.GetProperty("plan").GetProperty("status").GetString() == "separation") plans++;
                if (type == "submission")
                {
                    string? kind = data.GetProperty("kind").GetString();
                    if (kind == "move") moves++;
                    if (kind == "shoot") shots++;
                }
            }
            var pilot = await CommandAsync("""
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force='factorio_agent'}[1];assert(c);local attached=true;for _,p in pairs(game.connected_players) do if p.character~=c then attached=false end end;rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,attached=attached})
                """);
            evidence.Add(new { check = "native-uncovered-retreat", before, after, plans, moves, shots, pilot });
            Require(plans >= 3 && moves >= 3 && shots == 0 && after.Weapon.Rounds == before.Weapon.Rounds
                && after.Position!.DistanceTo(worm.Position) >= worm.Range + SpatialCollisionField.StationaryThreatMargin,
                "Retreat did not establish native separation without ammunition consumption.");
            Require(pilot.GetProperty("attached").GetBoolean() && pilot.GetProperty("players").GetInt32() == setup.GetProperty("players").GetInt32()
                && pilot.GetProperty("character").GetUInt32() == setup.GetProperty("character").GetUInt32(),
                "Retreat lost the native character or pilot attachment.");
            passed = true;
            return path;
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await defense.StopOwnedActionAsync(cleanup.Token);
            }
            finally { await LocalJson.WriteAsync(path, new { kind = "prepared-uncovered-retreat-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None); }
        }

        async Task<JsonElement> CommandAsync(string command)
        {
            using var document = JsonDocument.Parse(await native.ExecuteAsync(command, token));
            return document.RootElement.Clone();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
