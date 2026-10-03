using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Explicit fixture of a tight factory approach beside a westbound belt, never a campaign.</summary>
public sealed class BeltNavigationQualification(RuntimeSession session)
{
    private static readonly MapPosition Destination = new(-43.5, 3.5);

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Belt navigation qualification requires an explicit fixture.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        await using var native = session.CreateRcon(keepConnectionOpen: true);
        string id = Guid.NewGuid().ToString("N");
        string path = Path.Combine(session.Directory, $"belt-navigation-{id}.json");
        string journalPath = Path.Combine(session.Directory, $"belt-navigation-{id}.jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
                { reason = "Prepared moving belt and chests; actor reset between trials. Not an autonomous campaign." }), token);
            Require(marked.Ok, "Fixture marking failed.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and #game.connected_players==0);game.speed=1;for _,e in pairs(s.find_entities_filtered{area={{-56,-8},{-28,16}}}) do if e~=c then e.destroy() end end;local tiles={};for x=-56,-28 do for y=-8,16 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end;s.set_tiles(tiles);for x=-48.5,-40.5 do assert(s.create_entity{name='transport-belt',position={x,4.5},direction=defines.direction.west,force=f}) end;for _,p in pairs{{-44.5,3.5},{-42.5,3.5},{-44.5,6.5},{-42.5,6.5}} do assert(s.create_entity{name='iron-chest',position=p,force=f}) end;assert(c.teleport({-44.8,4.47}));rcon.print('belt-fixture-ready')
                """;
            Require((await native.ExecuteAsync(prepare, token)).Trim() == "belt-fixture-ready", "Fixture preparation failed.");
            var initial = await ReadAsync();
            var map = await new SpatialClient(game).CaptureAsync(cancellationToken: token);
            var route = new RoutePlanner().Find(new(map), Destination, 0, timeBudget: TimeSpan.FromSeconds(2), token: token);
            Require(route.Status == RouteStatus.Found, "The prepared approach has no C# route.");
            route = route with { Waypoints = SpatialController.Subdivide(map.Actor.Position, route.Waypoints) };
            var planned = SpatialController.SelectMovePath(new(map), route);
            Require(planned.Count > 1 && !PlacementPlanner.CanStop(new(map), planned[0])
                && PlacementPlanner.CanStop(new(map), planned[^1]), "The fixture must require a moving corner and a stable final approach.");
            evidence.Add(new { check = "native-belt-corner-planned", map.CollectedTick, map.Actor, route, planned });

            await using (var controller = new SpatialController(game, journal))
            {
                var first = await controller.WorkAsync("move", new { position = planned[0], tolerance = .15 }, 1800, token: token);
                Require(first.Status == "completed", "The first single-corner move did not complete.");
                var stopped = await ReadAsync();
                Require((await controller.WorkAsync("wait", new { ticks = 60 }, 360, token: token)).Status == "completed", "The pause did not complete.");
                var drifted = await ReadAsync();
                double drift = stopped.Position.DistanceTo(drifted.Position);
                evidence.Add(new { check = "native-pause-drifts-on-belt", first, stopped, drifted, drift });
                Require(drift > .4, "The prepared belt did not move the stopped character during the pause.");
            }
            Require((await native.ExecuteAsync("/silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force='factorio_agent'}[1];assert(c.teleport({-44.8,4.47}));rcon.print('belt-fixture-reset')", token)).Trim()
                == "belt-fixture-reset", "Explicit fixture reset failed.");
            await using (var controller = new SpatialController(game, journal))
            {
                var result = await controller.NavigateAsync(Destination, .2, token);
                var reached = await ReadAsync();
                Require(result.Receipts.All(r => r.Status == "completed")
                    && result.Receipts.Any(r => r.Effects.TryGetProperty("waypointCount", out var count) && count.GetInt32() > 1
                        && r.Effects.GetProperty("waypointsReached").GetInt32() == count.GetInt32()),
                    "No completed continuous native movement reached all its C# corners.");
                Require((await controller.WorkAsync("wait", new { ticks = 60 }, 360, token: token)).Status == "completed", "The final stability wait failed.");
                var stable = await ReadAsync();
                evidence.Add(new { check = "native-continuous-approach", result, reached, stable });
                Require(stable.Position.DistanceTo(Destination) <= .2 && !stable.Walking
                    && reached.Position == stable.Position && stable.Character == initial.Character
                    && stable.Inventory == initial.Inventory && stable.Health == initial.Health && stable.Players == 0,
                    "The continuous approach did not leave the same character on stable ground with unchanged stocks and health.");

                // Invalid paths fail before walking, including the exact bound enforced by the native API.
                await RejectedAsync(new { position = new MapPosition(-40.5, 3.5), waypoints = new[] { new MapPosition(-41.5, 3.5), new MapPosition(-39.5, 3.5) } }, "invalid_move_path");
                await RejectedAsync(new { position = new MapPosition(-17.5, 3.5), waypoints = new[] { new MapPosition(-30.5, 3.5), new MapPosition(-17.5, 3.5) } }, "move_path_too_far");
                await RejectedAsync(new { position = Destination, waypoints = Enumerable.Repeat(Destination, 65).ToArray() }, "invalid_move_path");

                async Task RejectedAsync(object args, string code)
                {
                    var before = await ReadAsync();
                    var receipt = await controller.WorkAsync("move", args, 600, token: token);
                    var after = await ReadAsync();
                    evidence.Add(new { check = "invalid-native-movement-path", code, receipt, before, after });
                    Require(receipt.Status == "failed" && receipt.Error?.Code == code && after.Position == before.Position && !after.Walking,
                        "The invalid native path was not rejected before movement.");
                }
            }
            // A longer factory corridor exercises the in-flight guard before a tight future corner.
            // The row forces an eastern detour; its final inserter leaves the same swept, non-rectangular
            // approach observed in normal logistics. All scene edits remain inside this declared fixture.
            const string prepareCorridor = """
                /silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and #game.connected_players==0);for _,e in pairs(s.find_entities_filtered{area={{-60,-20},{-28,16}}}) do if e~=c then e.destroy() end end;local tiles={};for x=-60,-28 do for y=-20,16 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end;s.set_tiles(tiles);for x=-56.5,-42.5 do assert(s.create_entity{name='iron-chest',position={x,-3.5},force=f}) end;for x=-48.5,-42.5 do assert(s.create_entity{name='transport-belt',position={x,-1.5},direction=defines.direction.west,force=f}) end;assert(s.create_entity{name='inserter',position={-42.5,-2.5},force=f});assert(s.create_entity{name='small-electric-pole',position={-43.5,-2.5},force=f});assert(c.teleport({-48.25,-1.87109375}));rcon.print('belt-corridor-ready')
                """;
            Require((await native.ExecuteAsync(prepareCorridor, token)).Trim() == "belt-corridor-ready", "The explicit long corridor preparation failed.");
            var corridorMap = await new SpatialClient(game).CaptureAsync(cancellationToken: token);
            var corridorField = new SpatialCollisionField(corridorMap);
            MapPosition corridorDestination = new(-46.5, -6.5);
            var corridorRoute = new RoutePlanner().Find(corridorField, corridorDestination, 0, timeBudget: TimeSpan.FromSeconds(2), token: token);
            Require(corridorRoute.Status == RouteStatus.Found, "No C# route through the prepared long corridor.");
            corridorRoute = corridorRoute with { Waypoints = SpatialController.Subdivide(corridorMap.Actor.Position, corridorRoute.Waypoints) };
            var corridorPath = SpatialController.SelectMovePath(corridorField, corridorRoute);
            bool tightCorner = corridorPath.Zip(corridorPath.Skip(1), (a, b) => a.DistanceTo(b) <= .75 + 1e-9
                && !corridorField.SteeringRegionClear(a, b) && corridorField.SegmentClear(a, b)).Any(clear => clear);
            evidence.Add(new { check = "native-tight-corridor-planned", corridorMap.CollectedTick, corridorMap.Actor, corridorRoute, corridorPath, tightCorner });
            Require(corridorPath.Count > 1 && tightCorner, "The long corridor did not select a short swept corner outside its steering rectangle.");
            await using (var controller = new SpatialController(game, journal))
            {
                var result = await controller.NavigateAsync(corridorDestination, .2, token);
                var continuous = result.Receipts.Where(r => r.Status == "completed"
                    && r.Effects.TryGetProperty("waypointCount", out var count) && count.GetInt32() > 1).ToArray();
                var checks = (await File.ReadAllLinesAsync(journalPath, token)).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                    .Where(row => row.GetProperty("type").GetString() == "movement-terrain-check")
                    .Select(row => row.GetProperty("data"))
                    .Where(data => continuous.Any(r => r.OperationId == data.GetProperty("operationId").GetString())).ToArray();
                Require(result.Receipts.All(r => r.Status == "completed") && continuous.Length > 0
                    && checks.Length > 0 && checks.All(data => data.GetProperty("valid").GetBoolean()),
                    "The longer continuous corridor did not complete with successful in-flight geometry checks.");
                var reached = await ReadAsync();
                Require((await controller.WorkAsync("wait", new { ticks = 60 }, 360, token: token)).Status == "completed", "The long corridor stability wait failed.");
                var stable = await ReadAsync();
                evidence.Add(new { check = "native-continuous-tight-corridor", result, checks, reached, stable });
                Require(stable.Position.DistanceTo(corridorDestination) <= .2 && !stable.Walking && reached.Position == stable.Position
                    && stable.Character == initial.Character && stable.Inventory == initial.Inventory && stable.Health == initial.Health && stable.Players == 0,
                    "The long corridor changed the character, stocks, health or final stability.");
            }
            var current = await new SpatialClient(game).CaptureAsync(cancellationToken: token);
            var cancellationRoute = new RoutePlanner().Find(new(current), new(current.Actor.Position.X, current.Actor.Position.Y - 8), 0, token: token);
            Require(cancellationRoute.Status == RouteStatus.Found, "No observed cancellation-test route.");
            var corners = SpatialController.Subdivide(current.Actor.Position, cancellationRoute.Waypoints);
            var cancelPath = new[] { corners[0], corners[^1] };
            Require(new SpatialCollisionField(current).SteeringRegionClear(cancelPath[0], cancelPath[1]), "The cancellation path must remain clear.");
            var operations = new OperationClient(game);
            var submission = OperationSubmission.Create(current.Scope, "move", new { position = cancelPath[^1], tolerance = .15, waypoints = cancelPath }, current.CollectedTick + 1800);
            await journal.AppendAsync("submission", submission, token);
            var accepted = await operations.SubmitAsync(submission, token);
            Require(!accepted.IsTerminal, "The cancellation trial already completed.");
            SpatialSnapshot moving = await new SpatialClient(game).CaptureAsync(radius: 4, cancellationToken: token);
            for (int attempt = 0; attempt < 6 && moving.Actor.Movement?.WaypointIndex != 2; attempt++)
            {
                await Task.Delay(30, token);
                moving = await new SpatialClient(game).CaptureAsync(radius: 4, cancellationToken: token);
            }
            Require(moving.Scope == current.Scope && moving.Actor.Movement is { } progress
                && progress.OperationId == submission.OperationId && progress.WaypointCount == 2 && progress.WaypointIndex == 2,
                "The native frame did not report continuous progression into the second segment.");
            var cancelled = await operations.CancelAsync(submission.OperationId, token);
            await journal.AppendAsync("cancel-receipt", cancelled, token);
            var afterCancel = await ReadAsync();
            evidence.Add(new { check = "continuous-path-native-cancellation", submission, accepted, moving.CollectedTick, moving.Actor, cancelled, afterCancel });
            Require(cancelled.Status == "cancelled" && !afterCancel.Walking, "Continuous movement did not confirm its native stop.");
            passed = true;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-belt-navigation-qualification", passed,
                isAutonomousCampaign = false, journalPath, evidence }, CancellationToken.None);
        }
        return path;

        async Task<NativeState> ReadAsync()
        {
            const string read = """
                /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force='factorio_agent'}[1];rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,position=c.position,health=c.health,walking=c.walking_state.walking,players=#game.connected_players,inventory=helpers.table_to_json(c.get_main_inventory().get_contents())})
                """;
            return JsonSerializer.Deserialize<NativeState>(await native.ExecuteAsync(read, token), Protocol.Json)
                ?? throw new InvalidDataException("Missing native belt navigation state.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed record NativeState(long Tick, long Character, MapPosition Position, double Health, bool Walking, int Players, string Inventory);
}
