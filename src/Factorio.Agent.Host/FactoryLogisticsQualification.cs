using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepared scarce-input fixture; supplied gears/circuits/plates are not autonomous intermediate production.</summary>
public sealed class FactoryLogisticsQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture || File.Exists(new FactoryRegistry(session.Directory).Path))
            throw new InvalidOperationException("Logistics qualification requires a fresh explicit fixture.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(12));
        token = deadline.Token;
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"factory-logistics-qualification-{Guid.NewGuid():N}.json");
        var journal = new ControllerJournal(Path.ChangeExtension(path, ".jsonl"));
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared gear balance: research/power/kit,25 gears,25 circuits,50 iron plates. No science. Not a campaign." }), token);
            if (!marked.Ok) throw new GameRpcException(marked.Error!);
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0 and #game.connected_players==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); for _,name in pairs{'steam-power','electronics','automation','automation-science-pack','logistics','logistic-science-pack'} do f.technologies[name].researched=true end; local supplies={['assembling-machine-1']=3,inserter=6,['iron-chest']=6,['small-electric-pole']=30,['iron-plate']=50,['iron-gear-wheel']=25,['electronic-circuit']=25}; for name,count in pairs(supplies) do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=3000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,supplied=supplies,science=f.get_item_production_statistics(s).get_input_count('logistic-science-pack')})
                """, token));
            Require(setup.RootElement.GetProperty("science").GetDouble() == 0, "The fixture must start with zero green science production.");
            evidence.Add(new { check = "explicit-preparation", native = setup.RootElement.Clone() });
            var builder = new FactoryCellBuilder(game, journal, session.Directory);
            var belts = await builder.BuildAsync("assembler", "assembling-machine-1", "transport-belt", token);
            var inserters = await builder.BuildAsync("assembler", "assembling-machine-1", "inserter", token);
            var science = await builder.BuildAsync("assembler", "assembling-machine-1", "logistic-science-pack", token);
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            var registry = new FactoryRegistry(session.Directory);
            await registry.SaveAsync((await registry.LoadAsync(catalog.Scope.WorldId, token)).WithTarget("logistic-science-pack", 4), token);
            await using var controller = new SpatialController(game, journal);
            var snapshots = new FactorySnapshotClient(game);
            var baseline = await snapshots.CaptureAsync(cancellationToken: token);
            Require(new[] { belts, inserters, science }.All(c => Finished(baseline, c) == 0), "Fixture machines must have zero initial crafts.");
            var carried = FactoryLogistics.Carried(baseline);
            Require(carried.GetValueOrDefault("transport-belt") == 0 && carried.GetValueOrDefault("inserter") == 0
                && carried.GetValueOrDefault("iron-gear-wheel") == 25 && carried.GetValueOrDefault("electronic-circuit") == 25,
                "Construction equipment must not contaminate the prepared production inputs.");
            var first = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(40, token);
            Require(first.Supplied.GetValueOrDefault("iron-gear-wheel") == 25, "The fixed gear lot was not supplied completely.");
            Require(await GearTransfers(belts.Entities["input-chest"]) == 8 && await GearTransfers(inserters.Entities["input-chest"]) == 17,
                "Scarce gears did not follow the planned belt/inserter recipe yields.");
            evidence.Add(new { check = "planned-scarce-ingredient-sharing", beltGears = 8, inserterGears = 17, first });
            // Recipe time alone omits the single input arm moving three different ingredients into an inserter assembler.
            for (int attempt = 0; ; attempt++)
            {
                var intermediates = await snapshots.CaptureAsync(cancellationToken: token);
                long beltCrafts = Finished(intermediates, belts), inserterCrafts = Finished(intermediates, inserters);
                long beltOutput = FactoryLogistics.Items(intermediates, belts.Entities["output-chest"]).GetValueOrDefault("transport-belt");
                long inserterOutput = FactoryLogistics.Items(intermediates, inserters.Entities["output-chest"]).GetValueOrDefault("inserter");
                evidence.Add(new { check = "native-intermediate-progress", intermediates.CollectedTick, beltCrafts, inserterCrafts, beltOutput, inserterOutput });
                Require(beltCrafts <= 8 && inserterCrafts <= 17, "The fixture produced more intermediates than its prepared gear lot permits.");
                if (beltCrafts == 8 && inserterCrafts == 17 && beltOutput == 16 && inserterOutput == 17) break;
                Require(attempt < 6, "The prepared intermediate outputs did not finish within their ninety-second budget.");
                Require((await controller.WorkAsync("wait", new { ticks = 900 }, 1200, token: token)).Status == "completed", "Intermediate production wait failed.");
            }
            var second = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(40, token);
            Require(second.Supplied.GetValueOrDefault("transport-belt") == 16 && second.Supplied.GetValueOrDefault("inserter") == 17,
                "Native intermediate outputs did not reach the science input chest.");
            FactorySnapshot final;
            for (int attempt = 0; ; attempt++)
            {
                final = await snapshots.CaptureAsync(cancellationToken: token);
                if (Finished(final, science) == 16 && FactoryLogistics.Items(final, science.Entities["output-chest"])
                    .GetValueOrDefault("logistic-science-pack") == 16) break;
                Require(attempt < 16, "Green science did not finish within its four-minute budget.");
                Require((await controller.WorkAsync("wait", new { ticks = 900 }, 1200, token: token)).Status == "completed", "Science production wait failed.");
            }
            using var counters = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("""
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,players=#game.connected_players,science=f.get_item_production_statistics(s).get_input_count('logistic-science-pack'),craftingQueue=c.crafting_queue_size})
                """, token));
            Require(counters.RootElement.GetProperty("science").GetDouble() == 16
                && counters.RootElement.GetProperty("character").GetInt64() == setup.RootElement.GetProperty("character").GetInt64()
                && counters.RootElement.GetProperty("players").GetInt32() == 0 && counters.RootElement.GetProperty("craftingQueue").GetInt32() == 0,
                "Native science production or headless actor identity differs from the fixture expectation.");
            foreach (string line in await File.ReadAllLinesAsync(Path.ChangeExtension(path, ".jsonl"), token))
            {
                using var row = JsonDocument.Parse(line);
                if (row.RootElement.GetProperty("type").GetString() != "submission") continue;
                string? kind = row.RootElement.GetProperty("data").GetProperty("kind").GetString();
                Require(kind is not ("craft" or "mine"), "Prepared production must not craft or mine by hand.");
            }
            evidence.Add(new { check = "native-green-science-from-scarce-inputs", final.CollectedTick, scienceCrafts = Finished(final, science),
                output = FactoryLogistics.Items(final, science.Entities["output-chest"]), second, native = counters.RootElement.Clone() });
            passed = true;
            return path;

            async Task<long> GearTransfers(string chest)
            {
                long count = 0;
                foreach (string line in await File.ReadAllLinesAsync(Path.ChangeExtension(path, ".jsonl"), token))
                {
                    using var row = JsonDocument.Parse(line);
                    if (row.RootElement.GetProperty("type").GetString() != "submission") continue;
                    var data = row.RootElement.GetProperty("data");
                    if (data.GetProperty("kind").GetString() != "insert") continue;
                    var args = data.GetProperty("args");
                    if (args.GetProperty("entityId").GetString() == chest && args.GetProperty("item").GetString() == "iron-gear-wheel")
                        count += args.GetProperty("count").GetInt64();
                }
                return count;
            }
        }
        finally { await LocalJson.WriteAsync(path, new { kind = "prepared-scarce-logistics-qualification", passed, isAutonomousCampaign = false, evidence }, CancellationToken.None); }
    }

    private static long Finished(FactorySnapshot snapshot, FactoryCell cell) => snapshot.Records.Single(r => r.Kind == "work"
        && r.EntityId == cell.Entities["machine"]).Data.GetProperty("productsFinished").GetInt64();
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
