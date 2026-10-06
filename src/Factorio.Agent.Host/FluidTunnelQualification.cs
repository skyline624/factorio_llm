using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: a cleared grass area, a storage tank filled with water explicitly, and a band of transport belts across
/// the whole area: the character walks over belts but no ordinary pipe can cross them. The actor carries pipes and
/// pipe-to-ground, with the steam-power technology (which unlocks pipe-to-ground) marked researched. The relay must reach a destination 150 tiles away by native underground hops, and the native graph and
/// the outlet's stock must prove the water arrived. Not a campaign.
/// </summary>
public sealed class FluidTunnelQualification(RuntimeSession session)
{
    private static readonly MapPosition Source = new(0, 0), Destination = new(150, 0);
    private const int BeltX = 60;

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Fluid tunnel qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string path = Path.Combine(session.Directory, $"fluid-tunnel-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            {
                reason = "Cleared area, a water-filled tank, a belt band across the area, supplied pipes and pipe-to-ground. Underground relay test."
            }), token);
            if (!mark.Ok) throw new InvalidDataException("Fixture marker rejected.");
            var setup = await CommandAsync(string.Create(CultureInfo.InvariantCulture, $$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={ {-48,-64},{200,64} } }) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,200 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y} } end end; s.set_tiles(tiles); assert(c.teleport({-4,4})); f.technologies['steam-power'].researched=true; c.health=c.max_health; local main=c.get_main_inventory(); main.clear(); for name,count in pairs{['pipe']=100,['pipe-to-ground']=60} do assert(main.insert{name=name,count=count}==count) end; local tank=s.create_entity{name='storage-tank',position={ {{Source.X}},{{Source.Y}} },force=f}; assert(tank and tank.insert_fluid{name='water',amount=20000}>0); local belts=0; for x={{BeltX}},{{BeltX + 2}} do for y=-64,63 do local b=s.create_entity{name='transport-belt',position={x+0.5,y+0.5},direction=defines.direction.north,force=f}; if b then belts=belts+1 end end end; rcon.print(helpers.table_to_json{tick=game.tick,character=tostring(c.unit_number),tank=tostring(tank.unit_number),belts=belts})
                """));
            File.Delete(new FactoryRegistry(session.Directory).Path);
            evidence.Add(new { check = "explicit-tunnel-preparation", native = setup });
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            if (FluidRelayController.UndergroundItem(catalog) is null) throw new InvalidDataException("The native pipe-to-ground recipe is not enabled.");
            var registry = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            await using (var walker = new SpatialController(game, journal))
                await new FluidRelayController(game, journal, session.Directory).ExtendAsync("water", Destination, catalog, walker,
                    new FactoryGround(registry, null), token);
            var state = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            var relays = state.Cells.Where(c => c.Kind == FluidRelayController.Kind).ToArray();
            var tunnels = relays.SelectMany(c => c.Plan!.Values).Count(p => p.Item == "pipe-to-ground");
            var after = await CommandAsync(string.Create(CultureInfo.InvariantCulture, $$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local best,water=nil,0; for _,e in pairs(s.find_entities_filtered{force=f,type={'pipe','pipe-to-ground'},area={ { {{Destination.X - 40}},-40},{ {{Destination.X + 10}},40} } }) do local w=e.get_fluid_count('water'); if not best or e.position.x>best then best=e.position.x; water=w end end; local belts=s.count_entities_filtered{name='transport-belt',force=f}; rcon.print(helpers.table_to_json{tick=game.tick,alive=c~=nil and c.valid,farthestX=best,waterThere=water,belts=belts,tunnels=s.count_entities_filtered{name='pipe-to-ground',force=f},pipes=s.count_entities_filtered{name='pipe',force=f} })
                """));
            evidence.Add(new { check = "relay", sections = relays.Select(c => new { c.Id, c.Status, pieces = c.Plan!.Count }), tunnels, native = after });
            Require(relays.Length > 0 && relays.All(c => c.Status == "ready"), "Not every relay section is ready.");
            Require(tunnels > 0, "No underground hop was planned across the belt band.");
            Require(after.GetProperty("farthestX").GetDouble() >= Destination.X - FluidRelayPlanner.Step, "The relay did not reach the destination area.");
            Require(after.GetProperty("waterThere").GetDouble() > 0, "No native water stood at the relay's far end.");
            Require(after.GetProperty("belts").GetInt32() == setup.GetProperty("belts").GetInt32(), "The belt band was disturbed.");
            Require(after.GetProperty("alive").GetBoolean(), "The actor died.");
            passed = true;
            return path;
        }
        catch (Exception error) { evidence.Add(new { check = "failure", error = error.GetType().Name, error.Message }); throw; }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-fluid-tunnel-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }

        async Task<JsonElement> CommandAsync(string command)
        {
            string response = await native.ExecuteAsync(command, token);
            if (!response.TrimStart().StartsWith('{')) throw new InvalidDataException("Native tunnel fixture failed: " + response[..Math.Min(response.Length, 1500)]);
            using var value = JsonDocument.Parse(response);
            return value.RootElement.Clone();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
