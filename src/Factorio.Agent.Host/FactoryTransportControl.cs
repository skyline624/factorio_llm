using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>Native item filtering and local destination-stock control for persistent transport inserters.</summary>
public sealed class FactoryTransportControl(IGameClient game, IControllerJournal journal)
{
    public async Task EnsureAsync(string inserterId, string item, ProductionCatalog catalog, SpatialController controller,
        CancellationToken token, string? chestId = null, int? maximum = null)
    {
        if (!catalog.Items.ContainsKey(item) || (chestId is null) != (maximum is null) || maximum is < 0 or > 10000)
            throw new ArgumentException("A native item and, together, a destination chest and bounded stock limit are required.");
        var map = await CaptureAsync();
        var arm = map.Entities.Single(e => e.Id == inserterId);
        if (Matches(arm.InserterControl, item, chestId, maximum)) return;
        if (map.Prototypes[arm.Name].FilterSlots is not > 0)
            throw new InvalidOperationException("Persistent transport requires native inserter filters; update the mod if their geometry is missing.");
        await controller.ApproachEntityAsync(inserterId, arm.Position, catalog, token);
        var receipt = await controller.WorkAsync("configure_inserter", new { entityId = inserterId, item, chestEntityId = chestId, maximum },
            600, token: token);
        if (receipt.Status != "completed" || receipt.Effects.GetProperty("targetId").GetString() != inserterId
            || receipt.Effects.GetProperty("item").GetString() != item)
            throw new InvalidDataException("Native inserter configuration lacks a matching completed receipt; reconcile the line.");
        map = await CaptureAsync();
        if (!Matches(map.Entities.Single(e => e.Id == inserterId).InserterControl, item, chestId, maximum))
            throw new InvalidDataException("The observed inserter filter or stock condition differs from its plan.");
        await journal.AppendAsync("factory-transport-control", new { inserterId, item, chestId, maximum, map.CollectedTick }, token);

        async Task<SpatialSnapshot> CaptureAsync()
        {
            var captured = await new SpatialClient(game).CaptureAsync(radius: 48, cancellationToken: token);
            if (captured.Scope != catalog.Scope) throw new InvalidDataException("Actor scope changed during transport configuration.");
            return captured;
        }
    }

    internal static bool Matches(ObservedInserterControl? control, string item, string? chestId = null, int? maximum = null) =>
        control is { UseFilters: true, FilterMode: "whitelist", NativeNormalFilters: true, CircuitSetsFilters: false,
            CircuitReadsHand: false, LogisticCondition: false, ScriptDisabled: false } && control.Filters.SequenceEqual([item])
        && (chestId is null ? !control.CircuitEnabled && control.RedNeighbourCount == 0 : control.CircuitEnabled && control.CircuitItem == item && control.Comparator == "<"
            && control.Maximum == maximum && control.RedNeighbourCount == 1 && control.RedNeighbours?.SequenceEqual([chestId]) == true);
}
