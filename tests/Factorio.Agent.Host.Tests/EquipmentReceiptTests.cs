using System.Text.Json.Nodes;
using Factorio.Agent.Core;
using Xunit;

namespace Factorio.Agent.Host.Tests;

public sealed class EquipmentReceiptTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("created-rounds")]
    [InlineData("wrong-slot")]
    [InlineData("missing-source-debit")]
    [InlineData("wrong-item")]
    [InlineData("negative-stock")]
    public void TransferNeedsMatchingDestinationAndConservedNativeItemsAndRounds(string variant)
    {
        var request = OperationSubmission.Create(new("world", "session", "actor", 1, 2), "equip",
            new { compartment = "ammo", slot = 1, sourceSlot = 7, item = "firearm-magazine", count = 3 }, 1000);
        var effect = JsonNode.Parse("""
            {"compartment":"ammo","slot":1,"sourceSlot":7,"item":"firearm-magazine","requested":3,"transferred":3,
             "equipmentBefore":{"main":{"firearm-magazine":3},"ammo":{},"mainRounds":24,"loadedRounds":0,"selectedSlot":1},
             "equipmentAfter":{"main":{},"ammo":{"firearm-magazine":3},"mainRounds":0,"loadedRounds":24,"selectedSlot":1}}
            """)!;
        if (variant == "created-rounds") effect["equipmentAfter"]!["loadedRounds"] = 30;
        if (variant == "wrong-slot") effect["slot"] = 2;
        if (variant == "missing-source-debit") effect["equipmentAfter"]!["main"]!["firearm-magazine"] = 3;
        if (variant == "wrong-item") effect["item"] = "piercing-rounds-magazine";
        if (variant == "negative-stock") effect["equipmentBefore"]!["main"]!["firearm-magazine"] = -3;
        var receipt = OperationReceipt.Parse(Protocol.ToElement(new { operationId = request.OperationId,
            kind = "equip", status = "completed", acceptedTick = 10, updatedTick = 10, effects = effect }), request.OperationId);
        if (variant == "valid") EquipmentReceipt.Validate(request, receipt);
        else Assert.Throws<InvalidDataException>(() => EquipmentReceipt.Validate(request, receipt));
    }
}
