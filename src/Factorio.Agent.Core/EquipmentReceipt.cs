using System.Text.Json;

namespace Factorio.Agent.Core;

public static class EquipmentReceipt
{
    public static void Validate(OperationSubmission request, OperationReceipt receipt)
    {
        if (request.Kind is not ("equip" or "select_weapon") || !receipt.IsTerminal) return;
        try
        {
            var args = request.Args;
            var effect = receipt.Effects;
            if (receipt.OperationId != request.OperationId || receipt.Kind != request.Kind
                || receipt.Status is not ("completed" or "partial"))
                throw new InvalidDataException("The native equipment action did not complete.");
            int slot = args.GetProperty("slot").GetInt32();
            if (request.Kind == "select_weapon")
            {
                if (effect.GetProperty("selectedSlot").GetInt32() != slot)
                    throw new InvalidDataException("The native weapon selection was not established.");
                return;
            }
            string item = args.GetProperty("item").GetString()!;
            string compartment = args.GetProperty("compartment").GetString()!;
            int requested = args.GetProperty("count").GetInt32();
            int moved = effect.GetProperty("transferred").GetInt32();
            var before = effect.GetProperty("equipmentBefore");
            var after = effect.GetProperty("equipmentAfter");
            string inventory = compartment == "gun" ? "guns" : "ammo";
            if (effect.GetProperty("slot").GetInt32() != slot
                || effect.GetProperty("sourceSlot").GetInt32() != args.GetProperty("sourceSlot").GetInt32()
                || effect.GetProperty("item").GetString() != item || effect.GetProperty("compartment").GetString() != compartment
                || effect.GetProperty("requested").GetInt32() != requested || moved < 1 || moved > requested
                || receipt.Status == "completed" && moved != requested
                || after.GetProperty("selectedSlot").GetInt32() != slot
                || Stock(before, "main", item) - Stock(after, "main", item) != moved
                || Stock(after, inventory, item) - Stock(before, inventory, item) != moved
                || Rounds(before) != Rounds(after))
                throw new InvalidDataException("Native equipment stock, destination or ammunition accounting is inconsistent.");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException or FormatException)
        {
            throw new InvalidDataException("Native equipment receipt lacks valid transfer evidence.", error);
        }
    }

    private static long Stock(JsonElement state, string inventory, string item)
    {
        long count = state.GetProperty(inventory).TryGetProperty(item, out var value) ? value.GetInt64() : 0;
        return count >= 0 ? count : throw new InvalidDataException("Negative equipment stock.");
    }

    private static long Rounds(JsonElement state)
    {
        long main = state.GetProperty("mainRounds").GetInt64(), loaded = state.GetProperty("loadedRounds").GetInt64();
        if (main < 0 || loaded < 0) throw new InvalidDataException("Negative ammunition count.");
        return checked(main + loaded);
    }
}
