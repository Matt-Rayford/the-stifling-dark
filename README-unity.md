# The Stifling Dark — Unity client (v1)

The front end for the server in `src/StiflingDark.Server`. Built to be **playable and clear**,
not pretty: the point of v1 is to sit down with three bots and find rules bugs.

Sibling project by design — same Unity version, same folder layout, same "engine as a DLL +
game data in StreamingAssets" sync, same code-built uGUI, same near-empty boot scene as
`../lemonade-wars/unity`.

---

## 1. Install Unity

**Unity 6000.5.2f1** — the exact version in `unity/ProjectSettings/ProjectVersion.txt`, and the
same one Lemonade Wars uses.

Unity Hub → Installs → Install Editor → Archive → pick `6000.5.2f1`. For a Windows build later,
tick **Windows Build Support (Mono)**.

You also need **poppler** for the board renders (one-off):

```sh
brew install poppler        # provides pdftoppm
```

## 2. Sync the engine and the art in

```sh
cd /Users/matt/Documents/GitHub/the-stifling-dark
tools/sync_unity.sh
```

That script is the only build step. It:

1. builds `StiflingDark.Engine` + `StiflingDark.Protocol` (Release, netstandard2.1) into
   `unity/Assets/Plugins/`;
2. copies `game-data/` — maps, `*-los-mask.bin`, `flashlight.json`, config, all card decks —
   into `unity/Assets/StreamingAssets/game-data/`, laid out exactly as the repo has it, because
   the client loads it with the engine's own `GameDatabase.Load()`;
3. copies `game-assets/tokens/` into `unity/Assets/StreamingAssets/tokens/`;
4. renders `game-assets/maps/*.pdf` to **4096×4096** PNGs in `unity/Assets/Textures/`
   (skipped if they already exist — `FORCE_TEXTURES=1` to redo them) and clones them into
   StreamingAssets so built players find them;
5. writes `StreamingAssets/client-config.json` with the default server URL.

Everything it produces is gitignored, exactly as in Lemonade Wars: `game-assets/` is itself
gitignored here, so anything derived from it has to be regenerable rather than committed.

Re-run it after any engine change or `game-data/` edit. Unity picks the changes up on refocus.

## 3. Run the server

```sh
~/.dotnet/dotnet run --project src/StiflingDark.Server      # http://localhost:5226
```

or `docker compose up --build`. See `README-server.md`.

## 4. Open the project and play

1. Unity Hub → **Add** → **Add project from disk** → select the `unity/` folder.
2. Open it with 6000.5.2f1.
3. Open **`Assets/Scenes/Main.unity`** (it holds only a camera and a light — the app builds
   itself), or just press Play in whatever scene is open. `StiflingDarkApp` bootstraps via
   `[RuntimeInitializeOnLoadMethod]`.
4. Press **Play**.
5. The menu: your name, then the server. `ws://localhost:5226/ws` is the default; the
   **localhost** button restores it. A bare host or a URL without `/ws` is fixed up for you, so
   the Railway deployment can be pasted as `wss://your-app.up.railway.app`. The last URL you
   actually connected to is remembered.
6. **CONNECT**, then **Create a room — I play an Investigator**.
7. In the lobby: **+ Bot Investigator** twice, **+ Bot Adversary** once. Pick your Investigator
   from the base 10, pick the scenario and the Adversary, then **START GAME**.
8. Play. See the controls below.

Nothing else needs configuring. Start spaces and Medical Item spaces are left unset, which
makes the server fill them in from the map — set them only when reproducing a specific setup.

## 5. Controls

| Input | Does |
| --- | --- |
| Scroll wheel | Zoom toward the cursor |
| Right-drag / middle-drag | Pan |
| **Fit** (top right) | Frame the whole board |
| Hover a space | Tooltip: id, zone, light level, move cost, and everything standing there |
| Click a highlighted space | Move one step (highlights show the MP cost) |
| Bottom bar | Sprint · Rest · Charge · **Place Flashlight** · Involved · End Turn |
| Right panel, top | Interacts for your space and its neighbours, items, abilities, objective actions |
| Right panel, bottom | The event log, redacted for your role |
| Left panel | Every Investigator's sheet, the Adversary's board, the objective |
| **Resync** | Ask the server to re-send your whole view and log |
| Esc | Cancel a space-picking prompt or a flashlight aim |

