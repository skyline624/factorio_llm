using System.Text.Json;

namespace Factorio.Agent.Core;

public sealed record DefenseAnchor(string Id, MapPosition Position);
public sealed record DefenseDeploymentStep(string Kind, InstalledTurret? Turret = null);
public sealed record InstalledTurret(string Id, string Name, MapPosition Position, string InventoryId, long Rounds,
    bool Active, double? Range, string? Ammunition);
public sealed record DefenseFactoryState(int SurfaceIndex, IReadOnlyList<DefenseAnchor> Anchors, IReadOnlyList<InstalledTurret> Turrets)
{
    private static readonly HashSet<string> Industry = ["mining-drill", "furnace", "assembling-machine", "lab", "boiler", "generator", "rocket-silo", "container", "storage-tank"];
    public static DefenseFactoryState Read(FactorySnapshot snapshot, ProductionCatalog catalog)
    {
        if (snapshot.Scope != catalog.Scope) throw new InvalidDataException("Defense stock belongs to another actor scope.");
        var actor = snapshot.Records.Single(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "actor");
        int surface = actor.Data.GetProperty("surfaceIndex").GetInt32();
        var anchors = new List<DefenseAnchor>();
        var turrets = new List<InstalledTurret>();
        foreach (var entity in snapshot.Records.Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
            && r.Data.GetProperty("surfaceIndex").GetInt32() == surface))
        {
            var data = entity.Data;
            var position = data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            string type = data.GetProperty("type").GetString()!;
            if (Industry.Contains(type)) anchors.Add(new(entity.Id, position));
            if (type != "ammo-turret" || data.GetProperty("quality").GetString() != "normal") continue;
            string inventoryId = data.GetProperty("ammoInventoryId").GetString()!;
            var inventory = snapshot.Records.Single(r => r.Kind == "inventory" && r.Id == inventoryId && r.EntityId == entity.Id);
            string? ammunition = inventory.Data.GetProperty("items").EnumerateObject()
                .Where(p => p.Value.GetInt64() > 0 && catalog.Items.GetValueOrDefault(p.Name)?.AmmoCategory is not null)
                .Select(p => p.Name).FirstOrDefault();
            long rounds = data.GetProperty("ammoRounds").GetInt64();
            double? range = data.GetProperty("defenseReady").GetBoolean() ? data.GetProperty("defenseRange").GetDouble() : null;
            if (rounds < 0 || range is { } value && (!double.IsFinite(value) || value <= 0))
                throw new InvalidDataException("Invalid native turret ammunition or range.");
            turrets.Add(new(entity.Id, entity.Name, position, inventoryId, rounds, data.GetProperty("active").GetBoolean(), range, ammunition));
        }
        return new(surface, anchors, turrets);
    }
}

public static class DefenseDeploymentPlanner
{
    public static DefenseDeploymentStep Next(DefenseFactoryState state, string entityName, int quantity, MapPosition actor)
    {
        if (quantity is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(quantity));
        var installed = state.Turrets.Where(t => t.Name == entityName && t.Active).ToArray();
        if (installed.Count(Ready) >= quantity) return new("complete");
        var service = installed.Where(t => !Ready(t)).OrderBy(t => t.Position.DistanceTo(actor))
            .ThenBy(t => t.Id, StringComparer.Ordinal).FirstOrDefault();
        return service is null ? new("build") : new("service", service);
    }
    public static bool Ready(InstalledTurret turret) => turret.Active && turret.Rounds >= ReserveRounds && turret.Range is > 0;
    public const int ReserveRounds = 100;
    public static int Coverage(DefenseAnchor anchor, IEnumerable<InstalledTurret> turrets) => turrets.Count(t => t.Active && t.Rounds > 0
        && t.Range is { } range && t.Position.DistanceTo(anchor.Position) <= range);

    public static DefenseAnchor SelectAnchor(DefenseFactoryState state, MapPosition actor, MapPosition initialPosition) => state.Anchors
        .OrderBy(a => Coverage(a, state.Turrets)).ThenBy(a => a.Position.DistanceTo(actor)).ThenBy(a => a.Id, StringComparer.Ordinal)
        .FirstOrDefault() ?? new("initial-actor-position", initialPosition);

    public static IReadOnlyList<PlacementCandidate> Candidates(SpatialSnapshot map, string item, NativeTurret turret,
        DefenseFactoryState factory, DefenseAnchor preferred)
    {
        var anchors = factory.Anchors.Count == 0 ? new[] { preferred } : factory.Anchors;
        return new PlacementPlanner().FindCandidates(new(map), item, preferred.Position, requireBuildReach: false)
            .Select(p => p with { Score = anchors.Where(a => a.Position.DistanceTo(p.Position) <= turret.Range * .9)
                .Sum(a => 1d / (1 + Coverage(a, factory.Turrets))) })
            .Where(p => p.Score > 0).OrderByDescending(p => p.Score)
            .ThenBy(p => p.Position.DistanceTo(preferred.Position)).ThenBy(p => p.Position.DistanceTo(map.Actor.Position)).ToArray();
    }

    public static string ChooseAmmunition(NativeTurret turret, ProductionCatalog catalog, IReadOnlyDictionary<string, long> stock)
    {
        var choices = catalog.Items.Where(p => p.Value.AmmoCategory is { } category && turret.AmmoCategories.Contains(category)
            && p.Value.MagazineSize is > 0).Select(p => new
            {
                p.Key, Stock = stock.GetValueOrDefault(p.Key),
                RecipeCost = catalog.Recipes.Where(r => r.Enabled && r.Products.Any(m => m.Name == p.Key && m.DeterministicItem)
                    && r.Ingredients.All(m => m.DeterministicItem)).Select(r => r.Ingredients.Sum(m => m.Amount!.Value)
                        / r.Products.Where(m => m.Name == p.Key).Sum(m => m.Amount!.Value)).DefaultIfEmpty(double.PositiveInfinity).Min()
            }).Where(p => p.Stock > 0 || double.IsFinite(p.RecipeCost)).OrderByDescending(p => p.Stock > 0)
            .ThenBy(p => p.RecipeCost).ThenBy(p => p.Key, StringComparer.Ordinal).FirstOrDefault();
        return choices?.Key ?? throw new InvalidOperationException("No observed or producible compatible ammunition is available.");
    }
}
