using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared fixture: an injected crude oil deposit and a shore, an energy interface with one pole about 95 tiles west of the
/// deposit, beside which the actor starts, a planned band registered between them, researched oil processing, plastics and
/// sulfur processing, and supplied construction items and coal. Proves that automating a fluid-chain product builds a
/// pumpjack extractor powered by links grown from that remote network around the band, a refinery and a chemical cell with
/// C#-routed pipes, that the engine refines pumped crude oil into petroleum gas, that logistics delivers the solid inputs and
/// collects the product, that maintenance rebuilds a destroyed pipe of the chain, and that a chemical cell reopened as
/// interrupted resumes from afar with its lost pipe rebuilt at its plan; not a campaign.
/// </summary>
public sealed class OilChemistryQualification(RuntimeSession session, string item = "plastic-bar", bool remoteWater = false, bool steamWater = false)
{
    // Beyond any 48-tile capture around the deposit, so only links grown from the network itself can reach the extractor.
    private static readonly MapPosition Deposit = new(12, 6), SourcePole = new(-82.5, .5);
    // A band no cell occupies yet, astride the straight line from the source to the deposit.
    private static readonly FactoryZone Band = new(1, new(-52, -8), 4, 6, 16);

    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Oil chemistry qualification requires an explicit fixture session.");
        if (item is not ("plastic-bar" or "sulfur")) throw new ArgumentException("The oil chemistry qualification covers plastic-bar and sulfur.");
        if (remoteWater && item != "sulfur") throw new ArgumentException("The remote water qualification requires sulfur.");
        if (steamWater && !remoteWater) throw new ArgumentException("The occupied steam source qualification requires remote water.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string path = Path.Combine(session.Directory, $"oil-chemistry-qualification-{Guid.NewGuid():N}.json");
        string journalPath = Path.ChangeExtension(path, ".jsonl");
        var journal = new ControllerJournal(journalPath);
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Injected crude oil, shore, power, oil research, construction items and coal. Oil chemistry cell test, not a campaign." }), token);
            Require(mark.Ok, "Fixture marker rejected.");
            string preparation = remoteWater ? Prepare.Replace("x>=30", "x<=-104", StringComparison.Ordinal)
                .Replace("pipe=150", "pipe=300", StringComparison.Ordinal)
                .Replace("area={{-112,-48},{48,48}}", "area={{-112,-96},{96,96}}", StringComparison.Ordinal)
                .Replace("for x=-112,48", "for x=-112,96", StringComparison.Ordinal)
                .Replace("for y=-48,48", "for y=-96,96", StringComparison.Ordinal) : Prepare;
            // The occupied source, the independent relay intake and its destructive repair each require one pump.
            if (steamWater) preparation = preparation.Replace("['offshore-pump']=1", "['offshore-pump']=3,boiler=1,['steam-engine']=1", StringComparison.Ordinal)
                .Replace("inserter=4", "inserter=5", StringComparison.Ordinal);
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(preparation + Statistics, token));
            var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
            // The fixture area was just emptied: the registry only keeps the planned band.
            await new FactoryRegistry(session.Directory).SaveAsync(new FactoryState(1, catalog.Scope.WorldId, [Band], []), token);
            var before = setup.RootElement.Clone();
            MapPosition? remotePumpPosition = remoteWater ? new(-99.5, 12.5) : null;
            evidence.Add(new { check = "explicit-oil-preparation", item, remoteWater, steamWater, band = Band.Box, native = before });
            Require(before.GetProperty("refineryCycles").GetInt64() == 0 && before.GetProperty("chemicalCycles").GetInt64() == 0,
                "The fixture must start without refineries or chemical plants.");
            if (item == "sulfur" && !remoteWater)
            {
                // Reproduce an already known water supply far from the oil. Sulfur must anchor at gas and pump
                // fresh water nearby rather than selecting this water first because of native recipe order.
                const string remoteWater = """
                    /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local tank=s.create_entity{name='storage-tank',position={-76,10},force=f}; assert(tank); tank.fluidbox[1]={name='water',amount=1000}; rcon.print(helpers.table_to_json{tick=game.tick,entityId=tostring(tank.unit_number),position=tank.position,fixtureWater=1000})
                    """;
                using var water = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(remoteWater, token));
                evidence.Add(new { check = "explicit-remote-water-supply", native = water.RootElement.Clone() });
            }
            // Standing beside the injected source makes its network known, as building it would; then the actor walks to the oil.
            var source = CellPowerLinker.NearestFedPole(await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token), Deposit);
            evidence.Add(new { check = "remote-power-source", source, deposit = Deposit, distance = source?.DistanceTo(Deposit), captureRadius = 48 });
            Require(source == SourcePole && source.DistanceTo(Deposit) > 60,
                "The known fed network must be the injected pole, over 60 tiles from the deposit.");
            SteamPowerResult? steam = null;
            if (steamWater)
            {
                await using (var approach = new SpatialController(game, journal))
                    await approach.TravelAsync(remotePumpPosition!, 6, catalog, token);
                steam = await new SteamPowerController(game, journal).RunAsync(token);
                var bridges = await JoinSteamNetworkAsync(game, journal, catalog, steam.Entities["pole"], token);
                var steamItems = await new PowerExpansionController(game, journal, session.Directory).SteamItemsAsync(catalog, token);
                Require(steamItems is not null, "The native steam installation is not part of the main network.");
                var steamMap = await new SpatialClient(game).CaptureAsync(steamItems!.All.Concat(["pipe"]).ToArray(), 48, token);
                var steamStock = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                var ground = new FactoryGround(await LoadAsync(), steamItems);
                var reserved = ground.Boxes(steamMap);
                Require(reserved.Count > 1, "The steam installation has no native future expansion footprints.");
                Require(FluidRelayController.WithdrawableBoxes(steamMap, steamStock, steam.Entities["pump"], "water").Count == 0,
                    "The existing steam pump must already be connected to its boiler.");
                var boxes = FluidRelayController.WithdrawableBoxes(steamMap, steamStock, steam.Entities["boiler"], "water");
                Require(boxes.Count > 0 && new FluidRelayPlanner().Find(FactoryGround.Reserve(steamMap, reserved, "pipe"),
                    "pipe", steam.Entities["boiler"], "water", Deposit, sourceBoxIndices: boxes) is null,
                    "The boiler's free water port must be blocked by its reserved native expansion.");
                evidence.Add(new { check = "occupied-steam-source-with-reserved-growth", steam, bridges, boxes, reserved,
                    stock = steamStock.FluidStockAt(steam.Entities["boiler"], "water"), steamMap.CollectedTick });
            }
            await using (var walker = new SpatialController(game, journal))
            {
                if (remotePumpPosition is not null && !steamWater)
                {
                    // Construct the prepared source through the same native geometry/placement path used by the agent.
                    // Script can_place_entity alone allowed a pump whose intake pointed at dry land in fixture75.
                    await walker.TravelAsync(remotePumpPosition, 6, catalog, token);
                    var waterMap = await new SpatialClient(game).CaptureAsync(["pipe"], 48, token);
                    var endpoint = new PlacementPlanner().FindCandidates(new(waterMap), "pipe", remotePumpPosition, requireBuildReach: false).First();
                    string endpointId = await new PoweredMachineController(game, journal).BuildAtAsync("pipe", endpoint, catalog, walker, token);
                    await new OffshoreSupplyController(game, journal).ConnectAsync(endpointId, "water", catalog, walker, token);
                    var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                    evidence.Add(new { check = "distant-pump-built-and-observed-by-character", known.CollectedTick, endpointId,
                        position = endpoint.Position, injectedWater = 0, amount = known.FluidStockAt(endpointId, "water"),
                        sources = FluidRelayController.Sources(known, "water").ToArray() });
                    Require(known.FluidStockAt(endpointId, "water") > 0, "The native prepared source has not supplied its observed outlet.");
                }
                await walker.TravelAsync(new(0, 0), 4, catalog, token);
            }

            var plan = await new FactoryDirector(game, journal, session.Directory).AutomateAsync(item, 12, token);
            var state = await LoadAsync();
            evidence.Add(new { check = "fluid-chain-built", plan, cells = state.Cells });
            var extractor = state.Cells.SingleOrDefault(c => c.Kind == FluidCellBuilder.ExtractorKind && c.Status == "ready" && c.Recipe == "crude-oil");
            var refinery = state.Cells.SingleOrDefault(c => c.Kind == FluidCellBuilder.MachineKind && c.Status == "ready" && c.Recipe == "basic-oil-processing");
            var chemical = state.Cells.SingleOrDefault(c => c.Kind == FluidCellBuilder.MachineKind && c.Status == "ready" && c.Recipe == item);
            Require(extractor is not null && refinery is not null && chemical is not null, "The extractor, refinery and chemical cells were not all completed.");
            Require(new[] { extractor!, refinery!, chemical! }.All(c => c.Plan is not null && c.Entities.Keys.All(c.Plan.ContainsKey)),
                "A fluid cell role lacks the plan maintenance needs to rebuild it.");
            Require(refinery!.Entities.Keys.Any(r => r.StartsWith("pipe-", StringComparison.Ordinal))
                && chemical!.Entities.Keys.Any(r => r.StartsWith("pipe-", StringComparison.Ordinal)), "The fluid routes were not registered with their cells.");
            if (remoteWater)
            {
                var relays = state.Cells.Where(c => c.Kind == FluidRelayController.Kind && c.Status == "ready" && c.Recipe == "water").ToArray();
                evidence.Add(new { check = "distant-water-relays", cells = relays, chemical = chemical!.Plan!["machine"].Position });
                Require(relays.Length >= 3 && relays.All(c => c.FluidRoute is not null && c.Plan is not null && c.Entities.ContainsKey("outlet")),
                    "The distant water source was not extended through persistent native pipe sections.");
                Require(!chemical.Entities.ContainsKey("pump"), "The remote test built an unplanned local pump instead of extending the known source.");
                if (steamWater)
                {
                    var intake = relays.SingleOrDefault(c => c.Entities.ContainsKey("pump"))
                        ?? throw new InvalidDataException("No persistent dedicated water intake was registered.");
                    var originalSteam = steam ?? throw new InvalidDataException("No original steam installation was recorded.");
                    Require(intake.Entities["pump"] != originalSteam.Entities["pump"],
                        "No separate persistent intake was built around the occupied steam installation.");
                    await using (var walker = new SpatialController(game, journal))
                        await walker.TravelAsync(intake!.Plan!["pump"].Position, 6, catalog, token);
                    var pumpMap = await new SpatialClient(game).CaptureAsync(["offshore-pump"], 48, token);
                    var watered = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                    Require(pumpMap.Entities.Single(e => e.Id == originalSteam.Entities["pump"]).FluidConnections!
                        .Any(p => p.TargetEntityId == originalSteam.Entities["boiler"]), "The relay changed the original steam intake connection.");
                    Require(watered.FluidStockAt(originalSteam.Entities["boiler"], "water") > 0 && watered.FluidStockAt(intake.Entities["outlet"], "water") > 0,
                        "The native steam and chemical supplies are not both watered.");
                    evidence.Add(new { check = "separate-water-intake-preserves-steam", intake, original = originalSteam.Entities,
                        boilerWater = watered.FluidStockAt(originalSteam.Entities["boiler"], "water"), outletWater = watered.FluidStockAt(intake.Entities["outlet"], "water"),
                        watered.CollectedTick });
                }
            }
            var linked = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            string[] links = extractor!.Entities.Keys.Where(r => r.StartsWith("link-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
            var onBand = state.Cells.Where(c => c.Plan is not null).SelectMany(c => c.Plan!.Values.Where(p => Band.Box.Contains(p.Position))
                .Select(p => new { cell = c.Id, p.Role, p.Position })).ToArray();
            evidence.Add(new { check = "remote-extractor-power", links, drillFed = FactoryPower.IsFed(linked, extractor.Entities["drill"]),
                linkPlans = links.Select(r => extractor.Plan![r].Position), onBand });
            Require(links.Length > 0 && FactoryPower.IsFed(linked, extractor.Entities["drill"]) == true,
                "The remote extractor was not linked to the fed network by its own link poles.");
            Require(onBand.Length == 0, "A fluid cell part or link stands on the planned band.");

            var logistics = new FactoryLogistics(game, journal, session.Directory);
            var first = await logistics.ServiceAsync(40, token);
            await WaitAsync(3600);
            var second = await logistics.ServiceAsync(40, token);
            evidence.Add(new { check = "chemical-logistics", first, second });
            Require(second.Collected.GetValueOrDefault(item) > 0, $"No {item} reached the chemical cell's output chest.");

            // Collection stops when the actor's per-item carrying cap is full. Store the preceding output so that
            // this repair check measures new production and delivery, rather than the remaining carrying capacity.
            await using (var walker = new SpatialController(game, journal))
            {
                int carried = checked((int)(await new ProductionController(game, journal).ObserveAsync(token)).Inventory.GetValueOrDefault(item));
                Require(carried > 0, "No carried product is available for the explicit fixture storage phase.");
                await walker.TravelAsync(chemical!.Plan!["machine"].Position, 6, catalog, token);
                var storageMap = await new SpatialClient(game).CaptureAsync(["iron-chest"], 48, token);
                var storage = new PlacementPlanner().FindCandidates(new(storageMap), "iron-chest", chemical.Plan["machine"].Position,
                    requireBuildReach: false).First();
                string storageId = await new PoweredMachineController(game, journal).BuildAtAsync("iron-chest", storage, catalog, walker, token);
                await walker.TravelAsync(storage.Position, 3, catalog, token);
                var deposited = await walker.WorkAsync("insert", new { entityId = storageId, inventory = "chest", item, count = carried }, 600, token: token);
                Require(deposited.Status == "completed" && deposited.Effects.GetProperty("transferred").GetInt32() == carried,
                    "The preceding chemical output was not deposited in the explicit fixture storage chest.");
                evidence.Add(new { check = "chemical-output-stored-before-repair", storageId, position = storage.Position, item, carried,
                    deposited, remainingCarried = (await new ProductionController(game, journal).ObserveAsync(token)).Inventory.GetValueOrDefault(item) });
            }
            long cyclesBeforeCut = (await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token)).Records
                .Single(r => r.Kind == "work" && r.EntityId == chemical!.Entities["machine"]).Data.GetProperty("productsFinished").GetInt64();

            // A destroyed gas pipe is rebuilt at its planned tile by the next logistics round.
            string lost = chemical!.Entities.Where(p => p.Key.StartsWith("pipe-", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal).First().Value;
            using var destroyed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(DestroyPipe.Replace("PIPE_ID", lost, StringComparison.Ordinal), token));
            var repair = await logistics.ServiceAsync(40, token);
            var repaired = (await LoadAsync()).Cells.Single(c => c.Id == chemical.Id);
            await WaitAsync(1800);
            var third = await logistics.ServiceAsync(40, token);
            long cyclesAfterRepair = (await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token)).Records
                .Single(r => r.Kind == "work" && r.EntityId == chemical.Entities["machine"]).Data.GetProperty("productsFinished").GetInt64();
            evidence.Add(new { check = "destroyed-pipe-rebuilt", lost, native = destroyed.RootElement.Clone(), repair, repaired, third,
                cyclesBeforeCut, cyclesAfterRepair });
            Require(repair.Maintenance?.Rebuilt.Count == 1 && !repaired.Entities.Values.Contains(lost)
                && repaired.Entities.Values.Contains(repair.Maintenance.Rebuilt[0]), "Maintenance did not rebuild the destroyed pipe in place.");
            Require(third.Collected.GetValueOrDefault(item) > 0, $"The chain stopped delivering {item} after the pipe repair.");
            Require(cyclesAfterRepair > cyclesBeforeCut, "The repaired chemical chain has no new native production cycles.");

            if (remoteWater)
            {
                // Reopen one section as an interrupted build, omit an applied receipt and destroy a different registered pipe.
                // Resumption must adopt the standing pipe and rebuild exactly the lost part without adding another section.
                var relayState = await LoadAsync();
                var relay = relayState.Cells.First(c => c.Kind == FluidRelayController.Kind && c.Status == "ready");
                var pipeRoles = relay.Entities.Where(p => p.Key.StartsWith("pipe-", StringComparison.Ordinal)).ToArray();
                var applied = pipeRoles.First();
                var cutRelay = pipeRoles.Last();
                using var relayCut = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(DestroyPipe.Replace("PIPE_ID", cutRelay.Value, StringComparison.Ordinal), token));
                var open = relay with { Status = "building", Entities = relay.Entities.Where(p => p.Key != applied.Key
                    && (!steamWater || p.Key != "pump")).ToDictionary() };
                await new FactoryRegistry(session.Directory).SaveAsync(relayState.With(open), token);
                await using (var walker = new SpatialController(game, journal))
                {
                    await walker.TravelAsync(SourcePole, 6, catalog, token);
                    await new FluidRelayController(game, journal, session.Directory).ExtendAsync("water", chemical.Plan!["machine"].Position,
                        catalog, walker, new FactoryGround(relayState, null), token);
                }
                var resumedState = await LoadAsync();
                var resumedRelay = resumedState.Cells.Single(c => c.Id == relay.Id);
                evidence.Add(new { check = "interrupted-water-relay-resumes", relay.Id, applied, cutRelay,
                    native = relayCut.RootElement.Clone(), resumed = resumedRelay, beforeCount = relayState.Cells.Count, afterCount = resumedState.Cells.Count });
                Require(resumedRelay.Status == "ready" && resumedRelay.Entities[applied.Key] == applied.Value
                    && resumedRelay.Entities[cutRelay.Key] != cutRelay.Value && resumedState.Cells.Count == relayState.Cells.Count,
                    "The interrupted water relay did not adopt its applied receipt and restore its original missing pipe.");
                if (steamWater)
                {
                    Require(resumedRelay.Entities["pump"] == relay.Entities["pump"], "The standing but unrecorded intake was not adopted.");
                    string lostPump = resumedRelay.Entities["pump"];
                    using var cutPump = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(DestroyPump.Replace("PUMP_ID", lostPump, StringComparison.Ordinal), token));
                    var pumpRepair = await logistics.ServiceAsync(40, token);
                    var repairedRelay = (await LoadAsync()).Cells.Single(c => c.Id == relay.Id);
                    await using (var walker = new SpatialController(game, journal))
                    {
                        await new FactoryRegistry(session.Directory).SaveAsync((await LoadAsync()).With(repairedRelay with { Status = "building" }), token);
                        await new FluidRelayController(game, journal, session.Directory).ExtendAsync("water", chemical.Plan!["machine"].Position,
                            catalog, walker, new FactoryGround(await LoadAsync(), null), token);
                    }
                    var rebound = (await LoadAsync()).Cells.Single(c => c.Id == relay.Id);
                    var restored = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
                    evidence.Add(new { check = "dedicated-intake-rebuilt-and-rebound", lostPump, native = cutPump.RootElement.Clone(), pumpRepair,
                        rebound, amount = restored.FluidStockAt(rebound.Entities["outlet"], "water") });
                    Require(pumpRepair.Maintenance?.Rebuilt.Contains(rebound.Entities["pump"]) == true && rebound.Entities["pump"] != lostPump
                        && rebound.FluidRoute!.Source!.EntityId == rebound.Entities["pump"] && restored.FluidStockAt(rebound.Entities["outlet"], "water") > 0,
                        "The destroyed intake was not rebuilt and rebound at its persistent plan.");
                }
            }

            // An interrupted build resumes from afar: the fixture reopens the chemical cell as if its build had stopped, destroys
            // one of its pipes meanwhile, and walks the actor back to the source, beyond any capture around the cell.
            var reopened = await LoadAsync();
            var interrupted = reopened.Cells.Single(c => c.Id == chemical.Id) with { Status = "building", Attempts = 1 };
            await new FactoryRegistry(session.Directory).SaveAsync(reopened.With(interrupted), token);
            var (cutRole, cut) = interrupted.Entities.Where(p => p.Key.StartsWith("pipe-", StringComparison.Ordinal))
                .OrderBy(p => p.Key, StringComparer.Ordinal).Last();
            using var severed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(DestroyPipe.Replace("PIPE_ID", cut, StringComparison.Ordinal), token));
            await using (var walker = new SpatialController(game, journal)) await walker.TravelAsync(new(-78, 4), 4, catalog, token);
            var machineAt = interrupted.Plan!["machine"].Position;
            double away = (await new SpatialClient(game).CaptureAsync(radius: 4, cancellationToken: token)).Actor.Position.DistanceTo(machineAt);
            var resumed = await new FluidCellBuilder(game, journal, session.Directory).BuildMachineAsync(interrupted.MachineItem, interrupted.Recipe!, token);
            var standing = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            string? rebuilt = resumed.Entities.GetValueOrDefault(cutRole);
            var rebuiltAt = standing.Records.SingleOrDefault(r => r.Kind == "entity" && r.EntityId == rebuilt)?
                .Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json);
            evidence.Add(new { check = "interrupted-cell-resumed", cutRole, cut, native = severed.RootElement.Clone(), away, rebuilt, rebuiltAt, resumed });
            Require(away > 48, "The actor must resume the cell from beyond a capture around it.");
            Require(resumed.Id == chemical.Id && resumed.Status == "ready" && resumed.Attempts == 2 && rebuilt is not null && rebuilt != cut
                && rebuiltAt == interrupted.Plan[cutRole].Position && resumed.Entities.Keys.Order().SequenceEqual(interrupted.Entities.Keys.Order()),
                "The interrupted cell did not resume from afar with its lost pipe rebuilt at its plan.");

            using var after = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync("/silent-command " + Statistics, token));
            var native = after.RootElement.Clone();
            // Force statistics are cumulative; only this run's growth counts.
            double Delta(string name) => native.GetProperty(name).GetDouble() - before.GetProperty(name).GetDouble();
            string[] inputChests = chemical.Entities.TryGetValue("input-chest", out var chest) ? [chest] : [];
            var rows = (await File.ReadAllLinesAsync(journalPath, token)).Select(l => JsonDocument.Parse(l)).ToArray();
            try
            {
                var submissions = rows.Where(r => r.RootElement.GetProperty("type").GetString() == "submission")
                    .Select(r => r.RootElement.GetProperty("data")).ToArray();
                string[] crafted = submissions.Where(s => s.GetProperty("kind").GetString() == "craft")
                    .Select(s => s.GetProperty("args").GetProperty("recipe").GetString()!).ToArray();
                int mines = submissions.Count(s => s.GetProperty("kind").GetString() == "mine");
                long delivered = submissions.Where(s => s.GetProperty("kind").GetString() == "insert"
                        && inputChests.Contains(s.GetProperty("args").GetProperty("entityId").GetString()))
                    .Sum(s => s.GetProperty("args").GetProperty("count").GetInt64());
                var growth = new[] { "crudeProduced", "crudeConsumed", "gasProduced", "gasConsumed", "waterConsumed", "plasticProduced",
                    "sulfurProduced", "coalConsumed" }.ToDictionary(n => n, Delta);
                evidence.Add(new { check = "native-oil-chemistry", native, growth, crafted, mines, deliveredToInputChest = delivered });
                Require(growth["crudeProduced"] > 0 && growth["crudeConsumed"] > 0, "The pumpjack did not extract crude oil that the refinery consumed.");
                Require(growth["gasProduced"] > 0 && native.GetProperty("refineryCycles").GetInt64() > 0, "The refinery did not produce petroleum gas natively.");
                Require(growth[item == "plastic-bar" ? "plasticProduced" : "sulfurProduced"] > 0 && native.GetProperty("chemicalCycles").GetInt64() > 0,
                    $"The chemical plant did not produce {item} natively.");
                Require(crafted.All(r => r != item), $"{item} was crafted by hand.");
                Require(mines == 0, "The prepared chain triggered manual mining.");
                if (item == "plastic-bar")
                    // Startup may already have filled the chest before the first full logistics tour.
                    Require(delivered > 0 && growth["coalConsumed"] > 0,
                        "Logistics did not deliver the coal the chemical plant consumed.");
                else
                    Require(growth["waterConsumed"] > 0 && (remoteWater || chemical.Entities.ContainsKey("pump")),
                        "The sulfur cell did not draw native water from its planned source.");
            }
            finally { foreach (var row in rows) row.Dispose(); }
            passed = true;
            return path;
        }
        finally
        {
            await LocalJson.WriteAsync(path, new { kind = "prepared-oil-chemistry-qualification", item, passed, isAutonomousCampaign = false, journalPath, evidence },
                CancellationToken.None);
        }

        async Task WaitAsync(int ticks)
        {
            await using var controller = new SpatialController(game, journal);
            Require((await controller.WorkAsync("wait", new { ticks }, ticks + 300, token: token)).Status == "completed", "Wait failed.");
        }

        async Task<FactoryState> LoadAsync()
        {
            var observed = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), token);
            var scope = observed.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
            return await new FactoryRegistry(session.Directory).LoadAsync(scope.WorldId, token);
        }
    }

    private static async Task<IReadOnlyList<string>> JoinSteamNetworkAsync(IGameClient game, IControllerJournal journal,
        ProductionCatalog catalog, string steamPoleId, CancellationToken token)
    {
        var bridges = new List<string>();
        await using var walker = new SpatialController(game, journal);
        await walker.TravelAsync(SourcePole, 6, catalog, token);
        for (int step = 0; step < 16; step++)
        {
            var map = await new SpatialClient(game).CaptureAsync(["small-electric-pole"], 48, token);
            var source = map.Entities.Single(e => e.Name == "small-electric-pole" && e.Position == SourcePole);
            var target = map.Entities.Single(e => e.Id == steamPoleId);
            if (source.Power?.NetworkId is { } network && network == target.Power?.NetworkId) return bridges;
            var next = new PowerGridPlanner().Next(map, "small-electric-pole", target.Bounds,
                bridges.Append(source.Id).ToHashSet(StringComparer.Ordinal), token);
            Require(next.Status == PowerGridSearchStatus.Extension && next.Pole is not null, "No observed native pole route joins the fixture's two networks.");
            bridges.Add(await new PoweredMachineController(game, journal).BuildAtAsync("small-electric-pole", next.Pole!, catalog, walker, token));
        }
        throw new InvalidDataException("The fixture network merge exhausted its bounded pole budget.");
    }

    // Crude oil about 95 tiles east of the injected power, where the actor starts, and a shore beyond the oil; exactly the
    // chain's machines, generous pipes, poles and coal.
    internal const string Prepare = """
        /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); game.speed=1; for _,e in pairs(s.find_entities_filtered{area={{-112,-48},{48,48}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-112,48 do for y=-48,48 do tiles[#tiles+1]={name=(x>=30 and 'water' or 'grass-1'),position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({-78,4})); c.health=c.max_health; c.get_main_inventory().clear(); for _,t in pairs{'steam-power','electronics','automation','oil-gathering','oil-processing','plastics','sulfur-processing'} do f.technologies[t].researched=true end; for name,count in pairs{pumpjack=1,['oil-refinery']=1,['chemical-plant']=1,['offshore-pump']=1,pipe=150,inserter=4,['iron-chest']=4,['small-electric-pole']=60,coal=100} do assert(c.insert{name=name,count=count}==count) end; local source=s.create_entity{name='electric-energy-interface',position={-84,0},force=f}; assert(source); source.electric_buffer_size=1000000000; source.power_production=3000000; source.energy=1000000000; assert(s.create_entity{name='small-electric-pole',position={-82.5,0.5},force=f}); assert(s.create_entity{name='crude-oil',position={12,6},amount=600000});
        """;

    private const string DestroyPipe = """
        /silent-command local e; for _,p in pairs(game.surfaces.nauvis.find_entities_filtered{type='pipe',force=game.forces.factorio_agent}) do if tostring(p.unit_number)=='PIPE_ID' then e=p; break end end; assert(e and e.valid); local at=e.position; e.destroy(); rcon.print(helpers.table_to_json{tick=game.tick,x=at.x,y=at.y})
        """;

    private const string DestroyPump = """
        /silent-command local e; for _,p in pairs(game.surfaces.nauvis.find_entities_filtered{type='offshore-pump',force=game.forces.factorio_agent}) do if tostring(p.unit_number)=='PUMP_ID' then e=p; break end end; assert(e and e.valid); local at=e.position; e.destroy(); rcon.print(helpers.table_to_json{tick=game.tick,x=at.x,y=at.y})
        """;

    // Cumulative force statistics and the cycles of every refinery and chemical plant; appended to a silent command.
    private const string Statistics = """
        local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local fs=f.get_fluid_production_statistics(s); local is=f.get_item_production_statistics(s); local r={tick=game.tick,crudeProduced=fs.get_input_count('crude-oil'),crudeConsumed=fs.get_output_count('crude-oil'),gasProduced=fs.get_input_count('petroleum-gas'),gasConsumed=fs.get_output_count('petroleum-gas'),waterConsumed=fs.get_output_count('water'),plasticProduced=is.get_input_count('plastic-bar'),sulfurProduced=is.get_input_count('sulfur'),coalConsumed=is.get_output_count('coal'),refineryCycles=0,chemicalCycles=0,players=#game.connected_players}; for _,e in pairs(s.find_entities_filtered{name='oil-refinery',force=f}) do r.refineryCycles=r.refineryCycles+e.products_finished end; for _,e in pairs(s.find_entities_filtered{name='chemical-plant',force=f}) do r.chemicalCycles=r.chemicalCycles+e.products_finished end; rcon.print(helpers.table_to_json(r))
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
