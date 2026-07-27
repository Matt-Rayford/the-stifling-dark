using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// The 10 base Investigators' Minor and Major Abilities
    /// (src/StiflingDark.Engine/Core/Game.InvestigatorAbilities.cs), plus the shared plumbing
    /// they unblocked: the Disoriented Wound's Ability lockout, the Fear Wound's forced Major,
    /// and the Two-way Radio / Blood Chalice cards that borrow someone else's Ability.
    /// </summary>
    public class InvestigatorAbilityTests
    {
        // The Sawmill has exactly 4 Start spaces, so a test game seats up to 4 Investigators.
        private static readonly string[] StartSpaces = { "285", "286", "305", "307" };
        private static readonly List<string> AllMedicalSpaces = new List<string> { "24", "208" };

        private static Game NewGame(string[] invIds, ulong seed = 1234, string adversary = "butcher")
        {
            var starts = new Dictionary<string, string>();
            for (int i = 0; i < invIds.Length; i++)
            {
                starts[invIds[i]] = StartSpaces[i];
            }
            int medical = TestData.Db.Config.ByInvestigatorCount[invIds.Length].MedicalItemsOnBoard;
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = adversary,
                InvestigatorStartSpaces = starts,
                MedicalItemSpaces = AllMedicalSpaces.Take(medical).ToList(),
            });
            CompleteAdversarySetup(game);
            return game;
        }

        private static void CompleteAdversarySetup(Game game)
        {
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
        }

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        /// <summary>Finish every outstanding Investigator turn, handing the phase to the Adversary.</summary>
        private static void EndInvestigatorTurns(Game game)
        {
            foreach (var inv in game.State.Investigators.Where(i => !i.TurnTakenThisRound).ToList())
            {
                if (game.State.ActiveInvestigator == null)
                {
                    game.BeginInvestigatorTurn(inv.DefId);
                }
                game.EndTurnWithoutFinalAction();
            }
        }

        /// <summary>Walk the round forward to the next Investigator phase.</summary>
        private static void EndRound(Game game)
        {
            EndInvestigatorTurns(game);
            game.AdversaryEndTurn();
        }

        private static WoundInstance GiveFaceUpWound(Game game, InvestigatorState inv, string cardId)
        {
            var wound = new WoundInstance { CardId = cardId, FaceUp = false };
            inv.Wounds.Add(wound);
            game.FlipWoundFaceUp(inv, wound);
            return wound;
        }

        // ---------- Aira Willson ----------

        [Fact]
        public void Aira_minor_takes_an_Involved_Action_without_ending_her_turn()
        {
            var game = NewGame(new[] { "aira", "mitchell" });
            game.BeginInvestigatorTurn("aira");
            game.UseMinorAbility();

            game.TakeInvolvedAction();
            Assert.Equal("aira", game.State.ActiveInvestigator);
            Assert.Equal(FinalActionKind.None, Inv(game, "aira").FinalAction);

            // "...but you may not perform any other Involved Action during that turn."
            Assert.NotEmpty(game.ActionBlockers("aira", Game.ActionInvolved));
            Assert.Throws<InvalidOperationException>(() => game.TakeInvolvedAction());

            // A different Final Action still ends the turn normally.
            game.PlaceFlashlight(0.0);
            Assert.Null(game.State.ActiveInvestigator);
        }

        [Fact]
        public void Aira_major_reveals_an_Adversary_on_or_next_to_a_named_space()
        {
            var game = NewGame(new[] { "aira", "mitchell" });
            var aira = Inv(game, "aira");
            game.BeginInvestigatorTurn("aira");
            Assert.False(game.State.Adversary.Revealed);

            // The Adversary stands on S-25; S-24 is adjacent to it.
            game.UseMajorAbility(args: new List<string> { "S-24", "G-1" });

            Assert.True(game.State.Adversary.Revealed);
            Assert.Equal(0, aira.MajorAbilityTokens);
            // "...does not make the spaces Bright."
            Assert.DoesNotContain("S-24", game.State.Overlay.BrightSpaces);
        }

        // ---------- Asher Palacios ----------

        [Fact]
        public void Asher_minor_ignores_the_face_up_Wound_in_his_first_slot()
        {
            var game = NewGame(new[] { "asher", "mitchell" });
            var asher = Inv(game, "asher");
            GiveFaceUpWound(game, asher, "breathless"); // "lose 1 Stamina at the end of each of your turns"

            game.BeginInvestigatorTurn("asher");
            game.EndTurnWithoutFinalAction();
            Assert.Equal(5, asher.Stamina); // slot 1: ignored

            // Push it out of the first slot and it bites again. (An Involved final, so the
            // automatic Rest does not mask the bite.)
            asher.Wounds.Insert(0, new WoundInstance { CardId = "drain", FaceUp = true });
            EndRound(game);
            game.BeginInvestigatorTurn("asher");
            game.TakeInvolvedAction();
            Assert.Equal(4, asher.Stamina);
        }

        [Fact]
        public void Asher_major_grants_Stamina_and_MP_and_ignores_every_face_up_Wound()
        {
            var game = NewGame(new[] { "asher", "mitchell" });
            var asher = Inv(game, "asher");
            asher.Stamina = 3;
            // Slot 0 is ignored by his Minor no matter what sits there, so the Wounds that
            // have to still bite before the Major fires go in slots 1 and 2.
            asher.Wounds.Add(new WoundInstance { CardId = "commiserate", FaceUp = true });
            GiveFaceUpWound(game, asher, "drain");        // "you may no longer take the Charge Final Action"
            GiveFaceUpWound(game, asher, "breathless");   // "lose 1 Stamina at the end of each of your turns"

            game.BeginInvestigatorTurn("asher");
            Assert.NotEmpty(game.ActionBlockers("asher", Game.ActionCharge)); // Drain, still in force
            game.UseMajorAbility();

            Assert.Equal(4, asher.Stamina);
            Assert.Equal(6, asher.MpRemaining);
            Assert.Empty(game.ActionBlockers("asher", Game.ActionCharge));
            game.EndTurnWithoutFinalAction();
            // Breathless ignored; the automatic Rest's +1 lands unopposed (a biting
            // Breathless would have cancelled it back down to 4).
            Assert.Equal(5, asher.Stamina);

            // The Ability lasted "during this turn" only.
            EndRound(game);
            game.BeginInvestigatorTurn("asher");
            Assert.NotEmpty(game.ActionBlockers("asher", Game.ActionCharge));
        }

        // ---------- Brielle Easton ----------

        [Fact]
        public void Brielle_minor_places_Cans_the_Adversary_kicks_over()
        {
            var game = NewGame(new[] { "brielle", "mitchell" });
            game.BeginInvestigatorTurn("brielle");
            game.UseMinorAbility(args: new List<string> { "272", "284" });
            Assert.Equal(2, game.BoardTokenIds("can:brielle:").Count);
            // "...within 3 spaces of yourself."
            Assert.Throws<InvalidOperationException>(() => game.UseMinorAbility(args: new List<string> { "G-1" }));
            game.EndTurnWithoutFinalAction();
            game.BeginInvestigatorTurn("mitchell");
            game.EndTurnWithoutFinalAction();

            game.State.Adversary.Space = "271";
            game.AdversaryMoveStep("272");

            Assert.Empty(game.BoardTokenIds("can:brielle:").Where(id => game.BoardTokenSpace(id) == "272"));
            Assert.True(game.HasBoardTokenAt("noise:brielle:", "272"));
            Assert.True(game.HasBoardTokenAt("can:brielle:", "284")); // the other Can is untouched
        }

        [Fact]
        public void Brielle_major_lets_everyone_ignore_this_round_s_Event()
        {
            var game = NewGame(new[] { "brielle", "mitchell" });
            // Stand in for whatever Event was drawn: a 1 MP penalty on every turn this round.
            game.SetRoundModifier(Game.MpPenaltyKey, 1);

            game.BeginInvestigatorTurn("brielle");
            game.UseMajorAbility();
            Assert.True(game.HasRoundModifier(Game.EventIgnoredKey));
            game.EndTurnWithoutFinalAction();

            game.BeginInvestigatorTurn("mitchell");
            Assert.Equal(4, Inv(game, "mitchell").MpRemaining); // no penalty applied
            game.EndTurnWithoutFinalAction();

            // The reprieve is for one round only.
            game.AdversaryEndTurn();
            Assert.False(game.HasRoundModifier(Game.EventIgnoredKey));
        }

        // ---------- Dylan J. Lee ----------

        [Fact]
        public void Dylan_minor_treats_up_to_3_Dark_spaces_a_turn_as_Dim()
        {
            var game = NewGame(new[] { "dylan", "mitchell" });
            var dylan = Inv(game, "dylan");
            game.BeginInvestigatorTurn("dylan");
            dylan.Space = "G-1"; // Dark, next to the equally Dark G-2
            dylan.MpRemaining = 20;

            game.MoveStep("G-2");
            game.MoveStep("G-1");
            game.MoveStep("G-2");
            Assert.Equal(17, dylan.MpRemaining); // 3 Dark spaces charged as Dim
            game.MoveStep("G-1");
            Assert.Equal(15, dylan.MpRemaining); // the 4th costs the printed 2

            // The allowance is per turn.
            game.EndTurnWithoutFinalAction();
            EndRound(game);
            game.BeginInvestigatorTurn("dylan");
            dylan.MpRemaining = 20;
            game.MoveStep("G-2");
            Assert.Equal(19, dylan.MpRemaining);
        }

        [Fact]
        public void Dylan_major_drops_an_Escape_Artist_token_he_can_teleport_back_to()
        {
            var game = NewGame(new[] { "dylan", "mitchell" });
            var dylan = Inv(game, "dylan");
            game.BeginInvestigatorTurn("dylan");
            game.UseMajorAbility(args: new List<string> { "271" });
            Assert.Equal("271", game.BoardTokenSpace("escape-artist:dylan"));
            Assert.Equal(0, dylan.MajorAbilityTokens);

            game.MoveStep("306");
            Assert.Equal("306", dylan.Space);

            // The return half is free: no second token is needed.
            game.UseMajorAbility();
            Assert.Equal("271", dylan.Space);
            Assert.Equal(0, dylan.MajorAbilityTokens);
            Assert.Null(game.BoardTokenSpace("escape-artist:dylan"));
        }

        [Fact]
        public void Dylan_Escape_Artist_token_is_removed_at_the_end_of_his_next_turn()
        {
            var game = NewGame(new[] { "dylan", "mitchell" });
            game.BeginInvestigatorTurn("dylan");
            game.UseMajorAbility(args: new List<string> { "271" });
            game.EndTurnWithoutFinalAction();
            EndRound(game);

            // Round 2 is still inside the window ("this round or the next round").
            game.BeginInvestigatorTurn("dylan");
            Assert.Equal("271", game.BoardTokenSpace("escape-artist:dylan"));
            game.EndTurnWithoutFinalAction();
            Assert.Null(game.BoardTokenSpace("escape-artist:dylan"));

            EndRound(game);
            game.BeginInvestigatorTurn("dylan");
            // No token and no Major Ability token left: nothing to do.
            Assert.Throws<InvalidOperationException>(() => game.UseMajorAbility());
        }

        // ---------- Ibraheem Hess ----------

        [Fact]
        public void Ibraheem_minor_floors_his_Footprint_at_4_and_charges_his_Sprint_instead()
        {
            var game = NewGame(new[] { "ibraheem", "mitchell" });
            var ibraheem = Inv(game, "ibraheem");
            GiveFaceUpWound(game, ibraheem, "pulled-hammy"); // -2 Footprint when Moving

            game.BeginInvestigatorTurn("ibraheem");
            Assert.Equal(4, ibraheem.MpRemaining); // never below 4

            game.Sprint();
            // Sprint faces are 2-4; the 2 refunded Footprint come off the roll (min 1).
            Assert.InRange(ibraheem.MpRemaining, 4 + 1, 4 + 2);
        }

        [Fact]
        public void Ibraheem_major_stretches_his_Trade_range_to_5_spaces()
        {
            var game = NewGame(new[] { "ibraheem", "mitchell" });
            var mitchell = Inv(game, "mitchell");
            mitchell.Space = "303"; // 3 spaces from Ibraheem's Start
            Inv(game, "ibraheem").Items.Add("energy-bar");

            game.BeginInvestigatorTurn("ibraheem");
            Assert.Throws<InvalidOperationException>(() => game.TradeItem("mitchell", "energy-bar"));

            game.UseMajorAbility();
            game.TradeItem("mitchell", "energy-bar");
            Assert.Contains("energy-bar", mitchell.Items);
        }

        // ---------- Lucy Belle ----------

        [Fact]
        public void Lucy_minor_counts_a_rolled_2_on_the_Sprint_die_as_3()
        {
            // Sprint faces are [2,2,3,3,3,4]; find a seed that actually rolls a 2 for her.
            bool sawIt = false;
            for (ulong seed = 1; seed <= 40 && !sawIt; seed++)
            {
                var game = NewGame(new[] { "lucy-belle", "mitchell" }, seed);
                game.BeginInvestigatorTurn("lucy-belle");
                game.Sprint();
                var log = game.State.Log.Last(e => e.Type == "sprint");
                if (!log.Detail.Contains("rolled 2 MP"))
                {
                    continue;
                }
                sawIt = true;
                Assert.Contains("adjusted to 3", log.Detail);
                Assert.Equal(7, Inv(game, "lucy-belle").MpRemaining);
                Assert.Contains(game.State.Log, e => e.Type == "ability" && e.Detail.Contains("counts a rolled 2 as 3"));
            }
            Assert.True(sawIt, "no seed in 1..40 rolled a 2 on the Sprint die");
        }

        [Fact]
        public void Lucy_major_barricades_block_the_Adversary_until_broken_twice()
        {
            var game = NewGame(new[] { "lucy-belle", "mitchell" });
            game.BeginInvestigatorTurn("lucy-belle");
            game.UseMajorAbility(args: new List<string> { "286", "306" });
            Assert.Equal(2, game.BoardTokenIds("barricade:lucy-belle:").Count);
            // "Investigators may Move through them."
            Assert.NotNull(game.Graph.TryStep(FigureKind.Investigator, "285", "286", game.State.Overlay));
            Assert.Null(game.Graph.TryStep(FigureKind.Adversary, "285", "286", game.State.Overlay));
            game.EndTurnWithoutFinalAction();
            EndInvestigatorTurns(game);

            game.State.Adversary.Space = "272";
            Assert.Throws<InvalidOperationException>(() => game.AdversaryMoveStep("286"));
            game.AdversaryBreakDoor("286"); // Damaged: still in the way
            Assert.Throws<InvalidOperationException>(() => game.AdversaryMoveStep("286"));
            Assert.Throws<InvalidOperationException>(() => game.AdversaryBreakDoor("286")); // once per turn
            game.AdversaryEndTurn();

            EndInvestigatorTurns(game);
            game.State.Adversary.Space = "272";
            game.AdversaryBreakDoor("286"); // Destroyed: the token is removed
            Assert.Single(game.BoardTokenIds("barricade:lucy-belle:"));
            Assert.DoesNotContain("286", game.State.Overlay.AdversaryBarriers);
            game.AdversaryMoveStep("286");
            Assert.Equal("286", game.State.Adversary.Space);
        }

        // ---------- Mada K. Rorrim ----------

        [Fact]
        public void Mada_minor_gains_a_Coin_on_a_Reveal_and_spends_it_beside_a_targeted_Investigator()
        {
            var game = NewGame(new[] { "aira", "mada" });
            var mada = Inv(game, "mada");
            Assert.False(game.State.Adversary.Counters.ContainsKey("coin:mada"));

            game.BeginInvestigatorTurn("aira");
            game.UseMajorAbility(args: new List<string> { "S-24", "G-1" }); // Reveals the Adversary
            Assert.Equal(1, game.State.Adversary.Counters["coin:mada"]);

            // Reacting to an Ability aimed at Aira, out of Mada's own turn.
            game.UseMinorAbility("mada", new List<string> { "aira", "271" });
            Assert.Equal("271", mada.Space);
            Assert.False(game.State.Adversary.Counters.ContainsKey("coin:mada"));
            Assert.Contains(game.State.Log, e => e.Type == "ability" && e.Detail.Contains("flipped the Coin"));
            Assert.Throws<InvalidOperationException>(() => game.UseMinorAbility("mada", new List<string> { "aira" }));
        }

        [Fact]
        public void Mada_major_burns_5_Stamina_for_3_Sprint_rolls_and_no_Wound()
        {
            var game = NewGame(new[] { "aira", "mada" });
            var mada = Inv(game, "mada");
            game.BeginInvestigatorTurn("mada");

            game.UseMajorAbility();

            Assert.Equal(0, mada.Stamina);
            Assert.Empty(mada.Wounds); // "does not incur a face-down Wound"
            Assert.InRange(mada.MpRemaining, 4 + 3 * 2, 4 + 3 * 4);
            Assert.True(mada.SprintedOrRested);
            Assert.Throws<InvalidOperationException>(() => game.Sprint());
        }

        // ---------- Marci Jo ----------

        [Fact]
        public void Marci_minor_only_takes_a_Wound_when_her_Stamina_reaches_0()
        {
            var game = NewGame(new[] { "aira", "marci" });
            var aira = Inv(game, "aira");
            var marci = Inv(game, "marci");
            aira.Items.Add("binding-tablet");   // "Lose 4 Stamina"
            marci.Items.Add("binding-tablet");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("binding-tablet");
            Assert.Equal(1, aira.Stamina);
            Assert.Single(aira.Wounds); // Aira's track has a Wound icon on space 1
            game.EndTurnWithoutFinalAction();

            game.BeginInvestigatorTurn("marci");
            game.UseItem("binding-tablet");
            Assert.Equal(1, marci.Stamina);
            Assert.Empty(marci.Wounds); // hers does not
            game.Sprint();
            Assert.Equal(0, marci.Stamina);
            Assert.Single(marci.Wounds);
        }

        [Fact]
        public void Marci_major_walks_two_other_Investigators_up_to_2_spaces()
        {
            var game = NewGame(new[] { "marci", "aira", "mitchell" });
            var aira = Inv(game, "aira");
            var mitchell = Inv(game, "mitchell");
            game.BeginInvestigatorTurn("marci");

            game.UseMajorAbility(args: new List<string> { "aira", "273", "mitchell", "304" });

            Assert.Equal("273", aira.Space);
            Assert.Equal("304", mitchell.Space);
            Assert.Equal(0, Inv(game, "marci").MajorAbilityTokens);
        }

        // ---------- Mitchell Carter ----------

        [Fact]
        public void Mitchell_minor_Sweeps_his_Flashlight_to_a_second_position_once()
        {
            var game = NewGame(new[] { "mitchell", "aira" });
            game.BeginInvestigatorTurn("mitchell");
            game.PlaceFlashlight(0.0);
            var placement = game.State.Flashlights.Single(f => f.InvestigatorId == "mitchell");
            var first = placement.BrightSpaces.ToList();
            Assert.Null(game.State.ActiveInvestigator); // placing already ended his turn

            game.UseMinorAbility("mitchell", new List<string> { "3.14159265" });

            Assert.NotEqual(first, placement.BrightSpaces);
            Assert.All(placement.BrightSpaces, s => Assert.Contains(s, game.State.Overlay.BrightSpaces));
            // Spaces only the 1st position lit are dark again.
            foreach (string space in first.Where(s => !placement.BrightSpaces.Contains(s)))
            {
                Assert.DoesNotContain(space, game.State.Overlay.BrightSpaces);
            }
            // "once per Flashlight"
            Assert.Throws<InvalidOperationException>(
                () => game.UseMinorAbility("mitchell", new List<string> { "1.5" }));
        }

        [Fact]
        public void Mitchell_major_reveals_an_Adversary_within_2_of_a_chosen_Investigator()
        {
            var game = NewGame(new[] { "mitchell", "aira" });
            var aira = Inv(game, "aira");
            game.BeginInvestigatorTurn("mitchell");

            // Nobody is near S-25 yet.
            game.UseMajorAbility(args: new List<string> { "aira" });
            Assert.False(game.State.Adversary.Revealed);

            aira.Space = "S-24"; // adjacent to the Adversary
            Inv(game, "mitchell").MajorAbilityTokens = 1;
            game.UseMajorAbility(args: new List<string> { "aira" });
            Assert.True(game.State.Adversary.Revealed);
        }

        // ---------- Vincent Campbell ----------

        [Fact]
        public void Vincent_minor_Scouts_nearby_Point_of_Interest_tokens_face_down()
        {
            var game = NewGame(new[] { "vincent", "aira" });
            var vincent = Inv(game, "vincent");
            game.BeginInvestigatorTurn("vincent");
            Assert.Throws<InvalidOperationException>(() => game.UseMinorAbility());

            vincent.Space = "42"; // a printed Point of Interest space
            game.UseMinorAbility();

            var scouted = game.State.PoiTokens.Where(p => p.ScoutedFaceDown).ToList();
            Assert.NotEmpty(scouted);
            Assert.All(scouted, p => Assert.True(
                game.Graph.DistancesFrom("42", 2, game.State.Overlay).ContainsKey(p.TokenSpace)));
            Assert.All(scouted, p => Assert.False(p.Revealed)); // face-down, not face-up
        }

        [Fact]
        public void Vincent_major_needs_an_Item_before_it_draws_a_Cursed_Item()
        {
            var game = NewGame(new[] { "vincent", "aira" });
            var vincent = Inv(game, "vincent");
            game.BeginInvestigatorTurn("vincent");
            Assert.Throws<InvalidOperationException>(() => game.UseMajorAbility());
            Assert.Equal(1, vincent.MajorAbilityTokens); // a refused Ability costs nothing

            vincent.Items.Add("energy-bar");
            int deck = game.State.CursedItemDeck.Count;
            game.UseMajorAbility();

            Assert.Equal(2, vincent.Items.Count);
            Assert.Equal(deck - 1, game.State.CursedItemDeck.Count);
            Assert.Equal(0, vincent.MajorAbilityTokens);
        }

        // ---------- Shared framework ----------

        [Fact]
        public void A_Major_Ability_costs_a_token_and_cannot_be_used_twice_without_regaining_one()
        {
            var game = NewGame(new[] { "aira", "mitchell" });
            var aira = Inv(game, "aira");
            game.BeginInvestigatorTurn("aira");
            game.UseMajorAbility(args: new List<string> { "S-24", "G-1" });
            Assert.Equal(0, aira.MajorAbilityTokens);

            Assert.Throws<InvalidOperationException>(
                () => game.UseMajorAbility(args: new List<string> { "271", "272" }));

            // ...and still refused on a later turn, because tokens are never regained for free.
            game.EndTurnWithoutFinalAction();
            EndRound(game);
            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(
                () => game.UseMajorAbility(args: new List<string> { "271", "272" }));
        }

        [Fact]
        public void A_passive_Minor_Ability_says_where_it_actually_lives()
        {
            var game = NewGame(new[] { "lucy-belle", "ibraheem" });
            game.BeginInvestigatorTurn("lucy-belle");
            var error = Assert.Throws<InvalidOperationException>(() => game.UseMinorAbility());
            Assert.Contains("passive", error.Message);
        }

        [Fact]
        public void Disoriented_blocks_both_Abilities()
        {
            var game = NewGame(new[] { "aira", "brielle" });
            var aira = Inv(game, "aira");
            GiveFaceUpWound(game, aira, "disoriented");

            game.BeginInvestigatorTurn("aira");
            Assert.NotEmpty(game.ActionBlockers("aira", Game.ActionUseAbility));
            Assert.Throws<InvalidOperationException>(() => game.UseMinorAbility());
            Assert.Throws<InvalidOperationException>(
                () => game.UseMajorAbility(args: new List<string> { "S-24", "G-1" }));
            Assert.Equal(1, aira.MajorAbilityTokens);

            // Brielle is unaffected.
            Assert.Empty(game.ActionBlockers("brielle", Game.ActionUseAbility));
        }

        [Fact]
        public void Fear_forces_the_Major_Ability_on_the_next_turn()
        {
            var game = NewGame(new[] { "aira", "mitchell" });
            var aira = Inv(game, "aira");
            game.BeginInvestigatorTurn("aira");
            GiveFaceUpWound(game, aira, "fear");
            game.EndTurnWithoutFinalAction(); // this turn is unaffected
            EndRound(game);

            game.BeginInvestigatorTurn("aira");
            Assert.NotEmpty(game.ActionBlockers("aira", Game.ActionCharge));
            Assert.Throws<InvalidOperationException>(() => game.EndTurnWithoutFinalAction());
            Assert.Throws<InvalidOperationException>(() => game.PlaceFlashlight(0.0));

            game.UseMajorAbility(args: new List<string> { "S-24", "G-1" });
            game.EndTurnWithoutFinalAction();
            Assert.Null(game.State.ActiveInvestigator);
        }

        [Fact]
        public void Fear_has_no_effect_without_a_Major_Ability_token()
        {
            var game = NewGame(new[] { "aira", "mitchell" });
            var aira = Inv(game, "aira");
            aira.MajorAbilityTokens = 0;
            game.BeginInvestigatorTurn("aira");
            GiveFaceUpWound(game, aira, "fear");
            game.EndTurnWithoutFinalAction();
            EndRound(game);

            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction(); // nothing forced
            Assert.Null(game.State.ActiveInvestigator);
        }

        [Fact]
        public void The_Two_way_Radio_borrows_an_out_of_play_Minor_Ability_and_only_1_of_its_tokens()
        {
            var game = NewGame(new[] { "aira", "mitchell" });
            var aira = Inv(game, "aira");
            aira.Items.Add("two-way-radio");
            game.BeginInvestigatorTurn("aira");

            // Mitchell is in play, so his board is out of reach.
            Assert.Throws<InvalidOperationException>(
                () => game.UseItem("two-way-radio", new List<string> { "mitchell" }));
            // "...you may use 1 of them but cannot keep the rest."
            Assert.Throws<InvalidOperationException>(
                () => game.UseItem("two-way-radio", new List<string> { "brielle", "271", "272" }));
            Assert.Contains("two-way-radio", aira.Items); // a refused use does not spend the card

            game.UseItem("two-way-radio", new List<string> { "brielle", "271" });
            Assert.Single(game.BoardTokenIds("can:aira:"));
            Assert.Empty(game.BoardTokenIds("can:brielle:")); // never touches the real owner's supply
            Assert.DoesNotContain("two-way-radio", aira.Items);
        }

        [Fact]
        public void The_Blood_Chalice_borrows_a_Major_Ability_for_a_Wound_instead_of_a_token()
        {
            var game = NewGame(new[] { "aira", "mitchell" });
            var aira = Inv(game, "aira");
            aira.Items.Add("blood-chalice");
            game.BeginInvestigatorTurn("aira");

            game.UseItem("blood-chalice", new List<string> { "lucy-belle", "271", "272" });

            Assert.Equal(1, aira.MajorAbilityTokens);          // "without using a Major Ability token"
            Assert.Single(aira.Wounds.Where(w => w.FaceUp));   // the price
            Assert.Equal(2, game.BoardTokenIds("barricade:aira:").Count);
            Assert.Contains("271", game.State.Overlay.AdversaryBarriers);
        }

        [Fact]
        public void A_Spirit_has_no_Investigator_Abilities()
        {
            var game = NewGame(new[] { "aira", "mitchell", "vincent" }, adversary: "cult-of-hunlow");
            var aira = Inv(game, "aira");
            aira.Dead = true;
            game.AdoptSpirit("aira", "apparition");

            Assert.Equal(0, aira.MajorAbilityTokens);
            var error = Assert.Throws<InvalidOperationException>(() => game.UseMinorAbility("aira"));
            Assert.Contains("Spirit", error.Message);
            Assert.Throws<InvalidOperationException>(() => game.UseMajorAbility("aira"));
        }
    }
}
