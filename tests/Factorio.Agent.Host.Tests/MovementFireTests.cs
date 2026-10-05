using System.Text.Json.Nodes;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class MovementFireTests
{
    [Theory]
    [InlineData("completed")]
    [InlineData("cancelled")]
    public void MovingFireProvesItsSubmittedTargetAndConservesAllActorRounds(string status)
    {
        var request = Request();
        MovementFireReceipt.Validate(request, Receipt(request, status, Effects()));
    }

    [Theory]
    [InlineData("wrong-target")]
    [InlineData("missing-mode")]
    [InlineData("wrong-kind")]
    [InlineData("no-accounting")]
    [InlineData("new-rounds")]
    [InlineData("negative")]
    [InlineData("wrong-consumption")]
    [InlineData("future-accounting")]
    [InlineData("unknown")]
    public void MissingOrInconsistentMovingFireEvidenceCannotBeAccepted(string invalid)
    {
        var request = Request(); var effects = Effects();
        switch (invalid)
        {
            case "wrong-target": effects["firingTargetId"] = "another-enemy"; break;
            case "missing-mode": effects.Remove("movementFire"); break;
            case "no-accounting": effects.Remove("ammoAccounting"); break;
            case "new-rounds": effects["ammoAccounting"]!["survivingRounds"] = 401; break;
            case "negative": effects["ammoAccounting"]!["beforeRounds"] = -1; break;
            case "wrong-consumption": effects["roundsConsumed"] = 5; break;
            case "future-accounting": effects["ammoAccounting"]!["collectedTick"] = 111; break;
            case "unknown": effects["ammoAccounting"]!["status"] = "unresolved-native-corpse"; break;
        }
        var receipt = Receipt(request, "completed", effects);
        if (invalid == "wrong-kind") receipt = receipt with { Kind = "shoot" };
        Assert.Throws<InvalidDataException>(() => MovementFireReceipt.Validate(request, receipt));
    }

    [Fact]
    public void OrdinaryMovementDoesNotInferAnyShootingOrNeedAnAmmunitionReceipt()
    {
        var request = OperationSubmission.Create(new("world", "session", "actor", 1, 2), "move", new { position = new MapPosition(2, 0) }, 200);
        MovementFireReceipt.Validate(request, Receipt(request, "completed", new JsonObject()));
    }

    [Fact]
    public void APackFocusesAVisiblyWoundedUnitWithinTheCurrentGunRange()
    {
        var (_, _, state, _) = PortableDefenseTests.Fixture();
        state = state with { Enemies = [new("closest", new(5, 0), "unit", 75), new("wounded", new(8, 0), "unit", 10),
            new("third", new(12, 0), "unit", 50), new("unreachable", new(20, 0), "unit", 1)] };
        Assert.Equal("wounded", DefensePolicy.SelectTarget(state)!.Id);
        var legacy = state with { Enemies = state.Enemies.Select(e => e with { Type = null, Health = null }).ToArray() };
        Assert.Equal("closest", DefensePolicy.SelectTarget(legacy)!.Id);
    }

    private static OperationSubmission Request() => OperationSubmission.Create(new("world", "session", "actor", 1, 2), "move",
        new { position = new MapPosition(-2, 0), tolerance = .15, shootEntityId = "visible-biter" }, 200);
    private static JsonObject Effects() => JsonNode.Parse("""
        {"movementFire":"native-walking-and-shooting","firingTargetId":"visible-biter","roundsConsumed":2,
         "ammoAccounting":{"status":"observed-native-actor","beforeRounds":400,"survivingRounds":398,"collectedTick":110}}
        """)!.AsObject();
    private static OperationReceipt Receipt(OperationSubmission request, string status, object effects) => OperationReceipt.Parse(
        Protocol.ToElement(new { request.OperationId, kind = "move", status, acceptedTick = 100, updatedTick = 110, effects }), request.OperationId);
}
