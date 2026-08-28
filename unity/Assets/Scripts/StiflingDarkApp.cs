using System;
using System.IO;
using StiflingDark.Engine.Data;
using StiflingDark.Protocol;
using UnityEngine;

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
    ///
    /// The menu's own screens (root / online / solo setup) live in StiflingDarkApp.Menu.cs.
    /// </summary>
    public sealed partial class StiflingDarkApp : MonoBehaviour
    {
        private const string PrefServer = "sd_server";
        private const string PrefName = "sd_name";
        private const string PrefKey = "sd_player_key";
        private const string PrefLastRoom = "sd_last_room";
        private const string DefaultServer = "ws://localhost:5226/ws";
        private const string DefaultPlayerName = "Matt";

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
        private LocalGameSession _solo;
        private Prompt _prompt;
        private LobbyUi _lobby;
        private GameUi _game;
        private BoardView _boardView;
        private BoardModel _boardModel;
        private string _builtScenario = "";
        /// <summary>The session <see cref="_game"/> was built against; a different one needs a
        /// fresh HUD, because the table holds its session for life.</summary>
        private IGameSession _gameSession;

        private Stage _stage = Stage.Menu;
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

            Music.ApplySavedVolume();
            var canvas = UiKit.CreateCanvas("SdCanvas", 0);
            _prompt = new Prompt(UiKit.CreateCanvas("SdModalCanvas", 200).transform);
            BuildMenu(canvas.transform);
            ShowStage(Stage.Menu);
        }

        // -------------------------------------------------------- connection

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
            string name = NameOrDefault();
            PlayerPrefs.SetString(PrefServer, url);
            PlayerPrefs.Save();

            _session?.Dispose();
            // A reconnect starts a fresh conversation, so the lobby and table are rebuilt
            // against the new session rather than left holding a dead one.
            _lobby?.Destroy();
            _lobby = null;
            DestroyTable();
            _session = ServerSession.Connect(url, PlayerKey(), name);
            _session.ErrorReceived += OnSessionError;
            _session.TurnAlert += code => Toast("Another game needs you: room " + code);
            _session.RoomChanged += OnRoomChanged;
            _session.RoomClosed += (code, by) =>
            {
                Toast("Game " + code + " was ended by " + by + ".");
                // Kicked out from under us: drop back to the online screen we came from
                // instead of a dead table.
                if (_session.Room.Code == code && _stage != Stage.Menu)
                {
                    ReturnToMenu(MenuScreen.Online);
                }
            };
            _menuRevision = -1;

            // The lobby and table are built against a live session, so rebuild them per connect.
            _lobby = new LobbyUi(_menu.parent, _session, _describe);
            _lobby.LeaveRequested = () => ReturnToMenu(MenuScreen.Online);
            _lobby.SetActive(false);
        }

        private void OnSessionError(string message)
        {
            Toast(message);
            Debug.Log("session error: " + message);
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

        // ------------------------------------------------------- offline play

        private void StartSoloGame()
        {
            if (_describe == null)
            {
                Toast("game-data did not load — offline play needs it.");
                return;
            }
            try
            {
                _solo = new LocalGameSession(_db, _soloScenario, _soloAdversary, _soloRole,
                    SoloInvestigatorPicks(), NameOrDefault(), (ulong)DateTime.UtcNow.Ticks);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Toast("Could not start the offline game: " + e.Message);
                return;
            }
            _solo.ErrorReceived += OnSessionError;
        }

        /// <summary>Offline games are session-only: leaving discards the whole thing.</summary>
        private void LeaveSoloGame()
        {
            _solo?.Dispose();
            _solo = null;
            DestroyTable();
            ReturnToMenu(MenuScreen.Solo);
        }

        // -------------------------------------------------------------- stages

        private void ShowStage(Stage stage)
        {
            _stage = stage;
            _menu.gameObject.SetActive(stage == Stage.Menu);
            _lobby?.SetActive(stage == Stage.Lobby);
            _game?.SetActive(stage == Stage.Game);
            // The menu track plays everywhere outside an actual game.
            if (stage == Stage.Game)
            {
                Music.StopMenuTrack();
            }
            else
            {
                Music.PlayMenuTrack();
            }
            if (stage != Stage.Game)
            {
                Tooltip.Hide();
            }
        }

        /// <summary>Back out of a table onto the menu screen that launched it.</summary>
        private void ReturnToMenu(MenuScreen screen)
        {
            ShowStage(Stage.Menu);
            ShowMenuScreen(screen);
        }

        private void Update()
        {
            TickToast();
            TickSoloSetup();
            if (_solo != null)
            {
                TickSoloGame();
                return;
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
                EnsureGame(_session, _session.View.ScenarioId);
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
                ReturnToMenu(MenuScreen.Online);
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

        private void TickSoloGame()
        {
            _solo.Pump();
            EnsureGame(_solo, _solo.View.ScenarioId);
            if (_game == null)
            {
                // The board would not build and the toast has already said why; sitting in a
                // dead offline table would just re-toast every frame.
                LeaveSoloGame();
                return;
            }
            if (_stage != Stage.Game)
            {
                ShowStage(Stage.Game);
            }
            _game.Tick();
        }

        /// <summary>
        /// Build the board once the session names the scenario. The map, the LoS mask and the
        /// board texture all key off it, and the session is the one that decides.
        /// </summary>
        private void EnsureGame(IGameSession session, string scenarioId)
        {
            if (_game != null && _builtScenario == scenarioId && _gameSession == session)
            {
                return;
            }
            if (string.IsNullOrEmpty(scenarioId) || _describe == null)
            {
                return;
            }
            DestroyTable();
            try
            {
                _boardModel = new BoardModel(_db, scenarioId, _dataDir);
                if (!_boardModel.HasLosMask)
                {
                    Toast("No line-of-sight mask for " + scenarioId +
                        " — the flashlight preview will light through walls.");
                }
                _boardView = new BoardView(_boardModel, _art, _describe);
                _game = new GameUi(_menu.parent, session, _boardModel, _boardView, _describe,
                    _prompt);
                _game.LeaveRequested = session == _solo
                    ? (Action)LeaveSoloGame
                    : () =>
                    {
                        _session.LeaveRoom();
                        ReturnToMenu(MenuScreen.Online);
                    };
                _gameSession = session;
                _builtScenario = scenarioId;
            }
            catch (Exception e)
            {
                _loadError = e.Message;
                Debug.LogException(e);
                Toast("Could not build the board: " + e.Message);
            }
        }

        /// <summary>Drop the HUD and the board so the next table builds against its own session.</summary>
        private void DestroyTable()
        {
            _game?.Destroy();
            _boardView?.Destroy();
            _game = null;
            _boardView = null;
            _gameSession = null;
            _builtScenario = "";
        }

        private void OnDestroy()
        {
            _session?.Dispose();
            _solo?.Dispose();
        }
    }
}
