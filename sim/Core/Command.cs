using Planetfall.Sim.World;

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

/// <summary>Hand alloy (whole units) to another nation. A minor power or rival warms to the giver.</summary>
public sealed record GiftCommand(int Player, int To, int Alloy) : Command(Player);

/// <summary>Ask another nation to move one step up the ladder: War, Neutral, Non-Aggression, Trade Pact, Alliance. It answers at once, with a reason.</summary>
public sealed record ProposeTreatyCommand(int Player, int To, Treaty Treaty) : Command(Player);

/// <summary>Drop a pact back to Neutral. Allowed, but it makes you Treacherous to everyone and blocks new treaties for five minutes.</summary>
public sealed record BreakTreatyCommand(int Player, int With) : Command(Player);

/// <summary>Go to war with another nation. Doing so against a partner under a pact is a betrayal.</summary>
public sealed record DeclareWarCommand(int Player, int On) : Command(Player);

/// <summary>Begin bringing an allied minor power into your nation: costs Influence, takes 60 s, needs opinion 75 or more.</summary>
public sealed record IntegrateCommand(int Player, int Minor) : Command(Player);

/// <summary>Ask a nation you are at war with to surrender to you. An AI answers with its Hopelessness verdict and a reason.</summary>
public sealed record DemandSurrenderCommand(int Player, int From) : Command(Player);
