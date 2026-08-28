using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    /// <summary>
    /// One test per hidden-information rule in <see cref="PlayerView"/>. These assert on the
    /// projection, not on the rules: the authoritative <see cref="GameState"/> keeps the truth,
    /// and each test proves that the truth does or does not survive the trip into a seat's view.
    /// </summary>
    public class PlayerViewTests
    {
        private static readonly string[] Roster = { "aira", "lucy-belle", "mitchell", "vincent" };

        private static Game NewGame(string adversary = "butcher", string? attack = null,
            List<string>? abilities = null, ulong seed = 42)
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
            foreach (string zone in game.Graph.Def.Zones.Keys)
            {
                game.PlaceHiddenEvidence(zone,
                    game.Graph.ZoneSpaces(zone).First(s => s.Kind == SpaceKind.Normal).Id);
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
            if (adversary == "cult-of-hunlow")
            {
                game.SetupCultists(CultistGroup(game, "S-25", Roster.Length), AltarSpace);
            }
            game.SetupAdversaryCards(attack ?? Cards(game, "attack").First(),
                abilities ?? Cards(game, "ability").Take(AbilityCount(adversary)).ToList());
            game.FinishAdversarySetup();
            return game;
        }

        /// <summary>The Altar's General space, used by the Cult objective tests.</summary>
        private const string AltarSpace = "L-1";

        private static List<string> Reachable(Game game, string from) =>
            game.Graph.DistancesFrom(from, 1, game.State.Overlay).Keys
                .Where(id => id != from && game.Graph.Space(id).Kind == SpaceKind.Normal)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();

        /// <summary>
        /// A single connected clump of General spaces starting next to Mor'gonnod and staying
        /// inside his Zone — the shape <see cref="Game.SetupCultists"/> demands.
        /// </summary>
        private static List<string> CultistGroup(Game game, string adversarySpace, int wanted)
        {
            string? zone = game.Graph.Space(adversarySpace).Zone;
            List<string> InZone(string from) =>
                Reachable(game, from).Where(id => game.Graph.Space(id).Zone == zone).ToList();

            var group = new List<string> { InZone(adversarySpace).First() };
            while (group.Count < wanted)
            {
                group.Add(group
                    .SelectMany(InZone)
                    .Distinct()
                    .Where(id => id != adversarySpace && !group.Contains(id))
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .First());
            }
            return group;
        }

        private static IEnumerable<string> Cards(Game game, string type)
        {
            string owner = game.State.Adversary.DefId switch
            {
                "butcher" => "butcher",
                "insatiable-horror" => "horror",
                _ => "cult",
            };
            return game.Db.Deck("adversary")
                .Where(c => c.Owner == owner && c.AdversaryCardType == type)
                .Select(c => c.Id)
                .OrderBy(id => id, StringComparer.Ordinal);
        }

        /// <summary>Ability cards each Adversary takes against the 4-Investigator roster.</summary>
        private static int AbilityCount(string adversary) => adversary switch
        {
            "butcher" => 2,
            "insatiable-horror" => 2,
            _ => 3,
        };

        private static InvestigatorState Inv(Game game, string id) =>
            game.State.Investigators.First(i => i.DefId == id);

        private static PlayerView InvestigatorView(Game game, string invId = "aira") =>
            game.ViewFor(ViewRole.Investigator, invId);

        private static PlayerView AdversaryView(Game game) => game.ViewFor(ViewRole.Adversary);

        // ------------------------------------------------- adversary position

        [Fact]
        public void A_hidden_adversary_has_no_space_in_the_investigator_view()
        {
            var game = NewGame();
            Assert.False(game.State.Adversary.Revealed);
            Assert.Equal("S-25", game.State.Adversary.Space);

            Assert.Null(InvestigatorView(game).Adversary.Space);
            // The Adversary player and the replay spectator still see their own standee.
            Assert.Equal("S-25", AdversaryView(game).Adversary.Space);
            Assert.Equal("S-25", game.ViewFor(ViewRole.Spectator).Adversary.Space);
        }

        [Fact]
        public void A_revealed_adversary_has_a_space_in_the_investigator_view()
        {
            var game = NewGame();
            game.State.Adversary.Revealed = true;

            var view = InvestigatorView(game);
            Assert.True(view.Adversary.Revealed);
            Assert.Equal("S-25", view.Adversary.Space);
        }

        [Fact]
        public void Cult_figures_are_revealed_one_at_a_time()
        {
            var game = NewGame("cult-of-hunlow");
            var figures = game.State.Adversary.Figures;
            Assert.True(figures.Count >= 2);
            figures[0].Revealed = true;

            var view = InvestigatorView(game);
            Assert.Equal(figures[0].Space, view.Adversary.Figures[0].Space);
            // The rest of the group is still hidden even though a neighbour is lit.
            Assert.All(view.Adversary.Figures.Skip(1), f => Assert.Null(f.Space));
            // The Adversary's own mini-map truth is intact.
            Assert.All(AdversaryView(game).Adversary.Figures, f => Assert.NotNull(f.Space));
        }

        [Fact]
        public void Shadow_and_noise_tokens_are_visible_to_the_investigators()
        {
            var game = NewGame();
            game.State.Adversary.ShadowTokens["S-24"] = "main";
            game.State.Adversary.NoiseTokens.Add("S-23");

            var view = InvestigatorView(game);
            Assert.Equal("main", view.Adversary.ShadowTokens["S-24"]);
            Assert.Contains("S-23", view.Adversary.NoiseTokens);
        }

        [Fact]
        public void The_adversarys_movement_budget_stays_behind_the_screen()
        {
            var game = NewGame();
            game.State.Adversary.MpRemaining = 7;
            game.State.Adversary.SprintRolled = 2;

            var hidden = InvestigatorView(game).Adversary;
            Assert.Null(hidden.MpRemaining);
            Assert.Null(hidden.SprintRolled);

            var own = AdversaryView(game).Adversary;
            Assert.Equal(7, own.MpRemaining);
            Assert.Equal(2, own.SprintRolled);
        }

        [Fact]
        public void Private_adversary_counters_are_dropped_from_the_investigator_view()
        {
            var game = NewGame();
            game.State.Adversary.Counters["stalk"] = 4;              // printed track: public
            game.State.Adversary.Counters["bufotoxin-face-up:aira"] = 3; // private bookkeeping

            var view = InvestigatorView(game);
            Assert.Equal(4, view.Adversary.Counters["stalk"]);
            Assert.DoesNotContain("bufotoxin-face-up:aira", view.Adversary.Counters.Keys);
            Assert.Contains("bufotoxin-face-up:aira", AdversaryView(game).Adversary.Counters.Keys);
        }

        // ---------------------------------------------------- evidence tokens

        [Fact]
        public void Hidden_evidence_tokens_are_omitted_entirely_from_the_investigator_view()
        {
            var game = NewGame();
            Assert.NotEmpty(game.State.Evidence);
            Assert.All(game.State.Evidence.Values, e => Assert.False(e.Revealed));

            Assert.Empty(InvestigatorView(game).Evidence);
            Assert.Equal(game.State.Evidence.Count, AdversaryView(game).Evidence.Count);
        }

        [Fact]
        public void A_revealed_evidence_token_stays_visible_after_the_light_fades()
        {
            var game = NewGame();
            var zone = game.State.Evidence.First();
            zone.Value.Revealed = true;
            // No Bright zone, no flashlight: the reveal is a property of the token, not the light.
            Assert.Empty(game.State.Overlay.BrightZones);
            Assert.Empty(game.State.Overlay.BrightSpaces);

            var seen = Assert.Single(InvestigatorView(game).Evidence);
            Assert.Equal(zone.Key, seen.Zone);
            Assert.Equal(zone.Value.Space, seen.Space);
            Assert.True(seen.Revealed);
        }

        // --------------------------------------------------------- POI tokens

        [Fact]
        public void A_hidden_poi_tokens_space_and_front_are_both_redacted()
        {
            var game = NewGame();
            var view = InvestigatorView(game);

            // The printed POI spaces are public map data, so the list length is not a secret.
            Assert.Equal(game.State.PoiTokens.Count, view.PoiTokens.Count);
            Assert.All(view.PoiTokens, p => Assert.Null(p.TokenSpace));
            Assert.All(view.PoiTokens, p => Assert.Null(p.CursedFront));
            Assert.All(view.PoiTokens, p => Assert.NotEqual("", p.PoiSpace));
        }

        [Fact]
        public void A_revealed_poi_token_shows_its_space_and_its_front()
        {
            var game = NewGame();
            var cursed = game.State.PoiTokens.First(p => p.CursedFront);
            cursed.Revealed = true;

            var shown = InvestigatorView(game).PoiTokens.First(p => p.PoiSpace == cursed.PoiSpace);
            Assert.Equal(cursed.TokenSpace, shown.TokenSpace);
            Assert.True(shown.CursedFront);
        }

        [Fact]
        public void A_scouted_poi_token_shows_its_space_but_keeps_its_front_secret()
        {
            var game = NewGame();
            var vincent = Inv(game, "vincent");
            game.BeginInvestigatorTurn("vincent");
            vincent.Space = "42"; // a printed Point of Interest
            game.UseMinorAbility();

            var scouted = game.State.PoiTokens.Where(p => p.ScoutedFaceDown).ToList();
            Assert.NotEmpty(scouted);
            var view = InvestigatorView(game);
            foreach (var token in scouted)
            {
                var shown = view.PoiTokens.First(p => p.PoiSpace == token.PoiSpace);
                Assert.True(shown.ScoutedFaceDown);
                Assert.Equal(token.TokenSpace, shown.TokenSpace); // the space is now public
                Assert.Null(shown.CursedFront);                   // the face is not
            }
        }

        [Fact]
        public void Scouting_hides_a_poi_tokens_front_from_the_adversary_too()
        {
            var game = NewGame();
            var vincent = Inv(game, "vincent");
            game.BeginInvestigatorTurn("vincent");
            vincent.Space = "42";
            game.UseMinorAbility();

            var scouted = game.State.PoiTokens.Where(p => p.ScoutedFaceDown).ToList();
            Assert.NotEmpty(scouted);
            var view = AdversaryView(game);
            foreach (var token in scouted)
            {
                var shown = view.PoiTokens.First(p => p.PoiSpace == token.PoiSpace);
                // The Investigators turned it face-down on the main board: the Adversary keeps
                // the position (it is on the board for everyone) but loses the front.
                Assert.Equal(token.TokenSpace, shown.TokenSpace);
                Assert.Null(shown.CursedFront);
            }
            // Untouched tokens still show their front to the Adversary who placed them.
            Assert.Contains(view.PoiTokens, p => !p.ScoutedFaceDown && p.CursedFront != null);
        }

        // ------------------------------------------------------------- wounds

        [Fact]
        public void A_face_down_wound_card_id_is_redacted_even_from_its_owner()
        {
            var game = NewGame();
            var aira = Inv(game, "aira");
            game.GainWound(aira, faceUp: false);
            game.GainWound(aira, faceUp: true);
            Assert.Equal(2, aira.Wounds.Count);
            Assert.All(aira.Wounds, w => Assert.NotEqual("", w.CardId));

            foreach (var view in new[] { InvestigatorView(game), AdversaryView(game) })
            {
                var slots = view.Investigators.First(i => i.DefId == "aira").Wounds;
                Assert.Equal(2, slots.Count);
                // The slot is visible — the card is not.
                Assert.Null(slots.Single(s => !s.FaceUp).CardId);
                Assert.NotNull(slots.Single(s => s.FaceUp).CardId);
            }
            // The replay spectator sees the deck's truth.
            var spectator = game.ViewFor(ViewRole.Spectator).Investigators.First(i => i.DefId == "aira");
            Assert.All(spectator.Wounds, s => Assert.NotNull(s.CardId));
        }

        // ------------------------------------------------- items and conditions

        [Fact]
        public void Investigator_items_are_shared_at_the_table_and_hidden_from_the_adversary()
        {
            var game = NewGame();
            var aira = Inv(game, "aira");
            aira.Items.Add("crowbar");
            aira.Items.Add("flare-gun");

            // Every Investigator seat reads every Investigator's inventory.
            foreach (string seat in Roster)
            {
                var panel = InvestigatorView(game, seat).Investigators.First(i => i.DefId == "aira");
                Assert.Equal(new List<string> { "crowbar", "flare-gun" }, panel.Items);
                Assert.Equal(2, panel.ItemCount);
            }

            var hidden = AdversaryView(game).Investigators.First(i => i.DefId == "aira");
            Assert.Null(hidden.Items);   // absent from the payload, not blanked
            Assert.Equal(2, hidden.ItemCount);
        }

        [Fact]
        public void Investigator_conditions_are_counted_but_not_named_for_the_adversary()
        {
            var game = NewGame();
            var aira = Inv(game, "aira");
            game.GainCondition(aira, "paranoid");

            Assert.Contains("paranoid",
                InvestigatorView(game).Investigators.First(i => i.DefId == "aira").Conditions!);

            var hidden = AdversaryView(game).Investigators.First(i => i.DefId == "aira");
            Assert.Null(hidden.Conditions);
            Assert.Equal(1, hidden.ConditionCount);
        }

        [Fact]
        public void A_face_down_bufotoxin_is_hidden_from_the_investigator_holding_it()
        {
            var game = NewGame();
            var aira = Inv(game, "aira");
            game.GainCondition(aira, "bufotoxin");

            var panel = InvestigatorView(game).Investigators.First(i => i.DefId == "aira");
            Assert.DoesNotContain("bufotoxin", panel.Conditions!);
            Assert.Equal(1, panel.ConditionCount); // the slot is visibly occupied

            // The Adversary dealt it, so the flip target is theirs to see.
            Assert.Contains("aira", AdversaryView(game).BufotoxinFlipTargets);
            Assert.Empty(InvestigatorView(game).BufotoxinFlipTargets);
        }

        [Fact]
        public void Flipping_bufotoxin_face_up_lets_its_holder_read_it()
        {
            var game = NewGame();
            var aira = Inv(game, "aira");
            game.GainCondition(aira, "bufotoxin");
            game.State.Adversary.Counters["bufotoxin-face-up:aira"] = game.State.Round;

            Assert.Contains("bufotoxin",
                InvestigatorView(game).Investigators.First(i => i.DefId == "aira").Conditions!);
            Assert.DoesNotContain("aira", AdversaryView(game).BufotoxinFlipTargets);
        }

        // ----------------------------------------------------- adversary cards

        [Fact]
        public void The_adversary_loadout_is_secret_until_a_card_is_played()
        {
            var game = NewGame("butcher", "rend", new List<string> { "decay", "escalating-terror" });

            var before = InvestigatorView(game).Adversary;
            Assert.Null(before.AttackCard);
            Assert.Empty(before.ActiveAbilities);
            // The COUNT is public — the cards are face-up on the Adversary board's slots.
            Assert.Equal(2, before.ActiveAbilityCount);
            Assert.Empty(InvestigatorView(game).KnownAdversaryCards);

            // The Adversary sees their own board in full.
            var own = AdversaryView(game).Adversary;
            Assert.Equal("rend", own.AttackCard);
            Assert.Equal(2, own.ActiveAbilities.Count);
        }

        [Fact]
        public void Playing_an_ability_teaches_the_investigators_its_name_for_good()
        {
            var game = NewGame("butcher", "rend", new List<string> { "decay", "escalating-terror" });
            foreach (var inv in game.State.Investigators.ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            game.State.Adversary.Counters["stalk"] = 1;
            game.PlayAdversaryCard("decay");

            var view = InvestigatorView(game);
            Assert.Contains("decay", view.KnownAdversaryCards);
            // Decay went onto a cooldown slot; the slot names the card the table has now seen.
            var cooldowns = view.Adversary.Cooldown1.Concat(view.Adversary.Cooldown2).ToList();
            Assert.Contains(cooldowns, c => c.CardId == "decay");
            // The card that has never been played is still nameless.
            Assert.DoesNotContain("escalating-terror", view.KnownAdversaryCards);
            Assert.DoesNotContain("escalating-terror", view.Adversary.ActiveAbilities);
            Assert.Null(view.Adversary.AttackCard);
        }

        // ------------------------------------------------------- escape choices

        [Fact]
        public void The_escape_shortlist_reaches_the_investigators_and_not_the_adversary()
        {
            var game = NewGame();
            var choices = new List<string> { "the-truck", "the-gate", "the-grave" };

            Assert.Equal(choices,
                game.ViewFor(ViewRole.Investigator, "aira", choices).EscapeChoices);
            Assert.Null(game.ViewFor(ViewRole.Adversary, null, choices).EscapeChoices);
            // No shortlist pending: absent for everyone.
            Assert.Null(InvestigatorView(game).EscapeChoices);
        }

        // ---------------------------------------------------- objective tokens

        [Fact]
        public void The_altar_stays_on_the_mini_map_until_it_is_revealed()
        {
            var game = NewGame("cult-of-hunlow");
            Assert.Equal(AltarSpace, game.State.Objective.Tokens["altar"]);

            Assert.DoesNotContain("altar", InvestigatorView(game).Objective.Tokens.Keys);
            Assert.Equal(AltarSpace, AdversaryView(game).Objective.Tokens["altar"]);

            game.State.Adversary.Counters["altar-revealed"] = 1;
            Assert.Equal(AltarSpace, InvestigatorView(game).Objective.Tokens["altar"]);
        }

        [Fact]
        public void The_actual_grave_is_hidden_until_its_space_is_bright()
        {
            var game = NewGame();
            game.State.Objective.Tokens["grave-actual"] = "S-21";
            game.State.Objective.Tokens["grave-decoy"] = "S-24";

            var hidden = InvestigatorView(game).Objective.Tokens;
            Assert.DoesNotContain("grave-actual", hidden.Keys);
            Assert.Equal("S-24", hidden["grave-decoy"]); // the decoy is face-down but on the board

            game.State.Overlay.BrightSpaces.Add("S-21");
            Assert.Equal("S-21", InvestigatorView(game).Objective.Tokens["grave-actual"]);
        }

        // ------------------------------------------------------- board and log

        [Fact]
        public void The_board_overlay_lights_and_doors_are_public_to_every_seat()
        {
            var game = NewGame();
            game.State.Overlay.BrightZones.Add("S");
            game.State.Overlay.DimZones.Add("L");
            game.State.Overlay.DoorStates["S-19"] = DoorState.Damaged;
            game.State.Overlay.SecretPassages.Add(BoardOverlay.EdgeKey("S-21", "S-24"));

            foreach (var view in new[]
                     {
                         InvestigatorView(game), AdversaryView(game),
                         game.ViewFor(ViewRole.Spectator),
                     })
            {
                Assert.Contains("S", view.Overlay.BrightZones);
                Assert.Contains("L", view.Overlay.DimZones);
                Assert.Equal(DoorState.Damaged, view.Overlay.DoorStates["S-19"]);
                Assert.Contains(BoardOverlay.EdgeKey("S-21", "S-24"), view.Overlay.SecretPassages);
            }
        }

        [Fact]
        public void The_investigator_log_never_carries_setup_or_todo_lines()
        {
            var game = NewGame("cult-of-hunlow");
            foreach (var inv in game.State.Investigators.ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }

            var investigator = InvestigatorView(game);
            // "setup" names every Cultist's starting space and the Altar.
            Assert.DoesNotContain(investigator.Log, e => e.Type == "setup");
            Assert.DoesNotContain(investigator.Log, e => e.Type == "todo");

            var adversary = AdversaryView(game);
            Assert.Contains(adversary.Log, e => e.Type == "setup");
            // The Investigators still get the public narration.
            Assert.Contains(investigator.Log, e => e.Type == "event");
        }

        [Fact]
        public void A_played_adversary_card_line_is_visible_to_investigators()
        {
            // The designer's live-playtest bug: a played card, a broken door, a Stalk change --
            // anything a table player would physically see -- must not be silently dropped just
            // because the engine tags it "adversary".
            var game = NewGame(abilities: new List<string> { "escalating-terror", "decay" });
            foreach (var inv in game.State.Investigators.ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            // Escalating Terror has no Stalk cost and needs no target: safe to play blind here.
            game.PlayAdversaryCard("escalating-terror");

            var investigator = InvestigatorView(game);
            Assert.Contains(investigator.Log,
                e => e.Type == "adversary" && e.Detail == "played escalating-terror");
        }

        [Fact]
        public void A_hidden_position_move_line_is_not_visible_to_investigators()
        {
            var game = NewGame();
            foreach (var inv in game.State.Investigators.ToList())
            {
                game.BeginInvestigatorTurn(inv.DefId);
                game.EndTurnWithoutFinalAction();
            }
            var adv = game.State.Adversary;
            Assert.False(adv.Revealed);
            string from = adv.Space;
            string to = game.Graph.DistancesFrom(from, 1, game.State.Overlay).Keys
                .First(id => id != from && !game.State.Overlay.BrightSpaces.Contains(id) &&
                             game.Graph.TryStep(FigureKind.Adversary, from, id, game.State.Overlay) != null);

            game.AdversaryMoveStep(to);

            Assert.False(adv.Revealed); // still Hidden: the move did not step into the light
            Assert.Contains(game.State.Log, e => e.Type == "adversary-secret" &&
                e.Detail.StartsWith("moved " + from + " -> " + to, StringComparison.Ordinal));

            var investigator = InvestigatorView(game);
            Assert.DoesNotContain(investigator.Log, e => e.Detail.Contains(from + " -> " + to));
            // The Adversary's own seat and the replay spectator still get the full truth.
            Assert.Contains(AdversaryView(game).Log, e => e.Detail.Contains(from + " -> " + to));
            Assert.Contains(game.ViewFor(ViewRole.Spectator).Log, e => e.Detail.Contains(from + " -> " + to));
        }

        [Fact]
        public void The_investigator_log_never_names_bufotoxin()
        {
            var game = NewGame();
            var aira = Inv(game, "aira");
            game.GainCondition(aira, "bufotoxin");
            game.State.Log.Add(new GameEvent
            {
                Round = game.State.Round,
                Type = "condition",
                Detail = "the Adversary may flip aira's Bufotoxin face-up (FlipBufotoxinFaceUp)",
            });

            Assert.DoesNotContain(InvestigatorView(game).Log,
                e => e.Detail.Contains("Bufotoxin"));
            Assert.Contains(AdversaryView(game).Log, e => e.Detail.Contains("Bufotoxin"));
        }

        // ---------------------------------------------------------- deck order

        [Fact]
        public void Deck_order_never_leaves_the_server_for_any_seat()
        {
            var game = NewGame();
            foreach (var view in new[]
                     {
                         InvestigatorView(game), AdversaryView(game),
                         game.ViewFor(ViewRole.Spectator),
                     })
            {
                Assert.Equal(game.State.WoundDeck.Count, view.Decks.Wound);
                Assert.Equal(game.State.EventDeck.Count, view.Decks.Event);
                Assert.Equal(game.State.GeneralItemDeck.Count, view.Decks.GeneralItem);
                Assert.Equal(game.State.CursedItemDeck.Count, view.Decks.CursedItem);
            }
        }
    }
}
