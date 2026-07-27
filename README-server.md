# The Stifling Dark — multiplayer server

A WebSocket host for the engine in `src/StiflingDark.Engine`. One authoritative `Game` per
room, one redacted `PlayerView` per seat, and **any subset of seats may be played by a bot** —
the same bot brains the arena plays with. A human plays through the protocol whether the other
three seats are people, bots, or a mix.

## Layout

| Project | Target | What it is |
| --- | --- | --- |
| `src/StiflingDark.Engine` | netstandard2.1 | The rules. Now also `Core/PlayerView.cs`, the per-role projection. |
| `src/StiflingDark.Protocol` | netstandard2.1 | Wire messages, one `GameCommand` per public `Game` method, and the JSON codec. Unity-importable. |
| `src/StiflingDark.Bots` | net8.0 | The arena's bot brains, plus `AdversarySetup` for a bot Adversary's secret placements. |
| `src/StiflingDark.Server` | net8.0 | Rooms, seats, sessions, the bot pump. |
| `tools/BotArena` | net8.0 | Unchanged CLI, now consuming `StiflingDark.Bots`. |

## Run it locally

```sh
~/.dotnet/dotnet run --project src/StiflingDark.Server
```

Listens on `http://0.0.0.0:5226` — `GET /` and `GET /health` for a pulse, `ws://…/ws` for play.
`game-data/` is found by walking up from the binary, so no configuration is needed in the repo.

| Variable | Default | Meaning |
| --- | --- | --- |
| `PORT` | `5226` | HTTP/WebSocket port. |
| `GAME_DATA_DIR` | *(walk up to `game-data/`)* | Where the maps, LoS masks, and card decks live. |
| `DATA_DIR` | `<bindir>/rooms` | Room snapshots and the player-identity store. |
| `BOT_DELAY_MS` | `1100` | Pause between bot **turns**. `0` for instant. |
| `RATE_LIMIT_PER_SEC` | `10` | Messages per second per socket. `0` disables. |
| `MAX_ROOMS` | `2000` | Global room ceiling. |

## Run it in Docker

```sh
docker compose up --build          # http://localhost:5226
docker compose logs -f server
```

or without compose:

```sh
docker build -t stifling-dark-server .
docker run -p 5226:5226 -v sd-rooms:/data stifling-dark-server
```

The image copies `game-data/` — including `maps/*.json` and the `*-los-mask.bin` raster
line-of-sight masks — into `/app/game-data` and sets `GAME_DATA_DIR`. Room snapshots go to the
`/data` volume, so games survive a redeploy. Point your platform's health probe at
`GET /health`.

## The protocol

Every message is a JSON object with a `type`. Enums travel as camelCase strings.

**Client → server**

| Type | Fields | Notes |
| --- | --- | --- |
| `hello` | `playerKey`, `name` | Durable identity. The server stores only a SHA-256 of the key. |
| `create_room` | `name`, `role?` | `role` is `investigator` (default) or `adversary`. |
| `join_room` | `code`, `name`, `token?`, `role?` | `token` reclaims a specific seat. |
| `leave_room` | — | Frees a lobby seat; a running game keeps it for reconnects. |
| `set_seat` | `seat`, `role?`, `fill?`, `investigatorId?` | Host only, pre-game. `fill` is `human` or `bot`. |
| `add_bot` | `role`, `investigatorId?` | Host only, pre-game. |
| `remove_seat` | `seat` | Host only, pre-game. |
| `configure` | `scenarioId?`, `adversaryId?`, `startSpaces?`, `medicalItemSpaces?`, `useMiniExpansionCards?` | Host only, pre-game. Anything left unset is filled in at start. |
| `set_speed` | `speed` | `slow` / `medium` / `fast`. Any seated human, any time. |
| `ready` | `ready` | Lobby readiness. Bots are always ready. |
| `start_game` | — | Host only. |
| `command` | `command` | One game action — see below. |
| `resync` | — | Re-send my whole view and log. |
| `list_games` | — | Needs `hello` first. |

