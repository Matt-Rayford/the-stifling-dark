using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using StiflingDark.Engine.Core;

namespace StiflingDark.Engine.Data
{
    /// <summary>
    /// Immutable, validated view of everything in game-data/. Loaded once and shared;
    /// all game state refers back to definitions by id.
    /// </summary>
    public sealed class GameDatabase
    {
        public GameConfig Config { get; }
        public IReadOnlyList<MapDef> Maps { get; }
        public IReadOnlyList<InvestigatorDef> Investigators { get; }
        public IReadOnlyList<CardDef> Cards { get; }
        public FlashlightDef Flashlight { get; }

        private readonly Dictionary<string, MapDef> _mapById;
        private readonly Dictionary<string, InvestigatorDef> _investigatorById;

        public MapDef Map(string id) =>
            _mapById.TryGetValue(id, out var m) ? m : throw new KeyNotFoundException($"No map with id '{id}'.");

        public InvestigatorDef Investigator(string id) =>
            _investigatorById.TryGetValue(id, out var i) ? i : throw new KeyNotFoundException($"No investigator with id '{id}'.");

        public IEnumerable<CardDef> Deck(string deck) => Cards.Where(c => c.Deck == deck);

        private GameDatabase(GameConfig config, List<MapDef> maps, List<InvestigatorDef> investigators, List<CardDef> cards, FlashlightDef flashlight)
        {
            Config = config;
            Maps = maps;
            Investigators = investigators;
            Cards = cards;
            Flashlight = flashlight;
            _mapById = maps.ToDictionary(m => m.Id);
            _investigatorById = investigators.ToDictionary(i => i.Id);
        }

        /// <summary>Load and validate all game data from a game-data directory.</summary>
        public static GameDatabase Load(string gameDataDir)
        {
            var config = LoadConfig(Path.Combine(gameDataDir, "config.json"));
            var maps = new List<MapDef>
            {
                LoadMap(Path.Combine(gameDataDir, "maps", "sawmill.json")),
                LoadMap(Path.Combine(gameDataDir, "maps", "amusement-park.json")),
            };
            var investigators = LoadInvestigators(Path.Combine(gameDataDir, "investigators.json"));
            var cards = LoadAllDecks(Path.Combine(gameDataDir, "cards"));
            var flashlight = LoadFlashlight(Path.Combine(gameDataDir, "flashlight.json"));
            Validate(maps, investigators);
            return new GameDatabase(config, maps, investigators, cards, flashlight);
        }

        private static FlashlightDef LoadFlashlight(string path)
        {
            var j = ReadJson(path);
            var def = new FlashlightDef
            {
                OriginX = j["origin"]!["x"]!.Value<double>(),
                OriginY = j["origin"]!["y"]!.Value<double>(),
                ImageWidth = j["imageSize"]!["w"]!.Value<double>(),
                ImageHeight = j["imageSize"]!["h"]!.Value<double>(),
                LengthInSpacePitches = j["scale"]!["lengthInSpacePitches"]!.Value<double>(),
            };
            foreach (var p in (JArray)j["outlinePolygon"]!)
            {
                def.OutlinePolygon.Add(new[] { p[0]!.Value<double>(), p[1]!.Value<double>() });
            }
            return def;
        }

        private static JObject ReadJson(string path) => JObject.Parse(File.ReadAllText(path));

        private static GameConfig LoadConfig(string path)
        {
            var j = ReadJson(path);
            var config = new GameConfig
            {
                Rounds = j["rounds"]!["total"]!.Value<int>(),
                SprintDieFaces = j["dice"]!["sprintDieFaces"]!.Values<int>().ToList(),
                ChargeMax = j["charge"]!["max"]!.Value<int>(),
                WoundsToDie = j["wounds"]!["deathAt"]!.Value<int>(),
            };
            foreach (var prop in ((JObject)j["byInvestigatorCount"]!).Properties())
            {
                if (!int.TryParse(prop.Name, out int count))
                {
                    continue; // "_notes" etc.
                }
                config.ByInvestigatorCount[count] = new InvestigatorCountRules
                {
                    EvidenceRequiredForObjective = prop.Value["evidenceRequiredForObjective"]!.Value<int>(),
                    StartingPointsOfInterest = prop.Value["startingPointsOfInterest"]!.Value<int>(),
                    MedicalItemsOnBoard = prop.Value["medicalItemsOnBoard"]!.Value<int>(),
                };
            }
            return config;
        }

