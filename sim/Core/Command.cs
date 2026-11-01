namespace Planetfall.Sim.Core;

/// <summary>Every input to the sim, human or AI, is a Command. The command log is the replay.</summary>
public abstract record Command(int Player);

/// <summary>Order a stack to a tile. Queue=true appends the route after its current one (Shift).</summary>
public sealed record MoveCommand(int Player, int StackId, int TargetTile, bool Queue = false) : Command(Player);

/// <summary>Cancel the route and any hop that has not completed.</summary>
public sealed record StopCommand(int Player, int StackId) : Command(Player);
