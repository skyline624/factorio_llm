namespace Factorio.Agent.Core;

/// <summary>
/// Chooses the actor's armor, gun and ammunition from native prototype data among items it carries or can craft from an
/// enabled recipe. On 2026-10-01 (seed 20261002) the pistol-armed actor without armor went from full health to death in about
/// seven seconds against biter packs, three times in 75 minutes.
/// </summary>
public static class SurvivalKitPlanner
{
    /// <summary>Carried magazines of the chosen ammunition kept beyond the loaded slots.</summary>
    public const int MagazineReserve = 20;
    private const string Bullet = "bullet";

    public static double ShotsPerSecond(NativeGun gun) => 60 / gun.Cooldown;

    /// <summary>Native physical protection first (biters deal physical damage), then the main inventory bonus.</summary>
    public static (double Percent, double Decrease, int Inventory) Protection(NativeArmor armor) =>
        (armor.PhysicalPercent, armor.PhysicalDecrease, armor.InventoryBonus);

    /// <summary>The obtainable armor offering more native protection than the worn one, or null.</summary>
    public static string? Armor(ProductionCatalog catalog, IReadOnlyDictionary<string, long> carried, string? worn) =>
        Armors(catalog, carried, worn).FirstOrDefault();

    /// <summary>Obtainable upgrades, strongest first: unavailable stock for one armor must not hide a weaker upgrade.</summary>
    public static IReadOnlyList<string> Armors(ProductionCatalog catalog, IReadOnlyDictionary<string, long> carried, string? worn)
    {
        var armors = catalog.Armors ?? new Dictionary<string, NativeArmor>();
        NativeArmor? current = worn is not null ? armors.GetValueOrDefault(worn) : null;
        return armors.Where(a => Obtainable(catalog, carried, a.Key)
                && (current is null || Protection(a.Value).CompareTo(Protection(current)) > 0))
            .OrderByDescending(a => Protection(a.Value)).ThenBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => a.Key).ToArray();
    }

    /// <summary>
    /// The obtainable bullet gun firing more native damage per second than every equipped gun, or null. The reflex only fires
    /// bullet projectiles without minimum range, so other guns are never chosen; equal ammunition scales all guns alike.
    /// </summary>
    public static string? Gun(ProductionCatalog catalog, IReadOnlyDictionary<string, long> carried, IReadOnlyList<string> equipped)
    {
        var guns = (catalog.Guns ?? new Dictionary<string, NativeGun>())
            .Where(g => g.Value.Projectile && g.Value.MinRange == 0 && g.Value.Cooldown > 0 && g.Value.AmmoCategories.Contains(Bullet)).ToDictionary();
        double Rate(NativeGun gun) => ShotsPerSecond(gun) * gun.DamageModifier;
        var best = guns.Where(g => Obtainable(catalog, carried, g.Key)).OrderByDescending(g => Rate(g.Value))
            .ThenByDescending(g => g.Value.Range).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key).FirstOrDefault();
        if (best is null || equipped.Contains(best)) return null;
        var current = equipped.Where(guns.ContainsKey).Select(g => guns[g]).ToArray();
        return current.Length == 0 || current.All(g => Rate(g) < Rate(guns[best]) || Rate(g) == Rate(guns[best]) && g.Range < guns[best].Range)
            ? best : null;
    }

    /// <summary>Obtainable bullet ammunition, strongest native round first: the first producible one is kept in reserve.</summary>
    public static IReadOnlyList<string> Ammunition(ProductionCatalog catalog, IReadOnlyDictionary<string, long> carried) => catalog.Items
        .Where(i => i.Value.AmmoCategory == Bullet && i.Value.MagazineSize is > 0 && Obtainable(catalog, carried, i.Key))
        .OrderByDescending(i => i.Value.RoundDamage ?? 0).ThenBy(i => i.Key, StringComparer.Ordinal).Select(i => i.Key).ToArray();

    /// <summary>
    /// Raw items missing to hand-craft the wanted items from the given stock, expanding only enabled hand-craft recipes with
    /// deterministic solid items; empty when the stock suffices. Smelted plates and steel must already exist: equipping
    /// never sends the actor mining or exploring unprotected first.
    /// </summary>
    public static IReadOnlyDictionary<string, long> Shortfall(ProductionCatalog catalog, IReadOnlyDictionary<string, long> wanted,
        IReadOnlyDictionary<string, long> stock) => Requirement(catalog, wanted, stock).Missing;

    /// <summary>What the hand-craft tree of the wanted items takes from the stock (Used) and what it still lacks (Missing).</summary>
    public static (IReadOnlyDictionary<string, long> Used, IReadOnlyDictionary<string, long> Missing) Requirement(ProductionCatalog catalog,
        IReadOnlyDictionary<string, long> wanted, IReadOnlyDictionary<string, long> stock)
    {
        var left = new Dictionary<string, long>(stock, StringComparer.Ordinal);
        var taken = new Dictionary<string, long>(StringComparer.Ordinal);
        var missing = new Dictionary<string, long>(StringComparer.Ordinal);
        void Need(string item, long count, int depth)
        {
            long used = Math.Min(count, left.GetValueOrDefault(item));
            left[item] = left.GetValueOrDefault(item) - used;
            if (used > 0) taken[item] = taken.GetValueOrDefault(item) + used;
            count -= used;
            if (count <= 0) return;
            var recipe = depth >= 8 ? null : catalog.Recipes.Where(r => r.Enabled && catalog.CanHandCraft(r) && r.Products.Any(p => p.Name == item)
                    && r.Products.All(p => p.DeterministicItem) && r.Ingredients.All(i => i.DeterministicItem))
                .OrderBy(r => r.Name, StringComparer.Ordinal).FirstOrDefault();
            if (recipe is null)
            {
                missing[item] = missing.GetValueOrDefault(item) + count;
                return;
            }
            long batches = (long)Math.Ceiling(count / recipe.Products.Where(p => p.Name == item).Sum(p => p.Amount!.Value));
            foreach (var ingredient in recipe.Ingredients) Need(ingredient.Name, (long)Math.Ceiling(ingredient.Amount!.Value * batches), depth + 1);
        }
        foreach (var (item, count) in wanted) Need(item, count, 0);
        return (taken, missing);
    }

    /// <summary>
    /// The next native equip operation toward the wanted armor and gun from one loadout observation, or null: the armor
    /// first (a worn one is named so it is swapped back, never lost), then the gun into an empty slot, then its strongest
    /// carried rounds. Every operation is decided again from a fresh observation.
    /// </summary>
    public static EquipmentDecision? NextEquipment(EquipmentState loadout, string? armor, string? gun)
    {
        if (armor is not null && loadout.Armor != armor
            && loadout.Carried.Where(c => c.Kind == "armor" && c.Name == armor).OrderBy(c => c.Slot).FirstOrDefault() is { } suit)
            return new("equip", loadout.Armor is null
                ? new { compartment = "armor", slot = 1, sourceSlot = suit.Slot, item = armor, count = 1 }
                : new { compartment = "armor", slot = 1, sourceSlot = suit.Slot, item = armor, count = 1, replaces = loadout.Armor });
        if (gun is null) return null;
        var mounted = loadout.Slots.Where(s => s.Gun == gun).OrderBy(s => s.Index).FirstOrDefault();
        if (mounted is null)
        {
            var free = loadout.Slots.Where(s => s.Gun is null && s.Ammo is null).OrderBy(s => s.Index).FirstOrDefault();
            var carried = loadout.Carried.Where(c => c.Kind == "gun" && c.Name == gun).OrderBy(c => c.Slot).FirstOrDefault();
            return free is null || carried is null ? null
                : new("equip", new { compartment = "gun", slot = free.Index, sourceSlot = carried.Slot, item = gun, count = 1 });
        }
        var rounds = loadout.Carried.Where(c => c.Kind == "ammo" && c.Bullet).OrderByDescending(c => c.Damage).ThenBy(c => c.Slot).FirstOrDefault();
        return mounted.Ammo is not null || rounds is null ? null
            : new("equip", new { compartment = "ammo", slot = mounted.Index, sourceSlot = rounds.Slot, item = rounds.Name, count = Math.Min(10, rounds.Count) });
    }

    private static bool Obtainable(ProductionCatalog catalog, IReadOnlyDictionary<string, long> carried, string item) =>
        carried.GetValueOrDefault(item) > 0 || catalog.Recipes.Any(r => r.Enabled && r.Products.Any(p => p.Name == item));
}