        private static MapDef LoadMap(string path)
        {
            var j = ReadJson(path);
            var map = new MapDef
            {
                Id = j["id"]!.Value<string>()!,
                Name = j["name"]!.Value<string>()!,
                SpacePitch = j["spacePitch"]!.Value<double>(),
                SpaceRadius = j["spaceRadius"]!.Value<double>(),
                Zones = ((JObject)j["zones"]!).Properties().ToDictionary(p => p.Name, p => p.Value.Value<string>()!),
            };
            foreach (var s in (JArray)j["spaces"]!)
            {
                map.Spaces.Add(new SpaceDef
                {
                    Id = s["id"]!.Value<string>()!,
                    X = s["x"]!.Value<double>(),
                    Y = s["y"]!.Value<double>(),
                    Zone = s["zone"]?.Value<string>(),
                    PrintedLight = ParseLight(s["light"]!.Value<string>()!),
                    Kind = ParseKind(s["kind"]!.Value<string>()!),
                    Carriage = s["carriage"]?.Value<bool>() ?? false,
                    Water = s["water"]?.Value<bool>() ?? false,
                });
            }
            foreach (var e in (JArray)j["edges"]!)
            {
                map.Edges.Add(new EdgeDef
                {
                    A = e["a"]!.Value<string>()!,
                    B = e["b"]!.Value<string>()!,
                    Type = ParseEdgeType(e["type"]!.Value<string>()!),
                    Color = e["color"] == null ? (MirrorDoorColor?)null : ParseColor(e["color"]!.Value<string>()!),
                    Water = e["water"]?.Value<bool>() ?? false,
                });
            }
            if (j["rides"] is JObject rides)
            {
                foreach (var prop in rides.Properties())
                {
                    var ride = new RideDef();
                    foreach (var carriage in (JArray)prop.Value["carriages"]!)
                    {
                        ride.Carriages.Add(carriage.Values<string>().Select(v => v!).ToList());
                    }
                    foreach (var fn in ((JObject)prop.Value["forcedNext"]!).Properties())
                    {
                        ride.ForcedNext[fn.Name] = fn.Value.Value<string>()!;
                    }
                    map.Rides[prop.Name] = ride;
                }
            }
            if (j["waterFlow"]?["clockwiseLoop"] is JArray loop)
            {
                map.WaterFlowLoop = loop.Values<string>().Select(v => v!).ToList();
            }
            return map;
        }

        private static List<InvestigatorDef> LoadInvestigators(string path)
        {
            var j = ReadJson(path);
            var result = new List<InvestigatorDef>();
            foreach (var i in (JArray)j["investigators"]!)
            {
                result.Add(new InvestigatorDef
                {
                    Id = i["id"]!.Value<string>()!,
                    Name = i["name"]!.Value<string>()!,
                    Mp = i["mp"]!.Value<int>(),
                    MinorAbility = ParseAbility(i["minorAbility"]),
                    MajorAbility = ParseAbility(i["majorAbility"]),
                    StaminaTrack = ParseTrack(i["staminaTrack"]!),
                    ChargeTrack = ParseTrack(i["chargeTrack"]!),
                    Set = i["set"]?.Value<string>() ?? "base",
                });
            }
            return result;
        }

        private static AbilityDef ParseAbility(JToken? t) => t == null
            ? new AbilityDef()
            : new AbilityDef { Name = t["name"]?.Value<string>(), Text = t["text"]?.Value<string>() ?? "" };

        private static TrackDef ParseTrack(JToken t) => new TrackDef
        {
            Spaces = t["spaces"]!.Value<int>(),
            Start = t["start"]!.Value<int>(),
            WoundIconSpaces = t["woundIconSpaces"]?.Values<int>().ToList() ?? new List<int>(),
        };

