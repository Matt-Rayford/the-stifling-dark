using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// The Cult of Hunlow: Cultist setup and Actions, the Blood track, The Final Sacrifice,
    /// Corporeal Mor'gonnod, and The Altar banish Objective.
    /// </summary>
    public class CultTests
    {
        private static readonly string[] InvestigatorIds = { "aira", "lucy-belle", "mitchell", "vincent" };
        private static readonly string[] StartSpaces = { "285", "286", "305", "307" };

        /// <summary>Default Ability loadout; the first card is the one that starts face-up.</summary>
        private static readonly string[] DefaultAbilities = { "razor-like-talons", "dried-tongue", "severed-ear" };

        private const string MorgonnodSpace = "S-25";
        private const string AltarSpace = "S-18";

        /// <summary>Cultists in one connected group, all within reach of Mor'gonnod on S-25.</summary>
        private static List<string> CultistSpaces(int investigators) =>
            new List<string> { "S-21", "S-24", "S-27", "S-30" }.Take(investigators).ToList();

        private static int AbilityCount(int investigators) =>
            investigators <= 2 ? 1 : investigators == 3 ? 2 : 3;

        private static Game NewCultGame(
            ulong seed = 5,
            int investigators = 4,
            string attack = "ravage",
            string[]? abilities = null,
            bool complete = true)
        {
            var startSpaces = new Dictionary<string, string>();
            for (int i = 0; i < investigators; i++)
            {
                startSpaces[InvestigatorIds[i]] = StartSpaces[i];
            }
            int medicalCount = TestData.Db.Config.ByInvestigatorCount[investigators].MedicalItemsOnBoard;
            var medical = TestData.Db.Map("sawmill").Spaces
                .Where(s => s.Kind == SpaceKind.MedicalItem).Take(medicalCount).Select(s => s.Id).ToList();

            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = "cult-of-hunlow",
                InvestigatorStartSpaces = startSpaces,
                MedicalItemSpaces = medical,
            });
            foreach (string zone in game.Graph.Def.Zones.Keys)
            {
                game.PlaceHiddenEvidence(zone, game.Graph.ZoneSpaces(zone).First(s => s.Kind == SpaceKind.Normal).Id);
            }
            bool cursedPlaced = false;
            foreach (var poi in game.Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest))
            {
                string target = game.Graph.DistancesFrom(poi.Id, 2, game.State.Overlay).Keys
                    .First(id => game.Graph.Space(id).Kind == SpaceKind.Normal);
                game.PlacePoiToken(poi.Id, target, cursedFront: !cursedPlaced);
                cursedPlaced = true;
            }
            game.PlaceAdversary(MorgonnodSpace);
            if (!complete)
            {
                return game; // still in AdversarySetup, for the placement-validation tests
            }
            game.SetupCultists(CultistSpaces(investigators), AltarSpace);
            game.SetupAdversaryCards(attack, (abilities ?? DefaultAbilities).Take(AbilityCount(investigators)).ToList());
            game.FinishAdversarySetup();
            return game;
        }

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        private static AdversaryFigure Cultist(Game game, string id) =>
            game.State.Adversary.Figures.First(f => f.Id == id);

        /// <summary>Take a do-nothing turn for everyone still waiting; the Adversary turn follows.</summary>
        private static void FinishInvestigatorTurns(Game game)
        {
            foreach (var inv in game.State.Investigators
                .Where(i => !i.Dead && !i.Escaped && !i.TurnTakenThisRound).ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
        }

        // ---------- Setup ----------

        [Fact]
        public void Cultist_setup_needs_one_connected_group_beside_morgonnod()
        {
            var game = NewCultGame(complete: false);
            var four = CultistSpaces(4);

            // 4 Investigators means 4 Cultists.
            Assert.Throws<InvalidOperationException>(() => game.SetupCultists(CultistSpaces(2), AltarSpace));
            // Two separate pairs are not a single group.
            Assert.Throws<InvalidOperationException>(() =>
                game.SetupCultists(new List<string> { "S-21", "S-24", "S-33", "S-38" }, AltarSpace));
            // One Cultist off on their own.
            Assert.Throws<InvalidOperationException>(() =>
                game.SetupCultists(new List<string> { "S-21", "S-24", "S-27", "S-29" }, AltarSpace));
            // Two Cultists cannot share a space.
            Assert.Throws<InvalidOperationException>(() =>
                game.SetupCultists(new List<string> { "S-21", "S-21", "S-24", "S-27" }, AltarSpace));
            // The Altar goes on a General space inside a Zone.
            Assert.Throws<InvalidOperationException>(() => game.SetupCultists(four, "S-20")); // Light Switch
            Assert.Throws<InvalidOperationException>(() => game.SetupCultists(four, "1"));    // outdoors, no Zone
            // Mor'gonnod must be adjacent to one of them.
            game.PlaceAdversary("S-15");
            Assert.Throws<InvalidOperationException>(() => game.SetupCultists(four, AltarSpace));
            Assert.Empty(game.State.Adversary.Figures);

            game.PlaceAdversary(MorgonnodSpace);
            game.SetupCultists(four, AltarSpace);
            Assert.Equal(new[] { "c1", "c2", "c3", "c4" }, game.State.Adversary.Figures.Select(f => f.Id));
            Assert.Equal(four, game.State.Adversary.Figures.Select(f => f.Space));
            Assert.All(game.State.Adversary.Figures, f => Assert.True(f.Alive && !f.Revealed));
            Assert.Equal(0, game.State.Adversary.Counters["blood"]);
            Assert.Equal(AltarSpace, game.State.Objective.Tokens["altar"]);
        }

        [Fact]
        public void Cult_setup_starts_with_one_face_up_ability_at_four_investigators()
        {
            var game = NewCultGame();
            var adv = game.State.Adversary;
            Assert.Equal("ravage", adv.AttackCard);
            Assert.Equal(new[] { "razor-like-talons" }, adv.ActiveAbilities);
            Assert.Equal(new[] { "dried-tongue", "severed-ear" }, adv.FaceDownAbilities);
            Assert.Equal(4, adv.KillsToWin); // the Cult must kill every Investigator
        }

        // ---------- Possessed (the Adversary-turn-start hook) ----------

        [Fact]
        public void Possessed_walks_the_investigator_over_and_bloodlets_them_then_discards_itself()
        {
            var game = NewCultGame(abilities: new[] { "severed-ear", "dried-tongue", "razor-like-talons" });
            var aira = Inv(game, "aira");
            FinishInvestigatorTurns(game);
            game.AdversaryEndTurn(); // Severed Ear may not be used on the first round

            aira.Space = "S-24"; // adjacent to Mor'gonnod on S-25 for Severed Ear
            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("severed-ear", new List<string> { "aira" });
            Assert.True(game.HasCondition(aira, "possessed"));
            game.AdversaryEndTurn();

            // Round 3: the Adversary is offered the Condition and cashes it in, dragging aira
            // to a space next to Cultist c1 (S-21) and Bloodletting with them.
            FinishInvestigatorTurns(game);
            int blood = game.State.Adversary.Counters["blood"];

            game.UsePossessed("c1", "aira", "S-18");

            // The Adversary-turn-start hook offered the Condition as the turn opened.
            Assert.Contains(game.State.Log,
                e => e.Type == "condition" && e.Detail.Contains("may use aira's Possessed"));
            Assert.Equal("S-18", aira.Space);
            Assert.Single(aira.Wounds);
            Assert.Equal(blood + 1, game.State.Adversary.Counters["blood"]);
            Assert.False(game.HasCondition(aira, "possessed"));
        }

        [Fact]
        public void Possessed_may_not_walk_an_investigator_more_than_four_spaces()
        {
            var game = NewCultGame(abilities: new[] { "severed-ear", "dried-tongue", "razor-like-talons" });
            var aira = Inv(game, "aira");
            FinishInvestigatorTurns(game);
            game.AdversaryEndTurn(); // Severed Ear may not be used on the first round

            aira.Space = "S-24";
            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("severed-ear", new List<string> { "aira" });
            game.AdversaryEndTurn();
            FinishInvestigatorTurns(game);

            var within4 = game.Graph.DistancesFrom(aira.Space, 4, game.State.Overlay);
            string far = game.Graph.Def.Spaces.Select(sp => sp.Id).First(id => !within4.ContainsKey(id));
            Assert.Throws<InvalidOperationException>(() => game.UsePossessed("c1", "aira", far));
            Assert.Equal("S-24", aira.Space);
            Assert.True(game.HasCondition(aira, "possessed"));
        }

        // ---------- End-of-Adversary-turn follow-ups and token triggers ----------

        [Fact]
        public void Unblinking_eye_gives_paranoid_to_everyone_who_placed_no_flashlight()
        {
            var game = NewCultGame(abilities: new[] { "unblinking-eye", "dried-tongue", "severed-ear" });
            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("unblinking-eye");
            game.AdversaryEndTurn();

            // Round 2: aira places a Flashlight, nobody else does.
            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);
            foreach (string id in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(id);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();

            Assert.False(game.HasCondition(Inv(game, "aira"), "paranoid"));
            foreach (string id in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                Assert.True(game.HasCondition(Inv(game, id), "paranoid"));
            }
        }

        [Fact]
        public void Shriveled_hand_leaves_the_game_when_two_cultists_bloodlet_and_cools_down_when_they_do_not()
        {
            var game = NewCultGame(abilities: new[] { "shriveled-hand", "dried-tongue", "severed-ear" });
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("shriveled-hand");
            game.AdversaryEndTurn();

            // Round 2: only 1 Cultist Bloodlets, so the card goes face-down into Cooldown 1.
            Inv(game, "aira").Space = "S-17"; // adjacent to c1 on S-21
            FinishInvestigatorTurns(game);
            game.Bloodletting("c1", "aira");
            game.AdversaryEndTurn();

            Assert.DoesNotContain("shriveled-hand", adv.ActiveAbilities);
            Assert.Contains(adv.Cooldown1, c => c.CardId == "shriveled-hand");
            Assert.Contains(game.State.Log, e => e.Type == "adversary" && e.Detail.Contains("Cooldown 1"));
        }

        [Fact]
        public void Shriveled_hand_is_removed_from_the_game_when_both_cultists_bloodlet()
        {
            var game = NewCultGame(abilities: new[] { "shriveled-hand", "dried-tongue", "severed-ear" });
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("shriveled-hand");
            game.AdversaryEndTurn();

            Inv(game, "aira").Space = "S-17";     // adjacent to c1 on S-21
            Inv(game, "lucy-belle").Space = "S-31"; // adjacent to c3 on S-27
            FinishInvestigatorTurns(game);
            game.Bloodletting("c1", "aira");
            game.Bloodletting("c3", "lucy-belle");
            game.AdversaryEndTurn();

            Assert.DoesNotContain("shriveled-hand", adv.ActiveAbilities);
            Assert.DoesNotContain(adv.Cooldown1, c => c.CardId == "shriveled-hand");
            Assert.DoesNotContain(adv.Cooldown2, c => c.CardId == "shriveled-hand");
        }

        [Fact]
        public void Burning_heart_ends_on_the_first_round_nobody_gains_charge_or_stamina()
        {
            var game = NewCultGame(abilities: new[] { "burning-heart", "dried-tongue", "severed-ear" });
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("burning-heart");
            game.AdversaryEndTurn();

            // Round 2: somebody Rests, gaining Stamina, so the card stays in effect.
            var aira = Inv(game, "aira");
            aira.Stamina = 2;
            game.BeginInvestigatorTurn("aira");
            game.Rest();
            game.EndTurnWithoutFinalAction();
            foreach (string id in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(id);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
            Assert.Contains("burning-heart", adv.ActiveAbilities);

            // Round 3: nobody gains anything.
            FinishInvestigatorTurns(game);
            game.AdversaryEndTurn();

            Assert.DoesNotContain("burning-heart", adv.ActiveAbilities);
            Assert.Contains(adv.Cooldown2, c => c.CardId == "burning-heart");
        }

        [Fact]
        public void Hellfire_tokens_wound_an_investigator_who_moves_onto_one()
        {
            var game = NewCultGame(abilities: new[] { "cleft-hoof", "dried-tongue", "severed-ear" });
            var aira = Inv(game, "aira");
            aira.Wounds.Add(new WoundInstance { CardId = "breathless", FaceUp = true });
            aira.Space = "S-18";
            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("cleft-hoof", new List<string> { "aira", "S-21", "S-24", "S-22" });
            Assert.False(aira.Wounds[0].FaceUp); // the cost: 1 face-up Wound flipped face-down
            game.AdversaryEndTurn();

            game.BeginInvestigatorTurn("aira");
            game.MoveStep("S-21");

            Assert.Equal(2, aira.Wounds.Count);
            Assert.True(aira.Wounds[1].FaceUp);
        }

        [Fact]
        public void Desecrated_ground_is_rolled_for_at_the_end_of_the_turn_and_ignored_when_bright()
        {
            var game = NewCultGame(abilities: new[] { "twisted-horn", "dried-tongue", "severed-ear" });
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");
            aira.Space = "S-18";
            lucy.Space = "S-25";
            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("twisted-horn", new List<string> { "S-21", "S-24" });
            game.AdversaryEndTurn();

            // A Bright token may be ignored outright.
            game.State.Overlay.BrightSpaces.Add("S-24");
            game.BeginInvestigatorTurn("lucy-belle");
            game.MoveStep("S-24");
            game.EndTurnWithoutFinalAction();
            Assert.Empty(lucy.Wounds);
            Assert.DoesNotContain(game.State.Log, e => e.Detail.Contains("Desecrated Ground they walked on"));

            // An unlit one owes a D6 at the end of the turn.
            game.BeginInvestigatorTurn("aira");
            game.MoveStep("S-21");
            Assert.Empty(aira.Wounds); // nothing happens until the turn ends
            game.EndTurnWithoutFinalAction();
            Assert.Contains(game.State.Log, e => e.Detail.Contains("Desecrated Ground they walked on"));
        }

        // ---------- Cultist Actions ----------

        [Fact]
        public void Each_cultist_moves_on_their_own_three_plus_shared_sprint_budget()
        {
            var game = NewCultGame();
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game);

            game.CultistMoveStep("c1", "S-17");
            int budget = 3 + adv.SprintRolled;
            Assert.InRange(budget, 5, 7);
            Assert.Equal(budget - 1, adv.Counters["cmp:c1"]);
            Assert.Equal(budget, adv.Counters["cmp:c2"]);
            Assert.Equal(budget, adv.MpRemaining); // Mor'gonnod keeps his own budget
            // Shadow tokens were updated before anyone acted.
            Assert.Equal("S-21", adv.ShadowTokens["c1"]);
            Assert.Equal(MorgonnodSpace, adv.ShadowTokens["main"]);
            Assert.Equal("S-17", Cultist(game, "c1").Space);

            adv.Counters["cmp:c1"] = 0;
            Assert.Throws<InvalidOperationException>(() => game.CultistMoveStep("c1", "S-18"));
        }

        [Fact]
        public void A_cultist_may_not_act_again_once_a_different_cultist_has_acted()
        {
            var game = NewCultGame();
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game);

            game.CultistMoveStep("c1", "S-17");
            game.CultistMoveStep("c1", "S-18"); // still c1's own Actions
            game.CultistMoveStep("c2", "S-25"); // c2 starts: c1 is done
            Assert.Equal(1, adv.Counters["cfin:c1"]);
            Assert.Throws<InvalidOperationException>(() => game.CultistMoveStep("c1", "S-21"));
            game.CultistMoveStep("c2", "S-26"); // c2 may keep going
            Assert.Equal("S-26", Cultist(game, "c2").Space);
        }

        [Fact]
        public void A_cultist_moving_onto_a_bright_space_is_revealed()
        {
            var game = NewCultGame();
            FinishInvestigatorTurns(game);
            game.State.Overlay.BrightSpaces.Add("S-17");
            game.CultistMoveStep("c1", "S-17");
            Assert.True(Cultist(game, "c1").Revealed);
            Assert.False(game.State.Adversary.Revealed); // Mor'gonnod is untouched
        }

        [Fact]
        public void Only_one_cultist_may_break_a_door_each_round()
        {
            var game = NewCultGame();
            FinishInvestigatorTurns(game);
            Cultist(game, "c1").Space = "S-22"; // adjacent to the S-23 door
            game.CultistBreakDoor("c1", "S-23");
            Assert.Equal(DoorState.Damaged, game.State.Overlay.DoorState("S-23"));

            Cultist(game, "c2").Space = "S-20";
            Assert.Throws<InvalidOperationException>(() => game.CultistBreakDoor("c2", "S-23"));
        }

        // ---------- Bloodletting ----------

        [Fact]
        public void Bloodletting_is_once_per_turn_never_in_round_one_and_flips_an_ability()
        {
            var game = NewCultGame();
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            FinishInvestigatorTurns(game);
            aira.Space = MorgonnodSpace; // adjacent to c1 (S-21) and c2 (S-24)
            Assert.Throws<InvalidOperationException>(() => game.Bloodletting("c1", "aira"));
            game.AdversaryEndTurn();
            Assert.Equal(2, game.State.Round);

            FinishInvestigatorTurns(game);
            Assert.Throws<InvalidOperationException>(() => game.Bloodletting("c4", "aira")); // c4 on S-30 is not adjacent
            game.Bloodletting("c1", "aira");
            Assert.Single(aira.Wounds);
            Assert.False(aira.Wounds[0].FaceUp);
            Assert.Equal(1, adv.Counters["blood"]);
            Assert.Equal("S-21", adv.ShadowTokens["c1"]);
            Assert.Equal(new[] { "razor-like-talons", "dried-tongue" }, adv.ActiveAbilities);
            Assert.Equal(new[] { "severed-ear" }, adv.FaceDownAbilities);

            // 1 Cultist per Adversary turn, not 1 per Cultist.
            Assert.Throws<InvalidOperationException>(() => game.Bloodletting("c2", "aira"));
            Assert.Equal(1, adv.Counters["blood"]);
        }

        [Fact]
        public void A_cultist_who_disappears_may_not_bloodlet_but_another_still_may()
        {
            var game = NewCultGame();
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            FinishInvestigatorTurns(game);
            game.AdversaryEndTurn(); // Bloodletting is illegal in round 1
            aira.Space = MorgonnodSpace;
            FinishInvestigatorTurns(game);

            Cultist(game, "c1").Revealed = true; // caught in the light during the Investigators' turns
            Assert.Throws<InvalidOperationException>(() => game.Bloodletting("c1", "aira")); // Revealed card
            game.CultistDisappear("c1");
            Assert.False(Cultist(game, "c1").Revealed);
            Assert.Equal("S-21", adv.ShadowTokens["c1"]);
            Assert.Throws<InvalidOperationException>(() => game.Bloodletting("c1", "aira"));

            game.Bloodletting("c2", "aira");
            Assert.Equal(1, adv.Counters["blood"]);
        }

        [Fact]
        public void Dried_tongue_makes_the_next_rounds_bloodletting_face_up()
        {
            var game = NewCultGame(investigators: 2, abilities: new[] { "dried-tongue" });
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("dried-tongue");
            Assert.Equal(2, adv.Counters["dried-tongue-round"]);
            game.AdversaryEndTurn();

            aira.Space = MorgonnodSpace;
            FinishInvestigatorTurns(game);
            game.Bloodletting("c1", "aira");
            Assert.Single(aira.Wounds);
            Assert.True(aira.Wounds[0].FaceUp);
        }

        // ---------- The Final Sacrifice ----------

        [Fact]
        public void The_final_sacrifice_needs_five_blood_and_the_whole_group()
        {
            var game = NewCultGame();
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game);

            Assert.Throws<InvalidOperationException>(() => game.TheFinalSacrifice()); // Blood track at 0
            adv.Counters["blood"] = 5;

            Cultist(game, "c4").Space = "S-38"; // out of the group
            Assert.Throws<InvalidOperationException>(() => game.TheFinalSacrifice());
            Cultist(game, "c4").Space = "S-30";

            adv.Space = "S-15"; // Mor'gonnod not adjacent to any Cultist
            Assert.Throws<InvalidOperationException>(() => game.TheFinalSacrifice());
            adv.Space = MorgonnodSpace;
            Assert.True(adv.Figures.All(f => f.Alive));
        }

        [Fact]
        public void The_final_sacrifice_consumes_the_cultists_and_ends_the_turn()
        {
            var game = NewCultGame();
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game);
            adv.Counters["blood"] = 5;

            game.TheFinalSacrifice();
            Assert.All(adv.Figures, f => Assert.False(f.Alive));
            Assert.True(adv.Revealed);
            Assert.Equal(1, adv.Counters["corporeal"]);
            Assert.Empty(adv.ShadowTokens);
            Assert.Equal(2, game.State.Round); // "...and end your turn"
            Assert.Throws<InvalidOperationException>(() => game.TheFinalSacrifice());
        }

        // ---------- Corporeal Mor'gonnod ----------

        [Fact]
        public void Ethereal_morgonnod_may_never_attack_but_corporeal_ravages()
        {
            var game = NewCultGame();
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            FinishInvestigatorTurns(game);
            aira.Space = "S-21"; // adjacent to Mor'gonnod on S-25
            Assert.Throws<InvalidOperationException>(() =>
                game.PlayAdversaryCard("ravage", new List<string> { "aira" }));

            adv.Counters["blood"] = 5;
            game.TheFinalSacrifice();
            FinishInvestigatorTurns(game);

            game.PlayAdversaryCard("ravage", new List<string> { "aira" });
            Assert.False(adv.AttackLockedThisTurn); // Corporeal ignores the Revealed card
            Assert.Equal(3, aira.Wounds.Count);
            Assert.All(aira.Wounds, w => Assert.True(w.FaceUp));
        }

        [Fact]
        public void Corporeal_immolate_may_repeat_on_a_different_investigator()
        {
            var game = NewCultGame(attack: "immolate");
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game);
            adv.Counters["blood"] = 5;
            game.TheFinalSacrifice();
            FinishInvestigatorTurns(game);

            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");
            aira.Space = "S-21";
            lucy.Space = "S-24";
            Assert.Throws<InvalidOperationException>(() =>
                game.PlayAdversaryCard("immolate", new List<string> { "aira", "aira" }));
            game.PlayAdversaryCard("immolate", new List<string> { "aira", "lucy-belle" });
            Assert.Equal(2, aira.Wounds.Count);
            Assert.Equal(2, lucy.Wounds.Count);
        }

        [Fact]
        public void Corporeal_movement_has_its_own_ten_mp_and_pays_two_for_bright()
        {
            var game = NewCultGame();
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game);
            adv.Counters["blood"] = 5;
            game.TheFinalSacrifice();
            FinishInvestigatorTurns(game);

            game.State.Overlay.BrightSpaces.Add("S-26");
            game.MorgonnodCorporealMoveStep("S-24"); // Dark: 1 MP
            // Corporeal Mor'gonnod spends the shared MP pool: a flat 10, no Sprint die.
            Assert.Equal(0, adv.SprintRolled);
            Assert.Equal(9, adv.MpRemaining);
            game.MorgonnodCorporealMoveStep("S-25");
            Assert.Equal(8, adv.MpRemaining);
            game.MorgonnodCorporealMoveStep("S-26"); // Bright: 2 MP
            Assert.Equal(6, adv.MpRemaining);
            Assert.Equal("S-26", adv.Space);

            adv.MpRemaining = 1;
            game.State.Overlay.BrightSpaces.Add("S-28");
            Assert.Throws<InvalidOperationException>(() => game.MorgonnodCorporealMoveStep("S-28"));
            // The shared entry point is the same path, not a second one that would undercharge.
            Assert.Throws<InvalidOperationException>(() => game.AdversaryMoveStep("S-28"));
        }

        [Fact]
        public void Ethereal_abilities_need_a_hidden_morgonnod_except_spiked_vertebrae()
        {
            var game = NewCultGame(investigators: 2, abilities: new[] { "razor-like-talons" });
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            FinishInvestigatorTurns(game);
            aira.Space = "S-24"; // adjacent to Mor'gonnod
            game.GainWound(aira, faceUp: false);

            adv.Revealed = true;
            Assert.Throws<InvalidOperationException>(() =>
                game.PlayAdversaryCard("razor-like-talons", new List<string> { "aira" }));
            adv.ActiveAbilities.Add("spiked-vertebrae"); // as if a Bloodletting had flipped it face-up
            game.PlayAdversaryCard("spiked-vertebrae");  // legal even while Revealed

            adv.Revealed = false;
            game.PlayAdversaryCard("razor-like-talons", new List<string> { "aira" });
            Assert.True(aira.Wounds[0].FaceUp);
            Assert.Equal(MorgonnodSpace, adv.ShadowTokens["main"]);
        }

        // ---------- The Altar (banish) ----------

        [Fact]
        public void The_altar_banish_flow_wins_the_game_for_the_investigators()
        {
            var game = NewCultGame(investigators: 2);
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");

            Assert.Throws<InvalidOperationException>(() => game.PlaceRitualTokens("S-21", "S-24"));
            game.SelectEscapeCard("the-altar");
            Assert.Equal("the-altar", game.State.Objective.SelectedEscapeCard);
            Assert.Throws<InvalidOperationException>(() => game.PlaceRitualTokens("S-20", "S-24")); // not General
            Assert.Throws<InvalidOperationException>(() => game.PlaceRitualTokens("S-21", "S-21")); // same space
            game.PlaceRitualTokens("S-21", "S-24");

            // Round 1: collect the tokens and light the Altar's Zone.
            game.BeginInvestigatorTurn("aira");
            aira.Space = "S-21";
            game.PickUpBanishToken("ritual-knife");
            aira.Space = AltarSpace;
            Assert.Throws<InvalidOperationException>(() => game.UseRitualKnife(false)); // Altar not Revealed
            aira.Space = "S-20";
            game.ActivateLightSwitch();
            aira.Space = AltarSpace;
            game.UseRitualKnife(flipFaceDownWound: false);
            Assert.Single(aira.Wounds);
            Assert.False(aira.Wounds[0].FaceUp);
            Assert.Equal(1, game.State.Adversary.Counters["banish-supplies"]);
            Assert.Equal(1, game.State.Adversary.Counters["altar-revealed"]);

            game.BeginInvestigatorTurn("lucy-belle");
            lucy.Space = "S-24";
            game.PickUpBanishToken("rope-circle");
            lucy.Space = "S-17"; // adjacent to the Altar space
            Assert.Throws<InvalidOperationException>(() => game.CutRopeCircle()); // 1 of 3 Supplies
            game.EndTurnWithoutFinalAction();
            game.AdversaryEndTurn();

            // Rounds 2 and 3: 1 Supply per round, the Altar stays Revealed after the lights die.
            for (int round = 2; round <= 3; round++)
            {
                Assert.Equal(round, game.State.Round);
                game.BeginInvestigatorTurn("aira");
                game.UseRitualKnife(flipFaceDownWound: false);
                Assert.Equal(round, game.State.Adversary.Counters["banish-supplies"]);
                if (round == 2)
                {
                    game.BeginInvestigatorTurn("lucy-belle");
                    game.EndTurnWithoutFinalAction();
                    game.AdversaryEndTurn();
                }
            }
            Assert.Equal(3, aira.Wounds.Count);

            game.BeginInvestigatorTurn("lucy-belle");
            game.CutRopeCircle();
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
        }

        [Fact]
        public void The_ritual_knife_can_flip_a_face_down_wound_instead_of_adding_one()
        {
            var game = NewCultGame(investigators: 2);
            game.SelectEscapeCard("the-altar");
            game.PlaceRitualTokens("S-21", "S-24");
            var aira = Inv(game, "aira");

            game.BeginInvestigatorTurn("aira");
            aira.Space = "S-21";
            game.PickUpBanishToken("ritual-knife");
            Assert.Throws<InvalidOperationException>(() => game.PickUpBanishToken("altar"));
            aira.Space = "S-20";
            game.ActivateLightSwitch();
            aira.Space = AltarSpace;
            Assert.Throws<InvalidOperationException>(() => game.UseRitualKnife(flipFaceDownWound: true));

            game.GainWound(aira, faceUp: false);
            game.UseRitualKnife(flipFaceDownWound: true);
            Assert.Single(aira.Wounds);
            Assert.True(aira.Wounds[0].FaceUp);
            Assert.Equal(1, game.State.Adversary.Counters["banish-supplies"]);

            // Once per round for the whole team.
            game.BeginInvestigatorTurn("lucy-belle");
            game.State.Objective.TokenCarriers["ritual-knife"] = "lucy-belle";
            Inv(game, "lucy-belle").Space = AltarSpace;
            Assert.Throws<InvalidOperationException>(() => game.UseRitualKnife(flipFaceDownWound: false));
        }
    }
}
