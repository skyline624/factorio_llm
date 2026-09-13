using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Prepared distant oil deposit; grid extension and research use native power and extraction.</summary>
public sealed class FluidExtractionQualification(RuntimeSession session, bool reuse = false)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Fluid extraction qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"fluid-extraction-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared distant crude oil, shore, oil-gathering research and equipment; native grid and oil-processing trigger. Not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and c.crafting_queue_size==0);for _,p in pairs(game.connected_players) do assert(p.character==c) end;game.speed=4;for _,e in pairs(s.find_entities_filtered{force=f}) do if e~=c then e.destroy() end end;for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{112,64}}}) do if e~=c then e.destroy() end end;local tiles={};for x=-64,112 do for y=-64,64 do tiles[#tiles+1]={name=x< -20 and 'water' or 'grass-1',position={x,y}} end end;s.set_tiles(tiles);assert(c.teleport({0,0}));c.health=c.max_health;c.get_main_inventory().clear();f.technologies['steam-power'].researched=true;f.technologies['oil-gathering'].researched=true;f.technologies['oil-processing'].researched=false;for name,count in pairs{['offshore-pump']=1,boiler=1,['steam-engine']=1,['small-electric-pole']=50,inserter=1,pumpjack=1,coal=200} do assert(c.insert{name=name,count=count}==count) end;assert(s.create_entity{name='crude-oil',position={70,0},amount=100000});rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "prepared-distant-fluid-deposit", native = setup.RootElement.Clone(), reuse });
            var power = await new SteamPowerController(game, new ControllerJournal(journalPath + ".power")).RunAsync(token);
            evidence.Add(new { check = "native-steam-construction", power });
            string install = reuse ? "assert(c.remove_item{name='pumpjack',count=1}==1);assert(s.create_entity{name='pumpjack',position={70,0},force=f});" : "";
            string beforeCommand = "/silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];local b=s.find_entities_filtered{name='boiler',force=f}[1];assert(b);b.get_fuel_inventory().clear();b.burner.remaining_burning_fuel=0;b.energy=0;for i=1,#b.fluidbox do b.fluidbox[i]=nil end;for _,g in pairs(s.find_entities_filtered{type='generator',force=f}) do g.energy=0;for i=1,#g.fluidbox do g.fluidbox[i]=nil end end;" + install
                + "assert(c.teleport({65,0}));local d=s.find_entities_filtered{name='pumpjack',force=f}[1];rcon.print(helpers.table_to_json{tick=game.tick,researched=f.technologies['oil-processing'].researched,produced=f.get_fluid_production_statistics(s).get_input_count('crude-oil'),pumpjack=d and tostring(d.unit_number),carried=c.get_item_count('pumpjack'),character=c.unit_number,players=#game.connected_players})";
            using var beforeDocument = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(beforeCommand, token));
            var before = beforeDocument.RootElement.Clone();
            Require(!before.GetProperty("researched").GetBoolean(), "The resource trigger was already researched before extraction.");
            var result = await new ResourceResearchController(game, journal).RunAsync("oil-processing", token);
            const string afterCommand = """
                /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];local ds=s.find_entities_filtered{name='pumpjack',force=f};assert(#ds==1);local d=ds[1];rcon.print(helpers.table_to_json{tick=game.tick,researched=f.technologies['oil-processing'].researched,produced=f.get_fluid_production_statistics(s).get_input_count('crude-oil'),pumpjack=tostring(d.unit_number),carried=c.get_item_count('pumpjack'),network=d.electric_network_id,energy=d.energy,character=c.unit_number,players=#game.connected_players})
                """;
            using var afterDocument = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(afterCommand, token));
            var after = afterDocument.RootElement.Clone();
            int links = 0, mines = 0, refills = 0, pumpBuilds = 0;
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "power-grid-link") links++;
                if (type != "submission") continue;
                string? kind = data.GetProperty("kind").GetString();
                var args = data.GetProperty("args");
                if (kind == "mine") mines++;
                if (kind == "build" && args.GetProperty("item").GetString() == "pumpjack") pumpBuilds++;
                if (kind == "insert" && args.GetProperty("entityId").GetString() == power.Entities["boiler"]) refills++;
            }
            evidence.Add(new { check = "native-distant-extraction", before, result, after, links, mines, refills, pumpBuilds });
            Require(after.GetProperty("researched").GetBoolean() && result.ConnectedFluidStock > 0 && result.PoweredSamples > 0
                && after.GetProperty("produced").GetDouble() > before.GetProperty("produced").GetDouble(),
                "Native oil production and research have not both been proven.");
            Require(after.GetProperty("network").GetInt64() == power.NetworkId && links > 1 && refills > 0,
                "The distant pump lacks a verified extended steam network and boiler refill.");
            Require(mines == 0 && pumpBuilds == (reuse ? 0 : 1) && after.GetProperty("carried").GetInt32() == 0,
                "Unexpected manual mining or pump construction cost.");
            Require(after.GetProperty("pumpjack").GetString() == result.MachineId
                && (!reuse || before.GetProperty("pumpjack").GetString() == result.MachineId)
                && after.GetProperty("character").GetUInt32() == before.GetProperty("character").GetUInt32(),
                "The native extractor or controlled character changed unexpectedly.");
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-fluid-extraction-qualification", passed,
                isAutonomousCampaign = false, reuse, journalPath, evidence }, CancellationToken.None);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
