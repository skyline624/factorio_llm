using System.Text.Json;
using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>Electric connectivity proven from one factory photograph, independent of the actor's view distance.</summary>
public static class FactoryPower
{
    /// <summary>True when a native power source shares the entity's network, false when none does or the entity is
    /// unknown, and null when the mod does not report network identities (older saves).</summary>
    public static bool? IsFed(FactorySnapshot snapshot, string entityId)
    {
        var entity = snapshot.Records.FirstOrDefault(r => r.Kind == "entity" && r.EntityId == entityId);
        if (entity is null) return false;
        if (!entity.Data.TryGetProperty("electricNetworkId", out var network) || network.ValueKind != JsonValueKind.Number) return null;
        long id = network.GetInt64();
        return snapshot.Records.Any(r => r.Kind == "entity" && r.EntityId != entityId
            && r.Data.TryGetProperty("type", out var type) && FactoryCellBuilder.IsPowerSource(type.GetString() ?? "")
            && r.Data.TryGetProperty("electricNetworkId", out var other) && other.ValueKind == JsonValueKind.Number && other.GetInt64() == id);
    }
}
