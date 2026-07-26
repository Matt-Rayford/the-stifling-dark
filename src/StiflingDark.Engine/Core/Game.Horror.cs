using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// The Insatiable Horror: Ambush (and its Enraged replacement), the 9 playable
    /// Attack/Ability cards from game-data/cards/adversary-cards.json, and the Eggs
    /// Banish objective from game-data/cards/escape-cards.json / player-aids.json.
    ///
    /// Several card effects reference systems the engine does not have yet (Hatchling AI,
    /// movement-blocking Mucus/Tunnel tokens, per-turn Charge hooks, an adversary-owned
    /// move/turn-end sub-hook). Those are logged as "todo" events rather than silently
    /// dropped; see the accompanying report for the full list. Everything the current state
    /// model *can* represent (movement, Wounds, Conditions via
    /// <see cref="GrantConditionWithSubstitution"/>, card tokens via
    /// <see cref="PlaceBoardToken"/>, Shadow token, Revealed, distance/adjacency validation)
    /// is applied for real.
    /// </summary>
    public sealed partial class Game
    {
        partial void BeginHorrorTurn()
        {
            var adv = State.Adversary;
            adv.ShadowTokens["main"] = adv.Space;

            // Devour grants a one-turn window; it expires if unused.
            adv.Counters.Remove("devour-active");
            if (adv.Counters.Remove("devour-next-turn"))
            {
                adv.Counters["devour-active"] = 1;
            }

            if (adv.Counters.TryGetValue("enraged", out int enraged) && enraged == 1 &&
                !adv.Counters.ContainsKey("enraged-move-todo-logged"))
            {
                adv.Counters["enraged-move-todo-logged"] = 1;
                Log("todo", "Enraged movement (4 MP flat, no Sprint die, Bright spaces cost 2 MP) is not " +
                             "enforced: EnsureAdversaryTurnStarted computes MP the same way for every adversary.");
            }
        }

        partial void ApplyHorrorCard(string cardId, List<string> targets)
        {
            switch (cardId)
            {
                case "bufotoxin":
                    ApplyMauledAttack(targets, "bufotoxin");
                    break;
                case "neurotoxin":
                    ApplyMauledAttack(targets, "neurotoxin");
                    break;
                case "gastric-secretions":
                    ApplyGastricSecretions(targets);
                    break;
                case "devour":
                    ApplyDevour();
                    break;
                case "fuming-fissure":
                    ApplyFumingFissure();
                    break;
                case "occluded-lights":
                    ApplyOccludedLights(targets);
                    break;
                case "projectile-adhesive":
                    ApplyProjectileAdhesive();
                    break;
                case "thick-mucus":
                    ApplyThickMucus(targets);
                    break;
                case "tunnel":
                    ApplyTunnel(targets);
                    break;
                default:
                    throw new InvalidOperationException($"'{cardId}' is not a playable Insatiable Horror card.");
            }
        }

        partial void SetupEggsBanish()
        {
            EnsureEggsSetup();
            _banishSetupDone = true;
        }

        // ---------- Ambush ----------

        /// <summary>
        /// Start-of-turn only, only while Hidden, never in round 1: pull any number of
        /// Investigators within 5 (Bright-doubled) spaces onto empty spaces adjacent to
        /// the Horror. Afterward the Attack card may hit any adjacent Investigator.
        /// </summary>
        public void HorrorAmbush(Dictionary<string, string> investigatorToSpace)
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            if (adv.Counters.TryGetValue("enraged", out int enraged) && enraged == 1)
            {
                throw new InvalidOperationException("The Horror is Enraged and can no longer Ambush.");
            }
            if (State.Round == 1)
            {
                throw new InvalidOperationException("The Horror cannot Ambush during round 1.");
            }
            if (adv.Revealed)
            {
                throw new InvalidOperationException("Ambush requires The Horror to be Hidden.");
            }
            if (adv.ActionsUsed.Count != 0)
            {
                throw new InvalidOperationException("Ambush is only available as the first action of The Horror's turn.");
            }
            GatherInvestigators(investigatorToSpace, maxRange: 5, weighted: true);
            adv.ActionsUsed.Add("ambush");
            Log("adversary", $"Ambush: pulled {investigatorToSpace.Count} investigator(s)");
        }

        /// <summary>
        /// The Enraged replacement for Ambush: once per turn, pull Investigators within a
        /// flat 2 spaces (no Bright penalty), then the Attack card may hit adjacent
        /// Investigators exactly as after a normal Ambush.
        /// </summary>
        public void EnragedGather(Dictionary<string, string> investigatorToSpace)
        {
            EnsureAdversaryTurnStarted();
            var adv = State.Adversary;
            if (!(adv.Counters.TryGetValue("enraged", out int enraged) && enraged == 1))
            {
                throw new InvalidOperationException("This action requires the Enraged Condition.");
            }
            if (!adv.ActionsUsed.Add("ambush"))
            {
                throw new InvalidOperationException("This has already been used this turn.");
            }
            GatherInvestigators(investigatorToSpace, maxRange: 2, weighted: false);
            Log("adversary", $"Enraged: gathered {investigatorToSpace.Count} investigator(s)");
        }

        /// <summary>Validates every pull before moving anyone, then applies the moves.</summary>
        private void GatherInvestigators(Dictionary<string, string> investigatorToSpace, int maxRange, bool weighted)
        {
            var adv = State.Adversary;
            Dictionary<string, int> dist = weighted
                ? WeightedDistancesFrom(adv.Space, maxRange)
                : Graph.DistancesFrom(adv.Space, maxRange, State.Overlay);
            var occupied = OccupiedSpaces();
            var moves = new List<Tuple<InvestigatorState, string, string>>();
            foreach (var pair in investigatorToSpace)
            {
                string invId = pair.Key;
                string destSpace = pair.Value;
                var inv = Investigator(invId);
                if (inv.Dead || inv.Escaped)
                {
                    throw new InvalidOperationException($"{invId} cannot be moved.");
                }
                if (!dist.ContainsKey(inv.Space))
                {
                    throw new InvalidOperationException($"{invId} is not within {maxRange} spaces of The Horror.");
                }
                if (!AdversaryAdjacentTo(adv.Space, destSpace))
                {
                    throw new InvalidOperationException($"'{destSpace}' is not adjacent to The Horror.");
                }
                if (occupied.Contains(destSpace) && inv.Space != destSpace)
                {
                    throw new InvalidOperationException($"'{destSpace}' is occupied.");
                }
                occupied.Remove(inv.Space);
                occupied.Add(destSpace);
                moves.Add(Tuple.Create(inv, inv.Space, destSpace));
            }
            foreach (var move in moves)
            {
                move.Item1.Space = move.Item3;
                RemoveFlashlightIfForcedMove(move.Item1.DefId);
                Log("adversary", $"{move.Item1.DefId} pulled from {move.Item2} to {move.Item3}");
            }
        }

        // ---------- Attacks ----------

        /// <summary>Shared gate for the 3 Attack cards: requires an Ambush this turn (or an
        /// active Devour window for a single target), forbids Attacking after Disappearing,
        /// and requires every target to be currently adjacent.</summary>
        private void ApplyAttack(List<string> targets, Action<InvestigatorState> effect)
        {
            if (targets.Count == 0)
            {
                throw new InvalidOperationException("Select at least 1 Investigator to Attack.");
            }
            var adv = State.Adversary;
            if (adv.ActionsUsed.Contains("disappear"))
            {
                throw new InvalidOperationException("The Horror cannot Attack this turn after Disappearing.");
            }
            bool ambushed = adv.ActionsUsed.Contains("ambush");
            bool devourReady = adv.Counters.TryGetValue("devour-active", out int dv) && dv == 1;
            if (!ambushed && !devourReady)
            {
                throw new InvalidOperationException("The Horror may not use its Attack card without Ambushing first.");
            }
            if (!ambushed && devourReady && targets.Count != 1)
            {
                throw new InvalidOperationException("Devour's Attack targets exactly 1 Investigator.");
            }
            var investigators = targets.Select(id => Investigator(id)).ToList();
            foreach (var inv in investigators)
            {
                if (!AdversaryAdjacentTo(adv.Space, inv.Space))
                {
                    throw new InvalidOperationException($"{inv.DefId} is not adjacent to The Horror.");
                }
            }
            foreach (var inv in investigators)
            {
                effect(inv);
            }
            if (!ambushed && devourReady)
            {
                adv.Counters.Remove("devour-active");
                adv.ShadowTokens["main"] = adv.Space;
                Log("adversary", "Devour: attacked without Ambushing");
                Log("todo", "Devour's 'no Move onto a Bright space before attacking' restriction is not enforced.");
            }
        }

        /// <summary>The Bufotoxin/Neurotoxin Attack cards: 1 face-down Wound plus their own
        /// named Condition and Mauled. <paramref name="conditionId"/> is the conditions.json id.</summary>
        private void ApplyMauledAttack(List<string> targets, string conditionId)
        {
            ApplyAttack(targets, inv =>
            {
                DealAttackWounds(inv.DefId, 1, faceUp: false);
                GrantConditionWithSubstitution(inv, conditionId);
                GrantConditionWithSubstitution(inv, "mauled");
            });
        }

        private void ApplyGastricSecretions(List<string> targets)
        {
            var investigatorIds = new HashSet<string>(State.Investigators.Select(i => i.DefId));
            var invTargets = targets.Where(investigatorIds.Contains).ToList();
            string? hatchlingSpace = targets.FirstOrDefault(t => !investigatorIds.Contains(t));

            ApplyAttack(invTargets, inv =>
            {
                DealAttackWounds(inv.DefId, 1, faceUp: false);
                GrantConditionWithSubstitution(inv, "mauled");
            });

            int hatchlingCount = State.Objective.Tokens.Keys.Count(k => k.StartsWith("hatchling-"));
            if (hatchlingCount >= 3)
            {
                Log("adversary", "Gastric Secretions: 3 Hatchlings are already out, no token placed");
                return;
            }
            if (hatchlingSpace == null || !AdversaryAdjacentTo(State.Adversary.Space, hatchlingSpace))
            {
                throw new InvalidOperationException(
                    "Gastric Secretions needs a space adjacent to The Horror for the Hatchling token.");
            }
            string tokenName = "hatchling-" + (hatchlingCount + 1);
            State.Objective.Tokens[tokenName] = hatchlingSpace;
            Log("adversary", $"placed {tokenName} at {hatchlingSpace}");
            Log("todo", "Hatchling movement and its Wound-flip-on-adjacency behavior are not simulated " +
                         "(needs a per-round Hatchling AI hook the engine does not have yet).");
        }

        // ---------- Abilities ----------

        private void ApplyDevour()
        {
            State.Adversary.Counters["devour-next-turn"] = 1;
            Log("adversary", "Devour queued: next turn The Horror may Attack once without Ambushing first");
            Log("todo", "Devour's 'as long as you do not Move onto a Bright space first' restriction is not enforced.");
        }

        private void ApplyFumingFissure()
        {
            State.Adversary.Counters["fuming-fissure-round"] = State.Round + 1;
            Log("adversary", "Fuming Fissure queued for next round");
            Log("todo", "Fuming Fissure's Charge effects (lose 1 Charge in-Zone / +1 Charge cost outside a Zone) " +
                         "are not enforced: they would need hooks in EndTurn/ChargeFlashlight/PlaceFlashlight in Game.cs.");
        }

        /// <summary>Either 2 investigator ids (both get Darkness) or 1 investigator id plus a
        /// zone letter (Darkness + a Zone-wide Bright/Dim block token).</summary>
        private void ApplyOccludedLights(List<string> targets)
        {
            if (targets.Count == 2 && Graph.Def.Zones.ContainsKey(targets[1]))
            {
                var inv = Investigator(targets[0]);
                string zone = targets[1];
                GrantConditionWithSubstitution(inv, "darkness");
                State.Objective.Tokens["occluded-lights"] = zone;
                Log("adversary", $"Occluded Lights token placed on zone {zone}");
                Log("todo", "Occluded Lights' Zone block (no Bright/Dim token next round) is not enforced " +
                             "(needs a hook in ActivateLightSwitch in Game.cs).");
            }
            else if (targets.Count == 2)
            {
                GrantConditionWithSubstitution(Investigator(targets[0]), "darkness");
                GrantConditionWithSubstitution(Investigator(targets[1]), "darkness");
            }
            else
            {
                throw new InvalidOperationException(
                    "Occluded Lights needs either 2 Investigators, or 1 Investigator and a Zone letter.");
            }

            // Cooldown icon is absent; the printed rule delays the move to Cooldown 2 until the
            // end of next round. Approximated here as an immediate move (see report).
            var adv = State.Adversary;
            if (adv.ActiveAbilities.Remove("occluded-lights"))
            {
                adv.Cooldown2.Add(new CooldownCard { CardId = "occluded-lights", FaceUp = false });
                Log("todo", "Occluded Lights' Cooldown 2 move is applied immediately, not at the end of next round.");
            }
        }

        private void ApplyProjectileAdhesive()
        {
            var adv = State.Adversary;
            adv.ShadowTokens["main"] = adv.Space;
            var dist = WeightedDistancesFrom(adv.Space, 5);
            var affected = State.Investigators.Where(i => !i.Dead && !i.Escaped && dist.ContainsKey(i.Space)).ToList();
            foreach (var inv in affected)
            {
                GrantConditionWithSubstitution(inv, "gear-jam");
            }
            Log("adversary", $"Projectile Adhesive: {affected.Count} investigator(s) within range");
        }

        private void ApplyThickMucus(List<string> targets)
        {
            if (targets.Count != 2)
            {
                throw new InvalidOperationException("Thick Mucus needs exactly 2 empty General spaces.");
            }
            var adv = State.Adversary;
            var dist = WeightedDistancesFrom(adv.Space, 5);
            var occupied = OccupiedSpaces();
            foreach (string space in targets)
            {
                if (!dist.ContainsKey(space))
                {
                    throw new InvalidOperationException($"'{space}' is not within 5 spaces of The Horror.");
                }
                if (Graph.Space(space).Kind != SpaceKind.Normal)
                {
                    throw new InvalidOperationException($"'{space}' is not a General space.");
                }
                if (occupied.Contains(space))
                {
                    throw new InvalidOperationException($"'{space}' is occupied.");
                }
            }
            adv.ShadowTokens["main"] = adv.Space;
            PlaceBoardToken("mucus-1", targets[0]);
            PlaceBoardToken("mucus-2", targets[1]);
            adv.Counters["mucus-placed-round"] = State.Round;
            Log("adversary", $"placed 2 Mucus tokens: {targets[0]}, {targets[1]}");
            Log("todo", "Mucus tokens are on the board under BoardTokens[\"mucus-*\"], but they do not block " +
                         "Movement (MapGraph.TryStep takes no card tokens) and are not auto-removed at the end of " +
                         "next round (OnRoundEnd has no adversary sub-hook).");
        }

        private void ApplyTunnel(List<string> targets)
        {
            if (targets.Count != 1)
            {
                throw new InvalidOperationException("Tunnel needs exactly 1 space.");
            }
            string space = targets[0];
            PlaceBoardToken("tunnel-1", space); // validates the space exists
            State.Adversary.Counters["tunnel-placed-round"] = State.Round;
            Log("adversary", $"Tunnel token placed at {space}");
            Log("todo", "the Tunnel token is on the board under BoardTokens[\"tunnel-1\"], but its " +
                         "Move-on-or-adjacent-to-the-token bypass next turn is not enforced (needs an " +
                         "AdversaryMoveStep hook), nor is the token's end-of-next-round removal.");
        }

        // ---------- The Eggs banish objective ----------

        private void EnsureEggsSetup()
        {
            var adv = State.Adversary;
            if (!adv.Counters.ContainsKey("eggsacs-remaining"))
            {
                adv.Counters["eggsacs-remaining"] = 4;
                adv.Counters["eggsacs-destroyed"] = 0;
                adv.Counters["eggsac-last-round"] = 0;
                Log("adversary", "The Horror takes 4 Egg Sac tokens");
            }
        }

        /// <summary>Adversary action: place 1 of the 4 Egg Sacs, at most 1 per round, within
        /// 3 spaces of the Horror's current space.</summary>
        public void PlaceEggSac(string space)
        {
            EnsureAdversaryTurnStarted();
            EnsureEggsSetup();
            var adv = State.Adversary;
            int remaining = adv.Counters["eggsacs-remaining"];
            if (remaining <= 0)
            {
                throw new InvalidOperationException("All 4 Egg Sac tokens have already been placed.");
            }
            int lastRound = adv.Counters.TryGetValue("eggsac-last-round", out int lr) ? lr : 0;
            if (lastRound == State.Round)
            {
                throw new InvalidOperationException("Only 1 Egg Sac may be placed per round.");
            }
            if (!Graph.DistancesFrom(adv.Space, 3, State.Overlay).ContainsKey(space))
            {
                throw new InvalidOperationException("The Egg Sac must be placed within 3 spaces of The Horror.");
            }
            int placedSoFar = 4 - remaining;
            string tokenName = "eggsac-" + (placedSoFar + 1);
            State.Objective.Tokens[tokenName] = space;
            adv.Counters["eggsacs-remaining"] = remaining - 1;
            adv.Counters["eggsac-last-round"] = State.Round;
            Log("adversary", $"placed {tokenName} at {space}");
        }

        /// <summary>Investigator Involved Action, on an Egg Sac token: destroy it. The 4th
        /// destruction gives the Horror the Enraged Condition.</summary>
        public void DestroyEggSac()
        {
            var inv = BeginInvolvedAction();
            string? key = State.Objective.Tokens
                .Where(kv => kv.Key.StartsWith("eggsac-") && kv.Value == inv.Space)
                .Select(kv => kv.Key)
                .FirstOrDefault();
            if (key == null)
            {
                throw new InvalidOperationException("No Egg Sac token here.");
            }
            State.Objective.Tokens.Remove(key);
            var adv = State.Adversary;
            int destroyed = (adv.Counters.TryGetValue("eggsacs-destroyed", out int d) ? d : 0) + 1;
            adv.Counters["eggsacs-destroyed"] = destroyed;
            Log("objective", $"{inv.DefId} destroyed {key} ({destroyed}/4)");
            if (destroyed >= 4 && !(adv.Counters.TryGetValue("enraged", out int e) && e == 1))
            {
                adv.Counters["enraged"] = 1;
                RevealAdversary("Enraged");
                Log("adversary", "The Horror gains the Enraged Condition");
                Log("todo", "Enraged should permanently block Disappear (AdversaryDisappear is shared with the " +
                             "other Adversaries and cannot be special-cased here); Counters[\"enraged\"] is the " +
                             "authoritative flag but nothing currently stops a Disappear call after this point.");
            }
            FinishInvolvedAction(inv);
        }

        /// <summary>Investigator Involved Action, adjacent to the Enraged Horror: place a Banish
        /// supply. The 3rd supply banishes The Horror and wins the game.</summary>
        public void BanishTheHorror()
        {
            var inv = BeginInvolvedAction();
            var adv = State.Adversary;
            if (!(adv.Counters.TryGetValue("enraged", out int e) && e == 1))
            {
                throw new InvalidOperationException("The Horror must be Enraged before it can be Banished.");
            }
            if (!AdversaryAdjacentTo(adv.Space, inv.Space))
            {
                throw new InvalidOperationException("Must be adjacent to The Horror to place a Banish supply.");
            }
            int supplies = (adv.Counters.TryGetValue("banish-supplies", out int s) ? s : 0) + 1;
            adv.Counters["banish-supplies"] = supplies;
            Log("objective", $"{inv.DefId} placed a Banish supply ({supplies}/3)");
            if (supplies >= 3)
            {
                State.Phase = GamePhase.GameOver;
                State.Result = GameResult.InvestigatorsWin;
                Log("gameover", "The Horror is banished");
            }
            FinishInvolvedAction(inv);
        }

        // ---------- Shared helpers ----------

        private HashSet<string> OccupiedSpaces()
        {
            var set = new HashSet<string>(State.Investigators.Where(i => !i.Dead && !i.Escaped).Select(i => i.Space));
            set.Add(State.Adversary.Space);
            return set;
        }

        /// <summary>Dijkstra where entering a Bright space costs 2 instead of 1 (the Ambush
        /// range rule). Connectivity mirrors MapGraph's private "counting" adjacency: normal
        /// printed adjacency plus Secret Passages, blocked by movement-blocking Door tokens
        /// and closed Mirror Maze doors; light level and Map Hazards otherwise do not matter.</summary>
        private Dictionary<string, int> WeightedDistancesFrom(string from, int maxDistance)
        {
            var dist = new Dictionary<string, int> { { from, 0 } };
            var frontier = new SortedSet<Tuple<int, string>> { Tuple.Create(0, from) };
            var settled = new HashSet<string>();
            while (frontier.Count > 0)
            {
                var current = frontier.Min;
                frontier.Remove(current);
                if (!settled.Add(current.Item2))
                {
                    continue;
                }
                if (current.Item1 >= maxDistance)
                {
                    continue;
                }
                foreach (string next in WeightedNeighbors(current.Item2))
                {
                    int weight = IsBright(next) ? 2 : 1;
                    int candidate = current.Item1 + weight;
                    if (candidate > maxDistance)
                    {
                        continue;
                    }
                    if (!dist.TryGetValue(next, out int existing) || candidate < existing)
                    {
                        dist[next] = candidate;
                        frontier.Add(Tuple.Create(candidate, next));
                    }
                }
            }
            return dist;
        }

        private IEnumerable<string> WeightedNeighbors(string spaceId)
        {
            foreach (var edge in Graph.Def.Edges)
            {
                if (edge.A != spaceId && edge.B != spaceId)
                {
                    continue;
                }
                string other = edge.A == spaceId ? edge.B : edge.A;
                string key = BoardOverlay.EdgeKey(edge.A, edge.B);
                bool passable = edge.Type switch
                {
                    EdgeType.Move => true,
                    EdgeType.Window => !State.Overlay.FalseWindows.Contains(key),
                    EdgeType.MirrorDoor => edge.Color == State.Overlay.OpenMirrorColor,
                    EdgeType.AdversaryLink => false,
                    _ => false,
                };
                var doorState = State.Overlay.DoorState(other);
                bool blocked = doorState == DoorState.Locked || doorState == DoorState.Damaged || doorState == DoorState.False;
                if (passable && !blocked)
                {
                    yield return other;
                }
            }
            foreach (string key in State.Overlay.SecretPassages)
            {
                int sep = key.IndexOf('|');
                string a = key.Substring(0, sep);
                string b = key.Substring(sep + 1);
                if (a == spaceId)
                {
                    yield return b;
                }
                else if (b == spaceId)
                {
                    yield return a;
                }
            }
        }
    }
}
