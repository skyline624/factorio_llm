using System.Text.Json;
using Factorio.Agent.Core;
using Factorio.Agent.Host;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class DefenseControllerTests
{
    [Fact]
    public async Task TerminalEquipmentWithoutTransferEvidenceCannotBeCalledSuccessful()
    {
        var fake = new GameStub { Armed = false, Loadout = Loadout(null, "gun", false), CompleteSubmission = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DefenseController(fake, new JournalStub()).StepAsync());
        Assert.Single(fake.Calls, x => x == "submit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleAmmoAndOccupiedSlotsAreNotOverwritten(bool occupied)
    {
        var loadout = JsonSerializer.SerializeToNode(Loadout("pistol", "ammo", false), Protocol.Json)!;
        if (occupied) loadout["slots"]![0]!["ammo"] = "shotgun-shell";
        else loadout["carried"]![0]!["bullet"] = false;
        var fake = new GameStub { Armed = false, Loadout = loadout };
        await new DefenseController(fake, new JournalStub()).StepAsync();
        Assert.Equal(["observe"], fake.Calls);
    }

    [Fact]
    public async Task EmptyNativeWeaponSlotsMayOmitNilGunAndAmmoFields()
    {
        var loadout = JsonSerializer.SerializeToNode(Loadout(null, "gun", false), Protocol.Json)!;
        loadout["slots"]![0]!.AsObject().Remove("gun");
        loadout["slots"]![0]!.AsObject().Remove("ammo");
        var fake = new GameStub { Armed = false, Loadout = loadout };
        await new DefenseController(fake, new JournalStub()).StepAsync();
        Assert.Equal("equip", fake.Submission?.Kind);
    }

    [Theory]
    [InlineData("manual", true, false, false)]
    [InlineData("ai", false, false, false)]
    [InlineData("ai", true, true, false)]
    [InlineData("ai", true, false, true)]
    public async Task CarriedEquipmentDoesNotBypassControlOrReplaceAReadyWeapon(string mode, bool alive, bool stopping, bool ready)
    {
        var fake = new GameStub { Mode = mode, Alive = alive, Stopping = stopping, Armed = ready,
            EnemiesEmptyObject = true, Loadout = Loadout("pistol", "ammo", false) };
        await new DefenseController(fake, new JournalStub()).StepAsync();
        Assert.Equal(["observe"], fake.Calls);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("negative-rounds")]
    [InlineData("duplicate-source")]
    public async Task MalformedLoadoutCannotTriggerAnEquipmentTransfer(string failure)
    {
        var loadout = JsonSerializer.SerializeToNode(Loadout("pistol", "ammo", false), Protocol.Json)!;
        if (failure == "incomplete") loadout["complete"] = false;
        if (failure == "negative-rounds") loadout["carried"]![0]!["rounds"] = -1;
        if (failure == "duplicate-source") loadout["carried"]!.AsArray().Add(loadout["carried"]![0]!.DeepClone());
        var fake = new GameStub { Armed = false, Loadout = loadout };
        await Assert.ThrowsAsync<InvalidDataException>(() => new DefenseController(fake, new JournalStub()).StepAsync());
        Assert.Equal(["observe"], fake.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnarmedActorUsesObservedCarriedAmmunitionBeforeProduction(bool enemy)
    {
        var fake = new GameStub { Armed = false, EnemiesEmptyObject = !enemy,
            Loadout = Loadout("pistol", "ammo", false) };
        var step = await new DefenseController(fake, new JournalStub()).StepAsync();
        Assert.Equal("defending", step.State);
        Assert.Equal("equip", fake.Submission?.Kind);
        Assert.Equal("ammo", fake.Submission!.Args.GetProperty("compartment").GetString());
        Assert.Equal(7, fake.Submission.Args.GetProperty("sourceSlot").GetInt32());
        Assert.Equal(3, fake.Submission.Args.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task EquipmentRestorePreemptsWorkOnlyWhenAnEnemyIsVisible()
    {
        foreach (bool enemy in new[] { false, true })
        {
            var fake = new GameStub { Armed = false, EnemiesEmptyObject = !enemy,
                Loadout = Loadout("pistol", "ammo", false), Active = Receipt("work", "craft", "running") };
            await new DefenseController(fake, new JournalStub()).StepAsync();
            Assert.Equal(enemy ? new[] { "observe", "cancel" } : new[] { "observe" }, fake.Calls);
        }
    }

    [Fact]
    public async Task RestoresCarriedGunToAnEmptySlotAndSelectsAnAlreadyLoadedSlot()
    {
        var gun = new GameStub { Armed = false, Loadout = Loadout(null, "gun", false) };
        await new DefenseController(gun, new JournalStub()).StepAsync();
        Assert.Equal("equip", gun.Submission?.Kind);
        Assert.Equal("gun", gun.Submission!.Args.GetProperty("compartment").GetString());
        Assert.Equal(1, gun.Submission.Args.GetProperty("count").GetInt32());
        var loaded = new GameStub { Armed = false, Loadout = Loadout("pistol", "ammo", true) };
        await new DefenseController(loaded, new JournalStub()).StepAsync();
        Assert.Equal("select_weapon", loaded.Submission?.Kind);
        Assert.Equal(2, loaded.Submission!.Args.GetProperty("slot").GetInt32());
    }

    [Fact]
    public async Task LostEquipmentReceiptIsQueriedWithoutRepeatingTheTransfer()
    {
        var fake = new GameStub { Armed = false, Loadout = Loadout("pistol", "ammo", false), LoseSubmitResponse = true };
        var controller = new DefenseController(fake, new JournalStub());
        await Assert.ThrowsAsync<OperationOutcomeUnknownException>(() => controller.StepAsync());
        await controller.StepAsync();
        await controller.StepAsync();
        Assert.Equal("equip", fake.Submission!.Kind);
        Assert.Single(fake.Calls, x => x == "submit");
        Assert.Single(fake.Calls, x => x == "operation");
    }

    private static object Loadout(string? gun, string carriedKind, bool loaded) => new
    {
        complete = true,
        slots = new[] { new { index = 2, gun, bulletGun = gun is not null, range = 15d,
            ammo = loaded ? "firearm-magazine" : null, rounds = loaded ? 9 : 0, ready = loaded } },
        carried = new[] { new { slot = 7, name = carriedKind == "gun" ? "pistol" : "firearm-magazine", kind = carriedKind,
            count = carriedKind == "gun" ? 1 : 3, bullet = true, range = carriedKind == "gun" ? 15d : 0d, rounds = carriedKind == "gun" ? 0 : 24 } }
    };

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
        public bool CompleteSubmission { get; init; }
        public bool LoseCancelResponse { get; init; }
        public bool EnemiesEmptyObject { get; init; }
        public object? Loadout { get; init; }
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
                            position = new MapPosition(0, 0), health = 250, weapon = new { ready = Armed, rounds = Armed ? 100 : 0, range = 15 }, loadout = Loadout },
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
                    Active = Receipt(Submission.OperationId, Submission.Kind, CompleteSubmission ? "completed" : "running");
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
