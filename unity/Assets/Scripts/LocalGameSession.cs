using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Bots;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;
using StiflingDark.Protocol;

namespace StiflingDark.Unity
{
    /// <summary>
    /// Offline play: the engine, the bot brains and one human seat, all in this process. No
    /// socket, no server, no persistence — closing the table ends the game.
    ///
    /// The human holds EITHER one Investigator (bot Adversary, bot teammates) OR the Adversary
    /// (an all-bot Investigator team); <see cref="BotTable"/> drives every other seat, the
    /// Adversary's own setup phase included.
    /// </summary>
    public sealed class LocalGameSession : IGameSession
    {
        /// <summary>Seconds between bot steps. A bot step is a whole TURN, so this is the beat
        /// the table moves at — fast enough to keep playing, slow enough to read the log.</summary>
        private const float BotStepSeconds = 0.6f;

        /// <summary>Seconds between replayed bot ACTIONS: a bot turn is snapshotted after
        /// every engine-accepted action and shown to the human at this pace, so their moves
        /// glide space by space instead of the turn appearing all at once.</summary>
        private const float BotReplaySeconds = 0.4f;

        private const string RoomCode = "SOLO";

        private readonly Game _game;
        private readonly BotTable _bots;
        private readonly ViewRole _viewRole;
        private readonly string _myInvestigatorId;
        private readonly bool _teamIsAllBots;
        private readonly List<PlayerView.LogEntry> _log = new List<PlayerView.LogEntry>();

        /// <summary>The Escape shortlist, drawn once (it consumes engine RNG) and held until
        /// the Investigators commit to one of the three. Null while none is owed.</summary>
        private List<string> _escapeChoices;

        private int _logCursor;
        private int _anomaliesLogged;
        private float _nextBotStepTime;

        /// <summary>Per-action snapshots of the bot turn in progress, oldest first.</summary>
        private readonly Queue<PlayerView> _botReplay = new Queue<PlayerView>();

        public RoomState Room { get; } = new RoomState { Code = RoomCode, Started = true };
        public PlayerView View { get; private set; }
        public IReadOnlyList<int> ActingSeats { get; private set; } = new List<int>();
        public bool YourTurn { get; private set; }
        public IReadOnlyList<PlayerView.LogEntry> Log => _log;
        public int Revision { get; private set; }

        public event Action GameUpdated;
        public event Action<string> ErrorReceived;

        /// <param name="investigatorPicks">One entry per Investigator seat (2 to 4 of them), the
        /// human's first when <paramref name="yourRole"/> is Investigator. An empty entry is
        /// drawn at random, as is one naming an Investigator an earlier seat already took.</param>
        public LocalGameSession(GameDatabase db, string scenarioId, string adversaryId,
            SeatRole yourRole, IReadOnlyList<string> investigatorPicks, string yourName,
            ulong seed)
        {
            // Off a different stream than the engine's own RNG, the same split the arena uses,
            // so setup choices and card shuffles do not shadow each other.
            var rng = new DeterministicRng(seed ^ 0xA24BAED4963EE407UL);
            var roster = ChooseRoster(db, rng, investigatorPicks);

            var map = db.Map(scenarioId);
            var startSpaces = ChooseStartSpaces(map, rng, roster);
            var medicalPool = map.Spaces.Where(s => s.Kind == SpaceKind.MedicalItem)
                .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            rng.Shuffle(medicalPool);

            _game = Game.NewGame(db, new GameSetup
            {
                ScenarioId = scenarioId,
                Seed = seed,
                AdversaryId = adversaryId,
                InvestigatorStartSpaces = startSpaces,
                MedicalItemSpaces = medicalPool
                    .Take(db.Config.ByInvestigatorCount[roster.Count].MedicalItemsOnBoard)
                    .ToList(),
                // Short-handed starting Items all go to the human's Investigator; on an
                // Adversary run the all-bot team defaults to its first seat.
                StartingItemsInvestigatorId = yourRole == SeatRole.Adversary ? null : roster[0],
            });

            _teamIsAllBots = yourRole == SeatRole.Adversary;
            _viewRole = _teamIsAllBots ? ViewRole.Adversary : ViewRole.Investigator;
            _myInvestigatorId = _teamIsAllBots ? "" : roster[0];
            Room.Seats = BuildSeats(yourRole, yourName, roster);
            Room.YourSeat = 0;

            _bots = new BotTable(_game, seed,
                Room.Seats.Where(s => s.Fill == SeatFill.Bot && s.Role == SeatRole.Investigator)
                    .Select(s => s.InvestigatorId),
                Room.Seats.Any(s => s.Fill == SeatFill.Bot && s.Role == SeatRole.Adversary),
                startSpaces.Values);
            _bots.AfterAction = () => _botReplay.Enqueue(BuildView());

            Refresh();
        }

