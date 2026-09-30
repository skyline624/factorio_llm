namespace Factorio.Agent.Host;

/// <summary>
/// The engine, or the native geometry checked before submission, refused one placement; the world is unchanged.
/// Manual control, lease loss, travel failures and missing items are never refusals.
/// </summary>
public sealed class PlacementRefusedException(string message) : InvalidOperationException(message);
