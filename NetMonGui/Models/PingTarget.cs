namespace NetMonGui.Models;

/// <summary>
/// A user-added extra ping destination, tracked in parallel with the app's default target
/// (e.g. a game server's datacenter, to compare against what the game itself reports).
/// </summary>
public sealed class PingTarget
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string Host { get; init; }
}
