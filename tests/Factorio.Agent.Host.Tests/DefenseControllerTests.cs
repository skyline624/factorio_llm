using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class DefenseControllerTests
{
    [Fact]
    public async Task Threat_preempts_work_then_requires_fresh_observation_before_shooting()
    {
        var fake = new GameStub { Active = Receipt("work", "wait", "running") };
        var journal = new JournalStub();
        var controller = new DefenseController(fake, journal);
        DefenseStep first = await controller.StepAsync();
        Assert.Equal("preempted", first.State);
        Assert.Equal(["observe", "cancel"], fake.Calls);
        Assert.Equal("cancel-intent", journal.Types[0]);
        fake.Scope = fake.Scope with { Generation = 3 };
        await controller.StepAsync();
        Assert.Equal(["observe", "cancel", "observe", "submit"], fake.Calls);
        Assert.Equal(3, fake.Submission!.Scope.Generation);
        Assert.Equal("shoot", fake.Submission.Kind);
        Assert.Equal("near", fake.Submission.Args.GetProperty("entityId").GetString());
    }

    [Theory]
    [InlineData("manual", true, false, true, 4)]
    [InlineData("ai", false, false, true, 4)]
    [InlineData("ai", true, true, true, 4)]
    [InlineData("ai", true, false, false, 4)]
    [InlineData("ai", true, false, true, 40)]
    public async Task No_mutation_when_manual_dead_stopping_unarmed_or_out_of_range(
        string mode, bool alive, bool stopping, bool armed, double distance)
    {
        var fake = new GameStub { Mode = mode, Alive = alive, Stopping = stopping, Armed = armed, Distance = distance };
        await new DefenseController(fake, new JournalStub()).StepAsync();
        Assert.Equal(["observe"], fake.Calls);
    }

    [Fact]
    public async Task Changed_control_after_preemption_prevents_submission()
    {
        var fake = new GameStub { Active = Receipt("work", "mine", "running") };
        var controller = new DefenseController(fake, new JournalStub());
        await controller.StepAsync();
        fake.Mode = "manual";
        await controller.StepAsync();
        Assert.DoesNotContain("submit", fake.Calls);
    }

    [Fact]
    public async Task Ambiguous_submission_is_queried_without_retransmission()
    {
        var fake = new GameStub { LoseSubmitResponse = true };
        var controller = new DefenseController(fake, new JournalStub());
        await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => controller.StepAsync());
        string id = fake.Submission!.OperationId;
        Assert.Equal("reconciled", (await controller.StepAsync()).State);
        Assert.Equal(["observe", "submit", "operation"], fake.Calls);
        Assert.Equal(id, fake.QueriedId);
        await controller.StepAsync();
        Assert.Single(fake.Calls, c => c == "submit");
    }

    [Fact]
    public async Task Failure_to_persist_intent_prevents_mutation()
    {
        var fake = new GameStub();
        await Assert.ThrowsAsync<IOException>(() => new DefenseController(fake, new JournalStub { Fail = true }).StepAsync());
        Assert.Equal(["observe"], fake.Calls);
    }

    [Fact]
    public async Task Ambiguous_preemption_is_resolved_before_a_new_action()
    {
        var fake = new GameStub { Active = Receipt("work", "wait", "running"), LoseCancelResponse = true };
        var controller = new DefenseController(fake, new JournalStub());
        await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => controller.StepAsync());
        await controller.StepAsync();
        Assert.Equal(["observe", "cancel", "operation"], fake.Calls);
        Assert.Equal("work", fake.QueriedId);
        await controller.StepAsync();
        Assert.Equal(["observe", "cancel", "operation", "observe", "submit"], fake.Calls);
    }

    [Fact]
    public async Task Stale_measurement_prevents_mutation()
    {
        var fake = new GameStub { ObservationTick = 99 };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DefenseController(fake, new JournalStub()).StepAsync());
        Assert.Equal(["observe"], fake.Calls);
    }

    [Fact]
    public async Task Lua_empty_table_is_a_valid_empty_enemy_list()
    {
        var fake = new GameStub { EnemiesEmptyObject = true };
        await new DefenseController(fake, new JournalStub()).StepAsync();
        Assert.Equal(["observe"], fake.Calls);
    }

    private static JsonElement Receipt(string id, string kind, string status) => Protocol.ToElement(new
    {
        operationId = id, kind, status, acceptedTick = 100, updatedTick = 100, effects = new { }
    });

    private sealed class JournalStub : IControllerJournal
    {
        public List<string> Types { get; } = [];
        public bool Fail { get; init; }
        public Task AppendAsync(string type, object data, CancellationToken token)
        {
            if (Fail) throw new IOException("Simulated disk failure.");
            Types.Add(type);
            return Task.CompletedTask;
        }
    }

    private sealed class GameStub : IGameClient
    {
        public List<string> Calls { get; } = [];
        public ActorScope Scope { get; set; } = new("world", "session", "character-1", 1, 2);
        public string Mode { get; set; } = "ai";
        public bool Alive { get; init; } = true;
        public bool Stopping { get; init; }
        public bool Armed { get; init; } = true;
        public double Distance { get; init; } = 4;
        public long ObservationTick { get; init; } = 100;
        public bool LoseSubmitResponse { get; init; }
        public bool LoseCancelResponse { get; init; }
        public bool EnemiesEmptyObject { get; init; }
        public JsonElement? Active { get; set; }
        public OperationSubmission? Submission { get; private set; }
        public string? QueriedId { get; private set; }

        public Task<GameResponse> ExecuteAsync(GameRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(request.Action);
            object data;
            switch (request.Action)
            {
                case "observe":
                    var observed = new Dictionary<string, object>
                    {
                        ["scope"] = Scope, ["collectedTick"] = ObservationTick,
                        ["coverage"] = new { atomic = true, collectionStartTick = ObservationTick, collectionEndTick = ObservationTick,
                            enemyVisibility = "normal-character-5x5-chunks-or-native-current-visibility" },
                        ["agent"] = new { alive = Alive, controlMode = Mode, stopUnconfirmed = Stopping,
                            position = new MapPosition(0, 0), health = 250, weapon = new { ready = Armed, rounds = Armed ? 100 : 0, range = 15 } },
                        ["enemies"] = EnemiesEmptyObject ? new { } : (object)new[]
                        {
                            new { id = "near", position = new MapPosition(Distance, 0), collectedTick = ObservationTick }
                        }
                    };
                    if (Active is not null) observed["operation"] = Active.Value;
                    data = observed;
                    break;
                case "cancel":
                    Active = Receipt(request.Arguments.GetProperty("operationId").GetString()!, "wait", "cancelled");
                    if (LoseCancelResponse) throw new IOException("Cancellation response lost after stopping the native action.");
                    data = Active;
                    break;
                case "submit":
                    Submission = request.Arguments.Deserialize<OperationSubmission>(Protocol.Json)!;
                    Active = Receipt(Submission.OperationId, Submission.Kind, "running");
                    if (LoseSubmitResponse) throw new IOException("Response lost after engine accepted the operation.");
                    data = Active;
                    break;
                case "operation":
                    QueriedId = request.Arguments.GetProperty("operationId").GetString();
                    data = Active!;
                    break;
                default: throw new InvalidOperationException(request.Action);
            }
            return Task.FromResult(new GameResponse(1, request.RequestId, true, 100, Protocol.ToElement(data)));
        }
    }
}
