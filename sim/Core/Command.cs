namespace Planetfall.Sim.Core;

/// <summary>Every input to the sim, human or AI, is a Command. The command log is the replay.</summary>
public abstract record Command(int Player);

/// <summary>Order a stack to a tile. Queue=true appends the route after its current one (Shift).</summary>
public sealed record MoveCommand(int Player, int StackId, int TargetTile, bool Queue = false) : Command(Player);

/// <summary>Cancel the route and any hop that has not completed.</summary>
public sealed record StopCommand(int Player, int StackId) : Command(Player);

/// <summary>Give up. The other major receives every tile and the match ends.</summary>
public sealed record SurrenderCommand(int Player) : Command(Player);

/// <summary>Buy one unit of a type; it appears as a fresh stack on a spawn site (capital, owned mine or depot).</summary>
public sealed record RecruitCommand(int Player, string UnitId, int Tile = -1) : Command(Player);

/// <summary>Order a stack that includes an engineer to build a structure on the tile it stands on.</summary>
public sealed record BuildCommand(int Player, int StackId, string StructureId) : Command(Player);
