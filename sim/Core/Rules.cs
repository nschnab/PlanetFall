namespace Planetfall.Sim.Core;

/// <summary>Base values before the StatResolver. Tuning lives here until the data files arrive (M4).</summary>
public static class Rules
{
    public const int TickMs = 100;                  // 10 Hz
    public const int RiverCrossPermille = 1500;     // a hop across a river edge takes 50% longer
    public const int HopDelayMs = 8000;             // stat: move.delay
    public const int SpeedPermille = 1000;          // stat: move.speed (timer rate)
    public const int BattleSlowPermille = 400;      // hop rate while in a fight, entering or leaving
    public const int DamagePerStrPerTickMilli = 12; // 0.12 hp per strength per second
    public const int StartStrength = 10;
    public const int NewStackStrength = 5;
    public const long HpUnit = 1000;                // hp is stored in thousandths
    public const long TileYieldMilli = 20;          // stat: yield.alloy, per owned tile per tick (0.2/s)
    public const int DepositMultiplier = 5;
    public const long StackCostMilli = 20_000;      // stat: build.cost.stack (20 alloy)
    public const int CapitulationTicks = 600;       // enemy holds your capital this long (60 s) and you capitulate
    public const int MinorStrength = 8;
    public const long EnergyDepositMilli = 100;     // stat: yield.energy, per Energy deposit per tick
    public const long RelicInfluenceMilli = 50;     // stat: yield.influence, per Relic site per tick
    public const long CapitalEnergyMilli = 30;      // fixed capital bonus per tick
    public const int UnlinkedPermille = 500;        // tiles cut off from the supply network yield half
    public const int StarvedAlloyPermille = 500;    // alloy income while upkeep outruns Energy

    public const int AttackerPenaltyPermille = 1150; // attackers take 15% more damage than defenders
    public const int FlankBonusPermille = 250;       // +25% damage per extra front a stack is attacked from
    public const int MaxFronts = 4;
    public const int EntrenchStartTicks = 100;       // 10 s standing still, then every 10 s...
    public const int EntrenchStepPermille = 100;     // ...+10% defence...
    public const int EntrenchMaxPermille = 500;      // ...up to +50%

    public const string StatMoveDelay = "move.delay";
    public const string StatMoveSpeed = "move.speed";
    public const string StatYield = "yield.alloy";
    public const string StatStackCost = "build.cost.stack";
}
