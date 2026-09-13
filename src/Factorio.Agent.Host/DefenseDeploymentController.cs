using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

public sealed record DefenseDeploymentResult(string Item, int Requested, int ReadyCount, int Built, int Serviced,
    long StartTick, long EndTick, int SurfaceIndex, int CoveredAnchors, int UncoveredAnchors,
    IReadOnlyList<string> ReadyIds, IReadOnlyList<string> ExposedIds);

/// <summary>Installs and replenishes observed turrets using ordinary production, native transfers and C# geometry.</summary>
public sealed class DefenseDeploymentController(IGameClient game, IControllerJournal journal)
{
    public async Task<DefenseDeploymentResult> RunAsync(string item, int quantity, CancellationToken token = default)
    {
        if (quantity is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(quantity));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(1));
        token = deadline.Token;
        var catalog = ProductionCatalog.Parse(await game.ExecuteAsync(GameRequest.Create("production_catalog"), token));
        if (catalog.Turrets is null || !catalog.Turrets.TryGetValue(item, out var model)
            || !catalog.Items.TryGetValue(item, out var prototype) || prototype.PlaceEntity != model.EntityName
            || prototype.PlaceEntityType != "ammo-turret")
            throw new InvalidOperationException("Defense requires a supported native ammunition turret.");
        var snapshots = new FactorySnapshotClient(game);
        var initial = await snapshots.CaptureAsync(cancellationToken: token);
        var initialState = DefenseFactoryState.Read(initial, catalog);
        var origin = ActorPosition(initial);
        long lastTick = initial.CollectedTick;
        int built = 0;
        var serviced = new HashSet<string>(StringComparer.Ordinal);
        await using var controller = new SpatialController(game, journal);
        var production = new ProductionGoalExecutor(game, journal);
        for (int iteration = 0; iteration < 128; iteration++)
        {
            var (snapshot, state) = await ReadAsync();
            var step = DefenseDeploymentPlanner.Next(state, model.EntityName, quantity, ActorPosition(snapshot));
            await journal.AppendAsync("defense-deployment-plan", new { item, quantity, snapshot.Scope, snapshot.CollectedTick, step }, token);
            if (step.Kind == "complete")
            {
                var ready = state.Turrets.Where(t => t.Name == model.EntityName && DefenseDeploymentPlanner.Ready(t)).ToArray();
                var exposed = state.Anchors.Where(a => DefenseDeploymentPlanner.Coverage(a, state.Turrets) == 0).ToArray();
                var result = new DefenseDeploymentResult(item, quantity, ready.Length, built, serviced.Count,
                    initial.CollectedTick, snapshot.CollectedTick, state.SurfaceIndex, state.Anchors.Count - exposed.Length, exposed.Length,
                    ready.Take(32).Select(t => t.Id).ToArray(), exposed.Take(16).Select(a => a.Id).ToArray());
                await journal.AppendAsync("defense-deployment-result", result, token);
                return result;
            }
            if (step.Kind == "build")
            {
                await production.RunAsync(item, 1, token);
                (snapshot, state) = await ReadAsync();
                // Production may move the avatar or discover existing defenses. Reuse them before building.
                if (DefenseDeploymentPlanner.Next(state, model.EntityName, quantity, ActorPosition(snapshot)).Kind != "build") continue;
                var anchor = DefenseDeploymentPlanner.SelectAnchor(state, ActorPosition(snapshot), origin);
                await controller.TravelAsync(anchor.Position, 8, catalog, token);
                (snapshot, state) = await ReadAsync();
                if (DefenseDeploymentPlanner.Next(state, model.EntityName, quantity, ActorPosition(snapshot)).Kind != "build") continue;
                var map = await new SpatialClient(game).CaptureAsync([item], radius: 48, cancellationToken: token);
                if (map.Scope != catalog.Scope) throw new InvalidDataException("Actor changed before turret placement.");
                var candidate = DefenseDeploymentPlanner.Candidates(map, item, model, state, anchor)
                    .FirstOrDefault(p => new PlacementPlanner().FindApproach(new(map), item, p) is not null)
                    ?? throw new InvalidOperationException("No reachable turret placement covering observed industry was found in the local search.");
                await journal.AppendAsync("defense-placement", new { anchor, candidate, map.CollectedTick }, token);
                string id = await new PoweredMachineController(game, journal).BuildAtAsync(item, candidate, catalog, controller, token);
                built++;
                await journal.AppendAsync("defense-installed", new { id, item }, token);
                continue;
            }
            var turret = step.Turret!;
            string ammunition = turret.Ammunition ?? DefenseDeploymentPlanner.ChooseAmmunition(model, catalog, snapshot.SummarizeStocks().InventoryItems);
            if (!catalog.Items.TryGetValue(ammunition, out var magazine) || magazine.MagazineSize is not > 0
                || magazine.AmmoCategory is null || !model.AmmoCategories.Contains(magazine.AmmoCategory))
                throw new InvalidOperationException("The installed ammunition is incompatible with the observed turret prototype.");
            int deficit = checked((int)Math.Ceiling(Math.Max(0, DefenseDeploymentPlanner.ReserveRounds - turret.Rounds) / (double)magazine.MagazineSize.Value));
            if (deficit == 0) throw new InvalidOperationException("Loaded turret lacks a supported native effective firing range.");
            // Keep a bounded delivery reserve in the bag. Partial magazines are remeasured after each transfer.
            await production.RunAsync(ammunition, Math.Min(1000, deficit + 10), token);
            (_, state) = await ReadAsync(ammunition);
            turret = state.Turrets.SingleOrDefault(t => t.Id == turret.Id && t.Active);
            if (turret is null || DefenseDeploymentPlanner.Ready(turret)) continue;
            await controller.ApproachEntityAsync(turret.Id, turret.Position, catalog, token);
            (snapshot, state) = await ReadAsync(ammunition);
            turret = state.Turrets.SingleOrDefault(t => t.Id == turret.Id && t.Active);
            if (turret is null || DefenseDeploymentPlanner.Ready(turret)) continue;
            if (turret.Ammunition is not null && turret.Ammunition != ammunition) continue;
            var inventory = snapshot.Records.Single(r => r.Id == turret.InventoryId && r.Kind == "inventory");
            long capacity = inventory.Data.GetProperty("capacityHints").GetProperty(ammunition).GetProperty("insertable").GetInt64();
            var actor = Actor(snapshot);
            var main = snapshot.Records.Single(r => r.Id == actor.Data.GetProperty("mainInventoryId").GetString() && r.Kind == "inventory");
            long available = main.Data.GetProperty("items").TryGetProperty(ammunition, out var amount) ? amount.GetInt64() : 0;
            deficit = checked((int)Math.Ceiling(Math.Max(0, DefenseDeploymentPlanner.ReserveRounds - turret.Rounds) / (double)magazine.MagazineSize.Value));
            int count = checked((int)Math.Min(deficit, Math.Min(capacity, available)));
            if (count <= 0) throw new InvalidOperationException("No native stock or capacity for the turret reserve; partial deployment remains observable.");
            var receipt = await controller.WorkAsync("insert", new { entityId = turret.Id, inventory = "ammo", item = ammunition, count }, 600, token: token);
            if (receipt.Status is not ("completed" or "partial") || receipt.Effects.GetProperty("targetId").GetString() != turret.Id
                || receipt.Effects.GetProperty("item").GetString() != ammunition || receipt.Effects.GetProperty("inventory").GetString() != "ammo"
                || receipt.Effects.GetProperty("direction").GetString() != "from_actor"
                || receipt.Effects.GetProperty("transferred").GetInt64() is var moved && (moved < 1 || moved > count))
                throw new InvalidDataException("Turret supply lacks a matching native transfer receipt.");
            serviced.Add(turret.Id);
        }
        throw new TimeoutException("Defense deployment exhausted its bounded steps; reconcile observed installed defenses before continuing.");

        async Task<(FactorySnapshot Snapshot, DefenseFactoryState State)> ReadAsync(string? capacityItem = null)
        {
            var snapshot = await snapshots.CaptureAsync(capacityItem is null ? null : [capacityItem], cancellationToken: token);
            if (snapshot.CollectedTick < lastTick) throw new InvalidDataException("Defense observations regressed.");
            var state = DefenseFactoryState.Read(snapshot, catalog);
            if (state.SurfaceIndex != initialState.SurfaceIndex) throw new InvalidDataException("Actor changed surface during defense deployment.");
            lastTick = snapshot.CollectedTick;
            return (snapshot, state);
        }
    }

    private static FactoryRecord Actor(FactorySnapshot snapshot) => snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
    private static MapPosition ActorPosition(FactorySnapshot snapshot) => Actor(snapshot).Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
}
