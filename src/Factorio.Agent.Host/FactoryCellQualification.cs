using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: injected power/research, trees and either construction items or stored materials.
/// Proves native band construction, clearance, recipe configuration and chest logistics; not a campaign.
/// </summary>
public sealed class FactoryCellQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token, bool fromMaterials = false)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Factory cell qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"factory-cell-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = fromMaterials
                ? "Injected power, researched recipes, material storage chests and trees; no construction kit supplied. Not a campaign."
                : "Injected energy interface, construction items, 400 iron plates and trees. Factory band test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); f.technologies['steam-power'].researched=true; f.technologies.electronics.researched=true; f.technologies.automation.researched=true; __SUPPLIES__ local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=2000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); for i=0,5 do assert(s.create_entity{name='tree-01',position={-12+i*2.5,6.5+(i%2)}}) end; local p=game.connected_players[1]; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,characters=s.count_entities_filtered{type='character'},players=#game.connected_players,pilotCharacter=p and p.character and p.character.unit_number or 0})
                """;
            string supplies = fromMaterials
                ? "assert(c.insert{name='pistol',count=1}==1); assert(c.insert{name='firearm-magazine',count=10}==10); local iron=s.create_entity{name='iron-chest',position={-20.5,20.5},force=f}; local other=s.create_entity{name='iron-chest',position={20.5,20.5},force=f}; assert(iron and other); assert(iron.insert{name='iron-plate',count=400}==400); assert(other.insert{name='copper-plate',count=100}==100); assert(other.insert{name='wood',count=50}==50);"
                : "for name,count in pairs{['assembling-machine-1']=2,inserter=4,['iron-chest']=4,['small-electric-pole']=12,lab=1,['iron-plate']=400} do assert(c.insert{name=name,count=count}==count) end;";
            string preparation = prepare.Replace("__SUPPLIES__", supplies, StringComparison.Ordinal);
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(preparation, token));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            evidence.Add(new { check = "explicit-factory-preparation", fromMaterials, native = setup.RootElement.Clone() });
            RequireSameActor(setup.RootElement);

            var builder = new FactoryCellBuilder(game, journal, session.Directory);
            var gears = await builder.BuildAsync("assembler", "assembling-machine-1", "iron-gear-wheel", token);
            var lab = await builder.BuildAsync("lab", "lab", null, token);
            evidence.Add(new { check = "cells-ready", gears, lab });
            // Link poles are recorded as link-n roles so maintenance can rebuild them; they are not cell parts.
            static int Parts(FactoryCell cell) => cell.Entities.Keys.Count(role => !role.StartsWith("link-", StringComparison.Ordinal));
            Require(gears.Status == "ready" && Parts(gears) == 6 && Parts(lab) == 2, "Cells were not completed with their native entities.");

            if (fromMaterials)
            {
                // These are real transfers from the fixture's storage, not an injected stock for the logistics check.
                using (ProductionReservations.EnterFactory(await new FactoryRegistry(session.Directory).LoadAsync(session.ProposedWorldId, token)))
                    await new ProductionGoalExecutor(game, journal).RunAsync("iron-plate", 100, token);
            }

            var logistics = new FactoryLogistics(game, journal, session.Directory);
            var first = await logistics.ServiceAsync(50, token);
            Require(first.Supplied.GetValueOrDefault("iron-plate") == 100, "The input chest was not stocked for 50 gear crafts.");
            await using (var controller = new SpatialController(game, journal))
                Require((await controller.WorkAsync("wait", new { ticks = 2400 }, 2700, token: token)).Status == "completed", "Wait failed.");
            var second = await logistics.ServiceAsync(50, token);
            evidence.Add(new { check = "chest-logistics", first, second });
            // One basic inserter moves about 0.83 plates per second, so 40 seconds yield roughly 16 gears.
            Require(second.Collected.GetValueOrDefault("iron-gear-wheel") >= 12, "The cell did not deliver gears to its output chest.");
            using var identity = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type='character',force='factorio_agent'}[1]; assert(c); local p=game.connected_players[1]; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,characters=s.count_entities_filtered{type='character'},players=#game.connected_players,pilotCharacter=p and p.character and p.character.unit_number or 0})
                """, token));
            RequireSameActor(identity.RootElement);
            Require(identity.RootElement.GetProperty("character").GetInt64() == setup.RootElement.GetProperty("character").GetInt64(),
                "Construction changed the native character.");
            evidence.Add(new { check = "same-native-actor-and-connected-pilot", native = identity.RootElement.Clone() });

            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var kinds = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data").GetProperty("kind").GetString()).ToArray();
                int cleared = rows.Count(r => r.RootElement.GetProperty("type").GetString() == "factory-clearance");
                int crafts = kinds.Count(k => k == "craft");
                evidence.Add(new { check = fromMaterials ? "native-construction-from-stored-materials" : "no-manual-production",
                    crafts, mines = kinds.Count(k => k == "mine"), cleared });
                Require((fromMaterials ? crafts > 0 : crafts == 0) && kinds.Count(k => k == "mine") == cleared,
                    "Construction procurement or resource mining does not match the declared fixture.");
                if (fromMaterials)
                {
                    var kit = rows.First(r => r.RootElement.GetProperty("type").GetString() == "construction-supply-plan")
                        .RootElement.GetProperty("data");
                    Require(kit.GetProperty("bundled").GetBoolean() && kit.GetProperty("needed").GetProperty("assembling-machine-1").GetInt32() == 1,
                        "The first assembler did not procure a grouped construction kit.");
                    var materials = kit.GetProperty("plan").GetProperty("materials").EnumerateArray().ToArray();
                    Require(materials.Length == 3 && materials.Any(m => m.GetProperty("item").GetString() == "iron-plate"
                            && m.GetProperty("targetStock").GetInt32() > 30)
                        && materials.Any(m => m.GetProperty("item").GetString() == "copper-plate")
                        && materials.Any(m => m.GetProperty("item").GetString() == "wood"),
                        "Shared native ingredients were not grouped into the three material stock goals.");
                    var proved = rows.First(r => r.RootElement.GetProperty("type").GetString() == "construction-supply-result")
                        .RootElement.GetProperty("data");
                    evidence.Add(new { check = "grouped-material-goals-and-native-carried-kit", kit = kit.Clone(), proof = proved.Clone() });
                }
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-factory-cell-qualification", fromMaterials, passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void RequireSameActor(JsonElement identity) => Require(identity.GetProperty("characters").GetInt32() == 1
        && (identity.GetProperty("players").GetInt32() == 0
            || identity.GetProperty("pilotCharacter").GetInt64() == identity.GetProperty("character").GetInt64()),
        "The connected pilot must share the single native character.");
}
