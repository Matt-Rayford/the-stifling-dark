using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>Which side of the table is looking.</summary>
    public enum ViewRole
    {
        /// <summary>An Investigator player. Sees the Investigator side of the table plus
        /// whatever the Adversary has been forced to reveal.</summary>
        Investigator,

        /// <summary>The Adversary player. Sees their own hidden figures and tokens, but not
        /// the identities of the Investigators' Item and Condition cards.</summary>
        Adversary,

        /// <summary>
        /// Debug / replay only: the unredacted truth. NEVER hand this to a seated player —
        /// it is the whole <see cref="GameState"/> in view shape, hidden tokens included.
        /// </summary>
        Spectator,
    }

    /// <summary>
    /// What one seat is allowed to see. This is the ONLY state object that should ever cross
    /// the network to a client: the full <see cref="GameState"/> (deck order, RNG state, hidden
    /// token placements, the Adversary's unplayed card loadout) stays server-side. Rich enough
    /// to drive a whole client UI without touching the <see cref="Game"/> object.
    /// </summary>
    /// <remarks>
    /// Every field that can be redacted is nullable or count-only, and the nulls are the point:
    /// a redacted field is ABSENT from the serialized view, not blanked, so a client cannot
    /// distinguish "hidden" from "not there" by looking at the bytes.
    /// </remarks>
    public sealed class PlayerView
    {
        public ViewRole Role { get; set; }

        /// <summary>The Investigator this seat plays; null for the Adversary and spectators.</summary>
        public string? ViewerInvestigatorId { get; set; }

        public string ScenarioId { get; set; } = "";
        public GamePhase Phase { get; set; }
        public GameResult Result { get; set; }
        public int Round { get; set; }
        /// <summary>Rounds in the scenario, so a client can draw the round tracker.</summary>
        public int TotalRounds { get; set; }
        public string? ActiveInvestigator { get; set; }
        public bool PendingWindowChoice { get; set; }

        public string? CurrentEvent { get; set; }
        public List<string> PendingEventChoices { get; set; } = new List<string>();
        public List<string> PersistentMajorEvents { get; set; } = new List<string>();
        public Dictionary<string, int> RoundModifiers { get; set; } = new Dictionary<string, int>();

        public List<InvestigatorPanel> Investigators { get; set; } = new List<InvestigatorPanel>();
        public AdversaryPanel Adversary { get; set; } = new AdversaryPanel();

        /// <summary>
        /// Evidence tokens. Investigators only ever see the Revealed ones — a hidden token's
        /// zone entry is omitted entirely, so the absence of an entry carries no information
        /// beyond "not found yet", which the Investigators already know.
        /// </summary>
        public List<EvidenceInfo> Evidence { get; set; } = new List<EvidenceInfo>();

        /// <summary>One entry per printed Point of Interest (the POI spaces themselves are
        /// printed on the board, so the list length is public). The token's SPACE and FRONT
        /// are redacted independently — see <see cref="PoiInfo"/>.</summary>
        public List<PoiInfo> PoiTokens { get; set; } = new List<PoiInfo>();

        public List<string> MedicalItemSpaces { get; set; } = new List<string>();

        /// <summary>Board overlay, lights, and doors: fully public to every seat.</summary>
        public OverlayInfo Overlay { get; set; } = new OverlayInfo();
        public List<FlashlightInfo> Flashlights { get; set; } = new List<FlashlightInfo>();
        public List<string> FalteringZones { get; set; } = new List<string>();
        public Dictionary<string, string> BoardTokens { get; set; } = new Dictionary<string, string>();

        public ObjectiveInfo Objective { get; set; } = new ObjectiveInfo();
        public DeckCounts Decks { get; set; } = new DeckCounts();

        /// <summary>
        /// The three Escape cards on offer, while the Investigators are choosing. Supplied by
        /// the caller (the engine's <see cref="Game.DrawEscapeChoices"/> consumes RNG, so the
        /// host draws once and holds the result); present in Investigator and Spectator views
        /// only — the Adversary does not get to see the shortlist.
        /// </summary>
        public List<string>? EscapeChoices { get; set; }

        /// <summary>Spirit cards a dead Investigator's player may still adopt.</summary>
        public List<string> AvailableSpiritIds { get; set; } = new List<string>();

        /// <summary>
        /// Adversary view only: Investigators holding a still-face-down Bufotoxin, i.e. the
        /// legal targets of <see cref="Game.FlipBufotoxinFaceUp"/>. The Adversary dealt the
        /// card, so this is not a leak; it is the one piece of Condition identity the Adversary
        /// is entitled to.
        /// </summary>
        public List<string> BufotoxinFlipTargets { get; set; } = new List<string>();

        /// <summary>
        /// Adversary Attack / Ability card ids this seat has learned, because the Adversary
        /// played them at least once. Empty for the Adversary's own view, which simply sees
        /// the real loadout.
        /// </summary>
        public List<string> KnownAdversaryCards { get; set; } = new List<string>();

        /// <summary>The event log as this seat is allowed to read it (see Game.ViewFor).</summary>
        public List<LogEntry> Log { get; set; } = new List<LogEntry>();

        // ------------------------------------------------------------- panels

        public sealed class InvestigatorPanel
        {
            public string DefId { get; set; } = "";
            public string Space { get; set; } = "";
            public int Stamina { get; set; }
            public int Charge { get; set; }
            public int MajorAbilityTokens { get; set; }
            public bool Dead { get; set; }
            public bool Escaped { get; set; }
            public string? SpiritId { get; set; }
            public int SpiritMajorTokens { get; set; }

            /// <summary>Wound slots, in order. A face-down Wound's card id is redacted for
            /// EVERY seat including its owner: nobody at the table may read it.</summary>
            public List<WoundSlot> Wounds { get; set; } = new List<WoundSlot>();
            /// <summary>Wounds parked outside the printed slots (Neurotoxin).</summary>
            public List<WoundSlot> NonSlotWounds { get; set; } = new List<WoundSlot>();

            /// <summary>Item card ids — null in the Adversary view, which sees only
            /// <see cref="ItemCount"/>.</summary>
            public List<string>? Items { get; set; }
            public int ItemCount { get; set; }

            /// <summary>Condition card ids — null in the Adversary view. In Investigator views
            /// a still-face-down Bufotoxin is dropped from the list (its holder may not read
            /// it) while still counting toward <see cref="ConditionCount"/>.</summary>
            public List<string>? Conditions { get; set; }
            public int ConditionCount { get; set; }

            public List<string> EvidenceCarried { get; set; } = new List<string>();
            public List<string> MapTokens { get; set; } = new List<string>();

            /// <summary>Round this Investigator was given a Spine Chill token; 0 for none.</summary>
            public int SpineChillRound { get; set; }

            // Per-turn bookkeeping — all of it public at the table.
            public int MpRemaining { get; set; }
            public bool TurnTakenThisRound { get; set; }
            public bool SprintedOrRested { get; set; }
            public bool Rested { get; set; }
            public bool MovementLocked { get; set; }
            public FinalActionKind FinalAction { get; set; }
            public int SpiritAbilitiesUsedThisTurn { get; set; }
            public bool WaterFloatUsedThisTurn { get; set; }
            public bool CarriageRotationUsedThisRound { get; set; }
        }

        public sealed class WoundSlot
        {
            public bool FaceUp { get; set; }
            /// <summary>Null while the card is face-down.</summary>
            public string? CardId { get; set; }
        }

        public sealed class AdversaryPanel
        {
            public string DefId { get; set; } = "";
            /// <summary>Null while the main figure is hidden from this seat.</summary>
            public string? Space { get; set; }
            public bool Revealed { get; set; }

            /// <summary>Cult figures, revealed individually.</summary>
            public List<FigureInfo> Figures { get; set; } = new List<FigureInfo>();

            /// <summary>Shadow tokens: the trail the Investigators are meant to read.</summary>
            public Dictionary<string, string> ShadowTokens { get; set; } = new Dictionary<string, string>();
            public List<string> NoiseTokens { get; set; } = new List<string>();

            /// <summary>Adversary-board tracks. Investigators see the printed tracks only
            /// (stalk, blood, ...); private bookkeeping counters are dropped.</summary>
            public Dictionary<string, int> Counters { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int> SpineChill { get; set; } = new Dictionary<string, int>();

            public int Kills { get; set; }
            public int KillsToWin { get; set; }

            public bool TurnStarted { get; set; }
            /// <summary>Null for Investigators: the movement budget is behind the screen.</summary>
            public int? MpRemaining { get; set; }
            /// <summary>Null for Investigators, for the same reason.</summary>
            public int? SprintRolled { get; set; }
            public bool AttackUsedThisTurn { get; set; }
            public bool AttackLockedThisTurn { get; set; }
            public bool CarriageRotationUsedThisRound { get; set; }

            /// <summary>Null until this seat has seen the Attack card played.</summary>
            public string? AttackCard { get; set; }
            /// <summary>Active Ability ids this seat knows; the rest are counted only.</summary>
            public List<string> ActiveAbilities { get; set; } = new List<string>();
            public int ActiveAbilityCount { get; set; }
            public int FaceDownAbilityCount { get; set; }
            public List<CooldownInfo> Cooldown1 { get; set; } = new List<CooldownInfo>();
            public List<CooldownInfo> Cooldown2 { get; set; } = new List<CooldownInfo>();

            /// <summary>Actions already spent this turn — Adversary view only (the ids name
            /// cards the Investigators may not have seen yet).</summary>
            public List<string> ActionsUsed { get; set; } = new List<string>();
        }

        public sealed class FigureInfo
        {
            public string Id { get; set; } = "";
            /// <summary>Null while this figure is hidden from the viewer.</summary>
            public string? Space { get; set; }
            public bool Revealed { get; set; }
            public bool Alive { get; set; }
        }

        public sealed class CooldownInfo
        {
            /// <summary>Null when this seat has not learned the card's identity.</summary>
            public string? CardId { get; set; }
            public bool FaceUp { get; set; }
        }

        public sealed class EvidenceInfo
        {
            public string Zone { get; set; } = "";
            public string Space { get; set; } = "";
            public bool Revealed { get; set; }
        }

        public sealed class PoiInfo
        {
            /// <summary>The printed Point of Interest space — public map data.</summary>
            public string PoiSpace { get; set; } = "";
            /// <summary>Where the token sits; null while its position is hidden from this seat.</summary>
            public string? TokenSpace { get; set; }
            /// <summary>True = Cursed Item front, false = General Item front; null while the
            /// face is hidden from this seat.</summary>
            public bool? CursedFront { get; set; }
            public bool Revealed { get; set; }
            public bool Collected { get; set; }
            /// <summary>Vincent Scouted it: on the main board, but face-down.</summary>
            public bool ScoutedFaceDown { get; set; }
        }

        public sealed class OverlayInfo
        {
            public Dictionary<string, DoorState> DoorStates { get; set; } = new Dictionary<string, DoorState>();
            public List<string> BrightZones { get; set; } = new List<string>();
            public List<string> DimZones { get; set; } = new List<string>();
            public List<string> BrightSpaces { get; set; } = new List<string>();
            public MirrorDoorColor? OpenMirrorColor { get; set; }
            public List<string> OpenWindows { get; set; } = new List<string>();
            public List<string> FalseWindows { get; set; } = new List<string>();
            public List<string> SecretPassages { get; set; } = new List<string>();
            public List<string> AdversaryBarriers { get; set; } = new List<string>();
        }

        public sealed class FlashlightInfo
        {
            public string InvestigatorId { get; set; } = "";
            public string Space { get; set; } = "";
            public double AngleRadians { get; set; }
            public List<string> BrightSpaces { get; set; } = new List<string>();
        }

        public sealed class ObjectiveInfo
        {
            public int EvidenceTurnedIn { get; set; }
            /// <summary>Evidence the team must turn in before the Escape card is chosen.</summary>
            public int EvidenceRequired { get; set; }
            public List<string> OncePerGameRewardsUsed { get; set; } = new List<string>();
            public string? SelectedEscapeCard { get; set; }
            /// <summary>Enough Evidence is in and the team owes its Escape card choice: no
            /// further turn begins and the round holds until it is made.</summary>
            public bool EscapeChoicePending { get; set; }
            /// <summary>Token name -> space. Tokens the rules keep on the mini-map until they
            /// are Revealed (the actual Grave, the Altar) are omitted for Investigators.</summary>
            public Dictionary<string, string> Tokens { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, string> TokenCarriers { get; set; } = new Dictionary<string, string>();
            public int Supplies { get; set; }
            public int PartsInstalled { get; set; }
            public bool EscapeOpen { get; set; }
            public int? EscapeReadyRound { get; set; }
            public int OpenLockboxUsedRound { get; set; }
            public int InstallPartUsedRound { get; set; }
            public int StartTruckUsedRound { get; set; }
        }

        /// <summary>Deck sizes only — the order of every deck is secret from every seat.</summary>
        public sealed class DeckCounts
        {
            public int Event { get; set; }
            public int GeneralItem { get; set; }
            public int CursedItem { get; set; }
            public int Wound { get; set; }
            public int WoundDiscard { get; set; }
        }

        public sealed class LogEntry
        {
            public int Round { get; set; }
            public string Type { get; set; } = "";
            public string Detail { get; set; } = "";
        }
    }

    public sealed partial class Game
    {
        /// <summary>
        /// Log entry types an Investigator seat may read in full. "adversary" is included: a
        /// played card, a broken door, a Stalk/track/counter change, a Disappear or a Reveal
        /// notice are all things a table player would see or hear happen. What is NOT public —
        /// a hidden figure's current or stepped-through space, a forced drag's destination
        /// while still Hidden, an unrevealed Grave/Altar location — is written under
        /// <see cref="AdversaryHiddenPositionLogType"/> instead of "adversary" at the call
        /// site (see Game.cs / Game.ItemEffects.cs / Game.Butcher.cs), so it falls through to
        /// the conservative default below and stays dropped. "setup" (Cultist/Altar
        /// placements), the engine's own "todo" notes, and any type a later rule invents are
        /// dropped for the same reason: the conservative default for an unclassified line is
        /// "hidden".
        /// </summary>
        private static readonly HashSet<string> InvestigatorPublicLogTypes = new HashSet<string>
        {
            "ability", "adversary", "condition", "death", "deck", "escape", "event", "evidence",
            "flashlight", "gameover", "item", "lights", "objective", "reveal", "ride", "spirit",
            "sprint", "token", "water", "wound",
        };

        /// <summary>
        /// Log type for "adversary" lines that name a hidden figure's own space or an
        /// unrevealed token's location — the exact information <see cref="ViewFor"/> must keep
        /// off an Investigator's screen even though the general "adversary" type above is now
        /// public. Never added to <see cref="InvestigatorPublicLogTypes"/>: these lines are
        /// meant to fall through to the default drop.
        /// </summary>
        private const string AdversaryHiddenPositionLogType = "adversary-secret";

        /// <summary>
        /// Adversary Counters an Investigator may read: the printed adversary-board tracks and
        /// the public objective gauges. Everything else in that bag is private bookkeeping
        /// (per-Investigator Bufotoxin flags, internal round stamps) and is dropped.
        /// </summary>
        private static readonly HashSet<string> InvestigatorPublicCounters = new HashSet<string>
        {
            "stalk", "blood", "enraged", "corporeal", "eggsacs-remaining", "eggsacs-destroyed",
            "banish-supplies", "altar-revealed", "burning-until", "knife-used-round",
            "skip-sprint-die-round", AbilitiesBlockedRoundKey,
        };

        /// <summary>Objective tokens the rules keep on the Adversary's mini-map until the
        /// Investigators light them up.</summary>
        private const string GraveActualToken = "grave-actual";
        private const string AltarToken = "altar";

        private const string PlayedLogPrefix = "played ";

        /// <summary>
        /// Project the authoritative state into what one seat may see.
        /// </summary>
        /// <param name="role">Which side is looking.</param>
        /// <param name="viewerInvestigatorId">
        /// The Investigator this seat plays. Required for <see cref="ViewRole.Investigator"/>
        /// only so the view can name itself; redaction is per-ROLE, never per-Investigator,
        /// because Investigators share everything they know across the table.
        /// </param>
        /// <param name="escapeChoices">
        /// The Escape shortlist the host drew, when one is pending. Reaches Investigator and
        /// Spectator views only.
        /// </param>
        public PlayerView ViewFor(ViewRole role, string? viewerInvestigatorId = null,
            IReadOnlyList<string>? escapeChoices = null)
        {
            bool adversarySeat = role == ViewRole.Adversary;
            bool omniscient = role == ViewRole.Spectator;
            bool investigatorSeat = role == ViewRole.Investigator;

            var known = investigatorSeat ? PlayedAdversaryCards() : null;

            var view = new PlayerView
            {
                Role = role,
                ViewerInvestigatorId = viewerInvestigatorId,
                ScenarioId = State.ScenarioId,
                Phase = State.Phase,
                Result = State.Result,
                Round = State.Round,
                TotalRounds = Db.Config.Rounds,
                ActiveInvestigator = State.ActiveInvestigator,
                PendingWindowChoice = State.PendingWindowChoice,
                CurrentEvent = State.CurrentEvent,
                PendingEventChoices = PendingEventChoices(),
                PersistentMajorEvents = PersistentMajorEvents(),
                RoundModifiers = new Dictionary<string, int>(State.RoundModifiers),
                MedicalItemSpaces = State.MedicalItemSpaces.ToList(),
                FalteringZones = State.FalteringZones.ToList(),
                BoardTokens = new Dictionary<string, string>(State.BoardTokens),
                AvailableSpiritIds = UnusedSpiritIds(),
                KnownAdversaryCards = known?.OrderBy(id => id, StringComparer.Ordinal).ToList()
                    ?? new List<string>(),
                EscapeChoices = escapeChoices != null && !adversarySeat
                    ? escapeChoices.ToList()
                    : null,
                Overlay = BuildOverlay(),
                Decks = new PlayerView.DeckCounts
                {
                    Event = State.EventDeck.Count,
                    GeneralItem = State.GeneralItemDeck.Count,
                    CursedItem = State.CursedItemDeck.Count,
                    Wound = State.WoundDeck.Count,
                    WoundDiscard = State.WoundDiscard.Count,
                },
                Flashlights = State.Flashlights.Select(f => new PlayerView.FlashlightInfo
                {
                    InvestigatorId = f.InvestigatorId,
                    Space = f.Space,
                    AngleRadians = f.AngleRadians,
                    BrightSpaces = f.BrightSpaces.ToList(),
                }).ToList(),
            };

            foreach (var inv in State.Investigators)
            {
                view.Investigators.Add(BuildInvestigator(inv, role));
            }

            view.Adversary = BuildAdversary(role, known);
            view.Evidence = BuildEvidence(investigatorSeat);
            view.PoiTokens = BuildPoiTokens(role);
            view.Objective = BuildObjective(investigatorSeat);
            view.Log = BuildLog(investigatorSeat);

            if (adversarySeat || omniscient)
            {
                view.BufotoxinFlipTargets = State.Investigators
                    .Where(i => HasCondition(i, "bufotoxin") &&
                                !State.Adversary.Counters.ContainsKey("bufotoxin-face-up:" + i.DefId))
                    .Select(i => i.DefId)
                    .ToList();
            }

            return view;
        }

        /// <summary>
        /// Adversary cards the table has seen, derived from the log rather than from extra
        /// state: <see cref="PlayAdversaryCard"/> writes one "played &lt;id&gt;" line per play,
        /// and a card that has been played is public knowledge from then on.
        /// </summary>
        private HashSet<string> PlayedAdversaryCards()
        {
            var played = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in State.Log)
            {
                if (entry.Type == "adversary" &&
                    entry.Detail.StartsWith(PlayedLogPrefix, StringComparison.Ordinal))
                {
                    played.Add(entry.Detail.Substring(PlayedLogPrefix.Length));
                }
            }
            return played;
        }

        private PlayerView.OverlayInfo BuildOverlay()
        {
            var overlay = State.Overlay;
            return new PlayerView.OverlayInfo
            {
                DoorStates = new Dictionary<string, DoorState>(overlay.DoorStates),
                BrightZones = overlay.BrightZones.OrderBy(z => z, StringComparer.Ordinal).ToList(),
                DimZones = overlay.DimZones.OrderBy(z => z, StringComparer.Ordinal).ToList(),
                BrightSpaces = overlay.BrightSpaces.OrderBy(s => s, StringComparer.Ordinal).ToList(),
                OpenMirrorColor = overlay.OpenMirrorColor,
                OpenWindows = overlay.OpenWindows.OrderBy(e => e, StringComparer.Ordinal).ToList(),
                FalseWindows = overlay.FalseWindows.OrderBy(e => e, StringComparer.Ordinal).ToList(),
                SecretPassages = overlay.SecretPassages.OrderBy(e => e, StringComparer.Ordinal).ToList(),
                AdversaryBarriers = overlay.AdversaryBarriers.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            };
        }

        private PlayerView.InvestigatorPanel BuildInvestigator(InvestigatorState inv, ViewRole role)
        {
            // Wound cards are drawn face-down and stay unreadable until something flips them:
            // not even the Investigator holding the card may look, so the id is redacted for
            // every seat. Only the replay/debug Spectator sees the truth.
            bool showFaceDownWounds = role == ViewRole.Spectator;
            PlayerView.WoundSlot Slot(WoundInstance wound) => new PlayerView.WoundSlot
            {
                FaceUp = wound.FaceUp,
                CardId = wound.FaceUp || showFaceDownWounds ? wound.CardId : null,
            };

            var panel = new PlayerView.InvestigatorPanel
            {
                DefId = inv.DefId,
                Space = inv.Space,
                Stamina = inv.Stamina,
                Charge = inv.Charge,
                MajorAbilityTokens = inv.MajorAbilityTokens,
                Dead = inv.Dead,
                Escaped = inv.Escaped,
                SpiritId = inv.SpiritId,
                SpiritMajorTokens = inv.SpiritMajorTokens,
                Wounds = inv.Wounds.Select(Slot).ToList(),
                NonSlotWounds = inv.NonSlotWounds.Select(Slot).ToList(),
                ItemCount = inv.Items.Count,
                ConditionCount = inv.Conditions.Count,
                EvidenceCarried = inv.EvidenceCarried.ToList(),
                MapTokens = inv.MapTokens.ToList(),
                SpineChillRound = State.Adversary.SpineChill.TryGetValue(inv.DefId, out int chill)
                    ? chill
                    : 0,
                MpRemaining = inv.MpRemaining,
                TurnTakenThisRound = inv.TurnTakenThisRound,
                SprintedOrRested = inv.SprintedOrRested,
                Rested = inv.Rested,
                MovementLocked = inv.MovementLocked,
                FinalAction = inv.FinalAction,
                SpiritAbilitiesUsedThisTurn = inv.SpiritAbilitiesUsedThisTurn,
                WaterFloatUsedThisTurn = inv.WaterFloatUsedThisTurn,
                CarriageRotationUsedThisRound = inv.CarriageRotationUsedThisRound,
            };

            if (role == ViewRole.Adversary)
            {
                // The Adversary player never reads the Investigators' Item or Condition cards —
                // only how many of each are on the table.
                return panel;
            }

            panel.Items = inv.Items.ToList();
            panel.Conditions = role == ViewRole.Spectator
                ? inv.Conditions.ToList()
                // Bufotoxin is dealt face-down: its holder may not read it until the Adversary
                // flips it. It still counts in ConditionCount, so the slot is visibly occupied.
                : inv.Conditions
                    .Where(id => id != "bufotoxin" ||
                                 State.Adversary.Counters.ContainsKey("bufotoxin-face-up:" + inv.DefId))
                    .ToList();
            return panel;
        }

        private PlayerView.AdversaryPanel BuildAdversary(ViewRole role, HashSet<string>? known)
        {
            var adv = State.Adversary;
            bool hideFigures = role == ViewRole.Investigator;
            bool behindTheScreen = role == ViewRole.Investigator;

            var panel = new PlayerView.AdversaryPanel
            {
                DefId = adv.DefId,
                Revealed = adv.Revealed,
                Space = !hideFigures || adv.Revealed ? adv.Space : null,
                Figures = adv.Figures.Select(f => new PlayerView.FigureInfo
                {
                    Id = f.Id,
                    Revealed = f.Revealed,
                    Alive = f.Alive,
                    // Cult figures are revealed one at a time: an unrevealed Cultist keeps its
                    // space even while its neighbour is standing in a flashlight beam.
                    Space = !hideFigures || f.Revealed ? f.Space : null,
                }).ToList(),
                ShadowTokens = new Dictionary<string, string>(adv.ShadowTokens),
                NoiseTokens = adv.NoiseTokens.ToList(),
                SpineChill = new Dictionary<string, int>(adv.SpineChill),
                Kills = adv.Kills,
                KillsToWin = adv.KillsToWin,
                TurnStarted = adv.TurnStarted,
                MpRemaining = behindTheScreen ? (int?)null : adv.MpRemaining,
                SprintRolled = behindTheScreen ? (int?)null : adv.SprintRolled,
                AttackUsedThisTurn = adv.AttackUsedThisTurn,
                AttackLockedThisTurn = adv.AttackLockedThisTurn,
                CarriageRotationUsedThisRound = adv.CarriageRotationUsedThisRound,
                ActiveAbilityCount = adv.ActiveAbilities.Count,
                FaceDownAbilityCount = adv.FaceDownAbilities.Count,
            };

            panel.Counters = behindTheScreen
                ? adv.Counters.Where(kv => InvestigatorPublicCounters.Contains(kv.Key))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
                : new Dictionary<string, int>(adv.Counters);

            if (!behindTheScreen)
            {
                panel.AttackCard = adv.AttackCard;
                panel.ActiveAbilities = adv.ActiveAbilities.ToList();
                panel.ActionsUsed = adv.ActionsUsed.OrderBy(a => a, StringComparer.Ordinal).ToList();
                panel.Cooldown1 = adv.Cooldown1
                    .Select(c => new PlayerView.CooldownInfo { CardId = c.CardId, FaceUp = c.FaceUp })
                    .ToList();
                panel.Cooldown2 = adv.Cooldown2
                    .Select(c => new PlayerView.CooldownInfo { CardId = c.CardId, FaceUp = c.FaceUp })
                    .ToList();
                return panel;
            }

            // Investigators learn a card id the first time it is played, and never forget it.
            var seen = known ?? new HashSet<string>(StringComparer.Ordinal);
            panel.AttackCard = adv.AttackCard != null && seen.Contains(adv.AttackCard)
                ? adv.AttackCard
                : null;
            panel.ActiveAbilities = adv.ActiveAbilities.Where(seen.Contains).ToList();
            panel.Cooldown1 = adv.Cooldown1.Select(c => Cooled(c, seen)).ToList();
            panel.Cooldown2 = adv.Cooldown2.Select(c => Cooled(c, seen)).ToList();
            return panel;
        }

        private static PlayerView.CooldownInfo Cooled(CooldownCard card, HashSet<string> seen) =>
            new PlayerView.CooldownInfo
            {
                CardId = seen.Contains(card.CardId) ? card.CardId : null,
                FaceUp = card.FaceUp,
            };

        private List<PlayerView.EvidenceInfo> BuildEvidence(bool investigatorSeat)
        {
            var list = new List<PlayerView.EvidenceInfo>();
            foreach (var pair in State.Evidence.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                // A hidden Evidence token sits on the Adversary's mini-map. Omit the whole
                // entry: a zone key with a blanked space would still say "not found yet",
                // and once Revealed the token stays known even after the light fades.
                if (investigatorSeat && !pair.Value.Revealed)
                {
                    continue;
                }
                list.Add(new PlayerView.EvidenceInfo
                {
                    Zone = pair.Key,
                    Space = pair.Value.Space,
                    Revealed = pair.Value.Revealed,
                });
            }
            return list;
        }

        private List<PlayerView.PoiInfo> BuildPoiTokens(ViewRole role)
        {
            var list = new List<PlayerView.PoiInfo>();
            foreach (var poi in State.PoiTokens.OrderBy(p => p.PoiSpace, StringComparer.Ordinal))
            {
                var info = new PlayerView.PoiInfo
                {
                    PoiSpace = poi.PoiSpace,
                    Revealed = poi.Revealed,
                    Collected = poi.Collected,
                    ScoutedFaceDown = poi.ScoutedFaceDown,
                    TokenSpace = poi.TokenSpace,
                    CursedFront = poi.CursedFront,
                };
                if (role == ViewRole.Investigator)
                {
                    // Position becomes public when the token is Revealed, when Vincent Scouted
                    // it onto the main board, or once it has been collected.
                    bool positionKnown = poi.Revealed || poi.ScoutedFaceDown || poi.Collected;
                    info.TokenSpace = positionKnown ? poi.TokenSpace : null;
                    // The front is readable only while the token is genuinely face-up. A
                    // Scouted token went onto the board face-down, so its face stays secret.
                    info.CursedFront = poi.Revealed && !poi.ScoutedFaceDown
                        ? (bool?)poi.CursedFront
                        : null;
                }
                else if (role == ViewRole.Adversary && poi.ScoutedFaceDown && !poi.Revealed)
                {
                    // The Investigators turned this one face-down on the main board, so the
                    // Adversary loses sight of its front too until it is Revealed for real.
                    info.CursedFront = null;
                }
                list.Add(info);
            }
            return list;
        }

        private PlayerView.ObjectiveInfo BuildObjective(bool investigatorSeat)
        {
            var objective = State.Objective;
            var tokens = new Dictionary<string, string>(objective.Tokens, StringComparer.Ordinal);
            if (investigatorSeat)
            {
                // The Altar is Revealed by lighting its space; the engine latches that in the
                // "altar-revealed" counter, so the reveal is sticky.
                if (AdversaryCounter("altar-revealed") != 1)
                {
                    tokens.Remove(AltarToken);
                }
                // The Grave has no latch: it is Revealed while its space is Bright, and known
                // for good once it has been dug up (which starts the Burning countdown).
                // Conservative reading — hide it again if the light goes out before the dig.
                if (tokens.TryGetValue(GraveActualToken, out string graveSpace) &&
                    !State.Adversary.Counters.ContainsKey("burning-until") &&
                    Graph.EffectiveLight(graveSpace, State.Overlay) != LightLevel.Bright)
                {
                    tokens.Remove(GraveActualToken);
                }
            }

            int required = Db.Config.ByInvestigatorCount
                .TryGetValue(State.Investigators.Count, out var rules)
                ? rules.EvidenceRequiredForObjective
                : 0;

            return new PlayerView.ObjectiveInfo
            {
                EvidenceTurnedIn = objective.EvidenceTurnedIn,
                EvidenceRequired = required,
                OncePerGameRewardsUsed = objective.OncePerGameRewardsUsed.ToList(),
                SelectedEscapeCard = objective.SelectedEscapeCard,
                EscapeChoicePending = EscapeChoicePending,
                Tokens = tokens,
                TokenCarriers = new Dictionary<string, string>(objective.TokenCarriers),
                Supplies = objective.Supplies,
                PartsInstalled = objective.PartsInstalled,
                EscapeOpen = objective.EscapeOpen,
                EscapeReadyRound = objective.EscapeReadyRound,
                OpenLockboxUsedRound = objective.OpenLockboxUsedRound,
                InstallPartUsedRound = objective.InstallPartUsedRound,
                StartTruckUsedRound = objective.StartTruckUsedRound,
            };
        }

        private List<PlayerView.LogEntry> BuildLog(bool investigatorSeat)
        {
            var log = new List<PlayerView.LogEntry>();
            foreach (var entry in State.Log)
            {
                if (investigatorSeat)
                {
                    if (!InvestigatorPublicLogTypes.Contains(entry.Type))
                    {
                        continue;
                    }
                    // The engine's own Bufotoxin prompt names the card, which would tell its
                    // holder what their face-down Condition is. Drop those lines wholesale.
                    if (entry.Detail.IndexOf("Bufotoxin", StringComparison.Ordinal) >= 0)
                    {
                        continue;
                    }
                }
                log.Add(new PlayerView.LogEntry
                {
                    Round = entry.Round,
                    Type = entry.Type,
                    Detail = entry.Detail,
                });
            }
            return log;
        }
    }
}
