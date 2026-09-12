namespace Factorio.Agent.Core;

public sealed record PlannedFluidSupply(string Fluid, FluidSupplyRoute Supply);

public sealed class MultiFluidSupplyPlanner
{
    public IReadOnlyList<PlannedFluidSupply>? Find(SpatialSnapshot map, FactorySnapshot stock, string pipeItem,
        string targetId, IReadOnlyList<string> fluids, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (map.Scope != stock.Scope) throw new InvalidDataException("Joint fluid planning requires one actor scope.");
        if (fluids.Count > 3 || fluids.Distinct(StringComparer.Ordinal).Count() != fluids.Count)
            throw new InvalidOperationException("Joint fluid routing supports at most three distinct inputs.");
        var pipe = map.Prototypes[map.Items[pipeItem].EntityName];
        string force = map.Entities.Single(e => e.Id == targetId).Force;
        foreach (var order in Orders(fluids))
        {
            var planned = new List<PlannedFluidSupply>();
            var current = map;
            foreach (string fluid in order)
            {
                var supply = new FluidSupplyPlanner().Find(current, stock, pipeItem, targetId, fluid, cancellationToken);
                if (supply is null || supply.Route.Pipes.Count > 200) break;
                planned.Add(new(fluid, supply));
                var added = supply.Route.Pipes.Select((position, index) => new SpatialEntity($"planned:{planned.Count}:{index}", pipe.Name,
                    position, pipe.CollisionBox.Translate(position), 0, force,
                    FluidConnections: Enumerable.Range(0, 4).Select(direction =>
                    {
                        var offset = ExtractionPlanner.Rotate(new(0, -1), direction * 4);
                        return new ObservedFluidConnection(1, direction + 1, position, new(position.X + offset.X, position.Y + offset.Y),
                            Type: "normal", FlowDirection: "input-output");
                    }).ToArray())).ToArray();
                current = current with { Entities = [.. current.Entities, .. added] };
            }
            if (planned.Count == fluids.Count) return planned;
        }
        return null;
    }

    private static IEnumerable<IReadOnlyList<string>> Orders(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0) { yield return []; yield break; }
        for (int index = 0; index < inputs.Count; index++)
            foreach (var tail in Orders(inputs.Where((_, i) => i != index).ToArray()))
                yield return new[] { inputs[index] }.Concat(tail).ToArray();
    }
}
