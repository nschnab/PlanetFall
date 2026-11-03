# Implementation Plan — Futuristic Real-Time Grand Strategy (working title: *Planetfall*)

Status: **approved — M0 unblocked** · rev 2 · 2026-11-01 · No game code yet.

Decisions confirmed with Nolen:
- **Control model:** army stacks moving tile to tile, with battles resolving when stacks meet. No per-soldier micro.
- **Bots:** two tiers. **Minor powers** can be befriended, absorbed or attacked. **AI rivals** play by player rules and can win.
- **No factions.** Every player and rival uses the **same rules and roster**. Playstyle variety comes from **leaders**, who grant bonuses.
- **v1 ships one leader with no bonuses.** The leader *system* is built (data-driven modifiers), but the only content is a neutral leader.
- **All §1 recommendations approved:** Godot 4.7 C#; single-player first + authoritative online; 300/700/1,400-tile maps with zoom; holographic vector art; real-time with pause, 45–75 min matches.
- **Platforms: Windows + macOS** (no Linux in v1).
- **Victory is military only:** you win when **every other major nation has capitulated**. Every player (human or AI rival) is a major nation; minor powers don't count.
- **v1 is single-player.** Online multiplayer moves to v1.1; the architecture still keeps it a drop-in.

Terminology: a **player** is any major participant, human or AI rival. A **minor power** is a native bot power. A **leader** is the character a player picks, which applies a set of modifiers.

---

## 0. Skill-pack grounding

