using Factorio.Agent.Core;
using Factorio.Agent.Infrastructure;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidStageGrowthTests
{
    [Fact]
    public async Task EightReadyFluidCellsAllowTheNextCallToPrepareAdditionalNativeCapacity()
    {
        var directory = Directory.CreateTempSubdirectory("fluid-growth-");
        try
        {
            var catalog = OilCatalogs.Oil();
            var stage = OilCatalogs.Plan(catalog, "plastic-bar", 498)!.Stages.Single(s => s.Recipe == "plastic-bar");
            var cells = Enumerable.Range(0, 8).Select(i => new FactoryCell($"chemical-{i}", 0, new(0, i, true),
                FluidCellBuilder.MachineKind, "chemical-plant", stage.Recipe, new Dictionary<string, string> { ["machine"] = $"native-{i}" },
                "ready", 100)).ToArray();
            var registry = new FactoryRegistry(directory.FullName);
            await registry.SaveAsync(new(1, catalog.Scope.WorldId, [], cells), default);
            var game = new CapacityBoundaryGame();
            await Assert.ThrowsAsync<CapacityBoundaryReached>(() => new FluidChainDirector(game,
                new ControllerJournal(Path.Combine(directory.FullName, "journal.jsonl")), directory.FullName).EnsureStageAsync(stage, catalog, default));
            Assert.Equal(1, game.Requests);
            Assert.Equal(cells.Select(c => (c.Id, c.Status)), (await registry.LoadAsync(catalog.Scope.WorldId, default)).Cells.Select(c => (c.Id, c.Status)));
        }
        finally { directory.Delete(recursive: true); }
    }

    // Stop at the first fresh native catalog needed for power/build preparation: this test authorizes no mutation.
    private sealed class CapacityBoundaryGame : IGameClient
    {
        public int Requests { get; private set; }
        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal("production_catalog", request.Action);
            Requests++;
            throw new CapacityBoundaryReached();
        }
    }
    private sealed class CapacityBoundaryReached : Exception;
}
