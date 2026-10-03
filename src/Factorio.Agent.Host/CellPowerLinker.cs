using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;

namespace Factorio.Agent.Host;

/// <summary>
/// Joins a cell pole to a fed electric network wherever that network stands. Links grow outward from fed poles toward the
/// cell, one pole per step: when no fed pole is in view, the actor walks to the known fed pole nearest the cell and extends
/// from there, so a pumpjack on a remote deposit still gets power. Every link pole is reported so its cell can register it,
/// and must itself be fed before the next one is planned. The reach is the step budget over observed, passable ground.
/// </summary>
internal sealed class CellPowerLinker(IGameClient game, IControllerJournal journal)
{
    /// <summary>Steps per connection: link poles plus the walk to a remote fed pole.</summary>
    public const int MaximumSteps = 128;

    /// <param name="items">Captured with the pole: the items whose geometry the reservations read.</param>
    /// <param name="reservations">Ground views to plan links on, strictest first; the next is tried when one leaves no path.</param>
    /// <param name="ensureCarried">Tops up the carried link poles to the given count before a pole is placed.</param>
    public async Task ConnectAsync(string poleId, MapPosition polePosition, string poleItem, IReadOnlyList<string> items, ProductionCatalog catalog,
        SpatialController controller, IReadOnlyList<Func<SpatialSnapshot, SpatialSnapshot>> reservations, Func<int, Task> ensureCarried,
        Func<string, PlacementCandidate, Task> registerLink, CancellationToken token)
    {
        var spatial = new SpatialClient(game);
        string? added = null;
        MapPosition? joined = null;
        for (int step = 0; step < MaximumSteps; step++)
        {
            var known = await new FactorySnapshotClient(game).CaptureAsync(cancellationToken: token);
            RequireScope(known.Scope, catalog);
            if (FactoryPower.IsFed(known, poleId) == true) return;
            // The photograph lists every known own entity wherever it stands: a pole missing from it was destroyed.
            if (!known.Records.Any(r => r.Kind == "entity" && r.EntityId == poleId))
                throw new InvalidOperationException($"The cell pole {poleId} no longer stands; rebuild the cell before linking it.");
            if (added is not null && FactoryPower.IsFed(known, added) != true)
                throw new InvalidDataException($"Link pole {added} did not join a fed network; reconcile the partial link.");
            var map = await spatial.CaptureAsync(items, 48, token);
            RequireScope(map.Scope, catalog);
            var next = await ControllerPlanning.RunAsync(t => Plan(known, map, poleId, polePosition, poleItem, reservations, t),
                controller, TimeSpan.FromMinutes(2), token);
            if (next is null)
            {
                var remote = NearestFedPole(known, polePosition) ?? throw new InvalidOperationException("No fed electric pole is known; build power first.");
                if (remote == joined) throw new InvalidOperationException($"The fed pole at {remote} is not observed on arrival.");
                joined = remote;
                await journal.AppendAsync("cell-power-remote", new { poleId, remote, map.CollectedTick }, token);
                await controller.TravelAsync(remote, 6, catalog, token);
                continue;
            }
            await journal.AppendAsync("cell-power-link", new { poleId, step, next, map.CollectedTick }, token);
            if (next.Status != PowerGridSearchStatus.Extension || next.Pole is null)
                throw new InvalidOperationException($"The cell pole cannot join a fed network from the observed poles: {next.Status}.");
            // The whole planned chain is carried before its first pole, so no production trip interrupts the link.
            await ensureCarried(PowerGridPlanner.ChainPoles(next));
            added = await new PoweredMachineController(game, journal).BuildAtAsync(poleItem, next.Pole, catalog, controller, token);
            joined = null;
            await registerLink(added, next.Pole);
            await controller.TravelAsync(next.Pole.Position, 3, catalog, token);
        }
        throw new InvalidOperationException($"Joining the cell to a fed network exceeded its {MaximumSteps}-step link budget.");
    }

    /// <summary>
    /// The next link from the fed poles in view toward the cell pole, planned on the first ground view that leaves a path, or
    /// null when no fed pole is in view. The pole's box comes from its planned position, so the cell may lie out of view.
    /// </summary>
    internal static PowerGridLink? Plan(FactorySnapshot known, SpatialSnapshot map, string poleId, MapPosition polePosition, string poleItem,
        IReadOnlyList<Func<SpatialSnapshot, SpatialSnapshot>> reservations, CancellationToken token = default)
    {
        var sources = map.Entities.Where(e => e.Id != poleId && FactoryPower.IsFed(known, e.Id) == true).Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        if (!map.Entities.Any(e => sources.Contains(e.Id) && map.Prototypes[e.Name].Type == "electric-pole")) return null;
        var target = known.Records.Single(r => r.Kind == "entity" && r.EntityId == poleId);
        var next = new PowerGridLink(PowerGridSearchStatus.NoObservedPath);
        foreach (var reserve in reservations)
        {
            next = new PowerGridPlanner().NextToPole(reserve(map), poleItem, target.Name, polePosition, sources, token);
            if (next.Status != PowerGridSearchStatus.NoObservedPath) break;
        }
        return next;
    }

    /// <summary>The known pole sharing a network with a power source that stands nearest to a point, if any.</summary>
    internal static MapPosition? NearestFedPole(FactorySnapshot known, MapPosition near) => known.Records
        .Where(r => r.Kind == "entity" && r.Data.GetProperty("role").GetString() == "factory"
            && r.Data.GetProperty("type").GetString() == "electric-pole" && FactoryPower.IsFed(known, r.EntityId) == true)
        .Select(r => r.Data.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!)
        .OrderBy(p => p.DistanceTo(near)).ThenBy(p => p.X).ThenBy(p => p.Y).FirstOrDefault();

    private static void RequireScope(ActorScope scope, ProductionCatalog catalog)
    {
        if (scope != catalog.Scope) throw new InvalidDataException("Actor identity changed while linking a cell pole; reconcile partial construction.");
    }
}
