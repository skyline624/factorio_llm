using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: a cleared grass area with one biter spawner and one small worm created explicitly 50 tiles east of
/// the actor, who carries turrets, magazines, light armor and a submachine gun. The actor first sees the nest from a
/// distance, so it enters the nest memory only through a normal local photograph; the controller must then destroy both
/// structures by turret creep, keep the actor alive and mine its surviving turrets back. Not a campaign.
/// </summary>
public sealed class NestClearingQualification(RuntimeSession session)
{
    private static readonly MapPosition Spawner = new(50, 0), Worm = new(57, 6);

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Nest clearing qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string path = Path.Combine(session.Directory, $"nest-clearing-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            {
                reason = "Cleared area, one spawner and one small worm created, supplied turrets, magazines, armor and gun. Nest clearing test."
            }), token);
            if (!mark.Ok) throw new InvalidDataException("Fixture marker rejected.");
            var setup = await CommandAsync(string.Create(CultureInfo.InvariantCulture, $$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={ {-48,-64},{144,64} } }) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,144 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y} } end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; local main=c.get_main_inventory(); main.clear(); c.get_inventory(defines.inventory.character_guns).clear(); c.get_inventory(defines.inventory.character_ammo).clear(); c.get_inventory(defines.inventory.character_armor).clear(); assert(c.get_inventory(defines.inventory.character_guns).insert{name='pistol',count=1}==1); assert(c.get_inventory(defines.inventory.character_ammo).insert{name='firearm-magazine',count=10}==10); for name,count in pairs{['gun-turret']=8,['firearm-magazine']=200,['light-armor']=1,['submachine-gun']=1} do assert(main.insert{name=name,count=count}==count) end; game.forces.enemy.set_evolution_factor(0,s); local spawner=s.create_entity{name='biter-spawner',position={ {{Spawner.X}},{{Spawner.Y}} },force='enemy'}; local worm=s.create_entity{name='small-worm-turret',position={ {{Worm.X}},{{Worm.Y}} },force='enemy'}; assert(spawner and worm); rcon.print(helpers.table_to_json{tick=game.tick,character=tostring(c.unit_number),spawner=tostring(spawner.unit_number),worm=tostring(worm.unit_number)})
                """));
            File.Delete(new FactoryRegistry(session.Directory).Path);
            File.Delete(new NestMemoryStore(session.Directory).Path);
            string spawner = setup.GetProperty("spawner").GetString()!, worm = setup.GetProperty("worm").GetString()!;
            evidence.Add(new { check = "explicit-nest-preparation", native = setup });

            // The nest enters memory only through an ordinary local photograph taken from the actor's own position.
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            await new SpatialClient(game).CaptureAsync(radius: 64, cancellationToken: token);
            var memory = await new NestMemoryStore(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            var nest = memory.Clusters().SingleOrDefault();
            evidence.Add(new { check = "nest-memory", memory.Structures });
            Require(nest is not null && nest.Spawners == 1 && nest.Worms == 1, "The local photograph did not remember the spawner and the worm.");

            var registry = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            var result = await new NestClearingController(game, journal, session.Directory).ExecuteAsync(catalog, registry, nest!, 0, token);
            var after = await CommandAsync($$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local function alive(id) for _,e in pairs(s.find_entities_filtered{force='enemy',area={ {40,-40},{110,40} } }) do if tostring(e.unit_number)==id then return true end end; return false end; local placed=s.count_entities_filtered{name='gun-turret',force=f}; rcon.print(helpers.table_to_json{tick=game.tick,alive=c~=nil and c.valid,health=c and c.health,spawner=alive({{Quote(spawner)}}),worm=alive({{Quote(worm)}}),turretsStanding=placed,turretsCarried=c and c.get_main_inventory().get_item_count('gun-turret'),magazines=c and c.get_main_inventory().get_item_count('firearm-magazine')})
                """);
            evidence.Add(new { check = "clearing", result, native = after });
            Require(result.Status == "cleared", $"The nest was not reported cleared: {result.Status}.");
            Require(!after.GetProperty("spawner").GetBoolean() && !after.GetProperty("worm").GetBoolean(), "A prepared enemy structure survived.");
            Require(after.GetProperty("alive").GetBoolean(), "The actor died during the clearing.");
            Require(after.GetProperty("turretsStanding").GetInt32() == 0, "Turrets were left standing on the cleared ground.");
            passed = true;
            return path;
        }
        catch (Exception error) { evidence.Add(new { check = "failure", error = error.GetType().Name, error.Message }); throw; }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-nest-clearing-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }

        async Task<JsonElement> CommandAsync(string command)
        {
            string response = await native.ExecuteAsync(command, token);
            if (!response.TrimStart().StartsWith('{')) throw new InvalidDataException("Native nest fixture failed: " + response[..Math.Min(response.Length, 1500)]);
            using var value = JsonDocument.Parse(response);
            return value.RootElement.Clone();
        }
    }

    private static string Quote(string value) => "'" + value.Replace("'", "", StringComparison.Ordinal) + "'";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
