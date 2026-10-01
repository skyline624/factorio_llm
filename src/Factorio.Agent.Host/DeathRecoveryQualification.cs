using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Factorio.Agent.Ollama;

namespace Factorio.Agent.Host;

/// <summary>
/// Prepared native death during an operation, automatic corpse recovery and subsequent real production. With
/// <c>dangerZone</c>, an inert biter beside the fresh corpse defers the recovery, which then runs once the zone expires.
/// </summary>
public sealed class DeathRecoveryQualification(RuntimeSession session, bool stationaryThreat = false, bool recoveryDeath = false,
    bool dangerZone = false)
{
    public async Task<string> RunAsync(CancellationToken token)
    {
        if (!session.IsFixture) throw new InvalidOperationException("Death recovery qualification requires an explicit fixture session.");
        if (new[] { stationaryThreat, recoveryDeath, dangerZone }.Count(selected => selected) > 1)
            throw new ArgumentException("Select one prepared death scenario at a time.");
        using var lease = ActorControlLease.Acquire(session.Directory);
        await using var game = session.CreateClient(lease);
        string id = Guid.NewGuid().ToString("N");
        string report = Path.Combine(session.Directory, $"death-recovery-qualification-{id}.json");
        string journalPath = Path.ChangeExtension(report, ".jsonl");
        string memoryPath = Path.Combine(session.Directory, $"death-recovery-memory-{id}.json");
        var evidence = new List<object>();
        bool passed = false;
        try
        {
            var initial = await game.ExecuteAsync(GameRequest.Create("observe"), token);
            if (!initial.Ok) throw new GameRpcException(initial.Error!);
            Require(initial.Data.GetProperty("goal").GetProperty("rocketsLaunched").GetInt32() == 0,
                "Death recovery requires a fixture without a previous rocket launch; use a fresh fixture.");
            var mark = await game.ExecuteAsync(GameRequest.Create("mark_fixture", new
            { reason = "Prepared stock and foreign corpse; native death during work, normal respawn and recovery. Not a campaign." }), token);
            if (!mark.Ok) throw new GameRpcException(mark.Error!);
            const string prepare = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c and c.crafting_queue_size==0); for _,p in pairs(game.connected_players) do assert(p.character==c) end; game.speed=4; for _,e in pairs(s.find_entities_filtered{force=f}) do if e~=c then e.destroy() end end; for _,e in pairs(s.find_entities_filtered{type='character-corpse'}) do e.destroy() end; for _,e in pairs(s.find_entities_filtered{area={{-64,-64},{64,64}}}) do if e~=c then e.destroy() end end; local tiles={}; for x=-64,64 do for y=-64,64 do tiles[#tiles+1]={name='grass-1',position={x,y}} end end; s.set_tiles(tiles); assert(c.teleport({12,8})); c.get_main_inventory().clear(); assert(c.insert{name='iron-plate',count=40}==40); assert(c.insert{name='coal',count=6}==6); local other=s.create_entity{name='character',position={15,8},force=game.forces.player}; assert(other and other.insert{name='iron-plate',count=3}==3); other.die(game.forces.enemy); rcon.print(helpers.table_to_json{tick=game.tick,characterId=tostring(c.unit_number),players=#game.connected_players})
                """;
            using var setup = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(prepare, token));
            evidence.Add(new { check = "explicit-preparation", native = setup.RootElement.Clone() });
            // The prepared area was emptied of every entity, enemies included: earlier fixture deaths no longer describe it.
            string dangerFile = new DangerZoneStore(session.Directory).Path;
            evidence.Add(new { check = "fixture-danger-memory-reset", forgotten = File.Exists(dangerFile) });
            File.Delete(dangerFile);
            if (recoveryDeath)
            {
                const string supply = """
                    /silent-command local s=game.surfaces.nauvis;local chest=s.create_entity{name='iron-chest',position={-5,8},force=game.forces.factorio_agent};assert(chest and chest.insert{name='iron-plate',count=40}==40);rcon.print(helpers.table_to_json{tick=game.tick,chest=tostring(chest.unit_number),preparedIron=40})
                    """;
                using var preparedSupply = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(supply, token));
                evidence.Add(new { check = "explicit-accessible-factory-stock", native = preparedSupply.RootElement.Clone() });
            }
            JsonElement before = await ReadAsync();
            var journal = new ControllerJournal(journalPath);
            var runner = new Runner(session, game, journal, stationaryThreat, recoveryDeath, dangerZone);
            var recovery = recoveryDeath ? new DyingRecovery(session, game, journal) : null;
            var result = await new StrategicCampaignController(game, runner, memoryPath, journalPath, recovery).RunAsync(dangerZone ? 3 : 2, token);
            JsonElement after = await ReadAsync();
            var memory = JsonSerializer.Deserialize<StrategicMemory>(await File.ReadAllTextAsync(memoryPath, token), Protocol.Json)!;
            int deaths = 0, recoveryTakes = 0, emptyRespawns = 0, deferrals = 0, retries = 0, submissionsWhileDeferred = 0;
            bool recovering = false;
            var inspections = new Dictionary<string, int>(StringComparer.Ordinal);
            JsonElement? lastStart = null;
            foreach (string line in await File.ReadAllLinesAsync(journalPath, token))
            {
                using var row = JsonDocument.Parse(line);
                string? type = row.RootElement.GetProperty("type").GetString();
                var data = row.RootElement.GetProperty("data");
                if (type == "fixture-native-death") deaths++;
                if (type == "submission" && data.GetProperty("kind").GetString() == "take"
                    && data.GetProperty("args").GetProperty("inventory").GetString() == "corpse") recoveryTakes++;
                if (type == "corpse-recovery-start" && !data.GetProperty("actorMainInventory").TryGetProperty("iron-plate", out _)) emptyRespawns++;
                if (type == "corpse-recovery-start") { lastStart = data.Clone(); recovering = true; }
                if (type == "danger-zone-inspection")
                    inspections[data.GetProperty("verdict").GetString()!] = inspections.GetValueOrDefault(data.GetProperty("verdict").GetString()!) + 1;
                if (type == "corpse-recovery-danger-deferral") deferrals++;
                // Between the deferral and its retry nothing may move toward the corpse.
                if (type == "submission" && recovering && dangerZone && retries == 0) submissionsWhileDeferred++;
                if (type == "deferred-recovery-retry") retries++;
            }
            var zones = await new DangerZoneStore(session.Directory).LoadAsync(session.ProposedWorldId, token);
            evidence.Add(new { check = "native-death-recovery-and-production", before, result, after, memory,
                deaths, recoveryTakes, emptyRespawns, recoveryCalls = recovery?.Calls, runner.RecoveryFeedbackObserved,
                inspections, deferrals, retries, submissionsWhileDeferred, lastStart, rememberedDeaths = zones.Deaths,
                runner.BiterId, runner.RetryTick, runner.Waited });
            Require(zones.Deaths.Count == (recoveryDeath ? 2 : 1), "The native deaths were not remembered as danger zones.");
            Require(dangerZone
                    ? inspections.GetValueOrDefault("occupied") == 1 && !inspections.ContainsKey("clear") && deferrals == 1 && retries == 1
                        && submissionsWhileDeferred == 0 && memory.Deferred is null && lastStart is { } retried
                        && retried.GetProperty("activeDeathZones").GetArrayLength() == 0
                    : inspections.GetValueOrDefault("clear") >= 1 && !inspections.ContainsKey("occupied") && deferrals == 0,
                "The fresh death zone was not inspected, deferred and retried after expiry as expected.");
            Require(after.GetProperty("characterId").GetString() != before.GetProperty("characterId").GetString()
                && after.GetProperty("incarnation").GetInt64() == before.GetProperty("incarnation").GetInt64() + (recoveryDeath ? 2 : 1)
                && after.GetProperty("deaths").GetInt64() == before.GetProperty("deaths").GetInt64() + (recoveryDeath ? 2 : 1)
                && after.GetProperty("worldId").GetString() == before.GetProperty("worldId").GetString(), "Native death and same-world respawn were not established.");
            // With deferred corpses the iron comes from the chest; collection may carry a whole stack, checked against the chest below.
            Require(after.GetProperty("gears").GetInt64() == 10 && (recoveryDeath || after.GetProperty("iron").GetInt64() == 20)
                && after.GetProperty("coal").GetInt64() == (recoveryDeath ? 0 : 6), "Recovered stock and subsequent native production do not reconcile.");
            Require(after.GetProperty("gearsProduced").GetInt64() - before.GetProperty("gearsProduced").GetInt64() == 10
                && after.GetProperty("ironConsumed").GetInt64() - before.GetProperty("ironConsumed").GetInt64() == 20,
                "Native gear recipe costs were not proven.");
            Require(after.GetProperty("corpseIron").GetInt64() == (recoveryDeath ? 43 : 3), "Native corpse iron does not reconcile.");
            if (recoveryDeath)
            {
                const string remainingSupply = """
                    /silent-command local chests=game.surfaces.nauvis.find_entities_filtered{type='container',force=game.forces.factorio_agent};assert(#chests==1);rcon.print(helpers.table_to_json{tick=game.tick,iron=chests[1].get_item_count('iron-plate')})
                    """;
                using var supplyProof = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(remainingSupply, token));
                Require(supplyProof.RootElement.GetProperty("iron").GetInt64() + after.GetProperty("iron").GetInt64() == 20,
                    "Accessible factory iron did not pay the native gear cost.");
                evidence.Add(new { check = "remaining-accessible-factory-stock", native = supplyProof.RootElement.Clone() });
            }
            Require(!memory.Pending && memory.Recovery is null && runner.RecoveryFeedbackObserved
                && deaths == (recoveryDeath ? 2 : 1) && (recoveryDeath ? recoveryTakes == 0 && recovery!.Calls == 1 : recoveryTakes > 0)
                && emptyRespawns == (dangerZone ? 2 : 1), "Strategic recovery did not complete or defer before the subsequent goal.");
            Require(after.GetProperty("players").GetInt32() == before.GetProperty("players").GetInt32()
                && after.GetProperty("pilotAttached").GetBoolean(), "The connected pilot did not follow the same native actor.");
            if (stationaryThreat)
            {
                var map = await new SpatialClient(game).CaptureAsync(cancellationToken: token);
                var threat = map.StationaryThreats!.Single(t => t.Id == runner.ThreatId);
                var observed = await game.ExecuteAsync(GameRequest.Create("observe"), token);
                Require(observed.Ok && observed.Data.GetProperty("agent").GetProperty("health").GetDouble() == 250
                    && map.Actor.Position.DistanceTo(threat.Position) >= threat.Range + 2,
                    "Recovery entered the prepared worm envelope or lost health.");
                evidence.Add(new { check = "stationary-threat-avoided", threat, map.Actor.Position, map.CollectedTick,
                    health = observed.Data.GetProperty("agent").GetProperty("health").GetDouble() });
            }
            passed = true;
            return report;
        }
        finally
        {
            await LocalJson.WriteAsync(report, new { kind = "prepared-death-recovery-qualification", passed,
                isAutonomousCampaign = false, stationaryThreat, recoveryDeath, dangerZone, journalPath, memoryPath, evidence }, CancellationToken.None);
        }

        async Task<JsonElement> ReadAsync()
        {
            const string command = """
                /silent-command local s=game.surfaces.nauvis; local f=game.forces.factorio_agent; local c=s.find_entities_filtered{type='character',force=f}[1]; assert(c); local o=helpers.json_to_table(remote.call('factorio_agent','execute',helpers.table_to_json{protocolVersion=1,requestId='death-proof',action='observe',arguments={radius=1,limit=1}})); assert(o.ok); local corpses=0; for _,e in pairs(s.find_entities_filtered{type='character-corpse'}) do corpses=corpses+e.get_inventory(defines.inventory.character_corpse).get_item_count('iron-plate') end; local attached=true; for _,p in pairs(game.connected_players) do if p.character~=c then attached=false end end; local stats=f.get_item_production_statistics(s); rcon.print(helpers.table_to_json{tick=game.tick,characterId=tostring(c.unit_number),worldId=o.data.scope.worldId,incarnation=o.data.scope.incarnation,deaths=o.data.agent.deaths,iron=c.get_item_count('iron-plate'),coal=c.get_item_count('coal'),gears=c.get_item_count('iron-gear-wheel'),corpseIron=corpses,gearsProduced=stats.get_input_count('iron-gear-wheel'),ironConsumed=stats.get_output_count('iron-plate'),players=#game.connected_players,pilotAttached=attached})
                """;
            using var value = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(command, token));
            return value.RootElement.Clone();
        }
    }

    private sealed class Runner(RuntimeSession session, IGameClient game, ControllerJournal journal, bool stationaryThreat, bool recoveryDeath,
        bool dangerZone) : IStrategicGoalRunner
    {
        private bool killed;
        public bool RecoveryFeedbackObserved { get; private set; }
        public string? ThreatId { get; private set; }
        public string? BiterId { get; private set; }
        public long? RetryTick { get; private set; }
        public object? Waited { get; private set; }
        public async Task<StrategicGoalResult> RunOnceAsync(CancellationToken token = default, string? previousResult = null)
        {
            var observed = await game.ExecuteAsync(GameRequest.Create("observe"), token);
            Require(observed.Ok, "Fixture actor observation rejected.");
            var scope = observed.Data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!;
            await journal.AppendAsync("strategic-context", new
                { facts = JsonSerializer.Serialize(new { observedTick = observed.Tick }, Protocol.Json) }, token);
            await journal.AppendAsync("strategic-goal", new { category = GoalCategory.Production, target = "iron-gear-wheel", quantity = 10, unit = GoalUnit.Items }, token);
            if (!killed)
            {
                killed = true;
                using var movementDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                movementDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                var tracked = new TrackingJournal(journal);
                await using var spatial = new SpatialController(game, tracked);
                Task<NavigationResult> navigating = spatial.NavigateAsync(new(30, 8), cancellationToken: movementDeadline.Token);
                try
                {
                    string? movingId = null;
                    while (!navigating.IsCompleted)
                    {
                        var current = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), movementDeadline.Token);
                        Require(current.Ok, "Movement observation rejected.");
                        if (current.Data.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.Object
                            && operation.GetProperty("status").GetString() is "running" or "accepted"
                            && operation.GetProperty("kind").GetString() == "move"
                            && tracked.Submissions.Any(s => s.OperationId == operation.GetProperty("operationId").GetString()))
                        {
                            movingId = operation.GetProperty("operationId").GetString();
                            break;
                        }
                        await Task.Delay(10, movementDeadline.Token);
                    }
                    Require(movingId is not null, "Prepared death requires an observed active C# navigation operation.");
                    string kill = "/silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1]; assert(c); local position=c.position; local surface=c.surface; c.die(game.forces.enemy); ";
                    kill += stationaryThreat
                        ? "game.speed=1; local worm=surface.create_entity{name='medium-worm-turret',position={position.x+24,position.y},force=game.forces.enemy}; assert(worm); rcon.print(helpers.table_to_json{dead=true,threatId=tostring(worm.unit_number)})"
                        // An inert biter eight tiles from the fresh corpse: visible to the normal observation, unable to attack.
                        : dangerZone
                        ? "local p=surface.find_non_colliding_position('small-biter',{position.x+8,position.y},8,0.5); assert(p); local b=surface.create_entity{name='small-biter',position=p,force=game.forces.enemy}; assert(b); b.disabled_by_script=true; assert(b.disabled_by_script); rcon.print(helpers.table_to_json{dead=true,biterId=tostring(b.unit_number),biter=b.position,death=position,tick=game.tick})"
                        : "rcon.print(helpers.table_to_json{dead=true})";
                    using var killedResponse = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(kill, token));
                    Require(killedResponse.RootElement.GetProperty("dead").GetBoolean(), "Prepared native death not acknowledged.");
                    ThreatId = killedResponse.RootElement.TryGetProperty("threatId", out var threatId) ? threatId.GetString() : null;
                    BiterId = killedResponse.RootElement.TryGetProperty("biterId", out var biterId) ? biterId.GetString() : null;
                    if (BiterId is not null) await journal.AppendAsync("fixture-inert-biter", killedResponse.RootElement.Clone(), token);
                    await journal.AppendAsync("fixture-native-death", new { scope, operationId = movingId }, token);
                    bool invalidated = false;
                    try { await navigating; }
                    catch (InvalidDataException) { invalidated = true; }
                    Require(invalidated && tracked.Submissions.All(s => s.Scope == scope),
                        "Navigation continued its old intent after native death or dispatched for another incarnation.");
                    await journal.AppendAsync("fixture-navigation-invalidated", new { scope, movingId, submissions = tracked.Submissions.Count }, token);
                    throw new InvalidOperationException("Prepared native death interrupted navigation; recovery must choose fresh actions.");
                }
                finally
                {
                    movementDeadline.Cancel();
                    try { await navigating; } catch (Exception) { }
                }
            }
            RecoveryFeedbackObserved = previousResult?.Contains("death-recovery-observed", StringComparison.Ordinal) == true;
            Require(RecoveryFeedbackObserved, "Next goal did not receive the reconciled recovery outcome.");
            if (dangerZone && RetryTick is null) return await WaitForZoneExpiryAsync(previousResult!, token);
            if (dangerZone)
            {
                // An emptied character corpse vanishes natively, so a complete collection may end without a surviving corpse.
                using var retried = JsonDocument.Parse(previousResult!);
                Require(retried.RootElement.GetProperty("recoveryOutcome").GetString() is "collected" or "no-surviving-corpse"
                    && retried.RootElement.GetProperty("collectedThisAttempt").GetProperty("iron-plate").GetInt64() == 40
                    && retried.RootElement.GetProperty("remainingItemTypes").GetInt32() == 0,
                    "The recovery retried after the zone expired did not collect the corpse stock.");
            }
            if (recoveryDeath)
            {
                using var feedback = JsonDocument.Parse(previousResult!);
                Require(feedback.RootElement.GetProperty("recoveryOutcome").GetString() == "unsafe-corpses-deferred"
                    && feedback.RootElement.GetProperty("remaining").GetProperty("iron-plate").GetInt64() == 40
                    && feedback.RootElement.GetProperty("collectedItemTypes").GetInt32() == 0,
                    "The next decision did not receive the exact deferred corpse stock.");
            }
            var production = await new ProductionGoalExecutor(game, journal).RunAsync("iron-gear-wheel", 10, token);
            return new(new("fixture", "Native production after corpse recovery", GoalCategory.Production, "iron-gear-wheel", 10,
                GoalUnit.Items, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null)), Production: production);
        }

        // The deferral is checked, the fixture removes the biter, and game time passes at an explicit speed until the zone expires.
        private async Task<StrategicGoalResult> WaitForZoneExpiryAsync(string previousResult, CancellationToken token)
        {
            using (var deferred = JsonDocument.Parse(previousResult))
            {
                var root = deferred.RootElement;
                Require(root.GetProperty("recoveryOutcome").GetString() == "unsafe-corpses-deferred"
                    && root.GetProperty("remaining").GetProperty("iron-plate").GetInt64() == 40
                    && root.GetProperty("collectedItemTypes").GetInt32() == 0
                    && root.GetProperty("automaticRetryTick").ValueKind == JsonValueKind.Number,
                    "The fresh death zone did not defer the recovery with an automatic retry tick.");
                RetryTick = root.GetProperty("automaticRetryTick").GetInt64();
            }
            if (BiterId is null || !long.TryParse(BiterId, out long biter)) throw new InvalidDataException("The prepared biter has no native identity.");
            // Units lack the get-by-unit-number flag: the prepared biter is found by its native number in the fixture area.
            using var cleared = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(
                $"/silent-command local b; for _,e in pairs(game.surfaces.nauvis.find_entities_filtered{{type='unit',area={{{{-96,-96}},{{96,96}}}}}}) do if e.unit_number=={biter} then b=e end end; assert(b and b.valid); b.destroy(); game.speed=32; rcon.print(helpers.table_to_json{{tick=game.tick,speed=game.speed}})",
                token));
            long start = cleared.RootElement.GetProperty("tick").GetInt64(), now = start;
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                while (now < RetryTick)
                {
                    await Task.Delay(500, budget.Token);
                    var observed = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), budget.Token);
                    Require(observed.Ok, "Observation failed while the death zone expired.");
                    now = observed.Tick;
                }
            }
            finally { await session.CreateRcon().ExecuteAsync("/silent-command game.speed=4", CancellationToken.None); }
            Waited = new { BiterId, RetryTick, startTick = start, endTick = now, fixtureSpeed = 32 };
            await journal.AppendAsync("fixture-danger-zone-wait", Waited, token);
            return new(new("fixture", "Wait until the death zone expires", GoalCategory.Research, "danger-zone-expiry", 1,
                GoalUnit.Completion, GoalPriority.Normal, new(TimeSpan.Zero, 1, null, null, null)),
                UnsupportedReason: "Fixture wait: the fixture removed its biter and game time passed; no controller action.");
        }
    }

    private sealed class DyingRecovery(RuntimeSession session, IGameClient game, ControllerJournal journal) : ICorpseRecovery
    {
        public int Calls { get; private set; }
        public async Task<CorpseRecoveryResult> RunAsync(NativeDeathTransition death, ActorScope scope, CancellationToken token)
        {
            Require(++Calls == 1, "Recovery was retried after a native death during recovery.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var tracked = new TrackingJournal(journal);
            Task<CorpseRecoveryResult> pending = new CorpseRecoveryController(game, tracked).RunAsync(death, scope, deadline.Token);
            try
            {
                string? movingId = null;
                while (!pending.IsCompleted)
                {
                    var observed = await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 1, limit = 1 }), deadline.Token);
                    Require(observed.Ok, "Recovery motion observation failed.");
                    if (observed.Data.TryGetProperty("operation", out var operation)
                        && operation.GetProperty("status").GetString() is "running" or "accepted"
                        && operation.GetProperty("kind").GetString() == "move"
                        && tracked.Submissions.Any(s => s.OperationId == operation.GetProperty("operationId").GetString()))
                    { movingId = operation.GetProperty("operationId").GetString(); break; }
                    await Task.Delay(10, deadline.Token);
                }
                Require(movingId is not null, "No native recovery movement was observed before the prepared death.");
                const string kill = """
                    /silent-command local c=game.surfaces.nauvis.find_entities_filtered{type='character',force=game.forces.factorio_agent}[1];assert(c);local unit=c.unit_number;c.die(game.forces.enemy);rcon.print(helpers.table_to_json{tick=game.tick,character=unit,dead=true})
                    """;
                using var killed = JsonDocument.Parse(await session.CreateRcon().ExecuteAsync(kill, deadline.Token));
                Require(killed.RootElement.GetProperty("dead").GetBoolean(), "Native recovery death was not confirmed.");
                await journal.AppendAsync("fixture-native-death", new { scope, operationId = movingId, duringRecovery = true,
                    native = killed.RootElement.Clone() }, deadline.Token);
                return await pending;
            }
            finally
            {
                deadline.Cancel();
                try { await pending; } catch (Exception) { }
            }
        }
    }

    private sealed class TrackingJournal(IControllerJournal inner) : IControllerJournal
    {
        public System.Collections.Concurrent.ConcurrentQueue<OperationSubmission> Submissions { get; } = new();
        public async Task AppendAsync(string type, object data, CancellationToken token)
        {
            await inner.AppendAsync(type, data, token);
            if (type == "submission" && data is OperationSubmission submission) Submissions.Enqueue(submission);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
