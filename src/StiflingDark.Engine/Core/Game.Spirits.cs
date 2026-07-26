using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Spirit play (game-data/cards/spirits.json, game-data/config.json "spirits", and the
    /// "spirit" player aid): when an Investigator dies and the Adversary's win condition is
    /// not yet satisfied, that player may take an unused Spirit card and keep playing.
    ///
    /// A Spirit is the same <see cref="InvestigatorState"/> row with
    /// <see cref="InvestigatorState.SpiritId"/> set — it keeps the dead Investigator's Items,
    /// Evidence, and standee (so it still occupies a space and still shows up in the turn
    /// order) but loses the player board: no Stamina, no Charge, no Wounds, and therefore no
    /// second death. Everything that has to behave differently is either branched in Game.cs
    /// at the single point where the turn flow forks (see <see cref="IsSpirit"/> call sites
    /// there) or handled here.
    ///
    /// The Ability texts are implemented for real wherever the engine has the state to carry
    /// them (movement, light tokens, board tokens, revealing, moving tokens and Flashlights,
    /// Charge). The five clauses that hinge on the Adversary's *movement* paying attention to
    /// Spirit tokens (Cold Spot, Ectoplasm, Whirlwind, True Darkness) or on a Map Hazard
    /// bypass system (Mysterious Passage) set the state the clause needs and log a
    /// Log("todo", ...) naming the missing system: <see cref="AdversaryMoveStep"/> has no card
    /// hook to consult, and no Hazard-effect layer exists to suppress.
    /// </summary>
    public sealed partial class Game
    {
        // ---------- Rules constants (game-data/config.json "spirits") ----------

        /// <summary>Movement points a Spirit gets each turn ("mp": 4), plus a free Sprint die.</summary>
        public const int SpiritMp = 4;

        /// <summary>Major Ability tokens a Spirit starts with and can never regain ("majorAbilityTokens": 2).</summary>
        public const int SpiritMajorTokenStart = 2;

        /// <summary>Abilities a Spirit may use per turn ("abilities.maxUsedPerTurn": 2).</summary>
        public const int SpiritAbilitiesPerTurn = 2;

        /// <summary>How far Push and Spectral Hand may shift a token / Flashlight.</summary>
        private const int SpiritPushRange = 3;

        /// <summary>How far Luring Lights may walk an adjacent Investigator.</summary>
        private const int LuringLightsRange = 3;

        // ---------- Board-token instance prefixes (see Game.PlaceBoardToken) ----------

        private const string GhostOrbsPrefix = "ghost-orbs-";
        private const string EctoplasmPrefix = "ectoplasm-";
        /// <summary>Prefix + the Spirit's Investigator def id -> the Zone its Emergency Lights Dim token covers.</summary>
        private const string SpiritDimPrefix = "spirit-dim:";

        // ---------- Round-modifier keys owned by Spirit Abilities ----------

        /// <summary>Prefix + the Spirit's def id: Cold Spot is armed on their space this round.</summary>
        public const string SpiritColdSpotPrefix = "spirit-cold-spot:";

        /// <summary>Prefix + the Spirit's def id: Whirlwind's first-move surcharge is armed this round.</summary>
        public const string SpiritWhirlwindPrefix = "spirit-whirlwind:";

        /// <summary>Prefix + Zone letter: True Darkness' +1 Footprint for the Adversary this round.</summary>
        public const string SpiritZoneFootprintSurchargePrefix = "spirit-zone-footprint-surcharge:";

        /// <summary>Prefix + the Spirit's def id: Mysterious Passage's "spend 1 Stamina to ignore
        /// a Map Hazard adjacent to this Spirit" offer is open this round.</summary>
        public const string SpiritHazardBypassPrefix = "spirit-hazard-bypass:";

        /// <summary>Prefix + the Spirit's def id + ":" + the Adversary Ability card id chosen by
        /// Ectoplasm, once the Adversary steps on a token.</summary>
        public const string SpiritEctoplasmLockoutPrefix = "spirit-ectoplasm-lockout:";

        // ---------- The Spirit roster (game-data/cards/spirits.json) ----------
        //
        // spirits.json is not part of GameDatabase (nothing loads it yet), so the roster is
        // mirrored here and SpiritTests asserts it still matches the file on disk.

        private static readonly Dictionary<string, string> SpiritCardNames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["apparition"] = "Apparition",
                ["phantom"] = "Phantom",
                ["poltergeist"] = "Poltergeist",
            };

        private static readonly Dictionary<string, string[]> SpiritMinorAbilityIds =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["apparition"] = new[] { "ghost-orbs", "cold-spot" },
                ["phantom"] = new[] { "clairvoyance", "ectoplasm" },
                ["poltergeist"] = new[] { "whirlwind", "mysterious-passage" },
            };

        private static readonly Dictionary<string, string[]> SpiritMajorAbilityIds =
            new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["apparition"] = new[] { "energy-transfer", "emergency-lights" },
                ["phantom"] = new[] { "luring-lights", "true-darkness" },
                ["poltergeist"] = new[] { "push", "spectral-hand" },
            };

        private static readonly Dictionary<string, string> SpiritAbilityCardNames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ghost-orbs"] = "Ghost Orbs",
                ["cold-spot"] = "Cold Spot",
                ["energy-transfer"] = "Energy Transfer",
                ["emergency-lights"] = "Emergency Lights",
                ["clairvoyance"] = "Clairvoyance",
                ["ectoplasm"] = "Ectoplasm",
                ["luring-lights"] = "Luring Lights",
                ["true-darkness"] = "True Darkness",
                ["whirlwind"] = "Whirlwind",
                ["mysterious-passage"] = "Mysterious Passage",
                ["push"] = "Push",
                ["spectral-hand"] = "Spectral Hand",
            };

        /// <summary>Every Spirit card id, in spirits.json order.</summary>
        public static IReadOnlyList<string> SpiritIds { get; } =
            new[] { "apparition", "phantom", "poltergeist" };

        /// <summary>The printed name of a Spirit card.</summary>
        public static string SpiritName(string spiritId) =>
            SpiritCardNames.TryGetValue(spiritId, out string name)
                ? name
                : throw new InvalidOperationException($"Unknown Spirit '{spiritId}'.");

        /// <summary>A Spirit card's 4 Ability ids: its 2 Minors, then its 2 Majors.</summary>
        public static List<string> SpiritAbilityIds(string spiritId)
        {
            SpiritName(spiritId); // validates
            return SpiritMinorAbilityIds[spiritId].Concat(SpiritMajorAbilityIds[spiritId]).ToList();
        }

        /// <summary>Spirit cards no Investigator in this game has taken (max 1 per Spirit).</summary>
        public List<string> UnusedSpiritIds() =>
            SpiritIds.Where(id => State.Investigators.All(i => i.SpiritId != id)).ToList();

        /// <summary>True when this Investigator has become a Spirit.</summary>
        private static bool IsSpirit(InvestigatorState inv) => inv.SpiritId != null;

        /// <summary>Which figure's movement rules apply: Spirits float (flat 1 MP, through
        /// Locked Doors, Mirror Maze doors, Windows, and Map Hazards alike).</summary>
        private static FigureKind FigureKindOf(InvestigatorState inv) =>
            IsSpirit(inv) ? FigureKind.Spirit : FigureKind.Investigator;

        private static void RequireSpirit(InvestigatorState inv, string what)
        {
            if (!IsSpirit(inv))
            {
                throw new InvalidOperationException($"{inv.DefId} is not a Spirit and cannot {what}.");
            }
        }

        private static void RequireNotSpirit(InvestigatorState inv, string what)
        {
            if (IsSpirit(inv))
            {
                throw new InvalidOperationException($"{inv.DefId} is a Spirit and may not {what}.");
            }
        }

        /// <summary>Refuse a Trade whose recipient is a Spirit: "Spirits may not receive any
        /// Evidence or Items, nor may they Trade to other Spirits" (player aid). Called by both
        /// Trade actions on the receiving side only, so giving *away* stays legal.</summary>
        private static void RequireCanReceiveTrade(InvestigatorState target)
        {
            if (IsSpirit(target))
            {
                throw new InvalidOperationException(
                    $"{target.DefId} is a Spirit: Spirits may give Items and Evidence but never receive them.");
            }
        }

        // ---------- Adoption ----------

        /// <summary>
        /// The dead Investigator's player takes an unused Spirit card. Legal only while the
        /// game is still undecided — the Adversary's win condition is checked first (see
        /// <see cref="GainWound"/>), which is why a Butcher game never has Spirits: his win
        /// fires on the first death.
        /// </summary>
        public void AdoptSpirit(string deadInvId, string spiritId)
        {
            if (State.Phase == GamePhase.GameOver)
            {
                throw new InvalidOperationException("The game is over; the Adversary's win condition was satisfied.");
            }
            var inv = Investigator(deadInvId);
            if (!inv.Dead)
            {
                throw new InvalidOperationException($"{deadInvId} is not dead; only a dead Investigator may take a Spirit card.");
            }
            if (inv.SpiritId != null)
            {
                throw new InvalidOperationException($"{deadInvId} is already the {SpiritName(inv.SpiritId)}.");
            }
            string name = SpiritName(spiritId); // validates the id
            var holder = State.Investigators.FirstOrDefault(i => i.SpiritId == spiritId);
            if (holder != null)
            {
                throw new InvalidOperationException($"The {name} Spirit card is already {holder.DefId}'s.");
            }

            inv.SpiritId = spiritId;
            inv.SpiritMajorTokens = SpiritMajorTokenStart;
            inv.SpiritAbilitiesUsedThisTurn = 0;
            // "Spirits keep their Items, Evidence, and their standee, but remove their player
            // board and associated tokens": the Stamina/Charge tracks, the Wound slots, and the
            // Investigator's own Major Ability token all go away with the board. Conditions sit
            // on the board too, and every one of them is written against a player board
            // (Stamina, Charge, Wounds, the Investigator's Abilities), so they go with it.
            inv.Stamina = 0;
            inv.Charge = 0;
            inv.Wounds.Clear();
            inv.NonSlotWounds.Clear();
            inv.Conditions.Clear();
            inv.MajorAbilityTokens = 0;
            Log("spirit", $"{deadInvId} returns as the {name} with {SpiritMajorTokenStart} Major Ability token(s)");
        }

        // ---------- Turn flow helpers called from Game.cs ----------

        /// <summary>
        /// A Spirit's Sprint: "Spirits can Sprint every round" and pay nothing for it. Still
        /// once per turn (the shared SprintedOrRested latch, checked by the caller). No Stamina
        /// cost means no Stamina-track Wound and no Pyrocumulus D6 either, and none of the
        /// Sprint-roll modifiers apply: every one of them is a Wound, Condition, or player-board
        /// card the Spirit no longer has.
        /// </summary>
        private void SprintAsSpirit(InvestigatorState inv)
        {
            int rolled = _rng.RollSprintDie(Db.Config.SprintDieFaces);
            SaveRng();
            inv.SprintedOrRested = true;
            inv.MpRemaining += rolled;
            Log("sprint", $"{inv.DefId}'s Spirit rolled {rolled} MP (free)");
        }

        // ---------- Ability use ----------

        /// <summary>
        /// Use one of the 4 Abilities on this Spirit's card. Up to 2 Abilities per turn; Minors
        /// are free, Majors discard 1 of the Spirit's Major Ability tokens (which never come
        /// back). <paramref name="abilityName"/> accepts the printed name ("Ghost Orbs") or its
        /// slug ("ghost-orbs"); <paramref name="args"/> carries the card's choices (see each
        /// ability's helper for the expected order).
        /// </summary>
        public void UseSpiritAbility(string abilityName, List<string>? args = null)
        {
            args ??= new List<string>();
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireSpirit(inv, "use Spirit Abilities");
            string spiritId = inv.SpiritId!;
            string abilityId = SpiritAbilityId(abilityName);
            bool major = SpiritMajorAbilityIds[spiritId].Contains(abilityId);
            if (!major && !SpiritMinorAbilityIds[spiritId].Contains(abilityId))
            {
                throw new InvalidOperationException(
                    $"The {SpiritName(spiritId)} has no '{abilityName}' Ability.");
            }
            if (inv.SpiritAbilitiesUsedThisTurn >= SpiritAbilitiesPerTurn)
            {
                throw new InvalidOperationException(
                    $"A Spirit may use up to {SpiritAbilitiesPerTurn} Abilities each turn.");
            }
            if (major && inv.SpiritMajorTokens <= 0)
            {
                throw new InvalidOperationException(
                    $"{inv.DefId}'s Spirit has no Major Ability tokens left (they cannot be regained).");
            }
            // Resolve first: an Ability that refuses its arguments costs nothing.
            ResolveSpiritAbility(inv, abilityId, args);
            if (major)
            {
                inv.SpiritMajorTokens -= 1;
            }
            inv.SpiritAbilitiesUsedThisTurn += 1;
            Log("spirit", $"{inv.DefId}'s {SpiritName(spiritId)} used {SpiritAbilityCardNames[abilityId]}" +
                          (major ? $" (Major, {inv.SpiritMajorTokens} token(s) left)" : " (Minor)"));
        }

        /// <summary>Printed name or slug -> ability id.</summary>
        private static string SpiritAbilityId(string abilityName) =>
            abilityName.Trim().ToLowerInvariant().Replace(' ', '-');

        private void ResolveSpiritAbility(InvestigatorState inv, string abilityId, List<string> args)
        {
            switch (abilityId)
            {
                case "ghost-orbs": GhostOrbs(inv, args); break;
                case "cold-spot": ColdSpot(inv); break;
                case "energy-transfer": EnergyTransfer(inv, args); break;
                case "emergency-lights": EmergencyLights(inv); break;
                case "clairvoyance": Clairvoyance(inv, args); break;
                case "ectoplasm": Ectoplasm(inv, args); break;
                case "luring-lights": LuringLights(inv, args); break;
                case "true-darkness": TrueDarkness(inv); break;
                case "whirlwind": Whirlwind(inv); break;
                case "mysterious-passage": MysteriousPassage(inv); break;
                case "push": Push(inv, args); break;
                case "spectral-hand": SpectralHand(inv, args); break;
                default: throw new InvalidOperationException($"Unhandled Spirit Ability '{abilityId}'.");
            }
        }

        // ---------- Apparition ----------

        /// <summary>"Place 1 Ghost Orbs token adjacent to you on the main board. That space
        /// counts as Bright. Remove it at the end of the round." args: [space].</summary>
        private void GhostOrbs(InvestigatorState inv, List<string> args)
        {
            string space = RequireAdjacentArg(inv, args, 0, "Ghost Orbs", "the space for the token");
            PlaceBoardToken(GhostOrbsPrefix + (BoardTokenIds(GhostOrbsPrefix).Count + 1), space);
            State.Overlay.BrightSpaces.Add(space);
            Log("spirit", $"Ghost Orbs light {space} until the end of the round");
            RevealOnBright(new[] { space });
        }

        /// <summary>"The first time the Adversary Moves onto or adjacent to your space, they
        /// must place their Shadow token on the main board on the space they moved on."</summary>
        private void ColdSpot(InvestigatorState inv)
        {
            SetRoundModifier(SpiritColdSpotPrefix + inv.DefId, 1);
            Log("spirit", $"Cold Spot is armed on {inv.Space} for the rest of the round");
            LogTodoOnce("spirit-cold-spot",
                "cold-spot: the forced Shadow token is not placed — AdversaryMoveStep has no card hook to consult " +
                $"the '{SpiritColdSpotPrefix}<spirit>' flag this sets, so the trigger cannot fire on the Adversary's move");
        }

        /// <summary>"Remove 1 Bright or Dim token from the board. Increase all Investigators'
        /// Charge by 1." args: [zone letter or space id of the token].</summary>
        private void EnergyTransfer(InvestigatorState inv, List<string> args)
        {
            if (args.Count < 1 || args[0].Length == 0)
            {
                throw new InvalidOperationException("Energy Transfer: name the Bright or Dim token to remove (a Zone letter or a space id).");
            }
            string target = args[0];
            string removed;
            if (State.Overlay.BrightZones.Remove(target))
            {
                // The zone's lights are out for good either way: EndRound only moves zones it
                // still finds lit onto the Faltering list, so record it here instead.
                State.FalteringZones.Add(target);
                removed = $"the Bright token on zone {target}";
            }
            else if (State.Overlay.DimZones.Remove(target))
            {
                foreach (string tokenId in BoardTokenIds(SpiritDimPrefix).Where(id => State.BoardTokens[id] == target).ToList())
                {
                    RemoveBoardToken(tokenId);
                }
                removed = $"the Dim token on zone {target}";
            }
            else if (State.Overlay.BrightSpaces.Remove(target))
            {
                // Also drop it from whatever put it there (a Flashlight beam, a Ghost Orbs
                // token), so nothing lights it again this round.
                foreach (var placement in State.Flashlights)
                {
                    placement.BrightSpaces.Remove(target);
                }
                foreach (string tokenId in BoardTokenIds(GhostOrbsPrefix).Where(id => State.BoardTokens[id] == target).ToList())
                {
                    RemoveBoardToken(tokenId);
                }
                removed = $"the Bright token on {target}";
            }
            else
            {
                throw new InvalidOperationException($"There is no Bright or Dim token on '{target}'.");
            }
            Log("spirit", $"Energy Transfer removed {removed}");
            foreach (var other in State.Investigators.Where(i => !i.Dead && !i.Escaped && !IsSpirit(i)))
            {
                GainCharge(other, 1);
            }
            Log("spirit", "Energy Transfer gave every Investigator 1 Charge");
        }

        /// <summary>"Place a Dim token on the Zone you are in. Remove it at the end of the round."</summary>
        private void EmergencyLights(InvestigatorState inv)
        {
            string zone = Graph.Space(inv.Space).Zone
                ?? throw new InvalidOperationException($"Emergency Lights needs a Zone; {inv.Space} is outdoors.");
            State.BoardTokens[SpiritDimPrefix + inv.DefId] = zone;
            State.Overlay.DimZones.Add(zone);
            Log("spirit", $"Emergency Lights make zone {zone} Dim until the end of the round");
        }

        // ---------- Phantom ----------

        /// <summary>"Reveal the Point of Interest token from an adjacent point of interest."
        /// args: [poi space] (optional when exactly one is in reach).</summary>
        private void Clairvoyance(InvestigatorState inv, List<string> args)
        {
            var reach = SpiritAdjacentSpaces(inv.Space);
            reach.Add(inv.Space);
            var candidates = State.PoiTokens
                .Where(p => !p.Revealed && !p.Collected && reach.Contains(p.PoiSpace))
                .ToList();
            if (args.Count > 0 && args[0].Length > 0)
            {
                candidates = candidates.Where(p => p.PoiSpace == args[0]).ToList();
            }
            var poi = candidates.OrderBy(p => p.PoiSpace, StringComparer.Ordinal).FirstOrDefault()
                ?? throw new InvalidOperationException("Clairvoyance needs an adjacent Point of Interest with an unrevealed token.");
            poi.Revealed = true;
            Log("reveal", $"POI token at {poi.TokenSpace} (Clairvoyance from {poi.PoiSpace})");
        }

        /// <summary>"Place 2 Ectoplasm tokens adjacent to yourself on the main board. If the
        /// Adversary Moves onto an Ectoplasm token, they cannot use 1 Ability card of your
        /// choice next round. Remove the tokens at the end of the current round."
        /// args: [space, space].</summary>
        private void Ectoplasm(InvestigatorState inv, List<string> args)
        {
            string first = RequireAdjacentArg(inv, args, 0, "Ectoplasm", "the first token space");
            string second = RequireAdjacentArg(inv, args, 1, "Ectoplasm", "the second token space");
            if (first == second)
            {
                throw new InvalidOperationException("Ectoplasm: the 2 tokens go on different spaces.");
            }
            int n = BoardTokenIds(EctoplasmPrefix).Count;
            PlaceBoardToken(EctoplasmPrefix + (n + 1), first);
            PlaceBoardToken(EctoplasmPrefix + (n + 2), second);
            Log("spirit", $"Ectoplasm covers {first} and {second} until the end of the round");
            LogTodoOnce("spirit-ectoplasm",
                "ectoplasm: stepping on a token does not lock out an Adversary Ability — AdversaryMoveStep has no " +
                $"card hook to notice the tokens, and nothing consults an '{SpiritEctoplasmLockoutPrefix}...' flag " +
                "when the Adversary plays an Ability card next round");
        }

        /// <summary>"Move 1 adjacent Investigator up to 3 spaces, treating Dark spaces as Dim.
        /// Map Hazards retain their effects." args: [investigator def id, destination].</summary>
        private void LuringLights(InvestigatorState inv, List<string> args)
        {
            if (args.Count < 2)
            {
                throw new InvalidOperationException("Luring Lights: name the adjacent Investigator and their destination space.");
            }
            var target = Investigator(args[0]);
            string destination = args[1];
            Graph.Space(destination);
            if (target.Dead || target.Escaped || IsSpirit(target))
            {
                throw new InvalidOperationException($"{target.DefId} is not an Investigator on the board.");
            }
            if (!SpiritAdjacentSpaces(inv.Space).Contains(target.Space))
            {
                throw new InvalidOperationException($"{target.DefId} is not adjacent to {inv.DefId}'s Spirit.");
            }
            // "Up to 3 spaces" is a distance, and DistancesFrom already ignores light levels
            // entirely — which is exactly what "treating Dark spaces as Dim" buys the target.
            if (destination != target.Space &&
                !Graph.DistancesFrom(target.Space, LuringLightsRange, State.Overlay).ContainsKey(destination))
            {
                throw new InvalidOperationException(
                    $"Luring Lights moves up to {LuringLightsRange} spaces; '{destination}' is further from {target.Space}.");
            }
            if (State.Investigators.Any(o => o != target && !o.Dead && !o.Escaped && o.Space == destination))
            {
                throw new InvalidOperationException($"'{destination}' is occupied by another Investigator.");
            }
            string from = target.Space;
            target.Space = destination;
            RemoveFlashlightIfForcedMove(target.DefId);
            Log("spirit", $"Luring Lights walked {target.DefId} from {from} to {destination}");
            LogTodoOnce("spirit-luring-lights",
                "luring-lights: \"Map Hazards retain their effects\" is not applied — this is a destination move " +
                "(the same shape as Possessed and Flagellate), so no Window crossing, Water float, or carriage " +
                "rotation along the path is resolved; MoveStep is the only per-step hazard site and it only " +
                "walks the active figure");
        }

        /// <summary>"Spaces in the Zone you are in cost the Adversary 1 extra Footprint this round."</summary>
        private void TrueDarkness(InvestigatorState inv)
        {
            string zone = Graph.Space(inv.Space).Zone
                ?? throw new InvalidOperationException($"True Darkness needs a Zone; {inv.Space} is outdoors.");
            SetRoundModifier(SpiritZoneFootprintSurchargePrefix + zone, 1);
            Log("spirit", $"True Darkness: zone {zone} costs the Adversary 1 extra Footprint this round");
            LogTodoOnce("spirit-true-darkness",
                "true-darkness: the surcharge is not charged — AdversaryMoveStep computes its cost from " +
                "MapGraph.TryStep plus the Bright-space rule only, with no hook for a per-Zone Footprint modifier");
        }

        // ---------- Poltergeist ----------

        /// <summary>"It costs the Adversary 1 extra Footprint the first time they Move onto your
        /// space or an adjacent space this round."</summary>
        private void Whirlwind(InvestigatorState inv)
        {
            SetRoundModifier(SpiritWhirlwindPrefix + inv.DefId, 1);
            Log("spirit", $"Whirlwind is armed around {inv.Space} for the rest of the round");
            LogTodoOnce("spirit-whirlwind",
                "whirlwind: the surcharge is not charged — AdversaryMoveStep has no hook for a per-space Footprint " +
                $"modifier and never consults the '{SpiritWhirlwindPrefix}<spirit>' flag this sets");
        }

        /// <summary>"While you are adjacent to a Map Hazard, Investigators may spend 1 Stamina to
        /// ignore the effects of the Hazard this round."</summary>
        private void MysteriousPassage(InvestigatorState inv)
        {
            SetRoundModifier(SpiritHazardBypassPrefix + inv.DefId, 1);
            Log("spirit", $"Mysterious Passage opens a 1-Stamina Hazard bypass around {inv.Space} this round");
            LogTodoOnce("spirit-mysterious-passage",
                "mysterious-passage: nothing can be bypassed yet — there is no Map Hazard effect layer to suppress " +
                "(the Window crossing in MoveStep/ResolveWindow is the only Hazard the engine resolves, and it has " +
                "no ignore-for-1-Stamina branch)");
        }

        /// <summary>"When adjacent to a Point of Interest, Objective Item, Medical Item, or
        /// Evidence token, you may move that token up to 3 spaces in any direction."
        /// args: [token descriptor, destination], where the descriptor is "poi:&lt;poi space&gt;",
        /// "evidence:&lt;zone&gt;", "medical:&lt;space&gt;", or "objective:&lt;token name&gt;".</summary>
        private void Push(InvestigatorState inv, List<string> args)
        {
            if (args.Count < 2)
            {
                throw new InvalidOperationException(
                    "Push: name the token (\"poi:<poi space>\", \"evidence:<zone>\", \"medical:<space>\", " +
                    "\"objective:<name>\") and the destination space.");
            }
            string descriptor = args[0];
            string destination = args[1];
            Graph.Space(destination);
            int sep = descriptor.IndexOf(':');
            if (sep <= 0)
            {
                throw new InvalidOperationException($"Push: '{descriptor}' is not a token descriptor like \"evidence:S\".");
            }
            string kind = descriptor.Substring(0, sep);
            string key = descriptor.Substring(sep + 1);

            string from;
            Action<string> move;
            switch (kind)
            {
                case "poi":
                {
                    var poi = State.PoiTokens.FirstOrDefault(p => p.PoiSpace == key && !p.Collected)
                        ?? throw new InvalidOperationException($"No uncollected POI token belongs to '{key}'.");
                    from = poi.TokenSpace;
                    move = space => poi.TokenSpace = space;
                    break;
                }
                case "evidence":
                {
                    if (!State.Evidence.TryGetValue(key, out var token))
                    {
                        throw new InvalidOperationException($"No Evidence token for zone '{key}' is on the board.");
                    }
                    if (!token.Revealed)
                    {
                        throw new InvalidOperationException($"Zone {key}'s Evidence token is still hidden on the mini-map.");
                    }
                    from = token.Space;
                    move = space => token.Space = space;
                    break;
                }
                case "medical":
                {
                    int index = State.MedicalItemSpaces.IndexOf(key);
                    if (index < 0)
                    {
                        throw new InvalidOperationException($"No Medical Item token on '{key}'.");
                    }
                    from = key;
                    move = space => State.MedicalItemSpaces[index] = space;
                    break;
                }
                case "objective":
                {
                    if (State.Objective.TokenCarriers.ContainsKey(key))
                    {
                        throw new InvalidOperationException($"The {key} token is being carried, not on the board.");
                    }
                    if (!State.Objective.Tokens.TryGetValue(key, out string space))
                    {
                        throw new InvalidOperationException($"No objective token named '{key}' is on the board.");
                    }
                    from = space;
                    move = target => State.Objective.Tokens[key] = target;
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Push: '{kind}' is not a token kind (poi, evidence, medical, objective).");
            }

            if (!SpiritAdjacentOrSame(inv.Space, from))
            {
                throw new InvalidOperationException($"Push needs the token to be on or adjacent to the Spirit; it is on {from}.");
            }
            if (from != destination && !Graph.DistancesFrom(from, SpiritPushRange, State.Overlay).ContainsKey(destination))
            {
                throw new InvalidOperationException(
                    $"Push moves a token up to {SpiritPushRange} spaces; '{destination}' is further from {from}.");
            }
            move(destination);
            Log("spirit", $"Push slid the {descriptor} token from {from} to {destination}");
            if (State.Overlay.BrightSpaces.Contains(destination) ||
                Graph.EffectiveLight(destination, State.Overlay) == LightLevel.Bright)
            {
                RevealOnBright(new[] { destination });
            }
        }

        /// <summary>"Take 1 Investigator's Flashlight from the board, move it up to 3 spaces, and
        /// reorient it as you see fit." args: [flashlight owner def id, destination, angle in
        /// radians].</summary>
        private void SpectralHand(InvestigatorState inv, List<string> args)
        {
            if (args.Count < 3)
            {
                throw new InvalidOperationException(
                    "Spectral Hand: name the Flashlight's Investigator, the destination space, and the new angle in radians.");
            }
            var placement = State.Flashlights.FirstOrDefault(f => f.InvestigatorId == args[0])
                ?? throw new InvalidOperationException($"{args[0]} has no Flashlight on the board.");
            string destination = args[1];
            Graph.Space(destination);
            if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
            {
                throw new InvalidOperationException($"Spectral Hand: '{args[2]}' is not an angle in radians.");
            }
            if (destination != placement.Space &&
                !Graph.DistancesFrom(placement.Space, SpiritPushRange, State.Overlay).ContainsKey(destination))
            {
                throw new InvalidOperationException(
                    $"Spectral Hand moves a Flashlight up to {SpiritPushRange} spaces; '{destination}' is further from {placement.Space}.");
            }
            // Beam geometry only; the TrimFlashlightBright cards (Misty, Hazy, Tunnel Vision)
            // all measure from the Investigator's own space, and this Flashlight is no longer
            // standing there.
            var bright = _beam.ComputeBright(Graph, destination, angle, _losBlocker);
            var newBright = bright.OrderBy(s => s, StringComparer.Ordinal).ToList();
            foreach (string space in placement.BrightSpaces)
            {
                bool litElsewhere = State.Flashlights.Any(f => f != placement && f.BrightSpaces.Contains(space));
                if (!litElsewhere && !HasBoardTokenAt(GhostOrbsPrefix, space))
                {
                    State.Overlay.BrightSpaces.Remove(space);
                }
            }
            string was = placement.Space;
            placement.Space = destination;
            placement.AngleRadians = angle;
            placement.BrightSpaces = newBright;
            State.Overlay.BrightSpaces.UnionWith(bright);
            Log("spirit", $"Spectral Hand moved {placement.InvestigatorId}'s Flashlight from {was} to {destination} ({bright.Count} spaces lit)");
            RevealOnBright(bright);
        }

        // ---------- Shared Spirit geometry ----------

        /// <summary>
        /// Spaces a Spirit counts as adjacent to: printed adjacency minus the Adversary-only
        /// dashed links, and with everything a Spirit floats through (Locked/Damaged Doors,
        /// closed Mirror Maze doors, Windows) treated as passable. Secret Passages count too.
        /// </summary>
        private List<string> SpiritAdjacentSpaces(string from)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in Graph.Def.Edges)
            {
                string? other = edge.A == from ? edge.B : (edge.B == from ? edge.A : null);
                if (other != null && Graph.TryStep(FigureKind.Spirit, from, other, State.Overlay) != null)
                {
                    result.Add(other);
                }
            }
            foreach (string key in State.Overlay.SecretPassages)
            {
                int sep = key.IndexOf('|');
                string a = key.Substring(0, sep);
                string b = key.Substring(sep + 1);
                if (a == from)
                {
                    result.Add(b);
                }
                else if (b == from)
                {
                    result.Add(a);
                }
            }
            return result.OrderBy(s => s, StringComparer.Ordinal).ToList();
        }

        private bool SpiritAdjacentOrSame(string from, string to) =>
            from == to || SpiritAdjacentSpaces(from).Contains(to);

        private string RequireAdjacentArg(InvestigatorState inv, List<string> args, int index, string ability, string what)
        {
            if (args.Count <= index || args[index].Length == 0)
            {
                throw new InvalidOperationException($"{ability}: name {what}.");
            }
            string space = args[index];
            Graph.Space(space);
            if (!SpiritAdjacentSpaces(inv.Space).Contains(space))
            {
                throw new InvalidOperationException($"{ability}: '{space}' is not adjacent to {inv.DefId}'s Spirit on {inv.Space}.");
            }
            return space;
        }

        // ---------- Per-action gating (fanned out from CollectActionBlockers) ----------

        partial void SpiritsCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers)
        {
            if (!IsSpirit(inv))
            {
                return;
            }
            switch (actionKey)
            {
                case ActionRest:
                    blockers.Add("Spirits have no Stamina track, so there is nothing to Rest for");
                    break;
                case ActionCharge:
                    blockers.Add("Spirits have no Charge track to Charge");
                    break;
                case ActionPlaceFlashlight:
                    blockers.Add("Spirits have no Flashlight to place");
                    break;
            }
        }

        // ---------- Death (fanned out from OnInvestigatorDeath) ----------

        /// <summary>
        /// Announce the Spirit offer. This runs from <see cref="GainWound"/> after the kill is
        /// counted but before the win condition is checked, so the same comparison the caller is
        /// about to make decides whether there is anything to offer — which is exactly why a
        /// Butcher game never has Spirits (KillsToWin 1).
        /// </summary>
        partial void SpiritsOnInvestigatorDeath(InvestigatorState inv)
        {
            if (State.Adversary.Kills >= State.Adversary.KillsToWin)
            {
                return; // the Adversary's win condition is satisfied: no Spirit card is offered
            }
            var unused = UnusedSpiritIds();
            Log("spirit", unused.Count == 0
                ? $"{inv.DefId} died; every Spirit card is already taken"
                : $"{inv.DefId} died; their player may take a Spirit: {string.Join(", ", unused)}");
        }

        // ---------- Round end ----------

        partial void SpiritsOnRoundEnd()
        {
            foreach (string tokenId in BoardTokenIds(GhostOrbsPrefix))
            {
                State.Overlay.BrightSpaces.Remove(State.BoardTokens[tokenId]);
            }
            RemoveBoardTokens(GhostOrbsPrefix);
            RemoveBoardTokens(EctoplasmPrefix);
            foreach (string tokenId in BoardTokenIds(SpiritDimPrefix))
            {
                State.Overlay.DimZones.Remove(State.BoardTokens[tokenId]);
            }
            RemoveBoardTokens(SpiritDimPrefix);
        }
    }
}
