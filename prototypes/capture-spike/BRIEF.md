# M1 capture spike - THROWAWAY

QUESTION:   Is real-time stack capture on irregular Voronoi tiles readable and tense?
CORE VERB:  Move your stack tile to tile; hold a tile to capture it while an enemy stack roams.
THROWAWAY:  Yes. GDScript, one file, hardcoded numbers. Never imported by the game. Delete after the verdict.
TIMEBOX:    One sitting.

KEEP IF (placeholder art, no explanation of controls):
  - a fresh player moves + captures on purpose within ~30 s unprompted
  - they can say at a glance which tiles are theirs / contested / enemy
  - the roaming enemy creates "go there or hold here?" moments
  - they ask for "one more game"
KILL / REFACTOR IF:
  - can't tell tile ownership or capture progress without being told
  - tiles feel too small/large to click or to path across
  - capture feels like waiting (nothing to decide)
  - tension only appears if we add far more systems

Controls: left-click a tile = move there. Space = pause. R = restart.
Win: own 75% of tiles. Lose: your stack is destroyed (no respawn).
Run: godot --path prototypes/capture-spike

## Design outcomes (approved by Nolen)
- Orders show an arrow; after a delay the stack hops in and captures the tile instantly (no capture timer).
- A fight starts when a hop order into an enemy tile begins, not on arrival.
- Hops into or out of a fight are slowed (BATTLE_SLOW 0.4x), retreat included.
- Verdict: KEEP (refactored twice: added supply economy + hunting enemy + merging stacks after "no reason to hold tiles"). Nolen: "this is fine for now". The code is still throwaway; M2 is a rewrite in sim/.

## Lessons for M2
- Instant capture alone gave no reason to hold a tile; value comes from income + a hunting enemy.
- Arrow + delay hop (3 s) and fight-on-order-start with slowed retreat are accepted mechanics.
- Shift-queued routes and merging of idle friendly stacks are wanted.
- Deposits must be scarce (5 of 88).
- Open: tune income/cost/hop-delay numbers once real playtime exists.