        // ---------------------------------------------------------------- setup

        /// <summary>Seats keep the Investigator they asked for and hold their order — the human's
        /// pick is first, so it takes seat 0 — while blanks draw from whoever is left.</summary>
        private static List<string> ChooseRoster(GameDatabase db, DeterministicRng rng,
            IReadOnlyList<string> picks)
        {
            var pool = db.Investigators.Where(i => i.Set == "base").Select(i => i.Id)
                .OrderBy(id => id, StringComparer.Ordinal).ToList();
            rng.Shuffle(pool);
            var roster = new List<string>();
            foreach (string pick in picks)
            {
                roster.Add(!string.IsNullOrEmpty(pick) && !roster.Contains(pick) ? pick : null);
            }
            for (int seat = 0; seat < roster.Count; seat++)
            {
                if (roster[seat] == null)
                {
                    roster[seat] = pool.First(id => !roster.Contains(id));
                }
            }
            return roster;
        }

        private static Dictionary<string, string> ChooseStartSpaces(MapDef map,
            DeterministicRng rng, List<string> roster)
        {
            var starts = map.Spaces.Where(s => s.Kind == SpaceKind.Start)
                .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();
            rng.Shuffle(starts);
            var startSpaces = new Dictionary<string, string>();
            for (int i = 0; i < roster.Count; i++)
            {
                startSpaces[roster[i]] = starts[i % starts.Count];
            }
            return startSpaces;
        }

        /// <summary>Seat 0 is always the human; the bots take the rest in roster order.</summary>
        private static List<SeatInfo> BuildSeats(SeatRole yourRole, string yourName,
            List<string> roster)
        {
            var seats = new List<SeatInfo>
            {
                new SeatInfo
                {
                    Seat = 0,
                    Name = string.IsNullOrEmpty(yourName) ? "You" : yourName,
                    Role = yourRole,
                    Fill = SeatFill.Human,
                    InvestigatorId = yourRole == SeatRole.Adversary ? "" : roster[0],
                    Connected = true,
                    Ready = true,
                },
            };
            int firstBotInvestigator = yourRole == SeatRole.Adversary ? 0 : 1;
            for (int i = firstBotInvestigator; i < roster.Count; i++)
            {
                seats.Add(new SeatInfo
                {
                    Seat = seats.Count,
                    Name = "Bot " + roster[i],
                    Role = SeatRole.Investigator,
                    Fill = SeatFill.Bot,
                    InvestigatorId = roster[i],
                    Connected = true,
                    Ready = true,
                });
            }
            if (yourRole != SeatRole.Adversary)
            {
                seats.Add(new SeatInfo
                {
                    Seat = seats.Count,
                    Name = "Bot Adversary",
                    Role = SeatRole.Adversary,
                    Fill = SeatFill.Bot,
                    Connected = true,
                    Ready = true,
                });
            }
            return seats;
        }

        // -------------------------------------------------------------- commands

