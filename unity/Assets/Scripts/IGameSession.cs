using System;
using System.Collections.Generic;
using StiflingDark.Engine.Core;
using StiflingDark.Protocol;

namespace StiflingDark.Unity
{
    /// <summary>
    /// What the table talks to. Online play posts every command to the server
    /// (<see cref="ServerSession"/>); offline play applies it to an in-process game with bots in
    /// every other seat (<see cref="LocalGameSession"/>). Either way the UI renders one per-seat
    /// <see cref="PlayerView"/> and knows nothing else about the rules.
    ///
    /// Deliberately game-only: the menu's games list, create/join/reconnect and abandon have no
    /// offline meaning, so they stay on <see cref="ServerSession"/> and the menu keeps using it
    /// concretely.
    /// </summary>
    public interface IGameSession : IDisposable
    {
        /// <summary>Latest view for our seat; null until the first update lands.</summary>
        PlayerView View { get; }

        /// <summary>Who we are at this table: seat number, role, and Investigator.</summary>
        RoomState Room { get; }

        /// <summary>Seats the game is waiting on right now.</summary>
        IReadOnlyList<int> ActingSeats { get; }

        bool YourTurn { get; }

        /// <summary>This seat's redacted log, oldest first, refused commands included.</summary>
        IReadOnlyList<PlayerView.LogEntry> Log { get; }

        /// <summary>Bumped on every change; the UI re-renders when it moves.</summary>
        int Revision { get; }

        event Action GameUpdated;

        /// <summary>A refused command, in the engine's own words.</summary>
        event Action<string> ErrorReceived;

        void Submit(GameCommand command);

        /// <summary>Rebuild this seat's view and log from scratch.</summary>
        void Resync();

        /// <summary>Drain the socket / advance the bots. Call once a frame.</summary>
        void Pump();
    }
}
