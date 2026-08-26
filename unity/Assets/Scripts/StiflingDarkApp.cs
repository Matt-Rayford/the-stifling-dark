using System;
using System.IO;
using System.Linq;
using StiflingDark.Engine.Data;
using StiflingDark.Protocol;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The shell: menu -> lobby -> table, and the connection lifecycle behind all three.
    /// Spawns itself on Play into whatever scene is open (Assets/Scenes/Main.unity is
    /// deliberately near-empty), the same bootstrap the Lemonade Wars client uses.
    ///
    /// Everything about the game is server-authoritative. The client's only local computation
    /// is geometry: the flashlight beam preview, move-cost highlighting, and the light mask,
    /// all of them running the engine's own code out of Assets/Plugins.
    /// </summary>
    public sealed class StiflingDarkApp : MonoBehaviour
    {
        private const string PrefServer = "sd_server";
        private const string PrefName = "sd_name";
        private const string PrefKey = "sd_player_key";
        private const string PrefLastRoom = "sd_last_room";
        private const string DefaultServer = "ws://localhost:5226/ws";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Camera.main == null)
            {
                var cameraGo = new GameObject("Main Camera", typeof(Camera));
                cameraGo.tag = "MainCamera";
                var camera = cameraGo.GetComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.02f, 0.02f, 0.03f);
                camera.orthographic = true;
            }
            new GameObject("StiflingDarkApp", typeof(StiflingDarkApp));
        }

        private enum Stage
        {
            Menu,
            Lobby,
            Game,
        }

        private GameDatabase _db;
        private Describe _describe;
        private TokenArt _art;
        private ServerSession _session;
        private Prompt _prompt;
        private LobbyUi _lobby;
        private GameUi _game;
        private BoardView _boardView;
        private BoardModel _boardModel;
        private string _builtScenario = "";

        private Stage _stage = Stage.Menu;
        private RectTransform _menu;
        private RectTransform _menuBody;
        private TMP_Text _menuStatus;
        private TMP_InputField _nameInput;
        private TMP_InputField _serverInput;
        private TMP_InputField _codeInput;
        private TMP_Text _toast;
        private float _toastUntil;
        private int _menuRevision = -1;
        /// <summary>Room code whose My Games × is one click from ending the game.</summary>
        private string _confirmEndCode = "";
        private string _dataDir = "";
        private string _loadError = "";

        private void Start()
        {
            Application.runInBackground = true;
            _dataDir = Path.Combine(Application.streamingAssetsPath, "game-data");
            try
            {
                _db = GameDatabase.Load(_dataDir);
                _describe = new Describe(_db);
            }
            catch (Exception e)
            {
                _loadError = e.Message;
                Debug.LogError("Could not load game-data from " + _dataDir +
                    " — run tools/sync_unity.sh. " + e);
            }
            _art = new TokenArt(Application.streamingAssetsPath, Application.dataPath);

            var canvas = UiKit.CreateCanvas("SdCanvas", 0);
            _prompt = new Prompt(UiKit.CreateCanvas("SdModalCanvas", 200).transform);
            BuildMenu(canvas.transform);
            ShowStage(Stage.Menu);
        }

        // ---------------------------------------------------------------- menu

        private void BuildMenu(Transform canvas)
        {
            _menu = UiKit.CreatePanel(canvas, "Menu", new Color(0.03f, 0.035f, 0.045f, 1f));
            UiKit.Anchor(_menu, Vector2.zero, Vector2.one);

            var title = UiKit.CreateText(_menu, "THE STIFLING DARK", 84, TextAnchor.MiddleCenter,
                UiKit.AccentColor);
            title.font = UiKit.TitleFont;
            UiKit.Anchor((RectTransform)title.transform, new Vector2(0, 0.87f), new Vector2(1, 0.97f));
            var subtitle = UiKit.CreateText(_menu,
                "Keep your friends close, and your flashlight closer", 16, TextAnchor.MiddleCenter,
                UiKit.MutedColor);
            UiKit.Anchor((RectTransform)subtitle.transform, new Vector2(0, 0.83f), new Vector2(1, 0.87f));

            // One centred column, Lemonade Wars style: every control full-width on its own row,
            // with the connected sections (New Table / Join / My Games) scrolling below.
            var column = UiKit.CreateGroup(_menu, "MenuColumn");
            UiKit.Anchor(column, new Vector2(0.32f, 0.07f), new Vector2(0.68f, 0.82f));
            var layout = column.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 8;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;

            UiKit.CreateText(column, "Your name", 14, TextAnchor.MiddleLeft, UiKit.MutedColor)
                .gameObject.AddComponent<LayoutElement>().minHeight = 20;
            _nameInput = UiKit.CreateInput(column, "name", PlayerPrefs.GetString(PrefName, "Matt"));
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

            _toast = UiKit.CreateText(_menu, "", 18, TextAnchor.MiddleCenter, UiKit.AccentColor);
            UiKit.Anchor((RectTransform)_toast.transform, Vector2.zero, new Vector2(1, 0),
                new Vector2(80, 16), new Vector2(-80, 52));
        }

        /// <summary>
        /// Last URL actually connected to wins; then StreamingAssets/client-config.json (written
        /// by tools/sync_unity.sh, so a build for a friend can point at Railway); then localhost.
        /// </summary>
        private static string LoadServerUrl()
        {
            string remembered = PlayerPrefs.GetString(PrefServer, "");
            if (!string.IsNullOrEmpty(remembered))
            {
                return remembered;
            }
            try
            {
                string path = Path.Combine(Application.streamingAssetsPath, "client-config.json");
                if (File.Exists(path))
                {
                    var config = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                    string url = (string)config["serverUrl"];
                    if (!string.IsNullOrEmpty(url))
                    {
                        return url;
                    }
                }
            }
            catch (Exception)
            {
                // Malformed config: fall through to the dev default.
            }
            return DefaultServer;
        }

        /// <summary>
        /// The durable identity secret. Generated once and kept in PlayerPrefs; the server
        /// stores only its SHA-256 and refuses anything under 16 characters.
        /// </summary>
        private static string PlayerKey()
        {
            string key = PlayerPrefs.GetString(PrefKey, "");
            if (key.Length < 16)
            {
                key = "sd-" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(PrefKey, key);
                PlayerPrefs.Save();
            }
            return key;
        }

        private void Connect()
        {
            if (_describe == null)
            {
                return;
            }
            string url = (_serverInput.text ?? "").Trim();
            if (url.Length == 0)
            {
                url = DefaultServer;
            }
            // Accept whatever gets pasted: an http(s) URL from the browser bar, a bare
            // domain, or a proper ws(s) URL. Public hosts sit behind TLS (Railway
            // terminates it), so anything that is not localhost defaults to wss://.
            if (url.StartsWith("http://"))
            {
                url = "ws://" + url.Substring("http://".Length);
            }
            else if (url.StartsWith("https://"))
            {
                url = "wss://" + url.Substring("https://".Length);
            }
            else if (!url.StartsWith("ws://") && !url.StartsWith("wss://"))
            {
                bool local = url.StartsWith("localhost") || url.StartsWith("127.0.0.1");
                url = (local ? "ws://" : "wss://") + url;
            }
            if (!url.EndsWith("/ws"))
            {
                url = url.TrimEnd('/') + "/ws";
            }
            string name = (_nameInput.text ?? "").Trim();
            PlayerPrefs.SetString(PrefServer, url);
            PlayerPrefs.SetString(PrefName, name);
            PlayerPrefs.Save();

            _session?.Dispose();
            // A reconnect starts a fresh conversation, so the lobby and table are rebuilt
            // against the new session rather than left holding a dead one.
            _lobby?.Destroy();
            _game?.Destroy();
            _boardView?.Destroy();
            _lobby = null;
            _game = null;
            _boardView = null;
            _session = ServerSession.Connect(url, PlayerKey(), name);
            _session.ErrorReceived += OnServerError;
            _session.TurnAlert += code => Toast("Another game needs you: room " + code);
            _session.RoomChanged += OnRoomChanged;
            _session.RoomClosed += (code, by) =>
            {
                Toast("Game " + code + " was ended by " + by + ".");
                // Kicked out from under us: drop back to the menu instead of a dead table.
                if (_session.Room.Code == code && _stage != Stage.Menu)
                {
                    ShowStage(Stage.Menu);
                }
            };
            _menuRevision = -1;

            // The lobby and table are built against a live session, so rebuild them per connect.
            _lobby = new LobbyUi(_menu.parent, _session, _describe);
            _lobby.LeaveRequested = () => ShowStage(Stage.Menu);
            _lobby.SetActive(false);
            _game = null;
            _builtScenario = "";
        }

        private void OnServerError(string message)
        {
            Toast(message);
            Debug.Log("server error: " + message);
        }

        private void OnRoomChanged()
        {
            var room = _session.Room;
            if (!string.IsNullOrEmpty(room.Code) && !string.IsNullOrEmpty(room.Token))
            {
                // Per-room reconnect credential: join_room with this token always reclaims
                // exactly this seat, even mid-lobby.
                PlayerPrefs.SetString("sd_token_" + room.Code, room.Token);
                PlayerPrefs.SetString(PrefLastRoom, room.Code);
                PlayerPrefs.Save();
            }
        }

        private void Toast(string message)
        {
            if (_toast != null)
            {
                _toast.text = message;
                _toastUntil = Time.time + 6f;
            }
        }

        private void RenderMenu()
        {
            UiKit.Clear(_menuBody);
            if (_loadError.Length > 0)
            {
                _menuStatus.text = "game-data did not load:\n" + _loadError +
                    "\n\nRun tools/sync_unity.sh, then reopen the project.";
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

            Head("NEW TABLE");
            UiKit.CreateButton(_menuBody, "Create a room — I play an Investigator", 18,
                () => _session.CreateRoom(NameOrDefault(), SeatRole.Investigator));
            UiKit.CreateButton(_menuBody, "Create a room — I play the Adversary", 18,
                () => _session.CreateRoom(NameOrDefault(), SeatRole.Adversary));

            Head("JOIN BY CODE");
            _codeInput = UiKit.CreateInput(_menuBody, "5-letter room code");
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

            Head("MY GAMES");
            if (_session.GamesList.Count == 0)
            {
                UiKit.CreateText(_menuBody, "(none yet)", 15, TextAnchor.MiddleLeft, UiKit.MutedColor)
                    .gameObject.AddComponent<LayoutElement>().minHeight = 24;
            }
            foreach (var summary in _session.GamesList)
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
            UiKit.CreateButton(_menuBody, "Refresh My Games", 15, () =>
            {
                _confirmEndCode = "";
                _session.ListGames();
            });
        }

        private string NameOrDefault()
        {
            string name = (_nameInput.text ?? "").Trim();
            return name.Length == 0 ? "Player" : name;
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

        private void Head(string title)
        {
            var text = UiKit.CreateText(_menuBody, title, 13, TextAnchor.MiddleLeft, UiKit.AccentColor);
            text.gameObject.AddComponent<LayoutElement>().minHeight = 28;
        }

        // -------------------------------------------------------------- screens

        private void ShowStage(Stage stage)
        {
            _stage = stage;
            _menu.gameObject.SetActive(stage == Stage.Menu);
            _lobby?.SetActive(stage == Stage.Lobby);
            _game?.SetActive(stage == Stage.Game);
            if (stage != Stage.Game)
            {
                Tooltip.Hide();
            }
        }

        private void Update()
        {
            if (_toast != null && _toastUntil > 0f && Time.time > _toastUntil)
            {
                _toast.text = "";
                _toastUntil = 0f;
            }
            if (_session == null)
            {
                if (_menuRevision != 0)
                {
                    _menuRevision = 0;
                    RenderMenu();
                }
                return;
            }
            _session.Pump();

            var room = _session.Room;
            bool inRoom = !string.IsNullOrEmpty(room.Code) && room.YourSeat >= 0;
            if (inRoom && room.Started && _session.View != null)
            {
                EnsureGame(_session.View.ScenarioId);
                if (_stage != Stage.Game && _game != null)
                {
                    ShowStage(Stage.Game);
                }
            }
            else if (inRoom && !room.Started)
            {
                if (_stage != Stage.Lobby)
                {
                    ShowStage(Stage.Lobby);
                }
            }
            else if (_stage != Stage.Menu && !inRoom)
            {
                ShowStage(Stage.Menu);
            }

            switch (_stage)
            {
                case Stage.Menu:
                    if (_menuRevision != _session.Revision)
                    {
                        _menuRevision = _session.Revision;
                        RenderMenu();
                    }
                    break;
                case Stage.Lobby:
                    _lobby?.Tick();
                    break;
                case Stage.Game:
                    _game?.Tick();
                    break;
            }
        }

        /// <summary>
        /// Build the board once the first update names the scenario. The map, the LoS mask and
        /// the board texture all key off it, and the server is the one that decides.
        /// </summary>
        private void EnsureGame(string scenarioId)
        {
            if (_game != null && _builtScenario == scenarioId)
            {
                return;
            }
            if (string.IsNullOrEmpty(scenarioId) || _describe == null)
            {
                return;
            }
            try
            {
                _boardModel = new BoardModel(_db, scenarioId, _dataDir);
                if (!_boardModel.HasLosMask)
                {
                    Toast("No line-of-sight mask for " + scenarioId +
                        " — the flashlight preview will light through walls.");
                }
                _boardView = new BoardView(_boardModel, _art, _describe);
                _game = new GameUi(_menu.parent, _session, _boardModel, _boardView, _describe,
                    _prompt);
                _game.LeaveRequested = () =>
                {
                    _session.LeaveRoom();
                    ShowStage(Stage.Menu);
                };
                _builtScenario = scenarioId;
            }
            catch (Exception e)
            {
                _loadError = e.Message;
                Debug.LogException(e);
                Toast("Could not build the board: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            _session?.Dispose();
        }
    }
}
