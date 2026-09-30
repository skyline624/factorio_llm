using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: an injected power source, trees in the factory area and supplied construction items.
/// Proves native band construction, clearance, recipe configuration and chest logistics; not a campaign.
/// </summary>
public sealed class FactoryCellQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
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
            { reason = "Injected energy interface, construction items, 400 iron plates and trees. Factory band test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); f.technologies['steam-power'].researched=true; f.technologies.electronics.researched=true; f.technologies.automation.researched=true; for name,count in pairs{['assembling-machine-1']=2,inserter=4,['iron-chest']=4,['small-electric-pole']=12,lab=1,['iron-plate']=400} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=2000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); for i=0,5 do assert(s.create_entity{name='tree-01',position={-12+i*2.5,6.5+(i%2)}}) end; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            File.Delete(new FactoryRegistry(session.Directory).Path); // The fixture area was just emptied.
            evidence.Add(new { check = "explicit-factory-preparation", native = setup.RootElement.Clone() });

            var builder = new FactoryCellBuilder(game, journal, session.Directory);
            var gears = await builder.BuildAsync("assembler", "assembling-machine-1", "iron-gear-wheel", token);
            var lab = await builder.BuildAsync("lab", "lab", null, token);
            evidence.Add(new { check = "cells-ready", gears, lab });
            // Link poles are recorded as link-n roles so maintenance can rebuild them; they are not cell parts.
            static int Parts(FactoryCell cell) => cell.Entities.Keys.Count(role => !role.StartsWith("link-", StringComparison.Ordinal));
            Require(gears.Status == "ready" && Parts(gears) == 6 && Parts(lab) == 2, "Cells were not completed with their native entities.");

            var logistics = new FactoryLogistics(game, journal, session.Directory);
            var first = await logistics.ServiceAsync(50, token);
            Require(first.Supplied.GetValueOrDefault("iron-plate") == 100, "The input chest was not stocked for 50 gear crafts.");
            await using (var controller = new SpatialController(game, journal))
                Require((await controller.WorkAsync("wait", new { ticks = 2400 }, 2700, token: token)).Status == "completed", "Wait failed.");
            var second = await logistics.ServiceAsync(50, token);
            evidence.Add(new { check = "chest-logistics", first, second });
            // One basic inserter moves about 0.83 plates per second, so 40 seconds yield roughly 16 gears.
            Require(second.Collected.GetValueOrDefault("iron-gear-wheel") >= 12, "The cell did not deliver gears to its output chest.");

            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var kinds = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data").GetProperty("kind").GetString()).ToArray();
                int cleared = rows.Count(r => r.RootElement.GetProperty("type").GetString() == "factory-clearance");
                evidence.Add(new { check = "no-manual-production", crafts = kinds.Count(k => k == "craft"), mines = kinds.Count(k => k == "mine"), cleared });
                Require(kinds.Count(k => k == "craft") == 0 && kinds.Count(k => k == "mine") == cleared, "Supplied construction triggered manual production.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-factory-cell-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