        private static List<CardDef> LoadAllDecks(string cardsDir)
        {
            var cards = new List<CardDef>();
            var decks = new (string File, string Deck)[]
            {
                ("wounds.json", "wound"),
                ("conditions.json", "condition"),
                ("general-items.json", "general-item"),
                ("medical-items.json", "medical-item"),
                ("cursed-items.json", "cursed-item"),
                ("objective-items.json", "objective-item"),
                ("escape-cards.json", "escape"),
                ("events.json", "event"),
                ("adversary-cards.json", "adversary"),
            };
            foreach (var (file, deck) in decks)
            {
                var j = ReadJson(Path.Combine(cardsDir, file));
                foreach (var c in (JArray)j["cards"]!)
                {
                    string text = c["text"]?.Value<string>() ?? "";
                    string? setup = c["setup"]?.Value<string>();
                    cards.Add(new CardDef
                    {
                        Id = c["id"]!.Value<string>()!,
                        Name = c["name"]!.Value<string>()!,
                        Deck = deck,
                        Count = c["count"]?.Value<int>() ?? 1,
                        Text = setup == null ? text : (text.Length == 0 ? setup : setup + "\n" + text),
                        Supply = ParseSupply(c["supply"]),
                        Set = c["set"]?.Value<string>() ?? "base",
                        Replaces = c["replaces"]?.Value<string>(),
                        Owner = c["owner"]?.Value<string>() ?? c["scenario"]?.Value<string>(),
                        Severity = c["severity"]?.Value<string>(),
                        AdversaryCardType = deck == "adversary" ? c["type"]?.Value<string>() : null,
                        Cooldown = c["cooldown"]?.Type == JTokenType.Integer ? c["cooldown"]!.Value<int>() : (int?)null,
                    });
                }
            }
            return cards;
        }

        private static int? ParseSupply(JToken? t)
        {
            if (t == null || t.Type == JTokenType.Null)
            {
                return null;
            }
            if (t.Type == JTokenType.String)
            {
                return -1; // "infinity"
            }
            return t.Value<int>();
        }

        private static void Validate(List<MapDef> maps, List<InvestigatorDef> investigators)
        {
            foreach (var map in maps)
            {
                var ids = new HashSet<string>();
                foreach (var s in map.Spaces)
                {
                    if (!ids.Add(s.Id))
                    {
                        throw new InvalidDataException($"Map '{map.Id}': duplicate space id '{s.Id}'.");
                    }
                    if (s.Zone != null && !map.Zones.ContainsKey(s.Zone))
                    {
                        throw new InvalidDataException($"Map '{map.Id}': space '{s.Id}' references unknown zone '{s.Zone}'.");
                    }
                }
                foreach (var e in map.Edges)
                {
                    if (!ids.Contains(e.A) || !ids.Contains(e.B))
                    {
                        throw new InvalidDataException($"Map '{map.Id}': edge {e.A}-{e.B} references a missing space.");
                    }
                    if (e.Type == EdgeType.MirrorDoor && e.Color == null)
                    {
                        throw new InvalidDataException($"Map '{map.Id}': mirror door {e.A}-{e.B} has no color.");
                    }
                }
            }
            if (investigators.Count == 0)
            {
                throw new InvalidDataException("No investigators loaded.");
            }
        }

        private static LightLevel ParseLight(string s) => s switch
        {
            "dim" => LightLevel.Dim,
            "dark" => LightLevel.Dark,
            _ => throw new InvalidDataException($"Unknown light level '{s}'."),
        };

        private static SpaceKind ParseKind(string s) => s switch
        {
            "normal" => SpaceKind.Normal,
            "door" => SpaceKind.Door,
            "lightswitch" => SpaceKind.LightSwitch,
            "computer" => SpaceKind.Computer,
            "ticketbooth" => SpaceKind.TicketBooth,
            "gamebooth" => SpaceKind.GameBooth,
            "poi" => SpaceKind.PointOfInterest,
            "medical" => SpaceKind.MedicalItem,
            "start" => SpaceKind.Start,
            _ => throw new InvalidDataException($"Unknown space kind '{s}'."),
        };

        private static EdgeType ParseEdgeType(string s) => s switch
        {
            "move" => EdgeType.Move,
            "window" => EdgeType.Window,
            "mirrorDoor" => EdgeType.MirrorDoor,
            "adversaryLink" => EdgeType.AdversaryLink,
            _ => throw new InvalidDataException($"Unknown edge type '{s}'."),
        };

        private static MirrorDoorColor ParseColor(string s) => s switch
        {
            "red" => MirrorDoorColor.Red,
            "green" => MirrorDoorColor.Green,
            "blue" => MirrorDoorColor.Blue,
            _ => throw new InvalidDataException($"Unknown mirror door color '{s}'."),
        };
    }
}
