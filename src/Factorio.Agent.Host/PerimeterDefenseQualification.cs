using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: injected power source, early technologies and supplied construction items, turrets, walls and
/// magazines. Proves a native perimeter around a real factory cell, turret kills against spawned small biters commanded
/// to attack the factory, measured ammunition use, and the rebuild of a deliberately destroyed turret and wall by the
/// logistics round. The actor carries no weapon, so every kill belongs to a perimeter turret. Not a campaign.
/// </summary>
public sealed class PerimeterDefenseQualification(RuntimeSession session)
{
    private const int Biters = 6;

    public async Task<string> RunAsync(int layers, CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Perimeter qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string path = Path.Combine(session.Directory, $"perimeter-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            {
                reason = "Injected power, research, construction items, turrets, walls, ammo; spawned biters. Perimeter test, not a campaign."
            }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            var setup = await CommandAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{64,64}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-64,64 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); c.get_inventory(defines.inventory.character_guns).clear(); c.get_inventory(defines.inventory.character_ammo).clear(); for _,t in pairs{'steam-power','electronics','automation'} do f.technologies[t].researched=true end; for name,count in pairs{['assembling-machine-1']=2,inserter=4,['iron-chest']=4,['small-electric-pole']=16,['iron-plate']=400,['gun-turret']=12,['stone-wall']=200,['firearm-magazine']=200} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=2000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number})
                """);
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            evidence.Add(new { check = "explicit-perimeter-preparation", native = setup });

            var gears = await new FactoryCellBuilder(game, journal, session.Directory).BuildAsync("assembler", "assembling-machine-1", "iron-gear-wheel", token);
            Require(gears.Status == "ready", "The protected factory cell was not built.");
            var result = await new PerimeterDefenseController(game, journal, session.Directory).RunAsync("stone-wall", "gun-turret", layers, token);
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var state = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            var turrets = state.Cells.Where(c => c.Kind == "turret" && c.Status == "ready").ToArray();
            var walls = state.Cells.Where(c => c.Kind == "wall" && c.Status == "ready").ToArray();
            int plannedWalls = walls.Sum(c => c.Plan?.Count ?? 0);
            evidence.Add(new { check = "perimeter-built", gears = gears.Id, result, turretCells = turrets.Length, wallCells = walls.Length, plannedWalls });
            Require(result.CanLeave && result.CanEnter && result.Nests >= 4 && result.CoverageGaps == 0 && result.Spacing <= catalog.Turrets!["gun-turret"].Range,
                "The plan lacks a proven exit, overlapping coverage or enough nests.");
            Require(result.TurretsReady == result.Nests && turrets.Length == result.Nests && result.Refused == 0,
                "Not every planned turret was built, registered and loaded to the reserve.");
            Require(result.Walls == plannedWalls && plannedWalls > 0 && walls.All(c => c.Entities.Count == c.Plan!.Count),
                "Not every planned wall was built and registered.");

            // The model may propose the perimeter again: the complete ring must be recognized and left untouched.
            var repeat = await new PerimeterDefenseController(game, journal, session.Directory).RunAsync("stone-wall", "gun-turret", layers, token);
            var repeated = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            evidence.Add(new { check = "perimeter-repeat", repeat, before = Defenses(state), after = Defenses(repeated) });
            Require(repeat.AlreadyComplete && repeat.Built == 0 && repeat.Refused == 0 && repeat.Nests == result.Nests && Defenses(repeated) == Defenses(state),
                "Repeating the perimeter changed or duplicated the complete ring.");

            // Attack: small biters appear beyond turret range north of the ring and are ordered onto the factory.
            var turretPositions = turrets.Select(c => c.Plan!["turret"].Position).ToArray();
            var factory = state.Zones.Single().Box;
            double top = turretPositions.Min(p => p.Y), range = catalog.Turrets!["gun-turret"].Range;
            var spawn = new MapPosition(Math.Round(turretPositions.Average(p => p.X)), Math.Max(-60, Math.Round(top - range - 6)));
            var target = new MapPosition((factory.Min.X + factory.Max.X) / 2, (factory.Min.Y + factory.Max.Y) / 2);
            string turretIds = Lua(turrets.Select(c => c.Entities["turret"]));
            var before = await DefenseAsync(turretIds, "{}");
            var attack = await CommandAsync(string.Create(CultureInfo.InvariantCulture, $$"""
                /silent-command local s=game.surfaces.nauvis; local ids={}; for i=1,{{Biters}} do local p=s.find_non_colliding_position('small-biter',{ {{spawn.X}}+(i%3)*2,{{spawn.Y}}-math.floor(i/3)*2 },8,0.5); assert(p); local b=s.create_entity{name='small-biter',position=p,force='enemy'}; assert(b and b.commandable); b.commandable.set_command{type=defines.command.attack_area,destination={ {{target.X}},{{target.Y}} },radius=12,distraction=defines.distraction.by_enemy}; ids[#ids+1]=tostring(b.unit_number) end; rcon.print(helpers.table_to_json{tick=game.tick,biters=ids})
                """));
            string biterIds = Lua(attack.GetProperty("biters").EnumerateArray().Select(b => b.GetString()!));
            JsonElement after = before;
            for (int second = 0; second < 180; second++)
            {
                await Task.Delay(1000, token);
                after = await DefenseAsync(turretIds, biterIds);
                if (after.GetProperty("biters").GetInt32() == 0) break;
            }
            long kills = after.GetProperty("turretKills").GetInt64() - before.GetProperty("turretKills").GetInt64();
            long consumed = before.GetProperty("rounds").GetInt64() - after.GetProperty("rounds").GetInt64();
            long forceKills = after.GetProperty("forceKills").GetInt64() - before.GetProperty("forceKills").GetInt64();
            evidence.Add(new { check = "attack-repelled", spawn, target, attack, before, after, turretKills = kills, roundsConsumed = consumed, forceKills });
            Require(after.GetProperty("biters").GetInt32() == 0, "Spawned biters survived the perimeter.");
            Require(kills == Biters && forceKills == Biters, "The perimeter turrets did not account for every spawned biter.");
            Require(consumed > 0 && after.GetProperty("actorRounds").GetInt64() == 0, "Turret ammunition use was not measured or the actor could have fired.");

            // A registry written before plans were recorded: the gear cell loses its plan, and one logistics round
            // must recover exactly the plan the builder had recorded from the native entities.
            var registry = new FactoryRegistry(session.Directory);
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            var original = state.Cells.Single(c => c.Id == gears.Id);
            await registry.SaveAsync(state.With(original with { Plan = null }), token);
            var recovery = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(40, token);
            var recovered = (await registry.LoadAsync(catalog.Scope.WorldId, token)).Cells.Single(c => c.Id == gears.Id);
            evidence.Add(new { check = "legacy-plan-recovered", recovery.Maintenance?.RecoveredPlans, original = original.Plan, recovered = recovered.Plan });
            Require(recovery.Maintenance?.RecoveredPlans.Contains(gears.Id) == true && recovered.Plan is { } plan && plan.Count == original.Plan!.Count
                && original.Plan.All(p => plan.TryGetValue(p.Key, out var value) && value == p.Value), "The legacy cell plan was not recovered exactly.");

            // Damage: destroy one turret, one wall and the gear cell pole natively, then let one logistics round rebuild them,
            // rearm the turret and prove the rebuilt pole is back on the generator network.
            var victimTurret = turrets.OrderBy(c => c.Id, StringComparer.Ordinal).First();
            var victimWall = walls.OrderBy(c => c.Id, StringComparer.Ordinal).First();
            var wallRole = victimWall.Entities.Keys.Order(StringComparer.Ordinal).First();
            string victimPole = recovered.Entities["pole"];
            var destroyed = await CommandAsync($$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local wanted={[{{Quote(victimTurret.Entities["turret"])}}]=true,[{{Quote(victimWall.Entities[wallRole])}}]=true,[{{Quote(victimPole)}}]=true}; local killed={}; for _,e in pairs(s.find_entities_filtered{force=f,type={'ammo-turret','wall','electric-pole'},area={ {-96,-96},{96,96} } }) do if wanted[tostring(e.unit_number)] then killed[#killed+1]={id=tostring(e.unit_number),name=e.name,position=e.position}; assert(e.die('enemy')) end end; rcon.print(helpers.table_to_json{tick=game.tick,killed=killed})
                """);
            Require(destroyed.GetProperty("killed").GetArrayLength() == 3, "The fixture could not destroy the chosen turret, wall and pole.");
            var service = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(40, token);
            state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            string newTurret = state.Cells.Single(c => c.Id == victimTurret.Id).Entities["turret"];
            string newWall = state.Cells.Single(c => c.Id == victimWall.Id).Entities[wallRole];
            string newPole = state.Cells.Single(c => c.Id == gears.Id).Entities["pole"];
            var rebuilt = await CommandAsync($$"""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local wanted={[{{Quote(newTurret)}}]=true,[{{Quote(newWall)}}]=true,[{{Quote(newPole)}}]=true}; local found={}; local function rounds(inv) local n=0; for i=1,#inv do local a=inv[i]; if a.valid_for_read and a.prototype.type=='ammo' then n=n+(a.count-1)*a.prototype.magazine_size+a.ammo end end; return n end; for _,e in pairs(s.find_entities_filtered{force=f,type={'ammo-turret','wall','electric-pole'},area={ {-96,-96},{96,96} } }) do if wanted[tostring(e.unit_number)] then found[#found+1]={id=tostring(e.unit_number),name=e.name,position=e.position,rounds=e.type=='ammo-turret' and rounds(e.get_inventory(defines.inventory.turret_ammo)) or nil,network=e.type=='electric-pole' and e.electric_network_id or nil} end end; local source=s.find_entities_filtered{name='electric-energy-interface',force=f}[1]; rcon.print(helpers.table_to_json{tick=game.tick,found=found,sourceNetwork=source and source.electric_network_id})
                """);
            evidence.Add(new { check = "rebuilt-after-attack", destroyed, service.Maintenance, service.Degraded, rebuilt });
            var found = rebuilt.GetProperty("found").EnumerateArray().ToArray();
            Require(newTurret != victimTurret.Entities["turret"] && newWall != victimWall.Entities[wallRole] && newPole != victimPole && found.Length == 3,
                "The destroyed turret, wall and pole were not rebuilt and re-registered.");
            foreach (var entity in found)
            {
                string id = entity.GetProperty("id").GetString()!;
                var recorded = id == newTurret ? victimTurret.Plan!["turret"] : id == newWall ? victimWall.Plan![wallRole] : recovered.Plan!["pole"];
                Require(entity.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!.DistanceTo(recorded.Position) < .01,
                    "A rebuilt entity is not at its recorded position.");
            }
            // The native reading comes after the rest of the logistics round; a loaded turret may already have fired,
            // so the reserve itself is proven by the native insert receipts below.
            Require(found.Single(e => e.GetProperty("id").GetString() == newTurret).GetProperty("rounds").GetInt64() > 0, "The rebuilt turret holds no ammunition.");
            Require(found.Single(e => e.GetProperty("id").GetString() == newPole).GetProperty("network").GetInt64() == rebuilt.GetProperty("sourceNetwork").GetInt64()
                && service.Maintenance?.Unpowered.Count == 0, "The rebuilt pole is not on the generator network, or maintenance reported a power fault.");

            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                string Type(JsonDocument row) => row.RootElement.GetProperty("type").GetString()!;
                var kinds = rows.Where(r => Type(r) == "submission").Select(r => r.RootElement.GetProperty("data").GetProperty("kind").GetString()).ToArray();
                int rebuilds = rows.Count(r => Type(r) == "factory-rebuild");
                long rearmed = rows.Where(r => Type(r) == "receipt").Select(r => r.RootElement.GetProperty("data"))
                    .Where(d => d.GetProperty("kind").GetString() == "insert" && d.GetProperty("status").GetString() is "completed" or "partial"
                        && d.GetProperty("effects").GetProperty("targetId").GetString() == newTurret && d.GetProperty("effects").GetProperty("inventory").GetString() == "ammo"
                        && d.GetProperty("effects").GetProperty("item").GetString() == "firearm-magazine" && d.GetProperty("effects").GetProperty("direction").GetString() == "from_actor")
                    .Sum(d => d.GetProperty("effects").GetProperty("transferred").GetInt64()) * catalog.Items["firearm-magazine"].MagazineSize!.Value;
                evidence.Add(new { check = "journal", rebuilds, rearmedRounds = rearmed, crafts = kinds.Count(k => k == "craft"), mines = kinds.Count(k => k == "mine"), builds = kinds.Count(k => k == "build") });
                Require(rebuilds == 3 && !kinds.Contains("craft") && !kinds.Contains("mine"), "Rebuilds were not journaled or supplied items were hand-made.");
                Require(rearmed >= DefenseDeploymentPlanner.ReserveRounds, "The rebuilt turret was not rearmed to the reserve by native transfers.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;
        }
        catch (Exception error) { evidence.Add(new { check = "failure", error = error.Message }); throw; }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-perimeter-defense-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }

        async Task<JsonElement> CommandAsync(string command)
        {
            string response = await native.ExecuteAsync(command, token);
            if (!response.TrimStart().StartsWith('{')) throw new InvalidDataException("Native perimeter fixture failed: " + response[..Math.Min(response.Length, 1500)]);
            using var value = JsonDocument.Parse(response);
            return value.RootElement.Clone();
        }

        // Independent native reading: turret kills and rounds, surviving spawned biters, force kill statistics, actor ammunition.
        Task<JsonElement> DefenseAsync(string turretIds, string biterIds) => CommandAsync($$"""
            /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; local turrets={{turretIds}}; local biters={{biterIds}}; local function rounds(inv) local n=0; for i=1,#inv do local a=inv[i]; if a.valid_for_read and a.prototype.type=='ammo' then n=n+(a.count-1)*a.prototype.magazine_size+a.ammo end end; return n end; local kills,total,alive=0,0,0; for _,t in pairs(s.find_entities_filtered{type='ammo-turret',force=f,area={ {-96,-96},{96,96} } }) do if turrets[tostring(t.unit_number)] then kills=kills+t.kills; total=total+rounds(t.get_inventory(defines.inventory.turret_ammo)) end end; for _,b in pairs(s.find_entities_filtered{name='small-biter',force='enemy',area={ {-160,-160},{160,160} } }) do if biters[tostring(b.unit_number)] then alive=alive+1 end end; local stats=f.get_kill_count_statistics(s); rcon.print(helpers.table_to_json{tick=game.tick,turretKills=kills,rounds=total,biters=alive,forceKills=stats.input_counts['small-biter'] or 0,actorHealth=c and c.health,actorRounds=c and rounds(c.get_inventory(defines.inventory.character_ammo)) or 0})
            """);
    }

    /// <summary>Registered defense cells and their native ids, to prove a repeated run changed nothing.</summary>
    private static string Defenses(FactoryState state) => string.Join(";", state.Cells.Where(c => c.Kind is "turret" or "wall")
        .OrderBy(c => c.Id, StringComparer.Ordinal).Select(c => c.Id + ":" + c.Status + ":" + string.Join(",", c.Entities
            .OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => e.Key + "=" + e.Value))));

    private static string Quote(string value) => "'" + value.Replace("'", "", StringComparison.Ordinal) + "'";
    private static string Lua(IEnumerable<string> ids) => "{" + string.Join(",", ids.Select(id => $"[{Quote(id)}]=true")) + "}";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
