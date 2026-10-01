using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared crude-oil trigger whose deposit is out of every local view. Without <c>radar</c> it lies in a chunk the actor's own
/// square requested for charting; with <c>radar</c> nothing charted holds crude oil until the factory radar scans its sector.
/// Charting itself, extraction, power and research stay native; only the terrain, deposit, equipment and prerequisites are prepared.
/// </summary>
public sealed class ChartedResourceQualification(RuntimeSession session, bool radar = false)
{
    // Chunk (2,0) is inside the 5x5 square the actor requests from (0,0); chunk (-1,-3) is outside every square it occupies.
    private static readonly MapPosition Deposit = new(80.5, 24.5), RadarDeposit = new(-16.5, -80.5);

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Charted resource qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"charted-resource-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        var deposit = radar ? RadarDeposit : Deposit;
        bool passed = false;
        try
        {
            // The native marker keeps at most 128 bytes of reason.
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            {
                reason = "Prepared shore, out-of-view crude oil, equipment, oil-gathering" + (radar ? ", radar" : "")
                    + "; native charting and oil trigger. Not a campaign."
            }), token);
            Require(mark.Ok, $"Fixture marker rejected: {mark.Error?.Code}.");
            string technologies = radar ? "f.technologies['radar'].researched=true;" : "";
            string radarItem = radar ? ",radar=1" : "";
            string prepare = "/silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and c.crafting_queue_size==0);for _,p in pairs(game.connected_players) do assert(p.character==c) end;game.speed=4;"
                + "for _,e in pairs(s.find_entities_filtered{force=f}) do if e~=c then e.destroy() end end;for _,e in pairs(s.find_entities_filtered{area={{-64,-112},{112,64}}}) do if e~=c then e.destroy() end end;"
                + "for _,e in pairs(s.find_entities_filtered{name='crude-oil',area={{-256,-256},{256,256}}}) do e.destroy() end;"
                + "local tiles={};for x=-64,112 do for y=-112,64 do tiles[#tiles+1]={name=x< -20 and 'water' or 'grass-1',position={x,y}} end end;s.set_tiles(tiles);"
                + "assert(c.teleport({0,0}));c.health=c.max_health;c.get_main_inventory().clear();f.technologies['steam-power'].researched=true;f.technologies['oil-gathering'].researched=true;f.technologies['oil-processing'].researched=false;" + technologies
                + "for name,count in pairs{['offshore-pump']=1,boiler=1,['steam-engine']=1,['small-electric-pole']=50,inserter=1,pumpjack=1,coal=200" + radarItem + "} do assert(c.insert{name=name,count=count}==count) end;"
                + $"local d=s.create_entity{{name='crude-oil',position={{{Lua(deposit.X)},{Lua(deposit.Y)}}},amount=100000}};assert(d);"
                + "rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,deposit=d.position,players=#game.connected_players})";
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "prepared-out-of-view-deposit", native = setup.RootElement.Clone(), radar });
            var power = await new SteamPowerController(game, new ControllerJournal(journalPath + ".power")).RunAsync(token);
            evidence.Add(new { check = "native-steam-construction", power });
            // The actor returns to the origin for a while, so its normal square requests chunk (2,0) as a player's sight would.
            await using (var walker = new SpatialController(game, journal))
            {
                var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
                await walker.TravelAsync(new(0, 0), 2, catalog, token);
                var waited = await walker.WorkAsync("wait", new { ticks = 120 }, 600, token: token);
                Require(waited.Status == "completed", "The charting wait did not complete.");
            }
            using var beforeDocument = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(State(deposit), token));
            var before = beforeDocument.RootElement.Clone();
            var view = await new SpatialClient(game).CaptureAsync(["pumpjack"], 48, token);
            var memory = await game.ReadResourceMemoryAsync(view, token);
            bool chartedBefore = before.GetProperty("depositChunkCharted").GetBoolean() || before.GetProperty("depositChunkRequested").GetBoolean();
            evidence.Add(new { check = "deposit-outside-local-view", before, view.Actor.Position, view.Bounds, view.CollectedTick,
                rememberedCrudeOil = memory.Resources.Count(r => r.Name == "crude-oil") });
            Require(!before.GetProperty("researched").GetBoolean() && !view.Bounds.Contains(deposit)
                && memory.Resources.All(r => r.Name != "crude-oil"), "The deposit was observed or remembered, or the trigger already researched.");
            Require(chartedBefore == !radar, radar ? "The radar deposit was already charted before any radar." : "The deposit chunk was not charted by the actor's square.");

            var result = await new ResourceResearchController(game, journal, session.Directory).RunAsync("oil-processing", token);
            using var afterDocument = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(State(deposit), token));
            var after = afterDocument.RootElement.Clone();
            var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            var cells = (await new FactoryRegistry(session.Directory).LoadAsync(known.Scope.WorldId, token)).Cells
                .Where(c => c.Kind == RadarController.Kind).ToArray();
            var journalFacts = await JournalAsync(journalPath, deposit, token);
            evidence.Add(new { check = "native-extraction-from-charted-destination", result, after, journal = journalFacts,
                radarCells = cells, radarFed = cells.Select(c => FactoryPower.IsFed(known, c.Entities["machine"])).ToArray() });

            Require(after.GetProperty("researched").GetBoolean() && result.ConnectedFluidStock > 0 && result.PoweredSamples > 0
                && after.GetProperty("produced").GetDouble() > before.GetProperty("produced").GetDouble(),
                "Native oil production and research have not both been proven.");
            Require(after.GetProperty("pumpjackDistance").GetDouble() < 2 && after.GetProperty("pumpjack").GetString() == result.MachineId,
                "The extractor does not stand on the prepared deposit.");
            Require(journalFacts.Mines == 0 && journalFacts.PumpBuilds == 1, "Unexpected manual mining or extractor construction.");
            Require(journalFacts.Searches > 0 && journalFacts.BlindSearches == 0 && journalFacts.FirstOrigin == ResourceSighting.Charted,
                "The search did not follow the charted destination without blind exploration.");
            Require(journalFacts.SurveyTickOfDeposit is { } seen && seen <= journalFacts.FirstSearchTick,
                "No map reading showed the deposit before the first search step.");
            if (radar)
            {
                var cell = cells.SingleOrDefault();
                Require(cell is { Status: "ready" } && cell.Plan?.ContainsKey("machine") == true && journalFacts.RadarBuilds == 1
                    && FactoryPower.IsFed(known, cell.Entities["machine"]) == true && after.GetProperty("radarEnergy").GetDouble() > 0,
                    "No single registered radar stands powered on a fed network.");
                Require(journalFacts.SurveyPurposeOfDeposit == "radar-charting" && journalFacts.RadarWaits > 0,
                    "The deposit was not revealed by the radar's charting.");
            }
            else Require(journalFacts.RadarBuilds == 0 && cells.Length == 0, "A radar was built although the deposit was charted.");
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-charted-resource-qualification", passed, isAutonomousCampaign = false,
                radar, deposit, journalPath, evidence }, CancellationToken.None);
        }
    }

    private static string State(MapPosition deposit) =>
        "/silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];"
        + $"local p={{{Lua(deposit.X)},{Lua(deposit.Y)}}};local k={{x=math.floor(p[1]/32),y=math.floor(p[2]/32)}};"
        + "local d=s.find_entities_filtered{name='pumpjack',force=f}[1];local r=s.find_entities_filtered{name='radar',force=f};local re=0;for _,e in pairs(r) do re=re+e.energy end;"
        + "rcon.print(helpers.table_to_json{tick=game.tick,researched=f.technologies['oil-processing'].researched,produced=f.get_fluid_production_statistics(s).get_input_count('crude-oil'),"
        + "pumpjack=d and tostring(d.unit_number),pumpjackDistance=d and math.sqrt((d.position.x-p[1])^2+(d.position.y-p[2])^2) or -1,"
        + "radars=#r,radarEnergy=re,depositChunkCharted=f.is_chunk_charted(s,k),depositChunkRequested=f.is_chunk_requested_for_charting(s,k),"
        + "character=c.unit_number,position=c.position,players=#game.connected_players})";

    private static string Lua(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    internal sealed record JournalFacts(int Searches, int BlindSearches, string? FirstOrigin, long? FirstSearchTick, long? SurveyTickOfDeposit,
        string? SurveyPurposeOfDeposit, int Surveys, int RadarBuilds, int RadarWaits, int Mines, int PumpBuilds);

    /// <summary>Search steps, map readings and native submissions of the run, as the journal recorded them.</summary>
    internal static async Task<JournalFacts> JournalAsync(string journalPath, MapPosition deposit, CancellationToken token)
    {
        var chunk = ChartedChunk.Of(deposit);
        int searches = 0, blind = 0, surveys = 0, radars = 0, waits = 0, mines = 0, pumps = 0;
        string? firstOrigin = null, purpose = null;
        long? firstSearch = null, revealed = null, lastSurvey = null;
        foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
        {
            using var row = JsonDocument.Parse(line);
            string? type = row.RootElement.GetProperty("type").GetString();
            var data = row.RootElement.GetProperty("data");
            switch (type)
            {
                case "charted-resource-survey":
                    surveys++;
                    lastSurvey = data.GetProperty("collectedTick").GetInt64();
                    if (revealed is null && data.GetProperty("nearest").EnumerateArray().Any(d => d.GetProperty("name").GetString() == "crude-oil"
                        && d.GetProperty("chunk").Deserialize<ChartedChunk>(Protocol.Json) == chunk))
                        (revealed, purpose) = (lastSurvey, data.GetProperty("purpose").GetString());
                    break;
                case "resource-research-search":
                    searches++;
                    var historical = data.GetProperty("historical");
                    if (historical.ValueKind != JsonValueKind.Object) blind++;
                    firstOrigin ??= historical.ValueKind == JsonValueKind.Object ? historical.GetProperty("origin").GetString() : "none";
                    firstSearch ??= data.GetProperty("frontier").GetProperty("collectedTick").GetInt64();
                    break;
                case "radar-built": radars++; break;
                case "radar-charting-wait": waits++; break;
                case "submission":
                    string? kind = data.GetProperty("kind").GetString();
                    if (kind == "mine") mines++;
                    if (kind == "build" && data.GetProperty("args").GetProperty("item").GetString() == "pumpjack") pumps++;
                    break;
            }
        }
        return new(searches, blind, firstOrigin, firstSearch, revealed, purpose, surveys, radars, waits, mines, pumps);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
