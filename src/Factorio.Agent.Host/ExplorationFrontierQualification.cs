using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Explicit synthetic shoreline and historical coverage; native walking must leave the old survey without gifts.</summary>
public sealed class ExplorationFrontierQualification(RuntimeSession session, bool corner = false)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Frontier qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var native = session.CreateClient(lease);
        string reportPath = Path.Combine(session.Directory, $"exploration-frontier-qualification-{Guid.NewGuid():N}.json");
        var journal = new ControllerJournal(Path.ChangeExtension(reportPath, ".jsonl"));
        var evidence = new List<object>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var marked = await native.ExecuteAsync(GameRequest.Create("mark_fixture", new
            {
                reason = "Synthetic shoreline, cleared ground, actor reset and remembered survey. Native blind walking; not a campaign."
            }), deadline.Token);
            Require(marked.Ok, "Fixture marking failed.");
            string setup = corner ? """
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; assert(c); s.request_to_generate_chunks({128,-128},18); s.force_generate_chunk_requests(); for _,e in ipairs(s.find_entities_filtered{area={{-304,-448},{640,176}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-304,640 do for y=-448,176 do tiles[#tiles+1]={name=(y< -352 or (y< -300 and (x<217 or x>=224))) and "water" or "grass-1",position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({220,-344})); rcon.print("synthetic-frontier-shoreline");
                """ : """
                /silent-command local s=game.surfaces.nauvis; local c=s.find_entities_filtered{type="character",force="factorio_agent"}[1]; assert(c); s.request_to_generate_chunks({0,-128},10); s.force_generate_chunk_requests(); for _,e in ipairs(s.find_entities_filtered{area={{-304,-448},{320,176}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-304,320 do for y=-448,176 do tiles[#tiles+1]={name=y< -352 and "water" or "grass-1",position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({64,-348})); rcon.print("synthetic-frontier-shoreline");
                """;
            Require((await session.CreateRcon().ExecuteAsync(setup, deadline.Token)).Trim() == "synthetic-frontier-shoreline",
                "Native shoreline preparation failed.");
            GameResponse before = await native.ExecuteAsync(GameRequest.Create("observe"), deadline.Token);
            Require(before.Ok, "Initial observation failed.");
            var initialScope = before.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
            var initialInventory = Inventory(before);
            var surveyed = (from x in Enumerable.Range(-56, corner ? 188 : 122)
                            from y in Enumerable.Range(-99, 132)
                            select new SurveyedCell(x, y)).ToArray();
            var game = new SurveyedFixtureClient(native, surveyed);
            var planner = new ExplorationPlanner();
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), deadline.Token));
            await using var controller = new SpatialController(game, journal);
            var spatial = new SpatialClient(game);
            var initial = await spatial.CaptureAsync(radius: 48, cancellationToken: deadline.Token);
            Require(initial.Rows.Any(r => r.Name == "water") && initial.Rows.Any(r => r.Name == "grass-1"),
                "The initial normal view must show both sides of the native shoreline.");
            bool leftSurvey = false;
            for (int step = 0; step < (corner ? 32 : 24); step++)
            {
                var target = await controller.FindExplorationWaypointAsync(planner, catalog, "", token: deadline.Token);
                var frontier = planner.Frontier;
                var navigation = await controller.NavigateAsync(target.Position, cancellationToken: deadline.Token);
                var after = await spatial.CaptureAsync(radius: 48, cancellationToken: deadline.Token);
                Require(after.Scope == initialScope && after.Actor.Position.DistanceTo(target.Position) <= .4
                    && navigation.Receipts.Count > 0 && navigation.Receipts.All(r => r.Status == "completed"),
                    "A blind step did not complete natively with the original actor.");
                leftSurvey = after.Actor.Position.X < -180 || after.Actor.Position.X > (corner ? 476 : 212) || after.Actor.Position.Y > 80;
                evidence.Add(new { check = "native-blind-step", step, target, frontier, position = after.Actor.Position,
                    after.CollectedTick, moves = navigation.Receipts.Count, leftSurvey });
                if (leftSurvey) break;
            }
            GameResponse final = await native.ExecuteAsync(GameRequest.Create("observe"), deadline.Token);
            Require(leftSurvey && final.Ok && final.Data.GetProperty("agent").GetProperty("deaths").GetInt32()
                    == before.Data.GetProperty("agent").GetProperty("deaths").GetInt32()
                && final.Data.GetProperty("agent").GetProperty("health").GetDouble()
                    == before.Data.GetProperty("agent").GetProperty("health").GetDouble()
                && initialInventory.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .SequenceEqual(Inventory(final).OrderBy(p => p.Key, StringComparer.Ordinal))
                && !final.Data.GetProperty("agent").GetProperty("mining").GetBoolean()
                && Connected(final) == Connected(before),
                "Blind exploration stayed in the old survey, changed inventory, harmed the actor or changed its pilot.");
            evidence.Add(new { check = "native-left-historical-survey", beforeTick = before.Tick, final.Tick,
                initialScope, corner, surveyedCells = surveyed.Length, inventoryPreserved = true,
                health = final.Data.GetProperty("agent").GetProperty("health").GetDouble(), connectedPlayers = Connected(final),
                setup = "Cleared terrain, created a shoreline, teleported once at setup and injected explicit synthetic historical coverage. No resources or research supplied." });
            await SaveAsync(true, null, token);
            return reportPath;
        }
        catch (Exception error)
        {
            await SaveAsync(false, error.Message, CancellationToken.None);
            throw;
        }
        Task SaveAsync(bool passed, string? error, CancellationToken saveToken) => File.WriteAllTextAsync(reportPath,
            JsonSerializer.Serialize(new { kind = "synthetic-exploration-frontier-qualification", passed, error,
                isAutonomousCampaign = false, evidence }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), saveToken);
    }

    private static IReadOnlyDictionary<string, long> Inventory(GameResponse response) =>
        response.Data.GetProperty("agent").GetProperty("inventory").Deserialize<Dictionary<string, long>>(Protocol.Json)!;

    private static int Connected(GameResponse response) => response.Data.GetProperty("players").ValueKind == JsonValueKind.Array
        ? response.Data.GetProperty("players").EnumerateArray().Count(p => p.GetProperty("connected").GetBoolean()) : 0;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private sealed class SurveyedFixtureClient(SessionGameClient inner, IReadOnlyList<SurveyedCell> survey)
        : IGameClient, IResourceMemoryReader, IDangerZoneReader
    {
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default) =>
            inner.ExecuteAsync(request, cancellationToken);

        public async Task<ResourceMemorySnapshot> ReadResourceMemoryAsync(SpatialSnapshot current, CancellationToken token = default)
        {
            var observed = await inner.ReadResourceMemoryAsync(current, token);
            return observed with { SurveyedCells = observed.SurveyedCells.Concat(survey).Distinct().ToArray() };
        }

        public Task<IReadOnlyList<NativeDeathTransition>> ReadActiveDeathsAsync(ActorScope scope, int surfaceIndex, long tick,
            CancellationToken token = default) => inner.ReadActiveDeathsAsync(scope, surfaceIndex, tick, token);
    }
}