        public void Submit(GameCommand command)
        {
            var required = Room.YourSeatInfo.Role == SeatRole.Adversary
                ? CommandSide.Adversary
                : CommandSide.Investigator;
            if (command.Side != required)
            {
                Refuse("That is an " + command.Side + " command and you hold the " +
                    Room.YourSeatInfo.Role + " seat.");
                return;
            }
            try
            {
                command.Apply(_game);
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
            {
                Refuse(e.Message);
                return;
            }
            if (command is DrawEscapeChoicesCommand draw)
            {
                _escapeChoices = draw.Choices;
            }
            if (_game.State.Objective.SelectedEscapeCard != null)
            {
                _escapeChoices = null; // the shortlist is spent
            }
            Refresh();
        }

        public void Resync() => Refresh();

        public void Pump()
        {
            // A bot turn in progress is replayed one snapshot at a time before any new bot
            // work happens, so the human watches every action land instead of a whole turn
            // appearing at once.
            if (_botReplay.Count > 0)
            {
                if (UnityEngine.Time.time < _nextBotStepTime)
                {
                    return;
                }
                _nextBotStepTime = UnityEngine.Time.time + BotReplaySeconds;
                Publish(_botReplay.Dequeue());
                return;
            }
            if (_game.State.Phase == GamePhase.GameOver ||
                UnityEngine.Time.time < _nextBotStepTime)
            {
                return;
            }
            _nextBotStepTime = UnityEngine.Time.time + BotStepSeconds;

            bool changed = DrawEscapeChoicesIfDue();
            try
            {
                changed |= _bots.TryStep();
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
            {
                // A bot walked into a rule it misread. Recording it beats retrying forever.
                _bots.Anomalies.Add("bot-step-failed: " + e.Message);
            }
            LogNewBotAnomalies();
            if (changed)
            {
                // The final state closes the replay; anything the turn snapshotted plays
                // out first at BotReplaySeconds.
                _botReplay.Enqueue(BuildView());
                _nextBotStepTime = 0f;
            }
        }

        /// <summary>
        /// A human Investigator team gets its three-card Escape shortlist drawn for it the
        /// moment the Evidence gate is met; committing to one of the three stays their decision.
        /// An all-bot team draws and picks inside <see cref="BotTable"/> instead.
        /// </summary>
        private bool DrawEscapeChoicesIfDue()
        {
            if (_teamIsAllBots || _escapeChoices != null ||
                _game.State.Objective.SelectedEscapeCard != null)
            {
                return false;
            }
            try
            {
                _escapeChoices = _game.DrawEscapeChoices().ToList();
                return true;
            }
            catch (InvalidOperationException)
            {
                return false; // the gate is not open yet; ask again next step
            }
        }

        // -------------------------------------------------------------- updates

        private PlayerView BuildView() =>
            _game.ViewFor(_viewRole, _myInvestigatorId, _escapeChoices);

        /// <summary>A human action jumps straight to the present: any bot-turn snapshots
        /// still queued for replay are behind it now and would render backwards.</summary>
        private void Refresh()
        {
            _botReplay.Clear();
            Publish(BuildView());
        }

        private void Publish(PlayerView view)
        {
            View = view;
            for (int i = _logCursor; i < view.Log.Count; i++)
            {
                _log.Add(view.Log[i]);
            }
            _logCursor = view.Log.Count;
            ActingSeats = ComputeActingSeats(view);
            YourTurn = ActingSeats.Contains(Room.YourSeat);
            Revision++;
            GameUpdated?.Invoke();
        }

        /// <summary>
        /// Seats the game is waiting on in this VIEW — the view rather than live state, so a
        /// replayed bot-turn snapshot doesn't flip YourTurn on before the replay finishes.
        /// During Investigator turns that is the seat whose turn is open, or — between turns
        /// — every Investigator who has not gone yet, because turn order inside the round is
        /// the team's to choose.
        /// </summary>
        private List<int> ComputeActingSeats(PlayerView view)
        {
            switch (view.Phase)
            {
                case GamePhase.AdversarySetup:
                case GamePhase.AdversaryTurn:
                    return Room.Seats.Where(s => s.Role == SeatRole.Adversary)
                        .Select(s => s.Seat).ToList();

                case GamePhase.InvestigatorTurns:
                    if (view.ActiveInvestigator != null)
                    {
                        return Room.Seats
                            .Where(s => s.InvestigatorId == view.ActiveInvestigator)
                            .Select(s => s.Seat).ToList();
                    }
                    var pending = view.Investigators
                        .Where(i => !i.TurnTakenThisRound && !i.Escaped &&
                                    (!i.Dead || i.SpiritId != null))
                        .Select(i => i.DefId).ToList();
                    return Room.Seats.Where(s => pending.Contains(s.InvestigatorId))
                        .Select(s => s.Seat).ToList();

                default:
                    return new List<int>();
            }
        }

        /// <summary>Bot confusion is a designer's bug report, so it lands in the log rather
        /// than staying inside <see cref="BotTable"/> where nobody would read it.</summary>
        private void LogNewBotAnomalies()
        {
            for (int i = _anomaliesLogged; i < _bots.Anomalies.Count; i++)
            {
                AddLogLine("error", "bot: " + _bots.Anomalies[i]);
            }
            _anomaliesLogged = _bots.Anomalies.Count;
        }

        private void Refuse(string message)
        {
            AddLogLine("error", message);
            Revision++;
            ErrorReceived?.Invoke(message);
        }

        private void AddLogLine(string type, string detail) => _log.Add(new PlayerView.LogEntry
        {
            Round = View?.Round ?? 0,
            Type = type,
            Detail = detail,
        });

        public void Dispose()
        {
        }
    }
}
