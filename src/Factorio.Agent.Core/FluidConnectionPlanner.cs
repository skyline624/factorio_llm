namespace Factorio.Agent.Core;

public sealed record FluidConnectionPlacement(PlacementCandidate Placement, int SourceBox, int SourcePort,
    int TargetBox, int TargetPort, MapPosition SourcePosition, MapPosition TargetPosition);

public sealed class FluidConnectionPlanner
{
    public IReadOnlyList<FluidConnectionPlacement> Find(SpatialCollisionField field, EntityGeometry source,
        PlacementCandidate sourcePlacement, string targetItem, string fluid)
    {
        EntityGeometry target = field.Map.Prototypes[field.Map.Items[targetItem].EntityName];
        var result = new List<FluidConnectionPlacement>();
        foreach (FluidBoxGeometry sourceBox in source.FluidBoxes ?? [])
        {
            if (sourceBox.Filter is not null && sourceBox.Filter != fluid) continue;
            foreach (FluidPortGeometry output in sourceBox.Connections.Where(p => p.Type == "normal" && p.FlowDirection is "output" or "input-output"))
            {
                Validate(output);
                MapPosition offset = output.Positions[sourcePlacement.Direction / 4];
                MapPosition sourcePosition = new(sourcePlacement.Position.X + offset.X, sourcePlacement.Position.Y + offset.Y);
                int direction = (output.Direction + sourcePlacement.Direction) % 16;
                MapPosition forward = ExtractionPlanner.Rotate(new(0, -1), direction);
                MapPosition targetPosition = new(sourcePosition.X + forward.X, sourcePosition.Y + forward.Y);
                foreach (FluidBoxGeometry targetBox in target.FluidBoxes ?? [])
                {
                    if (targetBox.Filter is not null && targetBox.Filter != fluid) continue;
                    foreach (FluidPortGeometry input in targetBox.Connections.Where(p => p.Type == "normal" && p.FlowDirection is "input" or "input-output"))
                    {
                        Validate(input);
                        if (!input.Categories.Intersect(output.Categories, StringComparer.Ordinal).Any()) continue;
                        for (int rotation = 0; rotation < 16; rotation += 4)
                        {
                            if ((input.Direction + rotation) % 16 != (direction + 8) % 16) continue;
                            MapPosition point = input.Positions[rotation / 4];
                            MapPosition position = new(targetPosition.X - point.X, targetPosition.Y - point.Y);
                            int width = rotation % 8 == 0 ? target.TileWidth : target.TileHeight;
                            int height = rotation % 8 == 0 ? target.TileHeight : target.TileWidth;
                            if (!Aligned(position.X, width) || !Aligned(position.Y, height)
                                || !field.PlacementClear(target, position, rotation)) continue;
                            result.Add(new(new(position, rotation, position.DistanceTo(field.Map.Actor.Position)),
                                sourceBox.Index, output.Index, targetBox.Index, input.Index, sourcePosition, targetPosition));
                        }
                    }
                }
            }
        }
        return result.OrderBy(p => p.Placement.Score).ThenBy(p => p.Placement.Direction).ToArray();
    }

    private static bool Aligned(double coordinate, int size) => Math.Abs(coordinate - size % 2 * 0.5 - Math.Round(coordinate - size % 2 * 0.5)) < 1e-8;

    private static void Validate(FluidPortGeometry port)
    {
        if (port.Positions.Count != 4 || port.Positions.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y))
            || port.Direction is < 0 or > 12 || port.Direction % 4 != 0)
            throw new InvalidDataException("Invalid native cardinal fluid connection geometry.");
    }
}
