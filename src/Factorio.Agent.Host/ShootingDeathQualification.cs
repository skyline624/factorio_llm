using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Native death during shooting must preserve the distinction between fired and corpse-held rounds.</summary>
public sealed class ShootingDeathQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Shooting death qualification requires an explicit fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        var operations = new OperationClient(game);
        string path = Path.Combine(session.Directory, $"shooting-death-qualification-{Guid.NewGuid():N}.json");
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared partial magazines and inert target; native death during shooting. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            var setup = await CommandAsync("""
                /silent-command local s=game.surfaces.nauvis;local c=s.find_entities_filtered{type='character',force='factorio_agent'}[1];assert(c and c.crafting_queue_size==0);for _,p in pairs(game.connected_players) do assert(p.character==c) end;game.speed=1;for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end;for _,e in pairs(s.find_entities_filtered{type='character-corpse'}) do e.destroy() end;local tiles={};for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end;s.set_tiles(tiles);assert(c.teleport({0,0}));c.health=c.max_health;for i=1,c.get_max_inventory_index() do local inv=c.get_inventory(i);if inv then inv.clear() end end;local main=c.get_main_inventory();main[1].set_stack{name='firearm-magazine',count=2};main[1].ammo=6;assert(main.insert{name='iron-plate',count=17}==17);local ammo=c.get_inventory(defines.inventory.character_ammo);ammo[1].set_stack{name='firearm-magazine',count=2};ammo[1].ammo=4;c.get_inventory(defines.inventory.character_guns)[1].set_stack{name='pistol',count=1};c.selected_gun_index=1;local b=s.create_entity{name='behemoth-biter',position={8,0},force=game.forces.enemy};assert(b);b.active=false;rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,target=tostring(b.unit_number),players=#game.connected_players})
                """);
            var observation = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe"), token));
            var shot = OperationSubmission.Create(observation.Scope, "shoot",
                new { entityId = setup.GetProperty("target").GetString(), ticks = 3600 }, observation.Tick + 4000);
            Require(!(await operations.SubmitAsync(shot, token)).IsTerminal, "Shooting did not start.");
            // The kill and before/after inventory readings occur in one native command.
            const string kill = """
                /silent-command local s=game.surfaces.nauvis;local c=s.find_entities_filtered{type='character',force='factorio_agent'}[1];assert(c);local function rounds(inv)local n=0;for i=1,#inv do local v=inv[i];if v.valid_for_read and v.prototype.type=='ammo' then n=n+(v.count-1)*v.prototype.magazine_size+v.ammo end end;return n end;local held=0;for i=1,c.get_max_inventory_index() do local inv=c.get_inventory(i);if inv then held=held+rounds(inv) end end;if held==30 then rcon.print(helpers.table_to_json{waiting=true,tick=game.tick});return end;assert(held>16 and held<30);local unit=c.unit_number;local pos=c.position;c.die(game.forces.enemy);local bodies=s.find_entities_filtered{type='character-corpse'};assert(#bodies==1);local inv=bodies[1].get_inventory(defines.inventory.character_corpse);rcon.print(helpers.table_to_json{waiting=false,tick=game.tick,character=unit,beforeDeathRounds=held,corpseRounds=rounds(inv),corpseIron=inv.get_item_count('iron-plate'),players=#game.connected_players})
                """;
            JsonElement death = default;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                death = await CommandAsync(kill);
                if (!death.GetProperty("waiting").GetBoolean()) break;
                await Task.Delay(100, token);
            }
            Require(!death.GetProperty("waiting").GetBoolean(), "No fired round was observed before the deadline.");
            var receipt = await operations.QueryAsync(shot.OperationId, token);
            evidence.Add(new { setup, initialRounds = 30, submission = shot, death, receipt = receipt.Evidence });
            long fired = 30 - death.GetProperty("corpseRounds").GetInt64();
            Require(receipt.Status == "failed" && receipt.Error?.Code == "actor_dead", "Native death did not terminate the shot.");
            Require(death.GetProperty("beforeDeathRounds").GetInt64() == death.GetProperty("corpseRounds").GetInt64()
                && death.GetProperty("corpseIron").GetInt64() == 17 && fired is > 0 and < 14,
                "Native corpse transfer did not preserve the surviving partial ammunition and iron.");
            Require(receipt.Effects.TryGetProperty("roundsConsumed", out var consumed) && consumed.GetInt64() == fired,
                "The shooting receipt counts corpse-held ammunition as fired rounds.");
            Require(receipt.Effects.GetProperty("ammoAccounting").GetProperty("status").GetString() == "reconciled-native-corpse",
                "The death receipt lacks explicit corpse accounting provenance.");
            var duplicate = await operations.SubmitAsync(shot, token);
            Require(duplicate.Evidence.GetRawText() == receipt.Evidence.GetRawText(), "Duplicate submission changed the sealed death receipt.");
            evidence.Add(new { check = "native-round-conservation-and-deduplication", fired, surviving = 30 - fired });
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-shooting-death-qualification", passed,
                isAutonomousCampaign = false, evidence }, CancellationToken.None);
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
