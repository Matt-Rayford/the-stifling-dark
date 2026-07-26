using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class ObjectiveTests
    {
        private static readonly string[] InvestigatorIds = { "aira", "lucy-belle", "mitchell", "vincent" };

        private static Game NewGame(string scenarioId, ulong seed, int investigators = 2, string adversary = "butcher")
        {
            string[] starts = scenarioId == "sawmill"
                ? new[] { "285", "286", "305", "307" }
                : new[] { "142", "153", "166", "180" };
            var startSpaces = new Dictionary<string, string>();
            for (int i = 0; i < investigators; i++)
            {
                startSpaces[InvestigatorIds[i]] = starts[i];
            }
            int medicalCount = TestData.Db.Config.ByInvestigatorCount[investigators].MedicalItemsOnBoard;
            var medical = TestData.Db.Map(scenarioId).Spaces
                .Where(s => s.Kind == SpaceKind.MedicalItem).Take(medicalCount).Select(s => s.Id).ToList();

            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = scenarioId,
                Seed = seed,
                AdversaryId = adversary,
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
            game.PlaceAdversary(scenarioId == "sawmill" ? "S-25" : "M-20");
            game.FinishAdversarySetup();
            return game;
        }

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        /// <summary>Take a do-nothing turn for everyone still waiting, then close the round.</summary>
        private static void FinishRound(Game game)
        {
            foreach (var inv in game.State.Investigators
                .Where(i => !i.Dead && !i.Escaped && !i.TurnTakenThisRound).ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
        }

        /// <summary>The die roll the engine logged in the last objective entry matching <paramref name="containing"/>.</summary>
        private static int LoggedRoll(Game game, string containing)
        {
            string detail = game.State.Log.Last(e => e.Type == "objective" && e.Detail.Contains(containing)).Detail;
            string tail = detail.Substring(detail.IndexOf("rolled ") + "rolled ".Length);
            return int.Parse(new string(tail.TakeWhile(char.IsDigit).ToArray()));
        }

        // ---------- Selection ----------

        [Fact]
        public void Escape_choices_need_the_required_evidence_turned_in()
        {
            var game = NewGame("sawmill", seed: 5, investigators: 4);
            // 4 Investigators must turn in 5 Evidence.
            Assert.Throws<InvalidOperationException>(() => game.DrawEscapeChoices());
            game.State.Objective.EvidenceTurnedIn = 4;
            Assert.Throws<InvalidOperationException>(() => game.DrawEscapeChoices());

            game.State.Objective.EvidenceTurnedIn = 5;
            var choices = game.DrawEscapeChoices();
            Assert.Equal(3, choices.Count);
            Assert.Contains(choices[0], new[] { "garage", "sawmill" });          // fix-the-truck
            Assert.Contains(choices[1], new[] { "north-gate", "south-gate" });   // power-the-gate
            Assert.Equal("the-grave", choices[2]);                              // butcher banish
        }

        [Fact]
        public void Escape_choices_offer_the_park_objectives_and_the_matching_banish_card()
        {
            var game = NewGame("amusement-park", seed: 11, investigators: 2, adversary: "cult-of-hunlow");
            game.State.Objective.EvidenceTurnedIn = 2;
            var choices = game.DrawEscapeChoices();
            Assert.Contains(choices[0], new[] { "the-zipper", "ferris-wheel" }); // fire-the-flare
            Assert.Contains(choices[1], new[] { "tunnel-of-love", "mirror-maze" }); // service-tunnels
            Assert.Equal("the-altar", choices[2]);
        }

        [Fact]
        public void Selection_rejects_foreign_cards_and_defers_banish_setup()
        {
            // All three Banish hooks (Game.Butcher/Horror/Cult) are implemented now, so this
            // exercises the Butcher's Grave banish selection directly instead of the now-obsolete
            // "hook not implemented yet" path.
            var game = NewGame("sawmill", seed: 3);
            Assert.Throws<InvalidOperationException>(() => game.SelectEscapeCard("mirror-maze"));
            Assert.Throws<InvalidOperationException>(() => game.SelectEscapeCard("the-altar")); // wrong adversary
            Assert.Null(game.State.Objective.SelectedEscapeCard);

            game.SelectEscapeCard("the-grave");
            Assert.Equal("the-grave", game.State.Objective.SelectedEscapeCard);
            Assert.Throws<InvalidOperationException>(() => game.SelectEscapeCard("north-gate"));
        }

        // ---------- Card setup ----------

        [Fact]
        public void North_gate_setup_places_the_printed_tokens_and_rolls_for_the_lockbox()
        {
            var game = NewGame("sawmill", seed: 5);
            game.SelectEscapeCard("north-gate");
            var tokens = game.State.Objective.Tokens;
            Assert.Equal("S-17", tokens["saw"]);
            Assert.Equal("10", tokens["locked-escape"]);

            int roll = LoggedRoll(game, "north-gate setup rolled");
            string expected = roll <= 2 ? "S-1" : roll <= 4 ? "104" : "S-41";
            Assert.Equal(expected, tokens["lockbox"]);
            Assert.Equal(3, tokens.Count);
        }

        [Fact]
        public void Garage_setup_places_the_truck_and_all_three_parts()
        {
            var game = NewGame("sawmill", seed: 5);
            game.SelectEscapeCard("garage");
            var tokens = game.State.Objective.Tokens;
            Assert.Equal("G-6", tokens["truck"]);
            int roll = LoggedRoll(game, "garage setup rolled");
            string[] expected = roll <= 2
                ? new[] { "S-37", "308", "313" }
                : roll <= 4 ? new[] { "50", "134", "159" } : new[] { "12", "L-7", "L-19" };
            Assert.Equal(expected, new[] { tokens["battery"], tokens["repair-kit"], tokens["spark-plug"] });
        }

        [Fact]
        public void Tunnel_of_love_setup_places_both_ride_parts_and_the_grinder()
        {
            var game = NewGame("amusement-park", seed: 5);
            game.SelectEscapeCard("tunnel-of-love");
            var tokens = game.State.Objective.Tokens;
            Assert.Equal("T-18", tokens["locked-escape"]);
            Assert.Equal("175", tokens["ride-parts-1"]);
            Assert.Equal("86", tokens["ride-parts-2"]);
            int roll = LoggedRoll(game, "tunnel-of-love setup rolled");
            Assert.Equal(roll <= 2 ? "M-6" : roll <= 4 ? "80" : "182", tokens["angle-grinder"]);
        }

        [Fact]
        public void The_zipper_setup_places_the_flare_gun_and_two_ammo()
        {
            var game = NewGame("amusement-park", seed: 9);
            game.SelectEscapeCard("the-zipper");
            var tokens = game.State.Objective.Tokens;
            Assert.Equal("71", tokens["locked-escape"]);
            int roll = LoggedRoll(game, "the-zipper setup rolled");
            string[] expected = roll <= 2
                ? new[] { "M-36", "M-20", "149" }
                : roll <= 4 ? new[] { "257", "188", "293" } : new[] { "19", "6", "27" };
            Assert.Equal(expected, new[] { tokens["flare-gun"], tokens["ammo-1"], tokens["ammo-2"] });
        }

        // ---------- Power the Gate ----------

        [Fact]
        public void Power_the_gate_run_ends_in_an_investigator_win()
        {
            var game = NewGame("sawmill", seed: 5);
            game.SelectEscapeCard("north-gate");
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");

            game.BeginInvestigatorTurn("aira");
            aira.Space = game.State.Objective.Tokens["lockbox"];
            game.PickUpObjectiveToken("lockbox");
            Assert.Equal("aira", game.State.Objective.TokenCarriers["lockbox"]);
            aira.Space = "S-17";
            game.OpenLockbox(pushYourLuck: false);
            Assert.Equal(1, game.State.Objective.Supplies);
            Assert.Equal(FinalActionKind.InvolvedAction, aira.FinalAction);
            FinishRound(game);

            for (int i = 0; i < 3; i++)
            {
                game.BeginInvestigatorTurn("aira");
                game.OpenLockbox(pushYourLuck: false);
                FinishRound(game);
            }
            Assert.Equal(4, game.State.Objective.Supplies);
            Assert.Contains("fuse", aira.Items);

            game.BeginInvestigatorTurn("aira");
            aira.Space = "10";
            game.PowerTheGate();
            Assert.True(game.State.Objective.EscapeOpen);
            FinishRound(game);

            game.BeginInvestigatorTurn("aira");
            game.EscapeThroughGate();
            Assert.True(aira.Escaped);
            Assert.Equal(GameResult.Undecided, game.State.Result);
            game.EndTurnWithoutFinalAction();

            game.BeginInvestigatorTurn("lucy-belle");
            lucy.Space = "10";
            game.EscapeThroughGate();
            Assert.True(lucy.Escaped);
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
        }

        [Fact]
        public void The_saw_may_only_be_used_once_per_round_by_the_whole_team()
        {
            var game = NewGame("sawmill", seed: 5);
            game.SelectEscapeCard("north-gate");
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");

            game.BeginInvestigatorTurn("aira");
            aira.Space = game.State.Objective.Tokens["lockbox"];
            game.PickUpObjectiveToken("lockbox");
            aira.Space = "S-17";
            game.OpenLockbox(pushYourLuck: false);

            // Handing the Lockbox to a second Investigator does not unlock a second use.
            game.BeginInvestigatorTurn("lucy-belle");
            game.State.Objective.TokenCarriers["lockbox"] = "lucy-belle";
            lucy.Space = "S-17";
            Assert.Throws<InvalidOperationException>(() => game.OpenLockbox(pushYourLuck: false));
            Assert.Equal(1, game.State.Objective.Supplies);

            lucy.Space = "286";
            game.EndTurnWithoutFinalAction();
            game.AdversaryEndTurn();
            Assert.Equal(2, game.State.Round);

            aira.Space = "285"; // step off the Saw so Lucy can finish her turn there
            game.BeginInvestigatorTurn("lucy-belle");
            lucy.Space = "S-17";
            game.OpenLockbox(pushYourLuck: false); // legal again next round
            Assert.Equal(2, game.State.Objective.Supplies);
        }

        [Fact]
        public void Pushing_your_luck_wounds_on_four_or_less_in_the_dark_but_only_on_two_or_less_when_bright()
        {
            var dark = PushLuck(bright: false);
            int roll = LoggedRoll(dark, "pushed their luck on the Saw and rolled");
            Assert.InRange(roll, 3, 4); // the seed is chosen so the two light levels disagree
            Assert.Equal(1, dark.State.Objective.Supplies); // no bonus Supply on the Dark Saw
            Assert.Single(Inv(dark, "aira").Wounds);
            Assert.True(Inv(dark, "aira").Wounds[0].FaceUp);

            var bright = PushLuck(bright: true);
            Assert.Equal(roll, LoggedRoll(bright, "pushed their luck on the Saw and rolled"));
            Assert.Equal(2, bright.State.Objective.Supplies);
            Assert.Empty(Inv(bright, "aira").Wounds);
        }

        private static Game PushLuck(bool bright)
        {
            var game = NewGame("sawmill", seed: PushLuckSeed);
            game.SelectEscapeCard("north-gate");
            var aira = Inv(game, "aira");
            if (bright)
            {
                game.State.Overlay.BrightSpaces.Add("S-17");
            }
            game.BeginInvestigatorTurn("aira");
            aira.Space = game.State.Objective.Tokens["lockbox"];
            game.PickUpObjectiveToken("lockbox");
            aira.Space = "S-17"; // printed Dark
            game.OpenLockbox(pushYourLuck: true);
            return game;
        }

        // ---------- Fix the Truck ----------

        [Fact]
        public void One_part_installed_starts_the_truck_only_on_a_six()
        {
            var game = NewGame("sawmill", seed: TruckFailSeed);
            game.SelectEscapeCard("garage");
            var aira = Inv(game, "aira");

            game.BeginInvestigatorTurn("aira");
            aira.Space = game.State.Objective.Tokens["battery"];
            game.PickUpObjectiveToken("battery");
            aira.Space = "G-6";
            game.InstallPart("battery");
            Assert.Equal(1, game.State.Objective.PartsInstalled);
            Assert.False(game.State.Objective.TokenCarriers.ContainsKey("battery"));
            FinishRound(game);

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.StartTruck("220")); // gate is space 10 or 306
            game.StartTruck("10");
            int roll = LoggedRoll(game, "to start the Truck");
            Assert.True(roll < 6, $"seed {TruckFailSeed} was expected to fail the 1-Part roll, rolled {roll}");
            Assert.False(aira.Escaped);
            Assert.False(game.State.Objective.Tokens.ContainsKey("escape"));

            // Once per round for the whole team: nobody else may try again this round.
            game.BeginInvestigatorTurn("lucy-belle");
            Inv(game, "lucy-belle").Space = "G-5"; // adjacent to the Truck at G-6
            Assert.Throws<InvalidOperationException>(() => game.StartTruck("10"));
        }

        [Fact]
        public void Three_parts_start_the_truck_automatically_and_carry_everyone_nearby()
        {
            var game = NewGame("sawmill", seed: 5);
            game.SelectEscapeCard("garage");
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");

            foreach (string part in new[] { "battery", "repair-kit", "spark-plug" })
            {
                game.BeginInvestigatorTurn("aira");
                aira.Space = game.State.Objective.Tokens[part];
                game.PickUpObjectiveToken(part);
                aira.Space = "G-6";
                game.InstallPart(part);
                FinishRound(game);
            }
            Assert.Equal(3, game.State.Objective.PartsInstalled);

            game.BeginInvestigatorTurn("aira");
            game.StartTruck("306");
            Assert.True(aira.Escaped); // 3 Parts: automatic success
            Assert.False(lucy.Escaped);
            Assert.Equal("306", game.State.Objective.Tokens["escape"]);
            Assert.Equal(GameResult.Undecided, game.State.Result);

            game.BeginInvestigatorTurn("lucy-belle");
            lucy.Space = "306";
            game.EscapeAtTruckExit();
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
        }

        // ---------- Fire the Flare ----------

        [Fact]
        public void The_helicopter_only_arrives_two_rounds_after_the_flare()
        {
            var game = NewGame("amusement-park", seed: 9);
            game.SelectEscapeCard("the-zipper");
            var aira = Inv(game, "aira");

            game.BeginInvestigatorTurn("aira");
            aira.Space = game.State.Objective.Tokens["flare-gun"];
            game.PickUpObjectiveToken("flare-gun");
            aira.Space = "65"; // a Zipper carriage: no help called yet
            Assert.Throws<InvalidOperationException>(() => game.EscapeByHelicopter());
            aira.Space = "71"; // the Locked Escape token
            Assert.Throws<InvalidOperationException>(() => game.FireFlareGun()); // no Ammo

            aira.Space = game.State.Objective.Tokens["ammo-1"];
            game.PickUpObjectiveToken("ammo-1");
            aira.Space = "71";
            game.FireFlareGun();
            Assert.Equal(3, game.State.Objective.EscapeReadyRound);
            FinishRound(game);

            game.BeginInvestigatorTurn("aira");
            aira.Space = "65";
            Assert.Throws<InvalidOperationException>(() => game.EscapeByHelicopter()); // round 2
            game.EndTurnWithoutFinalAction();
            FinishRound(game);
            Assert.Equal(3, game.State.Round);

            game.BeginInvestigatorTurn("aira");
            aira.Space = "142"; // not a carriage
            Assert.Throws<InvalidOperationException>(() => game.EscapeByHelicopter());
            aira.Space = "117"; // Zipper carriage
            game.EscapeByHelicopter();
            Assert.True(aira.Escaped);
        }

        // ---------- Service Tunnels ----------

        [Fact]
        public void Ride_parts_wound_on_four_or_less_in_the_dim_and_only_on_a_one_when_bright()
        {
            var dim = NewGame("amusement-park", seed: RidePartsSeed);
            dim.SelectEscapeCard("tunnel-of-love");
            var aira = Inv(dim, "aira");
            dim.BeginInvestigatorTurn("aira");
            aira.Space = "175"; // printed Dim
            dim.PickUpRideParts("ride-parts-1");
            int roll = LoggedRoll(dim, "took ride-parts-1 and rolled");
            Assert.InRange(roll, 2, 4); // the seed is chosen so the two light levels disagree
            Assert.Equal("aira", dim.State.Objective.TokenCarriers["ride-parts-1"]);
            Assert.Single(aira.Wounds);
            Assert.True(aira.Wounds[0].FaceUp);

            var bright = NewGame("amusement-park", seed: RidePartsSeed);
            bright.SelectEscapeCard("tunnel-of-love");
            var aira2 = Inv(bright, "aira");
            bright.State.Overlay.BrightSpaces.Add("175");
            bright.BeginInvestigatorTurn("aira");
            aira2.Space = "175";
            bright.PickUpRideParts("ride-parts-1");
            Assert.Equal(roll, LoggedRoll(bright, "took ride-parts-1 and rolled"));
            Assert.Empty(aira2.Wounds);
        }

        [Fact]
        public void Cutting_the_lock_opens_the_tunnel_on_the_next_round()
        {
            var game = NewGame("amusement-park", seed: 5);
            game.SelectEscapeCard("tunnel-of-love");
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");

            game.BeginInvestigatorTurn("aira");
            aira.Space = game.State.Objective.Tokens["angle-grinder"];
            game.PickUpObjectiveToken("angle-grinder");
            Assert.Throws<InvalidOperationException>(() => game.PickUpObjectiveToken("ride-parts-1"));
            aira.Space = "175";
            game.PickUpRideParts("ride-parts-1");
            FinishRound(game);

            game.BeginInvestigatorTurn("aira");
            aira.Space = "T-18";
            Assert.Throws<InvalidOperationException>(() => game.EscapeThroughTunnel()); // lock not cut
            game.OpenServiceTunnel();
            Assert.Equal(3, game.State.Objective.EscapeReadyRound);
            FinishRound(game);

            game.BeginInvestigatorTurn("aira");
            aira.Space = "T-18";
            game.EscapeThroughTunnel();
            Assert.True(game.State.Objective.EscapeOpen);
            Assert.True(aira.Escaped);
            game.EndTurnWithoutFinalAction();

            game.BeginInvestigatorTurn("lucy-belle");
            lucy.Space = "T-18";
            game.EscapeThroughTunnel();
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
        }

        // Seeds verified by running the engine: with these seeds the Objective D6 lands on 3
        // (Dark Saw push-your-luck: Wound; 1-Part Truck: failure; Dim Ride Parts: Wound).
        private const ulong PushLuckSeed = 5;
        private const ulong TruckFailSeed = 5;
        private const ulong RidePartsSeed = 5;
    }
}
