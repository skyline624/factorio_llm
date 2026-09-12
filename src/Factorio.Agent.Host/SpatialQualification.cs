using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed class SpatialQualification(RuntimeSession session)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Spatial qualification requires an explicit fixture session.");
        string id = Guid.NewGuid().ToString("N");
        string reportPath = Path.Combine(session.Directory, $"spatial-qualification-{id}.json");
        List<object> evidence = [];
        using var lease = ActorControlLease.Acquire(session.Directory);
        IGameClient game = session.CreateClient(lease);
        var changingWorld = new ObstacleInjectionClient(game, session);
        await using var controller = new SpatialController(changingWorld, new ControllerJournal(Path.Combine(session.Directory, $"spatial-qualification-{id}.jsonl")));
        try
        {
            GameResponse marked = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new { reason = "Synthetic C# navigation and placement qualification." }), token);
            Require(marked.Ok, "Fixture marking failed.");
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; assert(c); c.teleport({0,0}); for _,e in ipairs(s.find_entities_filtered{area={{-24,-24},{40,24}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-24,40 do for y=-24,24 do tiles[#tiles+1]={name="grass-1",position={x,y}} end end; s.set_tiles(tiles); local water={}; for x=4,7 do for y=-3,3 do water[#water+1]={name="water",position={x,y}} end end; s.set_tiles(water); for y=-6,6 do assert(s.create_entity{name="stone-wall",position={9.5,y+0.5},force=c.force}) end; c.insert{name="stone-furnace",count=1}; c.insert{name="wooden-chest",count=1}; rcon.print("spatial-layout-fixture");
                """;
            Require((await session.CreateRcon().ExecuteAsync(prepare, token)).Trim() == "spatial-layout-fixture", "Spatial fixture preparation failed.");
            SpatialNativeState before = await ReadNativeAsync(token);
            SpatialSnapshot map = await new SpatialClient(game).CaptureAsync(["stone-furnace", "wooden-chest"], cancellationToken: token);
            RoutePlan planned = new RoutePlanner().Find(new(map), new(20, 0), token: token);
            Require(planned.Status == RouteStatus.Found && planned.Waypoints.Count >= 2
                && planned.Waypoints.Any(p => Math.Abs(p.Y) > 5), "C# did not synthesize a detour around the fixture obstacles.");
            await File.WriteAllTextAsync(Path.Combine(session.Directory, $"spatial-map-{id}.json"), JsonSerializer.Serialize(map, Protocol.Json), token);
            evidence.Add(new { check = "synthetic-spatial-layout", disqualifiedAsCampaign = true,
                setup = "Cleared a local area, placed 13 walls and 28 water tiles, and supplied a furnace and chest. Only setup teleports the actor.",
                before, planned });
            NavigationResult navigation = await controller.NavigateAsync(new(20, 0), cancellationToken: token);
            SpatialNativeState arrived = await ReadNativeAsync(token);
            Require(arrived.Position.DistanceTo(new(20, 0)) <= 0.4 && arrived.Tick - before.Tick >= 100
                && arrived.Walls == 18 && arrived.WaterTiles == 28, "Native movement did not reach the goal around preserved obstacles.");
            Require(changingWorld.InjectedTick is not null && navigation.Receipts.Any(r => r.Error?.Code == "path_blocked"),
                "The obstacle added during movement did not produce an observed native blockage.");
            Require(navigation.Receipts.Count >= 3 && navigation.Receipts.All(r => (r.Status == "completed" || r.Error?.Code == "path_blocked")
                && r.Effects.GetProperty("elapsedTicks").GetInt64() > 0), "Native route segments lack completion or blockage evidence.");
            Require(arrived.CharacterId == before.CharacterId && arrived.CharacterCount == 1
                && (arrived.ConnectedPlayers == 0 || arrived.PilotCharacterId == arrived.CharacterId),
                "Navigation replaced the actor or detached its pilot.");
            evidence.Add(new { check = "native-obstacle-navigation", navigation, changingWorld.InjectedTick,
                addedWallsDuringMove = 5, after = arrived });
            OperationReceipt furnace = await controller.BuildAsync("stone-furnace", new(23, 0), token);
            OperationReceipt chest = await controller.BuildAsync("wooden-chest", new(23, 0), token);
            SpatialNativeState built = await ReadNativeAsync(token);
            Require(furnace.Status == "completed" && chest.Status == "completed" && built.Furnaces == 1 && built.Chests == 1
                && built.FurnaceItems == before.FurnaceItems - 1 && built.ChestItems == before.ChestItems - 1,
                "Computed construction did not consume the two actual construction items.");
            MapPosition furnacePosition = furnace.Effects.GetProperty("entityPosition").Deserialize<MapPosition>(Protocol.Json)!;
            MapPosition chestPosition = chest.Effects.GetProperty("entityPosition").Deserialize<MapPosition>(Protocol.Json)!;
            Require(furnacePosition == built.FurnacePosition && chestPosition == built.ChestPosition && furnacePosition != chestPosition,
                "Placement receipts do not match the distinct native entity positions.");
            evidence.Add(new { check = "native-computed-placement-and-cost", after = built,
                furnace = furnace.Evidence, chest = chest.Evidence, preferredPosition = new MapPosition(23, 0) });
            NavigationResult returned = await controller.NavigateAsync(new(0, 0), cancellationToken: token);
            SpatialNativeState final = await ReadNativeAsync(token);
            Require(final.Position.DistanceTo(new(0, 0)) <= 0.4 && final.Furnaces == 1 && final.Chests == 1
                && final.Walls == 18 && final.WaterTiles == 28, "Return navigation failed to preserve the newly built factory and obstacles.");
            evidence.Add(new { check = "native-return-after-map-change", returned, after = final });
            using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(token);
            var interruptingClient = new InterruptAfterMoveClient(game, interrupted);
            bool interruptedAsExpected = false;
            await using (var stopping = new SpatialController(interruptingClient,
                new ControllerJournal(Path.Combine(session.Directory, $"spatial-stop-{id}.jsonl"))))
            {
                try { await stopping.NavigateAsync(new(-10, 0), cancellationToken: interrupted.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { interruptedAsExpected = true; }
            }
            Require(interruptedAsExpected && interruptingClient.OperationId is not null, "Fixture did not interrupt an accepted move.");
            OperationReceipt stopped = await new OperationClient(game).QueryAsync(interruptingClient.OperationId!, token);
            SpatialNativeState stoppedAt = await ReadNativeAsync(token);
            await Task.Delay(500, token);
            SpatialNativeState stillStopped = await ReadNativeAsync(token);
            Require(stopped.Status == "cancelled" && stopped.Error?.Code != "stop_unconfirmed"
                && stillStopped.Tick > stoppedAt.Tick && stillStopped.Position == stoppedAt.Position,
                "Disposing the interrupted controller did not establish a stopped character.");
            evidence.Add(new { check = "native-controller-cancellation-stops-movement", receipt = stopped.Evidence,
                stoppedAt, after = stillStopped });
            await SaveAsync(true, null, token);
            return reportPath;
        }
        catch (Exception error)
        {
            await SaveAsync(false, error.Message, CancellationToken.None);
            throw;
        }
        Task SaveAsync(bool passed, string? error, CancellationToken saveToken) => File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(new { kind = "synthetic-spatial-qualification", passed, error,
                isAutonomousCampaign = false, evidence }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), saveToken);
    }

    private async Task<SpatialNativeState> ReadNativeAsync(CancellationToken token)
    {
        const string command = """
            /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; local p=game.connected_players[1]; local f=s.find_entities_filtered{name="stone-furnace",force=c.force}; local ch=s.find_entities_filtered{name="wooden-chest",force=c.force}; rcon.print(helpers.table_to_json({tick=game.tick,characterId=c.unit_number,characterCount=s.count_entities_filtered{type="character"},connectedPlayers=#game.connected_players,pilotCharacterId=p and p.character and p.character.unit_number or 0,position=c.position,walls=s.count_entities_filtered{name="stone-wall",force=c.force},waterTiles=s.count_tiles_filtered{area={{4,-3},{8,4}},name="water"},furnaces=#f,chests=#ch,furnaceItems=c.get_main_inventory().get_item_count("stone-furnace"),chestItems=c.get_main_inventory().get_item_count("wooden-chest"),furnacePosition=f[1] and f[1].position,chestPosition=ch[1] and ch[1].position}));
            """;
        return JsonSerializer.Deserialize<SpatialNativeState>(await session.CreateRcon().ExecuteAsync(command, token), Protocol.Json)
            ?? throw new InvalidDataException("Missing native spatial evidence.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    /// <summary>Fixture-only perturbation after the engine accepts the final leg of the original route.</summary>
    private sealed class ObstacleInjectionClient(IGameClient inner, RuntimeSession runtime) : IGameClient
    {
        public long? InjectedTick { get; private set; }
        public async Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            GameResponse response = await inner.ExecuteAsync(request, cancellationToken);
            if (InjectedTick is null && response.Ok && request.Action == "submit"
                && request.Arguments.GetProperty("kind").GetString() == "move"
                && request.Arguments.GetProperty("args").GetProperty("position").Deserialize<MapPosition>(Protocol.Json) == new MapPosition(20, 0))
            {
                const string command = """
                    /silent-command local s=game.surfaces.nauvis; for y=-6,-2 do assert(s.create_entity{name="stone-wall",position={15.5,y+0.5},force="factorio_agent"}) end; rcon.print(game.tick);
                    """;
                InjectedTick = long.Parse((await runtime.CreateRcon().ExecuteAsync(command, cancellationToken)).Trim(),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            return response;
        }
    }

    private sealed class InterruptAfterMoveClient(IGameClient inner, CancellationTokenSource interruption) : IGameClient
    {
        public string? OperationId { get; private set; }
        public async Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            GameResponse response = await inner.ExecuteAsync(request, cancellationToken);
            if (response.Ok && request.Action == "submit" && request.Arguments.GetProperty("kind").GetString() == "move")
            {
                OperationId = request.Arguments.GetProperty("operationId").GetString();
                interruption.Cancel();
            }
            return response;
        }
    }
}

public sealed record SpatialNativeState(long Tick, long CharacterId, int CharacterCount, int ConnectedPlayers, long PilotCharacterId,
    MapPosition Position, int Walls, int WaterTiles, int Furnaces, int Chests, int FurnaceItems, int ChestItems,
    MapPosition? FurnacePosition = null, MapPosition? ChestPosition = null);
