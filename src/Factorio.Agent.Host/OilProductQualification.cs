using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepared oil co-product storage and an unsupplied rocket-fuel or electric-engine consumer, not a campaign.</summary>
public sealed class OilProductQualification(RuntimeSession session, string item = "rocket-fuel")
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Oil product qualification requires an explicit fixture session.");
        if (item is not ("rocket-fuel" or "electric-engine-unit")) throw new ArgumentException("Unsupported oil product fixture.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        token = deadline.Token;
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"oil-product-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared advanced oil: crude, shore, power, research, equipment and plates supplied; no oil products or target." }), token);
            Require(marked.Ok, $"Fixture marker rejected: {marked.Error?.Code}.");
            const string extra = """
                for _,t in pairs{'advanced-oil-processing','lubricant','rocket-fuel','engine','electric-engine','automation-2','steel-processing'} do assert(f.technologies[t]); f.technologies[t].researched=true end; for name,count in pairs{['storage-tank']=3,['assembling-machine-2']=10,['chemical-plant']=2,['stone-furnace']=2,['offshore-pump']=2,inserter=28,['iron-chest']=28,['iron-plate']=1000,['copper-plate']=1000} do assert(c.insert{name=name,count=count}==count) end; for _,name in pairs{'solid-fuel','engine-unit','electric-engine-unit','rocket-fuel','steel-plate','electronic-circuit'} do assert(c.get_item_count(name)==0) end;
                """;
            using var prepared = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(OilChemistryQualification.Prepare + extra + Statistics, token));
            var before = prepared.RootElement.Clone();
            evidence.Add(new { check = "explicit-oil-product-preparation", item, native = before });
            Require(new[] { "heavy", "light", "gas", "lubricant", "solid-fuel", "engine-unit", "electric-engine-unit", "rocket-fuel" }
                .All(name => before.GetProperty(name).GetDouble() == 0), "Cumulative evidence requires a fresh fixture with no supplied intermediate product.");
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var registry = new FactoryRegistry(session.Directory);
            await registry.SaveAsync(new FactoryState(1, catalog.Scope.WorldId, [], []), token);
            var plan = await new FactoryDirector(game, journal, session.Directory).AutomateAsync(item, 1, token);
            var state = await registry.LoadAsync(catalog.Scope.WorldId, token);
            evidence.Add(new { check = "oil-product-chain-built", plan, state.Cells });
            var refinery = state.Cells.Single(c => c.Recipe == "advanced-oil-processing" && c.Status == "ready");
            Require(Enumerable.Range(0, 3).All(i => refinery.Entities.ContainsKey($"reservoir-{i}")
                && refinery.Plan!.ContainsKey($"reservoir-{i}")), "A simultaneous output has no registered native reservoir.");
            Require(state.Cells.Any(c => c.Recipe == item && c.Status == "ready"), "The target consumer was not constructed.");
            var logistics = new FactoryLogistics(game, journal, session.Directory);
            await using var controller = new SpatialController(game, journal);
            long collected = 0;
            for (int round = 0; round < 10 && collected == 0; round++)
            {
                var service = await logistics.ServiceAsync(5, token);
                collected += service.Collected.GetValueOrDefault(item);
                evidence.Add(new { check = "oil-product-logistics", round, service });
                if (collected == 0) Require((await controller.WorkAsync("wait", new { ticks = 3600 }, 3900, token: token)).Status == "completed",
                    "The production wait failed; reconcile native effects.");
            }
            Require(collected > 0, "No native oil target was collected.");
            JsonElement after = default;
            FactorySnapshot? stable = null;
            string[] oilCounters = ["heavy", "heavyConsumed", "light", "lightConsumed", "gas", "gasConsumed"];
            // Bracket the stock photograph with unchanged native counters; a recipe cycle between two readings is not loss.
            for (int sample = 0; sample < 3; sample++)
            {
                using var earlier = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("/silent-command " + Statistics, token));
                var candidate = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                Require(candidate.Scope == catalog.Scope, "Actor scope changed before the conservation proof.");
                using var later = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("/silent-command " + Statistics, token));
                if (oilCounters.Any(name => earlier.RootElement.GetProperty(name).GetDouble() != later.RootElement.GetProperty(name).GetDouble())) continue;
                after = later.RootElement.Clone();
                stable = candidate;
                break;
            }
            Require(stable is not null, "No stable native counter window for the conservation proof.");
            var snapshot = stable!;
            var totals = snapshot.SummarizeStocks().Fluids;
            evidence.Add(new { check = "native-oil-product-conservation", before, after, collected, stocks = totals });
            foreach (string fluid in new[] { "heavy-oil", "light-oil", "petroleum-gas" })
            {
                string key = fluid == "heavy-oil" ? "heavy" : fluid == "light-oil" ? "light" : "gas";
                double produced = after.GetProperty(key).GetDouble(), consumed = after.GetProperty(key + "Consumed").GetDouble();
                Require(produced > 0 && Math.Abs(produced - consumed - totals.GetValueOrDefault(fluid)) < .1,
                    $"The native {fluid} co-product was lost or mixed rather than stored or consumed.");
            }
            Require(after.GetProperty(item).GetDouble() > 0 && after.GetProperty("players").GetInt32() == 0,
                "Native production or headless isolation was not proven.");
            if (item == "rocket-fuel") Require(after.GetProperty("solid-fuel").GetDouble() >= 10 && after.GetProperty("lightConsumed").GetDouble() > 0,
                "Rocket fuel did not consume native-produced solid fuel and light oil.");
            else Require(after.GetProperty("lubricant").GetDouble() > 0 && after.GetProperty("lubricantConsumed").GetDouble() > 0
                && after.GetProperty("engine-unit").GetDouble() > 0 && after.GetProperty("circuit").GetDouble() > 0
                && after.GetProperty("steel").GetDouble() > 0, "An electric-engine intermediate was supplied instead of manufactured.");
            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Require(rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission").All(r =>
                    r.RootElement.GetProperty("data").GetProperty("kind").GetString() is not ("mine" or "craft")),
                    "The prepared fixture triggered manual mining or hand crafting.");
                Require(rows.Count(r => r.RootElement.GetProperty("type").GetString() == "fluid-reservoir-connected") == 3,
                    "The three isolated native reservoir capacities were not proven.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-oil-product-qualification", item, passed,
                isAutonomousCampaign = false, finiteReservoirsOnly = true, journalPath, evidence }, CancellationToken.None);
        }
        return path;
    }

    private const string Statistics = """
        local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local fs=f.get_fluid_production_statistics(s); local is=f.get_item_production_statistics(s); rcon.print(helpers.table_to_json{tick=game.tick,heavy=fs.get_input_count('heavy-oil'),heavyConsumed=fs.get_output_count('heavy-oil'),light=fs.get_input_count('light-oil'),lightConsumed=fs.get_output_count('light-oil'),gas=fs.get_input_count('petroleum-gas'),gasConsumed=fs.get_output_count('petroleum-gas'),lubricant=fs.get_input_count('lubricant'),lubricantConsumed=fs.get_output_count('lubricant'),['solid-fuel']=is.get_input_count('solid-fuel'),['rocket-fuel']=is.get_input_count('rocket-fuel'),['engine-unit']=is.get_input_count('engine-unit'),['electric-engine-unit']=is.get_input_count('electric-engine-unit'),circuit=is.get_input_count('electronic-circuit'),steel=is.get_input_count('steel-plate'),players=#game.connected_players})
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
