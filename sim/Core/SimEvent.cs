namespace Planetfall.Sim.Core;

/// <summary>What happened this tick. UI, audio and game-feel subscribe; nothing polls the state.</summary>
public abstract record SimEvent;

public sealed record TileCaptured(int Tile, int NewOwner, int PreviousOwner) : SimEvent;
public sealed record StackSpawned(int StackId, int Owner, int Tile) : SimEvent;
public sealed record StackDied(int StackId, int Owner, int Tile) : SimEvent;
public sealed record StacksMerged(int IntoStackId, int AbsorbedStackId) : SimEvent;
public sealed record StackRetreating(int StackId, int ToTile) : SimEvent;
public sealed record HopStarted(int StackId, int From, int To) : SimEvent;
public sealed record HopCompleted(int StackId, int Tile) : SimEvent;
public sealed record PlayerDefeated(int Player, string Reason) : SimEvent;
public sealed record MatchOver(int Winner) : SimEvent;
public sealed record BuildStarted(int Tile, int Owner, string Structure) : SimEvent;
public sealed record StructureBuilt(int Tile, int Owner, string Structure) : SimEvent;
public sealed record StructureLost(int Tile, string Structure) : SimEvent;
