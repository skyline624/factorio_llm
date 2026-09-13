using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Explicit prepared integration test, including real collection, native consumption and an empty-cargo launch.</summary>
public sealed class RocketQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Rocket qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(session.Directory, $"rocket-qualification-{id}.json");
        string journalPath = Path.Combine(session.Directory, $"rocket-qualification-{id}.jsonl");
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared silo, power, research, 96 parts and 40 ingredients each in chests. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); for _,p in pairs(game.connected_players) do assert(p.character==c) end; game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-20,-20},{25,20}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-20,25 do for y=-20,20 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,-2})); c.get_main_inventory().clear(); f.research_all_technologies(); local silo=s.create_entity{name='rocket-silo',position={10.5,0.5},force=f}; assert(silo); local source=s.create_entity{name='electric-energy-interface',position={1,5},force=f}; assert(source); assert(s.create_entity{name='small-electric-pole',position={4.5,1.5},force=f}); assert(s.create_entity{name='small-electric-pole',position={1.5,3.5},force=f}); source.electric_buffer_size=1000000000; source.power_production=100000000; source.energy=1000000000; silo.rocket_parts=96; local made={}; for i,n in ipairs{'processing-unit','low-density-structure','rocket-fuel'} do local e=s.create_entity{name='steel-chest',position={-4.5,-5.5+i*3},force=f}; assert(e and e.insert{name=n,count=40}==40); made[#made+1]={id=tostring(e.unit_number),item=n,count=40,position=e.position} end; rcon.print(helpers.table_to_json{tick=game.tick,siloId=tostring(silo.unit_number),parts=silo.rocket_parts,chests=made,rocketsLaunched=f.rockets_launched,players=#game.connected_players,gameSpeed=game.speed})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "explicit-preparation", native = setup.RootElement.Clone() });
            var observation = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 32, limit = 200 }), token);
            Require(observation.Ok && observation.Data.GetProperty("goal").GetProperty("fixture").GetBoolean(), "Native fixture flag missing.");
            var before = RocketSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("rocket_state"), token));
            string siloId = setup.RootElement.GetProperty("siloId").GetString()!;
            Require(before.Silos.Single(s => s.Id == siloId).Parts == 96, "Prepared partial rocket changed before execution.");
            var result = await new RocketLaunchController(game, new ControllerJournal(journalPath)).RunAsync("rocket-silo", token);
            var after = RocketSnapshot.Parse(await game.ExecuteAsync(GameRequest.Create("rocket_state"), token));
            const string inventoryProof = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local totals={}; for _,n in ipairs{'processing-unit','low-density-structure','rocket-fuel'} do local total=c.get_item_count(n); for _,e in pairs(s.find_entities_filtered{name='steel-chest',force=f,area={{-20,-20},{25,20}}}) do total=total+e.get_item_count(n) end; totals[n]=total end; rcon.print(helpers.table_to_json{tick=game.tick,remaining=totals,rocketsLaunched=f.rockets_launched,players=#game.connected_players})
                """;
            using var stock = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(inventoryProof, token));
            evidence.Add(new { check = "native-launch-and-materials", before, result, after, stock = stock.RootElement.Clone() });
            Require(result.SiloId == siloId && after.RocketsLaunched == before.RocketsLaunched + 1
                && stock.RootElement.GetProperty("rocketsLaunched").GetInt64() == after.RocketsLaunched,
                "Exactly one additional launch was not independently confirmed.");
            Require(stock.RootElement.GetProperty("remaining").EnumerateObject().All(p => p.Value.GetInt64() == 0)
                && after.Silos.Single(s => s.Id == siloId).Inputs.Values.All(n => n == 0),
                "The prepared ingredients were not all consumed by the native rocket recipe.");
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-rocket-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
