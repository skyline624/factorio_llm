using System.Text.Json;

namespace Factorio.Agent.Core;

/// <summary>Concurrent native walking and shooting must retain one operation identity and exact actor round accounting.</summary>
public static class MovementFireReceipt
{
    public static void Validate(OperationSubmission request, OperationReceipt receipt)
    {
        if (request.Kind != "move" || !request.Args.TryGetProperty("shootEntityId", out var target)
            || !receipt.IsTerminal || receipt.Status is not ("completed" or "partial" or "cancelled")) return;
        try
        {
            var effects = receipt.Effects;
            var accounting = effects.GetProperty("ammoAccounting");
            string? status = accounting.GetProperty("status").GetString();
            long before = accounting.GetProperty("beforeRounds").GetInt64(), surviving = accounting.GetProperty("survivingRounds").GetInt64();
            long consumed = effects.GetProperty("roundsConsumed").GetInt64();
            if (receipt.OperationId != request.OperationId || receipt.Kind != "move"
                || effects.GetProperty("firingTargetId").GetString() != target.GetString()
                || effects.GetProperty("movementFire").GetString() != "native-walking-and-shooting"
                || status is not ("observed-native-actor" or "reconciled-native-corpse")
                || before < 0 || surviving < 0 || surviving > before || consumed != before - surviving
                || accounting.GetProperty("collectedTick").GetInt64() > receipt.UpdatedTick)
                throw new InvalidDataException("Moving fire lacks matching native target and ammunition evidence.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or FormatException)
        { throw new InvalidDataException("Invalid native moving fire receipt.", error); }
    }
}
