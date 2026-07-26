using System.Text.Json;
using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// Spirit play: adoption and its validation, the Spirit turn (4 MP, a free Sprint every
    /// round, no Rest/Charge/Flashlight/Medical Item), floating through what stops
    /// Investigators, the give-only Trade rule, the 2-Abilities-per-turn and Major-token
    /// economy, and every one of the 3 cards' 4 Abilities.
    /// </summary>
    public class SpiritTests
    {
        private static readonly string[] InvestigatorIds = { "aira", "lucy-belle", "mitchell", "vincent" };

        private static Game NewGame(
            string adversary = "insatiable-horror",
            string scenarioId = "sawmill",
            int investigators = 3,
            ulong seed = 7)
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
            if (adversary == "cult-of-hunlow")
            {
                game.SetupCultists(new List<string> { "S-21", "S-24", "S-27", "S-30" }.Take(investigators).ToList(), "S-18");
            }
            game.SetupAdversaryCards(Attack(adversary), Abilities(adversary, investigators));
            game.FinishAdversarySetup();
            return game;
        }

        private static string Attack(string adversary) => adversary switch
        {
            "butcher" => "rend",
            "insatiable-horror" => "bufotoxin",
            _ => "ravage",
        };

        private static List<string> Abilities(string adversary, int investigators) => adversary switch
        {
            "butcher" => new List<string> { "decay", "escalating-terror" }.Take(investigators <= 2 ? 1 : 2).ToList(),
            "insatiable-horror" => new List<string> { "devour", "tunnel" }.Take(investigators <= 3 ? 1 : 2).ToList(),
            _ => new List<string> { "razor-like-talons", "dried-tongue", "severed-ear" }
                .Take(investigators <= 2 ? 1 : investigators == 3 ? 2 : 3).ToList(),
        };

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        /// <summary>Kill an Investigator outright (WoundsToDie face-down Wounds), no turn needed.</summary>
        private static void Kill(Game game, string invId)
        {
            var inv = Inv(game, invId);
            for (int i = 0; i < TestData.Db.Config.WoundsToDie; i++)
            {
                game.GainWound(inv, faceUp: false);
            }
        }

        /// <summary>Kill an Investigator and hand their player the named Spirit card.</summary>
        private static InvestigatorState BecomeSpirit(Game game, string invId, string spiritId)
        {
            Kill(game, invId);
            game.AdoptSpirit(invId, spiritId);
            return Inv(game, invId);
        }

        /// <summary>Everyone still owed a turn this round takes a do-nothing one — Spirits included,
        /// since a dead Investigator with a Spirit card still holds the phase open.</summary>
        private static void FinishInvestigatorTurns(Game game)
        {
            foreach (var inv in game.State.Investigators
                .Where(i => !i.Escaped && !i.TurnTakenThisRound && (!i.Dead || i.SpiritId != null)).ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
        }

        private static void FinishRound(Game game)
        {
            FinishInvestigatorTurns(game);
            game.AdversaryEndTurn();
        }

        /// <summary>A printed neighbour of <paramref name="space"/> with nobody standing on it.</summary>
        private static string Neighbor(Game game, string space, params string[] avoid)
        {
            foreach (var edge in game.Graph.Def.Edges)
            {
                string? other = edge.A == space ? edge.B : edge.B == space ? edge.A : null;
                if (other == null || avoid.Contains(other) ||
                    game.Graph.TryStep(FigureKind.Spirit, space, other, game.State.Overlay) == null ||
                    game.State.Investigators.Any(i => !i.Dead && !i.Escaped && i.Space == other))
                {
                    continue;
                }
                return other;
            }
            throw new InvalidOperationException($"No free neighbour of {space}.");
        }

        // ---------- Adoption ----------

        [Fact]
        public void Adoption_needs_a_dead_investigator_and_keeps_their_items_evidence_and_standee()
        {
            var game = NewGame();
            var aira = Inv(game, "aira");
            aira.Items.Add("medkit");
            aira.EvidenceCarried.Add("S");
            aira.Charge = 2;
            string standee = aira.Space;

            // Alive: no Spirit card.
            Assert.Throws<InvalidOperationException>(() => game.AdoptSpirit("aira", "apparition"));
            Assert.Equal(3, game.UnusedSpiritIds().Count);

            Kill(game, "aira");
            Assert.Equal(1, game.State.Adversary.Kills);
            Assert.Equal(GameResult.Undecided, game.State.Result);
            Assert.Contains(game.State.Log, e => e.Type == "spirit" && e.Detail.Contains("may take a Spirit"));
            // Dead and no Spirit card yet: off the board.
            Assert.Throws<InvalidOperationException>(() => game.BeginInvestigatorTurn("aira"));
            Assert.Throws<InvalidOperationException>(() => game.AdoptSpirit("aira", "banshee")); // not a Spirit card

            game.AdoptSpirit("aira", "apparition");
            Assert.Equal("apparition", aira.SpiritId);
            Assert.Equal(Game.SpiritMajorTokenStart, aira.SpiritMajorTokens);
            // Items, Evidence, and the standee stay; the player board goes away.
            Assert.Contains("medkit", aira.Items);
            Assert.Contains("S", aira.EvidenceCarried);
            Assert.Equal(standee, aira.Space);
            Assert.Empty(aira.Wounds);
            Assert.Equal(0, aira.Stamina);
            Assert.Equal(0, aira.Charge);

            Assert.Throws<InvalidOperationException>(() => game.AdoptSpirit("aira", "phantom")); // already a Spirit
            Assert.Equal(new[] { "phantom", "poltergeist" }, game.UnusedSpiritIds());
        }

        [Fact]
        public void Two_investigators_cannot_take_the_same_spirit_card()
        {
            // The Cult needs every Investigator dead, so 2 deaths out of 3 leave the game open.
            var game = NewGame("cult-of-hunlow");
            BecomeSpirit(game, "aira", "poltergeist");
            Kill(game, "lucy-belle");
            Assert.Equal(GameResult.Undecided, game.State.Result);

            Assert.Throws<InvalidOperationException>(() => game.AdoptSpirit("lucy-belle", "poltergeist"));
            game.AdoptSpirit("lucy-belle", "phantom");
            Assert.Equal(new[] { "apparition" }, game.UnusedSpiritIds());
        }

        [Fact]
        public void A_butcher_game_never_has_spirits_because_his_win_fires_on_the_first_death()
        {
            var game = NewGame("butcher");
            Kill(game, "aira");
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.AdversaryWins, game.State.Result);
            Assert.Throws<InvalidOperationException>(() => game.AdoptSpirit("aira", "apparition"));
            Assert.DoesNotContain(game.State.Log, e => e.Type == "spirit" && e.Detail.Contains("may take a Spirit"));
        }

        [Fact]
        public void A_spirit_cannot_gain_wounds_and_is_never_killed_again()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "apparition");
            game.GainWound(spirit, faceUp: true, origin: Game.WoundFromAdversary);
            Assert.Empty(spirit.Wounds);
            Assert.Equal(1, game.State.Adversary.Kills);
            Assert.Equal(GameResult.Undecided, game.State.Result);
        }

        // ---------- The Spirit turn ----------

        [Fact]
        public void Spirit_turns_have_four_mp_plus_a_free_sprint_every_round()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "apparition");

            // The Spirit is still owed a turn, so the round does not roll on without it.
            foreach (string id in new[] { "lucy-belle", "mitchell" })
            {
                game.BeginInvestigatorTurn(id);
                game.EndTurnWithoutFinalAction();
            }
            Assert.Equal(GamePhase.InvestigatorTurns, game.State.Phase);

            game.BeginInvestigatorTurn("aira");
            Assert.Equal(Game.SpiritMp, spirit.MpRemaining);
            game.Sprint();
            int afterSprint = spirit.MpRemaining;
            Assert.True(afterSprint > Game.SpiritMp, "the Sprint die always adds MP");
            Assert.Equal(0, spirit.Stamina); // no Stamina track to pay from
            Assert.Empty(spirit.Wounds);

            // Flat 1 MP per step, whatever the light level.
            string from = spirit.Space;
            string to = Neighbor(game, from);
            game.MoveStep(to);
            Assert.Equal(to, spirit.Space);
            Assert.Equal(afterSprint - 1, spirit.MpRemaining);
            Assert.Throws<InvalidOperationException>(game.Sprint); // still once per turn
            game.EndTurnWithoutFinalAction();
            Assert.Equal(GamePhase.AdversaryTurn, game.State.Phase);

            FinishRound(game);
            Assert.Equal(2, game.State.Round);
            game.BeginInvestigatorTurn("aira");
            Assert.Equal(Game.SpiritMp, spirit.MpRemaining);
            game.Sprint(); // and again next round, still free
            Assert.True(spirit.MpRemaining > Game.SpiritMp);
            Assert.Equal(0, spirit.Stamina);
        }

        [Fact]
        public void Spirits_cannot_rest_charge_place_a_flashlight_or_take_medical_items()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "apparition");
            spirit.Space = game.State.MedicalItemSpaces[0];

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(game.Rest);
            Assert.Throws<InvalidOperationException>(game.ChargeFlashlight);
            Assert.Throws<InvalidOperationException>(() => game.PlaceFlashlight(0.0));
            Assert.Throws<InvalidOperationException>(game.PickUpMedicalItem);
            Assert.Single(game.State.MedicalItemSpaces);
            // No Rest means no Stamina gain at the end of the turn either.
            game.EndTurnWithoutFinalAction();
            Assert.Equal(0, spirit.Stamina);
            Assert.NotEmpty(game.ActionBlockers("aira", Game.ActionRest));
            Assert.Empty(game.ActionBlockers("lucy-belle", Game.ActionRest));
        }

        [Fact]
        public void Spirit_movement_costs_a_flat_one_mp_where_an_investigator_pays_two()
        {
            var game = NewGame();
            Assert.Equal(LightLevel.Dark, game.Graph.EffectiveLight("S-5", game.State.Overlay));

            var lucy = Inv(game, "lucy-belle");
            lucy.Space = "S-1";
            game.BeginInvestigatorTurn("lucy-belle");
            int lucyMp = lucy.MpRemaining; // whatever this round's Event card left her
            game.MoveStep("S-5");
            Assert.Equal(lucyMp - 2, lucy.MpRemaining); // Dark costs an Investigator 2
            game.EndTurnWithoutFinalAction();

            var spirit = BecomeSpirit(game, "aira", "poltergeist");
            spirit.Space = "S-1";
            game.BeginInvestigatorTurn("aira");
            // The full 4 MP: Event cards that trim an Investigator's MP are "something that
            // affects movement", and Spirits are not affected by those either.
            Assert.Equal(Game.SpiritMp, spirit.MpRemaining);
            game.MoveStep("S-5");
            Assert.Equal(Game.SpiritMp - 1, spirit.MpRemaining);
        }

        [Fact]
        public void Spirits_float_through_a_locked_door_that_stops_investigators()
        {
            var game = NewGame();
            Assert.NotNull(game.Graph.Edge("S-20", "S-23"));
            Assert.Equal(SpaceKind.Door, game.Graph.Space("S-23").Kind);
            game.State.Overlay.DoorStates["S-23"] = DoorState.Locked;

            var lucy = Inv(game, "lucy-belle");
            lucy.Space = "S-20";
            game.BeginInvestigatorTurn("lucy-belle");
            Assert.Throws<InvalidOperationException>(() => game.MoveStep("S-23"));
            game.EndTurnWithoutFinalAction();

            var spirit = BecomeSpirit(game, "aira", "phantom");
            spirit.Space = "S-20";
            game.BeginInvestigatorTurn("aira");
            game.MoveStep("S-23");
            Assert.Equal("S-23", spirit.Space);
            Assert.Equal(Game.SpiritMp - 1, spirit.MpRemaining);
        }

        [Fact]
        public void Spirits_are_not_carried_by_water_or_the_carriage_rotation()
        {
            var game = NewGame(scenarioId: "amusement-park");
            var loop = game.Graph.Def.WaterFlowLoop;
            var lucy = Inv(game, "lucy-belle");

            // An Investigator entering the water is swept 2 spaces down the current.
            lucy.Space = loop[0];
            game.BeginInvestigatorTurn("lucy-belle");
            game.MoveStep(loop[1]);
            Assert.Equal(loop[3], lucy.Space);
            game.EndTurnWithoutFinalAction();

            var spirit = BecomeSpirit(game, "aira", "apparition");
            spirit.Space = loop[0];
            game.BeginInvestigatorTurn("aira");
            game.MoveStep(loop[1]);
            Assert.Equal(loop[1], spirit.Space); // stays put: no float
            game.EndTurnWithoutFinalAction();

            // Nor does the forced carriage rotation move them at the start of a turn.
            string carriage = game.Graph.Def.Rides["zipper"].ForcedNext.Keys.First();
            spirit.Space = carriage;
            FinishRound(game);
            game.BeginInvestigatorTurn("aira");
            Assert.Equal(carriage, spirit.Space);
            Assert.NotNull(game.Graph.RideNext(carriage));
        }

        // ---------- Trading ----------

        [Fact]
        public void Spirits_may_give_items_and_evidence_but_never_receive_them()
        {
            var game = NewGame();
            var lucy = Inv(game, "lucy-belle");
            var spirit = BecomeSpirit(game, "aira", "apparition");
            spirit.Space = Neighbor(game, lucy.Space, "285");
            spirit.Items.Add("flare-gun");
            spirit.EvidenceCarried.Add("S");
            lucy.Items.Add("medkit");

            game.BeginInvestigatorTurn("aira");
            game.TradeItem("lucy-belle", "flare-gun");
            game.TradeEvidence("lucy-belle", "S");
            Assert.Contains("flare-gun", lucy.Items);
            Assert.Contains("S", lucy.EvidenceCarried);
            Assert.DoesNotContain("flare-gun", spirit.Items);
            game.EndTurnWithoutFinalAction();

            // The other direction is illegal: a Spirit may never be a Trade target.
            game.BeginInvestigatorTurn("lucy-belle");
            Assert.Throws<InvalidOperationException>(() => game.TradeItem("aira", "medkit"));
            Assert.Throws<InvalidOperationException>(() => game.TradeEvidence("aira", "S"));
            Assert.Contains("medkit", lucy.Items);
            Assert.Contains("S", lucy.EvidenceCarried);
        }

        // ---------- Ability economy ----------

        [Fact]
        public void At_most_two_abilities_per_turn_and_the_count_resets_next_round()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "apparition");

            game.BeginInvestigatorTurn("aira");
            game.UseSpiritAbility("Ghost Orbs", new List<string> { Neighbor(game, spirit.Space) });
            game.UseSpiritAbility("cold-spot"); // the slug works as well as the printed name
            Assert.Equal(2, spirit.SpiritAbilitiesUsedThisTurn);
            Assert.Throws<InvalidOperationException>(() => game.UseSpiritAbility("Cold Spot"));
            // Abilities from another Spirit's card are not on this card.
            Assert.Throws<InvalidOperationException>(() => game.UseSpiritAbility("Whirlwind"));
            game.EndTurnWithoutFinalAction();

            FinishRound(game);
            game.BeginInvestigatorTurn("aira");
            Assert.Equal(0, spirit.SpiritAbilitiesUsedThisTurn);
            game.UseSpiritAbility("Cold Spot");
            Assert.Equal(1, spirit.SpiritAbilitiesUsedThisTurn);
        }

        [Fact]
        public void Major_abilities_burn_the_two_tokens_and_they_never_come_back()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "apparition");
            spirit.Space = "S-1"; // Emergency Lights needs a Zone
            string zone = game.Graph.Space("S-1").Zone!;

            game.BeginInvestigatorTurn("aira");
            game.UseSpiritAbility("Emergency Lights");
            Assert.Contains(zone, game.State.Overlay.DimZones);
            Assert.Equal(1, spirit.SpiritMajorTokens);
            game.UseSpiritAbility("Emergency Lights");
            Assert.Equal(0, spirit.SpiritMajorTokens);
            game.EndTurnWithoutFinalAction();

            // The Dim token is removed at the end of the round, and no token means no Major.
            FinishRound(game);
            Assert.DoesNotContain(zone, game.State.Overlay.DimZones);
            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.UseSpiritAbility("Emergency Lights"));
            Assert.Equal(0, spirit.SpiritMajorTokens);
            game.UseSpiritAbility("Cold Spot"); // Minors are still free
        }

        [Fact]
        public void Only_a_spirit_may_use_spirit_abilities()
        {
            var game = NewGame();
            BecomeSpirit(game, "aira", "apparition");
            game.BeginInvestigatorTurn("lucy-belle");
            Assert.Throws<InvalidOperationException>(() => game.UseSpiritAbility("Ghost Orbs"));
        }

        // ---------- Apparition ----------

        [Fact]
        public void Ghost_orbs_light_an_adjacent_space_until_the_end_of_the_round()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "apparition");
            string target = Neighbor(game, spirit.Space);

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.UseSpiritAbility("Ghost Orbs", new List<string> { "S-1" }));
            game.UseSpiritAbility("Ghost Orbs", new List<string> { target });
            Assert.Equal(LightLevel.Bright, game.Graph.EffectiveLight(target, game.State.Overlay));
            Assert.Single(game.BoardTokenIds("ghost-orbs-"));
            game.EndTurnWithoutFinalAction();

            FinishRound(game);
            Assert.Empty(game.BoardTokenIds("ghost-orbs-"));
            Assert.NotEqual(LightLevel.Bright, game.Graph.EffectiveLight(target, game.State.Overlay));
        }

        [Fact]
        public void Energy_transfer_removes_a_light_token_and_charges_every_investigator()
        {
            var game = NewGame();
            var lucy = Inv(game, "lucy-belle");
            var mitchell = Inv(game, "mitchell");
            var spirit = BecomeSpirit(game, "aira", "apparition");

            lucy.Space = "S-20"; // Light Switch for zone S
            game.BeginInvestigatorTurn("lucy-belle");
            game.ActivateLightSwitch();
            Assert.Contains("S", game.State.Overlay.BrightZones);
            game.EndTurnWithoutFinalAction();

            lucy.Charge = 1;
            mitchell.Charge = 0;
            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(() => game.UseSpiritAbility("Energy Transfer", new List<string> { "K" }));
            game.UseSpiritAbility("Energy Transfer", new List<string> { "S" });
            Assert.DoesNotContain("S", game.State.Overlay.BrightZones);
            Assert.Equal(2, lucy.Charge);
            Assert.Equal(1, mitchell.Charge);
            Assert.Equal(0, spirit.Charge); // Spirits have no Charge track to raise
            Assert.Equal(1, spirit.SpiritMajorTokens);
        }

        [Fact]
        public void Cold_spot_arms_this_round_and_reports_the_missing_adversary_move_hook()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "apparition");
            game.BeginInvestigatorTurn("aira");
            game.UseSpiritAbility("Cold Spot");
            Assert.True(game.HasRoundModifier(Game.SpiritColdSpotPrefix + "aira"));
            Assert.Equal(Game.SpiritMajorTokenStart, spirit.SpiritMajorTokens); // Minor: free
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.StartsWith("cold-spot:"));
            game.EndTurnWithoutFinalAction();
            FinishRound(game);
            Assert.False(game.HasRoundModifier(Game.SpiritColdSpotPrefix + "aira"));
        }

        // ---------- Phantom ----------

        [Fact]
        public void Clairvoyance_reveals_an_adjacent_point_of_interest_token()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "phantom");
            var poi = game.State.PoiTokens[0];
            Assert.False(poi.Revealed);

            game.BeginInvestigatorTurn("aira");
            spirit.Space = Neighbor(game, poi.PoiSpace);
            game.UseSpiritAbility("Clairvoyance");
            Assert.True(poi.Revealed);
            Assert.Equal(Game.SpiritMajorTokenStart, spirit.SpiritMajorTokens);
            Assert.Throws<InvalidOperationException>(() => game.UseSpiritAbility("Clairvoyance")); // nothing left in reach
        }

        [Fact]
        public void Luring_lights_walks_an_adjacent_investigator_up_to_three_spaces()
        {
            var game = NewGame();
            var lucy = Inv(game, "lucy-belle");
            var spirit = BecomeSpirit(game, "aira", "phantom");
            spirit.Space = Neighbor(game, lucy.Space, "285");
            string near = game.Graph.DistancesFrom(lucy.Space, 3, game.State.Overlay)
                .Where(kv => kv.Value == 3 && game.State.Investigators.All(i => i.Space != kv.Key))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            string far = game.Graph.DistancesFrom(lucy.Space, 5, game.State.Overlay)
                .First(kv => kv.Value == 5).Key;

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(
                () => game.UseSpiritAbility("Luring Lights", new List<string> { "lucy-belle", far }));
            Assert.Throws<InvalidOperationException>(
                () => game.UseSpiritAbility("Luring Lights", new List<string> { "mitchell", near })); // not adjacent
            game.UseSpiritAbility("Luring Lights", new List<string> { "lucy-belle", near });
            Assert.Equal(near, lucy.Space);
            Assert.Equal(1, spirit.SpiritMajorTokens);
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.StartsWith("luring-lights:"));
        }

        [Fact]
        public void Ectoplasm_and_true_darkness_place_their_state_and_report_what_is_missing()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "phantom");
            spirit.Space = "S-1";
            string first = Neighbor(game, "S-1");
            string second = Neighbor(game, "S-1", first);

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(
                () => game.UseSpiritAbility("Ectoplasm", new List<string> { first, first }));
            game.UseSpiritAbility("Ectoplasm", new List<string> { first, second });
            Assert.Equal(new[] { first, second }.OrderBy(s => s), game.BoardTokenSpaces("ectoplasm-").OrderBy(s => s));

            game.UseSpiritAbility("True Darkness");
            Assert.Equal(1, game.RoundModifier(Game.SpiritZoneFootprintSurchargePrefix + "S"));
            Assert.Equal(1, spirit.SpiritMajorTokens);
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.StartsWith("ectoplasm:"));
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.StartsWith("true-darkness:"));
            game.EndTurnWithoutFinalAction();

            FinishRound(game);
            Assert.Empty(game.BoardTokenIds("ectoplasm-"));
            Assert.Equal(0, game.RoundModifier(Game.SpiritZoneFootprintSurchargePrefix + "S"));
        }

        // ---------- Poltergeist ----------

        [Fact]
        public void Push_slides_an_adjacent_token_up_to_three_spaces()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "poltergeist");
            string medical = game.State.MedicalItemSpaces[0];
            spirit.Space = Neighbor(game, medical);
            string destination = game.Graph.DistancesFrom(medical, 3, game.State.Overlay)
                .Where(kv => kv.Value == 3).OrderBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
            string tooFar = game.Graph.DistancesFrom(medical, 5, game.State.Overlay).First(kv => kv.Value == 5).Key;

            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(
                () => game.UseSpiritAbility("Push", new List<string> { "medical:" + medical, tooFar }));
            Assert.Throws<InvalidOperationException>(
                () => game.UseSpiritAbility("Push", new List<string> { "medical:S-1", destination })); // no token there
            game.UseSpiritAbility("Push", new List<string> { "medical:" + medical, destination });
            Assert.Equal(new[] { destination }, game.State.MedicalItemSpaces);
            Assert.Equal(1, spirit.SpiritMajorTokens);
        }

        [Fact]
        public void Spectral_hand_moves_and_reorients_an_investigators_flashlight()
        {
            var game = NewGame();
            var lucy = Inv(game, "lucy-belle");
            var spirit = BecomeSpirit(game, "aira", "poltergeist");

            game.BeginInvestigatorTurn("lucy-belle");
            game.PlaceFlashlight(0.0); // ends lucy's turn
            var placement = game.State.Flashlights.Single();
            Assert.Equal(lucy.Space, placement.Space);
            var wasLit = placement.BrightSpaces.ToList();

            string destination = Neighbor(game, lucy.Space, "285");
            game.BeginInvestigatorTurn("aira");
            Assert.Throws<InvalidOperationException>(
                () => game.UseSpiritAbility("Spectral Hand", new List<string> { "mitchell", destination, "0" }));
            game.UseSpiritAbility("Spectral Hand", new List<string> { "lucy-belle", destination, "1.5708" });
            Assert.Equal(destination, placement.Space);
            Assert.Equal(1.5708, placement.AngleRadians, 4);
            Assert.NotEqual(wasLit, placement.BrightSpaces);
            Assert.Equal(placement.BrightSpaces.OrderBy(s => s), game.State.Overlay.BrightSpaces.OrderBy(s => s));
            Assert.Equal(1, spirit.SpiritMajorTokens);
        }

        [Fact]
        public void Whirlwind_and_mysterious_passage_arm_this_rounds_flags()
        {
            var game = NewGame();
            var spirit = BecomeSpirit(game, "aira", "poltergeist");
            game.BeginInvestigatorTurn("aira");
            game.UseSpiritAbility("Whirlwind");
            game.UseSpiritAbility("Mysterious Passage");
            Assert.True(game.HasRoundModifier(Game.SpiritWhirlwindPrefix + "aira"));
            Assert.True(game.HasRoundModifier(Game.SpiritHazardBypassPrefix + "aira"));
            Assert.Equal(Game.SpiritMajorTokenStart, spirit.SpiritMajorTokens); // both Minors
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.StartsWith("whirlwind:"));
            Assert.Contains(game.State.Log, e => e.Type == "todo" && e.Detail.StartsWith("mysterious-passage:"));
        }

        // ---------- Win conditions ----------

        [Fact]
        public void A_spirit_on_the_board_does_not_hold_up_the_investigators_win()
        {
            var game = NewGame();
            BecomeSpirit(game, "aira", "apparition");
            game.SelectEscapeCard("north-gate");
            game.State.Objective.EscapeOpen = true;
            string gate = game.State.Objective.Tokens["locked-escape"];

            foreach (string id in new[] { "lucy-belle", "mitchell" })
            {
                game.BeginInvestigatorTurn(id);
                Inv(game, id).Space = gate;
                game.EscapeThroughGate();
                if (!Inv(game, id).Escaped)
                {
                    throw new InvalidOperationException($"{id} did not escape");
                }
                if (game.State.Phase != GamePhase.GameOver)
                {
                    game.EndTurnWithoutFinalAction();
                }
            }
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
            Assert.NotNull(Inv(game, "aira").SpiritId);
        }

        // ---------- Data ----------

        [Fact]
        public void The_spirit_roster_matches_game_data()
        {
            string path = Path.Combine(TestData.GameDataDir(), "cards", "spirits.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var spirits = doc.RootElement.GetProperty("spirits").EnumerateArray().ToList();
            Assert.Equal(Game.SpiritIds.Count, spirits.Count);
            for (int i = 0; i < spirits.Count; i++)
            {
                string id = spirits[i].GetProperty("id").GetString()!;
                Assert.Equal(Game.SpiritIds[i], id);
                Assert.Equal(spirits[i].GetProperty("name").GetString(), Game.SpiritName(id));
                var abilities = spirits[i].GetProperty("abilities").EnumerateArray().ToList();
                Assert.Equal(4, abilities.Count);
                Assert.Equal(2, abilities.Count(a => a.GetProperty("type").GetString() == "major"));
                Assert.Equal(2, abilities.Count(a => a.GetProperty("type").GetString() == "minor"));
                var printed = abilities
                    .Select(a => a.GetProperty("name").GetString()!.ToLowerInvariant().Replace(' ', '-'));
                Assert.Equal(printed.OrderBy(s => s), Game.SpiritAbilityIds(id).OrderBy(s => s));
            }
        }
    }
}
