using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// Event card effects (Game.EventEffects.cs): the round-scoped modifiers non-Major cards
    /// write, the {infinity} Majors that outlive their round, and the Adversary choices some
    /// cards hand over.
    ///
    /// Tests stack the card they need on top of the Event deck and close the round, so the
    /// assertions do not depend on the shuffle. One test covers the shuffle itself (that a
    /// scenario only ever deals its own Events).
    /// </summary>
    public class EventEffectTests
    {
        // ---------- Setup helpers ----------

        private static Game NewSawmillGame(ulong seed = 1234)
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = "butcher",
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285", ["lucy-belle"] = "286", ["mitchell"] = "305", ["vincent"] = "307",
                },
                MedicalItemSpaces = new List<string> { "24", "208" },
            });
            var evidence = new Dictionary<string, string>
            {
                ["L"] = "L-1", ["K"] = "K-1", ["G"] = "G-1", ["S"] = "S-1", ["O"] = "O-2",
            };
            foreach (var (zone, space) in evidence)
            {
                game.PlaceHiddenEvidence(zone, space);
            }
            CompleteSetup(game, "S-25");
            return game;
        }

        private static Game NewAmusementParkGame(ulong seed = 7)
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "amusement-park",
                Seed = seed,
                AdversaryId = "butcher",
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "142", ["lucy-belle"] = "153", ["mitchell"] = "166", ["vincent"] = "180",
                },
                MedicalItemSpaces = new List<string> { "68", "T-32" },
            });
            var evidence = new Dictionary<string, string>
            {
                ["G"] = "G-1", ["M"] = "M-1", ["C"] = "C-1", ["T"] = "T-35", ["F"] = "F-2",
            };
            foreach (var (zone, space) in evidence)
            {
                game.PlaceHiddenEvidence(zone, space);
            }
            CompleteSetup(game, "M-20");
            return game;
        }

        private static void CompleteSetup(Game game, string adversarySpace)
        {
            bool cursedPlaced = false;
            foreach (var poi in game.Graph.Def.Spaces.Where(s => s.Kind == SpaceKind.PointOfInterest))
            {
                string target = game.Graph.DistancesFrom(poi.Id, 2, game.State.Overlay).Keys
                    .First(id => game.Graph.Space(id).Kind == SpaceKind.Normal);
                game.PlacePoiToken(poi.Id, target, cursedFront: !cursedPlaced);
                cursedPlaced = true;
            }
            game.PlaceAdversary(adversarySpace);
            game.FinishAdversarySetup();
        }

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        /// <summary>Close out the current round: finish every outstanding Investigator turn,
        /// then end the Adversary turn (which draws the next Event card).</summary>
        private static void FinishRound(Game game)
        {
            if (game.State.ActiveInvestigator != null)
            {
                game.EndTurnWithoutFinalAction();
            }
            foreach (var inv in game.State.Investigators
                .Where(i => !i.Dead && !i.Escaped && !i.TurnTakenThisRound).ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
        }

        /// <summary>Stack <paramref name="eventId"/> on top of the deck and roll into the next
        /// round so that it becomes the current Event.</summary>
        private static void DrawEventNextRound(Game game, string eventId)
        {
            game.State.EventDeck.Insert(0, eventId);
            FinishRound(game);
            Assert.Equal(eventId, game.State.CurrentEvent);
        }

        private static string FirstAliveId(Game game) =>
            game.State.Investigators.First(i => !i.Dead && !i.Escaped).DefId;

        /// <summary>An unoccupied General/Start neighbour reachable by a plain Movement line.</summary>
        private static string FreeNeighbour(Game game, string from)
        {
            var occupied = game.State.Investigators
                .Where(i => !i.Dead && !i.Escaped).Select(i => i.Space).ToHashSet();
            return game.Graph.Def.Edges
                .Where(e => e.Type == EdgeType.Move && (e.A == from || e.B == from))
                .Select(e => e.A == from ? e.B : e.A)
                .First(id => !occupied.Contains(id) && game.Graph.Space(id).Kind != SpaceKind.Door);
        }

        // ---------- Minor Events ----------

        [Fact]
        public void A_minor_events_modifier_applies_during_its_round_and_is_gone_the_next()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "updraft");
            Assert.Equal(1, game.RoundModifier(Game.MpPenaltyKey));

            int mp = game.Db.Investigator("aira").Mp;
            game.BeginInvestigatorTurn("aira");
            Assert.Equal(mp - 1, Inv(game, "aira").MpRemaining);

            DrawEventNextRound(game, "creeping-fire");
            Assert.Equal(0, game.RoundModifier(Game.MpPenaltyKey));
            game.BeginInvestigatorTurn("aira");
            Assert.Equal(mp, Inv(game, "aira").MpRemaining);
        }

        [Fact]
        public void A_flavour_only_event_writes_no_modifiers()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "firebreak");
            Assert.Empty(game.State.RoundModifiers);
            Assert.Contains(game.State.Log, e => e.Type == "event" && e.Detail == "firebreak: no effect");
        }

        [Fact]
        public void Severe_heat_makes_every_sprint_cost_an_extra_stamina()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "severe-heat");
            Assert.Equal(1, game.RoundModifier(Game.SprintStaminaSurchargeKey));

            var aira = Inv(game, "aira");
            int stamina = aira.Stamina;
            game.BeginInvestigatorTurn("aira");
            game.Sprint();

            Assert.Equal(stamina - 2, aira.Stamina);
        }

        [Fact]
        public void Cold_front_trips_the_stamina_tracks_wound_icons_a_space_early_when_sprinting()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "cold-front");
            Assert.Equal(1, game.RoundModifier(Game.SprintWoundIconShiftKey));

            var aira = Inv(game, "aira");
            var track = TestData.Db.Investigator("aira").StaminaTrack;
            // Stand 1 above a Wound icon: a Sprint's single Stamina lands on icon+1, which is
            // only a Wound because Cold Front shifted the icons.
            aira.Stamina = track.WoundIconSpaces.Max() + 2;
            game.BeginInvestigatorTurn("aira");
            game.Sprint();

            Assert.Single(aira.Wounds);
        }

        [Fact]
        public void Pyrocumulus_rolls_a_d6_for_every_sprint_and_wounds_on_four_plus()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "pyrocumulus");

            int wounded = 0;
            foreach (string id in new[] { "aira", "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(id);
                game.Sprint();
                game.EndTurnWithoutFinalAction();
                wounded += Inv(game, id).Wounds.Count;
            }

            Assert.Contains(game.State.Log, e => e.Type == "event" && e.Detail.Contains("rolled"));
            Assert.True(wounded > 0, "4 Sprints against a 4+ threshold should have wounded someone");
        }

        [Fact]
        public void Muddy_and_interference_take_their_actions_off_the_table()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "muddy");
            Assert.Contains("muddy", string.Join("|", game.ActionBlockers("aira", Game.ActionPickUpPoi)));
            Assert.Empty(game.ActionBlockers("aira", Game.ActionCharge));

            DrawEventNextRound(game, "interference");
            Assert.Contains("interference", string.Join("|", game.ActionBlockers("aira", Game.ActionCharge)));
            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.ChargeFlashlight());
        }

        [Fact]
        public void Hazy_trims_the_flashlight_down_to_its_center_line()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "hazy");
            var unrestricted = game.PreviewFlashlight("aira", 0.0);
            Assert.True(unrestricted.Count > 1, "the test angle must light more than the Investigator's own space");

            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);

            var placement = game.State.Flashlights.Single();
            Assert.Contains("285", placement.BrightSpaces);
            Assert.True(placement.BrightSpaces.Count < unrestricted.Count);
            Assert.Subset(unrestricted, placement.BrightSpaces.ToHashSet());
            // The board overlay is trimmed alongside the placement.
            Assert.Equal(placement.BrightSpaces.ToHashSet(), game.State.Overlay.BrightSpaces);
        }

        [Fact]
        public void Foggy_takes_an_extra_charge_when_the_flashlight_is_placed()
        {
            var game = NewAmusementParkGame();
            DrawEventNextRound(game, "foggy");
            var aira = Inv(game, "aira");
            int charge = aira.Charge;
            Assert.True(charge >= 2);

            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);

            Assert.Equal(charge - 2, aira.Charge);
        }

        // ---------- Moderate Events ----------

        [Fact]
        public void Heavy_smoke_blocks_the_stamina_a_rest_would_have_gained()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "heavy-smoke");
            Assert.Equal(1, game.RoundModifier(Game.NoRestStaminaKey));

            var aira = Inv(game, "aira");
            aira.Stamina = 2; // below the track maximum, so Resting would normally add 1
            game.BeginInvestigatorTurn("aira");
            game.Rest();
            game.EndTurnWithoutFinalAction();

            Assert.Equal(2, aira.Stamina);
            Assert.Contains(game.State.Log, e => e.Type == "event" && e.Detail.Contains("no Stamina from Resting"));
        }

        [Fact]
        public void Pyrocumulus_records_the_sprint_d6_threshold()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "pyrocumulus");
            Assert.Equal(4, game.RoundModifier(Game.SprintD6WoundThresholdKey));
        }

        [Fact]
        public void Misty_keeps_the_flashlight_inside_three_spaces()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "misty");
            var within3 = game.Graph.DistancesFrom("285", 3, game.State.Overlay);
            double angle = Enumerable.Range(0, 16)
                .Select(i => Math.PI * 2 * i / 16)
                .First(a => game.PreviewFlashlight("aira", a).Any(s => !within3.ContainsKey(s)));

            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(angle);

            var placement = game.State.Flashlights.Single();
            Assert.All(placement.BrightSpaces, s => Assert.True(within3.ContainsKey(s), $"{s} is further than 3 spaces"));
            Assert.Equal(placement.BrightSpaces.ToHashSet(), game.State.Overlay.BrightSpaces);
        }

        [Fact]
        public void Roll_vortex_places_the_chosen_tokens_and_drains_that_zone()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "roll-vortex");
            Assert.True(game.EventChoicePending);
            Assert.Equal(new List<string> { "roll-vortex" }, game.PendingEventChoices());

            var aira = Inv(game, "aira");
            aira.Space = "S-1"; // inside the chosen Zone
            int stamina = aira.Stamina;
            int charge = aira.Charge;
            var mitchell = Inv(game, "mitchell"); // outside it
            int mitchellStamina = mitchell.Stamina;

            game.ResolveEventChoice(new List<string> { "S", "door:S-4", "door:S-8", "window:S-12|S-13" });

            Assert.Equal(DoorState.Destroyed, game.State.Overlay.DoorState("S-4"));
            Assert.Equal(DoorState.Destroyed, game.State.Overlay.DoorState("S-8"));
            Assert.Contains(BoardOverlay.EdgeKey("S-12", "S-13"), game.State.Overlay.OpenWindows);
            Assert.Equal(stamina - 1, aira.Stamina);
            Assert.Equal(charge - 1, aira.Charge);
            Assert.Equal(mitchellStamina, mitchell.Stamina);
            Assert.False(game.EventChoicePending);
        }

        [Fact]
        public void A_rejected_event_choice_changes_nothing_and_stays_pending()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "roll-vortex");

            // K-6 is a Door, but it is not in Zone S.
            Assert.Throws<InvalidOperationException>(() =>
                game.ResolveEventChoice(new List<string> { "S", "door:S-4", "door:K-6" }));

            Assert.Equal(DoorState.Open, game.State.Overlay.DoorState("S-4"));
            Assert.Equal(DoorState.Open, game.State.Overlay.DoorState("K-6"));
            Assert.True(game.EventChoicePending);
        }

        // ---------- Choice plumbing ----------

        [Fact]
        public void Fallen_tree_places_a_false_door_on_an_empty_door_space()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "fallen-tree");

            Assert.Throws<InvalidOperationException>(() =>
                game.ResolveEventChoice(new List<string> { "door:285" })); // 285 is not a Door

            game.ResolveEventChoice(new List<string> { "door:K-6" });
            Assert.Equal(DoorState.False, game.State.Overlay.DoorState("K-6"));
            Assert.False(game.EventChoicePending);
        }

        [Fact]
        public void Flare_up_lowers_the_charge_of_up_to_two_investigators()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "flare-up");
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");
            var mitchell = Inv(game, "mitchell");
            int charge = aira.Charge;

            Assert.Throws<InvalidOperationException>(() =>
                game.ResolveEventChoice(new List<string> { "aira", "lucy-belle", "mitchell" }));

            game.ResolveEventChoice(new List<string> { "aira", "lucy-belle" });
            Assert.Equal(charge - 1, aira.Charge);
            Assert.Equal(charge - 1, lucy.Charge);
            Assert.Equal(charge, mitchell.Charge);
            Assert.Throws<InvalidOperationException>(() => game.ResolveEventChoice(new List<string> { "aira" }));
        }

        [Fact]
        public void An_unanswered_event_choice_expires_at_the_end_of_the_round()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "fallen-tree");
            Assert.True(game.EventChoicePending);

            game.State.EventDeck.Insert(0, "firebreak");
            FinishRound(game);

            Assert.False(game.EventChoicePending);
            Assert.Contains(game.State.Log, e => e.Type == "event" &&
                e.Detail.Contains("fallen-tree") && e.Detail.Contains("never made their choice"));
        }

        [Fact]
        public void Resolving_a_choice_no_event_is_waiting_on_throws()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "firebreak");
            Assert.False(game.EventChoicePending);
            Assert.Throws<InvalidOperationException>(() => game.ResolveEventChoice());
            Assert.Throws<InvalidOperationException>(() => game.ResolveEventChoice("firebreak", null));
        }

        // ---------- Major Events ----------

        [Fact]
        public void Firestorm_persists_across_rounds_and_charges_stamina_for_moving()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "firestorm");
            Assert.Equal(new List<string> { "firestorm" }, game.PersistentMajorEvents());

            game.BeginInvestigatorTurn("aira");
            Assert.Equal(1, game.RoundModifier(Game.MoveStaminaCostKey));
            var aira = Inv(game, "aira");
            int stamina = aira.Stamina;

            string away = FreeNeighbour(game, "285");
            game.MoveStep(away);
            Assert.Equal(stamina - 1, aira.Stamina);

            // The cost is paid once per turn, not once per step.
            game.MoveStep("285");
            Assert.Equal(stamina - 1, aira.Stamina);

            // Still in force a round later, with a different card face-up.
            DrawEventNextRound(game, "firebreak");
            Assert.Equal(new List<string> { "firestorm" }, game.PersistentMajorEvents());
            game.BeginInvestigatorTurn(FirstAliveId(game));
            Assert.Equal(1, game.RoundModifier(Game.MoveStaminaCostKey));
        }

        [Fact]
        public void Downpour_keeps_reasserting_its_line_of_sight_limit_every_round()
        {
            var game = NewAmusementParkGame();
            DrawEventNextRound(game, "downpour");
            Assert.Equal(new List<string> { "downpour" }, game.PersistentMajorEvents());

            game.BeginInvestigatorTurn("aira");
            Assert.Equal(1, game.RoundModifier(Game.FlashlightCenterLineOnlyKey));

            DrawEventNextRound(game, "eerie-calm");
            Assert.Equal(0, game.RoundModifier(Game.FlashlightCenterLineOnlyKey)); // not until a turn starts
            game.BeginInvestigatorTurn(FirstAliveId(game));
            Assert.Equal(1, game.RoundModifier(Game.FlashlightCenterLineOnlyKey));
        }

        [Fact]
        public void Toxic_gasses_keeps_rolling_for_every_investigator_each_round()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "toxic-gasses");

            int wounded = 0;
            for (int round = 0; round < 3; round++)
            {
                game.BeginInvestigatorTurn(FirstAliveId(game));
                wounded = game.State.Investigators.Sum(i => i.Wounds.Count);
                Assert.Equal(new List<string> { "toxic-gasses" }, game.PersistentMajorEvents());
                if (wounded > 0)
                {
                    break;
                }
                DrawEventNextRound(game, "firebreak");
            }
            Assert.True(wounded > 0, "3 rounds of Sprint-die rolls should have caught someone");
            Assert.Contains(game.State.Log, e => e.Type == "event" && e.Detail.StartsWith("toxic-gasses"));
        }

        [Fact]
        public void Hail_storm_wounds_the_unwounded_flips_the_rest_and_does_not_persist()
        {
            var game = NewAmusementParkGame();
            var lucy = Inv(game, "lucy-belle");
            game.GainWound(lucy, faceUp: false);

            DrawEventNextRound(game, "hail-storm");

            Assert.Empty(game.PersistentMajorEvents()); // "Discard this card after applying its effects"
            foreach (var inv in game.State.Investigators)
            {
                Assert.NotEmpty(inv.Wounds);
                Assert.All(inv.Wounds, w => Assert.True(w.FaceUp));
            }
        }

        [Fact]
        public void Fire_tornado_rolls_every_round_and_only_offers_the_zone_choice_on_a_high_roll()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "fire-tornado");

            bool sawDrain = false;
            bool sawChoice = false;
            for (int round = 0; round < 10 && !(sawDrain && sawChoice); round++)
            {
                game.BeginInvestigatorTurn(FirstAliveId(game));
                int roll = game.RoundModifier(Game.FireTornadoRollKey);
                Assert.InRange(roll, 1, 6);
                if (roll <= 3)
                {
                    sawDrain = true;
                    Assert.DoesNotContain("fire-tornado", game.PendingEventChoices());
                }
                else
                {
                    sawChoice = true;
                    Assert.Contains("fire-tornado", game.PendingEventChoices());
                }
                DrawEventNextRound(game, "firebreak");
            }
            Assert.True(sawDrain, "no round rolled 1-3");
            Assert.True(sawChoice, "no round rolled 4-6");
        }

        [Fact]
        public void Fire_tornados_zone_choice_destroys_every_door_in_that_zone()
        {
            var game = NewSawmillGame();
            DrawEventNextRound(game, "fire-tornado");
            for (int round = 0; round < 10; round++)
            {
                game.BeginInvestigatorTurn(FirstAliveId(game));
                if (game.PendingEventChoices().Contains("fire-tornado"))
                {
                    break;
                }
                DrawEventNextRound(game, "firebreak");
            }
            Assert.Contains("fire-tornado", game.PendingEventChoices());

            game.ResolveEventChoice("fire-tornado", new List<string> { "K", "false:K-6" });
            Assert.Equal(DoorState.False, game.State.Overlay.DoorState("K-6"));
            Assert.DoesNotContain("fire-tornado", game.PendingEventChoices());
        }

        // ---------- Deck composition ----------

        [Fact]
        public void Every_event_card_in_the_data_has_an_implementation()
        {
            foreach (var card in TestData.Db.Deck("event"))
            {
                var game = card.Owner == "sawmill" ? NewSawmillGame() : NewAmusementParkGame();
                DrawEventNextRound(game, card.Id);
                Assert.DoesNotContain(game.State.Log,
                    e => e.Type == "todo" && e.Detail.Contains("has no implementation"));
                // BeginRound logs the bare card id; resolving it has to say something as well.
                int logged = game.State.Log.Count(
                    e => e.Type == "event" && e.Detail.StartsWith(card.Id, StringComparison.Ordinal));
                Assert.True(logged >= 2, $"{card.Id} logged nothing when it resolved");
            }
        }

        [Fact]
        public void Each_scenario_only_ever_deals_its_own_event_cards()
        {
            var cards = TestData.Db.Deck("event").ToDictionary(c => c.Id);
            foreach (var (scenarioId, game) in new[]
            {
                ("sawmill", NewSawmillGame()),
                ("amusement-park", NewAmusementParkGame()),
            })
            {
                // Round 1 already took the top card, so the deal order is CurrentEvent then the deck.
                var dealt = new List<string> { game.State.CurrentEvent! }.Concat(game.State.EventDeck).ToList();
                Assert.All(dealt, id => Assert.Equal(scenarioId, cards[id].Owner));
                // 1 Major, face-down on the bottom; Minors above Moderates above it.
                Assert.Equal(1, dealt.Count(id => cards[id].Severity == "major"));
                Assert.Equal("major", cards[dealt[dealt.Count - 1]].Severity);
            }
        }
    }
}
