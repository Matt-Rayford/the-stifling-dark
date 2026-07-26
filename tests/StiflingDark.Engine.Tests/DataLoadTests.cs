using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Tests
{
    public class DataLoadTests
    {
        [Fact]
        public void Config_matches_rulebook_numbers()
        {
            var config = TestData.Db.Config;
            Assert.Equal(17, config.Rounds);
            Assert.Equal(new[] { 2, 2, 3, 3, 3, 4 }, config.SprintDieFaces);
            Assert.Equal(3, config.ChargeMax);
            Assert.Equal(4, config.WoundsToDie);
            Assert.Equal(2, config.ByInvestigatorCount[2].EvidenceRequiredForObjective);
            Assert.Equal(3, config.ByInvestigatorCount[3].EvidenceRequiredForObjective);
            Assert.Equal(5, config.ByInvestigatorCount[4].EvidenceRequiredForObjective);
        }

        [Fact]
        public void Both_maps_load_with_expected_space_counts()
        {
            var sawmill = TestData.Db.Map("sawmill");
            var park = TestData.Db.Map("amusement-park");
            Assert.Equal(426, sawmill.Spaces.Count);
            Assert.Equal(461, park.Spaces.Count);
            Assert.Equal(5, sawmill.Zones.Count);
            Assert.Equal(5, park.Zones.Count);
        }

        [Fact]
        public void Sawmill_special_spaces_match_the_board()
        {
            var sawmill = TestData.Db.Map("sawmill");
            Assert.Equal(5, sawmill.Spaces.Count(s => s.Kind == SpaceKind.Computer));
            Assert.Equal(5, sawmill.Spaces.Count(s => s.Kind == SpaceKind.PointOfInterest));
            Assert.Equal(2, sawmill.Spaces.Count(s => s.Kind == SpaceKind.MedicalItem));
            Assert.Equal(4, sawmill.Spaces.Count(s => s.Kind == SpaceKind.Start));
            Assert.Equal(12, sawmill.Spaces.Count(s => s.Kind == SpaceKind.Door));
        }

        [Fact]
        public void Park_special_spaces_match_the_board()
        {
            var park = TestData.Db.Map("amusement-park");
            Assert.Equal(6, park.Spaces.Count(s => s.Kind == SpaceKind.TicketBooth));
            Assert.Equal(2, park.Spaces.Count(s => s.Kind == SpaceKind.GameBooth));
            Assert.Equal(4, park.Spaces.Count(s => s.Kind == SpaceKind.PointOfInterest));
            Assert.Equal(12, park.Spaces.Count(s => s.Carriage));
            Assert.Equal(23, park.Spaces.Count(s => s.Water));
            Assert.Equal(12, park.Edges.Count(e => e.Type == EdgeType.MirrorDoor));
            Assert.Equal(12, park.Edges.Count(e => e.Type == EdgeType.AdversaryLink));
        }

        [Fact]
        public void Investigator_roster_is_complete()
        {
            var investigators = TestData.Db.Investigators;
            Assert.Equal(12, investigators.Count);
            Assert.Equal(10, investigators.Count(i => i.Set == "base"));
            Assert.All(investigators, i => Assert.Equal(4, i.Mp));
            var lucy = TestData.Db.Investigator("lucy-belle");
            Assert.Equal(5, lucy.StaminaTrack.Start);
            Assert.Equal(3, lucy.ChargeTrack.Start);
        }

        [Fact]
        public void Decks_have_expected_physical_card_counts()
        {
            int Total(string deck) => TestData.Db.Deck(deck).Sum(c => c.Count);
            Assert.Equal(26, Total("wound"));
            Assert.Equal(29, Total("condition"));
            Assert.Equal(28, Total("event"));
            Assert.Equal(35, Total("adversary"));
            // Base General Item deck (excluding MI replacements and NF cards) is 34 cards.
            Assert.Equal(34, TestData.Db.Deck("general-item").Where(c => c.Set == "base").Sum(c => c.Count));
        }

        [Fact]
        public void Mini_expansion_cards_replace_existing_base_cards()
        {
            var generalItems = TestData.Db.Deck("general-item").ToList();
            var baseIds = generalItems.Where(c => c.Set == "base").Select(c => c.Id).ToHashSet();
            var miCards = generalItems.Where(c => c.Set == "MI").ToList();
            Assert.Equal(5, miCards.Count);
            Assert.All(miCards, mi =>
            {
                Assert.NotNull(mi.Replaces);
                Assert.Contains(mi.Replaces!, baseIds);
            });
        }
    }
}
