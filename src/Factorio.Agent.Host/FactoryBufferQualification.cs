using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Prepared native proof of a planned fast intermediate continuing beyond the former forty-craft reserve.</summary>
public sealed class FactoryBufferQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Factory buffer qualification requires an explicit fixture session.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        token = deadline.Token;
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"factory-buffer-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Injected energy, early research, construction kit and 500 iron plates. Planned buffer test; no gears supplied." }), token);
            if (!marked.Ok) throw new InvalidDataException("Fixture marker rejected.");
            const string preparation = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0 and #game.connected_players==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-48,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-48,48 do for y=-48,48 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({0,0})); c.health=c.max_health; c.get_main_inventory().clear(); f.cancel_current_research(); for _,t in pairs{'steam-power','electronics','automation-science-pack','automation'} do f.technologies[t].researched=true end; for name,count in pairs{['assembling-machine-1']=1,inserter=2,['iron-chest']=2,['small-electric-pole']=20,['iron-plate']=500} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-20,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=2000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-18.5,0.5},force=f}); rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,providedIron=500,providedGears=0,players=#game.connected_players})
                """;
            using var prepared = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(preparation, token));
            evidence.Add(new { check = "explicit-buffer-preparation", native = prepared.RootElement.Clone() });
            var registry = new FactoryRegistry(session.Directory);
            File.Delete(registry.Path);
            var cell = await new FactoryCellBuilder(game, journal, session.Directory)
                .BuildAsync("assembler", "assembling-machine-1", "iron-gear-wheel", token);
            var before = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            await registry.SaveAsync((await registry.LoadAsync(before.Scope.WorldId, token)).WithTarget("iron-gear-wheel", 24), token);
            var service = await new FactoryLogistics(game, journal, session.Directory).ServiceAsync(usePlannedBuffers: true, token: token);
            evidence.Add(new { check = "planned-ten-minute-share", rate = 24, crafts = 240, result = service });
            if (service.Supplied.GetValueOrDefault("iron-plate") != 480)
                throw new InvalidDataException("The planned gear input did not receive its 240-craft share.");
            await using var controller = new SpatialController(game, journal);
            // The native input inserter delivers two plates per gear, so allow three minutes at its real throughput.
            var waited = await controller.WorkAsync("wait", new { ticks = 10800 }, 11400, token: token);
            if (waited.Status != "completed") throw new InvalidDataException("The bounded native production wait did not complete.");
            var after = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            if (after.Scope != before.Scope) throw new InvalidDataException("Actor identity changed during buffer qualification.");
            long crafts = after.Records.Single(r => r.Kind == "work" && r.EntityId == cell.Entities["machine"])
                .Data.GetProperty("productsFinished").GetInt64();
            long stocked = FactoryLogistics.Items(after, cell.Entities["output-chest"]).GetValueOrDefault("iron-gear-wheel");
            long carried = FactoryLogistics.Carried(after).GetValueOrDefault("iron-gear-wheel");
            evidence.Add(new { check = "native-production-without-another-refill", beforeTick = before.CollectedTick,
                afterTick = after.CollectedTick, crafts, stocked, carried, machine = cell.Entities["machine"] });
            if (crafts < 60 || stocked < 60 || carried != 0)
                throw new InvalidDataException("Native gear production did not continue beyond the former forty-craft limit.");
            var kinds = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var submissions = kinds.Where(row => row.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(row => row.RootElement.GetProperty("data").GetProperty("kind").GetString()).ToArray();
                evidence.Add(new { check = "one-refill-no-hand-craft-or-mining", inserts = submissions.Count(k => k == "insert"),
                    crafts = submissions.Count(k => k == "craft"), mines = submissions.Count(k => k == "mine") });
                if (submissions.Count(k => k == "insert") != 1 || submissions.Any(k => k is "craft" or "mine"))
                    throw new InvalidDataException("The prepared buffer scenario required extra actor production or refills.");
            }
            finally { foreach (var row in kinds) row.Dispose(); }
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-factory-buffer-qualification", passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }
    }
}
