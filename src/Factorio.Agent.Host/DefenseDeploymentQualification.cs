using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed class DefenseDeploymentQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Defense deployment qualification requires an explicit fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string path = Path.Combine(session.Directory, $"defense-deployment-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared armed actor, industry, empty turret, inputs and partial magazines; native defense deployment. Not a campaign." }), token);
            if (!marked.Ok) throw new GameRpcException(marked.Error!);
            var setup = await CommandAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{64,64}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-64,64 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,6})); c.health=250; c.get_main_inventory().clear(); c.get_inventory(defines.inventory.character_guns).clear(); c.get_inventory(defines.inventory.character_guns)[1].set_stack{name='pistol',count=1}; c.get_inventory(defines.inventory.character_ammo).clear(); f.recipes['gun-turret'].enabled=true; f.recipes['firearm-magazine'].enabled=true; local main=c.get_main_inventory(); assert(main.insert{name='iron-plate',count=200}==200); assert(main.insert{name='copper-plate',count=10}==10); assert(main.insert{name='iron-gear-wheel',count=10}==10); assert(main.insert{name='firearm-magazine',count=3}==3); main.find_item_stack('firearm-magazine').ammo=4; assert(s.create_entity{name='stone-furnace',position={-5,0},force=f}); assert(s.create_entity{name='stone-furnace',position={30,0},force=f}); local turret=s.create_entity{name='gun-turret',position={-8,0},force=f}; assert(turret); rcon.print(helpers.table_to_json{tick=game.tick,existingTurret=tostring(turret.unit_number)})
                """);
            var before = await ReadAsync();
            evidence.Add(new { check = "explicit-preparation", setup, before });
            Require(before.GetProperty("totalRounds").GetInt64() == 24, "Partial magazines were not prepared.");
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var controller = new DefenseDeploymentController(game, journal);
            var result = await controller.RunAsync("gun-turret", 2, token);
            var after = await ReadAsync();
            evidence.Add(new { check = "native-deployment", result, after });
            Require(result.Built == 1 && result.Serviced == 2 && result.ReadyCount == 2 && result.CoveredAnchors == 2 && result.UncoveredAnchors == 0,
                "The existing turret was not reused or both observed industrial anchors were not covered.");
            Require(result.ReadyIds.Contains(setup.GetProperty("existingTurret").GetString()!), "The prepared empty turret was replaced.");
            var stock = await new ProductionController(game, journal).ObserveAsync(token);
            Require(stock.Entities.Where(e => result.ReadyIds.Contains(e.Id)).All(e => !e.Inventories.TryGetProperty("output", out _)
                && e.Count("ammo", "firearm-magazine") >= 10), "Native defense inventories are still exported as production outputs.");
            var turrets = after.GetProperty("turrets").EnumerateArray().ToArray();
            Require(turrets.Length == 2 && turrets.All(t => t.GetProperty("rounds").GetInt64() >= 100 && t.GetProperty("active").GetBoolean()),
                "Independent native turrets lack the requested reserves.");
            long madeRounds = after.GetProperty("totalRounds").GetInt64() - 24;
            int magazineSize = catalog.Items["firearm-magazine"].MagazineSize!.Value;
            Require(madeRounds >= 0 && madeRounds % magazineSize == 0, "Partial ammunition was lost or refilled during transfer.");
            var turretRecipe = catalog.Recipes.Single(r => r.Name == "gun-turret");
            var ammoRecipe = catalog.Recipes.Single(r => r.Name == "firearm-magazine");
            foreach (var input in new[] { "iron-plate", "copper-plate", "iron-gear-wheel" })
            {
                double expected = turretRecipe.Ingredients.Where(m => m.Name == input).Sum(m => m.Amount!.Value)
                    + madeRounds / magazineSize * ammoRecipe.Ingredients.Where(m => m.Name == input).Sum(m => m.Amount!.Value);
                long consumed = before.GetProperty("items").GetProperty(input).GetInt64() - after.GetProperty("items").GetProperty(input).GetInt64();
                Require(consumed == expected, "Native recipe inputs do not account for installed turret and ammunition production.");
            }
            Require(after.GetProperty("carriedTurrets").GetInt32() == 0, "A carried turret was mistaken for a deployed defense.");
            Require(before.GetProperty("character").GetString() == after.GetProperty("character").GetString()
                && after.GetProperty("pilotAttached").GetBoolean(), "The pilot and agent no longer share the prepared avatar.");
            var repeated = await controller.RunAsync("gun-turret", 2, token);
            var unchanged = await ReadAsync();
            evidence.Add(new { check = "already-installed-goal", repeated, unchanged });
            Require(repeated.Built == 0 && repeated.Serviced == 0 && unchanged.GetProperty("totalRounds").GetInt64() == after.GetProperty("totalRounds").GetInt64(),
                "A satisfied deployment consumed or manufactured additional ammunition.");
            passed = true;
            return path;
        }
        catch (Exception error) { evidence.Add(new { check = "failure", error = error.Message }); throw; }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-defense-deployment-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement> CommandAsync(string command)
        {
            string response = await native.ExecuteAsync(command, token);
            if (!response.TrimStart().StartsWith('{')) throw new InvalidDataException("Native defense fixture failed: " + response[..Math.Min(response.Length, 1500)]);
            using var value = JsonDocument.Parse(response);
            return value.RootElement.Clone();
        }
        Task<JsonElement> ReadAsync() => CommandAsync("""
            /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c); local function rounds(inv) local n=0; for i=1,#inv do local a=inv[i]; if a.valid_for_read and a.prototype.type=='ammo' then n=n+(a.count-1)*a.prototype.magazine_size+a.ammo end end; return n end; local main=c.get_main_inventory(); local total=rounds(main)+rounds(c.get_inventory(defines.inventory.character_ammo)); local turrets={}; for _,t in pairs(s.find_entities_filtered{type='ammo-turret',force=f,area={{-64,-64},{64,64}}}) do local n=rounds(t.get_inventory(defines.inventory.turret_ammo)); total=total+n; turrets[#turrets+1]={id=tostring(t.unit_number),position=t.position,rounds=n,active=t.active} end; local attached=true; for _,p in pairs(game.connected_players) do if p.character~=c then attached=false end end; local items={}; for _,name in ipairs({'iron-plate','copper-plate','iron-gear-wheel'}) do items[name]=main.get_item_count(name) end; rcon.print(helpers.table_to_json{tick=game.tick,character=tostring(c.unit_number),items=items,carriedTurrets=main.get_item_count('gun-turret'),totalRounds=total,turrets=turrets,players=#game.connected_players,pilotAttached=attached})
            """);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
