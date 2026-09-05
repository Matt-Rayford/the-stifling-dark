using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// The 10 base Investigators' printed Minor and Major Abilities
    /// (game-data/investigators.json). Implements the Abilities* sub-hooks declared in
    /// Game.EffectDispatch.cs plus the two public entry points below.
    ///
    /// The API is deliberately two methods, mirroring <see cref="UseSpiritAbility"/>:
    ///
    ///   <see cref="UseMinorAbility"/>(invId?, args?)  - free, any number of times per the text.
    ///   <see cref="UseMajorAbility"/>(invId?, args?)  - discards 1 Major Ability token.
    ///
    /// Both dispatch on the named Investigator's def id (the active Investigator when
    /// <c>invId</c> is null). <c>invId</c> exists because two printed Abilities fire outside
    /// their owner's turn: Mada's Coin reacts to an Adversary Ability, and Mitchell's Sweep
    /// happens after Placing the Flashlight has already ended his turn. The player board's own
    /// tagging agrees — Minor Abilities are tagged "Turn" and Major Abilities "Round".
    ///
    /// Passive and triggered Minors are not routed through <see cref="UseMinorAbility"/> at
    /// all; they live on the sub-hooks (Lucy's Sprint die, Ibraheem's Footprint floor, Dylan's
    /// Dark discount, Asher's ignored Wound slot, Marci's Stamina track). Calling
    /// <see cref="UseMinorAbility"/> for one of those says so rather than pretending to do
    /// something.
    ///
    /// Tokens (Brielle's 6 Cans, Lucy's 2 Barricades, Dylan's Escape Artist, Mada's Coin) are
    /// keyed by the *acting* Investigator, not by the Ability's owner, so a borrowed Ability
    /// (Two-way Radio, Blood Chalice - see <see cref="UseBorrowedMinorAbility"/> /
    /// <see cref="UseBorrowedMajorAbility"/>) draws on the borrower's own supply and can never
    /// disturb the real owner's.
    ///
    /// Investigators who have become Spirits lose all of this: the player board is gone, and
    /// with it the printed Abilities and the Major Ability token (see
    /// <see cref="AdoptSpirit"/>). Spirit Abilities are an entirely separate system in
    /// Game.Spirits.cs and are never reachable from here.
    /// </summary>
    public sealed partial class Game
    {
        // ---------- Rules constants (game-data/investigators.json ability texts) ----------

        /// <summary>Brielle starts the game with 6 Can tokens.</summary>
        public const int BrielleCanTokens = 6;

        /// <summary>"...within 3 spaces of yourself."</summary>
        public const int BrielleCanRange = 3;

        /// <summary>Lucy Belle's Major places 2 Barricade tokens at once.</summary>
        public const int LucyBarricadeTokens = 2;

        /// <summary>A Barricade "works like a Door": Break Door Damages it, a second Break
        /// Destroys it and removes the token.</summary>
        public const int BarricadeBreaksToDestroy = 2;

        /// <summary>"You may treat up to 3 Dark spaces as Dim when you Move or Sprint onto
        /// them." Read as an allowance per turn (the board tags Minor Abilities "Turn").</summary>
        public const int DylanDarkSpacesPerTurn = 3;

        /// <summary>"Your Footprint for the Move Action can never drop below 4."</summary>
        public const int IbraheemMoveFloor = 4;

        /// <summary>"You may Trade with an Investigator that is up to 5 spaces away."</summary>
        public const int IbraheemTradeRange = 5;

        /// <summary>Mada's Coin reacts for Investigators "within 4 spaces of you".</summary>
        public const int MadaCoinRange = 4;

        /// <summary>"Lose 5 Stamina in order to roll the Sprint die 3 times during this turn."</summary>
        public const int MadaMajorStaminaCost = 5;

        /// <summary>See <see cref="MadaMajorStaminaCost"/>.</summary>
        public const int MadaMajorSprintRolls = 3;

        /// <summary>"Choose up to 2 other Investigators within 8 spaces of you..."</summary>
        public const int MarciMajorTargets = 2;

        /// <summary>See <see cref="MarciMajorTargets"/>.</summary>
        public const int MarciMajorRange = 8;

        /// <summary>"...you may move each of them up to 2 spaces in any direction."</summary>
        public const int MarciMajorMove = 2;

        /// <summary>"If the Adversary is within 2 spaces of that Investigator..."</summary>
        public const int MitchellMajorRange = 2;

        /// <summary>Scout finds Point of Interest tokens "within 2 spaces".</summary>
        public const int VincentScoutRange = 2;

        /// <summary>Aira's Major names 2 spaces anywhere on the board.</summary>
        public const int AiraMajorSpaces = 2;

        // ---------- Board-token instance id prefixes (see Game.PlaceBoardToken) ----------
        //
        // Every prefix embeds the acting Investigator's def id so a borrowed Ability uses the
        // borrower's own token supply. The Can prefixes are deliberately disjoint strings
        // rather than "can-" / "can-noise-": BoardTokenIds matches on StartsWith, so a shared
        // stem would make the unflipped-token query also match the flipped ones.

        private static string CanPrefix(string actorId) => "can:" + actorId + ":";

        private static string NoiseCanPrefix(string actorId) => "noise:" + actorId + ":";

        private static string BarricadePrefix(string actorId) => "barricade:" + actorId + ":";

        private static string EscapeArtistTokenId(string actorId) => "escape-artist:" + actorId;

        // ---------- Adversary.Counters keys (serializable, and must outlive a round) ----------

        /// <summary>Prefix + Barricade token instance id -> Break Door hits taken so far.</summary>
        public const string BarricadeDamagePrefix = "barricade-damage:";

        /// <summary>Prefix + Investigator def id -> the round their Escape Artist token was
        /// placed. The token may be used "this round or the next round".</summary>
        public const string EscapeArtistRoundPrefix = "escape-artist-round:";

        /// <summary>Prefix + Investigator def id -> 1 while Mada holds his Coin token
        /// ("You may only have 1 Coin token at a time").</summary>
        public const string CoinTokenPrefix = "coin:";

        /// <summary>Prefix + Investigator def id -> the round a face-up Fear Wound landed. The
        /// forced Major Ability is armed at their next turn start (see
        /// <see cref="AbilitiesOnTurnStart"/>).</summary>
        public const string FearForcedMajorPrefix = "fear-forced-major:";

        // ---------- RoundModifiers keys owned by this file ----------

        /// <summary>Prefix + Investigator def id: Aira armed her Minor, so the Involved Action
        /// does not end her turn and no second Involved Action is allowed that turn.</summary>
        public const string AiraInvolvedArmedPrefix = "aira-involved-armed:";

        /// <summary>Prefix + Investigator def id: ignore the effects of every face-up Wound
        /// they hold for the rest of this turn (Asher's Major).</summary>
        public const string IgnoreAllWoundsPrefix = "ignore-all-wounds:";

        /// <summary>Prefix + Investigator def id: Dark spaces already discounted to Dim this
        /// turn (Dylan's Minor, capped at <see cref="DylanDarkSpacesPerTurn"/>).</summary>
        public const string DarkAsDimUsedPrefix = "dark-as-dim-used:";

        /// <summary>Prefix + Investigator def id: Footprint a card took away this turn that
        /// Ibraheem's floor refunded and his Sprint roll must pay instead.</summary>
        public const string SprintPenaltyTransferPrefix = "sprint-penalty-transfer:";

        /// <summary>Prefix + Investigator def id: how far they may Trade this round when it is
        /// further than adjacent (Ibraheem's Major). Read by Game.RequireAdjacentForTrade.</summary>
        public const string TradeRangePrefix = "trade-range:";

        /// <summary>Prefix + Investigator def id: their Flashlight has already been Swept this
        /// round (Mitchell's Minor is "once per Flashlight", and a Flashlight lasts a round).</summary>
        public const string SweepUsedPrefix = "sweep-used:";

        /// <summary>Every Investigator ignores this round's Event card (Brielle's Major). Read
        /// by <see cref="EventEffectsIgnored"/>, which gates both the Events* sub-hooks and the
        /// Event-owned modifier reads in Game.cs.</summary>
        public const string EventIgnoredKey = "event-ignored";

        /// <summary>Prefix + Investigator def id: a face-up Fear Wound forces them to use their
        /// Major Ability before this turn may end.</summary>
        public const string ForcedMajorAbilityPrefix = "forced-major-ability:";

        // ---------- The public entry points ----------

        /// <summary>
        /// One use of an Investigator's printed Minor Ability. Free and repeatable, subject to
        /// each text's own limits. <paramref name="invId"/> defaults to the active Investigator;
        /// naming one explicitly is how the two out-of-turn Minors are used (Mada's Coin,
        /// Mitchell's Sweep). Throws when that Investigator's Minor is passive rather than
        /// activated, and says which hook carries it instead.
        /// </summary>
        public void UseMinorAbility(string? invId = null, List<string>? args = null)
        {
            var use = BeginAbilityUse(invId, args);
            ResolveMinorAbility(use);
            Log("ability", $"{use.Actor.DefId} used their Minor Ability");
        }

        /// <summary>
        /// One use of an Investigator's printed Major Ability. Discards 1 Major Ability token
        /// (max 1 held; they are never Traded and only the Evidence economy hands one back), so
        /// a second use in the same game without regaining one is refused. The Ability resolves
        /// first: one that refuses its arguments costs nothing.
        ///
        /// Dylan's Escape Artist is the single two-part Major: while his token is on the board
        /// the call resolves the free "place your figure back on the token" half instead, and
        /// spends nothing — the token was paid for when it was placed.
        /// </summary>
        public void UseMajorAbility(string? invId = null, List<string>? args = null)
        {
            var use = BeginAbilityUse(invId, args);
            var inv = use.Actor;
            if (inv.DefId == "dylan" && BoardTokenSpace(EscapeArtistTokenId(inv.DefId)) != null)
            {
                EscapeArtistReturn(inv);
                // The free return is the 2nd half of a Major he already paid for, but it is
                // still "using your Major Ability" for Fear's purposes (see
                // CanResolveMajorAbility): both branches of this method must release the
                // compulsion, not just the token-spending one below.
                ClearRoundModifier(ForcedMajorAbilityPrefix + inv.DefId);
                return;
            }
            if (inv.MajorAbilityTokens < 1)
            {
                throw new InvalidOperationException(
                    $"{inv.DefId} has no Major Ability token to discard.");
            }
            ResolveMajorAbility(use);
            inv.MajorAbilityTokens -= 1;
            ClearRoundModifier(ForcedMajorAbilityPrefix + inv.DefId);
            Log("ability", $"{inv.DefId} discarded a Major Ability token to use their Major Ability");
        }

        /// <summary>
        /// Two-way Radio: "You may use the Minor Ability of an Investigator that is not in
        /// play. If the Ability uses tokens, you may use 1 of them but cannot keep the rest."
        /// <paramref name="args"/>[0] is that Investigator's def id; the rest are the Ability's
        /// own arguments. Called from Game.ItemEffects.cs.
        /// </summary>
        private void UseBorrowedMinorAbility(InvestigatorState actor, List<string> args)
        {
            string ownerId = RequireOutOfPlayInvestigator(args, "Two-way Radio");
            var use = new AbilityUse
            {
                Actor = actor,
                OwnerId = ownerId,
                Args = args.Skip(1).ToList(),
                Borrowed = true,
                TokenLimit = 1,
            };
            ResolveMinorAbility(use);
            Log("ability", $"{actor.DefId} borrowed {Db.Investigator(ownerId).Name}'s Minor Ability over the Two-way Radio");
        }

        /// <summary>
        /// Blood Chalice: "Gain a face-up Wound to use any other Investigator's Major Ability
        /// without using a Major Ability token ... You may choose Investigators that are not in
        /// play." <paramref name="args"/>[0] is that Investigator's def id; the rest are the
        /// Ability's own arguments. No token is spent. Called from Game.ItemEffects.cs.
        /// </summary>
        private void UseBorrowedMajorAbility(InvestigatorState actor, List<string> args)
        {
            if (args.Count < 1 || args[0].Length == 0)
            {
                throw new InvalidOperationException("Blood Chalice: name the Investigator whose Major Ability you are using.");
            }
            string ownerId = args[0];
            var def = Db.Investigators.FirstOrDefault(i => i.Id == ownerId && i.Set == "base")
                ?? throw new InvalidOperationException($"'{ownerId}' is not one of the 10 base Investigators.");
            if (ownerId == actor.DefId)
            {
                throw new InvalidOperationException("Blood Chalice uses *another* Investigator's Major Ability.");
            }
            var use = new AbilityUse
            {
                Actor = actor,
                OwnerId = ownerId,
                Args = args.Skip(1).ToList(),
                Borrowed = true,
            };
            ResolveMajorAbility(use);
            Log("ability", $"{actor.DefId} used {def.Name}'s Major Ability through the Blood Chalice (no token spent)");
        }

        private string RequireOutOfPlayInvestigator(List<string> args, string source)
        {
            if (args.Count < 1 || args[0].Length == 0)
            {
                throw new InvalidOperationException($"{source}: name the Investigator whose Minor Ability you are using.");
            }
            string ownerId = args[0];
            if (Db.Investigators.All(i => i.Id != ownerId || i.Set != "base"))
            {
                throw new InvalidOperationException($"'{ownerId}' is not one of the 10 base Investigators.");
            }
            if (State.Investigators.Any(i => i.DefId == ownerId))
            {
                throw new InvalidOperationException($"{source} only reaches an Investigator that is not in play; {ownerId} is.");
            }
            return ownerId;
        }

        /// <summary>One in-flight Ability use: who is acting, whose printed text is being used,
        /// and the limits a borrowed use imposes.</summary>
        private sealed class AbilityUse
        {
            public InvestigatorState Actor { get; set; } = new InvestigatorState();
            public string OwnerId { get; set; } = "";
            public List<string> Args { get; set; } = new List<string>();
            /// <summary>True when the text is being read off someone else's player board
            /// (Two-way Radio, Blood Chalice).</summary>
            public bool Borrowed { get; set; }
            /// <summary>Ability tokens this use may place ("you may use 1 of them but cannot
            /// keep the rest").</summary>
            public int TokenLimit { get; set; } = int.MaxValue;
        }

        /// <summary>Shared validation for both entry points: resolve the acting Investigator,
        /// refuse a Spirit (no player board, so no printed Abilities), and run the per-action
        /// gate that carries the Disoriented Wound.</summary>
        private AbilityUse BeginAbilityUse(string? invId, List<string>? args)
        {
            if (State.Phase == GamePhase.GameOver)
            {
                throw new InvalidOperationException("The game is over.");
            }
            var inv = invId == null ? ActiveInv() : Investigator(invId);
            // Spirit first: a Spirit is by definition dead, and losing the player board (and
            // with it these Abilities) is the more precise reason to refuse.
            RequireNotSpirit(inv, "use Investigator Abilities (the player board is gone)");
            if (inv.Dead || inv.Escaped)
            {
                throw new InvalidOperationException($"{inv.DefId} is no longer on the board.");
            }
            if (State.ActiveInvestigator == inv.DefId)
            {
                RequireNoPendingWindow();
            }
            RequireActionAllowed(inv, ActionUseAbility);
            return new AbilityUse
            {
                Actor = inv,
                OwnerId = inv.DefId,
                Args = args ?? new List<string>(),
            };
        }

        // ---------- Minor Ability dispatch ----------

        private void ResolveMinorAbility(AbilityUse use)
        {
            switch (use.OwnerId)
            {
                case "aira": AiraArmInvolvedAction(use.Actor); break;
                case "brielle": BriellePlaceCans(use); break;
                case "mada": MadaFlipCoin(use); break;
                case "mitchell": MitchellSweep(use); break;
                case "vincent": VincentScout(use); break;

                case "asher":
                    throw new InvalidOperationException(
                        "Asher's Minor Ability is passive: the face-up Wound in his first slot is ignored " +
                        "wherever a Wound's effects are read (see IgnoresFaceUpWound).");
                case "dylan":
                    throw new InvalidOperationException(
                        "Dylan's Minor Ability is passive: up to 3 Dark spaces a turn are charged as Dim " +
                        "automatically by the Move-cost hook (AbilitiesAdjustMoveCost).");
                case "ibraheem":
                    throw new InvalidOperationException(
                        "Ibraheem's Minor Ability is passive: his Move Footprint is floored at 4 at the start " +
                        "of every turn and the shortfall is transferred to his Sprint roll.");
                case "lucy-belle":
                    throw new InvalidOperationException(
                        "Lucy Belle's Minor Ability is passive: a rolled 2 on the Sprint die is counted as 3 " +
                        "by the Sprint-roll hook.");
                case "marci":
                    throw new InvalidOperationException(
                        "Marci Jo's Minor Ability is printed on her Stamina track (only space 0 bears the " +
                        "Wound icon), so LoseStamina already applies it.");

                default:
                    throw new InvalidOperationException($"'{use.OwnerId}' has no implemented Minor Ability.");
            }
        }

        // ---------- Major Ability dispatch ----------

        private void ResolveMajorAbility(AbilityUse use)
        {
            switch (use.OwnerId)
            {
                case "aira": AiraSweepTwoSpaces(use); break;
                case "asher": AsherPushThrough(use); break;
                case "brielle": BrielleIgnoreEvent(use); break;
                case "dylan": DylanPlaceEscapeArtist(use); break;
                case "ibraheem": IbraheemExtendTradeRange(use); break;
                case "lucy-belle": LucyPlaceBarricades(use); break;
                case "mada": MadaTripleSprint(use); break;
                case "marci": MarciMoveInvestigators(use); break;
                case "mitchell": MitchellFindAdversary(use); break;
                case "vincent": VincentDrawCursedItem(use); break;
                default:
                    throw new InvalidOperationException($"'{use.OwnerId}' has no implemented Major Ability.");
            }
        }

        // ---------- Aira Willson ----------

        /// <summary>
        /// Minor: "You may take the Involved Action Final Action at any point during your turn
        /// (without ending your turn), but you may not perform any other Involved Action during
        /// that turn." Arms exactly the conversion Spare Tools already uses (Game.EndTurn reads
        /// <see cref="InvolvedAsInteractPrefix"/> and latches
        /// <see cref="InvolvedActionUsedPrefix"/> in its place), plus a marker so the
        /// "no other Involved Action" half can be enforced by the per-action gate.
        /// The Rest clause needs nothing: EndTurn already gains the Stamina whenever the Final
        /// Action on record is not an Involved Action, and the conversion clears it.
        /// </summary>
        private void AiraArmInvolvedAction(InvestigatorState inv)
        {
            RequireOwnTurn(inv, "Aira's Minor Ability arms her own turn");
            if (inv.FinalAction != FinalActionKind.None)
            {
                throw new InvalidOperationException("A Final Action was already taken this turn.");
            }
            if (HasRoundModifier(AiraInvolvedArmedPrefix + inv.DefId))
            {
                throw new InvalidOperationException($"{inv.DefId} has already armed the free Involved Action this turn.");
            }
            SetRoundModifier(AiraInvolvedArmedPrefix + inv.DefId, 1);
            SetRoundModifier(InvolvedAsInteractPrefix + inv.DefId, 1);
            Log("ability", $"{inv.DefId} may take one Involved Action this turn without ending their turn");
        }

        /// <summary>
        /// Major: "Choose 2 spaces anywhere on the board. If the Adversary is on or adjacent to
        /// those spaces they must Reveal themself. This is an immediate and one-time effect
        /// that does not make the spaces Bright." args: [space, space].
        /// </summary>
        private void AiraSweepTwoSpaces(AbilityUse use)
        {
            if (use.Args.Count < AiraMajorSpaces)
            {
                throw new InvalidOperationException($"Aira's Major Ability names {AiraMajorSpaces} spaces anywhere on the board.");
            }
            var chosen = use.Args.Take(AiraMajorSpaces).ToList();
            foreach (string space in chosen)
            {
                Graph.Space(space); // validates before anything happens
            }
            if (chosen[0] == chosen[1])
            {
                throw new InvalidOperationException("Aira's Major Ability names 2 different spaces.");
            }
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (string space in chosen)
            {
                covered.UnionWith(Graph.DistancesFrom(space, 1, State.Overlay).Keys);
            }
            Log("ability", $"{use.Actor.DefId} searches {chosen[0]} and {chosen[1]} (no light is placed)");
            RevealAdversaryFiguresOn(covered, $"{use.Actor.DefId} searched {chosen[0]} and {chosen[1]}");
        }

        // ---------- Asher Palacios ----------

        /// <summary>
        /// Major: "Gain 1 Stamina. During this turn, gain 2 Footprint and ignore the effects of
        /// any face-up Wound you have."
        /// </summary>
        private void AsherPushThrough(AbilityUse use)
        {
            var inv = use.Actor;
            RequireOwnTurn(inv, "Asher's Major Ability lasts \"during this turn\"");
            GainStamina(inv, 1);
            inv.MpRemaining += 2;
            SetRoundModifier(IgnoreAllWoundsPrefix + inv.DefId, 1);
            Log("ability", $"{inv.DefId} gains 1 Stamina and 2 MP and ignores every face-up Wound this turn");
        }

        // ---------- Brielle Easton ----------

        /// <summary>
        /// Minor: "You start the game with 6 Can tokens. Place any number of Can tokens on the
        /// main board within 3 spaces of yourself. When the Adversary moves onto a Can token,
        /// they flip it to the Noise side." args: one space id per token to place.
        /// </summary>
        private void BriellePlaceCans(AbilityUse use)
        {
            var inv = use.Actor;
            if (use.Args.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Cans: name the space(s) for the token(s), each within {BrielleCanRange} spaces of yourself.");
            }
            int onBoard = BoardTokenIds(CanPrefix(inv.DefId)).Count + BoardTokenIds(NoiseCanPrefix(inv.DefId)).Count;
            int allowed = Math.Min(BrielleCanTokens - onBoard, use.TokenLimit);
            if (allowed <= 0)
            {
                throw new InvalidOperationException(use.TokenLimit == int.MaxValue
                    ? $"All {BrielleCanTokens} Can tokens are already on the board."
                    : "A borrowed Ability may only use 1 of its tokens.");
            }
            if (use.Args.Count > allowed)
            {
                throw new InvalidOperationException($"Only {allowed} Can token(s) may be placed; {use.Args.Count} spaces were named.");
            }
            // Validate everything before placing anything: a refused placement changes nothing.
            var within = Graph.DistancesFrom(inv.Space, BrielleCanRange, State.Overlay);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string space in use.Args)
            {
                Graph.Space(space);
                if (!within.ContainsKey(space))
                {
                    throw new InvalidOperationException($"Cans: '{space}' is more than {BrielleCanRange} spaces from {inv.Space}.");
                }
                if (!seen.Add(space) || CanTokenAt(space) != null)
                {
                    throw new InvalidOperationException($"Cans: '{space}' already has a Can token.");
                }
            }
            foreach (string space in use.Args)
            {
                onBoard += 1;
                PlaceBoardToken(CanPrefix(inv.DefId) + onBoard.ToString(CultureInfo.InvariantCulture), space);
            }
            Log("ability", $"{inv.DefId} set out Can tokens on {string.Join(", ", use.Args)}");
        }

        /// <summary>The unflipped Can token instance id on a space, or null.</summary>
        private string? CanTokenAt(string spaceId) =>
            State.BoardTokens.Where(kv => kv.Value == spaceId &&
                                          kv.Key.StartsWith("can:", StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .FirstOrDefault();

        /// <summary>
        /// Major: "All Investigators may ignore the effects of an Event this round." Sets the
        /// flag <see cref="EventEffectsIgnored"/> reads, which gates every Events* sub-hook and
        /// every Event-owned modifier the core actions consult.
        /// </summary>
        private void BrielleIgnoreEvent(AbilityUse use)
        {
            if (State.CurrentEvent == null)
            {
                throw new InvalidOperationException("There is no Event card in play to ignore.");
            }
            if (HasRoundModifier(EventIgnoredKey))
            {
                throw new InvalidOperationException($"{State.CurrentEvent} is already being ignored this round.");
            }
            SetRoundModifier(EventIgnoredKey, 1);
            Log("ability", $"{use.Actor.DefId} lets every Investigator ignore {State.CurrentEvent} this round");
            if (State.Adversary.DefId == "butcher" && HasRoundModifier(FlashlightChargeSurchargeKey))
            {
                LogTodoOnce("brielle-major-decay",
                    "brielle major: the Butcher's Decay writes the same '" + FlashlightChargeSurchargeKey +
                    "' modifier the Event cards do, so suppressing the Event suppresses Decay's Charge " +
                    "surcharge too; separating them needs per-source modifier attribution");
            }
        }

        /// <summary>True while Brielle's Major Ability is suppressing this round's Event card.</summary>
        private bool EventEffectsIgnored() => State.RoundModifiers.ContainsKey(EventIgnoredKey);

        /// <summary>An Event-owned round modifier, or 0 while the Event is being ignored.</summary>
        private int EventRoundModifier(string key) => EventEffectsIgnored() ? 0 : RoundModifier(key);

        /// <summary>Whether an Event-owned round modifier is in force (never while the Event is
        /// being ignored).</summary>
        private bool HasEventRoundModifier(string key) => !EventEffectsIgnored() && HasRoundModifier(key);

        // ---------- Dylan J. Lee ----------

        /// <summary>
        /// Major, part 1: "Place an Escape Artist token on an adjacent space on the main
        /// board." args: [space].
        /// </summary>
        private void DylanPlaceEscapeArtist(AbilityUse use)
        {
            var inv = use.Actor;
            if (use.Args.Count < 1 || use.Args[0].Length == 0)
            {
                throw new InvalidOperationException("Escape Artist: name the adjacent space for the token.");
            }
            string space = use.Args[0];
            Graph.Space(space);
            if (!Graph.DistancesFrom(inv.Space, 1, State.Overlay).ContainsKey(space) || space == inv.Space)
            {
                throw new InvalidOperationException($"Escape Artist: '{space}' is not adjacent to {inv.Space}.");
            }
            PlaceBoardToken(EscapeArtistTokenId(inv.DefId), space);
            State.Adversary.Counters[EscapeArtistRoundPrefix + inv.DefId] = State.Round;
            Log("ability", $"{inv.DefId} dropped an Escape Artist token on {space} (usable this round and next)");
        }

        /// <summary>
        /// Major, part 2: "You may place your figure back on the Escape Artist token for free at
        /// any point this round or the next round. Remove the Escape Artist token when it is
        /// used or at the end of your next turn." Free — the token was paid for on placement.
        /// </summary>
        private void EscapeArtistReturn(InvestigatorState inv)
        {
            string tokenId = EscapeArtistTokenId(inv.DefId);
            string space = BoardTokenSpace(tokenId)!;
            int placedRound = State.Adversary.Counters.TryGetValue(EscapeArtistRoundPrefix + inv.DefId, out int r)
                ? r
                : State.Round;
            if (State.Round > placedRound + 1)
            {
                // Belt and braces: AbilitiesOnTurnEnd normally removes it first.
                RemoveEscapeArtistToken(inv);
                throw new InvalidOperationException("The Escape Artist token's window (this round or the next) has passed.");
            }
            if (State.Investigators.Any(o => o != inv && !o.Dead && !o.Escaped && o.Space == space))
            {
                throw new InvalidOperationException($"'{space}' is occupied by another Investigator.");
            }
            string from = inv.Space;
            inv.Space = space;
            RemoveEscapeArtistToken(inv);
            RemoveFlashlightIfForcedMove(inv.DefId);
            ApplyCarriageRotation(inv);
            Log("ability", $"{inv.DefId} vanished from {from} and reappeared on their Escape Artist token at {space}");
        }

        private void RemoveEscapeArtistToken(InvestigatorState inv)
        {
            RemoveBoardToken(EscapeArtistTokenId(inv.DefId));
            State.Adversary.Counters.Remove(EscapeArtistRoundPrefix + inv.DefId);
        }

        // ---------- Ibraheem Hess ----------

        /// <summary>Major: "You may Trade with an Investigator that is up to 5 spaces away."
        /// Read by Game.RequireAdjacentForTrade via <see cref="ExtendedTradeRange"/>.</summary>
        private void IbraheemExtendTradeRange(AbilityUse use)
        {
            SetRoundModifier(TradeRangePrefix + use.Actor.DefId, IbraheemTradeRange);
            Log("ability", $"{use.Actor.DefId} may Trade at up to {IbraheemTradeRange} spaces this round");
        }

        /// <summary>How far these 2 Investigators may Trade apart right now (1 = the printed
        /// adjacency rule). Either side of the Trade may be the one with the extended range.</summary>
        private int ExtendedTradeRange(InvestigatorState a, InvestigatorState b) =>
            Math.Max(Math.Max(RoundModifier(TradeRangePrefix + a.DefId), RoundModifier(TradeRangePrefix + b.DefId)), 1);

        // ---------- Lucy Belle ----------

        /// <summary>
        /// Major: "Place 2 Barricade tokens adjacent to yourself on the main board. They do not
        /// need to be adjacent to each other but must be placed at the same time. They work
        /// like Doors for the Adversary, except when they are Destroyed the Barricade token is
        /// removed. Investigators may Move through them and they do not block line of sight."
        /// args: [space, space].
        ///
        /// "Like a Door for the Adversary" is modeled as a per-space Adversary-only movement
        /// blocker (BoardOverlay.AdversaryBarriers, consulted by MapGraph.TryStep) plus a Break
        /// Door path: the first Break Damages the Barricade, the second Destroys it and removes
        /// the token. Investigator movement, line of sight, and the shared "within X spaces"
        /// distance metric are all deliberately untouched, exactly as the card says.
        /// </summary>
        private void LucyPlaceBarricades(AbilityUse use)
        {
            var inv = use.Actor;
            int allowed = Math.Min(LucyBarricadeTokens, use.TokenLimit);
            if (use.Args.Count < allowed)
            {
                throw new InvalidOperationException(
                    $"Barricades: name {allowed} adjacent space(s); they must be placed at the same time.");
            }
            var spaces = use.Args.Take(allowed).ToList();
            int onBoard = BoardTokenIds(BarricadePrefix(inv.DefId)).Count;
            if (onBoard + allowed > LucyBarricadeTokens)
            {
                throw new InvalidOperationException(
                    $"{inv.DefId} only has {LucyBarricadeTokens} Barricade tokens and {onBoard} are on the board.");
            }
            var adjacent = Graph.DistancesFrom(inv.Space, 1, State.Overlay);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string space in spaces)
            {
                Graph.Space(space);
                if (space == inv.Space || !adjacent.ContainsKey(space))
                {
                    throw new InvalidOperationException($"Barricades: '{space}' is not adjacent to {inv.Space}.");
                }
                if (!seen.Add(space) || State.Overlay.AdversaryBarriers.Contains(space))
                {
                    throw new InvalidOperationException($"Barricades: '{space}' already has a Barricade.");
                }
            }
            foreach (string space in spaces)
            {
                onBoard += 1;
                string tokenId = BarricadePrefix(inv.DefId) + onBoard.ToString(CultureInfo.InvariantCulture);
                PlaceBoardToken(tokenId, space);
                State.Adversary.Counters[BarricadeDamagePrefix + tokenId] = 0;
                State.Overlay.AdversaryBarriers.Add(space);
            }
            Log("ability", $"{inv.DefId} barricaded {string.Join(" and ", spaces)}");
        }

        /// <summary>
        /// The Adversary's Break Door action aimed at a Barricade token. Returns false when
        /// there is no Barricade on that space, so Game.AdversaryBreakDoor falls through to the
        /// printed Door rules. Called from Game.cs after the adjacency and once-per-turn checks.
        /// </summary>
        private bool BreakBarricadeAt(string spaceId)
        {
            if (!State.Overlay.AdversaryBarriers.Contains(spaceId))
            {
                return false;
            }
            string tokenId = State.BoardTokens
                .Where(kv => kv.Value == spaceId && kv.Key.StartsWith("barricade:", StringComparison.Ordinal))
                .Select(kv => kv.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .FirstOrDefault() ?? "";
            string damageKey = BarricadeDamagePrefix + tokenId;
            int hits = (State.Adversary.Counters.TryGetValue(damageKey, out int d) ? d : 0) + 1;
            if (hits >= BarricadeBreaksToDestroy)
            {
                State.Overlay.AdversaryBarriers.Remove(spaceId);
                RemoveBoardToken(tokenId);
                State.Adversary.Counters.Remove(damageKey);
                Log("adversary", $"destroyed the Barricade on {spaceId}; the token is removed");
            }
            else
            {
                State.Adversary.Counters[damageKey] = hits;
                Log("adversary", $"damaged the Barricade on {spaceId} ({hits}/{BarricadeBreaksToDestroy}); it still blocks the way");
            }
            return true;
        }

        // ---------- Mada K. Rorrim ----------

        /// <summary>
        /// Minor, part 2: "When an Investigator within 4 spaces of you is the target of an
        /// Adversary Ability (not Attack), you may move adjacent to that Investigator and flip
        /// your Coin. On a Smile, you may choose new target(s) for the Ability, ignoring range.
        /// On a Frown, the Adversary may choose new target(s), also ignoring range."
        /// args: [target investigator def id, destination space (optional)].
        /// Part 1 (gaining the Coin on a Reveal) is passive; see
        /// <see cref="AbilitiesOnAdversaryRevealed"/>.
        /// </summary>
        private void MadaFlipCoin(AbilityUse use)
        {
            var inv = use.Actor;
            string coinKey = CoinTokenPrefix + inv.DefId;
            if (!State.Adversary.Counters.ContainsKey(coinKey))
            {
                throw new InvalidOperationException($"{inv.DefId} has no Coin token; one is gained by Revealing an Adversary figure.");
            }
            if (use.Args.Count < 1 || use.Args[0].Length == 0)
            {
                throw new InvalidOperationException("Coin: name the Investigator the Adversary Ability is targeting.");
            }
            var target = Investigator(use.Args[0]);
            if (target == inv || target.Dead || target.Escaped || IsSpirit(target))
            {
                throw new InvalidOperationException($"{use.Args[0]} is not another Investigator on the board.");
            }
            if (!Graph.DistancesFrom(inv.Space, MadaCoinRange, State.Overlay).ContainsKey(target.Space))
            {
                throw new InvalidOperationException($"{target.DefId} is more than {MadaCoinRange} spaces from {inv.DefId}.");
            }
            // "You may move adjacent to that Investigator": pick the named landing space, or the
            // first free one when the caller leaves the choice to the engine.
            var landings = Graph.DistancesFrom(target.Space, 1, State.Overlay).Keys
                .Where(s => s != target.Space &&
                            !State.Investigators.Any(o => o != inv && !o.Dead && !o.Escaped && o.Space == s))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            string destination = use.Args.Count > 1 && use.Args[1].Length > 0
                ? use.Args[1]
                : (landings.FirstOrDefault() ?? throw new InvalidOperationException($"No free space adjacent to {target.DefId}."));
            if (!landings.Contains(destination))
            {
                throw new InvalidOperationException($"Coin: '{destination}' is not a free space adjacent to {target.DefId}.");
            }
            string from = inv.Space;
            inv.Space = destination;
            RemoveFlashlightIfForcedMove(inv.DefId);
            State.Adversary.Counters.Remove(coinKey);
            bool smile = _rng.Roll(2) == 1;
            SaveRng();
            Log("ability", $"{inv.DefId} slid from {from} to {destination} and flipped the Coin: " +
                           (smile ? "Smile - the Investigators re-target the Ability" : "Frown - the Adversary re-targets the Ability"));
            LogTodoOnce("mada-coin-retarget",
                "mada minor: the re-target itself is not applied — PlayAdversaryCard resolves an Ability and its " +
                "targets atomically, with no interrupt window a reaction could re-aim; flip the Coin first, then " +
                "play the card at the agreed targets");
        }

        /// <summary>
        /// Major: "Lose 5 Stamina in order to roll the Sprint die 3 times during this turn. You
        /// may not use this Ability unless you have 5 Stamina, and losing Stamina in this way
        /// does not incur a face-down Wound."
        /// </summary>
        private void MadaTripleSprint(AbilityUse use)
        {
            var inv = use.Actor;
            RequireOwnTurn(inv, "Mada's Major Ability rolls \"during this turn\"");
            if (inv.Stamina < MadaMajorStaminaCost)
            {
                throw new InvalidOperationException(
                    $"Mada's Major Ability needs {MadaMajorStaminaCost} Stamina; {inv.DefId} has {inv.Stamina}.");
            }
            if (inv.SprintedOrRested)
            {
                throw new InvalidOperationException("Sprint or Rest was already used this turn.");
            }
            // Direct decrement: "losing Stamina in this way does not incur a face-down Wound",
            // so LoseStamina's Wound-icon crossing must be bypassed on purpose.
            inv.Stamina -= MadaMajorStaminaCost;
            inv.SprintedOrRested = true;
            int total = 0;
            var rolls = new List<int>();
            for (int i = 0; i < MadaMajorSprintRolls; i++)
            {
                int rolled = _rng.RollSprintDie(Db.Config.SprintDieFaces);
                SaveRng();
                var rollBox = new List<int> { rolled };
                ModifySprintRoll(inv, rollBox);
                int final = Math.Max(1, rollBox[0]);
                rolls.Add(final);
                total += final;
            }
            inv.MpRemaining += total;
            Log("ability", $"{inv.DefId} burned {MadaMajorStaminaCost} Stamina and rolled {string.Join("+", rolls)} = {total} MP");
        }

        // ---------- Marci Jo ----------

        /// <summary>
        /// Major: "Choose up to 2 other Investigators within 8 spaces of you. You may move each
        /// of them up to 2 spaces in any direction, treating Dark spaces as Dim. Map Hazards
        /// retain their effects." args: [invId, destination, invId, destination].
        /// </summary>
        private void MarciMoveInvestigators(AbilityUse use)
        {
            var inv = use.Actor;
            if (use.Args.Count < 2 || use.Args.Count % 2 != 0)
            {
                throw new InvalidOperationException(
                    "Marci's Major Ability takes Investigator/destination pairs: [invId, space] or [invId, space, invId, space].");
            }
            if (use.Args.Count / 2 > MarciMajorTargets)
            {
                throw new InvalidOperationException($"Marci's Major Ability moves up to {MarciMajorTargets} other Investigators.");
            }
            var reach = Graph.DistancesFrom(inv.Space, MarciMajorRange, State.Overlay);
            var moves = new List<(InvestigatorState Target, string Destination)>();
            for (int i = 0; i < use.Args.Count; i += 2)
            {
                var target = Investigator(use.Args[i]);
                string destination = use.Args[i + 1];
                Graph.Space(destination);
                if (target == inv || target.Dead || target.Escaped || IsSpirit(target))
                {
                    throw new InvalidOperationException($"{use.Args[i]} is not another Investigator on the board.");
                }
                if (moves.Any(m => m.Target == target))
                {
                    throw new InvalidOperationException($"{target.DefId} was named twice.");
                }
                if (!reach.ContainsKey(target.Space))
                {
                    throw new InvalidOperationException($"{target.DefId} is more than {MarciMajorRange} spaces from {inv.DefId}.");
                }
                // DistancesFrom ignores light level entirely, which is exactly what "treating
                // Dark spaces as Dim" buys the target.
                if (destination != target.Space &&
                    !Graph.DistancesFrom(target.Space, MarciMajorMove, State.Overlay).ContainsKey(destination))
                {
                    throw new InvalidOperationException(
                        $"'{destination}' is more than {MarciMajorMove} spaces from {target.Space}.");
                }
                moves.Add((target, destination));
            }
            foreach (var move in moves)
            {
                if (State.Investigators.Any(o => o != move.Target && !o.Dead && !o.Escaped && o.Space == move.Destination))
                {
                    throw new InvalidOperationException($"'{move.Destination}' is occupied by another Investigator.");
                }
            }
            foreach (var move in moves)
            {
                string from = move.Target.Space;
                move.Target.Space = move.Destination;
                RemoveFlashlightIfForcedMove(move.Target.DefId);
                Log("ability", $"{inv.DefId} walked {move.Target.DefId} from {from} to {move.Destination}");
            }
            LogTodoOnce("marci-major-hazards",
                "marci major: \"Map Hazards retain their effects\" is not applied — this is a destination move (the " +
                "same shape as the Phantom's Luring Lights), so no Window crossing, Water float, or carriage " +
                "rotation along the path is resolved; MoveStep is the only per-step Hazard site and it only walks " +
                "the active figure");
        }

        // ---------- Mitchell Carter ----------

        /// <summary>
        /// Minor, "Sweep": "You may Sweep when you place your Flashlight. Sweep: Place your
        /// Flashlight and check if anything is Revealed. Then, move it to a new position. It
        /// will stay in the 2nd position until the end of the round." args: [new angle in
        /// radians]. Once per Flashlight, and a Flashlight lasts exactly one round.
        /// </summary>
        private void MitchellSweep(AbilityUse use)
        {
            var inv = use.Actor;
            var placement = State.Flashlights.FirstOrDefault(f => f.InvestigatorId == inv.DefId)
                ?? throw new InvalidOperationException($"Sweep follows placing a Flashlight; {inv.DefId} has none on the board.");
            if (HasRoundModifier(SweepUsedPrefix + inv.DefId))
            {
                throw new InvalidOperationException("Sweep may only be used once per Flashlight.");
            }
            if (use.Args.Count < 1 ||
                !double.TryParse(use.Args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double angle))
            {
                throw new InvalidOperationException("Sweep: give the Flashlight's new angle in radians.");
            }
            if (Math.Abs(angle - placement.AngleRadians) < 1e-9)
            {
                throw new InvalidOperationException("Sweep moves the Flashlight to a *new* position.");
            }
            var bright = _beam.ComputeBright(Graph, placement.Space, angle, _losBlocker);
            TrimFlashlightBright(inv, angle, bright);
            // "Move it to a new position": the 2nd cone REPLACES the 1st rather than adding to
            // it — designer ruling. Whatever the 1st cone Revealed stays Revealed (reveals are
            // a one-way latch; see RevealOnBright), but its Bright coverage must not linger, so
            // every space only the 1st position lit goes dark again unless the 2nd position (or
            // another Investigator's own Flashlight) also covers it. This deliberately does NOT
            // reuse the RecomputeBrightSpaces()/RemoveFlashlightIfForcedMove() pattern: that
            // rebuild wipes State.Overlay.BrightSpaces down to just State.Flashlights, which
            // would also extinguish space-anchored light this file has no way to restore
            // (Lantern, Lantern (MI), Lorgnette) — a strictly worse regression than the one
            // being fixed. Touching only the spaces this placement itself contributed keeps
            // every other Bright source untouched.
            foreach (string space in placement.BrightSpaces)
            {
                bool litElsewhere = State.Flashlights.Any(f => f != placement && f.BrightSpaces.Contains(space)) ||
                                    bright.Contains(space);
                if (!litElsewhere)
                {
                    State.Overlay.BrightSpaces.Remove(space);
                }
            }
            placement.AngleRadians = angle;
            placement.BrightSpaces = bright.OrderBy(s => s, StringComparer.Ordinal).ToList();
            State.Overlay.BrightSpaces.UnionWith(bright);
            SetRoundModifier(SweepUsedPrefix + inv.DefId, 1);
            Log("ability", $"{inv.DefId} Swept their Flashlight to a 2nd position ({bright.Count} spaces lit)");
            RevealOnBright(bright);
        }

        /// <summary>
        /// Major: "Choose an Investigator (including yourself). If the Adversary is within 2
        /// spaces of that Investigator, the Adversary must Reveal themself."
        /// args: [investigator def id] (defaults to the user).
        /// </summary>
        private void MitchellFindAdversary(AbilityUse use)
        {
            var chosen = use.Args.Count > 0 && use.Args[0].Length > 0 ? Investigator(use.Args[0]) : use.Actor;
            if (chosen.Dead || chosen.Escaped)
            {
                throw new InvalidOperationException($"{chosen.DefId} is no longer on the board.");
            }
            var within = new HashSet<string>(
                Graph.DistancesFrom(chosen.Space, MitchellMajorRange, State.Overlay).Keys, StringComparer.Ordinal);
            Log("ability", $"{use.Actor.DefId} listens for the Adversary within {MitchellMajorRange} spaces of {chosen.DefId}");
            RevealAdversaryFiguresOn(within, $"within {MitchellMajorRange} spaces of {chosen.DefId}");
        }

        // ---------- Vincent Campbell ----------

        /// <summary>
        /// Minor, "Scout": "While on a Point of Interest space, you may Scout. Scout: If there
        /// are any Point of Interest tokens within 2 spaces, the Adversary must place them
        /// face-down on the main board."
        /// </summary>
        private void VincentScout(AbilityUse use)
        {
            var inv = use.Actor;
            if (Graph.Space(inv.Space).Kind != SpaceKind.PointOfInterest)
            {
                throw new InvalidOperationException($"Scout may only be used on a Point of Interest space; {inv.DefId} is on {inv.Space}.");
            }
            var within = Graph.DistancesFrom(inv.Space, VincentScoutRange, State.Overlay);
            var found = State.PoiTokens
                .Where(p => !p.Collected && !p.ScoutedFaceDown && within.ContainsKey(p.TokenSpace))
                .OrderBy(p => p.TokenSpace, StringComparer.Ordinal)
                .ToList();
            if (found.Count == 0)
            {
                Log("ability", $"{inv.DefId} Scouted from {inv.Space} and found nothing within {VincentScoutRange} spaces");
                return;
            }
            foreach (var poi in found)
            {
                poi.ScoutedFaceDown = true;
            }
            Log("ability", $"{inv.DefId} Scouted out POI token(s) at {string.Join(", ", found.Select(p => p.TokenSpace))}");
            LogTodoOnce("vincent-scout",
                "vincent minor: \"face-down on the main board\" only records that the token's *space* is now public " +
                "(PoiTokenState.ScoutedFaceDown). The engine has no hidden-information layer — PoiTokenState already " +
                "carries both the space and the face for everyone — so nothing else changes, and picking the token " +
                "up still needs it Revealed");
        }

        /// <summary>Major: "If you have an Item card in your inventory, draw a Cursed Item card."</summary>
        private void VincentDrawCursedItem(AbilityUse use)
        {
            var inv = use.Actor;
            if (!inv.Items.Any(id => Db.Cards.Any(c => c.Id == id && IsItemDeck(c.Deck))))
            {
                throw new InvalidOperationException($"{inv.DefId} has no Item card in their inventory.");
            }
            string drawn = Draw(State.CursedItemDeck, "cursed item");
            inv.Items.Add(drawn);
            Log("ability", $"{inv.DefId} turned an Item into the Cursed Item {drawn}");
        }

        // ---------- Shared helpers ----------

        private void RequireOwnTurn(InvestigatorState inv, string why)
        {
            if (State.Phase != GamePhase.InvestigatorTurns || State.ActiveInvestigator != inv.DefId)
            {
                throw new InvalidOperationException($"{why}; it is not {inv.DefId}'s turn.");
            }
        }

        /// <summary>Reveal every Adversary figure standing in <paramref name="spaces"/>. Used by
        /// the two "the Adversary must Reveal themself" Majors (Aira's, Mitchell's), neither of
        /// which lights anything.</summary>
        private void RevealAdversaryFiguresOn(ICollection<string> spaces, string reason)
        {
            if (spaces.Contains(State.Adversary.Space))
            {
                RevealAdversary(reason);
            }
            foreach (var figure in State.Adversary.Figures)
            {
                if (figure.Alive && !figure.Revealed && spaces.Contains(figure.Space))
                {
                    figure.Revealed = true;
                    DropShadowToken(figure.Id);
                    Log("reveal", $"{figure.Id} at {figure.Space} ({reason})");
                    OnAdversaryRevealed(figure.Id);
                }
            }
        }

        /// <summary>
        /// True when this face-up Wound's effects are ignored right now: Asher's Minor
        /// ("Ignore the effects of any face-up Wound in your first slot") and his Major
        /// ("ignore the effects of any face-up Wound you have" for the turn). Consulted by
        /// FaceUpWound in Game.WoundConditionEffects.cs, which every ongoing Wound clause reads,
        /// and by the ResolveWoundFaceUp dispatcher, which carries the immediate ones.
        /// </summary>
        private bool IgnoresFaceUpWound(InvestigatorState inv, WoundInstance wound)
        {
            if (HasRoundModifier(IgnoreAllWoundsPrefix + inv.DefId))
            {
                return true;
            }
            // The Minor is printed on Asher's own board, so it is not borrowable and does not
            // key off anything but who he is. NonSlotWounds (Neurotoxin's) are in no slot at
            // all, so the reference comparison correctly never matches one.
            return inv.DefId == "asher" && inv.Wounds.Count > 0 && ReferenceEquals(inv.Wounds[0], wound);
        }

        /// <summary>Fear: "you must use your Major Ability on your next turn". Called by
        /// Game.EndTurnWithoutFinalAction; the Final Actions are refused by the per-action
        /// gate below.</summary>
        private void RequireForcedAbilityUsed(InvestigatorState inv)
        {
            if (ForcedMajorAbilityPending(inv))
            {
                throw new InvalidOperationException(
                    $"Fear: {inv.DefId} must use their Major Ability before their turn ends.");
            }
            if (HasRoundModifier(ForcedMajorAbilityPrefix + inv.DefId))
            {
                // The compulsion was armed (a token was held and blockers.Count was 0 at the
                // time), but by the time the turn is ending the Major Ability can no longer
                // legally resolve — Stamina spent elsewhere, an Item traded away, too few
                // reachable targets, the token itself spent some other way, ... "If you do not
                // have a Major Ability token (or if you are unable to use it), there is no
                // effect": release the compulsion here rather than let it lock the turn forever.
                ClearRoundModifier(ForcedMajorAbilityPrefix + inv.DefId);
                Log("wound", $"fear: {inv.DefId} can no longer resolve their Major Ability, so the compulsion is released");
            }
        }

        /// <summary>True while Fear's forced Major Ability is both armed and still legally
        /// satisfiable right now. Read by <see cref="RequireForcedAbilityUsed"/> and by
        /// <see cref="AbilitiesCollectActionBlockers"/> (the Charge / Place Flashlight /
        /// Involved Action gate) — deliberately side-effect free, since the latter also backs
        /// the public read-only <see cref="ActionBlockers"/> query.</summary>
        private bool ForcedMajorAbilityPending(InvestigatorState inv) =>
            HasRoundModifier(ForcedMajorAbilityPrefix + inv.DefId) &&
            // Death, escape, or Spirit conversion mid-turn ends any ability use (the same
            // gate BeginAbilityUse enforces), so the compulsion is unresolvable and must
            // release rather than deadlock the dying Investigator's final end-of-turn.
            !inv.Dead && !inv.Escaped && !IsSpirit(inv) &&
            inv.MajorAbilityTokens >= 1 &&
            CanResolveMajorAbility(inv);

        /// <summary>
        /// Fear: "If you do not have a Major Ability token (or if you are unable to use it),
        /// there is no effect." Best-effort per-Investigator precondition check mirroring each
        /// Major Ability's own validation in <see cref="ResolveMajorAbility"/>, so the forced
        /// compulsion is never enforced against an Investigator who could never satisfy it.
        /// Purely a read (no state is touched) so it is safe to call from the action-blocker
        /// gate as well as from the turn-end enforcement above. Preconditions that are only
        /// about the arguments a player supplies (a named space, a named target) rather than
        /// game state are not checked here — those always have *some* legal answer, so this
        /// returns true and lets the normal call refuse a specific bad argument instead.
        /// </summary>
        private bool CanResolveMajorAbility(InvestigatorState inv)
        {
            switch (inv.DefId)
            {
                case "mada":
                    // MadaTripleSprint: "You may not use this Ability unless you have 5 Stamina."
                    return inv.Stamina >= MadaMajorStaminaCost;

                case "vincent":
                    // VincentDrawCursedItem: "If you have an Item card in your inventory..."
                    // Cursed Item ids carry a ':' (borrowed-Ability-style instance tagging is
                    // the only other user of that character in an id); a plain Item id never
                    // does, so this mirrors the real Item check without a Db round-trip.
                    return inv.Items.Any(id => id.IndexOf(':') < 0);

                case "marci":
                    // MarciMoveInvestigators: up to 2 other live, non-Spirit Investigators
                    // within 8 spaces, each with at least 1 legal destination (their own space
                    // always counts, so this is really just a headcount).
                    return MarciHasLegalMajorTargets(inv);

                case "brielle":
                    // BrielleIgnoreEvent: needs a live Event card that is not already ignored.
                    return State.CurrentEvent != null && !HasRoundModifier(EventIgnoredKey);

                case "lucy-belle":
                    // LucyPlaceBarricades: at least 1 of her 2 tokens still unplaced, and at
                    // least 1 space adjacent to her not already Barricaded.
                    return LucyHasLegalBarricadeSpace(inv);

                // Aira (names 2 board spaces — always exist), Asher (Gain Stamina/MP and set a
                // round modifier — no precondition at all), Dylan (both halves of his Major —
                // placing the token on some adjacent space, or the free return while it is
                // out — are always legal), Ibraheem (sets a round modifier — no precondition),
                // and Mitchell (defaults to the acting Investigator, always a legal target)
                // never fail their own Major's preconditions.
                case "aira":
                case "asher":
                case "dylan":
                case "ibraheem":
                case "mitchell":
                    return true;

                default:
                    // Unrecognized def id: best-effort assumes the compulsion is satisfiable
                    // rather than silently releasing Fear for someone it should still bind.
                    return true;
            }
        }

        /// <summary>Marci's Major needs at least <see cref="MarciMajorTargets"/> other live,
        /// non-Spirit Investigators within <see cref="MarciMajorRange"/> spaces, each with at
        /// least 1 legal destination within <see cref="MarciMajorMove"/> (their own space is
        /// always one, so this only actually excludes someone stacked with no reachable free
        /// space at all — vanishingly rare, but mirrored here rather than assumed away).</summary>
        private bool MarciHasLegalMajorTargets(InvestigatorState inv)
        {
            var reach = Graph.DistancesFrom(inv.Space, MarciMajorRange, State.Overlay);
            int eligible = State.Investigators.Count(o =>
                o != inv && !o.Dead && !o.Escaped && !IsSpirit(o) && reach.ContainsKey(o.Space) &&
                Graph.DistancesFrom(o.Space, MarciMajorMove, State.Overlay).Keys.Any(s =>
                    !State.Investigators.Any(other => other != o && !other.Dead && !other.Escaped && other.Space == s)));
            return eligible >= MarciMajorTargets;
        }

        /// <summary>Lucy Belle's Major needs a Barricade token still off the board and a free
        /// (not already Barricaded) space adjacent to her.</summary>
        private bool LucyHasLegalBarricadeSpace(InvestigatorState inv)
        {
            if (BoardTokenIds(BarricadePrefix(inv.DefId)).Count >= LucyBarricadeTokens)
            {
                return false;
            }
            return Graph.DistancesFrom(inv.Space, 1, State.Overlay).Keys
                .Any(s => s != inv.Space && !State.Overlay.AdversaryBarriers.Contains(s));
        }

        /// <summary>A face-up Fear Wound just landed: arm the forced Major Ability for this
        /// Investigator's next turn. Called from Game.WoundConditionEffects.cs.</summary>
        private void ArmForcedMajorAbility(InvestigatorState inv)
        {
            State.Adversary.Counters[FearForcedMajorPrefix + inv.DefId] = State.Round;
            Log("wound", $"fear: {inv.DefId} must use their Major Ability on their next turn");
        }

        // ---------- Sub-hooks (fanned out from Game.EffectDispatch.cs) ----------

        partial void AbilitiesOnTurnStart(InvestigatorState inv)
        {
            // Per-turn allowances start fresh. Both keys live in RoundModifiers (which only
            // clears between rounds), so they are reset here rather than relied on to expire.
            ClearRoundModifier(DarkAsDimUsedPrefix + inv.DefId);
            ClearRoundModifier(SprintPenaltyTransferPrefix + inv.DefId);

            // Ibraheem's Minor. This runs last in the turn-start fanout on purpose: every card
            // that cuts MP (Fractured Foot, Pulled Hammy, Choking Fear, Paranoid, Rainy) has
            // already taken its bite, so what is left below 4 is exactly the shortfall his
            // Sprint roll has to pay instead.
            if (inv.DefId == "ibraheem" && inv.MpRemaining < IbraheemMoveFloor)
            {
                int deficit = IbraheemMoveFloor - inv.MpRemaining;
                inv.MpRemaining = IbraheemMoveFloor;
                SetRoundModifier(SprintPenaltyTransferPrefix + inv.DefId, deficit);
                Log("ability", $"{inv.DefId}'s Footprint never drops below {IbraheemMoveFloor}; " +
                               $"the {deficit} lost Footprint moves to his Sprint roll");
            }

            // Fear: "you must use your Major Ability on your next turn. If you do not have a
            // Major Ability token (or if you are unable to use it), there is no effect."
            // Armed at the first turn start of a *later* round than the one the Wound landed
            // in, which is "your next turn" for every case except a Wound flipped face-up
            // earlier in the same round but before this Investigator had acted; that one slips
            // to the following round rather than being enforced retroactively.
            string fearKey = FearForcedMajorPrefix + inv.DefId;
            if (State.Adversary.Counters.TryGetValue(fearKey, out int gainedRound) && gainedRound < State.Round)
            {
                State.Adversary.Counters.Remove(fearKey);
                var blockers = new List<string>();
                CollectActionBlockers(inv, ActionUseAbility, blockers);
                if (inv.MajorAbilityTokens >= 1 && blockers.Count == 0)
                {
                    SetRoundModifier(ForcedMajorAbilityPrefix + inv.DefId, 1);
                    Log("wound", $"fear: {inv.DefId} may do nothing else this turn until their Major Ability is used");
                }
                else
                {
                    Log("wound", $"fear: {inv.DefId} is unable to use a Major Ability, so the card has no effect");
                }
            }
        }

        partial void AbilitiesOnTurnEnd(InvestigatorState inv)
        {
            // Asher's Major lasts "during this turn".
            ClearRoundModifier(IgnoreAllWoundsPrefix + inv.DefId);
            ClearRoundModifier(ForcedMajorAbilityPrefix + inv.DefId);

            // Dylan's Escape Artist token: "Remove the token when it is used or at the end of
            // your next turn" — the first turn end in a later round than it was placed.
            if (BoardTokenSpace(EscapeArtistTokenId(inv.DefId)) != null &&
                State.Adversary.Counters.TryGetValue(EscapeArtistRoundPrefix + inv.DefId, out int placed) &&
                State.Round > placed)
            {
                RemoveEscapeArtistToken(inv);
                Log("ability", $"{inv.DefId}'s Escape Artist token is removed at the end of their next turn");
            }
        }

        partial void AbilitiesOnMoveStep(InvestigatorState inv, string from, string to)
        {
            // Consume Dylan's Dark-as-Dim allowance only once the step has actually happened:
            // AbilitiesAdjustMoveCost is asked before the MP check and must stay side-effect
            // free, so a step refused for want of MP costs him nothing. The two read exactly
            // the same state, so they always agree on whether the discount applied.
            if (DarkAsDimDiscountApplies(inv, to))
            {
                int used = RoundModifier(DarkAsDimUsedPrefix + inv.DefId) + 1;
                SetRoundModifier(DarkAsDimUsedPrefix + inv.DefId, used);
                Log("ability", $"{inv.DefId} treated Dark {to} as Dim ({used}/{DylanDarkSpacesPerTurn} this turn)");
            }
        }

        partial void AbilitiesAdjustMoveCost(InvestigatorState inv, string from, string to, List<int> costBox)
        {
            if (DarkAsDimDiscountApplies(inv, to))
            {
                costBox[0] -= 1;
            }
        }

        /// <summary>Dylan's Minor: "You may treat up to 3 Dark spaces as Dim when you Move or
        /// Sprint onto them." Always taken when available — each use saves exactly 1 MP, so
        /// spending them on the first eligible steps is never worse than saving them.</summary>
        private bool DarkAsDimDiscountApplies(InvestigatorState inv, string to) =>
            inv.DefId == "dylan" &&
            RoundModifier(DarkAsDimUsedPrefix + inv.DefId) < DylanDarkSpacesPerTurn &&
            Graph.HasSpace(to) &&
            Graph.EffectiveLight(to, State.Overlay) == LightLevel.Dark;

        partial void AbilitiesModifySprintRoll(InvestigatorState inv, List<int> rollBox)
        {
            // Lucy Belle's Minor reinterprets the die *face*, so it runs before the cards that
            // subtract from the result (this hook is first in the ModifySprintRoll fanout).
            if (inv.DefId == "lucy-belle" && rollBox[0] == 2)
            {
                rollBox[0] = 3;
                Log("ability", $"{inv.DefId} counts a rolled 2 as 3 Footprint");
            }
            int transferred = RoundModifier(SprintPenaltyTransferPrefix + inv.DefId);
            if (transferred > 0)
            {
                rollBox[0] -= transferred;
                Log("ability", $"{inv.DefId}'s Sprint roll pays the {transferred} Footprint his floor refunded");
            }
        }

        partial void AbilitiesCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers)
        {
            switch (actionKey)
            {
                case ActionInvolved:
                    // Aira's Minor: "...but you may not perform any other Involved Action during
                    // that turn." Game.EndTurn latches InvolvedActionUsedPrefix when it converts
                    // the first one.
                    if (HasRoundModifier(AiraInvolvedArmedPrefix + inv.DefId) &&
                        HasRoundModifier(InvolvedActionUsedPrefix + inv.DefId))
                    {
                        blockers.Add($"{inv.DefId} already took their free Involved Action this turn");
                    }
                    goto case ActionCharge;
                case ActionCharge:
                case ActionPlaceFlashlight:
                    if (ForcedMajorAbilityPending(inv))
                    {
                        blockers.Add("Fear: your Major Ability must be used before anything ends this turn");
                    }
                    break;
                case ActionUseAbility:
                    if (IsSpirit(inv))
                    {
                        blockers.Add("a Spirit has no player board, and so no Minor or Major Ability");
                    }
                    break;
            }
        }

        partial void AbilitiesOnAdversaryMoveStep(string from, string to)
        {
            // Brielle's Minor: "When the Adversary moves onto a Can token, they flip it to the
            // Noise side."
            string? canId = CanTokenAt(to);
            if (canId == null)
            {
                return;
            }
            RemoveBoardToken(canId);
            string suffix = canId.Substring(canId.LastIndexOf(':') + 1);
            string owner = canId.Split(':')[1];
            PlaceBoardToken(NoiseCanPrefix(owner) + suffix, to);
            Log("ability", $"the Adversary kicked {owner}'s Can on {to}: it is flipped to its Noise side");
        }

        partial void AbilitiesOnAdversaryRevealed(string figureId)
        {
            // Mada's Minor, part 1: "Gain your Coin token whenever you Reveal an Adversary
            // figure. You may only have 1 Coin token at a time."
            var mada = State.Investigators.FirstOrDefault(
                i => i.DefId == "mada" && !i.Dead && !i.Escaped && !IsSpirit(i));
            if (mada == null)
            {
                return;
            }
            string coinKey = CoinTokenPrefix + mada.DefId;
            if (State.Adversary.Counters.ContainsKey(coinKey))
            {
                return;
            }
            State.Adversary.Counters[coinKey] = 1;
            Log("ability", $"{mada.DefId} gains his Coin token ({figureId} was Revealed)");
        }
    }
}
