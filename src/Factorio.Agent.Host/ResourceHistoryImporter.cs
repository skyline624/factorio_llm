using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

public sealed record ResourceHistoryImport(int FilesRead, int ConfirmedOperations, IReadOnlyList<ResourceSighting> Resources);

/// <summary>Recovers dated locations from matching native mining submissions and completed receipts.</summary>
public static class ResourceHistoryImporter
{
    public static ResourceSighting? Correlate(OperationSubmission submission, OperationReceipt receipt,
        ProductionCatalog catalog, SpatialSnapshot current)
    {
        if (submission.Kind != "mine" || receipt.Kind != "mine" || receipt.Status != "completed" || receipt.Error is not null
            || submission.OperationId != receipt.OperationId || submission.Scope.WorldId != current.Scope.WorldId
            || receipt.AcceptedTick is null or < 0 || receipt.UpdatedTick < receipt.AcceptedTick
            || receipt.UpdatedTick > current.CollectedTick || receipt.UpdatedTick > submission.DeadlineTick) return null;
        try
        {
            string canonical = JsonSerializer.Serialize(new { scope = submission.Scope, kind = submission.Kind, args = submission.Args,
                preconditions = submission.Preconditions, deadlineTick = submission.DeadlineTick }, Protocol.Json);
            if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))) != submission.Fingerprint) return null;
            string name = submission.Args.GetProperty("name").GetString()!;
            var position = submission.Args.GetProperty("position").Deserialize<MapPosition>(Protocol.Json)!;
            string target = receipt.Effects.GetProperty("targetId").GetString()!;
            string[] parts = target.Split(':');
            if (parts.Length != 4 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int surface)
                || surface != current.SurfaceIndex || parts[1] != name
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                || !double.IsFinite(x) || !double.IsFinite(y) || position != new MapPosition(x, y)
                || !catalog.Mining.TryGetValue(name, out var products)
                || !products.Any(p => p.Name == receipt.Effects.GetProperty("product").GetString() && p.DeterministicItem)
                || !(receipt.Effects.GetProperty("produced").GetDouble() > 0)) return null;
            return new(target, name, position, receipt.UpdatedTick);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or NullReferenceException)
        {
            return null; // Incomplete legacy evidence cannot seed a location.
        }
    }

    public static async Task<ResourceHistoryImport> ReadAsync(string directory, ProductionCatalog catalog, SpatialSnapshot current, CancellationToken token)
    {
        string[] files = Directory.EnumerateFiles(directory, "*.jsonl").Order(StringComparer.Ordinal).Take(1025).ToArray();
        if (files.Length > 1024 || files.Sum(p => new FileInfo(p).Length) > 512L * 1024 * 1024)
            throw new InvalidOperationException("Historical journal import exceeds its bounded read budget.");
        int confirmed = 0;
        var sightings = new Dictionary<string, ResourceSighting>(StringComparer.Ordinal);
        foreach (string path in files)
        {
            var submissions = new Dictionary<string, OperationSubmission>(StringComparer.Ordinal);
            await foreach (string line in File.ReadLinesAsync(path, token))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (!root.TryGetProperty("type", out var type) || !root.TryGetProperty("data", out var data)) continue;
                    if (type.GetString() == "submission" && data.GetProperty("kind").GetString() == "mine")
                    {
                        var submission = data.Deserialize<OperationSubmission>(Protocol.Json)!;
                        submissions[submission.OperationId] = submission;
                    }
                    else if (type.GetString() == "receipt" && data.GetProperty("kind").GetString() == "mine")
                    {
                        var receipt = data.Deserialize<OperationReceipt>(Protocol.Json)!;
                        if (submissions.TryGetValue(receipt.OperationId, out var submission)
                            && Correlate(submission, receipt, catalog, current) is { } sighting)
                        {
                            confirmed++;
                            if (!sightings.TryGetValue(sighting.EntityId, out var older) || older.ObservedTick < sighting.ObservedTick)
                                sightings[sighting.EntityId] = sighting;
                        }
                    }
                }
                catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
                { /* Unrecognized or incomplete lines provide no evidence. */ }
            }
        }
        return new(files.Length, confirmed, sightings.Values.OrderByDescending(s => s.ObservedTick).ToArray());
    }
}
