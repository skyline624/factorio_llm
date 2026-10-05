namespace Factorio.Agent.Core;

/// <summary>A carried, compatible turret and paid magazines that can cover the current actor without a trip.</summary>
public sealed record PortableDefensePlan(string Turret, string Ammunition, int Magazines, PlacementCandidate Placement);

/// <summary>Local construction geometry for an exposed actor facing a normally visible pack; never assumes unseen safety.</summary>
public sealed class PortableDefensePlanner
{
    public const int TurretReserve = 2;
    public const int MagazinesPerTurret = 20;

    public static string? SupplyTurret(ProductionCatalog catalog, IReadOnlyDictionary<string, long> carried) =>
        (catalog.Turrets ?? new Dictionary<string, NativeTurret>())
            .Where(p => catalog.Items.TryGetValue(p.Key, out var item) && item.PlaceEntity == p.Value.EntityName
                && item.PlaceEntityType == "ammo-turret" && p.Value.AmmoCategories.Contains("bullet")
                && (carried.GetValueOrDefault(p.Key) > 0 || catalog.Recipes.Any(r => r.Enabled && catalog.CanHandCraft(r)
                    && r.Products.Any(product => product.DeterministicItem && product.Name == p.Key))))
            .OrderByDescending(p => p.Value.Range).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault();

    public PortableDefensePlan? Find(SafetyObservation observed, SpatialSnapshot map, ProductionCatalog catalog,
        IReadOnlyDictionary<string, long> carried, CancellationToken token = default, int requiredCovers = 1,
        IReadOnlyList<MapPosition>? refusedPlacements = null)
    {
        token.ThrowIfCancellationRequested();
        if (requiredCovers is < 1 or > TurretReserve) throw new ArgumentOutOfRangeException(nameof(requiredCovers));
        if (observed.Scope != map.Scope || catalog.Scope != map.Scope)
            throw new InvalidDataException("Portable defense observations belong to different actors.");
        if (!observed.Alive || observed.Health <= 0 || observed.ControlMode != "ai" || observed.StopUnconfirmed || !observed.LocalEnemiesComplete
            || observed.Position is null || observed.Enemies.Count(e => e.Type == "unit") < RetreatPlanner.OutnumberedEnemies
            || map.Actor.ControlMode != "ai" || map.CollectedTick < observed.Tick || map.CollectedTick - observed.Tick > 60
            || map.Actor.Position.DistanceTo(observed.Position) > .5)
            return null;
        // Reinforcement stays at the actor; an existing refuge never justifies an unprotected trip.
        if (observed.Defenses?.Count(r => observed.Position.DistanceTo(r.Position) <= RetreatPlanner.CoverRadius(r)) >= requiredCovers)
            return null;
        var field = new SpatialCollisionField(map);
        if (!PlacementPlanner.CanStop(field, map.Actor.Position)) return null;
        var turrets = (catalog.Turrets ?? new Dictionary<string, NativeTurret>())
            .Where(p => carried.GetValueOrDefault(p.Key) > 0 && map.Items.TryGetValue(p.Key, out var item)
                && catalog.Items.TryGetValue(p.Key, out var native) && native.PlaceEntity == p.Value.EntityName
                && native.PlaceEntityType == "ammo-turret" && p.Value.AmmoCategories.Contains("bullet")
                && item.EntityName == p.Value.EntityName && map.Prototypes[item.EntityName].Type == "ammo-turret")
            .OrderByDescending(p => p.Value.Range).ThenBy(p => p.Key, StringComparer.Ordinal);
        foreach (var turret in turrets)
        {
            string? ammo = catalog.Items.Where(p => p.Value.AmmoCategory is { } category && turret.Value.AmmoCategories.Contains(category)
                    && p.Value.MagazineSize is > 0 && carried.GetValueOrDefault(p.Key) >= MagazinesPerTurret + SurvivalKitPlanner.MagazineReserve)
                .OrderByDescending(p => p.Value.RoundDamage ?? 0).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault();
            if (ammo is null) continue;
            double reach = Math.Min(map.Actor.BuildDistance, map.Actor.ReachDistance) - 1;
            double radius = Math.Min(reach, Math.Min(4, turret.Value.Range * .5));
            if (radius <= 0) continue;
            var nearest = observed.Enemies.Where(e => e.Type == "unit").MinBy(e => e.Position.DistanceTo(map.Actor.Position))!.Position;
            double distance = nearest.DistanceTo(map.Actor.Position);
            // The first turret screens the actor; reinforcement is sought beside it, away from the pack's direct approach.
            bool reinforces = observed.Defenses?.Any(r => observed.Position.DistanceTo(r.Position) <= RetreatPlanner.CoverRadius(r)) == true;
            double dx = nearest.X - map.Actor.Position.X, dy = nearest.Y - map.Actor.Position.Y;
            var preferred = distance > 0 ? new MapPosition(map.Actor.Position.X + (reinforces ? -dy : dx) * 2 / distance,
                map.Actor.Position.Y + (reinforces ? dx : dy) * 2 / distance) : map.Actor.Position;
            var placements = new PlacementPlanner();
            var selected = placements.FindCandidates(field, turret.Key, preferred, limit: 100,
                    eligible: p => p.Position.DistanceTo(map.Actor.Position) <= radius
                        && refusedPlacements?.Any(rejected => p.Position.DistanceTo(rejected) < 1.5) != true,
                    cancellationToken: token)
                .FirstOrDefault(p => placements.PreservesExit(field, turret.Key, p, token));
            if (selected is not null) return new(turret.Key, ammo, MagazinesPerTurret, selected);
        }
        return null;
    }
}
