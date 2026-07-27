using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The lobby: the seat list, the host's table controls, the Investigator picker, and Start.
    /// Rebuilt from every <c>room</c> message, because the server re-indexes seats when one is
    /// removed and a cached seat number would lie.
    /// </summary>
    public sealed class LobbyUi
    {
        private readonly ServerSession _session;
        private readonly Describe _describe;
        private readonly RectTransform _root;
        private readonly TMP_Text _title;
        private readonly RectTransform _seatList;
        private readonly RectTransform _controls;
        private readonly TMP_Text _status;
        private int _renderedRevision = -1;

        public Action LeaveRequested;

        public LobbyUi(Transform canvas, ServerSession session, Describe describe)
        {
            _session = session;
            _describe = describe;

            _root = UiKit.CreatePanel(canvas, "Lobby", new Color(0.03f, 0.035f, 0.045f, 1f));
            UiKit.Anchor(_root, Vector2.zero, Vector2.one);

            _title = UiKit.CreateText(_root, "", 30, TextAnchor.MiddleLeft, UiKit.AccentColor);
            UiKit.Anchor((RectTransform)_title.transform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(60, -110), new Vector2(-60, -46));

            _status = UiKit.CreateText(_root, "", 16, TextAnchor.MiddleLeft, UiKit.MutedColor);
            UiKit.Anchor((RectTransform)_status.transform, new Vector2(0, 1), new Vector2(1, 1),
                new Vector2(60, -142), new Vector2(-60, -112));

            var seatPanel = UiKit.CreatePanel(_root, "Seats", UiKit.PanelColor);
            UiKit.Anchor(seatPanel, new Vector2(0, 0), new Vector2(0.5f, 1),
                new Vector2(60, 96), new Vector2(-12, -150));
            _seatList = UiKit.CreateScrollList(seatPanel, 6f);

            var controlPanel = UiKit.CreatePanel(_root, "Controls", UiKit.PanelColor);
            UiKit.Anchor(controlPanel, new Vector2(0.5f, 0), new Vector2(1, 1),
                new Vector2(12, 96), new Vector2(-60, -150));
            _controls = UiKit.CreateScrollList(controlPanel, 4f);

            var footer = UiKit.CreateRow(_root, "Footer", 10f, 44f);
            UiKit.Anchor(footer, Vector2.zero, new Vector2(1, 0),
                new Vector2(60, 26), new Vector2(-60, 88));
            UiKit.CreateButton(footer, "START GAME", 20, () => _session.StartGame());
            UiKit.CreateButton(footer, "Ready", 17, () => _session.SetReady(true));
            UiKit.CreateButton(footer, "Not ready", 17, () => _session.SetReady(false));
            UiKit.CreateButton(footer, "Leave table", 17, () =>
            {
                _session.LeaveRoom();
                LeaveRequested?.Invoke();
            });
        }

        public void SetActive(bool active) => _root.gameObject.SetActive(active);

        /// <summary>Tear the lobby down — used when a reconnect replaces the session.</summary>
        public void Destroy() => UnityEngine.Object.Destroy(_root.gameObject);

        public void Tick()
        {
            if (!_root.gameObject.activeSelf || _session.Revision == _renderedRevision)
            {
                return;
            }
            _renderedRevision = _session.Revision;
            Render();
        }

        private void Render()
        {
            var room = _session.Room;
            _title.text = "Room " + room.Code + "     " +
                Describe.Scenario(room.Setup.ScenarioId) + "  vs  " +
                Describe.Adversary(room.Setup.AdversaryId);
            _status.text = "You are seat " + room.YourSeat +
                (room.YouAreHost ? " (host — only you can change the table)" : "") +
                "     bot speed: " + room.Speed +
                "     " + room.InvestigatorSeats + " Investigator seat(s), " +
                (room.HasAdversary ? "Adversary seated" : "NO ADVERSARY YET");

            RenderSeats(room);
            RenderControls(room);
        }

        private void RenderSeats(RoomState room)
        {
            UiKit.Clear(_seatList);
            foreach (var seat in room.Seats.OrderBy(s => s.Seat))
            {
                var captured = seat;
                bool mine = seat.Seat == room.YourSeat;
                var card = UiKit.CreatePanel(_seatList, "Seat" + seat.Seat,
                    mine ? new Color(0.16f, 0.15f, 0.10f, 0.95f) : UiKit.PanelSoft);
                var layout = card.gameObject.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(12, 12, 8, 8);
                layout.spacing = 2;
                layout.childControlHeight = true;
                layout.childForceExpandHeight = false;

                string role = seat.Role == SeatRole.Adversary ? "ADVERSARY" : "Investigator";
                string who = seat.Role == SeatRole.Adversary
                    ? Describe.Adversary(room.Setup.AdversaryId)
                    : _describe.Investigator(seat.InvestigatorId);
                Row(card, "Seat " + seat.Seat + "  ·  " + role + (mine ? "   ← you" : ""), 18,
                    mine ? UiKit.AccentColor : UiKit.TextColor);
                Row(card, who, 16);
                Row(card, seat.Name +
                    (seat.Fill == SeatFill.Bot ? "  ·  bot" : "  ·  human") +
                    (seat.Connected ? "" : "  ·  disconnected") +
                    (seat.Ready ? "  ·  ready" : "  ·  not ready"), 14, UiKit.MutedColor);

                if (room.YouAreHost && !room.Started)
                {
                    var buttons = UiKit.CreateRow(card, "SeatButtons", 6f, 30f);
                    if (seat.Seat != 0)
                    {
                        UiKit.CreateButton(buttons, "Remove", 14,
                            () => _session.RemoveSeat(captured.Seat));
                    }
                    if (seat.Fill == SeatFill.Human && !seat.Connected)
                    {
                        UiKit.CreateButton(buttons, "Make bot", 14,
                            () => _session.SetSeat(captured.Seat, fill: SeatFill.Bot));
                    }
                    if (seat.Role == SeatRole.Investigator)
                    {
                        UiKit.CreateButton(buttons, "Change Investigator", 14,
                            () => ShowInvestigatorPicker(captured.Seat));
                    }
                }
            }
        }

        private void RenderControls(RoomState room)
        {
            UiKit.Clear(_controls);
            if (room.Started)
            {
                Head("The game has started.");
                return;
            }
            if (!room.YouAreHost)
            {
                Head("Waiting for the host");
                Row(_controls, "Only seat 0 may add bots, pick the scenario, or start.", 15,
                    UiKit.MutedColor);
                Head("Your Investigator");
                Row(_controls, "Ask the host to change it — set_seat is host-only.", 14,
                    UiKit.MutedColor);
                return;
            }

            Head("ADD A SEAT");
            UiKit.CreateButton(_controls, "+  Bot Investigator", 17,
                () => _session.AddBot(SeatRole.Investigator));
            UiKit.CreateButton(_controls, "+  Bot Adversary", 17,
                () => _session.AddBot(SeatRole.Adversary), !room.HasAdversary,
                "This table already has an Adversary.");

            Head("YOUR INVESTIGATOR");
            var yours = room.YourSeatInfo;
            if (yours != null && yours.Role == SeatRole.Investigator)
            {
                foreach (var def in _describe.BaseInvestigators)
                {
                    var captured = def;
                    bool taken = room.Seats.Any(s =>
                        s.Seat != room.YourSeat && s.InvestigatorId == captured.Id);
                    bool current = yours.InvestigatorId == captured.Id;
                    UiKit.CreateButton(_controls,
                        (current ? "●  " : "○  ") + captured.Name + "   ·   MP " + captured.Mp +
                        "   ·   " + captured.MinorAbility.Name + " / " + captured.MajorAbility.Name,
                        15,
                        () => _session.SetSeat(room.YourSeat, investigatorId: captured.Id),
                        !taken && !current,
                        taken ? "Another seat already plays " + captured.Name + "." : "Already yours.");
                }
            }

            Head("SCENARIO");
            foreach (string scenario in Options(room.Setup.AvailableScenarios, "sawmill",
                "amusement-park"))
            {
                string captured = scenario;
                UiKit.CreateButton(_controls,
                    (room.Setup.ScenarioId == captured ? "●  " : "○  ") + Describe.Scenario(captured),
                    16, () => _session.Configure(scenarioId: captured));
            }

            Head("ADVERSARY");
            foreach (string adversary in Options(room.Setup.AvailableAdversaries, "butcher",
                "cult-of-hunlow", "insatiable-horror"))
            {
                string captured = adversary;
                UiKit.CreateButton(_controls,
                    (room.Setup.AdversaryId == captured ? "●  " : "○  ") +
                    Describe.Adversary(captured),
                    16, () => _session.Configure(adversaryId: captured));
            }

            Head("BOT SPEED");
            var speeds = UiKit.CreateRow(_controls, "Speeds", 6f, 32f);
            foreach (string speed in new[] { "slow", "medium", "fast" })
            {
                string captured = speed;
                UiKit.CreateButton(speeds, captured, 15, () => _session.SetSpeed(captured));
            }

            Head("OPTIONS");
            UiKit.CreateButton(_controls,
                (room.Setup.UseMiniExpansionCards ? "●" : "○") + "  Mini-Expansion cards", 15,
                () => _session.Configure(
                    useMiniExpansionCards: !room.Setup.UseMiniExpansionCards));

            Head("START SPACES / MEDICAL ITEMS");
            Row(_controls, "Left unset the server fills these in from the map's Start and " +
                "Medical spaces, which is what you want unless you are reproducing a specific " +
                "setup. Configured: " +
                (room.Setup.StartSpaces.Count == 0
                    ? "none"
                    : string.Join(", ", room.Setup.StartSpaces.Select(s => s.Key + "@" + s.Value))) +
                "   ·   medical: " +
                (room.Setup.MedicalItemSpaces.Count == 0
                    ? "auto"
                    : string.Join(", ", room.Setup.MedicalItemSpaces)), 14, UiKit.MutedColor);
        }

        private void ShowInvestigatorPicker(int seat)
        {
            // Host-side reassignment of any Investigator seat, bot seats included.
            var room = _session.Room;
            UiKit.Clear(_controls);
            Head("SEAT " + seat + " PLAYS…");
            foreach (var def in _describe.BaseInvestigators)
            {
                var captured = def;
                bool taken = room.Seats.Any(s => s.Seat != seat && s.InvestigatorId == captured.Id);
                UiKit.CreateButton(_controls, captured.Name, 16,
                    () => _session.SetSeat(seat, investigatorId: captured.Id), !taken,
                    "Another seat already plays " + captured.Name + ".");
            }
            UiKit.CreateButton(_controls, "Back", 16, Render);
        }

        /// <summary>The server's own list when it sent one, else the known ids.</summary>
        private static IEnumerable<string> Options(List<string> fromServer, params string[] fallback) =>
            fromServer != null && fromServer.Count > 0 ? fromServer : fallback;

        private void Head(string title)
        {
            var text = UiKit.CreateText(_controls, title, 13, TextAnchor.MiddleLeft, UiKit.AccentColor);
            text.gameObject.AddComponent<LayoutElement>().minHeight = 28;
        }

        private static void Row(Transform parent, string content, int size, Color? color = null)
        {
            var text = UiKit.CreateText(parent, content, size, TextAnchor.UpperLeft, color);
            int lines = Mathf.Max(1, Mathf.CeilToInt(content.Length / Mathf.Max(8f, 620f / (size * 0.52f))));
            text.gameObject.AddComponent<LayoutElement>().minHeight = lines * (size + 4) + 4;
        }
    }
}
