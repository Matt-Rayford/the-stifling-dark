# The Stifling Dark — Game Data

JSON game data for the digital version, following the same pattern as `lemonade-wars/game-data`.

## Files

| File | Contents | Status |
|---|---|---|
| `config.json` | Core numbers, turn/round structure, light levels, wounds, spirits, dice, general rules | Complete |
| `board-features.json` | Space types, doors, windows, obstacles, map tokens | Complete |
| `scenarios.json` | Sawmill + Amusement Park: zones, features, objectives, evidence rewards | Complete |
| `adversaries.json` | Horror, Butcher, Cult: setup, actions, banish objectives | Complete (rulebook level) |
| `adversary-boards.json` | Exact adversary board text, tracks (Stalk 0-8, Blood 0-5), card slots | Complete |
| `investigators.json` | All 12 Investigators: MP, abilities, tracks, tokens, bios | Complete |
| `player-aids.json` | All 12 player aids verbatim + sprint die + round tracker + flashlight notes | Complete |
| `flashlight.json` | Flashlight template outline polygon, sight lines, LOS rules | Complete (scale needs designer confirm) |
| `cards/wounds.json` | 26 Wound cards | Complete |
| `cards/conditions.json` | 29 Condition cards (9 unique) | Complete |
| `cards/general-items.json` | 34 base General Items + MI/NF set variants | Complete |
| `cards/medical-items.json` | Medkit x3 | Complete |
| `cards/cursed-items.json` | 13 Cursed Items | Complete |
| `cards/objective-items.json` | 15 Objective Items | Complete |
| `cards/escape-cards.json` | 11 Escape/Banish cards incl. exact setup space IDs | Complete |
| `cards/events.json` | 28 Event cards (14 per scenario, minor/moderate/major) | Complete |
| `cards/spirits.json` | 3 Spirits, 4 abilities each | Complete |
| `cards/adversary-cards.json` | All 35 adversary cards: 9 Attacks, 21 Abilities, Enraged, Hatchling, 3 Revealed | Complete |
| `maps/sawmill.json` | Sawmill space graph: 426 spaces, 893 edges, verified | Complete |
| `maps/amusement-park.json` | Park space graph: 461 spaces, 962 edges incl. mirror doors, rides, water loop, verified | Complete |

## v1 scope (designer-confirmed 2026-07-26)

Base game only: 10 base Investigators (Kya Prosser and Winston Pitts tagged `set: promo`, excluded), base + MI item cards (NF cards tagged and excluded). MI cards are replacement alternatives for their same-named base cards, behind a "Use Mini-Expansion cards" settings toggle (designer-confirmed).

## Known gaps

1. **Obstacle/wall geometry for line of sight** — space graphs are done; LOS-blocking geometry (walls, obstacles, curtains, mirror walls) still needs extraction into a raster mask or polygons for flashlight/Stalk precomputation. Flashlight physical scale is confirmed (see `flashlight.json`).
2. Mini-map = same space graph as the main board (no separate extraction needed).
3. Token art PNGs live in `game-assets/tokens/` (organized by category) — ready for the Unity client.
