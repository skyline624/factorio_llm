namespace Factorio.Agent.Core;

public sealed record FuelFeederPlan(PlacementCandidate Container, PlacementCandidate Inserter,
    MapPosition Pickup, MapPosition Drop, string? ExistingContainerId = null, string? ExistingInserterId = null,
    PlacementCandidate? Pole = null);

public sealed class FuelFeederPlanner
{
    public FuelFeederPlan? Find(SpatialSnapshot map, string containerItem, string inserterItem, string boilerId, long networkId, string? poleItem = null)
    {
        var boiler = map.Entities.Single(e => e.Id == boilerId);
        var container = map.Prototypes[map.Items[containerItem].EntityName];
        var inserter = map.Prototypes[map.Items[inserterItem].EntityName];
        if (map.Prototypes[boiler.Name].Type != "boiler" || container.Type != "container"
            || !inserter.IsElectric || inserter.Type != "inserter" || inserter.InserterPickup is null || inserter.InserterDrop is null)
            throw new InvalidDataException("Feeder planning requires a boiler, container and native electric inserter geometry.");
        foreach (var existing in map.Entities.Where(e => e.Name == inserter.Name && e.Force == boiler.Force
            && e.DropTargetId == boilerId && e.Power?.NetworkId == networkId && e.PickupPosition is not null && e.DropPosition is not null))
        {
            var chest = map.Entities.SingleOrDefault(e => e.Id == existing.PickupTargetId && e.Name == container.Name && e.Force == boiler.Force);
            if (chest is not null) return new(new(chest.Position, chest.Direction, 0), new(existing.Position, existing.Direction, 0),
                existing.PickupPosition!, existing.DropPosition!, chest.Id, existing.Id);
        }
        var field = new SpatialCollisionField(map with { Entities = map.Entities.Where(e => e.Id != map.Actor.Id).ToArray() });
        foreach (var arm in new PlacementPlanner().FindCandidates(field, inserterItem, boiler.Position, requireBuildReach: false))
        {
            MapPosition pickup = At(arm, inserter.InserterPickup), drop = At(arm, inserter.InserterDrop);
            if (!boiler.Bounds.Contains(drop) || !Aligned(pickup.X, container.TileWidth) || !Aligned(pickup.Y, container.TileHeight)) continue;
            WorldBox armBox = inserter.CollisionBox.Rotate(arm.Direction).Translate(arm.Position);
            bool powered = map.Entities.Any(e => e.Force == boiler.Force && e.Power?.NetworkId == networkId
                && map.Prototypes[e.Name].SupplyArea is { } range
                && Coverage(e.Position, range).Overlaps(armBox));
            if (!powered && poleItem is null) continue;
            var projected = field.Map with
            {
                Entities = [.. field.Map.Entities,
                new("planned:feeder", inserter.Name, arm.Position, armBox, arm.Direction, boiler.Force)]
            };
            if (!new SpatialCollisionField(projected).PlacementClear(container, pickup, 0)) continue;
            PlacementCandidate? extension = null;
            if (!powered)
            {
                var pole = map.Prototypes[map.Items[poleItem!].EntityName];
                if (pole.SupplyArea is not > 0 || pole.MaxWireDistance is not > 0) continue;
                var withChest = projected with
                {
                    Entities = [.. projected.Entities,
                    new("planned:feeder-chest", container.Name, pickup, container.CollisionBox.Translate(pickup), 0, boiler.Force)]
                };
                extension = new PlacementPlanner().FindCandidates(new(withChest), poleItem!, arm.Position, requireBuildReach: false)
                    .FirstOrDefault(p => Coverage(p.Position, pole.SupplyArea.Value).Overlaps(armBox)
                        && map.Entities.Any(e => e.Force == boiler.Force && e.Power?.NetworkId == networkId
                            && map.Prototypes[e.Name].MaxWireDistance is > 0
                            && e.Position.DistanceTo(p.Position) <= Math.Min(pole.MaxWireDistance.Value, map.Prototypes[e.Name].MaxWireDistance!.Value)));
                if (extension is null) continue;
            }
            return new(new(pickup, 0, pickup.DistanceTo(boiler.Position)), arm, pickup, drop, Pole: extension);
        }
        return null;

        static bool Aligned(double coordinate, int size) => Math.Abs(coordinate - size % 2 * .5 - Math.Round(coordinate - size % 2 * .5)) < 1e-8;
        static WorldBox Coverage(MapPosition position, double range) => new(new(position.X - range, position.Y - range), new(position.X + range, position.Y + range));
        static MapPosition At(PlacementCandidate placement, MapPosition offset)
        {
            var rotated = ExtractionPlanner.Rotate(offset, placement.Direction);
            return new(placement.Position.X + rotated.X, placement.Position.Y + rotated.Y);
        }
    }
}
