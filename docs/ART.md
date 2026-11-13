# Planetfall art and audio brief

Direction from `PLAN.md`: a **holographic war table**. Flat vector polygons, glowing borders, NATO-style glyphs. Grim and functional, not decorative.

`data/presentation/manifest.json` lists every glyph, sound and colour the game uses. A test fails if the manifest and the code disagree. Nothing below ships as a binary file yet: it is all generated in `sim/Presentation`, so real assets can replace it one at a time.

## Colour
- Players use the Okabe-Ito colours: **you** blue (0,114,178), **rival** vermillion (213,94,0), **native minor powers** yellow (240,228,66). Neutral ground is a dark slate (40,43,50).
- Tiles are filled with a darker shade of the owner colour, so glyphs stand out.
- **Hard rule, tested:** every owner pair (as stack colour and as tile fill, including neutral ground) stays at least 18 CIE76 units apart under normal vision, protanopia, deuteranopia and tritanopia. Today's worst case is 23.5. Adding a nation means adding a colour that keeps the rule.
- Colour is never the only signal. Ownership also shows as a glowing border line on each owner's side of a frontier, and units are told apart by glyph shape.

## Glyphs
Line drawings in a -1..1 square, drawn white with a dark under-stroke, centred in the stack disc. A stack shows the glyph of its most numerous unit type.

| Role | Shape |
|---|---|
| infantry | X |
| armor | oval |
| artillery | dot |
| recon | triangle |
| engineer | bridge |

**Hard rule, tested:** any two glyphs differ in at least half of their inked pixels at 32x32. Today's closest pair (infantry and engineer) differs in 71%. `PLANETFALL_SHEET=1` opens the contact sheet: all roles, all owners, all four colour-vision types.

## Music
Three 16-second layers play together and loop without a seam: **calm** (a minor pad), **tense** (a low drone with a ticking pulse), **battle** (a kick and bass pulse at 120 bpm). The mix follows what the player can see: battle the moment a visible fight starts, tense for 45 s after, calm after that. The layers fade in under a second (tested, limit is 2 s). Replacement music must keep the same three-layer, same-length, same-tempo structure so the cross-fade still works.

## Effects
`click`, `recruit`, `capture`, `lost`, `alert`, `hit`, `gift`, `victory`, `defeat`. Each is under two seconds and peaks well below clipping. Replace by name.

## Interface feel
Buttons brighten and thicken their outline under the cursor. Captured tiles flash white for 0.8 s. Alerts slide in from the right and fade out. The leader portrait is a silhouette placeholder.

## Not yet done
No border shader (borders are drawn lines, pulsing slowly), no particle effects, no real art or recorded audio, no screen shake.
