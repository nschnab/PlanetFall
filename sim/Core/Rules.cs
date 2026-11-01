namespace Planetfall.Sim.Core;

/// <summary>Base values before the StatResolver. Tuning lives here until the data files arrive (M4).</summary>
public static class Rules
{
    public const int TickMs = 100;                  // 10 Hz
    public const int HopDelayMs = 3000;             // stat: move.delay
    public const int SpeedPermille = 1000;          // stat: move.speed (timer rate)
    public const int BattleSlowPermille = 400;      // hop rate while in a fight, entering or leaving
    public const int DamagePerStrPerTickMilli = 12; // 0.12 hp per strength per second
    public const int StartStrength = 10;
    public const int NewStackStrength = 5;
    public const long HpUnit = 1000;                // hp is stored in thousandths
    public const long TileYieldMilli = 20;          // stat: yield.alloy, per owned tile per tick (0.2/s)
    public const int DepositMultiplier = 5;
    public const long StackCostMilli = 20_000;      // stat: build.cost.stack (20 alloy)

    public const string StatMoveDelay = "move.delay";
    public const string StatMoveSpeed = "move.speed";
    public const string StatYield = "yield.alloy";
    public const string StatStackCost = "build.cost.stack";
}