**Server → client**

| Type | Carries |
| --- | --- |
| `welcome` | `playerId`, `name`, `gamesList` |
| `room` | `code`, `yourSeat`, `token`, `started`, `speed`, `setup`, `seats` |
| `update` | `events` (log delta), `view` (this seat's `PlayerView`), `actingSeats`, `yourTurn`, `resync` |
| `games` | `gamesList` |
| `turn_alert` | `code` — a game elsewhere is waiting on you |
| `error` | `message` |

### Commands

A `command` message wraps `{"$type": "<CommandName>", …fields}`. There is exactly one command
per public `Game` method a seat may call, so the protocol is a mirror of the rules API rather
than a second model of it; the engine stays the only thing that validates anything, and an
illegal command comes back as an `error` carrying the engine's own message. A seat may only
send commands for its own side of the table.

*Adversary setup* — `PlaceHiddenEvidenceCommand`, `PlacePoiTokenCommand`, `PlaceAdversaryCommand`,
`SetupCultistsCommand`, `SetupAdversaryCardsCommand`, `FinishAdversarySetupCommand`.

*Investigator turn* — `BeginInvestigatorTurnCommand`, `SprintCommand`, `RestCommand`,
`MoveStepCommand`, `ResolveWindowCommand`, `PickUpEvidenceCommand`, `ActivateLightSwitchCommand`,
`LockDoorCommand`, `OpenDoorCommand`, `PickUpMedicalItemCommand`, `PickUpPoiTokenCommand`,
`TradeItemCommand`, `TradeEvidenceCommand`, `ChargeFlashlightCommand`, `PlaceFlashlightCommand`,
`TakeInvolvedActionCommand`, `EndTurnCommand`, `UseItemCommand`, `UseMinorAbilityCommand`,
`UseMajorAbilityCommand`, `ResolvePainkillersCommand`, `ResolveEventChoiceCommand`,
`AdoptSpiritCommand`, `UseSpiritAbilityCommand`, `CommiserateCommand`.

*Evidence and map rewards* — `TurnInEvidenceCommand`, `PlaceOpenWindowTokenCommand`,
`PlaceDimTokenCommand`, `PlaceSecretPassageCommand`.

*Objectives* — `DrawEscapeChoicesCommand`, `SelectEscapeCardCommand`,
`PickUpObjectiveTokenCommand`, `DropObjectiveTokenCommand`, `OpenLockboxCommand`,
`PowerTheGateCommand`, `EscapeThroughGateCommand`, `InstallPartCommand`, `StartTruckCommand`,
`EscapeAtTruckExitCommand`, `FireFlareGunCommand`, `EscapeByHelicopterCommand`,
`PickUpRidePartsCommand`, `OpenServiceTunnelCommand`, `EscapeThroughTunnelCommand`,
`DigUpGraveCommand`, `UseTheHookCommand`, `UseFrayedRopesCommand`, `DestroyEggSacCommand`,
`BanishTheHorrorCommand`, `PickUpBanishTokenCommand`, `UseRitualKnifeCommand`,
`CutRopeCircleCommand`.

*Adversary turn* — `AdversaryMoveStepCommand`, `AdversaryDisappearCommand`,
`AdversaryBreakDoorCommand`, `AdversaryEndTurnCommand`, `PlayAdversaryCardCommand`,
`ButcherStalkCommand`, `PlaceGraveCommand`, `AnswerFrayedRopesCommand`, `HorrorAmbushCommand`,
`EnragedGatherCommand`, `PlaceEggSacCommand`, `CultistMoveStepCommand`,
`CultistDisappearCommand`, `CultistBreakDoorCommand`, `BloodlettingCommand`,
`TheFinalSacrificeCommand`, `MorgonnodCorporealMoveStepCommand`, `PlaceRitualTokensCommand`,
`UsePossessedCommand`, `FlipBufotoxinFaceUpCommand`.

The server never sends a `GameState`. `WireCodec` has no encoder that takes one: the only way
board state reaches a client is through `Game.ViewFor(role, …)`.

## One human against three bots

```jsonc
→ {"type":"create_room","name":"Matt"}          // seat 0, Investigator, human
← {"type":"room","code":"KQ7MP","yourSeat":0,"token":"…","seats":[…]}

→ {"type":"add_bot","role":"investigator"}      // seat 1
→ {"type":"add_bot","role":"investigator"}      // seat 2
→ {"type":"add_bot","role":"adversary"}         // seat 3
← {"type":"room", …}                            // after each

→ {"type":"configure","scenarioId":"sawmill","adversaryId":"butcher"}   // optional
→ {"type":"start_game"}
```

The bot Adversary plays its own secret setup (hidden Evidence, POI tokens and which one hides
the Cursed front, the standee, the card loadout), then round 1 opens. From there the client
just reacts to `update`:

```jsonc
← {"type":"update","yourTurn":true,"view":{"round":1,"activeInvestigator":null,…}}
→ {"type":"command","command":{"$type":"BeginInvestigatorTurnCommand","investigatorId":"aira"}}
→ {"type":"command","command":{"$type":"MoveStepCommand","to":"S-21"}}
→ {"type":"command","command":{"$type":"PlaceFlashlightCommand","angleRadians":1.57}}
→ {"type":"command","command":{"$type":"EndTurnCommand"}}
```

Turn order inside a round is the team's to choose, so between turns `actingSeats` lists every
Investigator who has not gone yet. The bot brain picks an order and drives its own seats; when
its pick is a human, the pump stops and waits. A human may also simply begin their own turn.

To play the monster instead, create the room with `"role":"adversary"` and add bot
Investigators — then drive the setup commands yourself before round 1.

Reconnecting: `join_room` with your `token` (or, after `hello`, with the same identity)
reclaims the seat and triggers a full `update` with `"resync":true` carrying the complete log.

## What each seat can see

`PlayerView` is the only state object that crosses the network. A redacted field is **absent**
from the JSON, never blanked, so "hidden" and "not there" look identical in the bytes.

| Information | Investigator view | Adversary view |
| --- | --- | --- |
| Adversary standee position | only while `revealed` | always |
| Cult figures | per figure, only while that figure is `revealed` | always |
| Shadow / Noise tokens | visible | visible |
| Adversary MP and Sprint roll | absent | visible |
| Adversary Counters | printed tracks only (stalk, blood, …) | all |
| Adversary Attack / Ability ids | only once played, then forever; counts always | all |
| Actions used this turn | absent | visible |
| Hidden Evidence token | entry omitted until revealed; sticky afterwards | always |
| POI token position | only once revealed, Scouted, or collected | always |
| POI token front | only while genuinely face-up | hidden again once Scouted |
| Investigator Items | all Investigators' | count only |
| Investigator Conditions | all Investigators' | count only |
| Face-down Bufotoxin | hidden from its holder until flipped | flip targets listed |
| Face-down Wound card id | absent — nobody at the table may read it | absent |
| Escape shortlist | visible while pending | absent |
| Objective tokens on the mini-map (`altar`, `grave-actual`) | hidden until revealed | always |
| Board overlay, lights, doors, flashlights | visible | visible |
| Deck order | counts only | counts only |
| Log | public line types only | all |

`ViewRole.Spectator` is the unredacted truth, for replay and debugging. Never hand it to a
seated player.

## Tests

```sh
~/.dotnet/dotnet test
```

`tests/StiflingDark.Engine.Tests` covers the rules and the view redactions;
`tests/StiflingDark.Server.Tests` drives whole games over real WebSockets — room lifecycle,
one human plus three bots across several rounds, reconnect resync, and a redaction check that
asserts the Adversary's hidden space never appears in the Investigator client's received bytes.
