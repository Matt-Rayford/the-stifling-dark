using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Ids to human labels, in one place. Everything the protocol carries is an id
    /// ("lucy-belle", "spare-batteries", "S-21"); the designer should be reading names.
    /// No UnityEngine reference — compile-checked by tools/ClientCheck.
    /// </summary>
    public sealed class Describe
    {
        private readonly GameDatabase _db;
        private readonly Dictionary<string, CardDef> _cards;

        public Describe(GameDatabase db)
        {
            _db = db;
            // Ids repeat across decks only for identical cards, so first wins.
            _cards = new Dictionary<string, CardDef>();
            foreach (var card in db.Cards)
            {
                if (!_cards.ContainsKey(card.Id))
                {
                    _cards[card.Id] = card;
                }
            }
        }

        public GameDatabase Db => _db;

        /// <summary>The 10 base Investigators, in the same order the server offers them.</summary>
        public IReadOnlyList<InvestigatorDef> BaseInvestigators => _db.Investigators
            .Where(i => i.Set == "base")
            .OrderBy(i => i.Id, System.StringComparer.Ordinal)
            .ToList();

        public InvestigatorDef InvestigatorOrNull(string id) =>
            _db.Investigators.FirstOrDefault(i => i.Id == id);

        public string Investigator(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "—";
            }
            var def = InvestigatorOrNull(id);
            return def != null ? def.Name : id;
        }

        /// <summary>First name only — the status bar and figure labels are tight.</summary>
        public string ShortInvestigator(string id)
        {
            string name = Investigator(id);
            int space = name.IndexOf(' ');
            return space > 0 ? name.Substring(0, space) : name;
        }

        public string Initials(string id)
        {
            string name = Investigator(id);
            var parts = name.Split(' ');
            if (parts.Length >= 2 && parts[1].Length > 0)
            {
                return (parts[0][0].ToString() + parts[1][0]).ToUpperInvariant();
            }
            return name.Length >= 2 ? name.Substring(0, 2).ToUpperInvariant() : name.ToUpperInvariant();
        }

        public CardDef CardOrNull(string id) =>
            id != null && _cards.TryGetValue(id, out var card) ? card : null;

        public string Card(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "(face-down)";
            }
            var card = CardOrNull(id);
            return card != null ? card.Name : id;
        }

        public string CardText(string id)
        {
            var card = CardOrNull(id);
            return card != null ? card.Text : "";
        }

        /// <summary>Supply uses left on an item, as a suffix like " x2" / " ∞".</summary>
        public string SupplySuffix(string id)
        {
            var card = CardOrNull(id);
            if (card == null || card.Supply == null)
            {
                return "";
            }
            return card.Supply.Value < 0 ? " ∞" : " x" + card.Supply.Value;
        }

        public static string Adversary(string id)
        {
            switch (id)
            {
                case "butcher": return "The Butcher of Manchac Swamp";
                case "cult-of-hunlow": return "The Cult of Hunlow";
                case "insatiable-horror": return "The Insatiable Horror";
                default: return string.IsNullOrEmpty(id) ? "—" : id;
            }
        }

        public static string Scenario(string id)
        {
            switch (id)
            {
                case "sawmill": return "The Sawmill";
                case "amusement-park": return "The Amusement Park";
                default: return string.IsNullOrEmpty(id) ? "—" : id;
            }
        }

        public static string Phase(GamePhase phase)
        {
            switch (phase)
            {
                case GamePhase.AdversarySetup: return "Adversary setup";
                case GamePhase.InvestigatorTurns: return "Investigator turns";
                case GamePhase.AdversaryTurn: return "Adversary turn";
                default: return "Game over";
            }
        }

        public static string Result(GameResult result)
        {
            switch (result)
            {
                case GameResult.InvestigatorsWin: return "The Investigators escaped.";
                case GameResult.AdversaryWins: return "The Adversary wins.";
                case GameResult.Draw: return "A draw.";
                default: return "";
            }
        }

        public static string FinalAction(FinalActionKind kind)
        {
            switch (kind)
            {
                case FinalActionKind.Charge: return "Charged";
                case FinalActionKind.PlaceFlashlight: return "Flashlight";
                case FinalActionKind.InvolvedAction: return "Involved";
                default: return "—";
            }
        }

        public static string Door(DoorState state)
        {
            switch (state)
            {
                case DoorState.Locked: return "Locked";
                case DoorState.Damaged: return "Damaged";
                case DoorState.Destroyed: return "Destroyed";
                case DoorState.False: return "False Door";
                default: return "Open";
            }
        }

        /// <summary>Space kinds worth naming on a hover or an interact button.</summary>
        public static string SpaceKindName(SpaceKind kind)
        {
            switch (kind)
            {
                case SpaceKind.Door: return "Door";
                case SpaceKind.LightSwitch: return "Light Switch";
                case SpaceKind.Computer: return "Computer";
                case SpaceKind.TicketBooth: return "Ticket Booth";
                case SpaceKind.GameBooth: return "Game Booth";
                case SpaceKind.PointOfInterest: return "Point of Interest";
                case SpaceKind.MedicalItem: return "Medical Item";
                case SpaceKind.Start: return "Start";
                default: return "";
            }
        }

        /// <summary>
        /// The Evidence turn-in rewards, in the order the rulebook lists them, with the extra
        /// argument each needs. Kept here rather than read from scenarios.json because
        /// <see cref="Game.TurnInEvidence"/>'s switch is the real contract.
        /// </summary>
        public static readonly (string Reward, string Label, RewardArg Arg)[] EvidenceRewards =
        {
            ("reveal-poi", "Reveal a Point of Interest", RewardArg.PoiSpace),
            ("open-window-token", "Take an Open Window token", RewardArg.None),
            ("general-item", "Draw a General Item", RewardArg.None),
            ("rearrange-mirror-doors", "Set the open Mirror Maze color", RewardArg.MirrorColor),
            ("cursed-item", "Draw a Cursed Item (once per game)", RewardArg.None),
            ("dim-token", "Take a Dim token (once per game)", RewardArg.None),
            ("secret-passage-token", "Take a Secret Passage token (once per game)", RewardArg.None),
            ("medical-item", "Draw a Medical Item (once per game)", RewardArg.None),
            ("major-ability-token", "Give a Major Ability token (once per game)", RewardArg.Investigator),
        };

        public enum RewardArg
        {
            None,
            PoiSpace,
            MirrorColor,
            Investigator,
        }

        /// <summary>A log line as the event panel shows it.</summary>
        public string LogLine(PlayerView.LogEntry entry) =>
            "r" + entry.Round + "  " + entry.Type + ": " + Humanize(entry.Detail);

        /// <summary>
        /// Swap bare ids for names inside a log detail. Cheap and greedy — it only rewrites
        /// whole words — but it turns "aira lit 6 spaces" into "Aira Willson lit 6 spaces".
        /// </summary>
        public string Humanize(string detail)
        {
            if (string.IsNullOrEmpty(detail))
            {
                return "";
            }
            var words = detail.Split(' ');
            for (int i = 0; i < words.Length; i++)
            {
                var def = InvestigatorOrNull(words[i]);
                if (def != null)
                {
                    words[i] = def.Name;
                    continue;
                }
                var card = CardOrNull(words[i]);
                if (card != null)
                {
                    words[i] = card.Name;
                }
            }
            return string.Join(" ", words);
        }
    }
}
