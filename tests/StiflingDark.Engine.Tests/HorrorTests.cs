using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class HorrorTests
    {
        private static Game NewHorrorGame(ulong seed = 1, string attackCardId = "bufotoxin", List<string>? abilityCards = null)
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = "insatiable-horror",
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285",
                    ["lucy-belle"] = "286",
                    ["mitchell"] = "305",
                    ["vincent"] = "307",
                },
                MedicalItemSpaces = new List<string> { "24", "208" },
            });
            game.SetupAdversaryCards(attackCardId, abilityCards ?? new List<string> { "devour", "tunnel" });

            var evidenceSpaces = new Dictionary<string, string>
            {
                ["L"] = "L-1", ["K"] = "K-1", ["G"] = "G-1", ["S"] = "S-1", ["O"] = "O-2",
            };
            foreach (var (zone, space) in evidenceSpaces)
            {
                game.PlaceHiddenEvidence(zone, space);
            }
            bool cursedPlaced = false;
            foreach (var poi in game.Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest))
            {
                string target = game.Graph.DistancesFrom(poi.Id, 2, game.State.Overlay).Keys
                    .First(id => game.Graph.Space(id).Kind == SpaceKind.Normal);
                game.PlacePoiToken(poi.Id, target, cursedFront: !cursedPlaced);
                cursedPlaced = true;
            }
            game.PlaceAdversary("S-25");
            game.FinishAdversarySetup();
            return game;
        }

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        /// <summary>Take a do-nothing turn for everyone still waiting this round.</summary>
        private static void FinishInvestigatorTurns(Game game)
        {
            foreach (var inv in game.State.Investigators
                .Where(i => !i.Dead && !i.Escaped && !i.TurnTakenThisRound).ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
        }

        /// <summary>Advance (with do-nothing turns/adversary turns) until the Adversary's turn
        /// in the given round is ready to act.</summary>
        private static void ReachAdversaryTurn(Game game, int round)
        {
            while (game.State.Round < round)
            {
                FinishInvestigatorTurns(game);
                game.AdversaryEndTurn();
            }
            FinishInvestigatorTurns(game);
        }

        // ---------- Ambush timing/eligibility ----------

        [Fact]
        public void Ambush_is_blocked_during_round_one()
        {
            var game = NewHorrorGame();
            Assert.Throws<InvalidOperationException>(() => game.HorrorAmbush(new Dictionary<string, string>()));
        }

        [Fact]
        public void Ambush_is_blocked_while_revealed()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            game.State.Adversary.Revealed = true;
            Assert.Throws<InvalidOperationException>(() => game.HorrorAmbush(new Dictionary<string, string>()));
        }

        [Fact]
        public void Ambush_is_blocked_after_another_action_this_turn()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            // Move itself is not recorded in ActionsUsed by the shared framework (only card
            // plays, Disappear, and Break Door are), so use Break Door here to exercise the
            // "first action of the turn" gate. S-23 is within the Horror's 3-space Break Door
            // reach from S-25.
            game.AdversaryBreakDoor("S-23");
            Assert.Throws<InvalidOperationException>(() => game.HorrorAmbush(new Dictionary<string, string>()));
        }

        [Fact]
        public void Ambush_is_blocked_once_the_horror_is_enraged()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            game.State.Adversary.Counters["enraged"] = 1;
            Assert.Throws<InvalidOperationException>(() => game.HorrorAmbush(new Dictionary<string, string>()));
        }

        // ---------- Ambush range (Bright counts double) ----------

        [Fact]
        public void Ambush_pulls_investigators_within_the_weighted_five_space_range()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            // Distances from the Horror at S-25 (plain "move" edges): S-19 = 3, S-37 = 4, S-41 = 5 (the boundary).
            Inv(game, "aira").Space = "S-19";
            Inv(game, "lucy-belle").Space = "S-37";
            Inv(game, "mitchell").Space = "S-41";

            game.HorrorAmbush(new Dictionary<string, string>
            {
                ["aira"] = "S-24",
                ["lucy-belle"] = "S-21",
                ["mitchell"] = "S-27",
            });

            Assert.Equal("S-24", Inv(game, "aira").Space);
            Assert.Equal("S-21", Inv(game, "lucy-belle").Space);
            Assert.Equal("S-27", Inv(game, "mitchell").Space);
            Assert.Contains("ambush", game.State.Adversary.ActionsUsed);
        }

        [Fact]
        public void Ambush_excludes_an_investigator_whose_only_path_crosses_a_bright_space()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            // S-42 is also 5 plain spaces from S-25, but its only neighbor toward the Horror is
            // S-37, so entering S-42 itself while it is Bright makes the weighted distance 6.
            Inv(game, "mitchell").Space = "S-42";
            game.State.Overlay.BrightSpaces.Add("S-42");

            Assert.Throws<InvalidOperationException>(() => game.HorrorAmbush(new Dictionary<string, string>
            {
                ["mitchell"] = "S-26",
            }));
        }

        [Fact]
        public void Ambush_rejects_an_occupied_or_non_adjacent_destination()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            Inv(game, "aira").Space = "S-19";
            Inv(game, "lucy-belle").Space = "S-24"; // sits on the destination we'll try to use

            Assert.Throws<InvalidOperationException>(() => game.HorrorAmbush(new Dictionary<string, string>
            {
                ["aira"] = "S-24", // occupied by lucy-belle
            }));
            Assert.Throws<InvalidOperationException>(() => game.HorrorAmbush(new Dictionary<string, string>
            {
                ["aira"] = "S-19", // not adjacent to the Horror at S-25
            }));
        }

        // ---------- Attacking ----------

        [Fact]
        public void Attack_without_ambushing_first_throws()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            Inv(game, "aira").Space = "S-24"; // adjacent to the Horror at S-25
            Assert.Throws<InvalidOperationException>(
                () => game.PlayAdversaryCard("bufotoxin", new List<string> { "aira" }));
        }

        [Fact]
        public void Ambush_then_attack_can_hit_every_adjacent_investigator()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            Inv(game, "aira").Space = "S-19";
            Inv(game, "lucy-belle").Space = "S-37";

            game.HorrorAmbush(new Dictionary<string, string>
            {
                ["aira"] = "S-24",
                ["lucy-belle"] = "S-21",
            });
            game.PlayAdversaryCard("bufotoxin", new List<string> { "aira", "lucy-belle" });

            Assert.Single(Inv(game, "aira").Wounds);
            Assert.Single(Inv(game, "lucy-belle").Wounds);
            Assert.False(Inv(game, "aira").Wounds[0].FaceUp);
            foreach (string id in new List<string> { "aira", "lucy-belle" })
            {
                Assert.True(game.HasCondition(Inv(game, id), "bufotoxin"));
                Assert.True(game.HasCondition(Inv(game, id), "mauled"));
            }
        }

        [Fact]
        public void Gastric_secretions_wounds_the_target_and_spawns_a_hatchling_token()
        {
            var game = NewHorrorGame(attackCardId: "gastric-secretions", abilityCards: new List<string> { "devour", "tunnel" });
            ReachAdversaryTurn(game, 2);
            Inv(game, "aira").Space = "S-19";
            game.HorrorAmbush(new Dictionary<string, string> { ["aira"] = "S-24" });
            game.PlayAdversaryCard("gastric-secretions", new List<string> { "aira", "S-26" });

            Assert.Single(Inv(game, "aira").Wounds);
            Assert.Equal("S-26", game.State.Objective.Tokens["hatchling-1"]);
        }

        // ---------- Cooldown cycling ----------

        [Fact]
        public void A_cooldown_two_ability_takes_two_full_adversary_turns_to_return_to_active()
        {
            var game = NewHorrorGame(abilityCards: new List<string> { "tunnel", "devour" });
            ReachAdversaryTurn(game, 2);

            game.PlayAdversaryCard("tunnel", new List<string> { "S-24" });
            Assert.DoesNotContain("tunnel", game.State.Adversary.ActiveAbilities);
            Assert.Contains(game.State.Adversary.Cooldown2, c => c.CardId == "tunnel");

            game.AdversaryEndTurn(); // round 2 ends
            ReachAdversaryTurn(game, 3);
            Assert.DoesNotContain("tunnel", game.State.Adversary.ActiveAbilities);
            Assert.Contains(game.State.Adversary.Cooldown2, c => c.CardId == "tunnel");

            game.AdversaryEndTurn(); // round 3 ends: Cooldown 2 -> Cooldown 1
            ReachAdversaryTurn(game, 4);
            Assert.DoesNotContain("tunnel", game.State.Adversary.ActiveAbilities);
            Assert.Contains(game.State.Adversary.Cooldown1, c => c.CardId == "tunnel");

            game.AdversaryEndTurn(); // round 4 ends: Cooldown 1 -> Active
            ReachAdversaryTurn(game, 5);
            Assert.Contains("tunnel", game.State.Adversary.ActiveAbilities);
        }

        [Fact]
        public void Devour_lets_the_horror_attack_next_turn_without_ambushing()
        {
            var game = NewHorrorGame(abilityCards: new List<string> { "devour", "tunnel" });
            ReachAdversaryTurn(game, 2);

            game.PlayAdversaryCard("devour", new List<string>());
            Assert.Equal(1, game.State.Adversary.Counters["devour-next-turn"]);

            game.AdversaryEndTurn();
            ReachAdversaryTurn(game, 3);
            Inv(game, "aira").Space = "S-24"; // adjacent to the Horror at S-25

            // No Ambush this turn, yet the Attack card works because Devour is active.
            game.PlayAdversaryCard("bufotoxin", new List<string> { "aira" });
            Assert.Single(Inv(game, "aira").Wounds);
            Assert.False(game.State.Adversary.Counters.ContainsKey("devour-active"));
        }

        // ---------- The Eggs banish objective ----------

        [Fact]
        public void PlaceEggSac_requires_being_within_three_spaces_of_the_horror()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            Assert.Throws<InvalidOperationException>(() => game.PlaceEggSac("S-2")); // 6 spaces away
            game.PlaceEggSac("S-19"); // 3 spaces away: legal
            Assert.Equal("S-19", game.State.Objective.Tokens["eggsac-1"]);
        }

        [Fact]
        public void PlaceEggSac_allows_only_one_per_round()
        {
            var game = NewHorrorGame();
            ReachAdversaryTurn(game, 2);
            game.PlaceEggSac("S-19");
            Assert.Throws<InvalidOperationException>(() => game.PlaceEggSac("S-24"));

            game.AdversaryEndTurn();
            ReachAdversaryTurn(game, 3);
            game.PlaceEggSac("S-24"); // legal again next round
            Assert.Equal("S-24", game.State.Objective.Tokens["eggsac-2"]);
        }

        [Fact]
        public void Destroying_all_four_egg_sacs_enrages_the_horror()
        {
            var game = NewHorrorGame();
            var eggSpaces = new[] { "S-19", "S-24", "S-21", "S-27" };
            for (int round = 2; round <= 5; round++)
            {
                ReachAdversaryTurn(game, round);
                game.PlaceEggSac(eggSpaces[round - 2]);
                game.AdversaryEndTurn();
            }

            // Each Investigator gets exactly 1 turn per round; use a different one per Egg Sac.
            var destroyers = new[] { "aira", "lucy-belle", "mitchell", "vincent" };
            for (int i = 0; i < eggSpaces.Length; i++)
            {
                Inv(game, destroyers[i]).Space = eggSpaces[i];
                game.BeginInvestigatorTurn(destroyers[i]);
                game.DestroyEggSac();
            }

            Assert.Equal(1, game.State.Adversary.Counters["enraged"]);
            Assert.True(game.State.Adversary.Revealed);
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.Contains("Enraged"));
        }

        [Fact]
        public void BanishTheHorror_requires_enraged_and_adjacency_then_wins_at_three_supplies()
        {
            var game = NewHorrorGame();
            Inv(game, "aira").Space = "S-24"; // adjacent to the Horror at S-25

            // Not Enraged yet.
            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.BanishTheHorror());
            game.EndTurnWithoutFinalAction();

            game.State.Adversary.Counters["enraged"] = 1;

            // Not adjacent.
            Inv(game, "lucy-belle").Space = "119";
            game.BeginInvestigatorTurn("lucy-belle");
            Assert.Throws<InvalidOperationException>(() => game.BanishTheHorror());
            game.EndTurnWithoutFinalAction();

            // Finish round 1 with everyone else doing nothing, then move to round 2 so
            // aira (already spent her round-1 turn above) can act again.
            Inv(game, "mitchell").Space = "S-26"; // also adjacent to S-25
            game.BeginInvestigatorTurn("mitchell");
            game.EndTurnWithoutFinalAction();
            Inv(game, "vincent").Space = "S-27"; // also adjacent to S-25
            game.BeginInvestigatorTurn("vincent");
            game.EndTurnWithoutFinalAction();
            game.AdversaryEndTurn();

            game.BeginInvestigatorTurn("aira");
            game.BanishTheHorror(); // an Involved Action: ends aira's turn on its own
            Assert.Equal(1, game.State.Adversary.Counters["banish-supplies"]);
            Assert.Equal(GameResult.Undecided, game.State.Result);

            game.BeginInvestigatorTurn("lucy-belle");
            game.EndTurnWithoutFinalAction();

            game.BeginInvestigatorTurn("mitchell");
            game.BanishTheHorror();
            Assert.Equal(2, game.State.Adversary.Counters["banish-supplies"]);

            game.BeginInvestigatorTurn("vincent");
            game.BanishTheHorror();

            Assert.Equal(3, game.State.Adversary.Counters["banish-supplies"]);
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
        }

        // ---------- Setup ----------

        [Fact]
        public void Setup_gives_the_horror_one_attack_and_two_abilities_at_four_investigators()
        {
            var game = NewHorrorGame(abilityCards: new List<string> { "devour", "occluded-lights" });
            Assert.Equal("bufotoxin", game.State.Adversary.AttackCard);
            Assert.Equal(2, game.State.Adversary.ActiveAbilities.Count);
            Assert.Contains("devour", game.State.Adversary.ActiveAbilities);
            Assert.Contains("occluded-lights", game.State.Adversary.ActiveAbilities);
        }
    }
}
