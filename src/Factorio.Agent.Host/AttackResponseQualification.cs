using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: two small industry clusters 48 tiles apart, carried turrets, walls, magazines, light and heavy armor and a
/// submachine gun, the actor armed with the spawn pistol only. Small biters spawned explicitly attack the eastern cluster. The
/// controller must detect the attack (damaged entities, visible enemies and their bearing), wear the heavy armor and mount the
/// submachine gun, then build registered turret nests on the attacked cluster only; a second wave must die to those turrets
/// while the actor stands back at the other cluster without firing. Not a campaign.
/// </summary>
public sealed class AttackResponseQualification(RuntimeSession session)
{
    private const int FirstWave = 3, SecondWave = 4;
    private static readonly MapPosition West = new(-24, 5), East = new(24, 0);

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Attack response qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string path = Path.Combine(session.Directory, $"attack-response-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            {
                reason = "Two clusters, supplied turrets, walls, magazines, armor, gun; spawned biters. Attack response test."
            }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            var setup = await CommandAsync(string.Create(CultureInfo.InvariantCulture, $$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={ {-96,-64},{96,64} } }) do if e~=c then e.destroy() end end; local tiles={}; for x=-96,96 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y} } end end; s.set_tiles(tiles); c.health=c.max_health; local main=c.get_main_inventory(); main.clear(); c.get_inventory(defines.inventory.character_guns).clear(); c.get_inventory(defines.inventory.character_ammo).clear(); c.get_inventory(defines.inventory.character_armor).clear(); assert(c.get_inventory(defines.inventory.character_guns).insert{name='pistol',count=1}==1); assert(c.get_inventory(defines.inventory.character_ammo).insert{name='firearm-magazine',count=10}==10); for name,count in pairs{['gun-turret']=6,['stone-wall']=100,['firearm-magazine']=100,['light-armor']=1,['heavy-armor']=1,['submachine-gun']=1} do assert(main.insert{name=name,count=count}==count) end; local function site(x) local ids={}; for _,p in ipairs{ {x-2,-1},{x+2,-1} } do local e=s.create_entity{name='stone-furnace',position=p,force=f}; assert(e); ids[#ids+1]=tostring(e.unit_number) end; for _,p in ipairs{ {x-1.5,2.5},{x+1.5,2.5} } do local e=s.create_entity{name='iron-chest',position=p,force=f}; assert(e); ids[#ids+1]=tostring(e.unit_number) end; return ids end; local west,east=site({{West.X}}),site({{East.X}}); local function look(p) assert(c.teleport(p)); local o=helpers.json_to_table(remote.call('factorio_agent','execute',helpers.table_to_json{protocolVersion=1,requestId='attack-fixture-'..p[1],action='observe',arguments={radius=32,limit=1,entityLimit=1} })); assert(o.ok) end; look({ {{East.X}},5 }); look({ {{West.X}},{{West.Y}} }); rcon.print(helpers.table_to_json{tick=game.tick,character=tostring(c.unit_number),west=west,east=east})
                """));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            File.Delete(new AttackLog(session.Directory).Path);
            string[] west = setup.GetProperty("west").EnumerateArray().Select(e => e.GetString()!).ToArray();
            string[] east = setup.GetProperty("east").EnumerateArray().Select(e => e.GetString()!).ToArray();
            evidence.Add(new { check = "explicit-two-cluster-preparation", native = setup });

            // First wave: a pack attacks the eastern cluster while the actor works at the western one.
            var wave = await SpawnAsync(FirstWave, East.X + 20);
            JsonElement damage = default;
            for (int poll = 0; poll < 240; poll++)
            {
                await Task.Delay(250, token);
                damage = await CommandAsync($$"""
                    /silent-command local wanted={{Lua(east)}}; local hurt={}; for _,e in pairs(game.surfaces.nauvis.find_entities_filtered{force=game.forces.factorio_agent,area={ {0,-16},{48,16} } }) do if wanted[tostring(e.unit_number)] and e.health<e.max_health then hurt[#hurt+1]={id=tostring(e.unit_number),health=e.health,maxHealth=e.max_health} end end; rcon.print(helpers.table_to_json{tick=game.tick,damaged=hurt})
                    """);
                if (damage.GetProperty("damaged").ValueKind == JsonValueKind.Array && damage.GetProperty("damaged").GetArrayLength() > 0) break;
            }
            Require(damage.GetProperty("damaged").ValueKind == JsonValueKind.Array, "The first wave never damaged the eastern cluster.");
            var controller = new AttackResponseController(game, journal, session.Directory);
            var detected = await controller.DetectAsync(token);
            evidence.Add(new { check = "attack-detected", wave, damage, detected });
            var attack = detected.SingleOrDefault(r => r.Damaged + r.Destroyed > 0);
            Require(attack is not null && detected.Count == 1 && east.Contains(attack.Cluster["cluster-".Length..]) && attack.Enemies > 0
                && attack.Direction is "E" or "NE" or "SE", "The attack on the eastern cluster was not detected with its visible enemies and bearing.");
            // The fixture removes the pack: the response answers an attack that has ended.
            await CommandAsync($$"""
                /silent-command local wanted={{Lua(Ids(wave))}}; local removed=0; for _,b in pairs(game.surfaces.nauvis.find_entities_filtered{force='enemy',type='unit'}) do if wanted[tostring(b.unit_number)] then b.destroy(); removed=removed+1 end end; rcon.print(helpers.table_to_json{tick=game.tick,removed=removed})
                """);

            var response = await controller.RunAsync(token);
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var state = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            var turrets = state.Cells.Where(c => c.Kind == "turret" && c.Status == "ready").ToArray();
            var walls = state.Cells.Where(c => c.Kind == "wall" && c.Status == "ready").ToArray();
            var equipment = await CommandAsync("""
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; local armor=c.get_inventory(defines.inventory.character_armor)[1]; local gun=c.get_inventory(defines.inventory.character_guns)[c.selected_gun_index]; local ammo=c.get_inventory(defines.inventory.character_ammo)[c.selected_gun_index]; rcon.print(helpers.table_to_json{tick=game.tick,armor=armor.valid_for_read and armor.name or nil,gun=gun.valid_for_read and gun.name or nil,ammo=ammo.valid_for_read and ammo.name or nil,health=c.health})
                """);
            evidence.Add(new { check = "response", response, turretCells = turrets.Select(c => new { c.Id, c.Entities }), wallCells = walls.Length,
                walls = walls.Sum(c => c.Entities.Count), equipment });
            var answer = response.Responses.SingleOrDefault(r => r.Cluster == attack!.Cluster);
            Require(response.Status == "complete" && answer is { Outcome: "turrets-deployed", NestsBuilt: > 0 }, "No turret nest was deployed on the attacked cluster.");
            Require(turrets.Length == answer!.NestsBuilt && turrets.All(c => c.Plan!["turret"].Position.X > 0),
                "Turret nests were not registered on the attacked cluster only.");
            Require(walls.Sum(c => c.Entities.Count) > 0, "Stocked walls were not built behind the turrets.");
            Require(equipment.GetProperty("armor").GetString() == "heavy-armor" && equipment.GetProperty("gun").GetString() == "submachine-gun"
                && equipment.TryGetProperty("ammo", out var loaded) && loaded.GetString() == "firearm-magazine",
                "The actor does not wear the best carried armor with the loaded submachine gun.");

            // The actor walks back to the western cluster; the second wave must die to the new turrets alone.
            await using (var walker = new SpatialController(game, journal)) await walker.TravelAsync(West, 4, catalog, token);
            string turretIds = Lua(turrets.Select(c => c.Entities["turret"]));
            var before = await DefenseAsync(turretIds, "{}");
            var second = await SpawnAsync(SecondWave, East.X + 34);
            JsonElement after = before;
            for (int second0 = 0; second0 < 180; second0++)
            {
                await Task.Delay(1000, token);
                after = await DefenseAsync(turretIds, Lua(Ids(second)));
                if (after.GetProperty("biters").GetInt32() == 0) break;
            }
            long kills = after.GetProperty("turretKills").GetInt64() - before.GetProperty("turretKills").GetInt64();
            evidence.Add(new { check = "second-wave", second, before, after, turretKills = kills });
            Require(after.GetProperty("biters").GetInt32() == 0 && kills == SecondWave, "The new turrets did not kill the second wave.");
            Require(after.GetProperty("actorRounds").GetInt64() == before.GetProperty("actorRounds").GetInt64(), "The actor fired: kills cannot be credited to the turrets.");
            Require(west.All(id => after.GetProperty("alive").TryGetProperty(id, out _)), "The western cluster was harmed.");
            passed = true;
            return path;
        }
        catch (Exception error) { evidence.Add(new { check = "failure", error = error.Message }); throw; }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-attack-response-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }

        async Task<JsonElement> CommandAsync(string command)
        {
            string response = await native.ExecuteAsync(command, token);
            if (!response.TrimStart().StartsWith('{')) throw new InvalidDataException("Native attack fixture failed: " + response[..Math.Min(response.Length, 1500)]);
            using var value = JsonDocument.Parse(response);
            return value.RootElement.Clone();
        }

        // Small biters east of the eastern cluster, ordered onto it.
        Task<JsonElement> SpawnAsync(int count, double x) => CommandAsync(string.Create(CultureInfo.InvariantCulture, $$"""
            /silent-command local s=game.surfaces.nauvis; local ids={}; for i=1,{{count}} do local p=s.find_non_colliding_position('small-biter',{ {{x}}+(i%2)*2,(i-2)*2 },8,0.5); assert(p); local b=s.create_entity{name='small-biter',position=p,force='enemy'}; assert(b and b.commandable); b.commandable.set_command{type=defines.command.attack_area,destination={ {{East.X}},{{East.Y}} },radius=10,distraction=defines.distraction.by_enemy}; ids[#ids+1]=tostring(b.unit_number) end; rcon.print(helpers.table_to_json{tick=game.tick,biters=ids})
            """));

        // Independent native reading: turret kills, surviving spawned biters, actor rounds and the surviving western entities.
        Task<JsonElement> DefenseAsync(string turretIds, string biterIds) => CommandAsync($$"""
            /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local turrets={{turretIds}}; local biters={{biterIds}}; local function rounds(inv) local n=0; for i=1,#inv do local a=inv[i]; if a.valid_for_read and a.prototype.type=='ammo' then n=n+(a.count-1)*a.prototype.magazine_size+a.ammo end end; return n end; local kills,total,alive=0,0,0; for _,t in pairs(s.find_entities_filtered{type='ammo-turret',force=f}) do if turrets[tostring(t.unit_number)] then kills=kills+t.kills; total=total+rounds(t.get_inventory(defines.inventory.turret_ammo)) end end; for _,b in pairs(s.find_entities_filtered{name='small-biter',force='enemy'}) do if biters[tostring(b.unit_number)] then alive=alive+1 end end; local present={}; for _,e in pairs(s.find_entities_filtered{force=f,area={ {-48,-16},{0,16} } }) do present[tostring(e.unit_number)]=e.health end; rcon.print(helpers.table_to_json{tick=game.tick,turretKills=kills,rounds=total,biters=alive,alive=present,actorRounds=rounds(c.get_inventory(defines.inventory.character_ammo))+rounds(c.get_main_inventory())})
            """);
    }

    private static IEnumerable<string> Ids(JsonElement spawned) => spawned.GetProperty("biters").EnumerateArray().Select(b => b.GetString()!);
    private static string Quote(string value) => "'" + value.Replace("'", "", StringComparison.Ordinal) + "'";
    private static string Lua(IEnumerable<string> ids) => "{" + string.Join(",", ids.Select(id => $"[{Quote(id)}]=true")) + "}";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
