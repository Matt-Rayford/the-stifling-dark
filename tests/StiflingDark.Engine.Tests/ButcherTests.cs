using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class ButcherTests
    {
        private static readonly string[] InvestigatorIds = { "aira", "lucy-belle", "mitchell", "vincent" };

        private static Game NewButcherGame(string attackCardId, List<string> abilityCardIds, ulong seed = 42)
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
            // 4 investigators: the Butcher takes 1 Attack card + 2 Ability cards.
            game.SetupAdversaryCards(attackCardId, abilityCardIds);
            game.PlaceAdversary("S-25");
            game.FinishAdversarySetup();
            return game;
        }

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        /// <summary>Take a do-nothing Investigator turn for everyone still waiting this round,
        /// leaving the game in the Adversary Turn phase without touching the Adversary at all.</summary>
        private static void FinishInvestigatorTurns(Game game)
        {
            foreach (var inv in game.State.Investigators
                .Where(i => !i.Dead && !i.Escaped && !i.TurnTakenThisRound).ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
        }

        /// <summary>Finish the Investigators' turns, do nothing as the Adversary, and close the round.</summary>
        private static void SkipRound(Game game)
        {
            FinishInvestigatorTurns(game);
            game.AdversaryEndTurn();
        }

        // ---------- Round-start and Investigator-turn sub-hooks ----------

        [Fact]
        public void Decay_makes_next_rounds_flashlight_placements_cost_an_extra_charge()
        {
            var game = NewButcherGame("rend", new List<string> { "decay", "escalating-terror" });
            FinishInvestigatorTurns(game);
            game.State.Adversary.Counters["stalk"] = 1;
            game.PlayAdversaryCard("decay");
            game.AdversaryEndTurn();

            // Round 2: the surcharge is in force from the moment the round begins, so it is
            // already there for the first Flashlight of the round.
            Assert.Equal(2, game.State.Round);
            Assert.Equal(1, game.RoundModifier(Game.FlashlightChargeSurchargeKey));
            var aira = Inv(game, "aira");
            aira.Charge = 3;
            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);
            Assert.Equal(1, aira.Charge);

            SkipRound(game);
            Assert.Equal(0, game.RoundModifier(Game.FlashlightChargeSurchargeKey));
        }

        [Fact]
        public void An_investigator_who_steps_on_an_evil_eye_token_hands_the_butcher_a_stalk()
        {
            var game = NewButcherGame("rend", new List<string> { "evil-eye", "escalating-terror" });
            var aira = Inv(game, "aira");
            aira.Space = "S-18";
            FinishInvestigatorTurns(game);
            game.State.Adversary.Counters["stalk"] = 0;
            game.PlayAdversaryCard("evil-eye", new List<string> { "S-21", "S-24" });
            game.AdversaryEndTurn();

            game.BeginInvestigatorTurn("aira");
            game.MoveStep("S-21");

            Assert.Equal(1, game.State.Adversary.Counters["stalk"]);
            Assert.Null(game.BoardTokenSpace("evil-eye-1"));
            Assert.Equal("S-24", game.BoardTokenSpace("evil-eye-2")); // the other token is untouched
        }

        [Fact]
        public void An_investigator_who_ends_their_turn_on_an_evil_eye_token_also_trips_it()
        {
            var game = NewButcherGame("rend", new List<string> { "evil-eye", "escalating-terror" });
            FinishInvestigatorTurns(game);
            game.State.Adversary.Counters["stalk"] = 0;
            game.PlayAdversaryCard("evil-eye", new List<string> { "S-21", "S-24" });
            game.AdversaryEndTurn();

            var aira = Inv(game, "aira");
            aira.Space = "S-24"; // put there by something other than a Move
            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction();

            Assert.Equal(1, game.State.Adversary.Counters["stalk"]);
            Assert.Null(game.BoardTokenSpace("evil-eye-2"));
        }

        // ---------- Stalk range ----------

        [Fact]
        public void Stalk_requires_the_target_within_8_spaces()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var within8 = game.Graph.DistancesFrom(adv.Space, 8, game.State.Overlay);
            string farSpace = game.Graph.Def.Spaces.Select(s => s.Id).First(id => !within8.ContainsKey(id));
            var aira = Inv(game, "aira");
            aira.Space = farSpace;

            Assert.Throws<InvalidOperationException>(() => game.ButcherStalk(new List<string> { "aira" }));
        }

        // ---------- Spine Chill / Stalk economy ----------

        [Fact]
        public void First_stalk_gives_spine_chill_without_gaining_stalk()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            aira.Space = game.Graph.DistancesFrom(adv.Space, 8, game.State.Overlay).Keys.First();

            game.ButcherStalk(new List<string> { "aira" });

            Assert.True(adv.SpineChill.ContainsKey("aira"));
            Assert.Equal(1, adv.SpineChill["aira"]);
            Assert.Equal(0, adv.Counters.TryGetValue("stalk", out int s) ? s : 0);
            Assert.Equal(adv.Space, adv.ShadowTokens["main"]);
        }

        // ---------- Stalking vs the light (playtest report: a Shadow token appeared on a
        // space the reporter believed a Flashlight beam had lit) ----------

        [Fact]
        public void Placing_a_flashlight_over_the_hidden_butcher_reveals_him_immediately()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            var aira = Inv(game, "aira");
            aira.Charge = 3;
            var beam = game.PreviewFlashlight("aira", 0.0);
            Assert.NotEmpty(beam);
            game.State.Adversary.Space = beam.First();

            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);

            Assert.True(game.State.Adversary.Revealed);
        }

        [Fact]
        public void Butcher_who_walks_into_a_standing_beam_is_revealed_and_cannot_stalk()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            var aira = Inv(game, "aira");
            aira.Charge = 3;
            game.BeginInvestigatorTurn("aira");
            game.PlaceFlashlight(0.0);

            // A lit space with a dark neighbour, so the step INTO the light is unambiguous.
            var beam = game.State.Flashlights.Single().BrightSpaces.ToHashSet();
            string lit = null, darkNeighbour = null;
            foreach (string candidate in beam)
            {
                darkNeighbour = game.Graph.DistancesFrom(candidate, 1, game.State.Overlay).Keys
                    .FirstOrDefault(n => n != candidate && !beam.Contains(n) &&
                        game.Graph.TryStep(FigureKind.Adversary, n, candidate, game.State.Overlay) != null);
                if (darkNeighbour != null)
                {
                    lit = candidate;
                    break;
                }
            }
            Assert.NotNull(lit);
            Assert.NotNull(darkNeighbour);

            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            adv.Space = darkNeighbour!;
            Assert.False(adv.Revealed);
            game.AdversaryMoveStep(lit);

            Assert.True(adv.Revealed);
            aira.Space = lit; // in range and in sight — only Revealed stands in the way
            Assert.Throws<InvalidOperationException>(() => game.ButcherStalk(new List<string> { "aira" }));
        }

        [Fact]
        public void Restalking_the_following_round_converts_chill_into_a_stalk()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            aira.Space = game.Graph.DistancesFrom(adv.Space, 8, game.State.Overlay).Keys.First();
            game.ButcherStalk(new List<string> { "aira" }); // round 1: gives chill
            game.AdversaryEndTurn(); // -> round 2

            FinishInvestigatorTurns(game);
            Assert.True(game.Graph.DistancesFrom(adv.Space, 8, game.State.Overlay).ContainsKey(aira.Space));
            game.ButcherStalk(new List<string> { "aira" }); // round 2 (the following round): converts

            Assert.False(adv.SpineChill.ContainsKey("aira"));
            Assert.Equal(1, adv.Counters["stalk"]);
        }

        [Fact]
        public void Spine_chill_expires_if_not_restalked_the_following_round()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            aira.Space = game.Graph.DistancesFrom(adv.Space, 8, game.State.Overlay).Keys.First();
            game.ButcherStalk(new List<string> { "aira" }); // round 1: gives chill
            game.AdversaryEndTurn(); // -> round 2

            SkipRound(game); // round 2: Butcher does nothing (no re-Stalk) -> round 3
            SkipRound(game); // round 3's Adversary turn starts: BeginButcherTurn expires the token -> round 4

            Assert.False(adv.SpineChill.ContainsKey("aira"));
            Assert.Equal(0, adv.Counters.TryGetValue("stalk", out int s) ? s : 0);
        }

        [Fact]
        public void Escalating_terror_doubles_the_next_stalk_gain()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            aira.Space = game.Graph.DistancesFrom(adv.Space, 8, game.State.Overlay).Keys.First();
            game.ButcherStalk(new List<string> { "aira" }); // round 1: gives chill
            game.AdversaryEndTurn(); // -> round 2

            FinishInvestigatorTurns(game);
            game.PlayAdversaryCard("escalating-terror");
            game.ButcherStalk(new List<string> { "aira" }); // converts chill: 1 gained, doubled to 2

            Assert.Equal(2, adv.Counters["stalk"]);
        }

        // ---------- Abilities ----------

        [Fact]
        public void Disturbed_presence_drains_lungs_and_grants_a_stalk_for_two_targets_at_turn_end()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");
            var within4 = game.Graph.DistancesFrom(adv.Space, 4, game.State.Overlay).Keys.ToList();
            aira.Space = within4[0];
            lucy.Space = within4[0];
            int airaStamina = aira.Stamina;
            int lucyStamina = lucy.Stamina;

            game.PlayAdversaryCard("disturbed-presence", new List<string> { "aira", "lucy-belle" });
            // Arming the card must not resolve it immediately: nothing happens until the turn
            // actually ends.
            Assert.Equal(airaStamina, aira.Stamina);
            Assert.Equal(lucyStamina, lucy.Stamina);
            Assert.Equal(0, adv.Counters.TryGetValue("stalk", out int s) ? s : 0);

            game.AdversaryEndTurn();

            Assert.Equal(airaStamina - 1, aira.Stamina);
            Assert.Equal(lucyStamina - 1, lucy.Stamina);
            Assert.Empty(aira.Wounds); // Lungs lost this way do not incur a Wound
            Assert.Equal(1, adv.Counters["stalk"]);
        }

        [Fact]
        public void Disturbed_presence_resolves_from_the_butchers_final_position_not_where_it_was_played()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            var within4 = game.Graph.DistancesFrom(adv.Space, 4, game.State.Overlay).Keys.ToList();
            aira.Space = within4[0]; // within 4 of where the Butcher plays the card...
            int airaStamina = aira.Stamina;

            game.PlayAdversaryCard("disturbed-presence");

            // ...but he then moves far enough away that nobody is within 4 when his turn ends.
            string farSpace = game.Graph.Def.Spaces.Select(s => s.Id)
                .First(id => !game.Graph.DistancesFrom(id, 4, game.State.Overlay).ContainsKey(aira.Space));
            adv.Space = farSpace;

            game.AdversaryEndTurn();

            Assert.Equal(airaStamina, aira.Stamina); // no drain
            Assert.Equal(0, adv.Counters.TryGetValue("stalk", out int s) ? s : 0); // no stalk
        }

        [Fact]
        public void Disturbed_presence_drains_everyone_within_4_of_the_final_position_even_if_2_were_never_targeted_when_played()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");
            int airaStamina = aira.Stamina;
            int lucyStamina = lucy.Stamina;

            // Play the card (no targets required to arm it), then move the Butcher so that both
            // Aira and Lucy Belle end up within 4 of his FINAL space.
            game.PlayAdversaryCard("disturbed-presence");
            var within4 = game.Graph.DistancesFrom(adv.Space, 4, game.State.Overlay).Keys.ToList();
            aira.Space = within4[0];
            lucy.Space = within4[0];

            game.AdversaryEndTurn();

            Assert.Equal(airaStamina - 1, aira.Stamina);
            Assert.Equal(lucyStamina - 1, lucy.Stamina);
            Assert.Equal(1, adv.Counters["stalk"]);
        }

        [Fact]
        public void Ability_cooldown_makes_it_unavailable_next_turn_and_available_after()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            aira.Space = game.Graph.DistancesFrom(adv.Space, 4, game.State.Overlay).Keys.First();

            game.PlayAdversaryCard("disturbed-presence", new List<string> { "aira" });
            Assert.DoesNotContain("disturbed-presence", adv.ActiveAbilities);
            game.AdversaryEndTurn(); // round 1 -> 2

            FinishInvestigatorTurns(game);
            Assert.Throws<InvalidOperationException>(() =>
                game.PlayAdversaryCard("disturbed-presence", new List<string> { "aira" }));
            game.AdversaryEndTurn(); // round 2 -> 3

            FinishInvestigatorTurns(game);
            aira.Space = game.Graph.DistancesFrom(adv.Space, 4, game.State.Overlay).Keys.First();
            game.PlayAdversaryCard("disturbed-presence", new List<string> { "aira" }); // available again
            Assert.DoesNotContain("disturbed-presence", adv.ActiveAbilities);
        }

        // ---------- Attacks ----------

        [Fact]
        public void Attack_throws_when_no_stalk_is_available()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            aira.Space = game.Graph.AdjacentForAdversaryAbilities(adv.Space, game.State.Overlay).First();

            Assert.Throws<InvalidOperationException>(() => game.PlayAdversaryCard("rend", new List<string> { "aira" }));
        }

        [Fact]
        public void Rend_spends_a_stalk_deals_a_wound_and_places_the_shadow_token()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            adv.Counters["stalk"] = 1;
            var aira = Inv(game, "aira");
            aira.Space = game.Graph.AdjacentForAdversaryAbilities(adv.Space, game.State.Overlay).First();
            int woundDeckBefore = game.State.WoundDeck.Count;

            game.PlayAdversaryCard("rend", new List<string> { "aira" });

            Assert.Equal(0, adv.Counters["stalk"]);
            Assert.Single(aira.Wounds);
            Assert.True(aira.Wounds[0].FaceUp);
            Assert.Equal(adv.Space, adv.ShadowTokens["main"]);
            Assert.Equal(woundDeckBefore - 3, game.State.WoundDeck.Count); // 1 kept + 2 discarded
        }

        [Fact]
        public void Onslaught_repeats_for_free_and_branches_on_nearby_investigators()
        {
            var game = NewButcherGame("onslaught", new List<string> { "disturbed-presence", "escalating-terror" });
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            adv.Counters["stalk"] = 1;
            var aira = Inv(game, "aira");
            var mitchell = Inv(game, "mitchell");
            string adjacentSpace = game.Graph.AdjacentForAdversaryAbilities(adv.Space, game.State.Overlay).First();
            aira.Space = adjacentSpace;
            // Move every other Investigator far away so Aira is not within 4 of another Investigator.
            var farFromAira = game.Graph.Def.Spaces.Select(s => s.Id)
                .First(id => !game.Graph.DistancesFrom(aira.Space, 4, game.State.Overlay).ContainsKey(id));
            mitchell.Space = farFromAira;
            Inv(game, "lucy-belle").Space = farFromAira;
            Inv(game, "vincent").Space = farFromAira;

            game.PlayAdversaryCard("onslaught", new List<string> { "aira" });

            Assert.Equal(2, aira.Wounds.Count);
            Assert.True(aira.Wounds.All(w => w.FaceUp));
            // No MP bonus: Aira was not within 4 of another Investigator, so only the base
            // budget (5 + the turn's sprint roll) applies, with nothing added by the Attack.
            Assert.Equal(5 + adv.SprintRolled, adv.MpRemaining);
            Assert.Equal(0, adv.Counters["stalk"]);
        }

        // ---------- Grave banish ----------

        [Fact]
        public void Grave_placement_validates_the_10_and_3_space_ranges()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            game.SelectEscapeCard("the-grave");

            var aira = Inv(game, "aira");
            var within10 = game.Graph.DistancesFrom(aira.Space, 10, game.State.Overlay);
            string tooFar = game.Graph.Def.Spaces.Select(s => s.Id)
                .First(id => game.State.Investigators.All(inv =>
                    !game.Graph.DistancesFrom(id, 10, game.State.Overlay).ContainsKey(inv.Space)));
            Assert.Throws<InvalidOperationException>(() => game.PlaceGrave(tooFar, tooFar));

            string actual = within10.Keys.First();
            var within3OfActual = game.Graph.DistancesFrom(actual, 3, game.State.Overlay);
            string tooFarDecoy = game.Graph.Def.Spaces.Select(s => s.Id).First(id => !within3OfActual.ContainsKey(id));
            Assert.Throws<InvalidOperationException>(() => game.PlaceGrave(actual, tooFarDecoy));

            string decoy = within3OfActual.Keys.First();
            game.PlaceGrave(actual, decoy);

            Assert.Equal(actual, game.State.Objective.Tokens["grave-actual"]);
            Assert.Equal(decoy, game.State.Objective.Tokens["grave-decoy"]);
        }

        [Fact]
        public void Hook_wrong_guess_reports_a_miss_then_a_correct_guess_banishes_the_butcher()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            game.SelectEscapeCard("the-grave");
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            var within10 = game.Graph.DistancesFrom(aira.Space, 10, game.State.Overlay);
            string actual = within10.Keys.First();
            string decoy = game.Graph.DistancesFrom(actual, 3, game.State.Overlay).Keys.First();
            game.PlaceGrave(actual, decoy);

            // Dig up the Grave: make its space Bright (a real Flashlight placement is not needed
            // for this test), stand on it, and take the Involved Action.
            game.State.Overlay.BrightSpaces.Add(actual);
            aira.Space = actual;
            game.BeginInvestigatorTurn("aira");
            game.DigUpGrave();
            Assert.Contains("the-hook", aira.Items);
            Assert.Contains("frayed-ropes", aira.Items);
            Assert.Equal(game.State.Round + 2, adv.Counters["burning-until"]);

            // Burn out the Grave (2 rounds), then place Aira adjacent to the real Butcher.
            while (game.State.Round < adv.Counters["burning-until"])
            {
                SkipRound(game);
            }
            string adjacentToButcher = game.Graph.Def.Edges
                .Where(e => e.A == adv.Space || e.B == adv.Space)
                .Select(e => e.A == adv.Space ? e.B : e.A)
                .First();
            aira.Space = adjacentToButcher;

            // Guess some other space adjacent to Aira (never the Butcher's real space) first, to
            // exercise the miss branch before the correct guess banishes him.
            string wrongGuess = game.Graph.Def.Edges
                .Where(e => e.A == aira.Space || e.B == aira.Space)
                .Select(e => e.A == aira.Space ? e.B : e.A)
                .First(s => s != adv.Space);

            game.BeginInvestigatorTurn("aira");
            game.UseTheHook(wrongGuess);
            Assert.NotEqual(GamePhase.GameOver, game.State.Phase);

            SkipRound(game);
            aira.Space = adjacentToButcher;
            game.BeginInvestigatorTurn("aira");
            game.UseTheHook(adv.Space);

            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
        }

        [Fact]
        public void Frayed_ropes_forces_a_nearby_shadow_token_and_has_3_uses()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            game.SelectEscapeCard("the-grave");
            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            string actual = game.Graph.DistancesFrom(aira.Space, 10, game.State.Overlay).Keys.First();
            string decoy = game.Graph.DistancesFrom(actual, 3, game.State.Overlay).Keys.First();
            game.PlaceGrave(actual, decoy);
            game.State.Overlay.BrightSpaces.Add(actual);
            aira.Space = actual;
            game.BeginInvestigatorTurn("aira");
            game.DigUpGrave(); // ends Aira's turn for this round, carrying Frayed Ropes

            SkipRound(game); // give Aira a fresh turn next round to use the item she is carrying
            game.BeginInvestigatorTurn("aira");
            game.UseFrayedRopes();
            Assert.Equal("aira", game.State.ActiveInvestigator); // free action: turn still open
            Assert.Equal(1, adv.Counters["frayed-ropes-uses"]);

            string within3 = game.Graph.DistancesFrom(adv.Space, 3, game.State.Overlay).Keys.First();
            game.AnswerFrayedRopes(within3);
            Assert.Equal(within3, adv.ShadowTokens["frayed"]);
            game.EndTurnWithoutFinalAction();
        }

        // ---------- Turn start ----------

        [Fact]
        public void Begin_butcher_turn_clears_noise_and_shadow_tokens()
        {
            var game = NewButcherGame("rend", new List<string> { "disturbed-presence", "escalating-terror" });
            var adv = game.State.Adversary;
            FinishInvestigatorTurns(game); // -> Adversary Turn phase (the framework already clears Noise here)

            // Simulate leftovers from earlier in the Adversary's turn, before EnsureAdversaryTurnStarted fires.
            adv.NoiseTokens.Add("leftover-noise");
            adv.ShadowTokens["main"] = adv.Space;

            game.AdversaryEndTurn(); // BeginButcherTurn runs first and must clear both

            Assert.Empty(adv.NoiseTokens);
            Assert.Empty(adv.ShadowTokens);
        }
    }
}