### Placing the flashlight

Press **PLACE FLASHLIGHT** and then move the mouse around your figure. The lit spaces brighten
live as you sweep: the client runs the engine's own `FlashlightBeam` over the engine's own
`RasterLineOfSightBlocker` (both out of `Assets/Plugins`, both fed from the synced
`game-data/maps/*-los-mask.bin`), so the preview is the same Bright set the server will compute.
Click to commit; the client sends `PlaceFlashlightCommand` with the angle in radians and the
server recomputes it authoritatively.

### The light model

The whole map is dimmed. **Bright** spaces and zones punch through to full board art, **Dim**
sits between, **Dark** is heavy but still legible enough to plan a route. It is one darkness mask
stretched over the board, composited on the CPU from the view's light state — a mask rather than
a glow, which is what makes a lit space read as *lit* instead of *highlighted*. The beam preview
repaints only the spaces that entered or left the beam, so sweeping the mouse is cheap.

## 6. Playing the Adversary

Create the room with **I play the Adversary** and add bot Investigators. Your standee and your
Cultists are drawn on your own board (your view is unredacted about them), so moving is a space
click. The right panel carries the setup placements, the card plays with target pickers, and the
per-adversary specials — Stalk and the Grave, Ambush / Enraged Gather / Egg Sacs, the Cult's
per-Cultist moves, Bloodletting, Possessed, the Ritual tokens and the Final Sacrifice.

This side is deliberately minimal-but-functional: buttons and pickers, not a designed
experience.

## 7. Layout

```
unity/
  Assets/
    Editor/            UrpSetup.cs (repair URP), BuildTools.cs (mac/Windows players)
    Plugins/           StiflingDark.Engine.dll, StiflingDark.Protocol.dll  (synced, committed)
    Scenes/Main.unity  near-empty boot scene
    Scripts/
      StiflingDarkApp.cs   bootstrap, menu, connection lifecycle, stage switching
      Session.cs           the whole protocol conversation (no UnityEngine reference)
      BoardModel.cs        map graph + beam + LoS mask + coordinate scale (no UnityEngine)
      Describe.cs          ids -> names (no UnityEngine)
      BoardView.cs         board texture, figures, tokens, picking, pan/zoom, flashlight aim
      LightOverlay.cs      the darkness mask
      GameUi.cs            status bar, roster, log, action bar, interacts, modals
      AdversaryUi.cs       the Adversary seat's controls
      LobbyUi.cs           seats, bots, Investigator picker, scenario/adversary, start
      Prompt.cs            modal option lists and the argument prompt
      UiKit.cs             code-built uGUI helpers
      UiSprites.cs         procedural rounded rect / disc / ring
      Pointers.cs          pointer relay + the shared tooltip
      TokenArt.cs          lazy texture cache and the art path map
    Settings/          URP pipeline + 2D renderer (committed)
    TextMesh Pro/      TMP essential resources (committed — no editor here to import them)
    StreamingAssets/   synced game-data, tokens, board textures, client-config.json (gitignored)
    Textures/          board-sawmill.png, board-amusement-park.png (gitignored, regenerable)
tools/
  sync_unity.sh        the one build step
  ClientCheck/         compiles the UnityEngine-free client files against the real engine
  UiCheck/             compiles ALL client files against a stub UnityEngine surface
```

### Coordinates — the scale factor

Space `x`/`y` in `game-data/maps/*.json` are pixel centres **in the full-resolution render**,
recorded in that file as `source.imageSize` (7092×7092 for the Sawmill, 6621×6621 for the
Amusement Park, both at `renderDpi` 285). The board textures are 4096×4096, so:

