using System.Globalization;
using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared crude-oil trigger whose deposit is out of every local view. By default it lies in a chunk the actor's own square
/// requested for charting. With <c>radar</c>, nothing charted holds crude oil until the factory radar scans its sector. With
/// <c>danger</c>, the same hidden deposit follows two native deaths of the actor, and the blind search must be refused once the
/// radar stands. Charting, deaths, extraction, power and research stay native; only the terrain, deposit, equipment and
/// prerequisites are prepared.
/// </summary>
public sealed class ChartedResourceQualification(RuntimeSession session, bool radar = false, bool danger = false)
{
    // Chunk (2,0) is inside the 5x5 square the actor requests from (0,0). The actor otherwise stands in chunks (-1,-1) to (0,0),
    // whose squares never reach chunk (2,-3); a radar beside the steam pole scans that chunk early in its third ring.
    private static readonly MapPosition Deposit = new(80.5, 24.5), HiddenDeposit = new(80.5, -80.5);
    private static readonly MapPosition Home = new(0, 0), HiddenHome = new(-8, 8);
    // Far from the base and the hidden deposit; the actor dies on arrival, before its square is charted there.
    private static readonly MapPosition[] DeathPlaces = [new(90.5, 40.5), new(100.5, -20.5)];

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Charted resource qualification requires an explicit fixture session.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"charted-resource-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool hidden = radar || danger, passed = false;
        var deposit = hidden ? HiddenDeposit : Deposit;
        try
        {
            // The native marker keeps at most 128 bytes of reason.
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            {
                reason = "Prepared shore, out-of-view oil, kit, oil-gathering" + (hidden ? ", radar" : "")
                    + (danger ? ", two deaths" : "") + "; native charting, oil trigger. Not a campaign."
            }), token);
            Require(mark.Ok, $"Fixture marker rejected: {mark.Error?.Code}.");
            string technologies = hidden ? "f.technologies['radar'].researched=true;" : "";
            string prepare = "/silent-command local s=game.surfaces.nauvis;local f=game.forces.factorio_agent;local c=s.find_entities_filtered{type='character',force=f}[1];assert(c and c.crafting_queue_size==0);for _,p in pairs(game.connected_players) do assert(p.character==c) end;game.speed=4;"
                + "for _,e in pairs(s.find_entities_filtered{force=f}) do if e~=c then e.destroy() end end;for _,e in pairs(s.find_entities_filtered{area={{-64,-112},{112,64}}}) do if e~=c then e.destroy() end end;"
                + "for _,e in pairs(s.find_entities_filtered{name='crude-oil',area={{-256,-256},{256,256}}}) do e.destroy() end;"
                + "local tiles={};for x=-64,112 do for y=-112,64 do tiles[#tiles+1]={name=x< -20 and 'water' or 'grass-1',position={x,y}} end end;s.set_tiles(tiles);"
                + "assert(c.teleport({0,0}));c.health=c.max_health;c.get_main_inventory().clear();f.technologies['steam-power'].researched=true;f.technologies['oil-gathering'].researched=true;f.technologies['oil-processing'].researched=false;" + technologies
                + "for name,count in pairs{['offshore-pump']=1,boiler=1,['steam-engine']=1,['small-electric-pole']=50,inserter=1,pumpjack=1,coal=200" + (hidden ? ",radar=1" : "") + "} do assert(c.insert{name=name,count=count}==count) end;"
                + $"local d=s.create_entity{{name='crude-oil',position={{{Lua(deposit.X)},{Lua(deposit.Y)}}},amount=100000}};assert(d);"
                + "rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number,deposit=d.position,players=#game.connected_players})";
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "prepared-out-of-view-deposit", native = setup.RootElement.Clone(), radar, danger });
            var power = await new SteamPowerController(game, new ControllerJournal(journalPath + ".power")).RunAsync(token);
            evidence.Add(new { check = "native-steam-construction", power });
            if (danger)
            {
                var deaths = new List<object>();
                foreach (var place in DeathPlaces) deaths.Add(await DieAsync(game, place, token));
                // The corpses keep the carried equipment; the respawned actor is given the same extraction kit again.
                const string kit = "/silent-command local f=game.forces.factorio_agent;local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=f}[1];assert(c and c.teleport({0,0}));for name,count in pairs{['small-electric-pole']=50,pumpjack=1,coal=200,radar=1} do assert(c.insert{name=name,count=count}==count) end;rcon.print(helpers.table_to_json{tick=game.tick,character=c.unit_number})";
                using var given = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(kit, token));
                evidence.Add(new { check = "two-native-deaths", deaths, kit = given.RootElement.Clone() });
            }
            // The actor stays home for a while: from the origin its normal square requests chunk (2,0) as a player's sight would.
            await using (var walker = new SpatialController(game, journal))
            {
                var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
                await walker.TravelAsync(hidden ? HiddenHome : Home, 2, catalog, token);
                var waited = await walker.WorkAsync("wait", new { ticks = 120 }, 600, token: token);
                Require(waited.Status == "completed", "The charting wait did not complete.");
            }
            using var beforeDocument = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(State(deposit), token));
            var before = beforeDocument.RootElement.Clone();
            var view = await new SpatialClient(game).CaptureAsync(["pumpjack"], 48, token);
            var memory = await game.ReadResourceMemoryAsync(view, token);
            var zones = await game.ReadActiveDeathsAsync(view.Scope, view.SurfaceIndex, view.CollectedTick, token);
            bool chartedBefore = before.GetProperty("depositChunkCharted").GetBoolean() || before.GetProperty("depositChunkRequested").GetBoolean();
            evidence.Add(new { check = "deposit-outside-local-view", before, view.Actor.Position, view.Bounds, view.CollectedTick,
                rememberedCrudeOil = memory.Resources.Count(r => r.Name == "crude-oil"), activeDeaths = zones });
            Require(!before.GetProperty("researched").GetBoolean() && !view.Bounds.Contains(deposit)
                && memory.Resources.All(r => r.Name != "crude-oil"), "The deposit was observed or remembered, or the trigger already researched.");
            Require(chartedBefore == !hidden, hidden ? "The hidden deposit was already charted before any radar." : "The deposit chunk was not charted by the actor's square.");
            Require(zones.Count == (danger ? DeathPlaces.Length : 0), "The active own death zones differ from the prepared deaths.");

            ResourceResearchResult? result = null;
            ExplorationTooDangerousException? refused = null;
            try { result = await new ResourceResearchController(game, journal, session.Directory).RunAsync("oil-processing", token); }
            catch (ExplorationTooDangerousException error) when (danger) { refused = error; }
            using var afterDocument = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(State(deposit), token));
            var after = afterDocument.RootElement.Clone();
            var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            var cells = (await new FactoryRegistry(session.Directory).LoadAsync(known.Scope.WorldId, token)).Cells
                .Where(c => c.Kind == RadarController.Kind).ToArray();
            var facts = await JournalAsync(journalPath, deposit, token);
            evidence.Add(new { check = danger ? "refused-blind-search-with-radar" : "native-extraction-from-charted-destination", result,
                refused = refused?.Message, after, journal = facts, radarCells = cells,
                radarFed = cells.Select(c => FactoryPower.IsFed(known, c.Entities["machine"])).ToArray() });
            if (hidden)
            {
                var cell = cells.SingleOrDefault();
                Require(cell is { Status: "ready" } && cell.Plan?.ContainsKey("machine") == true && facts.RadarBuilds == 1
                    && FactoryPower.IsFed(known, cell.Entities["machine"]) == true && after.GetProperty("radarEnergy").GetDouble() > 0,
                    "No single registered radar stands powered on a fed network.");
            }
            else Require(facts.RadarBuilds == 0 && cells.Length == 0, "A radar was built although the deposit was charted.");
            Require(facts.Mines == 0, "Unexpected manual mining.");
            if (danger)
            {
                Require(refused is not null && facts.Refusals == 1 && facts.Searches == 0 && facts.RadarWaits == 0 && facts.PumpBuilds == 0
                    && !after.GetProperty("researched").GetBoolean(), "The blind search was not refused before any exploration step.");
                passed = true;
                return path;
            }
            Require(result is not null && after.GetProperty("researched").GetBoolean() && result.ConnectedFluidStock > 0 && result.PoweredSamples > 0
                && after.GetProperty("produced").GetDouble() > before.GetProperty("produced").GetDouble(),
                "Native oil production and research have not both been proven.");
            Require(after.GetProperty("pumpjackDistance").GetDouble() < 2 && after.GetProperty("pumpjack").GetString() == result!.MachineId,
                "The extractor does not stand on the prepared deposit.");
            Require(facts.PumpBuilds == 1 && facts.Refusals == 0, "Unexpected extractor construction or refusal.");
            Require(facts.Searches > 0 && facts.BlindSearches == 0 && facts.FirstOrigin == ResourceSighting.Charted,
                "The search did not follow the charted destination without blind exploration.");
            Require(facts.SurveyTickOfDeposit is { } seen && seen <= facts.FirstSearchTick,
                "No map reading showed the deposit before the first search step.");
            if (radar)
                Require(facts.SurveyPurposeOfDeposit == "radar-charting" && facts.RadarWaits > 0, "The deposit was not revealed by the radar's charting.");
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-charted-resource-qualification", passed, isAutonomousCampaign = false,
                radar, danger, deposit, journalPath, evidence }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Kills the actor at a place, then observes until it respawns: every observation records the engine's latest own death, so
    /// each death is recorded before the next one happens.
    /// </summary>
    private async Task<object> DieAsync(SessionGameClient game, MapPosition place, CancellationToken token)
    {
        string kill = "/silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1];"
            + $"assert(c and c.teleport({{{Lua(place.X)},{Lua(place.Y)}}}));local unit=c.unit_number;c.die(game.forces.enemy);"
            + "rcon.print(helpers.table_to_json{tick=game.tick,character=unit})";
        using var killed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(kill, token));
        for (int poll = 0; poll < 240; poll++)
        {
            var observed = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
            Require(observed.Ok, "Observation after the prepared death failed.");
            if (observed.Data.GetProperty("agent").GetProperty("alive").GetBoolean())
                return new { place, native = killed.RootElement.Clone(), respawnObservedTick = observed.Tick };
            await Task.Delay(250, token);
        }
        throw new TimeoutException("The actor did not respawn after the prepared death.");
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
        string? SurveyPurposeOfDeposit, int Surveys, int RadarBuilds, int RadarWaits, int Mines, int PumpBuilds, int Refusals);

    /// <summary>Search steps, map readings, refusals and native submissions of the run, as the journal recorded them.</summary>
    internal static async Task<JournalFacts> JournalAsync(string journalPath, MapPosition deposit, CancellationToken token)
    {
        var chunk = ChartedChunk.Of(deposit);
        int searches = 0, blind = 0, surveys = 0, radars = 0, waits = 0, mines = 0, pumps = 0, refusals = 0;
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
                case "resource-research-too-dangerous": refusals++; break;
                case "radar-built": radars++; break;
                case "radar-charting-wait": waits++; break;
                case "submission":
                    string? kind = data.GetProperty("kind").GetString();
                    if (kind == "mine") mines++;
                    if (kind == "build" && data.GetProperty("args").GetProperty("item").GetString() == "pumpjack") pumps++;
                    break;
            }
        }
        return new(searches, blind, firstOrigin, firstSearch, revealed, purpose, surveys, radars, waits, mines, pumps, refusals);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
