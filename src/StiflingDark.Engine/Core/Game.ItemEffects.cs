using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StiflingDark.Engine.Data;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// Item card effects: General Items, the Medical Item (Medkit), and Cursed Items
    /// (game-data/cards/general-items.json [base + MI sets only; NF is out of v1 scope],
    /// medical-items.json, cursed-items.json). Everything routes through
    /// <see cref="UseItem"/>; the round-scoped/ongoing pieces hang off the Items sub-hooks
    /// declared in Game.EffectDispatch.cs.
    ///
    /// SERIALIZABLE STATE WITHOUT TOUCHING GameState.cs
    /// -------------------------------------------------
    /// Two kinds of bookkeeping need to persist across calls (Supply counters, ongoing
    /// curses) but GameState.cs may not be edited. Both piggyback on the one general-purpose
    /// serializable string bag every Investigator already has: <c>InvestigatorState.Items</c>.
    /// Real card ids are always plain kebab-case (no ':'), so entries containing ':' can
    /// never collide with a real card id and are safe to mix into the same list:
    ///
    ///   "supply:&lt;cardId&gt;:&lt;usesSoFar&gt;"  - how many of a Supply-N card's uses have
    ///                                         been spent. Absent = 0 used. Removed once the
    ///                                         card itself is discarded (see
    ///                                         <see cref="ApplySupplyOrDiscard"/>).
    ///   "marker:&lt;name&gt;"                - a standing, serializable flag on that
    ///                                         Investigator that outlives the physical card
    ///                                         (e.g. "marker:curse:diablerie-book" keeps
    ///                                         Diablerie Book's turn-start curse running even
    ///                                         after the single-use card itself is discarded;
    ///                                         "marker:obstruction:stamina" records a Cursed
    ///                                         Item's Obstruction token).
    ///
    /// Both helpers are private and encapsulated below (<see cref="GetSupplyUsed"/> /
    /// <see cref="SetSupplyUsed"/> / <see cref="ClearSupplyMarker"/> and
    /// <see cref="AddMarker"/> / <see cref="HasMarker"/> / <see cref="RemoveMarker"/>) so no
    /// other code needs to know the string format.
    ///
    /// DISCARD RULE (per the task spec, applied uniformly): a card with a numeric Supply
    /// discards once its Nth use is spent; a card with Supply "infinity" (CardDef.Supply
    /// == -1) never discards; a card with no Supply icon at all is single-use and discards
    /// immediately. This is applied the same way for General, Medical, and Cursed cards.
    /// A few Cursed Items read as permanent passive curses in their flavour text (Diablerie
    /// Book, Hexed Mirror) - those still discard the physical card under this rule, but the
    /// ongoing hookable part of their text (Diablerie Book's turn-start flip) is preserved
    /// via a "marker:" entry that survives the discard. See the per-card notes in
    /// <see cref="ApplyItemEffect"/> for every other judgment call.
    /// </summary>
    public sealed partial class Game
    {
        private const string SupplyPrefix = "supply:";
        private const string MarkerPrefix = "marker:";

        // ---------- Public API ----------

        /// <summary>
        /// Use a General, Medical, or Cursed Item card the active Investigator holds. Using
        /// Item cards is a free interact - it does not touch MP, the Final Action slot, or
        /// end the turn (Tripod is the one exception or turn-flow it can be built on, and it
        /// says so explicitly). <paramref name="args"/> carries whatever the card's text
        /// needs to pick (a target Investigator, a space, a die-roll choice, ...); each
        /// card's expectations are documented at its case in <see cref="ApplyItemEffect"/>.
        /// </summary>
        /// <summary>Complete a pending Painkillers draw: swap one face-up Wound for one of the
        /// two drawn cards, or pass nulls to decline. The undrafted cards are discarded.</summary>
        public void ResolvePainkillers(string? existingWoundCardId, string? chosenDrawnCardId)
        {
            var inv = ActiveInv();
            string? marker = inv.Items.FirstOrDefault(i => i.StartsWith("marker:painkillers:", StringComparison.Ordinal));
            if (marker == null)
            {
                throw new InvalidOperationException("No Painkillers draw is pending.");
            }
            var parts = marker.Split(':');
            string drawn1 = parts[2], drawn2 = parts[3];
            if (existingWoundCardId != null && chosenDrawnCardId != null)
            {
                var toReplace = inv.Wounds.FirstOrDefault(w => w.FaceUp && w.CardId == existingWoundCardId)
                    ?? throw new InvalidOperationException($"No face-up Wound '{existingWoundCardId}' to replace.");
                if (chosenDrawnCardId != drawn1 && chosenDrawnCardId != drawn2)
                {
                    throw new InvalidOperationException($"'{chosenDrawnCardId}' was not drawn ({drawn1}, {drawn2}).");
                }
                // The undrafted drawn card and the Wound it replaced both leave play.
                string undrafted = chosenDrawnCardId == drawn1 ? drawn2 : drawn1;
                State.WoundDiscard.Add(existingWoundCardId);
                State.WoundDiscard.Add(undrafted);
                toReplace.CardId = chosenDrawnCardId;
                ResolveWoundFaceUp(inv, toReplace);
                Log("item", $"{inv.DefId} replaced {existingWoundCardId} with {chosenDrawnCardId}");
            }
            else
            {
                State.WoundDiscard.Add(drawn1);
                State.WoundDiscard.Add(drawn2);
                Log("item", $"{inv.DefId} declined the Painkillers swap");
            }
            inv.Items.Remove(marker);
        }

        public void UseItem(string cardId, List<string>? args = null)
        {
            args ??= new List<string>();
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireActionAllowed(inv, ActionUseItem);
            if (!inv.Items.Contains(cardId))
            {
                throw new InvalidOperationException($"{inv.DefId} does not hold Item card '{cardId}'.");
            }
            var card = FindItemCard(cardId);
            ApplyItemEffect(inv, card, args);
            ApplySupplyOrDiscard(inv, card);
        }

        private CardDef FindItemCard(string cardId) =>
            Db.Cards.FirstOrDefault(c => c.Id == cardId && IsItemDeck(c.Deck))
            ?? throw new InvalidOperationException($"'{cardId}' is not a General, Medical, or Cursed Item card.");

        private static bool IsItemDeck(string deck) =>
            deck == "general-item" || deck == "medical-item" || deck == "cursed-item";

        // ---------- Supply-use / discard bookkeeping ----------

        private static string SupplyMarkerPrefix(string cardId) => SupplyPrefix + cardId + ":";

        private int GetSupplyUsed(InvestigatorState inv, string cardId)
        {
            string prefix = SupplyMarkerPrefix(cardId);
            string? entry = inv.Items.FirstOrDefault(e => e.StartsWith(prefix, StringComparison.Ordinal));
            return entry == null ? 0 : int.Parse(entry.Substring(prefix.Length), CultureInfo.InvariantCulture);
        }

        private void SetSupplyUsed(InvestigatorState inv, string cardId, int used)
        {
            ClearSupplyMarker(inv, cardId);
            inv.Items.Add(SupplyMarkerPrefix(cardId) + used.ToString(CultureInfo.InvariantCulture));
        }

        private void ClearSupplyMarker(InvestigatorState inv, string cardId)
        {
            string prefix = SupplyMarkerPrefix(cardId);
            inv.Items.RemoveAll(e => e.StartsWith(prefix, StringComparison.Ordinal));
        }

        private void ApplySupplyOrDiscard(InvestigatorState inv, CardDef card)
        {
            if (card.Supply == -1)
            {
                return; // "infinity": never discards, no counter needed
            }
            if (card.Supply == null)
            {
                inv.Items.Remove(card.Id);
                Log("item", $"{inv.DefId} discarded {card.Name} (single use)");
                return;
            }
            int used = GetSupplyUsed(inv, card.Id) + 1;
            if (used >= card.Supply.Value)
            {
                ClearSupplyMarker(inv, card.Id);
                inv.Items.Remove(card.Id);
                Log("item", $"{inv.DefId} discarded {card.Name} (used all {card.Supply} Supply)");
            }
            else
            {
                SetSupplyUsed(inv, card.Id, used);
                Log("item", $"{inv.DefId} used {card.Name} ({used}/{card.Supply} Supply)");
            }
        }

        // ---------- Persistent marker bookkeeping (curses, Obstruction tokens, ...) ----------

        private static string MarkerEntry(string name) => MarkerPrefix + name;

        private void AddMarker(InvestigatorState inv, string name)
        {
            string entry = MarkerEntry(name);
            if (!inv.Items.Contains(entry))
            {
                inv.Items.Add(entry);
            }
        }

        private bool HasMarker(InvestigatorState inv, string name) => inv.Items.Contains(MarkerEntry(name));

        private void RemoveMarker(InvestigatorState inv, string name) => inv.Items.Remove(MarkerEntry(name));

        // ---------- Small shared mutators ----------

        private void GainCharge(InvestigatorState inv, int amount) =>
            inv.Charge = Math.Min(Db.Config.ChargeMax, inv.Charge + amount);

        /// <summary>Set Stamina to an exact value, clamped, without the normal
        /// LoseStamina/GainStamina side effects (wound-on-cross, track max). Used by cards
        /// whose text explicitly bypasses those (Cursed Poppet, Makeshift IV, Mystery Syringe).</summary>
        private void SetStaminaDirect(InvestigatorState inv, int value)
        {
            int max = Db.Investigator(inv.DefId).StaminaTrack.Spaces - 1;
            inv.Stamina = Math.Max(0, Math.Min(max, value));
        }

        // ---------- Shared Wound flip-down gate ----------

        /// <summary>Flip a face-up Wound face-down, honoring the Hemorrhage Wound's printed
        /// restriction: "Other face-up Wounds may not be flipped face-down until this Wound
        /// is flipped face-down." Used by every Item effect in this file that flips a Wound
        /// face-down (Medkit, Leather Jacket, Mystery Syringe) so the restriction is enforced
        /// consistently everywhere a Wound can be flipped down. Game.WoundConditionEffects.cs
        /// logs a "todo" for this same clause because nothing over there flips Wounds down;
        /// this is the actual enforcement site.</summary>
        private void FlipWoundFaceDown(InvestigatorState inv, WoundInstance wound)
        {
            if (wound.CardId != "hemorrhage" && inv.Wounds.Any(w => w.FaceUp && w.CardId == "hemorrhage"))
            {
                throw new InvalidOperationException("Hemorrhage: other face-up Wounds may not be flipped face-down until Hemorrhage is.");
            }
            wound.FaceUp = false;
        }

        // ---------- Medkit ----------

        private void UseMedkit(InvestigatorState user, List<string> args)
        {
            if (args.Count < 1)
            {
                throw new InvalidOperationException("Medkit: choose the Investigator to treat.");
            }
            var target = Investigator(args[0]);
            if (target != user)
            {
                bool adjacent = Graph.Edge(user.Space, target.Space) != null ||
                                Graph.DistancesFrom(user.Space, 1, State.Overlay).ContainsKey(target.Space);
                if (!adjacent)
                {
                    throw new InvalidOperationException("Medkit can only treat yourself or an adjacent Investigator.");
                }
            }
            var wound = args.Count > 1
                ? target.Wounds.FirstOrDefault(w => w.FaceUp && w.CardId == args[1])
                : target.Wounds.FirstOrDefault(w => w.FaceUp);
            if (wound == null)
            {
                throw new InvalidOperationException($"{target.DefId} has no face-up Wound to flip.");
            }
            BestEffortReverseWound(target, wound);
            FlipWoundFaceDown(target, wound);
            Log("item", $"{user.DefId} used Medkit to flip {target.DefId}'s {wound.CardId} face-down");
        }

        /// <summary>Per the rulebook, flipping a Wound face-down with a Medkit must first
        /// undo that Wound's negative effect. The Wound deck itself is not implemented yet
        /// (Game.EffectDispatch.cs's WoundsResolveFaceUp hook has no implementation anywhere),
        /// so nothing has actually applied any Wound's effect in the first place; this method
        /// documents, per card, what "undo" would mean once that deck exists. Discharge,
        /// Fear, Fumble, and Spasm (the four Game.Effects.cs calls out by name for this hook)
        /// each explicitly print "you do not regain/get X back if this Wound is flipped
        /// face-down" - so for those four the correct reversal is truly a no-op, not a gap.</summary>
        private void BestEffortReverseWound(InvestigatorState inv, WoundInstance wound)
        {
            switch (wound.CardId)
            {
                case "discharge":
                case "fear":
                case "fumble":
                case "spasm":
                    break; // no-op by design; see the doc comment above
                default:
                    Log("todo", $"medkit: reverse {wound.CardId}'s face-up effect before flipping it down (Wound deck effects are not implemented yet)");
                    break;
            }
        }

        // ---------- Card dispatch ----------

        private void ApplyItemEffect(InvestigatorState inv, CardDef card, List<string> args)
        {
            switch (card.Id)
            {
                // ----- Medical -----
                case "medkit":
                    UseMedkit(inv, args);
                    break;

                // ----- General items (base) -----
                case "adrenaline-shot":
                {
                    if (inv.SprintedOrRested)
                    {
                        throw new InvalidOperationException("Sprint or Rest may be used once per turn.");
                    }
                    SpendStamina(inv, 1);
                    inv.SprintedOrRested = true;
                    inv.MpRemaining += 4;
                    Log("item", $"{inv.DefId} used Adrenaline Shot for a guaranteed 4 MP Sprint");
                    break;
                }
                case "blueprints":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Blueprints: choose a Point of Interest space.");
                    }
                    if (Graph.Space(args[0]).Kind != SpaceKind.PointOfInterest)
                    {
                        throw new InvalidOperationException($"'{args[0]}' is not a Point of Interest space.");
                    }
                    var within = Graph.DistancesFrom(args[0], 2, State.Overlay).Keys.ToHashSet();
                    int revealed = State.PoiTokens.Count(p => !p.Revealed && within.Contains(p.TokenSpace));
                    foreach (var poi in State.PoiTokens.Where(p => !p.Revealed && within.Contains(p.TokenSpace)))
                    {
                        poi.Revealed = true;
                    }
                    Log("item", $"{inv.DefId} used Blueprints, revealing {revealed} Point of Interest token(s)");
                    break;
                }
                case "cross":
                    // "The Adversary may not use any of their Ability cards during their next
                    // turn. They may still use Attacks and Core Actions." Their next turn is
                    // this round's Adversary turn; PlayAdversaryCard reads the latch.
                    State.Adversary.Counters[AbilitiesBlockedRoundKey] = State.Round;
                    Log("item", $"{inv.DefId} raised the Cross: no Adversary Ability cards this round");
                    break;
                case "dirty-rag":
                    Log("todo", "dirty-rag: no Map Hazard system exists yet to neutralize");
                    break;
                case "emergency-flare":
                {
                    if (args.Count < 2)
                    {
                        throw new InvalidOperationException("Emergency Flare: choose an adjacent space and an angle (radians).");
                    }
                    string space = args[0];
                    double angle = double.Parse(args[1], CultureInfo.InvariantCulture);
                    if (Graph.Edge(inv.Space, space) == null)
                    {
                        throw new InvalidOperationException("Emergency Flare must be placed on an adjacent space.");
                    }
                    var bright = _beam.ComputeBright(Graph, space, angle, _losBlocker);
                    State.Flashlights.Add(new FlashlightPlacement
                    {
                        InvestigatorId = inv.DefId,
                        Space = space,
                        AngleRadians = angle,
                        BrightSpaces = bright.OrderBy(s => s).ToList(),
                    });
                    State.Overlay.BrightSpaces.UnionWith(bright);
                    Log("item", $"{inv.DefId} used Emergency Flare at {space}, lighting {bright.Count} space(s)");
                    RevealOnBright(bright);
                    OnFlashlightPlaced(inv);
                    break;
                }
                case "energy-bar":
                    GainStamina(inv, 2);
                    Log("item", $"{inv.DefId} ate an Energy Bar");
                    break;
                case "energy-drink":
                    // "You may take the Move Action twice this turn." Movement spends from one
                    // shared MP pool rather than per-Action, so a second Move Action has exactly
                    // one observable effect: it lifts a movement lock, the only thing that can
                    // stop an Investigator Moving again on the same turn.
                    if (inv.MovementLocked)
                    {
                        inv.MovementLocked = false;
                        Log("item", $"{inv.DefId} used Energy Drink: they may Move again this turn");
                    }
                    else
                    {
                        Log("item", $"{inv.DefId} used Energy Drink; nothing was stopping them Moving, so the shared MP pool is unchanged");
                    }
                    break;
                case "firecrackers":
                {
                    // "Select a space within 3 spaces of your current position. All Adversary
                    // figures must immediately Move 2 spaces towards that space." A forced move
                    // outside the Adversary's own turn, so it spends no MP and none of their
                    // Actions; a figure pulled into the light is Revealed as usual.
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Firecrackers: choose a space within 3 spaces of you.");
                    }
                    string bang = args[0];
                    if (!Graph.DistancesFrom(inv.Space, 3, State.Overlay).ContainsKey(bang))
                    {
                        throw new InvalidOperationException("Firecrackers: the space must be within 3 spaces of you.");
                    }
                    var adv = State.Adversary;
                    string dragged = StepFigureTowards(adv.Space, bang, 2);
                    if (dragged != adv.Space)
                    {
                        adv.Space = dragged;
                        if (!adv.Revealed && IsBright(dragged))
                        {
                            RevealAdversary("dragged into the light by Firecrackers");
                        }
                        // Investigator-hidden unless the drag just Revealed him (checked above,
                        // Revealed is already updated): otherwise this names exactly where the
                        // still-Hidden main figure now stands.
                        Log(adv.Revealed ? "adversary" : AdversaryHiddenPositionLogType,
                            $"Firecrackers drag the Adversary to {dragged}");
                    }
                    foreach (var figure in adv.Figures.Where(f => f.Alive))
                    {
                        string next = StepFigureTowards(figure.Space, bang, 2);
                        if (next == figure.Space)
                        {
                            continue;
                        }
                        figure.Space = next;
                        if (!figure.Revealed && IsBright(next))
                        {
                            figure.Revealed = true;
                            DropShadowToken(figure.Id);
                            Log("reveal", $"{figure.Id} at {next} (caught in the light)");
                        }
                        // Same reasoning as the main figure above: only public once Revealed.
                        Log(figure.Revealed ? "adversary" : AdversaryHiddenPositionLogType,
                            $"Firecrackers drag {figure.Id} to {next}");
                    }
                    Log("item", $"{inv.DefId} set off Firecrackers at {bang}");
                    break;
                }
                case "fresh-batteries":
                    GainCharge(inv, 2);
                    Log("item", $"{inv.DefId} used Fresh Batteries");
                    break;
                case "glowstick":
                {
                    string onceKey = $"glowstick-used:{inv.DefId}";
                    if (HasRoundModifier(onceKey))
                    {
                        throw new InvalidOperationException("Glowstick may only be used once per round.");
                    }
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Glowstick: choose a space within 4.");
                    }
                    string space = args[0];
                    if (!Graph.DistancesFrom(inv.Space, 4, State.Overlay).ContainsKey(space))
                    {
                        throw new InvalidOperationException("Glowstick must target a space within 4.");
                    }
                    SetRoundModifier(onceKey, 1);
                    PlaceBoardToken($"glowstick-{inv.DefId}", space);
                    Log("item", $"{inv.DefId} placed a Glowstick at {space}");
                    Log("todo", "glowstick: the Dim-within-2 lighting isn't modeled (BoardOverlay only supports whole-zone Dim, not per-space Dim)");
                    break;
                }
                case "journal":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Journal: choose the Evidence reward to gain.");
                    }
                    string reward = args[0];
                    string? arg = args.Count > 1 ? args[1] : null;
                    string? arg2 = args.Count > 2 ? args[2] : null;
                    State.Objective.EvidenceTurnedIn += 1;
                    GrantReward(inv, reward, arg, arg2);
                    Log("item", $"{inv.DefId} used Journal for an extra '{reward}' reward");
                    break;
                }
                case "kerosene":
                {
                    if (args.Count < 2)
                    {
                        throw new InvalidOperationException("Kerosene: choose 2 spaces adjacent to you and to each other.");
                    }
                    string a = args[0], b = args[1];
                    if (Graph.Edge(inv.Space, a) == null || Graph.Edge(inv.Space, b) == null || Graph.Edge(a, b) == null)
                    {
                        throw new InvalidOperationException("Kerosene: both spaces must be adjacent to you and to each other.");
                    }
                    int n = BoardTokenIds("flame-").Count;
                    PlaceBoardToken($"flame-{n + 1}", a);
                    PlaceBoardToken($"flame-{n + 2}", b);
                    Log("item", $"{inv.DefId} used Kerosene at {a} and {b}");
                    Log("todo", "kerosene: the Adversary being blocked from moving onto Flame tokens isn't enforced (Adversary movement can't consult Item tokens)");
                    break;
                }
                case "lantern":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Lantern: choose an adjacent space.");
                    }
                    string space = args[0];
                    if (Graph.Edge(inv.Space, space) == null)
                    {
                        throw new InvalidOperationException("Lantern must be placed on an adjacent space.");
                    }
                    PlaceBoardToken($"lantern-{inv.DefId}", space);
                    var lit = Graph.DistancesFrom(space, 1, State.Overlay).Keys.ToList();
                    State.Overlay.BrightSpaces.UnionWith(lit);
                    Log("item", $"{inv.DefId} placed a Lantern at {space}, lighting {lit.Count} space(s)");
                    RevealOnBright(lit);
                    break;
                }
                case "leather-jacket":
                {
                    var wound = args.Count > 0
                        ? inv.Wounds.FirstOrDefault(w => w.FaceUp && w.CardId == args[0])
                        : inv.Wounds.FirstOrDefault(w => w.FaceUp);
                    if (wound == null)
                    {
                        throw new InvalidOperationException("Leather Jacket requires a face-up Wound to convert.");
                    }
                    FlipWoundFaceDown(inv, wound);
                    Log("item", $"{inv.DefId} used Leather Jacket on {wound.CardId}");
                    Log("todo", "leather-jacket: rules-correct timing is at the moment a face-up Wound is gained; here it retroactively flips an existing one");
                    break;
                }
                case "lucky-dice":
                    // "You may re-roll any of your dice rolls and choose which roll to keep."
                    // ModifySprintRoll is the one reroll interception point the engine has, so
                    // the Supply arms a Sprint reroll; keeping the better of the two rolls is
                    // what "choose" always comes to for a Sprint.
                    AddMarker(inv, "lucky-dice-reroll");
                    Log("item", $"{inv.DefId} readies Lucky Dice: their next Sprint roll is rerolled, best of two");
                    break;
                case "makeshift-iv":
                {
                    if (args.Count < 2)
                    {
                        throw new InvalidOperationException("Makeshift IV: give the other Investigator's id and a direction ('give' or 'take').");
                    }
                    var other = Investigator(args[0]);
                    bool adjacent = Graph.Edge(inv.Space, other.Space) != null ||
                                    Graph.DistancesFrom(inv.Space, 1, State.Overlay).ContainsKey(other.Space);
                    if (!adjacent)
                    {
                        throw new InvalidOperationException("Makeshift IV requires an adjacent Investigator.");
                    }
                    var (from, to) = args[1] == "take" ? (other, inv) : (inv, other);
                    if (from.Stamina < 1)
                    {
                        throw new InvalidOperationException($"{from.DefId} has no Stamina to give.");
                    }
                    SetStaminaDirect(from, from.Stamina - 1);
                    SetStaminaDirect(to, to.Stamina + 1);
                    Log("item", $"{inv.DefId} used Makeshift IV ({from.DefId} -> {to.DefId})");
                    break;
                }
                case "motion-detector":
                    Log("todo", "motion-detector: the engine has no hidden-information model for the Adversary's Sprint roll, so there is nothing to reveal");
                    break;
                case "painkillers":
                {
                    // Designer ruling: draw 2 Wound cards; the player sees them, then MAY
                    // replace one of their face-up Wounds with one of the drawn cards
                    // (wound count unchanged); everything undrafted is discarded. Two-step:
                    // the draw parks a pending marker, ResolvePainkillers completes it.
                    string drawn1 = DrawWound();
                    string drawn2 = DrawWound();
                    inv.Items.Add($"marker:painkillers:{drawn1}:{drawn2}");
                    Log("item", $"{inv.DefId} used Painkillers: drew {drawn1}, {drawn2}");
                    break;
                }
                case "rabbits-foot":
                {
                    if (!inv.Dead)
                    {
                        Log("todo", "rabbits-foot: only has an effect at the moment a Wound would kill you; call this right after that happens");
                        break;
                    }
                    int roll = _rng.Roll(6);
                    SaveRng();
                    if (roll >= 5)
                    {
                        inv.Dead = false;
                        State.Adversary.Kills = Math.Max(0, State.Adversary.Kills - 1);
                        if (State.Phase == GamePhase.GameOver && State.Result == GameResult.AdversaryWins &&
                            State.Adversary.Kills < State.Adversary.KillsToWin)
                        {
                            State.Phase = GamePhase.InvestigatorTurns;
                            State.Result = GameResult.Undecided;
                        }
                        AddMarker(inv, "rabbits-foot-active");
                        Log("item", $"{inv.DefId} survives the killing blow with Rabbit's Foot (rolled {roll})");
                    }
                    else
                    {
                        Log("item", $"{inv.DefId}'s Rabbit's Foot fails (rolled {roll}); the death stands");
                    }
                    Log("todo", "rabbits-foot: dying immediately if Attacked again isn't enforced (Attack resolution can't consult Item state)");
                    break;
                }
                case "security-bar":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Security Bar: choose an adjacent Door space.");
                    }
                    string door = args[0];
                    if (Graph.Edge(inv.Space, door) == null)
                    {
                        throw new InvalidOperationException("Security Bar must go on an adjacent Door space.");
                    }
                    var doorState = State.Overlay.DoorState(door);
                    if (doorState != DoorState.Locked && doorState != DoorState.Damaged)
                    {
                        throw new InvalidOperationException("Security Bar requires a Locked or Damaged Door.");
                    }
                    int n = BoardTokenIds("security-bar-").Count;
                    PlaceBoardToken($"security-bar-{n + 1}", door);
                    Log("item", $"{inv.DefId} placed a Security Bar on {door}");
                    Log("todo", "security-bar: consuming the token instead of the Door token on the Adversary's next Break Door isn't enforced (AdversaryBreakDoor can't consult Item tokens)");
                    break;
                }
                case "sedatives":
                    RemoveMarker(inv, "obstruction:stamina");
                    RemoveMarker(inv, "obstruction:charge");
                    SetStaminaDirect(inv, Db.Investigator(inv.DefId).StaminaTrack.Spaces - 1);
                    Log("item", $"{inv.DefId} used Sedatives");
                    Log("todo", "sedatives: skipping your next turn isn't enforced (BeginInvestigatorTurn has no lockout hook)");
                    break;
                case "survival-kit":
                    Log("todo", "survival-kit: Event effects are implemented now, but they are applied once, for everyone, " +
                                "as RoundModifiers when the card is drawn; ignoring one for a single Investigator needs " +
                                "those modifiers scoped per Investigator first");
                    break;
                case "torch":
                {
                    string? zone = Graph.Space(inv.Space).Zone;
                    if (zone == null)
                    {
                        throw new InvalidOperationException("Torch requires you to be in a Zone.");
                    }
                    State.BoardTokens[$"torch:{inv.DefId}"] = zone;
                    State.Overlay.DimZones.Add(zone);
                    Log("item", $"{inv.DefId} placed a Torch, making zone {zone} Dim for the round");
                    break;
                }
                case "tourniquet":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Tourniquet: choose the face-up Wound to ignore.");
                    }
                    var wound = inv.Wounds.FirstOrDefault(w => w.FaceUp && w.CardId == args[0]);
                    if (wound == null)
                    {
                        throw new InvalidOperationException($"{inv.DefId} does not have a face-up '{args[0]}' Wound.");
                    }
                    SetRoundModifier($"tourniquet-ignored:{inv.DefId}:{wound.CardId}", 1);
                    Log("item", $"{inv.DefId} used Tourniquet to ignore {wound.CardId} this round");
                    Log("todo", "tourniquet: no Wound-effect system exists yet to actually suppress, so the flag above is inert bookkeeping");
                    break;
                }
                case "two-way-radio":
                    // "You may use the Minor Ability of an Investigator that is not in play. If
                    // the Ability uses tokens, you may use 1 of them but cannot keep the rest."
                    // args: [that Investigator's def id, ...that Ability's own arguments].
                    UseBorrowedMinorAbility(inv, args);
                    break;
                case "whiskey":
                {
                    int roll = _rng.Roll(6);
                    SaveRng();
                    switch (roll)
                    {
                        case 1:
                        case 2:
                            Log("todo", "whiskey: decrease footprint by 1 next turn (no footprint stat on InvestigatorState)");
                            break;
                        case 3:
                        case 4:
                            if (args.Count > 0 && args[0] == "charge")
                            {
                                GainCharge(inv, 1);
                            }
                            else
                            {
                                GainStamina(inv, 1);
                            }
                            break;
                        default:
                            GainStamina(inv, 1);
                            Log("todo", "whiskey: increase footprint by 1 next turn (no footprint stat on InvestigatorState)");
                            break;
                    }
                    Log("item", $"{inv.DefId} drank Whiskey (rolled {roll})");
                    break;
                }
                case "crystal-amulet":
                {
                    int visible = Math.Min(3, Math.Max(0, State.EventDeck.Count - 1));
                    if (args.Count != visible)
                    {
                        throw new InvalidOperationException($"Crystal Amulet: provide the top {visible} Event card id(s) in the new order.");
                    }
                    var current = State.EventDeck.Take(visible).ToList();
                    if (!args.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(current.OrderBy(x => x, StringComparer.Ordinal)))
                    {
                        throw new InvalidOperationException("Crystal Amulet: the ids given must match the top cards exactly (any order).");
                    }
                    for (int i = 0; i < visible; i++)
                    {
                        State.EventDeck[i] = args[i];
                    }
                    Log("item", $"{inv.DefId} used Crystal Amulet to rearrange the top {visible} Event card(s)");
                    break;
                }
                case "spare-tools":
                    // "Make an Involved Action count as an Interact Action instead. This does not
                    // end your turn, but you may not take another Involved Action this turn."
                    // Every Involved Action funnels through Game.EndTurn, which consumes this
                    // modifier instead of ending the turn.
                    if (HasRoundModifier(InvolvedActionUsedPrefix + inv.DefId))
                    {
                        throw new InvalidOperationException($"{inv.DefId} has already taken an Involved Action this turn.");
                    }
                    SetRoundModifier(InvolvedAsInteractPrefix + inv.DefId, 1);
                    Log("item", $"{inv.DefId} used Spare Tools: their next Involved Action counts as an Interact");
                    break;
                case "stray-mutt":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Stray Mutt: choose the token's space.");
                    }
                    string target = args[0];
                    if (!Graph.DistancesFrom(inv.Space, 3, State.Overlay).ContainsKey(target))
                    {
                        throw new InvalidOperationException("Stray Mutt: the token must be within 3 spaces.");
                    }
                    bool isPoi = State.PoiTokens.Any(p => p.TokenSpace == target && p.Revealed && !p.Collected);
                    bool isMedical = State.MedicalItemSpaces.Contains(target);
                    if (!isPoi && !isMedical)
                    {
                        throw new InvalidOperationException("No revealed Point of Interest or Medical Item token there.");
                    }
                    string original = inv.Space;
                    inv.Space = target;
                    try
                    {
                        if (isPoi)
                        {
                            PickUpPoiToken();
                        }
                        else
                        {
                            PickUpMedicalItem();
                        }
                    }
                    finally
                    {
                        inv.Space = original;
                    }
                    Log("item", $"{inv.DefId} used Stray Mutt to grab a token at {target}");
                    break;
                }
                case "tripod":
                {
                    RequireNoFinalAction(inv);
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Tripod: choose the Flashlight angle (radians).");
                    }
                    double angle = double.Parse(args[0], CultureInfo.InvariantCulture);
                    // Same Flashlight, same price and same beam limits as the Final Action —
                    // Tripod only changes whether the turn ends.
                    RequireActionAllowed(inv, ActionPlaceFlashlight);
                    PayFlashlightCharge(inv);
                    inv.FinalAction = FinalActionKind.PlaceFlashlight;
                    var bright = PreviewFlashlight(inv.DefId, angle);
                    TrimFlashlightBright(inv, angle, bright);
                    State.Flashlights.Add(new FlashlightPlacement
                    {
                        InvestigatorId = inv.DefId,
                        Space = inv.Space,
                        AngleRadians = angle,
                        BrightSpaces = bright.OrderBy(s => s).ToList(),
                    });
                    State.Overlay.BrightSpaces.UnionWith(bright);
                    Log("item", $"{inv.DefId} used Tripod to place the Flashlight without ending their turn ({bright.Count} space(s) lit)");
                    RevealOnBright(bright);
                    OnFlashlightPlaced(inv);
                    break;
                }
                case "whistle":
                {
                    var distToMe = Graph.DistancesFrom(inv.Space, 999, State.Overlay);
                    foreach (string targetId in args.Take(2))
                    {
                        var target = Investigator(targetId);
                        for (int step = 0; step < 2; step++)
                        {
                            if (!distToMe.TryGetValue(target.Space, out int curDist) || curDist == 0)
                            {
                                break;
                            }
                            string? closer = Graph.DistancesFrom(target.Space, 1, State.Overlay).Keys
                                .Where(n => n != target.Space && distToMe.TryGetValue(n, out int nd) && nd < curDist)
                                .OrderBy(n => distToMe[n])
                                .FirstOrDefault();
                            if (closer == null)
                            {
                                break;
                            }
                            target.Space = closer;
                        }
                    }
                    Log("item", $"{inv.DefId} used Whistle to pull Investigators closer");
                    Log("todo", "whistle: treating Dark spaces as Dim for this movement isn't distinguished (the distance metric already ignores light level)");
                    break;
                }
                case "ghillie-suit":
                    Log("todo", "ghillie-suit: removing the Investigator from the board and returning them at end of round isn't modeled (risks corrupting other space-based invariants)");
                    break;
                case "metal-detector":
                {
                    var within = Graph.DistancesFrom(inv.Space, 2, State.Overlay).Keys.ToHashSet();
                    int revealed = State.Evidence.Values.Count(t => !t.Revealed && within.Contains(t.Space)) +
                                   State.PoiTokens.Count(p => !p.Revealed && within.Contains(p.TokenSpace));
                    foreach (var token in State.Evidence.Values.Where(t => !t.Revealed && within.Contains(t.Space)))
                    {
                        token.Revealed = true;
                    }
                    foreach (var poi in State.PoiTokens.Where(p => !p.Revealed && within.Contains(p.TokenSpace)))
                    {
                        poi.Revealed = true;
                    }
                    Log("item", $"{inv.DefId} used Metal Detector, revealing {revealed} hidden token(s)");
                    break;
                }
                case "spare-batteries":
                    // "You may spend a Supply token from this card instead of spending a Charge
                    // when using a Flashlight." Game.PlaceFlashlight consumes the waiver while it
                    // works out what the placement costs, so no Charge changes hands at all. A
                    // marker, not a round modifier: spending the Supply after this turn's
                    // placement must still pay for the NEXT one (playtest 2026-08-31).
                    AddMarker(inv, FlashlightChargeWaiverMarker);
                    Log("item", $"{inv.DefId} spent a Spare Batteries Supply: their next Flashlight placement costs 1 less Charge");
                    break;

                // ----- General items (MI) -----
                case "adrenaline-shot-mi":
                {
                    if (inv.SprintedOrRested)
                    {
                        throw new InvalidOperationException("Sprint or Rest may be used once per turn.");
                    }
                    SpendStamina(inv, 1);
                    int a = _rng.Roll(6), b = _rng.Roll(6);
                    SaveRng();
                    int best = Math.Max(a, b);
                    inv.SprintedOrRested = true;
                    inv.MpRemaining += best;
                    Log("item", $"{inv.DefId} used Adrenaline Shot (MI), rolled {a} and {b}, took {best} MP");
                    break;
                }
                case "cross-mi":
                    Log("todo", "cross-mi: forcing a Noise token on every Door the Adversary moves onto isn't enforced (Adversary movement can't consult Item state)");
                    break;
                case "dirty-rag-mi":
                    Log("todo", "dirty-rag-mi: no Map Hazard system exists yet to ignore");
                    break;
                case "lantern-mi":
                {
                    string onceKey = $"lantern-mi-used:{inv.DefId}";
                    if (HasRoundModifier(onceKey))
                    {
                        throw new InvalidOperationException("Lantern may only be used once per round.");
                    }
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Lantern: choose an adjacent space.");
                    }
                    string space = args[0];
                    if (Graph.Edge(inv.Space, space) == null)
                    {
                        throw new InvalidOperationException("Lantern must be placed on an adjacent space.");
                    }
                    SetRoundModifier(onceKey, 1);
                    PlaceBoardToken($"lantern-{inv.DefId}", space);
                    State.Overlay.BrightSpaces.Add(space);
                    Log("item", $"{inv.DefId} placed a Lantern (MI) at {space} (Bright)");
                    RevealOnBright(new[] { space });
                    Log("todo", "lantern-mi: the surrounding Dim-within-3 radius isn't modeled (BoardOverlay only supports whole-zone Dim, not per-space Dim)");
                    break;
                }
                case "rabbits-foot-mi":
                    if (State.EventDeck.Count >= 2)
                    {
                        (State.EventDeck[0], State.EventDeck[1]) = (State.EventDeck[1], State.EventDeck[0]);
                        Log("item", $"{inv.DefId} used Rabbit's Foot (MI) to swap the top 2 Event cards");
                    }
                    else
                    {
                        Log("item", $"{inv.DefId} used Rabbit's Foot (MI) (fewer than 2 Event cards left; nothing to swap)");
                    }
                    Log("todo", "rabbits-foot-mi: replacing a card with one from the discard pile isn't tracked (GameState has no Event discard pile)");
                    break;

                // ----- Cursed items -----
                case "binding-tablet":
                    if (inv.Stamina < 4)
                    {
                        throw new InvalidOperationException("Binding Tablet requires at least 4 Stamina.");
                    }
                    LoseStamina(inv, 4);
                    Log("item", $"{inv.DefId} used Binding Tablet");
                    Log("todo", "binding-tablet: restricting the Adversary to only the Move Action next turn isn't enforced by Attack/Ability resolution");
                    break;
                case "blood-chalice":
                    // "Gain a face-up Wound to use any other Investigator's Major Ability
                    // without using a Major Ability token ... You may choose Investigators that
                    // are not in play." args: [that Investigator's def id, ...their Ability's
                    // own arguments]. The Wound is the price, so it is paid first — but only
                    // once the borrowed Ability itself has been accepted, so a refused Ability
                    // leaves the drinker unharmed.
                    UseBorrowedMajorAbility(inv, args);
                    GainWound(inv, faceUp: true);
                    Log("item", $"{inv.DefId} used Blood Chalice");
                    Log("todo", "blood-chalice: \"You may not Trade this Item after using it\" isn't enforced " +
                                "(TradeItem has no per-card lock, only the per-Investigator ActionTrade gate)");
                    break;
                case "cursed-poppet":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Cursed Poppet: choose another Investigator to hold the Poppet token.");
                    }
                    var target = Investigator(args[0]);
                    if (target == inv)
                    {
                        throw new InvalidOperationException("The Poppet token must go to another Investigator.");
                    }
                    SetStaminaDirect(inv, 0);
                    AddMarker(target, $"poppet-owner:{inv.DefId}");
                    Log("item", $"{inv.DefId} used Cursed Poppet; {target.DefId} now holds the Poppet token");
                    Log("todo", "cursed-poppet: the ongoing Poppet<->owner Stamina/Wound linkage has no reactive hook to observe the holder's changes");
                    break;
                }
                case "diablerie-book":
                    AddMarker(inv, "curse:diablerie-book");
                    Log("item", $"{inv.DefId} used Diablerie Book; a Wound will surface at the start of each future turn");
                    Log("todo", "diablerie-book: permanently increase footprint by 2 (no footprint stat on InvestigatorState)");
                    break;
                case "foul-spell-bag":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Foul Spell Bag: choose another Investigator for the second roll.");
                    }
                    var other = Investigator(args[0]);
                    int roll1 = _rng.Roll(6);
                    int roll2 = _rng.Roll(6);
                    SaveRng();
                    switch (roll1)
                    {
                        case 1:
                        case 2:
                            Log("todo", "foul-spell-bag: permanently increase your footprint by 1 (no footprint stat on InvestigatorState)");
                            break;
                        case 3:
                        case 4:
                            // "Your Flashlight may never drop below 1 Charge for the rest of the
                            // game": a standing block on the Place Flashlight Action whenever
                            // paying for it would empty the Charge track.
                            AddMarker(inv, "flashlight-min-charge");
                            Log("item", $"{inv.DefId}'s Flashlight may never drop below 1 Charge again");
                            break;
                        default:
                            Log("todo", "foul-spell-bag: no longer gain face-down Wounds from Sprinting — GainWound now tags " +
                                        "Sprint Wounds (WoundFromSprint), but the OnWoundGained hook can only change or add " +
                                        "Wounds, not cancel the one being gained");
                            break;
                    }
                    switch (roll2)
                    {
                        case 1:
                        case 2:
                            AddMarker(other, "obstruction:stamina");
                            Log("todo", "foul-spell-bag: the Obstruction token on the highest Stamina space isn't enforced by GainStamina");
                            break;
                        case 3:
                        case 4:
                            AddMarker(other, "obstruction:charge");
                            Log("todo", "foul-spell-bag: the Obstruction token on the highest Charge space isn't enforced");
                            break;
                        default:
                            GainCondition(other, "paranoid");
                            break;
                    }
                    Log("item", $"{inv.DefId} used Foul Spell Bag (rolls {roll1}, {roll2})");
                    break;
                }
                case "hexed-mirror":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Hexed Mirror: choose the Investigator to change places with.");
                    }
                    var target = Investigator(args[0]);
                    (inv.Space, target.Space) = (target.Space, inv.Space);
                    Log("item", $"{inv.DefId} used Hexed Mirror to swap places with {target.DefId}");
                    Log("todo", "hexed-mirror: the automatic 'after another Investigator takes a Wound from the Adversary' trigger isn't observed; call this manually right after that happens");
                    break;
                }
                case "mystery-syringe":
                {
                    int roll = _rng.Roll(6);
                    SaveRng();
                    switch (roll)
                    {
                        case 1:
                            Log("todo", "mystery-syringe: may not Move or Sprint next round isn't enforced (no lockout hook into next round's turn)");
                            break;
                        case 2:
                            Log("todo", "mystery-syringe: may not gain/lose Stamina for 2 rounds isn't enforced (GainStamina/LoseStamina have no gate)");
                            break;
                        case 3:
                        {
                            LoseStamina(inv, 2);
                            var faceUp = inv.Wounds.FirstOrDefault(w => w.FaceUp);
                            if (faceUp != null)
                            {
                                FlipWoundFaceDown(inv, faceUp);
                            }
                            break;
                        }
                        case 4:
                            SetStaminaDirect(inv, Db.Investigator(inv.DefId).StaminaTrack.Spaces - 1);
                            break;
                        case 5:
                            Log("todo", "mystery-syringe: increase footprint by 2 for 2 rounds (no footprint stat on InvestigatorState)");
                            break;
                        default:
                            if (inv.SprintedOrRested)
                            {
                                Log("todo", "mystery-syringe: next-Sprint bonus not applied (Sprint or Rest already used this turn)");
                            }
                            else
                            {
                                SpendStamina(inv, 1);
                                int a2 = _rng.RollSprintDie(Db.Config.SprintDieFaces);
                                int b2 = _rng.RollSprintDie(Db.Config.SprintDieFaces);
                                SaveRng();
                                inv.SprintedOrRested = true;
                                inv.MpRemaining += a2 + b2;
                                Log("item", $"{inv.DefId}'s next Sprint rolled {a2}+{b2}={a2 + b2} MP");
                            }
                            break;
                    }
                    Log("item", $"{inv.DefId} used Mystery Syringe (rolled {roll})");
                    break;
                }
                case "phantom-amulet":
                {
                    if (args.Count < 2)
                    {
                        throw new InvalidOperationException("Phantom Amulet: choose the 2 spaces for the Secret Passage.");
                    }
                    if (!GainCondition(inv, "mauled"))
                    {
                        GainWound(inv, faceUp: false);
                    }
                    else
                    {
                        Graph.Space(args[0]);
                        Graph.Space(args[1]);
                        State.Overlay.SecretPassages.Add(BoardOverlay.EdgeKey(args[0], args[1]));
                    }
                    Log("item", $"{inv.DefId} used Phantom Amulet");
                    break;
                }
                case "summoning-stones":
                {
                    if (args.Count != 2)
                    {
                        throw new InvalidOperationException("Summoning Stones: choose exactly 2 General Item cards.");
                    }
                    foreach (string id in args)
                    {
                        if (!State.GeneralItemDeck.Remove(id))
                        {
                            throw new InvalidOperationException($"'{id}' is not in the General Item deck.");
                        }
                        inv.Items.Add(id);
                    }
                    _rng.Shuffle(State.GeneralItemDeck);
                    SaveRng();
                    foreach (var other in State.Investigators.Where(i => !i.Dead && !i.Escaped))
                    {
                        LoseStamina(other, 1);
                    }
                    Log("item", $"{inv.DefId} used Summoning Stones");
                    break;
                }
                case "lorgnette":
                {
                    if (args.Count < 1)
                    {
                        throw new InvalidOperationException("Lorgnette: choose the Investigator who gains Darkness.");
                    }
                    var target = Investigator(args[0]);
                    var myFlashlight = State.Flashlights.FirstOrDefault(f => f.InvestigatorId == inv.DefId);
                    var spaces = args.Skip(1).Take(3).ToList();
                    foreach (string s in spaces)
                    {
                        if (myFlashlight == null || !myFlashlight.BrightSpaces.Contains(s))
                        {
                            throw new InvalidOperationException($"'{s}' is not touched by {inv.DefId}'s Flashlight.");
                        }
                        State.Overlay.BrightSpaces.Add(s);
                    }
                    GainCondition(target, "darkness");
                    Log("item", $"{inv.DefId} used Lorgnette on {spaces.Count} space(s)");
                    break;
                }
                case "witch-bells":
                {
                    // "Make the Adversary either not use a Sprint die, or place their Shadow token
                    // on the main board where they begin their next turn." args[0] picks:
                    // "sprint" (the default) or "shadow".
                    string choice = args.Count > 0 ? args[0] : "sprint";
                    if (choice != "sprint" && choice != "shadow")
                    {
                        throw new InvalidOperationException("Witch Bells takes \"sprint\" or \"shadow\".");
                    }
                    if (choice == "sprint")
                    {
                        State.Adversary.Counters["skip-sprint-die-round"] = State.Round;
                        Log("item", $"{inv.DefId} rang Witch Bells: no Adversary Sprint die this round");
                    }
                    else
                    {
                        // Every Adversary already drops its Shadow token on its own space as its
                        // turn begins (BeginButcherTurn / BeginHorrorTurn / BeginCultTurn), which
                        // is exactly "where they begin their next turn".
                        Log("item", $"{inv.DefId} rang Witch Bells: the Adversary's Shadow token shows where they begin their next turn");
                    }
                    GainCondition(inv, "darkness");
                    break;
                }
                case "ravens-wing":
                    Log("todo", "ravens-wing: no Map Hazard system exists to move through, so the card has nothing to act " +
                                "on (its Flashlight-lockout price would now ride the 'place-flashlight' action gate)");
                    break;
                case "inscribed-axe":
                    SetStaminaDirect(inv, 0);
                    AddMarker(inv, "obstruction:stamina");
                    Log("item", $"{inv.DefId} used Inscribed Axe");
                    Log("todo", "inscribed-axe: redirecting the Adversary's Attack isn't enforced (invoke this manually right after being Attacked); the Obstruction cap isn't enforced by GainStamina");
                    break;

                default:
                    Log("todo", $"{card.Name}: no scripted effect yet");
                    break;
            }
        }

        // ---------- Items sub-hooks (Game.EffectDispatch.cs) ----------

        partial void ItemsOnTurnStart(InvestigatorState inv)
        {
            if (!HasMarker(inv, "curse:diablerie-book"))
            {
                return;
            }
            var faceDown = inv.Wounds.Where(w => !w.FaceUp).ToList();
            if (faceDown.Count > 0)
            {
                var pick = faceDown[_rng.Next(faceDown.Count)];
                SaveRng();
                FlipWoundFaceUp(inv, pick);
            }
            else
            {
                GainWound(inv, faceUp: true);
            }
        }

        partial void ItemsOnTurnEnd(InvestigatorState inv)
        {
            if (!HasMarker(inv, "rabbits-foot-active"))
            {
                return;
            }
            int roll = _rng.Roll(6);
            SaveRng();
            if (roll >= 5)
            {
                return;
            }
            RemoveMarker(inv, "rabbits-foot-active");
            inv.Dead = true;
            State.Adversary.Kills += 1;
            Log("death", inv.DefId);
            if (State.Adversary.Kills >= State.Adversary.KillsToWin)
            {
                State.Phase = GamePhase.GameOver;
                State.Result = GameResult.AdversaryWins;
            }
            Log("item", $"{inv.DefId}'s Rabbit's Foot luck runs out (rolled {roll})");
        }

        partial void ItemsOnMoveStep(InvestigatorState inv, string from, string to)
        {
            if (BoardTokenIds("flame-").Any(id => State.BoardTokens[id] == to))
            {
                GainWound(inv, faceUp: true);
                Log("item", $"{inv.DefId} stepped into Kerosene flames at {to}");
            }
        }

        partial void ItemsCollectActionBlockers(InvestigatorState inv, string actionKey, List<string> blockers)
        {
            switch (actionKey)
            {
                case ActionInvolved:
                    // Spare Tools: "you may not take another Involved Action this turn."
                    if (HasRoundModifier(InvolvedActionUsedPrefix + inv.DefId))
                    {
                        blockers.Add("Spare Tools: you have already taken an Involved Action this turn");
                    }
                    break;
                case ActionPlaceFlashlight:
                    // Foul Spell Bag: "your Flashlight may never drop below 1 Charge."
                    if (HasMarker(inv, "flashlight-min-charge") &&
                        inv.Charge - (1 + Math.Max(0, RoundModifier(FlashlightChargeSurchargeKey))) < 1 &&
                        !HasMarker(inv, FlashlightChargeWaiverMarker))
                    {
                        blockers.Add("Foul Spell Bag: your Flashlight may never drop below 1 Charge");
                    }
                    break;
            }
        }

        partial void ItemsModifySprintRoll(InvestigatorState inv, List<int> rollBox)
        {
            if (!HasMarker(inv, "lucky-dice-reroll"))
            {
                return;
            }
            RemoveMarker(inv, "lucky-dice-reroll");
            int reroll = _rng.RollSprintDie(Db.Config.SprintDieFaces);
            SaveRng();
            Log("item", $"{inv.DefId}'s Lucky Dice reroll: {rollBox[0]} vs {reroll}");
            rollBox[0] = Math.Max(rollBox[0], reroll);
        }

        /// <summary>
        /// Walk a figure up to <paramref name="steps"/> spaces along the shortest route toward
        /// <paramref name="target"/>, stopping early when nothing gets closer. Used by the
        /// cards that shove figures around outside their own turn (Firecrackers, Whistle).
        /// </summary>
        private string StepFigureTowards(string from, string target, int steps)
        {
            var toTarget = Graph.DistancesFrom(target, int.MaxValue, State.Overlay);
            string current = from;
            for (int i = 0; i < steps; i++)
            {
                if (!toTarget.TryGetValue(current, out int distance) || distance == 0)
                {
                    break;
                }
                string? closer = Graph.DistancesFrom(current, 1, State.Overlay).Keys
                    .Where(n => n != current && toTarget.TryGetValue(n, out int nd) && nd < distance)
                    .OrderBy(n => toTarget[n])
                    .ThenBy(n => n, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (closer == null)
                {
                    break;
                }
                current = closer;
            }
            return current;
        }

        partial void ItemsOnRoundEnd()
        {
            RemoveBoardTokens("lantern-");
            RemoveBoardTokens("glowstick-");
            RemoveBoardTokens("flame-");
            foreach (string key in BoardTokenIds("torch:"))
            {
                State.Overlay.DimZones.Remove(State.BoardTokens[key]);
            }
            RemoveBoardTokens("torch:");
        }
    }
}
