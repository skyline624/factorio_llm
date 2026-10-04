using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Fixture-only relocation to an ungenerated edge; generation and subsequent walking use the ordinary agent.</summary>
public sealed class ChunkGenerationQualification(RuntimeSession session, bool expectGeneration = true)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Chunk generation qualification requires an explicit fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var console = session.CreateRcon(keepConnectionOpen: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        string path = Path.Combine(session.Directory, $"chunk-generation-qualification-{Guid.NewGuid():N}.json");
        var evidence = new List<object>();
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            {
                reason = "One fixture teleport to the initial terrain edge; no terrain, resources, equipment or research supplied."
            }), deadline.Token);
            Require(marked.Ok, "Fixture marking failed.");
            using var prepared = await ReadAsync("setup", """
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type='character',force='factorio_agent'}[1]; assert(c and #game.connected_players==0); local chosen=nil; for _,p in ipairs{{304,0},{0,304},{-304,0},{0,-304}} do local pos=s.find_non_colliding_position('character',p,8,0.5); if pos then chosen=pos;break end end; assert(chosen and c.teleport(chosen)); rcon.print(helpers.table_to_json{tick=game.tick,fixture=true,position=c.position,remoteGenerated=s.is_chunk_generated({128,128}),resourcesSupplied=false,researchSupplied=false})
                """);
            Require(!prepared.RootElement.GetProperty("remoteGenerated").GetBoolean(), "Remote control chunk must initially be ungenerated.");
            var before = await game.ExecuteAsync(GameRequest.Create("observe"), deadline.Token);
            Require(before.Ok, "Initial native observation failed.");
            var initialScope = before.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
            var inventory = Inventory(before);
            var initial = await new SpatialClient(game).CaptureAsync(radius: 48, cancellationToken: deadline.Token);
            // Baseline and candidate must begin at the same native terrain boundary.
            // The ordinary one-second refresh may already have completed candidate generation.
            evidence.Add(new { check = "explicit-teleport-only", native = prepared.RootElement.Clone(),
                initial.CollectedTick, initial.Actor.Position, initial.Scope, initialOutOfMapRows = initial.Rows.Count(r => r.Name == "out-of-map") });
            await using var controller = new SpatialController(game, new ControllerJournal(Path.ChangeExtension(path, ".jsonl")));
            var waited = await controller.WorkAsync("wait", new { ticks = 600 }, 1800, token: deadline.Token);
            Require(waited.Status == "completed", "Native generation wait did not complete.");
            using var generated = await ReadAsync("local-generation", """
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type='character',force='factorio_agent'}[1]; assert(c); local x,y=math.floor(c.position.x/32),math.floor(c.position.y/32); local chunks={}; local complete=0; for dx=-2,2 do for dy=-2,2 do local p={x=x+dx,y=y+dy}; local g=s.is_chunk_generated(p);if g then complete=complete+1 end;chunks[#chunks+1]={x=p.x,y=p.y,generated=g} end end;rcon.print(helpers.table_to_json{tick=game.tick,position=c.position,complete=complete,localChunks=chunks,remoteGenerated=s.is_chunk_generated({128,128})})
                """);
            Require(!generated.RootElement.GetProperty("remoteGenerated").GetBoolean(), "Generation escaped to the remote control chunk.");
            var map = await new SpatialClient(game).CaptureAsync(radius: 48, cancellationToken: deadline.Token);
            Require(map.Scope == initialScope && map.Actor.Position.DistanceTo(initial.Actor.Position) < .01,
                "Generation changed the actor scope or moved it.");
            int complete = generated.RootElement.GetProperty("complete").GetInt32();
            if (expectGeneration)
            {
                Require(complete == 25 && map.Rows.All(r => r.Name != "out-of-map"),
                    "The standalone actor's native 5x5 square or local terrain is still ungenerated.");
                var field = new SpatialCollisionField(map);
                bool BeyondInitialEdge(MapPosition p, double minimum) => Math.Abs(initial.Actor.Position.X) > 280
                    ? Math.Sign(p.X) == Math.Sign(initial.Actor.Position.X) && Math.Abs(p.X) >= minimum
                    : Math.Sign(p.Y) == Math.Sign(initial.Actor.Position.Y) && Math.Abs(p.Y) >= minimum;
                var target = (from x in Enumerable.Range((int)Math.Ceiling(map.Bounds.Min.X / 4), 24)
                              from y in Enumerable.Range((int)Math.Ceiling(map.Bounds.Min.Y / 4), 24)
                              let p = new MapPosition(x * 4, y * 4)
                              where BeyondInitialEdge(p, 324) && p.DistanceTo(map.Actor.Position) <= 40
                                  && PlacementPlanner.CanStop(field, p)
                              orderby p.DistanceTo(map.Actor.Position), p.Y, p.X
                              select p).FirstOrDefault(p => new RoutePlanner().Find(field, p, token: deadline.Token).Status == RouteStatus.Found);
                Require(target is not null, "No native dry route beyond the original terrain edge was found; walking is unproved.");
                var navigation = await controller.NavigateAsync(target!, cancellationToken: deadline.Token);
                var arrived = await new SpatialClient(game).CaptureAsync(radius: 48, cancellationToken: deadline.Token);
                evidence.Add(new { check = "native-walk-result", target, arrived.Actor.Position,
                    arrived.CollectedTick, arrived.Scope, navigation.Receipts });
                // Native arrival may stop 0.15 tiles short of the target. The old
                // terrain border is 320, so require the whole character beyond it,
                // while preserving the target's ordinary arrival tolerance.
                Require(arrived.Scope == initialScope && arrived.Actor.Position.DistanceTo(target!) <= .4
                    && BeyondInitialEdge(arrived.Actor.Position, 321) && navigation.Receipts.Count > 0
                    && navigation.Receipts.All(r => r.Status == "completed"), "Native walking did not cross the old ungenerated edge.");
                evidence.Add(new { check = "native-walk-beyond-initial-edge", target, arrived.Actor.Position,
                    arrived.CollectedTick, operations = navigation.Receipts.Count });
            }
            else Require(complete < 25 && map.Rows.Any(r => r.Name == "out-of-map"), "The baseline did not reproduce missing generation.");
            var final = await game.ExecuteAsync(GameRequest.Create("observe"), deadline.Token);
            Require(final.Ok && final.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json) == initialScope
                && final.Data.GetProperty("agent").GetProperty("deaths").GetInt32() == before.Data.GetProperty("agent").GetProperty("deaths").GetInt32()
                && final.Data.GetProperty("agent").GetProperty("health").GetDouble() == before.Data.GetProperty("agent").GetProperty("health").GetDouble()
                && inventory.OrderBy(p => p.Key, StringComparer.Ordinal).SequenceEqual(Inventory(final).OrderBy(p => p.Key, StringComparer.Ordinal))
                && (final.Data.GetProperty("players").ValueKind != JsonValueKind.Array
                    || !final.Data.GetProperty("players").EnumerateArray().Any(p => p.GetProperty("connected").GetBoolean())),
                "Generation qualification changed inventory, harmed the actor, changed scope or connected a player.");
            evidence.Add(new { check = expectGeneration ? "candidate-generated-normal-local-square" : "baseline-missing-generation",
                native = generated.RootElement.Clone(), final.Tick, inventoryPreserved = true, noPlayerConnected = true });
            await SaveAsync(true, null);
            return path;
        }
        catch (Exception error)
        {
            await SaveAsync(false, error.Message);
            throw;
        }
        async Task<JsonDocument> ReadAsync(string check, string command)
        {
            string response = await console.ExecuteAsync(command, deadline.Token);
            await File.WriteAllTextAsync(Path.ChangeExtension(path, check + ".txt"), response, deadline.Token);
            return JsonDocument.Parse(response);
        }
        Task SaveAsync(bool passed, string? error) => LocalJson.WriteAsync(path, new
        {
            kind = "fixture-native-chunk-generation", passed, error, expectGeneration, isAutonomousCampaign = false,
            normalRocketProof = false, resourcesSupplied = false, researchSupplied = false, evidence
        }, CancellationToken.None);
    }

    private static IReadOnlyDictionary<string, long> Inventory(GameResponse response) =>
        response.Data.GetProperty("agent").GetProperty("inventory").Deserialize<Dictionary<string, long>>(Protocol.Json)!;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
