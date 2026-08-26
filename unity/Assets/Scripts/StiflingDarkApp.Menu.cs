using System;
using StiflingDark.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The menu stage and its three screens. Each is built once and then shown or hidden;
    /// only the scrolling bodies are re-rendered, so what the player typed survives.
    /// </summary>
    public sealed partial class StiflingDarkApp
    {
        private enum MenuScreen
        {
            Root,
            Online,
            Solo,
        }

        private RectTransform _menu;
        private RectTransform _rootScreen;
        private RectTransform _onlineScreen;
        private RectTransform _soloScreen;
        private MenuScreen _menuScreen = MenuScreen.Root;

        private TMP_Text _rootStatus;
        private TMP_Text _menuStatus;
        private RectTransform _menuBody;
        private RectTransform _soloBody;
        private TMP_InputField _nameInput;
        private TMP_InputField _soloNameInput;
        private TMP_InputField _serverInput;
        private TMP_InputField _codeInput;
        private TMP_Text _toast;
        private float _toastUntil;
        private int _menuRevision = -1;
        /// <summary>Room code whose My Games × is one click from ending the game.</summary>
        private string _confirmEndCode = "";

        // ---- offline setup, as the solo screen has it dialled in
        private string _soloScenario = "sawmill";
        private string _soloAdversary = "butcher";
        private SeatRole _soloRole = SeatRole.Investigator;
        private string _soloInvestigator = "";
        private int _soloInvestigatorCount = 3;

        // --------------------------------------------------------------- build

        private void BuildMenu(Transform canvas)
        {
            _menu = UiKit.CreatePanel(canvas, "Menu", new Color(0.03f, 0.035f, 0.045f, 1f));
            UiKit.Anchor(_menu, Vector2.zero, Vector2.one);

            BuildRootScreen();
            BuildOnlineScreen();
            BuildSoloScreen();

            // Last child, so the toast draws over whichever screen is up.
            _toast = UiKit.CreateText(_menu, "", 18, TextAnchor.MiddleCenter, UiKit.AccentColor);
            UiKit.Anchor((RectTransform)_toast.transform, Vector2.zero, new Vector2(1, 0),
                new Vector2(80, 16), new Vector2(-80, 52));

            ShowMenuScreen(MenuScreen.Root);
        }

        private void BuildRootScreen()
        {
            _rootScreen = UiKit.CreateGroup(_menu, "MenuRoot");
            UiKit.Anchor(_rootScreen, Vector2.zero, Vector2.one);

            var title = UiKit.CreateText(_rootScreen, "THE STIFLING DARK", 84,
                TextAnchor.MiddleCenter, UiKit.AccentColor);
            title.font = UiKit.TitleFont;
            UiKit.Anchor((RectTransform)title.transform, new Vector2(0, 0.72f), new Vector2(1, 0.88f));
            var subtitle = UiKit.CreateText(_rootScreen,
                "Keep your friends close, and your flashlight closer", 16, TextAnchor.MiddleCenter,
                UiKit.MutedColor);
            UiKit.Anchor((RectTransform)subtitle.transform, new Vector2(0, 0.66f), new Vector2(1, 0.72f));

            var column = MenuColumn(_rootScreen, "RootColumn",
                new Vector2(0.34f, 0.34f), new Vector2(0.66f, 0.60f), 14);
            UiKit.CreateButton(column, "Play vs Bots", 24, OpenSoloScreen)
                .GetComponent<LayoutElement>().minHeight = 64;
            UiKit.CreateButton(column, "Join with a room code", 24,
                () => ShowMenuScreen(MenuScreen.Online))
                .GetComponent<LayoutElement>().minHeight = 64;

            _rootStatus = UiKit.CreateText(_rootScreen, "", 15, TextAnchor.UpperCenter,
                UiKit.MutedColor);
            UiKit.Anchor((RectTransform)_rootStatus.transform,
                new Vector2(0.2f, 0.12f), new Vector2(0.8f, 0.30f));
        }

        private void BuildOnlineScreen()
        {
            _onlineScreen = UiKit.CreateGroup(_menu, "MenuOnline");
            UiKit.Anchor(_onlineScreen, Vector2.zero, Vector2.one);
            CreateBackButton(_onlineScreen);
            CreateHeading(_onlineScreen, "PLAY ONLINE",
                "Start a table of your own, or join one with its room code");

            var column = MenuColumn(_onlineScreen, "OnlineColumn",
                new Vector2(0.30f, 0.06f), new Vector2(0.70f, 0.79f), 8);

            UiKit.CreateText(column, "Your name", 14, TextAnchor.MiddleLeft, UiKit.MutedColor)
                .gameObject.AddComponent<LayoutElement>().minHeight = 20;
            _nameInput = UiKit.CreateInput(column, "name", RememberedPlayerName());
            UiKit.CreateText(column, "Server", 14, TextAnchor.MiddleLeft, UiKit.MutedColor)
                .gameObject.AddComponent<LayoutElement>().minHeight = 20;
            _serverInput = UiKit.CreateInput(column, DefaultServer, LoadServerUrl());

            UiKit.CreateButton(column, "Connect", 18, Connect)
                .GetComponent<LayoutElement>().minHeight = 44;
            UiKit.CreateButton(column, "Use localhost", 18, () =>
            {
                _serverInput.text = DefaultServer;
                Connect();
            }).GetComponent<LayoutElement>().minHeight = 44;

            _menuStatus = UiKit.CreateText(column, "", 15, TextAnchor.MiddleCenter, UiKit.MutedColor);
            _menuStatus.gameObject.AddComponent<LayoutElement>().minHeight = 28;

            var actions = UiKit.CreatePanel(column, "Actions", UiKit.PanelColor);
            var actionsElement = actions.gameObject.AddComponent<LayoutElement>();
            actionsElement.flexibleHeight = 1;
            actionsElement.minHeight = 120;
            _menuBody = UiKit.CreateScrollList(actions, 6f);
        }

        private void BuildSoloScreen()
        {
            _soloScreen = UiKit.CreateGroup(_menu, "MenuSolo");
            UiKit.Anchor(_soloScreen, Vector2.zero, Vector2.one);
            CreateBackButton(_soloScreen);
            CreateHeading(_soloScreen, "PLAY VS BOTS",
                "Offline — one human seat, bots in all the others. Nothing is saved.");

            var column = MenuColumn(_soloScreen, "SoloColumn",
                new Vector2(0.26f, 0.06f), new Vector2(0.74f, 0.79f), 8);

            UiKit.CreateText(column, "Your name", 14, TextAnchor.MiddleLeft, UiKit.MutedColor)
                .gameObject.AddComponent<LayoutElement>().minHeight = 20;
            _soloNameInput = UiKit.CreateInput(column, "name", RememberedPlayerName());

            var setup = UiKit.CreatePanel(column, "Setup", UiKit.PanelColor);
            var setupElement = setup.gameObject.AddComponent<LayoutElement>();
            setupElement.flexibleHeight = 1;
            setupElement.minHeight = 200;
            _soloBody = UiKit.CreateScrollList(setup, 6f);
        }

        /// <summary>The centred stack of full-width rows every menu screen is laid out in.</summary>
        private static RectTransform MenuColumn(RectTransform screen, string name,
            Vector2 anchorMin, Vector2 anchorMax, float spacing)
        {
            var column = UiKit.CreateGroup(screen, name);
            UiKit.Anchor(column, anchorMin, anchorMax);
            var layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = spacing;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            return column;
        }

        private void CreateBackButton(RectTransform screen)
        {
            var back = UiKit.CreateButton(screen, "←  Back", 18,
                () => ShowMenuScreen(MenuScreen.Root));
            UiKit.Anchor((RectTransform)back.transform,
                new Vector2(0.02f, 0.90f), new Vector2(0.10f, 0.955f));
        }

        private static void CreateHeading(RectTransform screen, string title, string note)
        {
            var heading = UiKit.CreateText(screen, title, 44, TextAnchor.MiddleCenter,
                UiKit.AccentColor);
            UiKit.Anchor((RectTransform)heading.transform,
                new Vector2(0, 0.87f), new Vector2(1, 0.96f));
            var subtitle = UiKit.CreateText(screen, note, 15, TextAnchor.MiddleCenter,
                UiKit.MutedColor);
            UiKit.Anchor((RectTransform)subtitle.transform,
                new Vector2(0, 0.82f), new Vector2(1, 0.87f));
        }

        // ---------------------------------------------------------- navigation

        private void ShowMenuScreen(MenuScreen screen)
        {
            RememberPlayerName();
            _menuScreen = screen;
            _rootScreen.gameObject.SetActive(screen == MenuScreen.Root);
            _onlineScreen.gameObject.SetActive(screen == MenuScreen.Online);
            _soloScreen.gameObject.SetActive(screen == MenuScreen.Solo);

            if (screen == MenuScreen.Online)
            {
                _nameInput.text = RememberedPlayerName();
            }
            else if (screen == MenuScreen.Solo)
            {
                _soloNameInput.text = RememberedPlayerName();
            }
            _confirmEndCode = "";
            // The body that is now on screen belongs to the screen we just left.
            _menuRevision = -1;
        }

        private void OpenSoloScreen()
        {
            if (_describe == null)
            {
                Toast("game-data did not load — offline play needs it.");
                return;
            }
            if (_soloInvestigator.Length == 0 && _describe.BaseInvestigators.Count > 0)
            {
                _soloInvestigator = _describe.BaseInvestigators[0].Id;
            }
            ShowMenuScreen(MenuScreen.Solo);
        }

        private static string RememberedPlayerName() =>
            PlayerPrefs.GetString(PrefName, DefaultPlayerName);

        /// <summary>
        /// Online and offline each carry their own name field, so the one being left hands its
        /// value to the other through PlayerPrefs rather than letting the two drift apart.
        /// </summary>
        private void RememberPlayerName()
        {
            var field = _menuScreen == MenuScreen.Solo ? _soloNameInput : _nameInput;
            string typed = (field?.text ?? "").Trim();
            if (typed.Length == 0)
            {
                return;
            }
            PlayerPrefs.SetString(PrefName, typed);
            PlayerPrefs.Save();
        }

        private string NameOrDefault()
        {
            var field = _menuScreen == MenuScreen.Solo ? _soloNameInput : _nameInput;
            string name = (field?.text ?? "").Trim();
            return name.Length == 0 ? "Player" : name;
        }

        private void Toast(string message)
        {
            if (_toast != null)
            {
                _toast.text = message;
                _toastUntil = Time.time + 6f;
            }
        }

        private void TickToast()
        {
            if (_toast != null && _toastUntil > 0f && Time.time > _toastUntil)
            {
                _toast.text = "";
                _toastUntil = 0f;
            }
        }

        // -------------------------------------------------------------- render

        private void RenderMenu()
        {
            switch (_menuScreen)
            {
                case MenuScreen.Online:
                    RenderOnlineScreen();
                    break;
                case MenuScreen.Solo:
                    RenderSoloScreen();
                    break;
                default:
                    _rootStatus.text = _loadError.Length > 0 ? DataLoadErrorText : "";
                    break;
            }
        }

        private string DataLoadErrorText =>
            "game-data did not load:\n" + _loadError +
            "\n\nRun tools/sync_unity.sh, then reopen the project.";

        private void RenderOnlineScreen()
        {
            // The body is rebuilt from scratch every revision, so a half-typed code has to be
            // carried across the rebuild by hand.
            string typedCode = _codeInput == null ? "" : _codeInput.text;
            UiKit.Clear(_menuBody);
            if (_loadError.Length > 0)
            {
                _menuStatus.text = DataLoadErrorText;
                return;
            }
            if (_session == null)
            {
                _menuStatus.text = "Not connected.";
                return;
            }
            _menuStatus.text = _session.Connected
                ? (_session.Greeted
                    ? "Connected as " + _session.PlayerId
                    : "Connected — identifying…")
                : _session.ConnectionError.Length > 0
                    ? "Not connected: " + _session.ConnectionError
                    : "Connecting to " + _session.Url + "…";

            if (!_session.Greeted)
            {
                return;
            }

            Head(_menuBody, "NEW TABLE");
            UiKit.CreateButton(_menuBody, "Create a room — I play an Investigator", 18,
                () => _session.CreateRoom(NameOrDefault(), SeatRole.Investigator));
            UiKit.CreateButton(_menuBody, "Create a room — I play the Adversary", 18,
                () => _session.CreateRoom(NameOrDefault(), SeatRole.Adversary));

            Head(_menuBody, "JOIN BY CODE");
            _codeInput = UiKit.CreateInput(_menuBody, "5-letter room code", typedCode);
            UiKit.CreateButton(_menuBody, "Join as Investigator", 17,
                () => Join(SeatRole.Investigator));
            UiKit.CreateButton(_menuBody, "Join as Adversary", 17, () => Join(SeatRole.Adversary));

            string lastRoom = PlayerPrefs.GetString(PrefLastRoom, "");
            if (lastRoom.Length > 0)
            {
                UiKit.CreateButton(_menuBody, "Rejoin " + lastRoom + " (with saved token)", 17,
                    () => _session.JoinRoom(lastRoom, NameOrDefault(),
                        PlayerPrefs.GetString("sd_token_" + lastRoom, ""), SeatRole.Investigator));
            }

            Head(_menuBody, "MY GAMES");
            if (_session.GamesList.Count == 0)
            {
                UiKit.CreateText(_menuBody, "(none yet)", 15, TextAnchor.MiddleLeft, UiKit.MutedColor)
                    .gameObject.AddComponent<LayoutElement>().minHeight = 24;
            }
            foreach (var summary in _session.GamesList)
            {
                CreateGameRow(summary);
            }
            UiKit.CreateButton(_menuBody, "Refresh My Games", 15, () =>
            {
                _confirmEndCode = "";
                _session.ListGames();
            });
        }

        private void CreateGameRow(GameSummary summary)
        {
            var captured = summary;
            string label = captured.Code + "   " +
                Describe.Scenario(captured.ScenarioId) + " vs " +
                Describe.Adversary(captured.AdversaryId) +
                "   round " + captured.Round +
                (captured.Finished ? "   FINISHED" : captured.YourTurn ? "   ← YOUR TURN" : "") +
                "   (" + string.Join(", ", captured.Players) + ")";
            var row = UiKit.CreateRow(_menuBody, "Game " + captured.Code);
            var join = UiKit.CreateButton(row, label, 15, () => _session.JoinRoom(captured.Code,
                NameOrDefault(), PlayerPrefs.GetString("sd_token_" + captured.Code, ""),
                captured.YourRole));
            var joinLayout = join.GetComponent<LayoutElement>();
            joinLayout.flexibleWidth = 1;
            // That label runs to ~100 characters, so the label-derived preferred width is
            // far wider than the row. When preferred widths do not fit, a
            // HorizontalLayoutGroup shrinks EVERY child toward its minWidth — which is how
            // the × ended up a thin red sliver. Ask for a modest slice and let
            // flexibleWidth do the filling: the row's preferred total then fits, so the ×
            // gets exactly its own (fixed) width and this button still spans the rest.
            joinLayout.preferredWidth = 240f;
            // End-for-everyone, two clicks: the × arms, "end?" fires. Any re-render
            // (refresh, another row's ×) disarms it again.
            bool armed = _confirmEndCode == captured.Code;
            UiKit.CreateButton(row, armed ? "end?" : "×", 15, () =>
            {
                if (_confirmEndCode == captured.Code)
                {
                    _confirmEndCode = "";
                    _session.AbandonGame(captured.Code);
                    Toast("Ending game " + captured.Code + " for everyone…");
                }
                else
                {
                    _confirmEndCode = captured.Code;
                    RenderMenu();
                }
            }, danger: true,
                // A square icon while idle, a little wider once armed so "end?" reads.
                // fixedWidth pins minWidth too, so neither state can be squeezed flat.
                fixedWidth: armed ? 56f : 34f);
        }

        private void Join(SeatRole role)
        {
            string code = (_codeInput?.text ?? "").Trim().ToUpperInvariant();
            if (code.Length == 0)
            {
                Toast("Enter a room code first.");
                return;
            }
            _session.JoinRoom(code, NameOrDefault(),
                PlayerPrefs.GetString("sd_token_" + code, ""), role);
        }

        // ----------------------------------------------------------- solo setup

        private void RenderSoloScreen()
        {
            UiKit.Clear(_soloBody);
            if (_describe == null)
            {
                return;
            }

            Head(_soloBody, "SCENARIO");
            foreach (string scenario in new[] { "sawmill", "amusement-park" })
            {
                string captured = scenario;
                Choice(Describe.Scenario(captured), _soloScenario == captured,
                    () => _soloScenario = captured);
            }

            Head(_soloBody, "ADVERSARY");
            foreach (string adversary in new[] { "butcher", "cult-of-hunlow", "insatiable-horror" })
            {
                string captured = adversary;
                Choice(Describe.Adversary(captured), _soloAdversary == captured,
                    () => _soloAdversary = captured);
            }

            Head(_soloBody, "YOU PLAY");
            Choice("An Investigator", _soloRole == SeatRole.Investigator,
                () => _soloRole = SeatRole.Investigator);
            Choice("The Adversary", _soloRole == SeatRole.Adversary,
                () => _soloRole = SeatRole.Adversary);

            if (_soloRole == SeatRole.Investigator)
            {
                Head(_soloBody, "YOUR INVESTIGATOR");
                foreach (var def in _describe.BaseInvestigators)
                {
                    var captured = def;
                    Choice(captured.Name + "   ·   MP " + captured.Mp + "   ·   " +
                        captured.MinorAbility.Name + " / " + captured.MajorAbility.Name,
                        _soloInvestigator == captured.Id, () => _soloInvestigator = captured.Id);
                }
            }

            Head(_soloBody, "INVESTIGATORS AT THE TABLE");
            var sizes = UiKit.CreateRow(_soloBody, "PartySize", 6f, 34f);
            foreach (int size in new[] { 2, 3, 4 })
            {
                int captured = size;
                UiKit.CreateButton(sizes,
                    (_soloInvestigatorCount == captured ? "●  " : "○  ") + captured, 16,
                    () =>
                    {
                        _soloInvestigatorCount = captured;
                        RenderMenu();
                    });
            }

            UiKit.CreateButton(_soloBody, "START", 20, StartSoloGame)
                .GetComponent<LayoutElement>().minHeight = 48;
        }

        /// <summary>One radio row of the solo setup; picking re-renders so the dot moves.</summary>
        private void Choice(string label, bool current, Action pick)
        {
            UiKit.CreateButton(_soloBody, (current ? "●  " : "○  ") + label, 16, () =>
            {
                pick();
                RenderMenu();
            });
        }

        private static void Head(RectTransform body, string title)
        {
            var text = UiKit.CreateText(body, title, 13, TextAnchor.MiddleLeft, UiKit.AccentColor);
            text.gameObject.AddComponent<LayoutElement>().minHeight = 28;
        }
    }
}
