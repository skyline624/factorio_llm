using System.Text.Json;

namespace Factorio.Agent.Core;

public sealed record WeaponSlot(int Index, bool BulletGun, double Range, int Rounds, bool Ready, string? Gun = null, string? Ammo = null);
/// <summary>Damage is the native damage of one round of carried ammunition, so the reflex loads the strongest first.</summary>
public sealed record CarriedWeaponItem(int Slot, string Name, string Kind, int Count, bool Bullet, double Range, int Rounds, double Damage = 0);
/// <summary>Armor is the worn armor item, if any; carried armor appears with kind "armor".</summary>
public sealed record EquipmentState(IReadOnlyList<WeaponSlot> Slots, IReadOnlyList<CarriedWeaponItem> Carried, string? Armor = null)
{
    public static EquipmentState Parse(JsonElement node)
    {
        if (!node.GetProperty("complete").GetBoolean()) throw new InvalidDataException("Incomplete native equipment observation.");
        var slots = Read<WeaponSlot>(node.GetProperty("slots"));
        var carried = Read<CarriedWeaponItem>(node.GetProperty("carried"));
        string? armor = node.TryGetProperty("armor", out var worn) && worn.ValueKind == JsonValueKind.String ? worn.GetString() : null;
        if (slots.Select(s => s.Index).Distinct().Count() != slots.Length
            || carried.Select(s => s.Slot).Distinct().Count() != carried.Length
            || slots.Any(s => s.Index < 1 || s.Rounds < 0 || !double.IsFinite(s.Range) || s.Range < 0
                || s.BulletGun && (string.IsNullOrWhiteSpace(s.Gun) || s.Range <= 0)
                || s.Ready && (!s.BulletGun || string.IsNullOrWhiteSpace(s.Ammo) || s.Rounds == 0))
            || carried.Any(s => s.Slot < 1 || string.IsNullOrWhiteSpace(s.Name) || s.Count < 1 || s.Rounds < 0
                || s.Kind is not ("gun" or "ammo" or "armor") || !double.IsFinite(s.Range) || s.Range < 0
                || !double.IsFinite(s.Damage) || s.Damage < 0 || s.Kind == "armor" && s.Bullet
                || s.Bullet && (s.Kind == "gun" ? s.Range <= 0 : s.Rounds == 0))
            || armor is { Length: 0 })
            throw new InvalidDataException("Malformed native weapon slots or carried equipment.");
        return new(slots, carried, armor);
    }

    private static T[] Read<T>(JsonElement node) => node.ValueKind == JsonValueKind.Object && !node.EnumerateObject().Any()
        ? [] : node.Deserialize<T[]>(Protocol.Json) ?? throw new InvalidDataException("Missing equipment list.");
}

public sealed record EquipmentDecision(string Kind, object Arguments);

public static class EquipmentPolicy
{
    public static EquipmentDecision? Select(SafetyObservation state)
    {
        if (!state.Alive || state.ControlMode != "ai" || state.StopUnconfirmed || state.Health <= 0
            || state.Loadout is not { } loadout) return null;
        // The strongest carried rounds, then the longest-range gun that is loaded or can be: a pistol never displaces an
        // available submachine gun.
        var ammunition = loadout.Carried.Where(s => s.Kind == "ammo" && s.Bullet).OrderByDescending(s => s.Damage).ThenBy(s => s.Slot).FirstOrDefault();
        var best = loadout.Slots.Where(s => s.Ready || s.BulletGun && s.Ammo is null && ammunition is not null)
            .OrderByDescending(s => s.Range).ThenByDescending(s => s.Ready).ThenBy(s => s.Index).FirstOrDefault();
        // A loaded pistol must not hide a mounted empty submachine gun and carried rounds. Keep an equally capable ready gun.
        if (state.Weapon.Ready && (best is null || best.Range <= state.Weapon.Range)) return null;
        if (best is { Ready: true }) return new("select_weapon", new { slot = best.Index });
        if (best is not null)
            return new("equip", new { compartment = "ammo", slot = best.Index, sourceSlot = ammunition!.Slot,
                item = ammunition.Name, count = Math.Min(10, ammunition.Count) });
        var emptyGun = loadout.Slots.Where(s => s.Gun is null && s.Ammo is null).OrderBy(s => s.Index).FirstOrDefault();
        var gun = loadout.Carried.Where(s => s.Kind == "gun" && s.Bullet).OrderByDescending(s => s.Range).ThenBy(s => s.Slot).FirstOrDefault();
        return emptyGun is null || gun is null ? null : new("equip", new { compartment = "gun", slot = emptyGun.Index,
            sourceSlot = gun.Slot, item = gun.Name, count = 1 });
    }
}
