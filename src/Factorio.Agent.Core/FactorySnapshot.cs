using System.Collections.ObjectModel;
using System.Text.Json;

namespace Factorio.Agent.Core;

public sealed record FactoryRecord(string Id, string Kind, string EntityId, string Name, JsonElement Data);

public sealed record FactorySnapshot(string SnapshotId, ActorScope Scope, long CollectedTick, long ExpiresTick,
    JsonElement Coverage, IReadOnlyList<FactoryRecord> Records)
{
    public IReadOnlyList<FactoryRecord> FluidRecordsAt(string entityId, int? boxIndex = null)
    {
        SummarizeStocks();
        return Records.Where(r => r.Kind == "fluid" && ((boxIndex is null && r.EntityId == entityId)
            || (r.Data.TryGetProperty("sourceBoxes", out var boxes) && boxes.ValueKind == JsonValueKind.Array
                && boxes.EnumerateArray().Any(b => b.GetProperty("entityId").GetString() == entityId
                    && (boxIndex is null || b.GetProperty("index").GetInt32() == boxIndex))))).ToArray();
    }

    public double FluidStockAt(string entityId, string fluid, int? boxIndex = null) => FluidRecordsAt(entityId, boxIndex)
        .Sum(r => r.Data.GetProperty("contents").TryGetProperty(fluid, out var value) ? value.GetDouble() : 0);

    public FactoryStockSummary SummarizeStocks()
    {
        var inventories = new Dictionary<string, long>(StringComparer.Ordinal);
        var transit = new Dictionary<string, long>(StringComparer.Ordinal);
        var fluids = new Dictionary<string, double>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (FactoryRecord record in Records)
        {
            if (!ids.Add(record.Id)) throw new InvalidDataException("A stock record appears more than once.");
            if (record.Kind is "inventory" or "transit")
            {
                Dictionary<string, long> target = record.Kind == "inventory" ? inventories : transit;
                if (!record.Data.TryGetProperty("items", out JsonElement items) || items.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Missing native item counts.");
                foreach (JsonProperty item in items.EnumerateObject())
                {
                    if (item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetInt64(out long quantity) || quantity < 0)
                        throw new InvalidDataException("Invalid native item stock.");
                    target[item.Name] = checked(target.GetValueOrDefault(item.Name) + quantity);
                }
            }
            if (record.Kind == "fluid")
            {
                if (!record.Data.GetProperty("aggregateSafe").GetBoolean())
                    throw new InvalidDataException("An undeduplicated fluid sample cannot be aggregated.");
                if (!record.Data.TryGetProperty("contents", out JsonElement contents) || contents.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Missing native fluid contents.");
                foreach (JsonProperty fluid in contents.EnumerateObject())
                {
                    if (fluid.Value.ValueKind != JsonValueKind.Number) throw new InvalidDataException("Invalid fluid amount.");
                    double amount = fluid.Value.GetDouble();
                    double total = fluids.GetValueOrDefault(fluid.Name) + amount;
                    if (!double.IsFinite(amount) || amount < 0 || !double.IsFinite(total))
                        throw new InvalidDataException("Invalid native fluid stock.");
                    fluids[fluid.Name] = total;
                }
            }
        }
        return new(new ReadOnlyDictionary<string, long>(inventories), new ReadOnlyDictionary<string, long>(transit),
            new ReadOnlyDictionary<string, double>(fluids));
    }
}

/// <summary>Physical inventories, moving items, and deduplicated fluid stores are deliberately separate.</summary>
public sealed record FactoryStockSummary(IReadOnlyDictionary<string, long> InventoryItems,
    IReadOnlyDictionary<string, long> TransitItems, IReadOnlyDictionary<string, double> Fluids);

public sealed record FactorySnapshotPage(string SnapshotId, ActorScope Scope, ActorScope SnapshotScope,
    long CollectedTick, long ExpiresTick, int TotalRecords, int Offset, int NextOffset, bool Complete,
    JsonElement Coverage, IReadOnlyList<FactoryRecord> Records)
{
    public static FactorySnapshotPage Parse(GameResponse response)
    {
        if (!response.Ok) throw new GameRpcException(response.Error ?? new("invalid_response", "Factory snapshot failed."));
        try
        {
            JsonElement data = response.Data;
            var records = new List<FactoryRecord>();
            JsonElement array = data.GetProperty("records");
            if (array.ValueKind == JsonValueKind.Array)
                records.AddRange(array.EnumerateArray().Select(e => e.Deserialize<FactoryRecord>(Protocol.Json)
                    ?? throw new InvalidDataException("Null factory record.")));
            else if (array.ValueKind != JsonValueKind.Object || array.EnumerateObject().Any())
                throw new InvalidDataException("Invalid native record collection.");
            var page = new FactorySnapshotPage(data.GetProperty("snapshotId").GetString()!,
                data.GetProperty("scope").Deserialize<ActorScope>(Protocol.Json)!,
                data.GetProperty("snapshotScope").Deserialize<ActorScope>(Protocol.Json)!,
                data.GetProperty("collectedTick").GetInt64(), data.GetProperty("expiresTick").GetInt64(),
                data.GetProperty("totalRecords").GetInt32(), data.GetProperty("offset").GetInt32(),
                data.GetProperty("nextOffset").GetInt32(), data.GetProperty("complete").GetBoolean(),
                data.GetProperty("coverage").Clone(), records.AsReadOnly());
            if (string.IsNullOrWhiteSpace(page.SnapshotId) || page.Scope is null || page.SnapshotScope is null
                || !ValidScope(page.Scope) || !ValidScope(page.SnapshotScope)
                || page.CollectedTick < 0 || page.CollectedTick > response.Tick || page.ExpiresTick < response.Tick
                || page.TotalRecords is < 0 or > 150000 || page.Offset < 0 || page.Offset > page.TotalRecords
                || page.NextOffset != page.Offset + records.Count || page.NextOffset > page.TotalRecords
                || page.Complete != (page.NextOffset == page.TotalRecords) || (!page.Complete && records.Count == 0)
                || !page.Coverage.GetProperty("atomic").GetBoolean()
                || !page.Coverage.GetProperty("knownInventoriesComplete").GetBoolean()
                || !page.Coverage.GetProperty("knownBeltAndInserterTransitComplete").GetBoolean()
                || !page.Coverage.GetProperty("fluidSegmentsDeduplicated").GetBoolean())
                throw new InvalidDataException("Inconsistent factory snapshot page.");
            foreach (FactoryRecord record in records)
            {
                if (string.IsNullOrWhiteSpace(record.Id) || string.IsNullOrWhiteSpace(record.EntityId)
                    || string.IsNullOrWhiteSpace(record.Name) || record.Data.ValueKind != JsonValueKind.Object
                    || record.Kind is not ("entity" or "inventory" or "transit" or "fluid" or "work"))
                    throw new InvalidDataException("Invalid factory record identity or kind.");
            }
            return page;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Invalid native factory snapshot; partial data is not a complete stock.", error);
        }
    }

    private static bool ValidScope(ActorScope scope) => !string.IsNullOrWhiteSpace(scope.WorldId)
        && !string.IsNullOrWhiteSpace(scope.SessionId) && !string.IsNullOrWhiteSpace(scope.ActorId)
        && scope.Incarnation >= 0 && scope.Generation >= 0;
}
