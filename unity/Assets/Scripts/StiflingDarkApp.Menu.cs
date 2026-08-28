using StiflingDark.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The menu stage and its four screens. Each is built once and then shown or hidden;
    /// only the scrolling bodies are re-rendered, so what the player typed survives.
    /// </summary>
    public sealed partial class StiflingDarkApp
    {
        private enum MenuScreen
        {
            Root,
            Online,
            Solo,
            Settings,
        }

        private RectTransform _menu;
        private RectTransform _rootScreen;
        private RectTransform _onlineScreen;
        private RectTransform _soloScreen;
        private RectTransform _settingsScreen;
        private MenuScreen _menuScreen = MenuScreen.Root;

        private TMP_Text _rootStatus;
        private TMP_Text _menuStatus;
        private RectTransform _menuBody;
        private RectTransform _soloBody;
        private TMP_InputField _settingsNameInput;
        private TMP_InputField _serverInput;
        private TMP_InputField _codeInput;
        private TMP_Text _toast;
        private float _toastUntil;
        private int _menuRevision = -1;
        /// <summary>Room code whose My Games × is one click from ending the game.</summary>
        private string _confirmEndCode = "";

        // --------------------------------------------------------------- build

        private void BuildMenu(Transform canvas)
        {
            _menu = UiKit.CreatePanel(canvas, "Menu", new Color(0.03f, 0.035f, 0.045f, 1f));
            UiKit.Anchor(_menu, Vector2.zero, Vector2.one);

            BuildRootScreen();
            BuildOnlineScreen();
            BuildSoloScreen();
            BuildSettingsScreen();
            BuildSoloSetup();

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
                TextAnchor.MiddleCenter, UiKit.TitleColor);
            title.font = UiKit.TitleFont;
            UiKit.Anchor((RectTransform)title.transform, new Vector2(0, 0.72f), new Vector2(1, 0.88f));
            var subtitle = UiKit.CreateText(_rootScreen,
                "Keep your friends close, and your flashlight closer", 16, TextAnchor.MiddleCenter,
                UiKit.MutedColor);
            UiKit.Anchor((RectTransform)subtitle.transform, new Vector2(0, 0.66f), new Vector2(1, 0.72f));

            var column = MenuColumn(_rootScreen, "RootColumn",
                new Vector2(0.34f, 0.24f), new Vector2(0.66f, 0.60f), 14);
            MenuButton(column, "Play vs Bots", OpenSoloScreen);
            MenuButton(column, "Join with a room code", () => ShowMenuScreen(MenuScreen.Online));
            MenuButton(column, "Settings", () => ShowMenuScreen(MenuScreen.Settings));

            _rootStatus = UiKit.CreateText(_rootScreen, "", 15, TextAnchor.UpperCenter,
                UiKit.MutedColor);
            UiKit.Anchor((RectTransform)_rootStatus.transform,
                new Vector2(0.2f, 0.12f), new Vector2(0.8f, 0.30f));
        }

        private void BuildOnlineScreen()
        {
            _onlineScreen = UiKit.CreateGroup(_menu, "MenuOnline");
            UiKit.Anchor(_onlineScreen, Vector2.zero, Vector2.one);
            var columnMin = new Vector2(0.30f, 0.06f);
            CreateBackButton(_onlineScreen, columnMin.x);
            CreateHeading(_onlineScreen, "PLAY ONLINE",
                "Start a table of your own, or join one with its room code");

            var column = MenuColumn(_onlineScreen, "OnlineColumn",
                columnMin, new Vector2(0.70f, 0.81f), 8);

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
            // Wider than the other screens: the Investigator board strip lives in this column.
            var columnMin = new Vector2(0.20f, 0.06f);
            CreateBackButton(_soloScreen, columnMin.x);
            CreateHeading(_soloScreen, "PLAY VS BOTS", "");

            var column = MenuColumn(_soloScreen, "SoloColumn",
                columnMin, new Vector2(0.80f, 0.85f), 8);

            var setup = UiKit.CreatePanel(column, "Setup", UiKit.PanelColor);
            var setupElement = setup.gameObject.AddComponent<LayoutElement>();
            setupElement.flexibleHeight = 1;
            setupElement.minHeight = 200;
            _soloBody = UiKit.CreateScrollList(setup, 6f);
        }

        private void BuildSettingsScreen()
        {
            _settingsScreen = UiKit.CreateGroup(_menu, "MenuSettings");
            UiKit.Anchor(_settingsScreen, Vector2.zero, Vector2.one);
            var columnMin = new Vector2(0.34f, 0.50f);
            CreateBackButton(_settingsScreen, columnMin.x);
            CreateHeading(_settingsScreen, "SETTINGS", "");

            var column = MenuColumn(_settingsScreen, "SettingsColumn",
                columnMin, new Vector2(0.66f, 0.81f), 8);

            Head(column, "YOUR NAME");
            _settingsNameInput = UiKit.CreateInput(column, "name", RememberedPlayerName());
            Head(column, "MASTER VOLUME");
            var volume = UiKit.CreateSlider(column,
                100 / Music.VolumeStep, Music.Volume / Music.VolumeStep);
            volume.onValueChanged.AddListener(step => Music.Volume = (int)step * Music.VolumeStep);

            UiKit.CreateButton(column, "Save", 18, () => ShowMenuScreen(MenuScreen.Root))
                .GetComponent<LayoutElement>().minHeight = 44;

            // Future settings rows go in this column, below Save.
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

        /// <summary>
        /// The arrow home, sitting on the heading's own line with its left edge flush to the
        /// screen's content column — <paramref name="columnLeft"/> is that column's left anchor,
        /// which differs per screen.
        /// </summary>
        private void CreateBackButton(RectTransform screen, float columnLeft)
        {
            // Bare gold arrow matching the heading; the button skin only appears on hover,
            // inverting to dark-on-gold.
            var go = new GameObject("Back", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(screen, false);
            var rect = (RectTransform)go.transform;
            // Mid-band of the heading (CreateHeading anchors at 0.87..0.96).
            rect.anchorMin = rect.anchorMax = new Vector2(columnLeft, 0.915f);
            rect.pivot = new Vector2(0f, 0.5f);
            rect.sizeDelta = new Vector2(64f, 64f);
            rect.anchoredPosition = Vector2.zero;

            var background = go.GetComponent<Image>();
            background.color = Color.clear;
            var button = go.GetComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => ShowMenuScreen(MenuScreen.Root));

            var arrow = UiKit.CreateText(go.transform, "←", 54, TextAnchor.MiddleCenter,
                UiKit.TitleColor);
            arrow.fontStyle = TMPro.FontStyles.Bold;
            // Midline centers the glyph's actual bounds; line-metric centering hangs a lone
            // arrow noticeably off-center in the square.
            arrow.alignment = TMPro.TextAlignmentOptions.Midline;
            UiKit.Anchor((RectTransform)arrow.transform, Vector2.zero, Vector2.one);

            UiKit.AddHover(go,
                () =>
                {
                    background.color = UiKit.TitleColor;
                    arrow.color = UiKit.AccentTextColor;
                },
                () =>
                {
                    background.color = Color.clear;
                    arrow.color = UiKit.TitleColor;
                });
        }

        private static void CreateHeading(RectTransform screen, string titleText, string subtitleText = "")
        {
            var heading = UiKit.CreateText(screen, titleText, 44, TextAnchor.MiddleCenter,
                UiKit.TitleColor);
            heading.font = UiKit.MenuFont;
            UiKit.Anchor((RectTransform)heading.transform,
                new Vector2(0, 0.87f), new Vector2(1, 0.96f));
            var subtitle = UiKit.CreateText(screen, subtitleText, 15, TextAnchor.MiddleCenter,
                UiKit.MutedColor);
            UiKit.Anchor((RectTransform)subtitle.transform,
                new Vector2(0, 0.82f), new Vector2(1, 0.87f));
        }

        /// <summary>A root-menu button: full-width 64px row in the menu display font.</summary>
        private static void MenuButton(RectTransform column, string label,
            UnityEngine.Events.UnityAction onClick)
        {
            UiKit.CreateButton(column, label, 24, onClick, labelFont: UiKit.MenuFont)
                .GetComponent<LayoutElement>().minHeight = 64;
        }

        // ---------------------------------------------------------- navigation

        private void ShowMenuScreen(MenuScreen screen)
        {
            RememberPlayerName();
            _menuScreen = screen;
            _rootScreen.gameObject.SetActive(screen == MenuScreen.Root);
            _onlineScreen.gameObject.SetActive(screen == MenuScreen.Online);
            _soloScreen.gameObject.SetActive(screen == MenuScreen.Solo);
            _settingsScreen.gameObject.SetActive(screen == MenuScreen.Settings);

            if (screen == MenuScreen.Settings)
            {
                _settingsNameInput.text = RememberedPlayerName();
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
        /// The Settings screen owns the only name field; leaving it (Back or Save, both of
        /// which route through <see cref="ShowMenuScreen"/>) hands whatever was typed to
        /// PlayerPrefs so every other screen can read it back.
        /// </summary>
        private void RememberPlayerName()
        {
            if (_menuScreen != MenuScreen.Settings)
            {
                return;
            }
            string typed = (_settingsNameInput?.text ?? "").Trim();
            if (typed.Length == 0)
            {
                return;
            }
            PlayerPrefs.SetString(PrefName, typed);
            PlayerPrefs.Save();
        }

        private static string NameOrDefault()
        {
            string name = RememberedPlayerName().Trim();
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

        private static void Head(RectTransform body, string title)
        {
            var text = UiKit.CreateText(body, title, 16, TextAnchor.MiddleLeft, UiKit.TitleColor);
            text.font = UiKit.MenuFont;
            text.gameObject.AddComponent<LayoutElement>().minHeight = 28;
        }
    }
}