```
texture px = map px × (textureWidth / source.imageSize.w)
           = map px × 0.577552   (Sawmill:  4096/7092)
           = map px × 0.618638   (Amusement Park: 4096/6621)
```

The client never hardcodes those numbers. One world unit **is** one full-res map pixel, and the
board sprite is scaled by `source.imageSize.w / texture.width` to match — so dropping in an
8192px render changes nothing but the sharpness. `spacePitch` 304 and `spaceRadius` 81 are in
the same full-res pixel space.

## 8. Verifying changes without opening Unity

```sh
~/.dotnet/dotnet build tools/ClientCheck    # protocol client, board model, describers
~/.dotnet/dotnet build tools/UiCheck        # every client file, against UnityEngine stubs
~/.dotnet/dotnet test                       # the real suite: engine + server
```

`tools/UiCheck/UnityStubs.cs` is a compile-only shape of the Unity API the client touches. Green
means *well-formed* — correct member names, argument counts, usings — not *works*. Neither check
project is in `TheStiflingDark.sln`.

## 9. Known v1 limitations — honestly

**Not verified in a Unity editor at all.** This client was authored as files; there was no Unity
on the build machine. It compiles against a stub UnityEngine and the real engine/protocol
assemblies, and every asset it needs is either committed or generated by the sync script, but
nobody has yet seen it draw a frame. Expect to fix layout numbers, and expect one or two Unity
API mismatches the stubs could not catch. Nothing has been laid out by eye.

**Button enabling is a best guess.** The protocol's `update` carries view + `actingSeats` +
`yourTurn` — there is no legal-move list (unlike the Lemonade Wars protocol) and the engine's
`Game.ActionBlockers` is server-side only. So buttons are enabled from what the view does say
(phase, whose turn, MP, Charge, whether the Final Action is spent) and everything else is
offered anyway: press it, and the engine's own refusal appears in the log in red. An over-eager
button that explains itself is more useful for bug-hunting than a hidden one — but it does mean
you will see refusals that are working as intended.

**Item, ability, event and Spirit arguments are typed by hand.** Those commands take a
`List<string>` whose meaning is per-card, and nothing machine-readable describes it. The prompt
gives you a text field plus quick-fill chips for the Investigator ids, spaces in range, your
item ids and the zone letters; the engine's error message is the documentation.

**Painkillers has no pending flag in the view**, so its resolution is a manual button in the
items list rather than an automatic modal.

**The beam preview can be generous.** Cards that shrink the beam — Misty, Hazy, Downpour, Tunnel
Vision — are applied server-side *after* the beam is computed, and the view does not say they are
in force. In those rounds the preview may light more than the placement will.

**Figures are face tokens, not standees.** There is no standee or full-body art anywhere in
`game-assets/` — only the 118px round faces in `tokens/investigator/faces/`. Investigators wear
those (yours ringed in amber); anything with no art falls back to a coloured disc with initials.
Four objective tokens named in `scenarios.json` — Lockbox, Battery, RepairKit, SparkPlug — have
no art at all.

**No card images.** Items, Wounds, Conditions, Events and Adversary cards are shown as name +
rules text from `game-data`, not as card faces. The art exists only as print-sheet PDFs.

**Ability text still contains icon markup.** `{footprint}`, `{charge}`, `{involvedaction}` and
the rest of `investigators.json`'s `_icons` are rendered literally; there is no glyph atlas yet.

**One board per game.** The board is built from the first `update`'s `scenarioId`; switching
scenario mid-session means leaving the table and rejoining.

**Mini-map / adversary-board tracks are text.** Counters, cooldowns and tracks are printed as
`key value` rows rather than drawn on the adversary board art.

**Reconnect is manual.** A dropped socket does not auto-reconnect: go back to the menu, press
CONNECT, and use **Rejoin <CODE>** (the per-room token is saved in PlayerPrefs). The server then
sends a full `resync`.

**No sound, no animation, no card hover previews.** Lemonade Wars has all three; this does not.
