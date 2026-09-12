namespace Factorio.Agent.Core;

public sealed record FeederStockReading(long Source, long InHand)
{
    public long DeliveredSince(FeederStockReading previous)
    {
        long delivered = checked(previous.Source + previous.InHand - Source - InHand);
        if (Source < 0 || InHand < 0 || previous.Source < 0 || previous.InHand < 0 || delivered < 0)
            throw new InvalidDataException("Fuel feeder stock increased without a recorded supply operation.");
        return delivered;
    }

    public static FeederStockReading From(FactorySnapshot snapshot, string chestId, string inserterId, string fuel)
    {
        snapshot.SummarizeStocks();
        long Count(FactoryRecord record) => record.Data.GetProperty("items").TryGetProperty(fuel, out var value) ? value.GetInt64() : 0;
        var source = snapshot.Records.Where(r => r.EntityId == chestId && r.Kind == "inventory").ToArray();
        var hand = snapshot.Records.Where(r => r.EntityId == inserterId && r.Kind == "transit").ToArray();
        if (source.Length != 1 || hand.Length != 1)
            throw new InvalidDataException("Missing or ambiguous native feeder inventory or hand record.");
        return new(Count(source[0]), Count(hand[0]));
    }
}
