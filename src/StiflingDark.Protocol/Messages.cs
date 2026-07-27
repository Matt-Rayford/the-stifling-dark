using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace StiflingDark.Protocol
{
    /// <summary>
    /// Wire messages. Client -> server: hello, create_room, join_room, leave_room, set_seat,
    /// add_bot, remove_seat, configure, set_speed, ready, start_game, command, resync,
    /// list_games, abandon_game. Server -> client: welcome, room, update, games, turn_alert,
    /// room_closed, error. Everything is a JSON object with a "type" field.
    /// </summary>
    public static class MessageType
    {
        // client -> server
        public const string Hello = "hello";
        public const string CreateRoom = "create_room";
        public const string JoinRoom = "join_room";
        public const string LeaveRoom = "leave_room";
        public const string SetSeat = "set_seat";
        public const string AddBot = "add_bot";
        public const string RemoveSeat = "remove_seat";
        public const string Configure = "configure";
        public const string SetSpeed = "set_speed";
        public const string Ready = "ready";
        public const string StartGame = "start_game";
        public const string Command = "command";
        public const string Resync = "resync";
        public const string ListGames = "list_games";
        public const string AbandonGame = "abandon_game";

        // server -> client
        public const string Welcome = "welcome";
        public const string Room = "room";
        public const string Update = "update";
        public const string Games = "games";
        public const string TurnAlert = "turn_alert";
        public const string RoomClosed = "room_closed";
        public const string Error = "error";
    }

    /// <summary>Which side of the table a seat plays.</summary>
    public enum SeatRole
    {
        Investigator,
        Adversary,
    }

    /// <summary>Who drives a seat. Any subset of seats may be <see cref="Bot"/>.</summary>
    public enum SeatFill
    {
        Human,
        Bot,
    }

    public sealed class SeatInfo
    {
        public int Seat { get; set; }
        public string Name { get; set; } = "";
        public SeatRole Role { get; set; }
        public SeatFill Fill { get; set; }
        /// <summary>The Investigator this seat plays; empty for the Adversary seat.</summary>
        public string InvestigatorId { get; set; } = "";
        public bool Connected { get; set; }
        public bool Ready { get; set; }
    }

    /// <summary>The scenario the host has dialled in; sent with every room snapshot.</summary>
    public sealed class SetupInfo
    {
        public string ScenarioId { get; set; } = "";
        public string AdversaryId { get; set; } = "";
        /// <summary>Investigator def id -> chosen Start space. Empty entries mean "host picks".</summary>
        public Dictionary<string, string> StartSpaces { get; set; } = new Dictionary<string, string>();
        public List<string> MedicalItemSpaces { get; set; } = new List<string>();
        public bool UseMiniExpansionCards { get; set; }
        /// <summary>Scenarios and adversaries this server will accept, so a client can offer them.</summary>
        public List<string> AvailableScenarios { get; set; } = new List<string>();
        public List<string> AvailableAdversaries { get; set; } = new List<string>();
        public List<string> AvailableInvestigators { get; set; } = new List<string>();
    }

    /// <summary>Room composition; sent on every lobby change and on (re)join.</summary>
    public sealed class RoomMessage
    {
        public string Type { get; set; } = MessageType.Room;
        public string Code { get; set; } = "";
        public int YourSeat { get; set; }
        /// <summary>Reconnect token — present only in messages to the seat it belongs to.</summary>
        public string Token { get; set; } = "";
        public bool Started { get; set; }
        /// <summary>Bot pacing for this room: "slow" / "medium" / "fast".</summary>
        public string Speed { get; set; } = "medium";
        public SetupInfo Setup { get; set; } = new SetupInfo();
        public List<SeatInfo> Seats { get; set; } = new List<SeatInfo>();
    }

    /// <summary>One line of the game log, already filtered for the receiving seat.</summary>
    public sealed class LogEntryMessage
    {
        public int Round { get; set; }
        public string Type { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    /// <summary>
    /// Per-seat game state push: what just happened, and what this seat may see. The view is
    /// a <see cref="StiflingDark.Engine.Core.PlayerView"/> serialized with WireCodec settings —
    /// never the GameState.
    /// </summary>
    public sealed class UpdateMessage
    {
        public string Type { get; set; } = MessageType.Update;
        /// <summary>Log lines new since this seat's last update — the delta of View.Log.</summary>
        public List<LogEntryMessage> Events { get; set; } = new List<LogEntryMessage>();
        public JObject? View { get; set; }
        /// <summary>Seats the game is currently waiting on.</summary>
        public List<int> ActingSeats { get; set; } = new List<int>();
        public bool YourTurn { get; set; }
        /// <summary>True when this update is a full resync rather than an incremental push.</summary>
        public bool Resync { get; set; }
    }

    public sealed class ErrorMessage
    {
        public string Type { get; set; } = MessageType.Error;
        public string Message { get; set; } = "";
    }

    /// <summary>One of the caller's games, for the My Games list.</summary>
    public sealed class GameSummary
    {
        public string Code { get; set; } = "";
        public List<string> Players { get; set; } = new List<string>();
        public int YourSeat { get; set; }
        public SeatRole YourRole { get; set; }
        public bool Started { get; set; }
        public bool Finished { get; set; }
        /// <summary>The game is waiting on YOUR input.</summary>
        public bool YourTurn { get; set; }
        public int Round { get; set; }
        public string ScenarioId { get; set; } = "";
        public string AdversaryId { get; set; } = "";
    }

    /// <summary>Reply to hello: your durable identity and everything you're playing.</summary>
    public sealed class WelcomeMessage
    {
        public string Type { get; set; } = MessageType.Welcome;
        public string PlayerId { get; set; } = "";
        public string Name { get; set; } = "";
        public List<GameSummary> GamesList { get; set; } = new List<GameSummary>();
    }

    /// <summary>Reply to list_games: a fresh My Games snapshot.</summary>
    public sealed class GamesMessage
    {
        public string Type { get; set; } = MessageType.Games;
        public List<GameSummary> GamesList { get; set; } = new List<GameSummary>();
    }

    /// <summary>
    /// A game you're NOT currently watching needs your input. Sent once per turn edge to
    /// every other live connection of that player.
    /// </summary>
    public sealed class TurnAlertMessage
    {
        public string Type { get; set; } = MessageType.TurnAlert;
        public string Code { get; set; } = "";
    }

    /// <summary>A room was ended for everyone (abandon_game). Sent to every connected member;
    /// the room is gone from My Games and its code no longer joins.</summary>
    public sealed class RoomClosedMessage
    {
        public string Type { get; set; } = MessageType.RoomClosed;
        public string Code { get; set; } = "";
        /// <summary>Display name of the player who ended it.</summary>
        public string By { get; set; } = "";
    }
}
