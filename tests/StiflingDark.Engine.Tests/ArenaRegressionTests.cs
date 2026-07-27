using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// Regression coverage for engine bugs found by self-play in tools/BotArena: a decided
    /// game resuming after the fact, a stuck "every survivor escaped" win, adversary actions
    /// marked used before their validation finished, the Butcher acting after Disappearing,
    /// an unenforced Ability ban, and Immolate wounding a target before its 2nd was validated.
    /// Also covers 2 designer rulings: deaths no longer downgrade an escape to a Draw — dead
    /// Investigators play on as Spirits, so every living Investigator escaping while the
    /// Adversary's kill condition is unmet is an outright Investigators win, regardless of
    /// prior deaths (the round-limit timeout still counts on-board survivors as killed and can
    /// still produce a Draw); and Cult figures (Cultists and Mor'gonnod) can never stack.
    ///
    /// 3 more turn-lock bugs found by a later self-play run: Fear's "unable to use it" clause
    /// was never checked (only whether a Major Ability token was held), hard-locking an
    /// Investigator whose Major could never legally resolve (Mada below 5 Stamina, etc.);
    /// Dylan's free Escape Artist return skipped clearing Fear's compulsion, hard-locking a
    /// Fear'd Dylan while his token was out; and Gear Jam's Stamina cost was paid in the
    /// end-of-turn hooks, after Breathless could already have drained the same Stamina,
    /// throwing out of EndTurn. Plus a designer ruling on Mitchell's Sweep: the 2nd Flashlight
    /// position replaces the 1st (its Reveals stand, its Bright coverage does not).
    /// </summary>
    public class ArenaRegressionTests
    {
        private static readonly Dictionary<string, string> SawmillEvidenceSpaces = new Dictionary<string, string>
        {
            ["L"] = "L-1", ["K"] = "K-1", ["G"] = "G-1", ["S"] = "S-1", ["O"] = "O-2",
        };

        private static Game NewSawmillGame(string adversaryId, ulong seed = 1234)
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = adversaryId,
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285",
                    ["lucy-belle"] = "286",
                    ["mitchell"] = "305",
                    ["vincent"] = "307",
                },
                MedicalItemSpaces = new List<string> { "24", "208" },
            });
            FinishAdversarySetup(game);
            return game;
        }

        private static Game NewButcherGame(string attackCardId, ulong seed = 1234)
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
            game.SetupAdversaryCards(attackCardId, new List<string> { "disturbed-presence", "escalating-terror" });
            FinishAdversarySetup(game);
            return game;
        }

        /// <summary>A 2-Investigator Insatiable Horror game (KillsToWin is 2 regardless of count).</summary>
        private static Game New2InvestigatorHorrorGame(string attackCardId, List<string> abilityCardIds, ulong seed = 1234)
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
                },
                MedicalItemSpaces = new List<string>(), // 2 investigators start with 0 Medical Items
            });
            game.SetupAdversaryCards(attackCardId, abilityCardIds);
            FinishAdversarySetup(game);
            return game;
        }

        /// <summary>A 3-Investigator Insatiable Horror game (KillsToWin is 2 regardless of count).</summary>
        private static Game New3InvestigatorHorrorGame(string attackCardId, List<string> abilityCardIds, ulong seed = 1234)
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
                },
                MedicalItemSpaces = new List<string> { "24" }, // 3 investigators start with 1 Medical Item
            });
            game.SetupAdversaryCards(attackCardId, abilityCardIds);
            FinishAdversarySetup(game);
            return game;
        }

        /// <summary>A 4-Investigator Cult of Hunlow game, Cultists in their default start spaces (c1 S-21, c2 S-24, c3 S-27, c4 S-30).</summary>
        private static Game NewCultGame(ulong seed = 5)
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = "cult-of-hunlow",
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285",
                    ["lucy-belle"] = "286",
                    ["mitchell"] = "305",
                    ["vincent"] = "307",
                },
                MedicalItemSpaces = new List<string> { "24", "208" },
            });
            foreach (var (zone, space) in SawmillEvidenceSpaces)
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
            game.SetupCultists(new List<string> { "S-21", "S-24", "S-27", "S-30" }, "S-18");
            game.SetupAdversaryCards("ravage", new List<string> { "razor-like-talons", "dried-tongue", "severed-ear" });
            game.FinishAdversarySetup();
            return game;
        }

        private static AdversaryFigure Cultist(Game game, string id) =>
            game.State.Adversary.Figures.First(f => f.Id == id);

        private static void FinishAdversarySetup(Game game)
        {
            foreach (var (zone, space) in SawmillEvidenceSpaces)
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

        /// <summary>Finish the round (every remaining Investigator turn, then the Adversary's).</summary>
        private static void FinishRound(Game game)
        {
            FinishInvestigatorTurns(game);
            game.AdversaryEndTurn();
        }

        // ---------- Helpers for the Fear / Gear Jam / Sweep regressions below: these need an
        // arbitrary Investigator roster (Mada, Dylan), not the fixed aira/lucy-belle/mitchell/
        // vincent one NewSawmillGame hands out. ----------

        private static readonly string[] AbilityGameStartSpaces = { "285", "286", "305", "307" };

        private static Game NewAbilityGame(string[] invIds, ulong seed = 1234, string adversary = "butcher")
        {
            var starts = new Dictionary<string, string>();
            for (int i = 0; i < invIds.Length; i++)
            {
                starts[invIds[i]] = AbilityGameStartSpaces[i];
            }
            int medical = TestData.Db.Config.ByInvestigatorCount[invIds.Length].MedicalItemsOnBoard;
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = seed,
                AdversaryId = adversary,
                InvestigatorStartSpaces = starts,
                MedicalItemSpaces = new List<string> { "24", "208" }.Take(medical).ToList(),
            });
            FinishAdversarySetup(game);
            return game;
        }

        private static WoundInstance GiveFaceUpWound(Game game, InvestigatorState inv, string cardId)
        {
            var wound = new WoundInstance { CardId = cardId, FaceUp = false };
            inv.Wounds.Add(wound);
            game.FlipWoundFaceUp(inv, wound);
            return wound;
        }

        // ---------- Bug 1: a decided game must stay decided ----------

        [Fact]
        public void Death_from_an_end_of_turn_wound_ends_the_game_and_it_stays_ended()
        {
            var game = NewSawmillGame("butcher"); // KillsToWin = 1
            var aira = Inv(game, "aira");
            foreach (string id in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(id);
                game.EndTurnWithoutFinalAction();
            }
            // 3 Wounds now; Bleeding's end-of-turn Wound will be the 4th (lethal) one.
            for (int i = 0; i < 3; i++)
            {
                game.GainWound(aira, faceUp: false);
            }
            game.GrantConditionWithSubstitution(aira, "bleeding");

            game.BeginInvestigatorTurn("aira");
            game.EndTurnWithoutFinalAction(); // aira dies mid-EndTurn, from Bleeding's own hook

            Assert.True(aira.Dead);
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.AdversaryWins, game.State.Result);

            // Pre-fix, EndTurn unconditionally overwrote Phase to AdversaryTurn right after this,
            // and AdversaryEndTurn -> EndRound -> BeginRound would then resume a decided game.
            game.AdversaryEndTurn();
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.AdversaryWins, game.State.Result);
            Assert.Equal(1, game.State.Round);
        }

        // ---------- Bug 2: a death that leaves only escapees must decide the game, not deadlock ----------

        [Fact]
        public void Last_living_investigator_dying_after_a_teammate_escaped_ends_the_game_in_an_investigators_win()
        {
            // Revised designer ruling: deaths no longer downgrade an escape to a Draw. The bug
            // this guards against (the game deadlocking, Phase stuck at
            // InvestigatorTurns/AdversaryTurn forever) is unchanged.
            var game = New2InvestigatorHorrorGame("bufotoxin", new List<string> { "devour" });
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");
            aira.Escaped = true;
            for (int i = 0; i < 4; i++)
            {
                game.GainWound(lucy, faceUp: false);
            }

            Assert.True(lucy.Dead);
            // Below the Horror's KillsToWin of 2: the Adversary has not won this outright.
            Assert.Equal(1, game.State.Adversary.Kills);
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
        }

        // ---------- Designer ruling: full-team escape with no deaths is still an outright win ----------

        [Fact]
        public void All_investigators_escaping_with_no_deaths_still_wins_outright()
        {
            var game = NewSawmillGame("butcher");
            game.SelectEscapeCard("north-gate");
            game.State.Objective.EscapeOpen = true;
            string gate = game.State.Objective.Tokens["locked-escape"];

            foreach (string id in new[] { "aira", "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(id);
                Inv(game, id).Space = gate;
                game.EscapeThroughGate();
                if (game.State.Phase != GamePhase.GameOver)
                {
                    game.EndTurnWithoutFinalAction();
                }
            }

            Assert.True(game.State.Investigators.All(i => i.Escaped));
            Assert.DoesNotContain(game.State.Investigators, i => i.Dead);
            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
        }

        // ---------- Designer ruling: 1 death then the rest escaping is an Investigators win ----------

        [Fact]
        public void One_death_then_the_rest_escaping_ends_the_game_in_an_investigators_win()
        {
            var game = NewSawmillGame("insatiable-horror"); // KillsToWin = 2
            var aira = Inv(game, "aira");
            for (int i = 0; i < 4; i++)
            {
                game.GainWound(aira, faceUp: false);
            }
            Assert.True(aira.Dead);
            // Below the Horror's KillsToWin of 2: the death alone did not decide the game.
            Assert.Equal(1, game.State.Adversary.Kills);
            Assert.Equal(GamePhase.InvestigatorTurns, game.State.Phase);

            game.SelectEscapeCard("north-gate");
            game.State.Objective.EscapeOpen = true;
            string gate = game.State.Objective.Tokens["locked-escape"];

            foreach (string id in new[] { "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(id);
                Inv(game, id).Space = gate;
                game.EscapeThroughGate();
                if (game.State.Phase != GamePhase.GameOver)
                {
                    game.EndTurnWithoutFinalAction();
                }
            }

            Assert.Equal(GamePhase.GameOver, game.State.Phase);
            Assert.Equal(GameResult.InvestigatorsWin, game.State.Result);
        }

        // ---------- Designer ruling: round-limit timeout counts unescaped survivors as killed ----------

        [Fact]
        public void Timeout_with_a_death_and_an_escape_still_reaching_kills_to_win_is_an_adversary_win()
        {
            var game = New3InvestigatorHorrorGame("bufotoxin", new List<string> { "devour" });
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");
            aira.Dead = true;
            game.State.Adversary.Kills = 1; // KillsToWin is 2 for the Horror
            lucy.Escaped = true;
            // mitchell is left on the board, uncounted until the round-limit tally.

            while (game.State.Phase != GamePhase.GameOver)
            {
                foreach (var inv in game.State.Investigators.Where(i => !i.Dead && !i.Escaped))
                {
                    game.BeginInvestigatorTurn(inv.DefId);
                    game.EndTurnWithoutFinalAction();
                }
                game.AdversaryEndTurn();
            }

            Assert.Equal(17, game.State.Round);
            // killedAtTimeout = 1 (mitchell, still on the board); Kills(1) + 1 = 2 >= KillsToWin(2).
            Assert.Equal(GameResult.AdversaryWins, game.State.Result);
        }

        [Fact]
        public void Timeout_is_an_adversary_win_even_with_escapes_below_kills_to_win()
        {
            var game = New3InvestigatorHorrorGame("bufotoxin", new List<string> { "devour" });
            var lucy = Inv(game, "lucy-belle");
            var mitchell = Inv(game, "mitchell");
            lucy.Escaped = true;
            mitchell.Escaped = true;
            // aira is left on the board with the Horror's Kills still at 0.

            while (game.State.Phase != GamePhase.GameOver)
            {
                foreach (var inv in game.State.Investigators.Where(i => !i.Dead && !i.Escaped))
                {
                    game.BeginInvestigatorTurn(inv.DefId);
                    game.EndTurnWithoutFinalAction();
                }
                game.AdversaryEndTurn();
            }

            Assert.Equal(17, game.State.Round);
            // killedAtTimeout = 1 (aira); Kills(0) + 1 = 1 < KillsToWin(2), but some Investigators
            // did escape, so this is a Draw rather than a default Adversary win.
            Assert.Equal(GameResult.AdversaryWins, game.State.Result);
        }

        // ---------- Designer ruling: Cult figures can never stack ----------

        [Fact]
        public void A_cultist_may_move_through_a_stacked_space_but_not_retire_there()
        {
            var game = NewCultGame();
            FinishInvestigatorTurns(game);

            // Moving through: standing on another cult figure's space mid-activation is fine,
            // as long as the Cultist moves off again before a different Cultist takes over.
            Cultist(game, "c2").Space = "S-17"; // simulate c2 already sitting there
            game.CultistMoveStep("c1", "S-17"); // c1 steps onto c2's space: no throw
            game.CultistMoveStep("c1", "S-18"); // ...and off again before retiring: still no throw

            // Retiring stacked: put c2 on c1's now-parked space (S-18) and try to switch away
            // from c1 to c2 — the driver must move one of them off first.
            Cultist(game, "c2").Space = "S-18";
            var error = Assert.Throws<InvalidOperationException>(() => game.CultistMoveStep("c2", "S-19"));
            Assert.Contains("stacked", error.Message);
        }

        // ---------- Bug 3: illegal actions must leave ActionsUsed untouched ----------

        [Fact]
        public void A_refused_disappear_does_not_block_a_later_legal_disappear()
        {
            var game = NewButcherGame("eviscerate");
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;
            Assert.False(adv.Revealed);

            // Refused: The Butcher is already Hidden, so there is nothing to Disappear from.
            Assert.Throws<InvalidOperationException>(() => game.AdversaryDisappear());
            Assert.DoesNotContain("disappear", adv.ActionsUsed);

            // Now make it legal and confirm the refused attempt did not burn the action.
            adv.Revealed = true;
            game.AdversaryDisappear();
            Assert.False(adv.Revealed);
            Assert.Contains("disappear", adv.ActionsUsed);
        }

        [Fact]
        public void A_refused_break_door_does_not_block_a_later_legal_break_door()
        {
            var game = NewButcherGame("eviscerate");
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;

            // Refused: "S-25" (the Adversary's own space) is not a door in reach.
            Assert.Throws<InvalidOperationException>(() => game.AdversaryBreakDoor("S-25"));
            Assert.DoesNotContain("breakDoor", adv.ActionsUsed);

            // Now make it legal and confirm the refused attempt did not burn the action.
            adv.Space = "S-20"; // adjacent to the S-23 door
            game.AdversaryBreakDoor("S-23");
            Assert.Equal(DoorState.Damaged, game.State.Overlay.DoorState("S-23"));
            Assert.Contains("breakDoor", adv.ActionsUsed);
        }

        // ---------- Bug 4: the Butcher may not Stalk or Attack after Disappearing ----------

        [Fact]
        public void Butcher_may_not_stalk_or_attack_after_disappearing()
        {
            var game = NewButcherGame("eviscerate");
            FinishInvestigatorTurns(game);
            var adv = game.State.Adversary;

            // Start the turn Hidden (a Move that neither reveals nor uses up Stalk/Attack),
            // then simulate a mid-turn reveal (e.g. caught in a Flashlight) before Disappearing.
            string firstStep = game.Graph.AdjacentForAdversaryAbilities(adv.Space, game.State.Overlay).First();
            game.AdversaryMoveStep(firstStep);
            adv.Revealed = true;
            adv.Counters["stalk"] = 3;
            game.AdversaryDisappear();
            Assert.False(adv.Revealed);

            var aira = Inv(game, "aira");
            aira.Space = game.Graph.AdjacentForAdversaryAbilities(adv.Space, game.State.Overlay).First();

            var stalkError = Assert.Throws<InvalidOperationException>(
                () => game.ButcherStalk(new List<string> { "aira" }));
            Assert.Contains("Disappear", stalkError.Message);

            var attackError = Assert.Throws<InvalidOperationException>(
                () => game.PlayAdversaryCard("eviscerate", new List<string> { "aira" }));
            Assert.Contains("Disappear", attackError.Message);
        }

        // ---------- Bug 5: SetupAdversaryCards must honour adversaries.json's Ability bans ----------

        [Fact]
        public void SetupAdversaryCards_rejects_a_banned_ability_for_the_2_investigator_horror()
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = 99,
                AdversaryId = "insatiable-horror",
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285",
                    ["lucy-belle"] = "286",
                },
                MedicalItemSpaces = new List<string>(),
            });

            // adversaries.json bans Projectile Adhesive (and Occluded Lights) for a 2-Investigator Horror.
            Assert.Throws<InvalidOperationException>(
                () => game.SetupAdversaryCards("bufotoxin", new List<string> { "projectile-adhesive" }));

            // A non-banned loadout is still accepted.
            game.SetupAdversaryCards("bufotoxin", new List<string> { "devour" });
            Assert.Equal("bufotoxin", game.State.Adversary.AttackCard);
            Assert.Contains("devour", game.State.Adversary.ActiveAbilities);
        }

        // ---------- Bonus: Immolate must validate both targets before wounding either ----------

        [Fact]
        public void Immolate_validates_both_targets_before_wounding_either()
        {
            var game = Game.NewGame(TestData.Db, new GameSetup
            {
                ScenarioId = "sawmill",
                Seed = 5,
                AdversaryId = "cult-of-hunlow",
                InvestigatorStartSpaces = new Dictionary<string, string>
                {
                    ["aira"] = "285",
                    ["lucy-belle"] = "286",
                    ["mitchell"] = "305",
                    ["vincent"] = "307",
                },
                MedicalItemSpaces = new List<string> { "24", "208" },
            });
            foreach (var (zone, space) in SawmillEvidenceSpaces)
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
            game.SetupCultists(new List<string> { "S-21", "S-24", "S-27", "S-30" }, "S-18");
            game.SetupAdversaryCards("immolate", new List<string> { "razor-like-talons", "dried-tongue", "severed-ear" });
            game.FinishAdversarySetup();

            var adv = game.State.Adversary;
            var aira = Inv(game, "aira");
            var lucy = Inv(game, "lucy-belle");
            foreach (var id in new[] { "aira", "lucy-belle", "mitchell", "vincent" })
            {
                game.BeginInvestigatorTurn(id);
                game.EndTurnWithoutFinalAction();
            }
            adv.Counters["blood"] = 5;
            game.TheFinalSacrifice(); // Mor'gonnod becomes Corporeal; the Attack card is now usable
            foreach (var inv in game.State.Investigators.Where(i => !i.Dead && !i.Escaped))
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }

            var adjacent = game.Graph.AdjacentForAdversaryAbilities(adv.Space, game.State.Overlay).ToHashSet();
            aira.Space = adjacent.First(); // legal target
            lucy.Space = game.Graph.Def.Spaces.Select(s => s.Id)
                .First(id => id != adv.Space && !adjacent.Contains(id)); // illegal (not adjacent) target

            Assert.Throws<InvalidOperationException>(
                () => game.PlayAdversaryCard("immolate", new List<string> { "aira", "lucy-belle" }));

            // Pre-fix, aira (the 1st, legal target) was wounded before lucy-belle (the 2nd,
            // illegal one) was found invalid.
            Assert.Empty(aira.Wounds);
            Assert.Empty(lucy.Wounds);
        }

        // ---------- Bug 6: Fear must release when the Major Ability can never resolve ----------

        [Fact]
        public void Fear_releases_for_mada_below_5_stamina_instead_of_locking_the_turn()
        {
            var game = NewAbilityGame(new[] { "mada", "mitchell" });
            var mada = Inv(game, "mada");

            game.BeginInvestigatorTurn("mada");
            GiveFaceUpWound(game, mada, "fear");
            game.EndTurnWithoutFinalAction(); // this turn is unaffected ("your next turn")
            FinishRound(game);

            mada.Stamina = 3; // below MadaMajorStaminaCost (5): his Major can never resolve
            game.BeginInvestigatorTurn("mada");

            // Pre-fix: ForcedMajorAbilityPending only checked MajorAbilityTokens >= 1, so this
            // threw forever (an 8/1000-game hard lock) instead of releasing the compulsion.
            game.EndTurnWithoutFinalAction();

            Assert.Null(game.State.ActiveInvestigator);
            Assert.Equal(1, mada.MajorAbilityTokens); // never spent
            Assert.Contains(game.State.Log, e => e.Type == "wound" && e.Detail.Contains("released"));
        }

        [Fact]
        public void Fear_is_still_enforced_when_the_major_ability_is_resolvable()
        {
            var game = NewAbilityGame(new[] { "mada", "mitchell" });
            var mada = Inv(game, "mada");

            game.BeginInvestigatorTurn("mada");
            GiveFaceUpWound(game, mada, "fear");
            game.EndTurnWithoutFinalAction();
            FinishRound(game);

            mada.Stamina = 5; // exactly MadaMajorStaminaCost: his Major *can* resolve
            game.BeginInvestigatorTurn("mada");
            Assert.NotEmpty(game.ActionBlockers("mada", Game.ActionCharge));
            Assert.Throws<InvalidOperationException>(() => game.EndTurnWithoutFinalAction());
            Assert.Throws<InvalidOperationException>(() => game.PlaceFlashlight(0.0));

            game.UseMajorAbility(); // MadaTripleSprint: spends the 5 Stamina, satisfies Fear
            game.EndTurnWithoutFinalAction();

            Assert.Null(game.State.ActiveInvestigator);
            Assert.Equal(0, mada.MajorAbilityTokens);
        }

        // ---------- Bug 7: Dylan's free Escape Artist return must satisfy Fear ----------

        [Fact]
        public void Fear_is_satisfied_by_dylans_free_escape_artist_return()
        {
            var game = NewAbilityGame(new[] { "dylan", "mitchell" });
            var dylan = Inv(game, "dylan");

            game.BeginInvestigatorTurn("dylan");
            game.UseMajorAbility(args: new List<string> { "271" }); // drops the token, spends it
            Assert.Equal(0, dylan.MajorAbilityTokens);
            GiveFaceUpWound(game, dylan, "fear");
            game.EndTurnWithoutFinalAction(); // this turn is unaffected
            FinishRound(game);

            // A fresh Major Ability token (e.g. the Evidence economy) while the Escape Artist
            // token is still out and inside its window: UseMajorAbility always takes the free
            // return branch here, per the card's own two-part design.
            dylan.MajorAbilityTokens = 1;
            game.BeginInvestigatorTurn("dylan");
            Assert.Equal("271", game.BoardTokenSpace("escape-artist:dylan"));

            // Pre-fix: the free-return branch in UseMajorAbility returned before clearing the
            // Fear compulsion, so this stayed forced forever even after "using" the Major.
            Assert.Throws<InvalidOperationException>(() => game.EndTurnWithoutFinalAction());

            game.UseMajorAbility(); // free return: no args, no token spent
            Assert.Equal("271", dylan.Space);
            Assert.Equal(1, dylan.MajorAbilityTokens); // the return is free

            game.EndTurnWithoutFinalAction();
            Assert.Null(game.State.ActiveInvestigator);
        }

        // ---------- Bug 7b (fine-aim arena run, seed 516): Fear must release on death ----------

        [Fact]
        public void Fear_releases_when_the_owner_dies_during_their_own_turn()
        {
            var game = NewAbilityGame(new[] { "dylan", "mitchell" });
            var dylan = Inv(game, "dylan");

            game.BeginInvestigatorTurn("dylan");
            GiveFaceUpWound(game, dylan, "fear");
            game.EndTurnWithoutFinalAction(); // this turn is unaffected ("your next turn")
            FinishRound(game);

            game.BeginInvestigatorTurn("dylan");
            dylan.Dead = true; // e.g. a 4th Wound drawn mid-turn

            // Pre-fix: ForcedMajorAbilityPending never checked Dead/Escaped/Spirit, and Dylan's
            // Major counts as always-resolvable, so a Fear'd Dylan who died on his own turn
            // could never legally end it — UseMajorAbility itself refuses anyone off the board.
            game.EndTurnWithoutFinalAction();

            Assert.Null(game.State.ActiveInvestigator);
            Assert.Contains(game.State.Log, e => e.Type == "wound" && e.Detail.Contains("released"));
        }

        // ---------- Bug 8: Gear Jam's Stamina cost must not race Breathless at end of turn ----------

        [Fact]
        public void Gear_jam_and_breathless_together_no_longer_lock_the_turn()
        {
            var game = NewAbilityGame(new[] { "aira", "mitchell" });
            var aira = Inv(game, "aira");
            game.GrantConditionWithSubstitution(aira, "gear-jam");

            game.BeginInvestigatorTurn("aira");
            GiveFaceUpWound(game, aira, "breathless"); // "lose 1 Stamina at the end of each of your turns"
            aira.Stamina = 1; // just enough for Gear Jam's spend; Breathless would then overdraw
            aira.Charge = 0;

            // Pre-fix: Gear Jam's SpendStamina ran in ConditionsOnTurnEnd, *after* Breathless's
            // own end-of-turn Stamina loss in the same OnInvestigatorTurnEnd fanout had already
            // spent the last point, throwing "Not enough Stamina" out of EndTurn (seed 680).
            // Charge is automatic at end of turn now; the ordering contract is the same.
            game.EndTurnWithoutFinalAction();

            Assert.Equal(1, aira.Charge); // the automatic Charge still happened
            // Gear Jam's 1 spent, then Breathless's 1 clamped at 0, then the automatic Rest's +1.
            Assert.Equal(1, aira.Stamina);
            Assert.Null(game.State.ActiveInvestigator); // the turn ended cleanly
        }

        // ---------- Designer ruling: Mitchell's Sweep replaces the 1st cone, not adds to it ----------

        [Fact]
        public void Mitchell_sweep_replaces_the_1st_flashlight_cone_but_keeps_its_reveals()
        {
            var game = NewAbilityGame(new[] { "mitchell", "aira" });
            // "274" is only in the 1st cone (angle 0.0) of Mitchell's default Sawmill start
            // space; not in the 2nd (angle pi). Confirmed by direct inspection of both cones.
            game.State.Adversary.Space = "274";

            game.BeginInvestigatorTurn("mitchell");
            game.PlaceFlashlight(0.0);
            var placement = game.State.Flashlights.Single(f => f.InvestigatorId == "mitchell");
            var firstCone = placement.BrightSpaces.ToList();
            Assert.Contains("274", firstCone);
            Assert.True(game.State.Adversary.Revealed); // caught by the 1st cone

            game.UseMinorAbility("mitchell", new List<string> { "3.14159265" }); // ~pi: the opposite direction
            var secondCone = placement.BrightSpaces.ToList();

            // The 2nd cone is lit...
            Assert.All(secondCone, s => Assert.Contains(s, game.State.Overlay.BrightSpaces));
            // ...and every space only the 1st cone lit (minus any overlap with the 2nd, and
            // Mitchell's own space, which both cones always include) is dark again.
            foreach (string space in firstCone.Where(s => !secondCone.Contains(s)))
            {
                Assert.DoesNotContain(space, game.State.Overlay.BrightSpaces);
            }
            Assert.DoesNotContain("274", secondCone);
            Assert.DoesNotContain("274", game.State.Overlay.BrightSpaces);

            // Reveals are permanent: the Adversary caught by the 1st cone stays Revealed even
            // though the space that caught them has gone dark again.
            Assert.True(game.State.Adversary.Revealed);
        }
    }
}
