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