The router found no engine fingerprint (empty folder), so this is a new project. Per router §6 it defaults to **Godot**, the most fully covered engine (21 Godot skills, baseline **Godot 4.7**, C# targets **.NET 8**). The skills read for this plan:

| Skill | Used for | Key rule taken from it |
|---|---|---|
| `procedural-gen` (+ `references/noise.md`) | Map gen | One seeded RNG threaded everywhere; generate into plain data, then render; validate (flood fill) before play; cap retry loops; separate seeds for elevation and moisture; **ship our own PRNG** with a pinned-output test, because Godot calls its PCG32 an implementation detail; cylinder sampling for east-west wrap; Poisson disc for scatter |
| `game-ai` (+ `pathfinding.md`) | Army movement, AI layers | Decide / steer / path as separate layers; A* on a graph with an admissible heuristic; repath on change, not every frame; flow fields only when many agents share a goal |
| `ai-behavior-trees-utility-ai` | Rival + minor AI | Hybrid BT → Utility; normalized 0..1 considerations; hysteresis bonus; tick decisions slower than render; no allocation per tick |
| `performance-optimization` | Perf plan | Profile first; frame budget math; MultiMesh for identical instances; draw-call reduction; pooling; budgets enforced in CI |
| `prototype-fast` | M1 spike, M2 slice | One question per prototype; throwaway vs keep decided up front; written kill criteria; spike lives in `prototypes/`, and a "keep" means a rewrite |
| `godot-multiplayer` | Online (v1.1) | ENet; server-authoritative; clients send *intent*; `any_peer` + sender check; per-peer `MultiplayerSynchronizer` visibility (prevents fog-of-war cheating) |
| `godot-csharp` | Language | `partial` classes; .NET 8 SDK; **C# cannot export to web in Godot 4** |
| `godot-resources` | Possible data authoring | Resources are shared refs, so duplicate before mutating; `res://` is read-only in exports |
| `tower-defense` (+ `balancing.md`) | Economy, counters | Income-vs-strength curves ("almost afford it"); hard counters stop a single dominant build; telegraph threats |
| `roguelike` | Fog of war analogue | Visible vs explored memory; deterministic run RNG |
| `camera-systems`, `input-systems`, `game-ui-ux`, `save-systems`, `audio-design`, `game-feel`, `level-design`, `create-game-assets` | Later milestones | See §9 per milestone |
| `godot-gdscript-headless-testing` | CI | Headless runs with a real exit code and a CI timeout (applied to the Godot side; the C# sim uses `dotnet test`) |

### Where the skills stop helping (designed from first principles)

| Gap | Nearest skill | How this plan covers it |
|---|---|---|
| **No RTS / grand-strategy genre skill** | `tower-defense`, `roguelike` | Territory, fronts, supply, fog, command and leaders are original design (§3–§6) |
| **No Voronoi / irregular-tile generation** | `procedural-gen` (Poisson + noise only) | Poisson → Lloyd relaxation → Delaunay/Voronoi (§4). Verify Godot 4.7's `Geometry2D.triangulate_delaunay` against the docs, or vendor a small tested C# Delaunator port |
| **`godot-tilemap` does not apply** | — | Tiles are polygons rendered by a custom mesh and shader |
| **No Godot navigation skill** | `game-ai` | Not needed: A* on the tile adjacency graph (~1–2k nodes) |
| **No lockstep netcode skill** | `godot-multiplayer` (authoritative only) | Use authoritative host; keep the sim deterministic for replays and tests anyway |
| **No diplomacy / relationship skill** | `ai-behavior-trees-utility-ai` | Opinion + treaty state machine, original (§7) |
| **Headless testing skill is GDScript-only** | `godot-gdscript-headless-testing` | C# sim tested with `dotnet test`; the GDScript runner is a Godot smoke test only |

---

## 1. Approved decisions (reference)

| Decision | Choice | Accepted trade-off |
|---|---|---|
| Engine | **Godot 4.7 .NET (C#)**, 2D top-down cylindrical map (wraps east-west, ice caps at the poles), desktop | No web export (C# limitation) |
| Platforms | **Windows (x86_64) + macOS (universal: Apple Silicon + Intel)** | macOS public builds need an Apple Developer ID (paid) for codesign + notarization, or Gatekeeper blocks them (`godot-export`) |
| Multiplayer | **v1: single-player only:** 1 human vs up to 7 AI nations + 6–12 minor powers. **v1.1:** online authoritative host, 1–6 humans | No MP at launch; Commands + deterministic sim keep it a drop-in (§8) |
| Scale | Fixed-size with continuous zoom: **Small 300 · Standard 700 · Large 1,400 tiles** | — |
| Art | **Holographic war-table:** flat vector polygons, glow borders, NATO-style glyphs; mostly code-native + SVG | Less spectacle |
| Pace | **Real-time with pause + 1×/2×/4×** ; v1.1 MP adds host-vote speed + limited pauses. Standard match 45–75 min | — |

Timing anchors (all data-driven):

| Event | Target at 1× |
|---|---|
| Army crosses one plains tile | 6–10 s |
| Capture undefended neutral tile | 15–25 s |
| Capture defended, fortified enemy tile | 60–120 s + battle |
| Build basic stack | 30 s |
| First contact with a rival | 6–10 min |
| Enemy holds a capital → that nation capitulates | 60 s |
| Surrender offer persistence (AI) | 60 s above threshold |

The sim only knows a **tile graph**, never geometry, so a **3D globe view** later is a generator + renderer swap, not a rewrite.

---

## 2. Concept summary and design pillars

**Pitch:** You land on a newly generated alien planet, crowded with rival invaders and native minor powers. You expand tile by tile, hold supply lines, court or crush locals, and turn or break alliances until you control the planet.

| # | Pillar | What it buys | Trade-off it imposes |
|---|---|---|---|
| 1 | **Every tile is a decision.** Territory is the economy, the vision, and the road to enemy capitals. | Clear goal; constant small choices | Tiles must differ meaningfully (terrain, deposit, chokepoint), which costs generator and balance effort |
| 2 | **Same rules, different minds.** Everyone plays one ruleset; variety comes from the map, diplomacy and (later) leaders. | One balance target; easy to learn; leader bonuses layer on cleanly | Less day-one identity; the map and AI personalities must carry variety until leaders ship |
| 3 | **Diplomacy is a weapon.** Minor powers and rivals are levers, not scenery. | Emergent stories; non-combat paths | AI must be legible (why it likes or hates you), so UI work goes into opinion breakdowns |
| 4 | **Readable at a glance.** The war table, not a battle movie. | Scales to 1,400 tiles; cheap art | Game-feel must come from crisp UI motion and audio |
| 5 | **A new planet every match, always fair.** | Infinite maps, competitive integrity | Needs a fairness validator and seed sweeps; some "cool" maps get rejected |

---

## 3. Leader system (replaces factions)

### What a leader is
A **leader** is pure data: an id, a name, a portrait reference and a list of **modifiers**. Every player picks one at match setup; AI rivals get one assigned. The leader changes numbers and unlocks, never the core rules.

```json
// data/leaders/commander.json — the ONLY leader in v1
{ "id": "commander", "name": "Field Commander", "portrait": "placeholder", "modifiers": [] }
```

### Modifier pipeline (built in v1, exercised by tests)
All tunable sim values are read through one resolver, `Stat(player, key) = base × Π(1 + pct) + Σ flat`. Leaders are just one **modifier source**. The same pipeline also serves terrain, fortification, treaties, and later tech or events.

| Modifier target examples | Key |
|---|---|
| Capture rate | `capture.rate` |
| Tile yield by resource | `yield.alloy`, `yield.energy`, `yield.influence` |
| Unit stats by role/tag | `unit.attack[armor]`, `unit.hp[infantry]`, `unit.speed` |
| Build cost / time | `build.cost[stack]`, `build.time[structure]` |
| Upkeep | `upkeep.energy` |
| Diplomacy | `opinion.minor`, `treaty.acceptance` |
| Vision / fortification | `vision.range`, `fort.max` |
| Unlocks | `unlock.unit[...]`, `unlock.structure[...]` |

**v1 content rule:** exactly one leader with an empty modifier list. The system is proven by **unit tests with test-only leaders** (e.g. a +50% capture leader really captures 1.5× faster), so adding real leaders later is data only.

### Why this is good for balance
Identical rules mean **win rate should depend only on spawn position and skill**. Bot-vs-bot win rate by spawn slot then becomes a direct **map-fairness test** (§4, §9).

### Future leader backlog (not in v1, for reference)
These came from the dropped faction ideas and could return as leader perks:

| Leader idea | Flavour | Possible modifiers |
|---|---|---|
| Fortifier | Methodical fronts | +fort max, +def in supply, −speed |
| Expansionist | Land grab | +capture rate, −unit HP |
| Diplomat | Wins friends | +opinion with minors, cheaper integration |
| Raider | Hit and run | +speed, loot yield on enemy tiles |

Leader balance rules for later: each leader has an equal **point budget** of upsides minus downsides, every bonus has a map-readable counter, and leaders face the same 45–55% win-rate gate.

---

## 4. Map generation design

Pipeline (all in `sim/MapGen`, pure C#, one `Pcg32` seeded from the match seed, sub-seeds per stage via SplitMix64):

```
seed ─► 1 Points ─► 2 Voronoi tiles ─► 3 Terrain ─► 4 Adjacency graph ─► 5 Resources
     ─► 6 Spawns ─► 7 Minor powers ─► 8 Validate ─(fail)─► next sub-seed (max 50 tries)
                                                └(pass)─► MapData (plain data) ─► renderer
```

1. **Points:** Poisson-disc sampling on a cylinder-width rectangle (radius from target tile count), then 2 iterations of **Lloyd relaxation**. Ghost-copy points across the east-west seam for wrap.
2. **Voronoi:** Delaunay → dual Voronoi cells = tiles. Store polygon (render only), centroid, area.
3. **Terrain:** elevation = fBm (5 octaves, `pow(e, 1.8)` redistribution), moisture = fBm with a **separate seed**, both sampled cylindrically (`noise.md`); latitude adds cold. Biomes → **Plains, Forest/Jungle, Highlands, Mountains (ground-impassable), Wasteland, Ice, Water (ground-impassable; flagged `NavalPassable`)**. Rivers by downhill walk on Voronoi edges → river-crossing edge flag.
4. **Adjacency graph:** shared Voronoi edges. Edge data: border length, move cost, **per-domain passability bitmask** (`Ground | Air | Naval`). Mark **chokepoints** (articulation points / low-degree land tiles).
5. **Resources:** base yields by terrain; **deposits** (Alloy veins, Energy wells, Relic sites → Influence) from weighted tables with **per-region quotas** and blue-noise spacing.
6. **Spawns:** farthest-point sampling on the graph over valid land tiles.
7. **Minor powers:** 6–12 clusters of 3–8 tiles in the band between spawns, each with a culture type.
8. **Validation and fairness:**
   - All land reachable from every spawn (flood fill); no spawn enclosed.
   - Per-spawn economic value within 4 hops: max/min ≤ **1.15**.
   - Distance to nearest rival: spread ≤ 20%.
   - Chokepoints within 3 hops: difference ≤ 1.
   - ≥ 1 minor power within 6 hops of each spawn.
   - Land fraction 55–75%.
   - Max 50 attempts; on exhaustion, fall back to mirrored generation and log it.

**Seeding:** shareable `MatchSeed` (u64) + `GeneratorVersion`. Saves store the **generated MapData**, not just the seed. A `Pcg32` golden-output test pins the first 16 outputs.

**Tooling:** `mapgen-sweep` CLI generates 1,000 seeds headless → fairness CSV, histograms, sample PNGs.

---

## 5. Territory and economy design

### Tile state
`Owner`, `Control[player] 0..1000`, `Fortification 0..3`, `Development 0..3`, `Structure?` (1 slot).

### Rules revision from the M1 spike (supersedes the control meter below for M2)
The M1 capture spike was judged **keep** with these rules, which M2 implements:
- **Hop orders:** a Move shows an arrow toward the next tile; after `Stat(move.delay)` (base 3 s) the stack enters it. Routes are paths of single hops; **Shift queues** more hops.
- **Instant capture:** a stack alone on a tile it does not own captures it on arrival. No capture timer. (Adjacency is implied: you only ever hop to neighbours.)
- **Fights start with the order:** a hop into an enemy-held tile starts the battle when the order begins, not on arrival. Any stack in a fight, whether entering, leaving or holding, hops at **0.4x** speed (so retreating is slow too).
- **A reason to hold tiles:** every owned tile makes supply; **deposits are scarce** (about 5 per 88 tiles, 5x yield); supply buys extra stacks at the capital; the AI hunts the nearest tile it does not own, so unguarded tiles are retaken.
- **Merging:** idle friendly stacks on one tile merge (strength and health add).
- Open for tuning: income, stack cost, hop delay, `BATTLE_SLOW`. The control meter and the fortification discount below are **deferred**; revisit only if these rules prove too thin.

### Capture
- A stack on a tile with **no hostile stacks** ticks control toward its owner: `rate = Stat(capture.rate) × stackCapturePower × terrainMod × (1 − fortification × 0.25)`. Unoccupied tiles slowly bleed control back to their owner.
- **Adjacency rule:** you may only capture tiles adjacent to your territory. This prevents deep captures; air drops later get their own rule.
- **Contested** (hostile stacks present) → capture freezes and a battle resolves.
- Ownership flips at 1000. Structures are captured or destroyed by type.

### Resources and yields (1 s economy tick)
- **Alloy:** stacks and structures.
- **Energy:** upkeep and development.
- **Influence:** diplomacy and minor integration.

`yield = Stat(yield.X) over (terrainBase + deposit + development)`. Tiles **disconnected** from your capital yield 50% (a shared supply rule). Capital gives a fixed bonus. **Upkeep:** each stack costs Energy, so over-building armies starves development, which is the core tension. Tune so the next army is *almost* affordable (`tower-defense`).

### Structures (1 per tile)
Extractor (+yield on a deposit) · Depot (reinforcement point, extends supply) · Fortress (+fortification cap).

### Victory: military capitulation only
**You win when every other major nation has capitulated.** There are no territory-percentage, score or diplomatic victories in v1.

A major nation **capitulates** in any of three ways:
- **Forced:** an enemy holds its **capital tile for 60 s continuously** (a visible countdown for everyone; retaking the capital cancels it).
- **Collapse:** it owns **zero tiles**.
- **Surrender (voluntary):** it surrenders to a chosen nation (see below).

On capitulation:
- **Its remaining tiles transfer to the receiving nation** (the capital-holder for a forced capitulation, the named nation for a surrender). If it fell by losing all tiles, there is nothing to transfer. This makes capital strikes decisive and speeds up the endgame.
- Its stacks disband and all of its treaties dissolve.
- A human who capitulates loses the match (with the option to keep watching).

### Surrender
Any major nation can end its own war early.

| Rule | Detail |
|---|---|
| **Who can surrender** | Any nation, human or AI, at any time. The human UI is a Surrender button in the diplomacy panel, behind a confirmation. |
| **To whom** | A nation it is **at war with**. It cannot surrender to an ally or to a minor power. |
| **Acceptance** | The receiver always gets the surrender if it is an AI at war with the surrenderer. A human receiver sees a prompt (accept, or decline and keep fighting). Declining blocks a repeat offer from that nation for 2 minutes. |
| **Terms** | v1 has one term: **unconditional**. The surrenderer is eliminated and all its tiles, structures and treasury go to the receiver. Negotiated terms (vassal, partial cession) are out of v1. |
| **AI offers surrender** | A utility consideration, `Hopelessness`, scored 0..1 from: share of tiles and income versus the strongest neighbour, loss of its capital's defenders, stacks remaining, and whether its capital has an enemy stack adjacent. It offers only above a threshold that **persists for 60 s**, and each personality has a different pain tolerance: Zealot very high, Builder low. |
| **Demand surrender** | A new diplomacy action. The AI answers with the same `Hopelessness` score plus personality, and shows its reason ("They still hold a fortified capital", "They have no way to win"). This is the player's tool for shortening the endgame. |
| **Why an AI will not just surrender at once** | The persistence window and personality tolerance stop early-game surrenders; an AI with a live capital and an army never reaches the threshold. |
| **Effect on victory** | A surrender counts as a capitulation for the win condition, so the endgame can end without besieging every last tile. |

Alliances help you survive and win, but only one nation can win, so every alliance eventually ends (pillar 3: betrayal is part of the design). Capitals cannot be relocated in v1.

---

## 6. Unit, combat, and command design

### Data model (domain-ready)
```
UnitDef      { id, domain: Ground|Air|Naval|Orbital, roles[], hp, attack{type,value},
               armor{type}, speed, vision, upkeep, cost, captureMultiplier, tags[] }
Stack        { id, owner, domain, units[], tile, orders[], org (0..100), supplied }
DomainRules  { movementGraph(domain), canEngage[domain][domain], capturePossible(domain) }
```
v1 ships `Ground` only. Air or naval later means adding a domain: a passability mask on the existing graph plus engagement-matrix rows. Stacks are single-domain, so movement, combat and capture code stays untouched.

### Shared roster (everyone, v1)
| Role | Job | Damage → Armor |
|---|---|---|
| Infantry | Cheap, best capture rate, holds ground | Kinetic → Light |
| Armor | Breakthrough, poor in forest/highlands | Explosive → Armored |
| Artillery | **Supports battles on adjacent tiles**; weak if engaged | Explosive → Fortified |
| Recon | Fast, +vision, weak | Kinetic → Light |
| Engineer | Builds structures, fortifies tiles | — |

A damage/armor type matrix provides the hard counters (`tower-defense`).

### Combat (deterministic, 1 round/s)
- A battle opens when hostile stacks share a tile. It is an entity: stacks can join and adjacent artillery contributes.
- Per round: `damage = Σ attack × typeMatrix × terrainMod × fortMod × orgMod × (1 ± 0.1·rng)`, distributed by role targeting priority.
- **Organization** drains with losses. At < 25% the stack **auto-retreats** to a friendly adjacent tile, or is destroyed if it has none.
- The battle UI shows a strength bar, predicted outcome range and modifiers.

### Orders (all `Command` objects)
Move · Attack-move · Hold/Fortify · Capture-area · Merge · Split · Retreat · Build · Shift-queue · Surrender · AcceptSurrender · Propose/Respond treaty. One command stream drives SP, network, replays and AI.

### Selection and controls (`input-systems`, `camera-systems`, `game-ui-ux`)
Click / box select / double-click all-in-tile; right-click tile = move/attack; Shift queue; Ctrl+1–9 groups; Space pause; Tab cycle idle stacks. Action-mapped and rebindable with conflict detection. Camera: WASD / edge / middle-drag pan, zoom to cursor, pole clamp, seamless wrap, double-tap group to center.

### Fog of war
Per player on the tile graph: **Visible** (within vision hops of stacks/tiles; highlands +1), **Explored** (last-known owner, no stacks), **Unknown**. Updated incrementally on move/flip. In MP the host sends each peer only its visible state.

### Pathfinding
A* per domain on the adjacency graph with an admissible centroid-distance heuristic. Paths are cached and recomputed only on graph change or target move.

---

## 7. Bot and diplomacy design

### Rival AI: hybrid BT ↔ Utility
| Layer | Tick | Model | Output |
|---|---|---|---|
| **Strategic** | 5 s | **Utility**: *Expand, Develop, Defend Front, Attack Target, Court Minor, Seek Alliance, Betray* | Goal weights + budget split |
| **Operational** | 2 s | Allocator over the graph: finds **fronts**, assigns stacks to goals | Assignments, build queue |
| **Tactical** | 1 s/stack | **BT**: `Selector[ Retreat-if-losing, Join-nearby-battle, Execute-goal, Idle-fortify ]` with ReactiveSequence | Commands |

The AI is time-sliced (one rival's strategic layer per tick), with +0.1 hysteresis on the current goal and a debug overlay of scores and the active BT path. It issues the same Commands as humans with no hidden cheats at Normal; Hard adds only an openly stated economy bonus.

### AI personalities (behaviour, not buffs)
Personalities change *how the AI decides*, never its stats, so they stay compatible with the "one leader, no bonuses" rule.

| Personality | Leans toward |
|---|---|
| Conqueror | Attack, Betray; grudges decay slowly |
| Builder | Develop, Defend; values non-aggression pacts |
| Opportunist | Attacks the weakest / whoever is already at war |
| Diplomat | Courts minors, seeks alliances, punishes betrayal |
| Zealot | Picks one rival as a permanent enemy |

Each personality is assigned per match from the seed. When leaders gain bonuses later, each leader can carry a preferred personality.

### Minor powers (FSM)
`Peaceful ↔ Wary ↔ Hostile`, `Friendly → Allied → Integrated`, plus `Raiding`.
- They don't expand beyond their home region; garrisons grow over time.
- **Tasks:** clear raiders on tile X, gift Alloy, keep Depots off their border; completing one raises opinion.
- **Integration:** opinion ≥ 75 + Influence + 60 s → tiles join peacefully. Conquest is faster but angers other minors of the same culture.

### Relationship model (all pairs)
- **Opinion −100..+100** = Σ decaying modifiers (shared border, gifts, attacks on allies, common enemy, *Treacherous*). The UI always shows the breakdown.
- **Treaty FSM:** `War ↔ Neutral ↔ Non-Aggression ↔ Trade Pact ↔ Alliance` (+ `Integrated` for minors). Thresholds + acceptance. Breaking a treaty is allowed but applies a global *Treacherous* modifier and a 5-minute treaty cooldown.
- **Actions:** Gift · Propose treaty · Demand tribute · Declare war · Military access · Integrate · **Surrender** · **Demand surrender**.
- **AI acceptance** is a utility score with a shown reason string.

### Unengaged bots
Rivals pursue their plans and fight each other through the same sim; minors develop, raid if Hostile, and post tasks.

---

## 8. Technical architecture

### Solution layout
```
game/
  sim/                      # pure C#, NO Godot references
    Core/    (Pcg32, FixedPoint, Ids, Command, EventBus, Modifiers/StatResolver)
    MapGen/  (Poisson, Voronoi, Terrain, Fairness)
    World/   (TileGraph, Tiles SoA, Stacks, Players, Leaders, Structures)
    Systems/ (Movement, Capture, Combat, Economy, Supply, Visibility, Diplomacy, Victory)
    AI/      (Utility, BT runtime, Operational planner, MinorFsm)
    Data/    (DefLoader, schema validation)
  sim.tests/                # unit, determinism, golden PRNG, modifier tests, balance smoke
  tools/mapgen-sweep/
  tools/balance-runner/     # N headless AI-vs-AI matches → win rate by spawn slot
  data/                     # leaders/*.json, units/*.json, terrain.json, structures.json, ai/personalities.json
  godot/                    # Godot 4.7 .NET project: presentation only
  prototypes/               # throwaway spikes
  docs/
```

### Sim vs rendering
Fixed **10 Hz** tick: `(State, Commands[]) → (State', Events[])`. Godot reads immutable snapshots and interpolates. Events drive UI, audio and game-feel; there is no polling.

### Data-driven design
Units, leaders, terrain, structures and AI personalities live in **JSON**, schema-validated in tests and hot-reloaded in debug builds. JSON is chosen over `.tres` so the sim stays engine-free. **Export gotcha (`godot-export`):** non-resource files are not auto-included, so both export presets need `*.json` in *Filters to export non-resource files*. The M0 CI export check catches this.

### Cross-platform rules (Windows + macOS)
- File paths only via `user://` / `Path.Combine`; no hardcoded separators; treat file names as case-sensitive (APFS can be configured case-sensitive, and CI runs on Linux).
- Saves and settings go to `user://` (resolves to `%APPDATA%` on Windows and `~/Library/Application Support` on macOS).
- Input: map **Ctrl ↔ Cmd** for shortcuts on macOS (control groups, copy), and support trackpad pinch-zoom and two-finger pan in `camera-systems`.
- Determinism: integer/fixed-point sim means Windows ↔ macOS cross-play needs no float-determinism work (and the host is authoritative anyway). Every tunable is read through the **StatResolver** (§3), so leaders, terrain and future tech all plug in identically.

### Determinism
Own `Pcg32`; sub-seeds per system; integer/fixed-point sim values; iterate by sorted IDs only. CI test: same seed + command log → identical state hash.

### Networking (v1.1, not in v1)
Clients send `Command` via `@rpc("any_peer")` to peer 1; the host validates and broadcasts per-peer fog-filtered deltas at 10 Hz; reconnect = full snapshot.

### Save
Versioned JSON `{version, generatorVersion, mapData, simState, rngStates, commandLogTail}`; atomic write; migrations; 5-minute rotating autosave (SP).

### Performance (Large: 1,400 tiles, 400 stacks, 8 players, 12 minors; 60 FPS)
| Subsystem | Budget | Approach |
|---|---|---|
| Sim tick | ≤ 4 ms/tick | SoA tiles, incremental visibility, cached paths, AI time-slicing |
| Territory | 1–3 draw calls | One mesh; per-tile data texture; borders in shader |
| Stack glyphs | 1 call per atlas | `MultiMeshInstance2D`, pooled |
| UI | ≤ 2 ms | Event-driven |

Profile release builds; CI fails if the mean sim tick exceeds budget.

---

## 9. Milestone roadmap

| M | Name | Deliverables | Skills | Done test |
|---|---|---|---|---|
| **M0** | Foundations (≤ 2 days) | Own git repo; Godot 4.7 .NET project; `sim` + `sim.tests`; CI (`dotnet test` + Godot headless smoke); **Windows Desktop export (macOS preset exists, deferred)** (`*.json` filter, macOS universal, codesign off for dev); CI builds both exports; `Pcg32` golden test | `godot-csharp`, `godot-gdscript-headless-testing`, `godot-export` | CI green on a clean clone; both exports build in CI; the Windows build runs locally; the macOS build launches on a Mac (right-click → Open while unsigned) and shows the sim version |
| **M1** | **Spike, throwaway** (1 sitting) | `prototypes/capture-spike/`: ~80 hardcoded Voronoi tiles, 1 player stack, 1 random-walking enemy stack, capture-by-occupation | `prototype-fast`, `procedural-gen` | **Question:** *Is real-time stack capture on irregular tiles readable and tense?* **Keep if** a fresh player captures on purpose within 30 s and wants "one more tile". **Kill/refactor if** it feels like busywork |
| **M2** | **Vertical slice** | Seeded map gen (200 tiles); shared rules; **Field Commander** leader via StatResolver; 1 AI rival (3-goal utility + tactical BT); 1 static hostile minor; Alloy only; Infantry; combat + retreat; capital-capture capitulation victory; **human Surrender button**; pause/speed | `procedural-gen`, `game-ai`, `ai-behavior-trees-utility-ai`, `camera-systems`, `input-systems` | Seed X twice → identical map; full match in ≤ 30 min; AI captures and attacks without sticking; determinism test passes; test-only "+50% capture" leader captures 1.5× faster |
| **M3** | Planet generator | Full §4 pipeline; `mapgen-sweep` | `procedural-gen` (+ refs), `level-design` | 1,000 seeds: 100% validate within 50 tries; value spread ≤ 1.15 on ≥ 95%; Large gen < 1 s |
| **M4** | Full economy + roster | 3 resources, upkeep, supply, structures, all 5 roles, damage/armor matrix, full modifier key set | `tower-defense` (`balancing.md`), `godot-resources` (only if a `.tres` editor is wanted) | A new unit or a new test leader is added via JSON only; schema tests pass |
| **M5** | Rival AI | Full 3-layer AI, 5 personalities, fronts, time-slicing, debug overlay; **`Hopelessness` surrender offers**; `balance-runner` | `ai-behavior-trees-utility-ai` (refs), `game-ai`, `performance-optimization` | 500 AI-vs-AI matches: no stuck stacks; ≥ 90% end by a win condition; **surrenders occur in ≥ 30% of matches and never before minute 10 (test)**; **win rate per spawn slot 40–60%** (tighten to 45–55% in M9) |
| **M6** | Minors + diplomacy | Minor FSM, tasks, integration; opinion; treaties; diplomacy panel | `game-ai`, `ai-behavior-trees-utility-ai`, `game-ui-ux` | Scripted: befriend → integrate; ally → betray → *Treacherous* everywhere; AI accept/reject shows a reason; **demand surrender** accepted by a hopeless AI and refused by a healthy one, each with a reason |
| **M7** | Fog, UX, save | Visibility; HUD, minimap, alerts; rebinding; leader-select screen (1 entry); save/load | `game-ui-ux`, `input-systems`, `camera-systems`, `save-systems`, `godot-ui-control` | Save mid-battle → reload → same state hash; no stacks visible outside vision; UI works from 720p to 4K |
| **M8** | Presentation | Art brief + manifest; player colour palettes (colour-blind safe); glyphs; border shader; leader portrait placeholder; adaptive music; SFX; UI juice | `create-game-assets`, `shader-programming`, `godot-shaders`, `audio-design`, `game-feel`, `godot-audio` | Glyph contact sheet passes the colour-blind check; music shifts within 2 s of battle |
| **M9** | Balance, perf, ship prep | Balance passes; perf gates; settings; tutorial; **macOS codesign + notarization** (Developer ID); store builds for both platforms | `performance-optimization`, `godot-export`, `steam-publish` / `itch-publish` | Large map holds 60 FPS on min-spec for **both** platforms (measured release builds, e.g. a low-end Windows laptop + a base M1 Mac); the notarized macOS build opens without a Gatekeeper warning; spawn-slot win rates 45–55% |
| *v1.1* | Online multiplayer (post-v1) | Lobby, command RPCs, fog-filtered sync, reconnect, speed vote | `godot-multiplayer` (refs), `godot-export` | 4 humans + 4 AI, 60 min, no crash/desync; fogged stacks are absent from packets |

**M2 sub-steps** (each ends with green CI before the next starts):
- **M2a Map + modifiers:** flat 200-tile map from a seed (jittered points, Lloyd, Voronoi, adjacency, deposits, two spawns, fairness check with bounded retries); `StatResolver`; leader JSON with the no-bonus Field Commander. Tests: same seed gives identical map, fairness holds over 1,000 seeds, a test-only +50% leader changes a stat by exactly 1.5x.
- **M2b Sim core:** state, `Command`s, 10 Hz tick, hop/fight/capture/economy/merge systems, state hash. Tests: determinism (seed + command log gives the same hash), fight-on-order-start, slowed retreat, merge, supply-buys-stack.
- **M2c Opponents + victory:** utility AI rival with hunting behaviour, static hostile minor, capital-hold capitulation, Surrender command. Tests: AI-vs-AI matches terminate with no stuck stacks.
- **M2d Godot client:** renderer from snapshots, click/Shift-queue/selection, HUD, pause and speed, Surrender button, `Main` scene replaced by the match.

**M2 rules added after the first playtest requests** (all in `sim/`, covered by tests):
- **Encirclement:** a pocket of non-owned tiles whose whole border is yours and linked to your capital is captured automatically (not if an enemy stack is inside or it holds another major's capital; map edge counts as wall).
- **Combat modifiers:** attackers take +15% damage; +25% damage per extra distinct attacking tile (max 4 fronts); entrenchment gives defenders +10% per 10 s stood still after the first 10 s, up to +50%, lost on moving, frozen while attacked.
- **Spawning:** new stacks appear only on your capital or an owned deposit ("mine"), never on a tile with an enemy stack; the site with the fewest friendly stacks is chosen. No open site means no purchase and supply keeps accumulating.
- **Rival cap:** `Player.MaxStacks` limits the AI (client sets 4) as a difficulty knob.
- **M3a terrain (started early at the user's request, before the slice was playtested):** rank-based fBm elevation and moisture give Plains, Forest, Highlands, Mountains and Water. Mountains and Water are ground-impassable and have no adjacency, so pathing, AI and encirclement need no special cases. Spawns are forced open, cut-off land becomes lake, and validation adds a 72-92% passable band. Biomes are cosmetic apart from passability until M4. `SplitMix64.Derive` had a collision bug (nearby seeds shared sub-seeds), now fixed and tested.
- **M3b chokepoints:** articulation points of the passable graph are stored in `MapData.Chokepoint` (drawn as faint rings) and validated: chokepoints within 3 hops of each spawn differ by at most 1, and the minor power is within 10 hops of both spawns (plan target is 6 once multiple minors exist). A brute-force test confirms the flags.
- **M3c rivers:** up to 4 rivers walk downhill from highlands and run along the boundary between each path tile and its left-bank neighbours (`MapData.Rivers`, edge keys). A hop across a river edge takes 50% longer (`Rules.RiverCrossPermille`, `Simulation.HopNeeded`); rivers never block, and never touch spawn tiles. Drawn as blue lines on the shared tile edge.
- Note: the M2 done-test "+50% capture" means "+50% move speed" (capture is instant).

Critical path: M0 → M1 → M2. Nothing after M2 starts until the slice is judged fun.

---

## 10. Scope

**In v1:** 2D cylindrical planet · one shared ruleset and roster · **leader system with one no-bonus leader** · ground domain · stacks on Voronoi tiles · 3 resources · rival + minor AI · diplomacy · fog · military victory by capitulation · single-player · save/load.

| Out of v1 | Room left |
|---|---|
| **Online multiplayer** (v1.1) | All input is already `Command`s; host-authoritative design in §8; per-peer fog filtering is planned |
| **Non-military victories** (territory %, score, diplomatic/integration) | `Victory` is its own system; add conditions as data + rule classes |
| **Leaders with bonuses** | Modifier pipeline + JSON leaders; add data only (§3 backlog) |
| **Asymmetric factions** | Could return later as leader unlock sets or a `ruleset` layer; the StatResolver + unlocks cover most of it |
| Air, naval, space | `Domain` + passability mask + engagement matrix; water already `NavalPassable` |
| 3D globe | Graph-only sim; swap generator sampler + renderer |
| Deep diplomacy, shared victory | Treaty FSM extensible; modifiers are data |
| Tech tree | New modifier source for the StatResolver |
| Lockstep / replay UI | Deterministic sim + command log exist |
| Web build | Requires a port (C# can't export to web) |
| Modding | JSON is the mod surface |

---

## 11. Risk register

| # | Risk | L | I | Mitigation |
|---|---|---|---|---|
| 1 | Capture loop is busywork | M | H | M1 kill criteria; capture times/tile counts are data; Capture-area order |
| 2 | **Matches feel samey without factions** | M | M | Map variety, AI personalities and diplomacy carry variety; leaders with bonuses are the first post-v1 content drop; playtest at M2/M6 |
| 3 | AI dumb or stuck | H | H | Layered AI, debug overlay, stuck-stack watchdog, hysteresis, command-log repro |
| 4 | Diplomacy feels arbitrary | M | M | Opinion breakdown; reason strings |
| 5 | Map fairness fails on some seeds | M | H | Validator, bounded retries, mirrored fallback; spawn-slot win rates in `balance-runner` |
| 6 | Scope creep (factions, air, tech) | H | H | §10 is the contract; new ideas go to a v1.x backlog |
| 7 | Desync / cheating (v1.1) | L | M | Out of v1; authoritative host, fog-filtered sync and determinism tests already designed |
| 7b | **Long, dull endgame** (mopping up 7 nations after the outcome is clear) | H | M | Tiles transfer to the capital-holder on capitulation (snowball by design); AI nations that are hopelessly behind **offer surrender**, and the player can **demand** it (§5, M5); balance-runner tracks time-to-win, target 45–75 min on Standard |
| 8 | Large-map performance | L | M | Graph sim, one-mesh territory, MultiMesh, CI perf gate |
| 9 | Voronoi bugs (seam, degenerate cells) | M | M | Tested triangulator; property tests (≥ 3 neighbours, symmetric adjacency, area sum) |
| 10 | Readability at zoom extremes | M | M | Vector art; strategic-zoom simplification; outside playtests |
| 11 | Godot .NET toolchain friction | L | M | Pin .NET 8 + Godot 4.7; CI exports from M0 |
| 12 | **macOS untested day-to-day** (dev is on Windows) | M | M | CI builds macOS every push; test on a real Mac at the end of each milestone (or rent a cloud Mac); budget for the Apple Developer fee before M9 |

---

## 12. Open questions (ranked by how much each blocks)

Resolved: Mac access (yes, Apple Silicon, so every milestone's done test runs on it); dedicated repo created at `Documents/game` (branch `main`).

Also resolved: victory is military capitulation only; multiplayer is v1.1.

1. **Capitulation rule check:** a nation capitulates after an enemy holds its capital for 60 s (or it loses every tile), and its tiles go to the capital-holder. Is that what you mean by "complete capitulation"? *Affects M2, which is cheap to change.*
2. **Setting tone:** grim military, hopeful frontier, or stranger? Affects M8 only.
3. **Release target** (Steam, itch, personal)? Affects M9 only.
4. **First leaders after v1:** any concepts you already want? Not blocking.
- **M3d minors:** 6-12 clusters of 3-8 connected passable tiles (never within 3 hops of a spawn, never touching each other); each is one static minor power garrisoning its first tile. Every spawn has a cluster within 6 hops (`MaxMinorHops` restored to 6). Client uses `GameState.NewWithMinors`.
- **M3e deposit kinds:** Alloy / Energy / Relic. Each spawn gets an Energy deposit near it; ~1/6 of deposits are Relics, only >=5 hops from every spawn. Data only for now (all deposits still act as mines); economy use lands in M4. Drawn gold / cyan / violet.
- **M3f mapgen-sweep:** `dotnet run -c Release --project tools/mapgen-sweep -- [count] [firstSeed] [cols] [rows] [csv]` prints histograms and the done-test verdict; runs in CI. Result: 1000 seeds, 0 fallbacks, 100% spread <= 1.15, median 2.5 ms (200 tiles). A 800-tile grid (40x20) generates in ~84 ms median, <= 520 ms worst. Remaining in M3: cylindrical wrap seam.
- **M3g cylindrical wrap:** east and west edges join (periodic Voronoi via ghost copies, wrapping elevation/moisture noise, seam adjacency); top and bottom stay walls. Spawns moved to x=25%/75% so both ways round are equally long. Fewer maps have chokepoints (loops), ~40%. Routes crossing the seam draw as an exit and an entry arrow. Large (800 tiles) worst case 0.88 s, p95 0.52 s. **M3 complete.**
