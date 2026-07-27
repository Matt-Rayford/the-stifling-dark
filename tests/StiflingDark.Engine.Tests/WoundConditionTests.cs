using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class WoundConditionTests
    {
        // Copied privately from GameFlowTests's setup-helper pattern.
        private static Game NewSawmillGame(ulong seed = 1234, string adversary = "butcher")
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = adversary,
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285",
                    ["lucy-belle"] = "286",
                    ["mitchell"] = "305",
                    ["vincent"] = "307",
                },
                MedicalItemSpaces = new List<string> { "24", "208" },
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

        /// <summary>Ends aira's current turn, then cycles the other 3 investigators and the
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

        // ---------- Wound face-up resolution (WoundsResolveFaceUp) ----------

        [Fact]
        public void GainWound_face_up_triggers_resolution_and_discharge_zeroes_charge()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Charge = 3;
            game.State.WoundDeck.Insert(0, "discharge");

            game.GainWound(aira, faceUp: true);

            Assert.Equal(0, aira.Charge);
            Assert.Single(aira.Wounds);
            Assert.True(aira.Wounds[0].FaceUp);
        }

        [Fact]
        public void Spasm_face_up_loses_2_stamina_without_a_face_down_wound()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            int before = aira.Stamina;
            game.State.WoundDeck.Insert(0, "spasm");

            game.GainWound(aira, faceUp: true);

            Assert.Equal(before - 2, aira.Stamina);
            // Only the Spasm card itself should be in the Wound pile - losing Stamina this
            // way must not have incurred a second, face-down Wound.
            Assert.Single(aira.Wounds);
        }

        [Fact]
        public void Fumble_face_up_discards_a_random_item()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            string item = TestData.Db.Deck("general-item").First().Id;
            aira.Items.Add(item);
            game.State.WoundDeck.Insert(0, "fumble");

            game.GainWound(aira, faceUp: true);

            Assert.Empty(aira.Items);
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.Contains("fumble"));
        }

        [Fact]
        public void Fumble_face_up_with_no_items_has_no_effect()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.State.WoundDeck.Insert(0, "fumble");

            game.GainWound(aira, faceUp: true);

            Assert.Empty(aira.Items);
        }

        [Fact]
        public void Claustrophobia_blocks_locking_and_opening_doors_while_face_up()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.State.WoundDeck.Insert(0, "claustrophobia");
            game.GainWound(aira, faceUp: true);

            Assert.Contains("Claustrophobia", string.Join("|", game.ActionBlockers("aira", Game.ActionLockDoor)));
            Assert.Contains("Claustrophobia", string.Join("|", game.ActionBlockers("aira", Game.ActionOpenDoor)));
            // Everything else stays legal.
            Assert.Empty(game.ActionBlockers("aira", Game.ActionSprint));

            game.BeginInvestigatorTurn("aira");
            aira.Space = "S-16";
            var error = Assert.Throws<InvalidOperationException>(() => game.LockDoor("S-17"));
            Assert.Contains("Claustrophobia", error.Message);

            // Face-down again (a Medkit, Leather Jacket, ...) and the restriction lifts.
            aira.Wounds[0].FaceUp = false;
            Assert.Empty(game.ActionBlockers("aira", Game.ActionLockDoor));
        }

        [Fact]
        public void FlipWoundFaceUp_resolves_the_wounds_text()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Charge = 2;
            var wound = new WoundInstance { CardId = "discharge", FaceUp = false };
            aira.Wounds.Add(wound);

            game.FlipWoundFaceUp(aira, wound);

            Assert.True(wound.FaceUp);
            Assert.Equal(0, aira.Charge);
            Assert.Contains(game.State.Log, e => e.Type == "wound" && e.Detail.Contains("flipped discharge face-up"));
        }

        [Fact]
        public void FlipWoundFaceUp_is_a_no_op_when_already_face_up()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Charge = 2;
            var wound = new WoundInstance { CardId = "discharge", FaceUp = true };
            aira.Wounds.Add(wound);

            game.FlipWoundFaceUp(aira, wound);

            // Discharge's text only fires "when you receive or flip this card face-up";
            // it was already face-up, so nothing should have re-triggered.
            Assert.Equal(2, aira.Charge);
        }

        // ---------- Ongoing wound effects (WoundsOnTurnStart / WoundsOnTurnEnd) ----------

        [Fact]
        public void Fractured_foot_reduces_mp_by_1_at_turn_start_only_while_face_up()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            int baseMp = TestData.Db.Investigator("aira").Mp;
            aira.Wounds.Add(new WoundInstance { CardId = "fractured-foot", FaceUp = true });

            game.BeginInvestigatorTurn("aira");

            Assert.Equal(baseMp - 1, aira.MpRemaining);
        }

        [Fact]
        public void Fractured_foot_face_down_has_no_effect()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            int baseMp = TestData.Db.Investigator("aira").Mp;
            aira.Wounds.Add(new WoundInstance { CardId = "fractured-foot", FaceUp = false });

            game.BeginInvestigatorTurn("aira");

            Assert.Equal(baseMp, aira.MpRemaining);
        }

        [Fact]
        public void Pulled_hammy_reduces_mp_by_2_at_turn_start()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            int baseMp = TestData.Db.Investigator("aira").Mp;
            aira.Wounds.Add(new WoundInstance { CardId = "pulled-hammy", FaceUp = true });

            game.BeginInvestigatorTurn("aira");

            Assert.Equal(baseMp - 2, aira.MpRemaining);
        }

        [Fact]
        public void Slipped_disc_discards_down_to_2_items_at_turn_start()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Items.AddRange(new[] { "a", "b", "c", "d" });
            aira.Wounds.Add(new WoundInstance { CardId = "slipped-disc", FaceUp = true });

            game.BeginInvestigatorTurn("aira");

            Assert.Equal(2, aira.Items.Count);
        }

        [Fact]
        public void Breathless_drains_1_stamina_at_the_end_of_each_turn_without_a_wound()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            int before = aira.Stamina;
            aira.Wounds.Add(new WoundInstance { CardId = "breathless", FaceUp = true });

            // Designer-confirmed: on a quiet turn the drain nets out against the automatic
            // Rest — Stamina "should stay the same".
            aira.Stamina = before - 1; // below max, so the automatic Rest is not clamped away
            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction();
            Assert.Equal(before - 1, aira.Stamina);

            // An Involved final forfeits the automatic Rest, so the drain lands net.
            foreach (string other in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(other);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
            game.BeginInvestigatorTurn("aira");
            game.TakeInvolvedAction();
            Assert.Equal(before - 2, aira.Stamina);
            Assert.Single(aira.Wounds); // no extra face-down Wound from the loss.
        }

        [Fact]
        public void Dying_battery_drains_1_charge_at_the_end_of_each_turn()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Charge = 2;
            aira.Wounds.Add(new WoundInstance { CardId = "dying-battery", FaceUp = true });

            // Designer-confirmed: on a quiet turn the drain nets out against the automatic
            // Charge — it "should stay the same".
            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction();
            Assert.Equal(2, aira.Charge);

            // An Involved final forfeits the automatic Charge, so the drain lands net.
            foreach (string other in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(other);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
            game.BeginInvestigatorTurn("aira");
            game.TakeInvolvedAction();
            Assert.Equal(1, aira.Charge);
        }

        [Fact]
        public void Panic_cancels_the_rest_stamina_gain()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Stamina = 2; // below max, so Resting would normally add 1.
            aira.Wounds.Add(new WoundInstance { CardId = "panic", FaceUp = true });

            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction();

            Assert.Equal(2, aira.Stamina);
        }

        // ---------- Commiserate (discretionary discard helper) ----------

        [Fact]
        public void Commiserate_discards_the_wound_and_gives_the_other_investigator_2_face_down_wounds()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            var lucy = game.State.Investigators.First(i => i.DefId == "lucy-belle");
            aira.Space = "117";
            lucy.Space = "S-17"; // adjacent via a Window edge, per GameFlowTests.
            aira.Wounds.Add(new WoundInstance { CardId = "commiserate", FaceUp = false });

            bool result = game.Commiserate(aira, lucy);

            Assert.True(result);
            Assert.DoesNotContain(aira.Wounds, w => w.CardId == "commiserate");
            Assert.Equal(2, lucy.Wounds.Count);
            Assert.All(lucy.Wounds, w => Assert.False(w.FaceUp));
        }

        [Fact]
        public void Commiserate_requires_adjacency()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            var vincent = game.State.Investigators.First(i => i.DefId == "vincent");
            aira.Wounds.Add(new WoundInstance { CardId = "commiserate", FaceUp = false });
            // aira and vincent are far apart on their starting spaces.

            Assert.Throws<InvalidOperationException>(() => game.Commiserate(aira, vincent));
        }

        [Fact]
        public void Commiserate_without_the_card_does_nothing()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            var lucy = game.State.Investigators.First(i => i.DefId == "lucy-belle");
            aira.Space = "117";
            lucy.Space = "S-17";

            bool result = game.Commiserate(aira, lucy);

            Assert.False(result);
            Assert.Empty(lucy.Wounds);
        }

        // ---------- Condition grant + duplicate substitution (GrantConditionWithSubstitution) ----------

        [Fact]
        public void Grant_condition_with_substitution_grants_normally_the_first_time()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");

            game.GrantConditionWithSubstitution(aira, "gear-jam");

            Assert.True(game.HasCondition(aira, "gear-jam"));
        }

        [Fact]
        public void Grant_condition_with_substitution_bleeding_grants_a_face_up_wound_instead()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "bleeding");

            game.GrantConditionWithSubstitution(aira, "bleeding");

            Assert.Equal(1, aira.Conditions.Count(c => c == "bleeding"));
            Assert.Single(aira.Wounds);
            Assert.True(aira.Wounds[0].FaceUp);
        }

        [Fact]
        public void Grant_condition_with_substitution_darkness_loses_1_charge_instead()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Charge = 3;
            game.GrantConditionWithSubstitution(aira, "darkness");

            game.GrantConditionWithSubstitution(aira, "darkness");

            Assert.Equal(1, aira.Conditions.Count(c => c == "darkness"));
            Assert.Equal(2, aira.Charge);
        }

        // ---------- Per-condition behavior (ConditionsOnTurnStart / ConditionsOnTurnEnd) ----------

        [Fact]
        public void Choking_fear_reduces_mp_then_discards_itself_at_the_end_of_that_turn()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            int baseMp = TestData.Db.Investigator("aira").Mp;
            game.GrantConditionWithSubstitution(aira, "choking-fear");

            game.BeginInvestigatorTurn("aira");
            Assert.Equal(baseMp - 1, aira.MpRemaining);
            Assert.True(game.HasCondition(aira, "choking-fear"));

            game.EndTurnWithoutFinalAction();
            Assert.False(game.HasCondition(aira, "choking-fear"));
        }

        [Fact]
        public void Darkness_discards_itself_at_the_end_of_the_next_turn()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "darkness");

            game.BeginInvestigatorTurn("aira");
            Assert.True(game.HasCondition(aira, "darkness"));
            game.EndTurnWithoutFinalAction();

            Assert.False(game.HasCondition(aira, "darkness"));
        }

        [Fact]
        public void Bleeding_grants_a_face_up_wound_each_turn_then_discards_itself_after_two()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "bleeding");

            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction();

            Assert.Single(aira.Wounds);
            Assert.True(aira.Wounds[0].FaceUp);
            Assert.True(game.HasCondition(aira, "bleeding"));

            // Second Wound from the same card: "once you've gained 2 from this card, discard it."
            foreach (string inv in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction();

            Assert.Equal(2, aira.Wounds.Count);
            Assert.False(game.HasCondition(aira, "bleeding"));
        }

        [Fact]
        public void Gear_jam_eventually_discards_itself_on_a_turn_end_die_roll()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "gear-jam");

            for (int round = 0; round < 40 && game.HasCondition(aira, "gear-jam"); round++)
            {
                game.BeginInvestigatorTurn("aira");
                FinishRoundAfterAira(game);
            }

            Assert.False(game.HasCondition(aira, "gear-jam"));
            Assert.Contains(game.State.Log, e => e.Type == "condition" && e.Detail.Contains("Gear Jam"));
        }

        [Fact]
        public void Paranoid_eventually_discards_itself_on_a_turn_start_die_roll()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "paranoid");

            for (int round = 0; round < 40 && game.HasCondition(aira, "paranoid"); round++)
            {
                game.BeginInvestigatorTurn("aira");
                FinishRoundAfterAira(game);
            }

            Assert.False(game.HasCondition(aira, "paranoid"));
            Assert.Contains(game.State.Log, e => e.Type == "condition" && e.Detail.Contains("Paranoid"));
        }

        [Fact]
        public void Bufotoxin_starts_face_down_and_the_adversary_may_flip_it_on_their_turn()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "bufotoxin");

            // Face-down: no restriction, and it may not be flipped outside an Adversary turn.
            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.FlipBufotoxinFaceUp("aira"));
            FinishRoundAfterAira(game); // round 2: the Adversary turn offered the flip on the way
            Assert.Contains(game.State.Log, e => e.Type == "condition" && e.Detail.Contains("may flip"));
        }

        [Fact]
        public void A_flipped_bufotoxin_trims_next_rounds_flashlight_then_discards_itself()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "bufotoxin");
            game.BeginInvestigatorTurn("aira");
            FinishRoundAfterAira(game); // now in round 2 with the Adversary turn behind us

            // Flip it during the round-2 Adversary turn, so round 3 is the restricted one.
            foreach (string inv in new[] { "aira", "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            game.FlipBufotoxinFaceUp("aira");
            Assert.Throws<InvalidOperationException>(() => game.FlipBufotoxinFaceUp("aira"));
            game.AdversaryEndTurn();

            Assert.Equal(3, game.State.Round);
            var unrestricted = game.PreviewFlashlight("aira", 0.0);
            Assert.True(unrestricted.Count > 1);
            game.BeginInvestigatorTurn("aira");
            aira.Charge = 3;
            game.PlaceFlashlight(0.0);
            Assert.True(game.State.Flashlights.Single().BrightSpaces.Count < unrestricted.Count);
            Assert.Contains(game.State.Log, e => e.Type == "condition" && e.Detail.Contains("center line"));

            // "Discard this Condition at the end of the next round."
            foreach (string inv in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(inv);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
            Assert.False(game.HasCondition(aira, "bufotoxin"));
        }

        [Fact]
        public void Mauled_adds_one_extra_face_down_wound_to_adversary_wounds_only()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "mauled");

            // An untagged Wound (a card cost, an objective) is unaffected.
            game.GainWound(aira, faceUp: false);
            Assert.Single(aira.Wounds);

            // One from the Adversary brings a second, face-down, and cannot cascade further.
            game.GainWound(aira, faceUp: true, origin: Game.WoundFromAdversary);
            Assert.Equal(3, aira.Wounds.Count);
            Assert.Equal(1, aira.Wounds.Count(w => w.FaceUp));
            Assert.Contains(game.State.Log, e => e.Type == "condition" && e.Detail.Contains("Mauled adds"));
        }

        [Fact]
        public void Neurotoxin_parks_a_face_up_wound_outside_the_slots_each_turn()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "neurotoxin");

            game.BeginInvestigatorTurn("aira");

            Assert.Single(aira.NonSlotWounds);
            Assert.True(aira.NonSlotWounds[0].FaceUp);
            Assert.Empty(aira.Wounds); // it does not take up a Wound slot, so it cannot kill
        }

        [Fact]
        public void Neurotoxin_discards_itself_and_both_wounds_once_two_are_below_it()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "neurotoxin");

            game.BeginInvestigatorTurn("aira");
            FinishRoundAfterAira(game);
            Assert.Single(aira.NonSlotWounds); // 1 after round 1's end
            Assert.True(game.HasCondition(aira, "neurotoxin"));

            game.BeginInvestigatorTurn("aira");
            Assert.Equal(2, aira.NonSlotWounds.Count);
            FinishRoundAfterAira(game);

            Assert.Empty(aira.NonSlotWounds);
            Assert.False(game.HasCondition(aira, "neurotoxin"));
        }

        [Fact]
        public void A_neurotoxin_wound_still_applies_its_ongoing_effect()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            int baseMp = TestData.Db.Investigator("aira").Mp;
            game.GrantConditionWithSubstitution(aira, "neurotoxin");
            game.State.WoundDeck.Insert(0, "fractured-foot");

            game.BeginInvestigatorTurn("aira");

            // WoundsOnTurnStart runs before ConditionsOnTurnStart, so the MP cut lands on the
            // following turn; what this pins down is that the non-slot Wound counts as face-up.
            Assert.Equal("fractured-foot", aira.NonSlotWounds[0].CardId);
            FinishRoundAfterAira(game);
            game.BeginInvestigatorTurn("aira");
            Assert.Equal(baseMp - 1, aira.MpRemaining);
        }

        [Fact]
        public void Possessed_is_offered_at_the_start_of_the_adversary_turn()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "possessed");

            game.BeginInvestigatorTurn("aira");
            FinishRoundAfterAira(game);

            Assert.Contains(game.State.Log,
                e => e.Type == "condition" && e.Detail.Contains("may use aira's Possessed"));
        }

        [Fact]
        public void Torn_ligament_subtracts_one_from_the_sprint_roll_with_a_floor_of_one()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Wounds.Add(new WoundInstance { CardId = "torn-ligament", FaceUp = true });
            int baseMp = TestData.Db.Investigator("aira").Mp;

            game.BeginInvestigatorTurn("aira");
            game.Sprint();

            // The Sprint die is 2,2,3,3,3,4, so -1 lands in 1..3 and never below the floor.
            Assert.InRange(aira.MpRemaining - baseMp, 1, 3);
            Assert.Contains(game.State.Log, e => e.Type == "wound" && e.Detail.Contains("Torn Ligament"));
        }

        [Fact]
        public void Punctured_lung_turns_sprint_wounds_face_up_but_leaves_other_wounds_alone()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Wounds.Add(new WoundInstance { CardId = "punctured-lung", FaceUp = true });

            game.GainWound(aira, faceUp: false, origin: Game.WoundFromWindow);
            Assert.False(aira.Wounds[1].FaceUp);

            game.GainWound(aira, faceUp: false, origin: Game.WoundFromSprint);
            Assert.True(aira.Wounds[2].FaceUp);
            Assert.Contains(game.State.Log, e => e.Type == "wound" && e.Detail.Contains("Punctured Lung"));
        }

        [Fact]
        public void Tunnel_vision_keeps_only_the_three_center_lines_of_the_flashlight()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            aira.Wounds.Add(new WoundInstance { CardId = "tunnel-vision", FaceUp = true });
            var unrestricted = game.PreviewFlashlight("aira", 0.0);
            Assert.True(unrestricted.Count > 1);

            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);

            var placement = game.State.Flashlights.Single();
            Assert.True(placement.BrightSpaces.Count < unrestricted.Count);
            Assert.Contains(aira.Space, placement.BrightSpaces);
            // Nothing outside the trimmed beam is lit, so nothing outside it was Revealed either.
            Assert.Equal(placement.BrightSpaces.ToHashSet(), game.State.Overlay.BrightSpaces);
        }

        [Fact]
        public void Drain_ergophobia_nyctophilia_and_mistrust_each_block_their_own_action()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            var lucy = game.State.Investigators.First(i => i.DefId == "lucy-belle");
            aira.Wounds.Add(new WoundInstance { CardId = "drain", FaceUp = true });
            aira.Wounds.Add(new WoundInstance { CardId = "ergophobia", FaceUp = true });
            aira.Wounds.Add(new WoundInstance { CardId = "nyctophilia", FaceUp = true });
            lucy.Wounds.Add(new WoundInstance { CardId = "mistrust", FaceUp = true });
            aira.Space = "117";
            lucy.Space = "S-17";
            aira.Items.Add("energy-bar");

            game.BeginInvestigatorTurn("aira");
            Assert.Contains("Drain", string.Join("|", game.ActionBlockers("aira", Game.ActionCharge)));
            Assert.Throws<InvalidOperationException>(() => game.TakeInvolvedAction());
            Assert.Throws<InvalidOperationException>(() => game.PlaceFlashlight(0.0));
            // Mistrust is on the *other* side of the trade: "or be Traded with".
            var error = Assert.Throws<InvalidOperationException>(() => game.TradeItem("lucy-belle", "energy-bar"));
            Assert.Contains("Mistrust", error.Message);
            Assert.Equal(FinalActionKind.None, aira.FinalAction);
        }

        [Fact]
        public void Gear_jam_costs_a_stamina_to_charge_and_blocks_it_outright_at_zero()
        {
            var game = NewSawmillGame();
            var aira = game.State.Investigators.First(i => i.DefId == "aira");
            game.GrantConditionWithSubstitution(aira, "gear-jam");
            aira.Charge = 1;

            game.BeginInvestigatorTurn("aira");
            game.Sprint(); // no automatic Rest this turn, so Gear Jam's toll stays visible
            int afterSprint = aira.Stamina;
            game.EndTurnWithoutFinalAction();
            Assert.Equal(2, aira.Charge); // the automatic Charge still happened
            Assert.Equal(afterSprint - 1, aira.Stamina); // ...but Gear Jam took its Stamina toll

            // With no Stamina left to spend the automatic Charge is simply vetoed.
            game.GrantConditionWithSubstitution(aira, "gear-jam");
            aira.Stamina = 0;
            Assert.Contains("Gear Jam", string.Join("|", game.ActionBlockers("aira", Game.ActionCharge)));
        }
    }
}
