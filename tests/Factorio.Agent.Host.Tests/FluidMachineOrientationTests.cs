using System.Text.Json;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class FluidMachineOrientationTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 1)]
    [InlineData(8, 2)]
    [InlineData(12, 3)]
    public async Task ConfiguredAssemblerReachesItsPlannedFluidOrientation(int direction, int rotations)
    {
        var game = new Game();
        await using var controller = new SpatialController(game, new Journal());
        int actual = await new PoweredMachineController(game, new Journal()).OrientFluidAsync("machine", direction,
            Catalogs.Raw() with { Scope = game.Map.Scope }, controller, CancellationToken.None);
        Assert.Equal(rotations, actual);
        Assert.Equal(rotations, game.Submissions);
        Assert.Equal(direction, game.Map.Entities.Single().Direction);
    }

    [Fact]
    public async Task SolidRecipeWithoutPortsDoesNotRotateAnUnrotatableAssembler()
    {
        var game = new Game(ports: false);
        await using var controller = new SpatialController(game, new Journal());
        Assert.Equal(0, await new PoweredMachineController(game, new Journal()).OrientFluidAsync("machine", 4,
            Catalogs.Raw() with { Scope = game.Map.Scope }, controller, CancellationToken.None));
        Assert.Equal(0, game.Submissions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UnchangedOrContradictedRotationIsNeverSubmittedAgain(bool receiptUnchanged)
    {
        var game = new Game(move: false, receiptUnchanged: receiptUnchanged);
        await using var controller = new SpatialController(game, new Journal());
        await Assert.ThrowsAsync<InvalidDataException>(() => new PoweredMachineController(game, new Journal()).OrientFluidAsync("machine", 4,
            Catalogs.Raw() with { Scope = game.Map.Scope }, controller, CancellationToken.None));
        Assert.Equal(1, game.Submissions);
    }

    private sealed class Game(bool ports = true, bool move = true, bool receiptUnchanged = false) : IGameClient
    {
        public SpatialSnapshot Map { get; private set; } = FactoryMaps.Grass(16, [new("machine", "assembling-machine-1", new(4.5, .5),
            new(new(3.3, -.7), new(5.7, 1.7)), 0, "agent", FluidConnections: ports
                ? [new(1, 1, new(4.5, -.5), new(4.5, -1.5), FlowDirection: "input", Filter: "light-oil")] : [])]);
        public int Submissions { get; private set; }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object data = request.Action switch
            {
                "spatial" => Map,
                "observe" => new
                {
                    Map.Scope, collectedTick = Map.CollectedTick,
                    coverage = new { atomic = true, collectionStartTick = Map.CollectedTick, collectionEndTick = Map.CollectedTick,
                        enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                    agent = new { alive = true, controlMode = "ai", stopUnconfirmed = false, position = Map.Actor.Position,
                        health = 250, weapon = new { ready = false, rounds = 0, range = 0 } },
                    enemies = Array.Empty<object>()
                },
                "submit" => Submit(request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!),
                _ => throw new InvalidOperationException(request.Action)
            };
            return Task.FromResult(new GameResponse(1, request.RequestId, true, Map.CollectedTick, Protocol.ToElement(data)));
        }

        private object Submit(OperationSubmission operation)
        {
            Assert.Equal("rotate", operation.Kind);
            Submissions++;
            int before = Map.Entities.Single().Direction;
            int after = (before + 4) % 16;
            if (move) Map = Map with { Entities = [Map.Entities.Single() with { Direction = after }] };
            return new { operation.OperationId, operation.Kind, status = "completed", acceptedTick = Map.CollectedTick,
                updatedTick = Map.CollectedTick, effects = new { beforeDirection = before, afterDirection = receiptUnchanged ? before : after } };
        }
    }

    private sealed class Journal : IControllerJournal
    {
        public Task AppendAsync(string type, object data, CancellationToken token) => Task.CompletedTask;
    }
}
