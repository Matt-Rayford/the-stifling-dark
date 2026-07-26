using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>Covers Game.ItemEffects.cs: General, Medical, and Cursed Item card effects,
    /// the Supply-use/discard convention, and the ongoing effects hung on the Items sub-hooks.</summary>
    public class ItemEffectTests
    {
        // Copied privately from EffectsInfraTests/WoundConditionTests's setup-helper pattern.
        private static Game NewSawmillGame(ulong seed = 9001)
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = "butcher",
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285",
                    ["lucy-belle"] = "286",
                    ["mitchell"] = "305",
                    ["vincent"] = "307",
                },
                MedicalItemSpaces = new List<string> { "24", "208" },
            });
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

        private static InvestigatorState Aira(Game game) =>
            game.State.Investigators.First(i => i.DefId == "aira");

        private static InvestigatorState LucyBelle(Game game) =>
            game.State.Investigators.First(i => i.DefId == "lucy-belle");

        /// <summary>Ends aira's current turn, then cycles the other 3 Investigators and the
        /// Adversary trivially so the game advances to the next round with aira free to
        /// start another turn.</summary>
        private static void FinishRoundAfterAira(Game game)
        {
            game.EndTurnWithoutFinalAction();
            foreach (string inv in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
        }

        // ---------- Cards the new intercept points unblocked ----------

        [Fact]
        public void Spare_Batteries_pays_a_flashlight_placement_out_of_its_own_supply()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("spare-batteries");
            aira.Charge = 2;

            game.BeginInvestigatorTurn("aira");
            game.UseItem("spare-batteries");
            game.PlaceFlashlight(0.0);

            Assert.Equal(2, aira.Charge); // the Supply paid, not the Charge track
            Assert.Single(game.State.Flashlights);
        }

        [Fact]
        public void Spare_Batteries_covers_only_the_next_placement()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("spare-batteries");
            aira.Charge = 2;

            game.BeginInvestigatorTurn("aira");
            game.UseItem("spare-batteries");
            game.PlaceFlashlight(0.0); // ends aira's turn
            foreach (string inv in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);

            Assert.Equal(1, aira.Charge);
        }

        [Fact]
        public void Spare_Tools_turns_an_involved_action_into_an_interact_that_does_not_end_the_turn()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("spare-tools");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("spare-tools");
            game.TakeInvolvedAction();

            Assert.Equal("aira", game.State.ActiveInvestigator);
            Assert.Equal(FinalActionKind.None, aira.FinalAction);
            // "...but you may not take another Involved Action this turn."
            Assert.Throws<InvalidOperationException>(() => game.TakeInvolvedAction());
            // A different Final Action is still open.
            game.ChargeFlashlight();
            Assert.True(aira.TurnTakenThisRound);
        }

        [Fact]
        public void The_Cross_takes_the_adversarys_abilities_away_for_their_next_turn()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("cross");
            game.State.Adversary.ActiveAbilities.Add("escalating-terror");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("cross");
            FinishRoundAfterAira(game); // the Adversary turn happens on the way out of the round

            Assert.Contains(game.State.Log, e => e.Type == "item" && e.Detail.Contains("Cross"));
            // Next round the lockout has expired.
            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction();
            foreach (string inv in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            game.PlayAdversaryCard("escalating-terror");
            Assert.Equal(1, game.State.Adversary.Counters["escalating-terror-pending"]);
        }

        [Fact]
        public void The_Cross_still_leaves_attacks_and_core_actions_available()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("cross");
            aira.Space = "S-24"; // adjacent to The Butcher on S-25: clear line of sight
            game.State.Adversary.ActiveAbilities.Add("escalating-terror");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("cross");
            game.EndTurnWithoutFinalAction();
            foreach (string inv in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }

            var error = Assert.Throws<InvalidOperationException>(() => game.PlayAdversaryCard("escalating-terror"));
            Assert.Contains("Cross", error.Message);
            game.ButcherStalk(new List<string> { "aira" }); // a Core Action: still fine
            Assert.True(game.State.Adversary.SpineChill.ContainsKey("aira"));
        }

        [Fact]
        public void Lucky_Dice_rerolls_the_next_sprint_and_keeps_the_better_roll()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("lucky-dice");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("lucky-dice");
            int before = aira.MpRemaining;
            game.Sprint();

            var reroll = game.State.Log.Last(e => e.Type == "item" && e.Detail.Contains("Lucky Dice reroll"));
            Assert.Contains("vs", reroll.Detail);
            Assert.InRange(aira.MpRemaining - before, 2, 4);
            // One Supply spent of 2, so the card is still in hand.
            Assert.Contains("lucky-dice", aira.Items);
        }

        [Fact]
        public void Energy_Drink_reopens_movement_after_a_window_stopped_it()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("energy-drink");

            game.BeginInvestigatorTurn("aira");
            aira.Space = "117";
            game.MoveStep("S-17");
            game.ResolveWindow(stopAndLoseStamina: true);
            Assert.True(aira.MovementLocked);

            game.UseItem("energy-drink");

            Assert.False(aira.MovementLocked);
            Assert.DoesNotContain("energy-drink", aira.Items); // single use
        }

        [Fact]
        public void Firecrackers_drag_every_adversary_figure_two_spaces_toward_the_noise()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("firecrackers");
            var adv = game.State.Adversary;
            aira.Space = "S-19";
            string target = game.Graph.DistancesFrom(aira.Space, 3, game.State.Overlay).Keys
                .First(id => id != aira.Space);
            int before = game.Graph.DistancesFrom(target, int.MaxValue, game.State.Overlay)[adv.Space];

            game.BeginInvestigatorTurn("aira");
            game.UseItem("firecrackers", new List<string> { target });

            int after = game.Graph.DistancesFrom(target, int.MaxValue, game.State.Overlay)[adv.Space];
            Assert.Equal(Math.Max(0, before - 2), after);
            Assert.Contains(game.State.Log, e => e.Type == "adversary" && e.Detail.Contains("Firecrackers"));
        }

        [Fact]
        public void Firecrackers_need_a_space_within_three()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("firecrackers");
            var within3 = game.Graph.DistancesFrom(aira.Space, 3, game.State.Overlay);
            string far = game.Graph.Def.Spaces.Select(s => s.Id).First(id => !within3.ContainsKey(id));

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.UseItem("firecrackers", new List<string> { far }));
            Assert.Contains("firecrackers", aira.Items); // the failed use spent nothing
        }

        [Fact]
        public void Witch_Bells_can_take_the_adversarys_sprint_die_away()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("witch-bells");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("witch-bells", new List<string> { "sprint" });
            Assert.True(game.HasCondition(aira, "darkness"));
            game.EndTurnWithoutFinalAction();
            foreach (string inv in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryMoveStep("S-26");

            Assert.Equal(0, game.State.Adversary.SprintRolled);
            Assert.Equal(5 - 1, game.State.Adversary.MpRemaining); // base 5, no die, 1 step taken
        }

        // ---------- Supply / discard bookkeeping ----------

        [Fact]
        public void A_Supply_2_card_is_usable_twice_then_discarded()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("adrenaline-shot");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("adrenaline-shot"); // 1st use
            Assert.Contains("adrenaline-shot", aira.Items);
            Assert.Contains(game.State.Log, e => e.Type == "item" && e.Detail.Contains("1/2 Supply"));
            FinishRoundAfterAira(game);

            game.BeginInvestigatorTurn("aira");
            game.UseItem("adrenaline-shot"); // 2nd use: exhausts Supply, discards
            Assert.DoesNotContain("adrenaline-shot", aira.Items);
            Assert.Contains(game.State.Log, e => e.Type == "item" && e.Detail.Contains("discarded Adrenaline Shot"));

            Assert.Throws<InvalidOperationException>(() => game.UseItem("adrenaline-shot"));
        }

        [Fact]
        public void A_single_use_card_with_no_Supply_icon_discards_immediately()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("energy-bar");
            int before = aira.Stamina;
            // Aira starts at max Stamina; drop her down so the +2 from Energy Bar is visible.
            aira.Stamina = Math.Max(0, before - 3);

            game.BeginInvestigatorTurn("aira");
            game.UseItem("energy-bar");

            Assert.Equal(before - 1, aira.Stamina); // -3 then +2
            Assert.DoesNotContain("energy-bar", aira.Items);
            Assert.Throws<InvalidOperationException>(() => game.UseItem("energy-bar"));
        }

        [Fact]
        public void A_Supply_infinity_card_never_discards()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("blood-chalice");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("blood-chalice");
            game.UseItem("blood-chalice");

            Assert.Contains("blood-chalice", aira.Items);
            Assert.Equal(2, aira.Wounds.Count(w => w.FaceUp));
        }

        [Fact]
        public void UseItem_requires_the_active_Investigator_to_hold_the_card()
        {
            var game = NewSawmillGame();
            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.UseItem("energy-bar"));
        }

        [Fact]
        public void Mangled_Hands_blocks_all_Item_and_Cursed_Item_use()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("energy-bar");
            aira.Wounds.Add(new WoundInstance { CardId = "mangled-hands", FaceUp = true });

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.UseItem("energy-bar"));
            Assert.Contains("energy-bar", aira.Items); // never consumed
        }

        // ---------- Medkit ----------

        [Fact]
        public void Medkit_flips_a_face_up_Wound_face_down_and_discards_after_one_use()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("medkit");
            aira.Wounds.Add(new WoundInstance { CardId = "torn-ligament", FaceUp = true });

            game.BeginInvestigatorTurn("aira");
            game.UseItem("medkit", new List<string> { "aira" });

            Assert.False(aira.Wounds.Single().FaceUp);
            Assert.DoesNotContain("medkit", aira.Items);
            // torn-ligament isn't one of the 4 wounds this file knows how to reverse.
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.Contains("torn-ligament"));
        }

        [Fact]
        public void Medkit_can_treat_an_adjacent_Investigator()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            var lucy = LucyBelle(game);
            aira.Items.Add("medkit");
            // Teleport lucy next to aira so adjacency holds regardless of the map layout.
            lucy.Space = game.Graph.DistancesFrom(aira.Space, 1, game.State.Overlay).Keys.First(id => id != aira.Space);
            lucy.Wounds.Add(new WoundInstance { CardId = "spasm", FaceUp = true });

            game.BeginInvestigatorTurn("aira");
            game.UseItem("medkit", new List<string> { "lucy-belle" });

            Assert.False(lucy.Wounds.Single().FaceUp);
        }

        [Fact]
        public void Hemorrhage_blocks_flipping_other_Wounds_down_but_not_itself()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("medkit");
            aira.Items.Add("medkit");
            var hemorrhage = new WoundInstance { CardId = "hemorrhage", FaceUp = true };
            aira.Wounds.Add(hemorrhage);
            aira.Wounds.Add(new WoundInstance { CardId = "fumble", FaceUp = true });

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.UseItem("medkit", new List<string> { "aira", "fumble" }));
            Assert.True(aira.Wounds.Single(w => w.CardId == "fumble").FaceUp);

            game.UseItem("medkit", new List<string> { "aira", "hemorrhage" });
            Assert.False(hemorrhage.FaceUp);
        }

        // ---------- Representative General Items ----------

        [Fact]
        public void Fresh_Batteries_grants_2_Charge_capped_at_the_max()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Charge = 0;
            aira.Items.Add("fresh-batteries");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("fresh-batteries");

            Assert.Equal(2, aira.Charge);
        }

        [Fact]
        public void Blueprints_reveals_POI_tokens_within_2_spaces_of_a_chosen_POI()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("blueprints");
            string poiSpace = game.Graph.Def.Spaces.First(s => s.Kind == SpaceKind.PointOfInterest).Id;
            // PlacePoiToken (in setup) always drops the token within 2 of its own POI space,
            // so the token owned by this POI is guaranteed to be revealed by the card.
            var relatedToken = game.State.PoiTokens.First(p => p.PoiSpace == poiSpace);
            Assert.False(relatedToken.Revealed);

            game.BeginInvestigatorTurn("aira");
            game.UseItem("blueprints", new List<string> { poiSpace });

            Assert.True(relatedToken.Revealed);
        }

        [Fact]
        public void Painkillers_draws_2_and_may_swap_one_face_up_wound()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("painkillers");
            aira.Wounds.Add(new WoundInstance { CardId = "torn-ligament", FaceUp = true });

            game.BeginInvestigatorTurn("aira");
            game.UseItem("painkillers");
            string marker = aira.Items.Single(i => i.StartsWith("marker:painkillers:"));
            string drawn = marker.Split(':')[2];

            // Choosing a card that wasn't drawn is rejected and keeps the choice pending.
            Assert.Throws<InvalidOperationException>(() => game.ResolvePainkillers("torn-ligament", "not-a-card"));
            game.ResolvePainkillers("torn-ligament", drawn);

            Assert.Single(aira.Wounds); // swap, not gain: count unchanged
            Assert.Equal(drawn, aira.Wounds[0].CardId);
            Assert.DoesNotContain(aira.Items, i => i.StartsWith("marker:painkillers:"));
            Assert.Throws<InvalidOperationException>(() => game.ResolvePainkillers(null, null));
        }

        [Fact]
        public void Painkillers_swap_may_be_declined()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("painkillers");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("painkillers");
            game.ResolvePainkillers(null, null);

            Assert.Empty(aira.Wounds);
            Assert.DoesNotContain(aira.Items, i => i.StartsWith("marker:painkillers:"));
        }

        [Fact]
        public void Binding_Tablet_requires_4_Stamina_and_spends_it()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("binding-tablet");
            aira.Stamina = 3;

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.UseItem("binding-tablet"));

            aira.Stamina = 4;
            game.UseItem("binding-tablet");
            Assert.Equal(0, aira.Stamina);
        }

        // ---------- Cursed Items ----------

        [Fact]
        public void Cursed_Poppet_zeroes_Stamina_without_a_Wound_and_marks_the_holder()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            var lucy = LucyBelle(game);
            aira.Items.Add("cursed-poppet");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("cursed-poppet", new List<string> { "lucy-belle" });

            Assert.Equal(0, aira.Stamina);
            Assert.Empty(aira.Wounds); // "do not incur any face-down Wound"
            Assert.Contains("marker:poppet-owner:aira", lucy.Items);
            Assert.DoesNotContain("cursed-poppet", aira.Items);
        }

        [Fact]
        public void Phantom_Amulet_grants_Mauled_and_places_a_Secret_Passage_the_first_time()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("phantom-amulet");
            string a = aira.Space;
            string b = game.Graph.DistancesFrom(a, 1, game.State.Overlay).Keys.First(id => id != a);

            game.BeginInvestigatorTurn("aira");
            game.UseItem("phantom-amulet", new List<string> { a, b });

            Assert.True(game.HasCondition(aira, "mauled"));
            Assert.Contains(BoardOverlay.EdgeKey(a, b), game.State.Overlay.SecretPassages);
            Assert.Empty(aira.Wounds);
        }

        [Fact]
        public void Phantom_Amulet_grants_a_Wound_instead_when_already_Mauled()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            game.GainCondition(aira, "mauled");
            aira.Items.Add("phantom-amulet");
            string a = aira.Space;
            string b = game.Graph.DistancesFrom(a, 1, game.State.Overlay).Keys.First(id => id != a);

            game.BeginInvestigatorTurn("aira");
            game.UseItem("phantom-amulet", new List<string> { a, b });

            Assert.Single(aira.Wounds);
            Assert.False(aira.Wounds[0].FaceUp);
            Assert.DoesNotContain(BoardOverlay.EdgeKey(a, b), game.State.Overlay.SecretPassages);
        }

        [Fact]
        public void Summoning_Stones_draws_2_chosen_cards_and_costs_every_Investigator_1_Stamina()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            var lucy = LucyBelle(game);
            aira.Items.Add("summoning-stones");
            string first = game.State.GeneralItemDeck[0];
            string second = game.State.GeneralItemDeck[1];
            int lucyStaminaBefore = lucy.Stamina;

            game.BeginInvestigatorTurn("aira");
            game.UseItem("summoning-stones", new List<string> { first, second });

            Assert.Contains(first, aira.Items);
            Assert.Contains(second, aira.Items);
            Assert.DoesNotContain(first, game.State.GeneralItemDeck);
            Assert.DoesNotContain(second, game.State.GeneralItemDeck);
            Assert.Equal(lucyStaminaBefore - 1, lucy.Stamina);
        }

        // ---------- Ongoing / round-scoped effects ----------

        [Fact]
        public void Lantern_lights_adjacent_spaces_and_expires_at_round_end()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("lantern");
            string target = game.Graph.DistancesFrom(aira.Space, 1, game.State.Overlay).Keys.First(id => id != aira.Space);

            game.BeginInvestigatorTurn("aira");
            game.UseItem("lantern", new List<string> { target });

            Assert.Equal(LightLevel.Bright, game.Graph.EffectiveLight(target, game.State.Overlay));
            Assert.Equal(target, game.BoardTokenSpace($"lantern-{aira.DefId}"));

            FinishRoundAfterAira(game);

            Assert.Null(game.BoardTokenSpace($"lantern-{aira.DefId}"));
            Assert.NotEqual(LightLevel.Bright, game.Graph.EffectiveLight(target, game.State.Overlay));
        }

        [Fact]
        public void Torch_makes_its_Zone_Dim_for_the_round_and_clears_at_round_end()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            var zonedSpace = game.Graph.Def.Spaces.First(s => s.Zone != null);
            aira.Space = zonedSpace.Id;
            aira.Items.Add("torch");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("torch");

            Assert.Contains(zonedSpace.Zone!, game.State.Overlay.DimZones);

            FinishRoundAfterAira(game);

            Assert.DoesNotContain(zonedSpace.Zone!, game.State.Overlay.DimZones);
            Assert.Null(game.BoardTokenSpace($"torch:{aira.DefId}"));
        }

        [Fact]
        public void Glowstick_may_only_be_used_once_per_round()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("glowstick");
            string target = game.Graph.DistancesFrom(aira.Space, 4, game.State.Overlay).Keys.First(id => id != aira.Space);

            game.BeginInvestigatorTurn("aira");
            game.UseItem("glowstick", new List<string> { target });
            Assert.Throws<InvalidOperationException>(() => game.UseItem("glowstick", new List<string> { target }));

            FinishRoundAfterAira(game);
            game.BeginInvestigatorTurn("aira");
            game.UseItem("glowstick", new List<string> { target }); // allowed again next round
        }

        [Fact]
        public void Diablerie_Book_flips_a_face_down_Wound_at_the_start_of_each_future_turn()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("diablerie-book");
            var wound = new WoundInstance { CardId = "torn-ligament", FaceUp = false };
            aira.Wounds.Add(wound);

            game.BeginInvestigatorTurn("aira");
            game.UseItem("diablerie-book");
            Assert.DoesNotContain("diablerie-book", aira.Items); // discarded per the generic Supply rule...
            Assert.False(wound.FaceUp); // ...but the curse marker keeps the hook alive.

            FinishRoundAfterAira(game);
            game.BeginInvestigatorTurn("aira");

            Assert.True(wound.FaceUp);
        }

        // ---------- Rabbit's Foot ----------

        [Fact]
        public void Rabbits_foot_has_no_effect_when_not_currently_dying()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("rabbits-foot");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("rabbits-foot");

            Assert.False(aira.Dead);
            Assert.DoesNotContain("rabbits-foot", aira.Items); // still discarded (single use)
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.Contains("rabbits-foot"));
        }

        [Fact]
        public void Rabbits_foot_recurring_check_either_confirms_the_save_or_ends_it()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("marker:rabbits-foot-active");

            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction();

            bool stillLucky = aira.Items.Contains("marker:rabbits-foot-active");
            Assert.Equal(stillLucky, !aira.Dead);
        }

        // ---------- Stray Mutt / Whistle / Crystal Amulet ----------

        [Fact]
        public void Stray_Mutt_picks_up_a_Medical_Item_token_within_3_spaces()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            string medicalSpace = game.State.MedicalItemSpaces[0];
            // Teleport aira 1 space away so the pickup can only succeed via Stray Mutt's
            // "as if you were on that space" trick, not by already standing on it.
            aira.Space = game.Graph.DistancesFrom(medicalSpace, 1, game.State.Overlay).Keys.First(id => id != medicalSpace);
            string original = aira.Space;
            aira.Items.Add("stray-mutt");

            game.BeginInvestigatorTurn("aira");
            game.UseItem("stray-mutt", new List<string> { medicalSpace });

            Assert.Equal(original, aira.Space); // teleport-and-back left no trace
            Assert.Contains("medkit", aira.Items);
            Assert.DoesNotContain(medicalSpace, game.State.MedicalItemSpaces);
        }

        [Fact]
        public void Whistle_pulls_a_distant_Investigator_closer()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            var lucy = LucyBelle(game);
            aira.Items.Add("whistle");
            var dist = game.Graph.DistancesFrom(aira.Space, 999, game.State.Overlay);
            lucy.Space = dist.Where(kv => kv.Value >= 2).OrderByDescending(kv => kv.Value).First().Key;
            int before = dist[lucy.Space];

            game.BeginInvestigatorTurn("aira");
            game.UseItem("whistle", new List<string> { "lucy-belle" });

            int after = dist.TryGetValue(lucy.Space, out int d) ? d : before;
            Assert.True(after < before);
        }

        [Fact]
        public void Crystal_Amulet_rearranges_the_top_of_the_Event_deck_without_touching_the_Major()
        {
            var game = NewSawmillGame();
            var aira = Aira(game);
            aira.Items.Add("crystal-amulet");
            var majorCard = game.State.EventDeck[^1];
            var top3 = game.State.EventDeck.Take(3).ToList();
            var reordered = new List<string> { top3[2], top3[0], top3[1] };

            game.BeginInvestigatorTurn("aira");
            game.UseItem("crystal-amulet", reordered);

            Assert.Equal(reordered, game.State.EventDeck.Take(3));
            Assert.Equal(majorCard, game.State.EventDeck[^1]);
        }
    }
}
