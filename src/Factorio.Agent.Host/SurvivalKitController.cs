using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Status: complete, deferred (enemies in sight, manual control) or failed; Missing lists stock lacking per wanted item.</summary>
public sealed record SurvivalKitResult(string Reason, string Status, string? Armor, IReadOnlyList<string> Guns, long CarriedMagazines,
    IReadOnlyList<string> Produced, IReadOnlyList<string> Equipped, IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> Missing, long Tick);

/// <summary>
/// Equips the actor before it leaves defended ground: the best obtainable armor, bullet gun and reserve of the strongest
/// bullet rounds, crafted only from carried items and finished factory stock, then worn through the native equip action with
/// validated receipts. On 2026-10-01 (seed 20261002, run 16) the actor died 14 times in 30 minutes: it respawned with the
/// spawn pistol only and explored for crude oil unarmored. Never during a fight and bounded in time; a failure is journaled
/// and the trip proceeds with what the actor has.
/// </summary>
public sealed class SurvivalKitController(IGameClient game, IControllerJournal journal)
{
    /// <summary>Wall-clock budget of one pass, production included.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(4);
    /// <summary>Game ticks during which an unchanged loadout of the same incarnation is not reconsidered (five minutes).</summary>
    public const long RecheckTicks = 18000;
    private static readonly AsyncLocal<bool> Running = new();
    private static readonly Lock Gate = new();
    private static (string? Key, long Tick, string? Turret) settled;
    private readonly OperationClient operations = new(game);

    /// <summary>
    /// Entry point before exploration and long travel. One small observation when nothing changed since a recent pass; nested
    /// calls from the kit's own production are skipped.
    /// </summary>
    public async Task<SurvivalKitResult?> BeforeTripAsync(string reason, CancellationToken token)
    {
        if (Running.Value) return null;
        try
        {
            var observation = await ObserveAsync(token);
            if (observation?.Loadout is null) return null;
            lock (Gate)
                if (settled.Key == Key(observation, settled.Turret) && observation.Tick - settled.Tick is >= 0 and < RecheckTicks) return null;
            return await EnsureAsync(reason, token);
        }
        catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
        {
            // The trip itself observes and fails on its own terms; equipping is never a reason to abandon it.
            await journal.AppendAsync("survival-kit-failed", new { reason, error = error.GetType().Name, error.Message }, token);
            return null;
        }
    }

