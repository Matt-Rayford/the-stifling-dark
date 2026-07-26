using System;
using System.Collections.Generic;
using System.Linq;

namespace StiflingDark.Engine.Core
{
    /// <summary>
    /// The four Scenario escape Objectives: drawing and selecting an Escape card, its token
    /// setup, and the Interact / Involved Actions that carry each Objective to a win.
    /// </summary>
    public sealed partial class Game
    {
        /// <summary>One Scenario Escape card's setup, transcribed from game-data/cards/escape-cards.json.</summary>
        private sealed class EscapeCardSetup
        {
            public string Objective { get; set; } = "";
            /// <summary>Owning scenario id.</summary>
            public string Owner { get; set; } = "";
            /// <summary>Fire the Flare only: the Ride whose carriages the helicopter reaches.</summary>
            public string? RideId { get; set; }
            /// <summary>Token name -> space, placed with no roll.</summary>
            public Dictionary<string, string> FixedTokens { get; set; } = new Dictionary<string, string>();
            /// <summary>The card's D6 table, in printed order: rows for 1-2, 3-4, 5-6.</summary>
            public List<Dictionary<string, string>> D6Rows { get; set; } = new List<Dictionary<string, string>>();
        }

        // Transcribed literally from game-data/cards/escape-cards.json (card ids in the keys).
        private static readonly Dictionary<string, EscapeCardSetup> EscapeSetups = new Dictionary<string, EscapeCardSetup>
        {
            ["north-gate"] = new EscapeCardSetup
            {
                Objective = "power-the-gate",
                Owner = "sawmill",
                FixedTokens = new Dictionary<string, string> { ["saw"] = "S-17", ["locked-escape"] = "10" },
                D6Rows = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["lockbox"] = "S-1" },
                    new Dictionary<string, string> { ["lockbox"] = "104" },
                    new Dictionary<string, string> { ["lockbox"] = "S-41" },
                },
            },
            ["south-gate"] = new EscapeCardSetup
            {
                Objective = "power-the-gate",
                Owner = "sawmill",
                FixedTokens = new Dictionary<string, string> { ["saw"] = "S-28", ["locked-escape"] = "306" },
                D6Rows = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["lockbox"] = "101" },
                    new Dictionary<string, string> { ["lockbox"] = "S-33" },
                    new Dictionary<string, string> { ["lockbox"] = "222" },
                },
            },
            ["garage"] = new EscapeCardSetup
            {
                Objective = "fix-the-truck",
                Owner = "sawmill",
                FixedTokens = new Dictionary<string, string> { ["truck"] = "G-6" },
                D6Rows = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["battery"] = "S-37", ["repair-kit"] = "308", ["spark-plug"] = "313" },
                    new Dictionary<string, string> { ["battery"] = "50", ["repair-kit"] = "134", ["spark-plug"] = "159" },
                    new Dictionary<string, string> { ["battery"] = "12", ["repair-kit"] = "L-7", ["spark-plug"] = "L-19" },
                },
            },
            ["sawmill"] = new EscapeCardSetup
            {
                Objective = "fix-the-truck",
                Owner = "sawmill",
                FixedTokens = new Dictionary<string, string> { ["truck"] = "220" },
                D6Rows = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["battery"] = "G-5", ["repair-kit"] = "121", ["spark-plug"] = "191" },
                    new Dictionary<string, string> { ["battery"] = "L-5", ["repair-kit"] = "36", ["spark-plug"] = "O-2" },
                    new Dictionary<string, string> { ["battery"] = "K-7", ["repair-kit"] = "K-9", ["spark-plug"] = "S-1" },
                },
            },
            ["tunnel-of-love"] = new EscapeCardSetup
            {
                Objective = "service-tunnels",
                Owner = "amusement-park",
                FixedTokens = new Dictionary<string, string>
                {
                    ["locked-escape"] = "T-18", ["ride-parts-1"] = "175", ["ride-parts-2"] = "86",
                },
                D6Rows = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["angle-grinder"] = "M-6" },
                    new Dictionary<string, string> { ["angle-grinder"] = "80" },
                    new Dictionary<string, string> { ["angle-grinder"] = "182" },
                },
            },
            ["mirror-maze"] = new EscapeCardSetup
            {
                Objective = "service-tunnels",
                Owner = "amusement-park",
                FixedTokens = new Dictionary<string, string>
                {
                    ["locked-escape"] = "M-20", ["ride-parts-1"] = "C-9", ["ride-parts-2"] = "57",
                },
                D6Rows = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["angle-grinder"] = "268" },
                    new Dictionary<string, string> { ["angle-grinder"] = "T-36" },
                    new Dictionary<string, string> { ["angle-grinder"] = "196" },
                },
            },
            ["the-zipper"] = new EscapeCardSetup
            {
                Objective = "fire-the-flare",
                Owner = "amusement-park",
                RideId = "zipper",
                FixedTokens = new Dictionary<string, string> { ["locked-escape"] = "71" },
                D6Rows = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["flare-gun"] = "M-36", ["ammo-1"] = "M-20", ["ammo-2"] = "149" },
                    new Dictionary<string, string> { ["flare-gun"] = "257", ["ammo-1"] = "188", ["ammo-2"] = "293" },
                    new Dictionary<string, string> { ["flare-gun"] = "19", ["ammo-1"] = "6", ["ammo-2"] = "27" },
                },
            },
            ["ferris-wheel"] = new EscapeCardSetup
            {
                Objective = "fire-the-flare",
                Owner = "amusement-park",
                RideId = "ferrisWheel",
                FixedTokens = new Dictionary<string, string> { ["locked-escape"] = "65" },
                D6Rows = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["flare-gun"] = "31", ["ammo-1"] = "G-7", ["ammo-2"] = "M-32" },
                    new Dictionary<string, string> { ["flare-gun"] = "C-10", ["ammo-1"] = "105", ["ammo-2"] = "240" },
                    new Dictionary<string, string> { ["flare-gun"] = "301", ["ammo-1"] = "282", ["ammo-2"] = "257" },
                },
            },
        };

        /// <summary>Adversary def id -> its Banish Escape card (escape-cards.json owners are butcher/cult/horror).</summary>
        private static readonly Dictionary<string, string> BanishCardByAdversary = new Dictionary<string, string>
        {
            ["butcher"] = "the-grave",
            ["cult-of-hunlow"] = "the-altar",
            ["insatiable-horror"] = "the-eggs",
        };

        /// <summary>Objective tokens an Investigator can carry; the rest stay on the board.</summary>
        private static readonly HashSet<string> PortableTokens = new HashSet<string>
        {
            "lockbox", "battery", "repair-kit", "spark-plug", "flare-gun", "ammo-1", "ammo-2",
            "angle-grinder", "ride-parts-1", "ride-parts-2",
        };

        private static readonly string[] TruckParts = { "battery", "repair-kit", "spark-plug" };

        // ---------- Objective selection ----------

        /// <summary>
        /// The 3 Escape cards the Investigators choose between: 1 random card per Scenario
        /// Objective plus the Banish card for the Adversary in play.
        /// </summary>
        public IReadOnlyList<string> DrawEscapeChoices()
        {
            RequireNoEscapeCardSelected();
            int required = Db.Config.ByInvestigatorCount[State.Investigators.Count].EvidenceRequiredForObjective;
            if (State.Objective.EvidenceTurnedIn < required)
            {
                throw new InvalidOperationException(
                    $"{required} Evidence must be turned in before selecting an Escape card ({State.Objective.EvidenceTurnedIn} so far).");
            }
            var choices = new List<string>();
            foreach (string objective in ScenarioObjectives())
            {
                var pool = EscapeSetups
                    .Where(kv => kv.Value.Owner == State.ScenarioId && kv.Value.Objective == objective)
                    .Select(kv => kv.Key)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                choices.Add(pool[_rng.Next(pool.Count)]);
            }
            SaveRng();
            choices.Add(BanishCard());
            Log("objective", "escape choices: " + string.Join(", ", choices));
            return choices;
        }

        /// <summary>Commit to one Escape card and run its setup (token placement, incl. its D6 roll).</summary>
        public void SelectEscapeCard(string cardId)
        {
            RequireNoEscapeCardSelected();
            if (!Db.Deck("escape").Any(c => c.Id == cardId))
            {
                throw new InvalidOperationException($"'{cardId}' is not an Escape card.");
            }
            if (!EscapeSetups.TryGetValue(cardId, out var setup))
            {
                if (cardId != BanishCard())
                {
                    throw new InvalidOperationException(
                        $"'{cardId}' is neither a '{State.ScenarioId}' Escape card nor the Banish card for '{State.Adversary.DefId}'.");
                }
                // Banish setup is adversary-specific; the matching partial (Game.Butcher /
                // Game.Horror / Game.Cult) implements the hook. An unimplemented hook is a no-op,
                // so guard with a flag the hooks must set.
                _banishSetupDone = false;
                switch (cardId)
                {
                    case "the-grave": SetupGraveBanish(); break;
                    case "the-eggs": SetupEggsBanish(); break;
                    case "the-altar": SetupAltarBanish(); break;
                }
                if (!_banishSetupDone)
                {
                    throw new NotImplementedException($"Banish setup for '{cardId}' is not implemented.");
                }
                // Banish cards have no EscapeSetups entry (no scenario owner, no FixedTokens/D6
                // roll) — the adversary-specific hook just run is entirely responsible for its
                // own token placement, so commit the selection and stop here.
                State.Objective.SelectedEscapeCard = cardId;
                Log("objective", $"selected the Banish card '{cardId}'");
                return;
            }
            if (setup.Owner != State.ScenarioId)
            {
                throw new InvalidOperationException($"'{cardId}' belongs to scenario '{setup.Owner}', not '{State.ScenarioId}'.");
            }

            State.Objective.SelectedEscapeCard = cardId;
            foreach (var kv in setup.FixedTokens)
            {
                PlaceObjectiveToken(kv.Key, kv.Value);
            }
            int roll = _rng.Roll(6);
            SaveRng();
            Log("objective", $"{cardId} setup rolled {roll}");
            foreach (var kv in setup.D6Rows[(roll - 1) / 2])
            {
                PlaceObjectiveToken(kv.Key, kv.Value);
            }
        }

        // ---------- Carrying Objective tokens ----------

        /// <summary>Interact Action: take an Objective token on your space.</summary>
        public void PickUpObjectiveToken(string tokenName)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (!PortableTokens.Contains(tokenName))
            {
                throw new InvalidOperationException($"The {tokenName} token cannot be picked up.");
            }
            if (IsRidePartsToken(tokenName))
            {
                throw new InvalidOperationException("Ride Parts are collected with the PickUpRideParts Involved Action.");
            }
            RequireOnToken(inv, tokenName);
            State.Objective.Tokens.Remove(tokenName);
            State.Objective.TokenCarriers[tokenName] = inv.DefId;
            Log("objective", $"{inv.DefId} picked up {tokenName}");
        }

        /// <summary>Interact Action: put a carried Objective token down on your space.</summary>
        public void DropObjectiveToken(string tokenName)
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireCarrying(inv, tokenName);
            State.Objective.TokenCarriers.Remove(tokenName);
            State.Objective.Tokens[tokenName] = inv.Space;
            Log("objective", $"{inv.DefId} dropped {tokenName} on {inv.Space}");
        }

        // ---------- Power the Gate (Sawmill) ----------

        /// <summary>Involved Action on the Saw with the Lockbox: place 1 Supply, optionally pushing your luck for a 2nd.</summary>
        public void OpenLockbox(bool pushYourLuck)
        {
            var inv = BeginInvolvedAction();
            RequireCarrying(inv, "lockbox");
            RequireOnToken(inv, "saw");
            RequireTeamActionUnused(State.Objective.OpenLockboxUsedRound, "The Saw may only be used once per round");

            State.Objective.OpenLockboxUsedRound = State.Round;
            AddSupply();
            if (pushYourLuck)
            {
                int roll = _rng.Roll(6);
                SaveRng();
                // Power the Gate aid: Bright 1-2 Wound / 3+ Supply; Dim or Dark 1-4 Wound / 5+ Supply.
                int supplyFrom = IsBright(inv.Space) ? 3 : 5;
                Log("objective", $"{inv.DefId} pushed their luck on the Saw and rolled {roll}");
                if (roll >= supplyFrom)
                {
                    AddSupply();
                }
                else
                {
                    GainWound(inv, faceUp: true);
                }
            }
            if (State.Objective.Supplies >= 4 && !State.Investigators.Any(i => i.Items.Contains("fuse")))
            {
                inv.Items.Add("fuse");
                Log("objective", $"the Lockbox is open: {inv.DefId} gained the Fuse");
            }
            FinishInvolvedAction(inv);
        }

        /// <summary>Involved Action with the Fuse on the Locked Escape token: flip it to its Escape side.</summary>
        public void PowerTheGate()
        {
            var inv = BeginInvolvedAction();
            if (!inv.Items.Contains("fuse"))
            {
                throw new InvalidOperationException($"{inv.DefId} does not have the Fuse.");
            }
            RequireOnToken(inv, "locked-escape");
            if (State.Objective.EscapeOpen)
            {
                throw new InvalidOperationException("The gate is already open.");
            }
            State.Objective.EscapeOpen = true;
            Log("objective", $"{inv.DefId} powered the gate at {inv.Space}");
            FinishInvolvedAction(inv);
        }

        /// <summary>Interact Action on the opened gate.</summary>
        public void EscapeThroughGate()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            if (!State.Objective.EscapeOpen)
            {
                throw new InvalidOperationException("The gate is still locked.");
            }
            RequireOnToken(inv, "locked-escape");
            Escape(inv);
        }

        // ---------- Fix the Truck (Sawmill) ----------

        /// <summary>Involved Action on the Truck: install one carried Part.</summary>
        public void InstallPart(string partToken)
        {
            var inv = BeginInvolvedAction();
            if (Array.IndexOf(TruckParts, partToken) < 0)
            {
                throw new InvalidOperationException($"'{partToken}' is not a Truck Part.");
            }
            RequireCarrying(inv, partToken);
            RequireOnToken(inv, "truck");
            RequireTeamActionUnused(State.Objective.InstallPartUsedRound, "Only 1 Part may be installed per round");

            State.Objective.InstallPartUsedRound = State.Round;
            State.Objective.TokenCarriers.Remove(partToken);
            State.Objective.PartsInstalled += 1;
            Log("objective", $"{inv.DefId} installed {partToken} ({State.Objective.PartsInstalled}/3)");
            FinishInvolvedAction(inv);
        }

        /// <summary>
        /// Involved Action on or adjacent to the Truck: roll to start it. Success target is the
        /// lowest number with a Part token above it on the aid (1 Part: 6, 2: 4+, 3: automatic).
        /// </summary>
        public void StartTruck(string escapeSpace)
        {
            var inv = BeginInvolvedAction();
            string truck = TokenSpace("truck");
            if (!OnOrAdjacent(inv.Space, truck))
            {
                throw new InvalidOperationException($"Starting the Truck requires being on or adjacent to {truck}.");
            }
            if (State.Objective.PartsInstalled < 1)
            {
                throw new InvalidOperationException("At least 1 Part must be installed first.");
            }
            if (escapeSpace != "10" && escapeSpace != "306")
            {
                throw new InvalidOperationException("The Truck rams the gate at space 10 or 306.");
            }
            RequireTeamActionUnused(State.Objective.StartTruckUsedRound, "One Investigator per round may try to start the Truck");

            State.Objective.StartTruckUsedRound = State.Round;
            int target = State.Objective.PartsInstalled == 1 ? 6 : State.Objective.PartsInstalled == 2 ? 4 : 1;
            int roll = _rng.Roll(6);
            SaveRng();
            Log("objective", $"{inv.DefId} rolled {roll} to start the Truck (needs {target}+)");
            if (roll < target)
            {
                FinishInvolvedAction(inv);
                return;
            }
            State.Objective.Tokens["escape"] = escapeSpace;
            foreach (var rider in State.Investigators
                .Where(i => !i.Dead && !i.Escaped && OnOrAdjacent(i.Space, truck))
                .ToList())
            {
                Escape(rider);
            }
            Log("objective", $"the Truck starts; Escape token placed on {escapeSpace}");
            FinishInvolvedAction(inv);
        }

        /// <summary>Interact Action on the Escape token the Truck opened.</summary>
        public void EscapeAtTruckExit()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireOnToken(inv, "escape");
            Escape(inv);
        }

        // ---------- Fire the Flare (Amusement Park) ----------

        /// <summary>Involved Action on the Locked Escape token with the Flare Gun and Ammo: help arrives in 2 rounds.</summary>
        public void FireFlareGun()
        {
            var inv = BeginInvolvedAction();
            RequireOnToken(inv, "locked-escape");
            RequireCarrying(inv, "flare-gun");
            if (!CarriesAny(inv, "ammo-1", "ammo-2"))
            {
                throw new InvalidOperationException($"{inv.DefId} has no Ammo token.");
            }
            ConsumeCarriedToken(inv, "ammo-1", "ammo-2");
            State.Objective.EscapeReadyRound = State.Round + 2;
            Log("objective", $"{inv.DefId} fired the flare; help arrives in round {State.Objective.EscapeReadyRound}");
            FinishInvolvedAction(inv);
        }

        /// <summary>Interact Action in any carriage of the Escape card's Ride, once help has arrived.</summary>
        public void EscapeByHelicopter()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            var setup = SelectedSetup("fire-the-flare");
            RequireEscapeReady();
            string rideId = setup.RideId ?? throw new InvalidOperationException($"'{State.Objective.SelectedEscapeCard}' names no Ride.");
            var carriages = Graph.Def.Rides[rideId].Carriages.SelectMany(c => c).ToHashSet();
            if (!carriages.Contains(inv.Space))
            {
                throw new InvalidOperationException($"{inv.DefId} must be in a carriage of the {rideId} to be picked up.");
            }
            Escape(inv);
        }

        // ---------- Service Tunnels (Amusement Park) ----------

        /// <summary>Involved Action on a Ride Parts token: pick it up, then roll for a face-up Wound.</summary>
        public void PickUpRideParts(string token)
        {
            var inv = BeginInvolvedAction();
            if (!IsRidePartsToken(token))
            {
                throw new InvalidOperationException($"'{token}' is not a Ride Parts token.");
            }
            RequireOnToken(inv, token);

            State.Objective.Tokens.Remove(token);
            State.Objective.TokenCarriers[token] = inv.DefId;
            int roll = _rng.Roll(6);
            SaveRng();
            // Service Tunnels aid: Bright Wound on a 1; Dim or Dark Wound on 4 or less. Picked up either way.
            int safeFrom = IsBright(inv.Space) ? 2 : 5;
            Log("objective", $"{inv.DefId} took {token} and rolled {roll}");
            if (roll < safeFrom)
            {
                GainWound(inv, faceUp: true);
            }
            FinishInvolvedAction(inv);
        }

        /// <summary>Involved Action on the Locked Escape token with the Angle Grinder and Ride Parts.</summary>
        public void OpenServiceTunnel()
        {
            var inv = BeginInvolvedAction();
            RequireOnToken(inv, "locked-escape");
            RequireCarrying(inv, "angle-grinder");
            if (!CarriesAny(inv, "ride-parts-1", "ride-parts-2"))
            {
                throw new InvalidOperationException($"{inv.DefId} has no Ride Parts token.");
            }
            ConsumeCarriedToken(inv, "ride-parts-1", "ride-parts-2");
            State.Objective.EscapeReadyRound = State.Round + 1;
            Log("objective", $"{inv.DefId} started cutting the lock; it opens in round {State.Objective.EscapeReadyRound}");
            FinishInvolvedAction(inv);
        }

        /// <summary>Interact Action on the cut-open Locked Escape token.</summary>
        public void EscapeThroughTunnel()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            SelectedSetup("service-tunnels");
            RequireEscapeReady();
            RequireOnToken(inv, "locked-escape");
            State.Objective.EscapeOpen = true; // the lock is cut: the token is on its Escape side
            Escape(inv);
        }

        // ---------- Shared objective plumbing ----------

        /// <summary>Spend one of the named tokens the investigator carries (designer ruling: Ammo and Ride Parts are consumed).</summary>
        private void ConsumeCarriedToken(InvestigatorState inv, params string[] tokenNames)
        {
            foreach (string token in tokenNames)
            {
                if (State.Objective.TokenCarriers.TryGetValue(token, out string carrier) && carrier == inv.DefId)
                {
                    State.Objective.TokenCarriers.Remove(token);
                    Log("objective", $"{inv.DefId} spent {token}");
                    return;
                }
            }
            throw new InvalidOperationException($"{inv.DefId} carries none of: {string.Join(", ", tokenNames)}.");
        }

        private void Escape(InvestigatorState inv)
        {
            inv.Escaped = true;
            Log("escape", inv.DefId);
            CheckObjectiveWin();
        }

        private void CheckObjectiveWin()
        {
            if (State.Phase == GamePhase.GameOver)
            {
                return;
            }
            var living = State.Investigators.Where(i => !i.Dead).ToList();
            if (living.Count == 0 || !living.All(i => i.Escaped))
            {
                return;
            }
            State.Phase = GamePhase.GameOver;
            State.Result = GameResult.InvestigatorsWin;
            Log("gameover", "every surviving Investigator escaped");
        }

        private InvestigatorState BeginInvolvedAction()
        {
            var inv = ActiveInv();
            RequireNoPendingWindow();
            RequireNoFinalAction(inv);
            return inv;
        }

        private void FinishInvolvedAction(InvestigatorState inv)
        {
            inv.FinalAction = FinalActionKind.InvolvedAction;
            if (State.Phase == GamePhase.GameOver)
            {
                return; // the action ended the game (a fatal Wound, or the last escape)
            }
            EndTurn(inv);
        }

        private void AddSupply()
        {
            State.Objective.Supplies = Math.Min(4, State.Objective.Supplies + 1); // the aid has 4 slots
            Log("objective", $"supply {State.Objective.Supplies}/4");
        }

        private void PlaceObjectiveToken(string tokenName, string space)
        {
            Graph.Space(space);
            State.Objective.Tokens[tokenName] = space;
            Log("objective", $"{tokenName} token on {space}");
        }

        private IEnumerable<string> ScenarioObjectives()
        {
            var objectives = EscapeSetups.Values
                .Where(s => s.Owner == State.ScenarioId)
                .Select(s => s.Objective)
                .Distinct()
                .OrderBy(o => o, StringComparer.Ordinal)
                .ToList();
            if (objectives.Count != 2)
            {
                throw new InvalidOperationException($"Scenario '{State.ScenarioId}' has no escape Objectives.");
            }
            return objectives;
        }

        private string BanishCard() =>
            BanishCardByAdversary.TryGetValue(State.Adversary.DefId, out string card)
                ? card
                : throw new InvalidOperationException($"No Banish card for adversary '{State.Adversary.DefId}'.");

        private EscapeCardSetup SelectedSetup(string objective)
        {
            string? selected = State.Objective.SelectedEscapeCard;
            if (selected == null || !EscapeSetups.TryGetValue(selected, out var setup) || setup.Objective != objective)
            {
                throw new InvalidOperationException($"The selected Escape card is not a {objective} card.");
            }
            return setup;
        }

        private void RequireNoEscapeCardSelected()
        {
            if (State.Objective.SelectedEscapeCard != null)
            {
                throw new InvalidOperationException($"Escape card '{State.Objective.SelectedEscapeCard}' is already selected.");
            }
        }

        private void RequireEscapeReady()
        {
            int? ready = State.Objective.EscapeReadyRound;
            if (ready == null)
            {
                throw new InvalidOperationException("No Escape token is on the round tracker yet.");
            }
            if (State.Round < ready.Value)
            {
                throw new InvalidOperationException($"The escape opens in round {ready.Value}; it is round {State.Round}.");
            }
        }

        private void RequireTeamActionUnused(int lastUsedRound, string message)
        {
            if (lastUsedRound == State.Round)
            {
                throw new InvalidOperationException($"{message} (not once per Investigator).");
            }
        }

        private void RequireCarrying(InvestigatorState inv, string tokenName)
        {
            if (!State.Objective.TokenCarriers.TryGetValue(tokenName, out string carrier) || carrier != inv.DefId)
            {
                throw new InvalidOperationException($"{inv.DefId} is not carrying the {tokenName} token.");
            }
        }

        private void RequireOnToken(InvestigatorState inv, string tokenName)
        {
            if (!State.Objective.Tokens.TryGetValue(tokenName, out string space) || space != inv.Space)
            {
                throw new InvalidOperationException($"There is no {tokenName} token on {inv.Space}.");
            }
        }

        private string TokenSpace(string tokenName) =>
            State.Objective.Tokens.TryGetValue(tokenName, out string space)
                ? space
                : throw new InvalidOperationException($"The {tokenName} token is not on the board.");

        private bool CarriesAny(InvestigatorState inv, params string[] tokenNames) =>
            tokenNames.Any(t => State.Objective.TokenCarriers.TryGetValue(t, out string carrier) && carrier == inv.DefId);

        private bool OnOrAdjacent(string space, string target) =>
            space == target || Graph.Edge(space, target) != null;

        private static bool IsRidePartsToken(string tokenName) =>
            tokenName == "ride-parts-1" || tokenName == "ride-parts-2";
    }
}
