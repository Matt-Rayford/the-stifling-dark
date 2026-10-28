using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>Kya Prosser's and Winston Pitts' Abilities (Game.PromoAbilities.cs) and the
    /// light-model additions they ride on: per-space Dim and Dim/Dark inversion.</summary>
    public class PromoAbilityTests
    {
        private static Game NewGame(ulong seed = 7) =>
            TestData.NewSawmillGame(new[] { "kya", "winston", "aira" }, seed);

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        private static void EndRound(Game game)
        {
            foreach (var inv in game.State.Investigators.Where(i => !i.TurnTakenThisRound).ToList())
            {
                if (game.State.ActiveInvestigator == null)
                {
                    game.BeginInvestigatorTurn(inv.DefId);
                }
                game.EndTurnWithoutFinalAction();
            }
            game.AdversaryEndTurn();
        }

        [Fact]
        public void Promo_investigators_are_seated()
        {
            var game = NewGame();
            Assert.Equal(3, game.State.Investigators.Count);
            Assert.Equal(1, Inv(game, "kya").MajorAbilityTokens);
        }

        [Fact]
        public void Kya_swaps_a_charge_for_stamina_and_forfeits_the_turn_recovery()
        {
            var game = NewGame();
            var kya = Inv(game, "kya");
            game.BeginInvestigatorTurn("kya");
            game.Sprint(); // 5 -> 4 Stamina, so there is room to gain one back
            int stamina = kya.Stamina, charge = kya.Charge;

            game.UseMinorAbility(args: new List<string> { Game.KyaGainStamina });

            Assert.Equal(stamina + 1, kya.Stamina);
            Assert.Equal(charge - 1, kya.Charge);
            var again = Assert.Throws<InvalidOperationException>(
                () => game.UseMinorAbility(args: new List<string> { Game.KyaGainStamina }));
            Assert.Contains("already swapped", again.Message);

            game.EndTurnWithoutFinalAction();
            Assert.Equal(charge - 1, kya.Charge); // no automatic Charge this turn
        }

        [Fact]
        public void Kya_swaps_stamina_for_a_charge_and_does_not_rest()
        {
            var game = NewGame();
            var kya = Inv(game, "kya");
            game.BeginInvestigatorTurn("kya");
            game.Sprint();
            game.UseMinorAbility(args: new List<string> { Game.KyaGainStamina });
            game.EndTurnWithoutFinalAction();
            EndRound(game);

            game.BeginInvestigatorTurn("kya");
            int stamina = kya.Stamina, charge = kya.Charge;
            game.UseMinorAbility(args: new List<string> { Game.KyaGainCharge });
            Assert.Equal(stamina - 1, kya.Stamina);
            Assert.Equal(charge + 1, kya.Charge);

            game.EndTurnWithoutFinalAction();
            Assert.Equal(stamina - 1, kya.Stamina); // no Rest either
            Assert.Equal(charge + 1, kya.Charge);
        }

        [Fact]
        public void Kya_refuses_a_swap_with_nothing_to_spend_or_room_to_gain()
        {
            var game = NewGame();
            game.BeginInvestigatorTurn("kya");
            // Both tracks start full, so neither direction has room to gain.
            Assert.Throws<InvalidOperationException>(
                () => game.UseMinorAbility(args: new List<string> { Game.KyaGainCharge }));
            Assert.Throws<InvalidOperationException>(
                () => game.UseMinorAbility(args: new List<string> { Game.KyaGainStamina }));
            Assert.Throws<InvalidOperationException>(() => game.UseMinorAbility());
        }

        [Fact]
        public void Kya_major_inverts_dim_and_dark_for_the_round()
        {
            var game = NewGame();
            var overlay = game.State.Overlay;
            string dark = game.Graph.Def.Spaces.First(s => s.PrintedLight == LightLevel.Dark && s.Zone != null).Id;
            string dim = game.Graph.Def.Spaces.First(s => s.PrintedLight == LightLevel.Dim).Id;
            overlay.BrightSpaces.Add("285");

            game.BeginInvestigatorTurn("kya");
            game.UseMajorAbility();

            Assert.Equal(LightLevel.Dim, game.Graph.EffectiveLight(dark, overlay));
            Assert.Equal(LightLevel.Dark, game.Graph.EffectiveLight(dim, overlay));
            Assert.Equal(LightLevel.Bright, game.Graph.EffectiveLight("285", overlay));
            Assert.Equal(0, Inv(game, "kya").MajorAbilityTokens);

            game.EndTurnWithoutFinalAction();
            EndRound(game);
            Assert.False(overlay.DimDarkInverted);
            Assert.Equal(LightLevel.Dark, game.Graph.EffectiveLight(dark, overlay));
            Assert.Equal(LightLevel.Dim, game.Graph.EffectiveLight(dim, overlay));
        }

        [Fact]
        public void Winston_night_vision_places_the_centre_lines_for_no_charge()
        {
            var game = NewGame();
            var winston = Inv(game, "winston");
            game.BeginInvestigatorTurn("winston");
            int charge = winston.Charge;
            var narrow = game.PreviewFlashlight("winston", 0, Game.CenterLineSightLines);
            var full = game.PreviewFlashlight("winston", 0);
            Assert.True(narrow.Count < full.Count, "the Sawmill start should see more with the full template");

            game.UseMinorAbility(args: new List<string> { "0" });

            var placement = Assert.Single(game.State.Flashlights, f => f.InvestigatorId == "winston");
            Assert.True(placement.CenterLineOnly);
            Assert.True(placement.BrightSpaces.All(narrow.Contains));
            Assert.Equal(charge, winston.Charge); // nothing spent, and no automatic Charge after a placement
            Assert.Equal(FinalActionKind.PlaceFlashlight, winston.FinalAction);
            Assert.Null(game.State.ActiveInvestigator); // the placement ended his turn
        }

        [Fact]
        public void Winston_night_vision_needs_at_least_one_charge()
        {
            var game = NewGame();
            var winston = Inv(game, "winston");
            winston.Charge = 0;
            game.BeginInvestigatorTurn("winston");
            var refused = Assert.Throws<InvalidOperationException>(
                () => game.UseMinorAbility(args: new List<string> { "0" }));
            Assert.Contains("0 Charge", refused.Message);
            Assert.Empty(game.State.Flashlights);
            Assert.Equal(FinalActionKind.None, winston.FinalAction);
        }

        [Fact]
        public void Winston_campfire_lights_dims_heals_and_burns_out_after_the_next_round()
        {
            var game = NewGame();
            var overlay = game.State.Overlay;
            var winston = Inv(game, "winston");
            game.BeginInvestigatorTurn("winston");
            string here = winston.Space;
            string adjacent = game.Graph.DistancesFrom(here, 1, overlay).Keys.First(s => s != here);
            string farAway = game.Graph.Def.Spaces.First(s =>
                !game.Graph.DistancesFrom(here, 2, overlay).ContainsKey(s.Id)).Id;
            Assert.Throws<InvalidOperationException>(
                () => game.UseMajorAbility(args: new List<string> { farAway }));
            Assert.Throws<InvalidOperationException>(
                () => game.UseMajorAbility(args: new List<string> { here }));

            game.Sprint(); // 5 -> 4 so the Campfire's Stamina is visible
            game.UseMajorAbility(args: new List<string> { adjacent });

            Assert.Equal(adjacent, game.State.BoardTokens[Game.CampfireTokenPrefix + "winston"]);
            Assert.Equal(LightLevel.Bright, game.Graph.EffectiveLight(adjacent, overlay));
            foreach (string near in game.Graph.DistancesFrom(adjacent, Game.CampfireRadius, overlay).Keys)
            {
                Assert.NotEqual(LightLevel.Dark, game.Graph.EffectiveLight(near, overlay));
            }
            int stamina = winston.Stamina;
            game.EndTurnWithoutFinalAction(); // Sprinted, so no Rest: the +1 is the Campfire's
            Assert.Equal(stamina + 1, winston.Stamina);

            EndRound(game); // end of the round it was placed in: still burning
            Assert.True(game.State.BoardTokens.ContainsKey(Game.CampfireTokenPrefix + "winston"));
            Assert.Equal(LightLevel.Bright, game.Graph.EffectiveLight(adjacent, overlay));

            EndRound(game); // end of the next round: gone, and its light with it
            Assert.False(game.State.BoardTokens.ContainsKey(Game.CampfireTokenPrefix + "winston"));
            Assert.NotEqual(LightLevel.Bright, game.Graph.EffectiveLight(adjacent, overlay));
            Assert.Empty(overlay.DimSpaces);
        }
    }
}
