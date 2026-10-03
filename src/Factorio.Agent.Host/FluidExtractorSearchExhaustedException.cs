using Factorio.Agent.Core;

namespace Factorio.Agent.Host;

/// <summary>The observed site search ended before an extractor was planned or built. Native work with an unknown outcome is not this refusal.</summary>
public sealed class FluidExtractorSearchExhaustedException(string resource, ActorScope scope, long observedTick, int observations)
    : InvalidOperationException($"No usable {resource} extractor site was found within {observations} observed search steps.")
{
    public string Resource { get; } = resource;
    public ActorScope Scope { get; } = scope;
    public long ObservedTick { get; } = observedTick;
    public int Observations { get; } = observations;
}
