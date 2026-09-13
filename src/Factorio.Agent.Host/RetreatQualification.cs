using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepared unarmed retreat around native obstacles into existing turret coverage.</summary>
public sealed class RetreatQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Retreat qualification requires an explicit fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string path = Path.Combine(session.Directory, $"retreat-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        var defense = new DefenseController(game, journal);
        string? waitingId = null;
        bool passed = false;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared low-health unarmed actor, wall, loaded turret and pursuing biter; C# retreat routing. Not a campaign." }), token);
            if (!marked.Ok) throw new GameRpcException(marked.Error!);
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-40,-40},{40,40}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-40,40 do for y=-40,40 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=80; c.get_main_inventory().clear(); c.get_inventory(defines.inventory.character_guns).clear(); c.get_inventory(defines.inventory.character_ammo).clear(); local turret=s.create_entity{name='gun-turret',position={-24,0},force=f}; assert(turret); local empty=helpers.json_to_table(remote.call('factorio_agent','execute',helpers.table_to_json{protocolVersion=1,requestId='empty-refuge-proof',action='observe',arguments={radius=32,limit=200}})); assert(empty.ok and next(empty.data.defenses)==nil); assert(turret.insert{name='firearm-magazine',count=10}==10); for y=-1.5,1.5,1 do assert(s.create_entity{name='stone-wall',position={-6.5,y},force=f}) end; rcon.print(helpers.table_to_json{tick=game.tick,turretId=tostring(turret.unit_number),emptyTurretIgnored=true})
                """;
            var setup = await CommandAsync(prepare, token);
            string turretId = setup.GetProperty("turretId").GetString()!;
            var before = await ReadAsync(turretId, "0", token);
            evidence.Add(new { check = "explicit-preparation", setup, before });
            var initial = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe"), token));
            Require(!initial.Weapon.Ready && initial.Defenses!.Any(d => d.Id == turretId && d.AmmoRounds == 100),
                "The observed unarmed actor and loaded turret were not established.");
            var waiting = OperationSubmission.Create(initial.Scope, "wait", new { ticks = 3600 }, initial.Tick + 4000);
            waitingId = waiting.OperationId;
            await journal.AppendAsync("submission", waiting, token);
            var receipt = await new OperationClient(game).SubmitAsync(waiting, token);
            await journal.AppendAsync("receipt", receipt, token);
            Require(!receipt.IsTerminal, "Prepared work did not start.");
            const string attack = """
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; local b=c.surface.create_entity{name='small-biter',position={8,0},force=game.forces.enemy}; assert(b and b.commandable); b.commandable.set_command{type=defines.command.attack,target=c,distraction=defines.distraction.none}; rcon.print(helpers.table_to_json{tick=game.tick,enemyId=tostring(b.unit_number),position=b.position})
                """;
            var spawned = await CommandAsync(attack, token);
            string enemyId = spawned.GetProperty("enemyId").GetString()!;
            evidence.Add(new { check = "prepared-pursuit", spawned });
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(60));
            JsonElement after;
            do
            {
                await defense.StepAsync(deadline.Token);
                after = await ReadAsync(turretId, enemyId, deadline.Token);
                Require(after.GetProperty("character").GetString() == before.GetProperty("character").GetString()
                    && after.GetProperty("incarnation").GetInt64() == before.GetProperty("incarnation").GetInt64(), "The actor died during retreat.");
                if (!after.GetProperty("enemyAlive").GetBoolean()) break;
                await Task.Delay(100, deadline.Token);
            } while (true);
            await defense.StopOwnedActionAsync(token);
            after = await ReadAsync(turretId, enemyId, token);
            var stopped = await new OperationClient(game).QueryAsync(waitingId, token);
            waitingId = null;
            int plans = 0, completedMoves = 0;
            bool wentAroundWall = false;
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "retreat-plan" && data.GetProperty("plan").GetProperty("status").GetString() == "found")
                {
                    Require(data.GetProperty("plan").GetProperty("refugeId").GetString() == turretId, "Retreat chose another refuge.");
                    plans++;
                }
                if (type is "receipt" or "final-receipt" && data.GetProperty("kind").GetString() == "move"
                    && data.GetProperty("status").GetString() == "completed")
                {
                    completedMoves++;
                    var position = data.GetProperty("effects").GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
                    if (Math.Abs(position.Y) > 2) wentAroundWall = true;
                }
            }
            var finalPosition = after.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            int turretRounds = after.GetProperty("turretRounds").GetInt32();
            Require(stopped.Status == "cancelled" && plans >= 3 && completedMoves >= 3 && wentAroundWall && finalPosition.X < -7,
                "Native movement did not establish a retreat around the obstacle.");
            Require(turretRounds is > 0 and < 100 && after.GetProperty("actorRounds").GetInt32() == 0
                && after.GetProperty("health").GetDouble() > 0 && after.GetProperty("pilotAttached").GetBoolean()
                && after.GetProperty("players").GetInt32() == before.GetProperty("players").GetInt32(), "Turret-supported survival and pilot continuity were not proven.");
            evidence.Add(new { check = "native-retreat-and-turret-defense", before, after, plans, completedMoves, wentAroundWall, stopped });
            passed = true;
            return path;
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await defense.StopOwnedActionAsync(cleanup.Token);
                if (waitingId is not null)
                {
                    var operations = new OperationClient(game);
                    var receipt = await operations.QueryAsync(waitingId, cleanup.Token);
                    if (!receipt.IsTerminal) receipt = await operations.CancelAsync(waitingId, cleanup.Token);
                    await journal.AppendAsync("final-receipt", receipt, cleanup.Token);
                }
            }
            catch (Exception error) { evidence.Add(new { check = "cleanup-unconfirmed", error = error.Message }); }
            await LocalJson.WriteAsync(path, new { kind = "prepared-retreat-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement> CommandAsync(string command, CancellationToken ct)
        {
            string response = await native.ExecuteAsync(command, ct);
            if (!response.TrimStart().StartsWith('{')) throw new InvalidDataException("Native retreat fixture failed: " + response[..Math.Min(response.Length, 1500)]);
            using var value = JsonDocument.Parse(response);
            return value.RootElement.Clone();
        }
        Task<JsonElement> ReadAsync(string turret, string enemy, CancellationToken ct)
        {
            static string Id(string value) => long.Parse(value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return CommandAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c); local t; for _,e in pairs(s.find_entities_filtered{type='ammo-turret',force=f}) do if e.unit_number==TURRET_ID then t=e;break end end; assert(t); local alive=false; for _,e in pairs(s.find_entities_filtered{type='unit',force=game.forces.enemy}) do if e.unit_number==ENEMY_ID then alive=true;break end end; local inv=t.get_inventory(defines.inventory.turret_ammo); local rounds=0; for i=1,#inv do local a=inv[i]; if a.valid_for_read then rounds=rounds+(a.count-1)*a.prototype.magazine_size+a.ammo end end; local o=helpers.json_to_table(remote.call('factorio_agent','execute',helpers.table_to_json{protocolVersion=1,requestId='native-retreat-proof',action='observe',arguments={radius=1,limit=1}})); assert(o.ok); local attached=true; for _,p in pairs(game.connected_players) do if p.character~=c then attached=false end end; rcon.print(helpers.table_to_json{tick=game.tick,character=tostring(c.unit_number),incarnation=o.data.scope.incarnation,position=c.position,health=c.health,turretRounds=rounds,actorRounds=o.data.agent.ammoRounds,enemyAlive=alive,players=#game.connected_players,pilotAttached=attached})
                """.Replace("TURRET_ID", Id(turret)).Replace("ENEMY_ID", Id(enemy)), ct);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
