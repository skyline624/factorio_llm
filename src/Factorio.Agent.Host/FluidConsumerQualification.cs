using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Explicitly prepared oil chain that must manufacture acid and consume it in a battery or processor cell.</summary>
public sealed class FluidConsumerQualification(RuntimeSession session, string item = "battery", bool fromMaterials = false)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Fluid consumer qualification requires an explicit fixture session.");
        if (item is not ("battery" or "processing-unit")) throw new ArgumentException("The fluid consumer fixture covers battery and processing-unit.");
        if (fromMaterials && item != "processing-unit") throw new ArgumentException("The intermediate production fixture requires processing-unit.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"fluid-consumer-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared acid consumer: crude, shore, power, research, equipment, solid inputs injected; no acid or target." }), token);
            Require(mark.Ok, $"Fixture marker rejected: {mark.Error?.Code}.");
            const string extra = """
                local f=game.forces.factorio_agent; local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=f}[1]; for _,t in pairs{'battery','processing-unit','automation-2'} do assert(f.technologies[t]); f.technologies[t].researched=true end; for name,count in pairs{['chemical-plant']=2,['assembling-machine-2']=1,['offshore-pump']=2,inserter=4,['iron-chest']=4,['iron-plate']=50,['copper-plate']=25,['electronic-circuit']=200,['advanced-circuit']=25} do assert(c.insert{name=name,count=count}==count) end; assert(c.get_item_count('sulfur')==0 and c.get_item_count('battery')==0 and c.get_item_count('processing-unit')==0);
                """;
            const string processorEquipment = """
                for name,count in pairs{['assembling-machine-2']=9,inserter=24,['iron-chest']=24,['iron-plate']=950,['copper-plate']=975} do assert(c.insert{name=name,count=count}==count) end;
                """;
            const string intermediateSetup = """
                f.technologies['advanced-circuit'].researched=true; assert(c.remove_item{name='electronic-circuit',count=200}==200); assert(c.remove_item{name='advanced-circuit',count=25}==25); assert(c.get_item_count('electronic-circuit')==0 and c.get_item_count('advanced-circuit')==0);
                """;
            string preparation = await session.CreateRcon().ExecuteAsync(OilChemistryQualification.Prepare + extra
                + (item == "processing-unit" ? processorEquipment : "") + (fromMaterials ? intermediateSetup : "") + Statistics, token);
            await journal.AppendAsync("fixture-preparation-response", new { response = preparation }, token);
            using var prepared = JsonDocument.Parse(preparation);
            var before = prepared.RootElement.Clone();
            evidence.Add(new { check = "explicit-fluid-consumer-preparation", native = before });
            Require(before.GetProperty("acidProduced").GetDouble() == 0 && before.GetProperty(item).GetDouble() == 0,
                "Use a fresh fixture for cumulative production evidence.");
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            await new FactoryRegistry(session.Directory).SaveAsync(new FactoryState(1, catalog.Scope.WorldId, [], []), token);
            var plan = await new FactoryDirector(game, journal, session.Directory).AutomateAsync(item, 2, token);
            var state = await new FactoryRegistry(session.Directory).LoadAsync(catalog.Scope.WorldId, token);
            evidence.Add(new { check = "acid-consumer-chain-built", plan, state.Cells });
            if (fromMaterials)
                Require(new[] { "copper-cable", "electronic-circuit", "advanced-circuit", "plastic-bar" }.All(recipe =>
                    state.Cells.Any(c => c.Recipe == recipe && c.Status == "ready")), "A solid or chemical intermediate producer was not constructed.");
            Require(new[] { "basic-oil-processing", "sulfur", "sulfuric-acid", item }.All(recipe =>
                state.Cells.Any(c => c.Kind == FluidCellBuilder.MachineKind && c.Recipe == recipe && c.Status == "ready")),
                "A required fluid chain stage was not completed.");
            var consumer = state.Cells.Single(c => c.Kind == FluidCellBuilder.MachineKind && c.Recipe == item && c.Status == "ready");
            var map = await new SpatialClient(game).CaptureAsync([consumer.MachineItem], 48, token);
            var machine = map.Entities.Single(e => e.Id == consumer.Entities["machine"]);
            // Adjacent native ports need no pipe item. Native consumption below proves the whole route.
            Require(machine.FluidConnections?.Any(p => p.Filter == "sulfuric-acid" && p.FlowDirection is "input" or "input-output"
                && p.TargetEntityId is not null) == true, "The consumer has no native acid input connection.");
            evidence.Add(new { check = "native-consumer-port", machine.Id, machine.FluidConnections,
                pipes = consumer.Entities.Keys.Count(r => r.StartsWith("pipe-", StringComparison.Ordinal)) });
            var logistics = new FactoryLogistics(game, journal, session.Directory);
            long collected = 0;
            await using var controller = new SpatialController(game, journal);
            for (int round = 0; round < 8 && collected == 0; round++)
            {
                var service = await logistics.ServiceAsync(5, token);
                collected += service.Collected.GetValueOrDefault(item);
                evidence.Add(new { check = "consumer-logistics", round, service });
                if (collected == 0)
                    Require((await controller.WorkAsync("wait", new { ticks = 3600 }, 3900, token: token)).Status == "completed", "Production wait failed.");
            }
            Require(collected > 0, $"No native {item} reached an output chest.");
            using var observed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("/silent-command " + Statistics, token));
            var after = observed.RootElement.Clone();
            evidence.Add(new { check = "native-acid-consumption", before, after, collected });
            Require(after.GetProperty("acidProduced").GetDouble() > before.GetProperty("acidProduced").GetDouble()
                && after.GetProperty("acidConsumed").GetDouble() > before.GetProperty("acidConsumed").GetDouble()
                && after.GetProperty(item).GetDouble() > before.GetProperty(item).GetDouble(),
                "The engine did not both produce and consume acid to manufacture the target.");
            if (fromMaterials)
                Require(new[] { "cableProduced", "circuitProduced", "advancedProduced", "plasticProduced" }.All(name =>
                    before.GetProperty(name).GetDouble() == 0 && after.GetProperty(name).GetDouble() > 0),
                    "The engine did not manufacture every unsupplied circuit intermediate.");
            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Require(rows.Any(r => r.RootElement.GetProperty("type").GetString() == "fluid-stage-primed"), "The acid supplier was not primed.");
                var mutations = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission").Select(r => r.RootElement.GetProperty("data")).ToArray();
                Require(mutations.All(s => s.GetProperty("kind").GetString() is not ("mine" or "craft")),
                    "The fully supplied fixture triggered mining or hand crafting.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-fluid-consumer-qualification", item, fromMaterials, passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }
        return path;
    }

    private const string Statistics = """
        local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local fs=f.get_fluid_production_statistics(s); local is=f.get_item_production_statistics(s); rcon.print(helpers.table_to_json{tick=game.tick,acidProduced=fs.get_input_count('sulfuric-acid'),acidConsumed=fs.get_output_count('sulfuric-acid'),battery=is.get_input_count('battery'),['processing-unit']=is.get_input_count('processing-unit'),sulfurProduced=is.get_input_count('sulfur'),crudeProduced=fs.get_input_count('crude-oil'),gasProduced=fs.get_input_count('petroleum-gas'),cableProduced=is.get_input_count('copper-cable'),circuitProduced=is.get_input_count('electronic-circuit'),advancedProduced=is.get_input_count('advanced-circuit'),plasticProduced=is.get_input_count('plastic-bar'),players=#game.connected_players})
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
