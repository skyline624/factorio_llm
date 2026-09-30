using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>
/// Ground the persistent factory keeps for its growth: every band with its unbuilt slots, every planned resource row with its
/// walkway, and the steam installation's next units. Parts built outside bands and rows, such as fluid cells with their pipes,
/// pumps and power links, are planned around it; the character still walks over it.
/// </summary>
internal sealed record FactoryGround(FactoryState State, PowerExpansionController.SteamItems? Steam)
{
    /// <summary>Items whose native geometry the reserved ground needs in a capture.</summary>
    public IEnumerable<string> Items => (Steam?.All ?? [])
        .Concat((State.Rows ?? []).SelectMany(r => new[] { r.Equipment.Drill, r.Equipment.Chest, r.Equipment.Furnace, r.Equipment.Inserter, r.Equipment.Pole })
            .OfType<string>());

    /// <summary>
    /// Reserved boxes read from a capture holding <see cref="Items"/>. Steam growth is planned for the boilers in view, as the
    /// expansion itself plans it; without steam items it is not reserved.
    /// </summary>
    public IReadOnlyList<WorldBox> Boxes(SpatialSnapshot map)
    {
        IEnumerable<WorldBox> growth = Steam is null ? [] : PowerExpansionController.ReserveGrowth(map, Steam, State.Zones,
            map.Entities.Single(e => e.Id == map.Actor.Id).Force).Entities.Skip(map.Entities.Count).Select(e => e.Bounds);
        return [.. State.Zones.Select(z => z.Box), .. (State.Rows ?? []).SelectMany(r => ResourceCellPlanner.Reservation(map, r)), .. growth];
    }

    /// <summary>The map with the boxes blocking buildings that collide like the given item, but not the character.</summary>
    public static SpatialSnapshot Reserve(SpatialSnapshot map, IEnumerable<WorldBox> boxes, string buildingItem) =>
        ResourceCellPlanner.Reserve(map, boxes, buildingItem);
}