    public async Task<SurvivalKitResult> EnsureAsync(string reason, CancellationToken token)
    {
        Running.Value = true; // Flows into the kit's own production and travel, never back to the caller.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Budget);
        var produced = new List<string>();
        var equipped = new List<string>();
        var missing = new Dictionary<string, IReadOnlyDictionary<string, long>>(StringComparer.Ordinal);
        string status = "complete";
        ProductionCatalog? catalog = null;
        string? portableTurret = null;
        int magazineTarget = SurvivalKitPlanner.MagazineReserve;
        try
        {
            catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), deadline.Token));
            var observation = await SafeAsync(deadline.Token);
            if (observation is null) status = "deferred";
            else
            {
                var loadout = observation.Loadout!;
                var armors = SurvivalKitPlanner.Armors(catalog, Carried(loadout), loadout.Armor);
                string? armor = armors.FirstOrDefault();
                string? gun = SurvivalKitPlanner.Gun(catalog, Carried(loadout), loadout.Slots.Where(s => s.Gun is not null).Select(s => s.Gun!).ToArray());
                await journal.AppendAsync("survival-kit-plan", new { reason, worn = loadout.Armor, armor, gun, observation.Tick }, deadline.Token);
                armor = null;
                foreach (string candidate in armors)
                {
                    if (await ObtainAsync(candidate, 1)) { armor = candidate; break; }
                    // Only a proven stock shortfall permits another armor attempt; a failed production is reconciled first.
                    if (!missing.ContainsKey(candidate)) break;
                }
                if (gun is not null && !await ObtainAsync(gun, 1)) gun = null;
                var supplied = await SafeAsync(deadline.Token);
                if (supplied is null) status = "deferred";
                else if (supplied.Inventory is { } inventory)
                {
                    portableTurret = PortableDefensePlanner.SupplyTurret(catalog, inventory);
                    if (portableTurret is not null && await ObtainAsync(portableTurret, PortableDefensePlanner.TurretReserve))
                        magazineTarget += PortableDefensePlanner.TurretReserve * PortableDefensePlanner.MagazinesPerTurret;
                    await journal.AppendAsync("survival-kit-portable-plan", new { portableTurret,
                        turretTarget = PortableDefensePlanner.TurretReserve, magazineTarget, supplied.Tick }, deadline.Token);
                }
                // Revisit equipment after reserving ammunition, so newly obtained rounds are loaded before the trip.
                int equipmentSteps = 0;
                for (int pass = 0; pass < 2 && status == "complete"; pass++)
                {
                    int before = equipmentSteps;
                    while (equipmentSteps < 4 && status == "complete")
                    {
                        var current = await SafeAsync(deadline.Token);
                        if (current is null) { status = "deferred"; break; }
                        var next = SurvivalKitPlanner.NextEquipment(current.Loadout!, armor, gun) ?? EquipmentPolicy.Select(current);
                        if (next is null) break;
                        await SubmitAsync(current, next, deadline.Token);
                        equipmentSteps++;
                        var args = Protocol.ToElement(next.Arguments);
                        equipped.Add(next.Kind == "select_weapon" ? "select-weapon:" + args.GetProperty("slot").GetInt32()
                            : args.GetProperty("compartment").GetString() + ":" + args.GetProperty("item").GetString());
                    }
                    if (pass > 0 && equipmentSteps == before) break;
                    // Replenish the carried reserve after loading; weaker obtainable rounds remain the fallback.
                    var latest = await ObserveAsync(deadline.Token);
                    if (status == "complete" && latest?.Loadout is { } after)
                        foreach (string ammunition in SurvivalKitPlanner.Ammunition(catalog, Carried(after)))
                            if (Carried(after).GetValueOrDefault(ammunition) >= magazineTarget
                                || await ObtainAsync(ammunition, magazineTarget)) break;
                }
            }
        }
        catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
        {
            status = "failed";
            await journal.AppendAsync("survival-kit-failed", new { reason, error = error.GetType().Name, error.Message }, token);
        }
        var final = await ObserveAsync(token);
        // A failed pass is not retried at every trip either; a deferred one is, once enemies are gone.
        if (status != "deferred" && final?.Loadout is not null)
            lock (Gate) settled = (Key(final, portableTurret), final.Tick, portableTurret);
        var result = new SurvivalKitResult(reason, status, final?.Loadout?.Armor,
            final?.Loadout?.Slots.Where(s => s.Gun is not null).Select(s => s.Gun!).ToArray() ?? [],
            final?.Loadout?.Carried.Where(c => c.Kind == "ammo" && c.Bullet).Sum(c => (long)c.Count) ?? 0,
            produced, equipped, missing, final?.Tick ?? 0);
        await journal.AppendAsync("survival-kit", result, token);
        return result;

        // Crafts toward the carried target only when finished factory stock in collectable chests covers the whole recipe tree.
        // That stock is collected first, so the bag keeps what the trip's own task carries.
        async Task<bool> ObtainAsync(string item, int target)
        {
            var production = new ProductionController(game, journal);
            var state = await production.ObserveAsync(deadline.Token);
            long have = state.Inventory.GetValueOrDefault(item);
            if (have >= target) return true;
            var stored = Stock(state);
            foreach (var (name, count) in state.Inventory) stored[name] -= count;
            var (used, shortfall) = SurvivalKitPlanner.Requirement(catalog!, new Dictionary<string, long> { [item] = target - have }, stored);
            if (shortfall.Count > 0)
            {
                missing[item] = shortfall;
                await journal.AppendAsync("survival-kit-unavailable", new { item, target, have, shortfall }, deadline.Token);
                return false;
            }
            try
            {
                foreach (var (name, count) in used)
                    await production.CollectAvailableAsync(name, (int)Math.Min(1000, state.Inventory.GetValueOrDefault(name) + count), deadline.Token);
                var collected = await production.ObserveAsync(deadline.Token);
                if (used.Any(p => collected.Inventory.GetValueOrDefault(p.Key) < state.Inventory.GetValueOrDefault(p.Key) + p.Value))
                {
                    await journal.AppendAsync("survival-kit-unavailable", new { item, target, have, reason = "stock changed before collection" }, deadline.Token);
                    return false;
                }
                await new ProductionGoalExecutor(game, journal).RunAsync(item, target, deadline.Token);
            }
            catch (Exception error) when (FactoryResearchController.Recoverable(error, token))
            {
                await journal.AppendAsync("survival-kit-production-failed", new { item, target, error = error.GetType().Name, error.Message }, deadline.Token);
                return false;
            }
            produced.Add(item);
            return true;
        }
    }

    private async Task SubmitAsync(SafetyObservation observation, EquipmentDecision decision, CancellationToken token)
    {
        var submission = OperationSubmission.Create(observation.Scope, decision.Kind, decision.Arguments, observation.Tick + 180,
            new { position = observation.Position, positionTolerance = .5 });
        await journal.AppendAsync("submission", submission, token);
        OperationReceipt receipt;
        // An ambiguous submission is reconciled by identity, never retransmitted.
        try { receipt = await operations.SubmitAsync(submission, token); }
        catch (OperationOutcomeUnknownException) { receipt = await operations.QueryAsync(submission.OperationId, token); }
        await journal.AppendAsync("receipt", receipt, token);
        if (!receipt.IsTerminal) throw new InvalidDataException("A native equip operation did not finish at once.");
        EquipmentReceipt.Validate(submission, receipt);
    }

    /// <summary>A current observation with no enemy in sight, or null: equipment never changes during a fight.</summary>
    private async Task<SafetyObservation?> SafeAsync(CancellationToken token)
    {
        var observation = await ObserveAsync(token);
        return observation is { Alive: true, ControlMode: "ai", StopUnconfirmed: false, Loadout: not null, Enemies.Count: 0 } ? observation : null;
    }

    private async Task<SafetyObservation?> ObserveAsync(CancellationToken token)
    {
        var observation = SafetyObservation.Parse(await game.ExecuteAsync(GameRequest.Create("observe", new { radius = 32, limit = 200, entityLimit = 1 }), token));
        return observation.Alive ? observation : null;
    }

    /// <summary>Carried items plus finished stock production may collect: outputs of unreserved machines and cell output chests.</summary>
    internal static Dictionary<string, long> Stock(ProductionState state)
    {
        var stock = new Dictionary<string, long>(state.Inventory, StringComparer.Ordinal);
        foreach (var entity in state.Entities.Where(e => ProductionReservations.Collects(e.Id)))
            foreach (var (name, count) in entity.Items("output")) stock[name] = stock.GetValueOrDefault(name) + count;
        return stock;
    }

    private static Dictionary<string, long> Carried(EquipmentState loadout) => loadout.Carried.GroupBy(c => c.Name, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Sum(c => (long)c.Count), StringComparer.Ordinal);

    /// <summary>Incarnation and equipment: a death, a loot or a lost gun makes the next trip reconsider the kit.</summary>
    private static string Key(SafetyObservation observation, string? turret) => string.Join("|", observation.Scope.WorldId, observation.Scope.Incarnation,
        observation.Loadout!.Armor, string.Join(",", observation.Loadout.Slots.Select(s => $"{s.Gun}/{s.Ammo}")),
        string.Join(",", observation.Loadout.Carried.GroupBy(c => c.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key}:{g.Sum(c => (long)c.Count)}")),
        turret is null ? "" : $"{turret}:{observation.Inventory?.GetValueOrDefault(turret)}");
}
