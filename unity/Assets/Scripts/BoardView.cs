using System;
using System.Collections.Generic;
using System.Linq;
using StiflingDark.Engine.Core;
using StiflingDark.Engine.Data;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace StiflingDark.Unity
{
    /// <summary>
    /// The board: the 4096px render, the light mask, every figure and token from the view
    /// snapshot, move highlights, and the live flashlight aim.
    ///
    /// Coordinates: one world unit is one pixel of the FULL-res render the map JSON was
    /// measured against (7092 for the Sawmill, 6621 for the Amusement Park), so a space's
    /// x/y from the JSON is its world position directly — world = (x, -y). The board texture
    /// is scaled by SourceWidth/textureWidth to fit that space, which is the whole of the
    /// resolution-independence story: swap in an 8192px render and nothing else changes.
    ///
    /// Spaces are not GameObjects. 400+ colliders to hit-test a circle grid would be silly;
    /// picking is a nearest-center search over the map data.
    /// </summary>
    public sealed class BoardView
    {
        private const float MinOrthoSize = 400f;
        private const float DragThreshold = 6f;

        private readonly BoardModel _board;
        private readonly TokenArt _art;
        private readonly Describe _describe;
        private readonly Transform _root;
        private readonly Transform _dynamic;
        private readonly LightOverlay _light;
        private readonly Camera _camera;

        private readonly Dictionary<string, int> _moveTargets = new Dictionary<string, int>();
        private readonly Dictionary<string, List<string>> _notes =
            new Dictionary<string, List<string>>();
        private PlayerView _view;
        private string _myInvestigatorId = "";
        private string _hovered;

        private Vector3 _dragOrigin;
        private Vector3 _dragCameraOrigin;
        private bool _dragging;
        private bool _dragMoved;
        private float _fitOrthoSize;

        // Aim state.
        private string _aimFrom;
        private Action<double> _aimConfirm;
        private Action _aimCancel;
        private double _aimAngle;
        private bool _aiming;
        private Transform _beamIndicator;

        /// <summary>A space was left-clicked (and the click was not a pan).</summary>
        public Action<string> SpaceClicked;
        /// <summary>True while the flashlight is being aimed — the turn UI locks down.</summary>
        public bool Aiming => _aiming;

        public BoardView(BoardModel board, TokenArt art, Describe describe)
        {
            _board = board;
            _art = art;
            _describe = describe;

            var rootGo = new GameObject("Board");
            _root = rootGo.transform;
            var dynamicGo = new GameObject("Dynamic");
            _dynamic = dynamicGo.transform;
            _dynamic.SetParent(_root, false);

            var boardSprite = art.Board(board.Map.Id);
            var boardGo = new GameObject("BoardTexture", typeof(SpriteRenderer));
            boardGo.transform.SetParent(_root, false);
            var renderer = boardGo.GetComponent<SpriteRenderer>();
            renderer.sortingOrder = 0;
            if (boardSprite != null)
            {
                renderer.sprite = boardSprite;
                // Render pixels -> source pixels. 4096/7092 for the Sawmill, 4096/6621 for the
                // park: the scale factor the client must apply to the map JSON's coordinates.
                float scale = (float)(board.SourceWidth / boardSprite.texture.width);
                boardGo.transform.localScale = new Vector3(scale, scale, 1f);
            }
            else
            {
                Debug.LogWarning("No board texture for '" + board.Map.Id +
                    "' — run tools/sync_unity.sh. Falling back to the space graph only.");
                renderer.sprite = UiSprites.RoundedRect;
                renderer.color = new Color(0.10f, 0.11f, 0.13f);
                renderer.drawMode = SpriteDrawMode.Sliced;
                renderer.size = new Vector2((float)board.SourceWidth, (float)board.SourceHeight);
                boardGo.transform.position =
                    new Vector3((float)board.SourceWidth / 2f, -(float)board.SourceHeight / 2f, 0f);
                DrawGraphFallback();
            }

            _light = new LightOverlay(_root, board, 10);

            _camera = Camera.main;
            if (_camera == null)
            {
                var cameraGo = new GameObject("Main Camera", typeof(Camera));
                cameraGo.tag = "MainCamera";
                _camera = cameraGo.GetComponent<Camera>();
            }
            _camera.orthographic = true;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = new Color(0.02f, 0.02f, 0.03f);
            ResetCamera();
        }

        public void SetActive(bool active)
        {
            _root.gameObject.SetActive(active);
        }

        /// <summary>Tear the board down — used when a reconnect replaces the session.</summary>
        public void Destroy() => UnityEngine.Object.Destroy(_root.gameObject);

        /// <summary>Frame the whole board, leaving room for the side panels.</summary>
        public void ResetCamera()
        {
            float aspect = Mathf.Max(0.4f, _camera.aspect);
            float byHeight = (float)_board.SourceHeight / 2f;
            float byWidth = (float)_board.SourceWidth / 2f / aspect;
            _fitOrthoSize = Mathf.Max(byHeight, byWidth) * 1.04f;
            _camera.orthographicSize = _fitOrthoSize;
            _camera.transform.position = new Vector3(
                (float)_board.SourceWidth / 2f, -(float)_board.SourceHeight / 2f, -10f);
        }

        public Vector3 WorldOf(string spaceId)
        {
            var space = _board.SpaceOrNull(spaceId);
            return space == null
                ? Vector3.zero
                : new Vector3((float)space.X, -(float)space.Y, 0f);
        }

        /// <summary>Center the view on a space without changing the zoom.</summary>
        public void FocusOn(string spaceId)
        {
            var space = _board.SpaceOrNull(spaceId);
            if (space == null)
            {
                return;
            }
            var world = WorldOf(spaceId);
            _camera.transform.position = new Vector3(world.x, world.y, -10f);
        }

        // ----------------------------------------------------------- rendering

        public void Render(PlayerView view, string myInvestigatorId)
        {
            _view = view;
            _myInvestigatorId = myInvestigatorId ?? "";
            _light.SetLight(view);
            UiKit.Clear(_dynamic);
            _notes.Clear();
            _beamIndicator = null;
            if (view == null)
            {
                return;
            }

            DrawOverlayMarkers(view);
            DrawObjectiveTokens(view);
            DrawEvidence(view);
            DrawPoiTokens(view);
            DrawMedicalItems(view);
            DrawBoardTokens(view);
            DrawFlashlights(view);
            DrawAdversary(view);
            DrawInvestigators(view);
            DrawHighlights();
            if (_aiming)
            {
                BuildBeamIndicator();
                // Clearing _dynamic threw the old indicator away; put the new one where the
                // mouse already is rather than waiting for the next movement.
                UpdateAim(force: true);
            }
        }

        /// <summary>
        /// Spaces the active figure may step to, with the MP each costs. Highlights are drawn by
        /// <see cref="Render"/>, so set these BEFORE rendering.
        /// </summary>
        public void SetMoveTargets(Dictionary<string, int> costs)
        {
            _moveTargets.Clear();
            if (costs == null)
            {
                return;
            }
            foreach (var pair in costs)
            {
                _moveTargets[pair.Key] = pair.Value;
            }
        }

        private void DrawGraphFallback()
        {
            // Without the board render, at least draw the printed graph so the game is playable.
            foreach (var edge in _board.Map.Edges)
            {
                var a = _board.SpaceOrNull(edge.A);
                var b = _board.SpaceOrNull(edge.B);
                if (a == null || b == null)
                {
                    continue;
                }
                var from = new Vector3((float)a.X, -(float)a.Y, 0f);
                var to = new Vector3((float)b.X, -(float)b.Y, 0f);
                var color = edge.Type == EdgeType.Move
                    ? new Color(0.32f, 0.34f, 0.38f)
                    : edge.Type == EdgeType.Window
                        ? new Color(0.45f, 0.62f, 0.78f)
                        : edge.Type == EdgeType.MirrorDoor
                            ? new Color(0.72f, 0.52f, 0.80f)
                            : new Color(0.78f, 0.72f, 0.30f);
                var line = NewSprite(_root, "Edge", UiSprites.RoundedRect, color, 2);
                var mid = (from + to) / 2f;
                line.transform.position = mid;
                float length = Vector3.Distance(from, to);
                line.GetComponent<SpriteRenderer>().drawMode = SpriteDrawMode.Sliced;
                line.GetComponent<SpriteRenderer>().size = new Vector2(length, 14f);
                line.transform.rotation = Quaternion.Euler(0, 0,
                    Mathf.Atan2(to.y - from.y, to.x - from.x) * Mathf.Rad2Deg);
            }
            foreach (var space in _board.Map.Spaces)
            {
                var disc = NewSprite(_root, "Space", UiSprites.Ring,
                    space.PrintedLight == LightLevel.Dim
                        ? new Color(0.55f, 0.56f, 0.60f)
                        : new Color(0.32f, 0.33f, 0.36f), 3);
                disc.transform.position = new Vector3((float)space.X, -(float)space.Y, 0f);
                Scale(disc, (float)_board.Map.SpaceRadius * 2f);
            }
        }

        private void DrawOverlayMarkers(PlayerView view)
        {
            var overlay = view.Overlay;
            foreach (var pair in overlay.DoorStates)
            {
                if (pair.Value == DoorState.Open)
                {
                    continue;
                }
                Token(pair.Key, TokenArt.DoorToken(pair.Value), new Color(0.80f, 0.55f, 0.35f),
                    Describe.Door(pair.Value).Substring(0, 2), 0.78f, 22);
            }
            foreach (string zone in view.FalteringZones)
            {
                foreach (var space in _board.Graph.ZoneSpaces(zone).Take(1))
                {
                    Token(space.Id, TokenArt.FalteringMarker, new Color(0.70f, 0.66f, 0.35f),
                        "F", 0.6f, 21);
                }
            }
            DrawEdgeMarkers(overlay.OpenWindows, TokenArt.OpenWindowMarker,
                new Color(0.50f, 0.72f, 0.88f), "W", "Open Window token on an edge here");
            DrawEdgeMarkers(overlay.FalseWindows, TokenArt.FalseWindowMarker,
                new Color(0.78f, 0.36f, 0.34f), "X", "False Window token — impassable");
            DrawEdgeMarkers(overlay.SecretPassages, TokenArt.SecretPassageMarker,
                new Color(0.62f, 0.80f, 0.56f), "S", "Secret Passage");
            foreach (string space in overlay.AdversaryBarriers)
            {
                Token(space, TokenArt.BarricadeMarker, new Color(0.72f, 0.62f, 0.42f), "B", 0.7f, 21);
            }
        }

        /// <summary>Edge tokens sit at the midpoint of the two spaces they join.</summary>
        private void DrawEdgeMarkers(IEnumerable<string> edgeKeys, string artPath, Color fallback,
            string letter, string letterNote)
        {
            foreach (string key in edgeKeys)
            {
                int sep = key.IndexOf('|');
                if (sep < 0)
                {
                    continue;
                }
                var a = _board.SpaceOrNull(key.Substring(0, sep));
                var b = _board.SpaceOrNull(key.Substring(sep + 1));
                if (a == null || b == null)
                {
                    continue;
                }
                var world = new Vector3(
                    (float)(a.X + b.X) / 2f, -(float)(a.Y + b.Y) / 2f, 0f);
                PlaceToken(world, artPath, fallback, letter, 0.55f, 21);
                Note(a.Id, letterNote);
                Note(b.Id, letterNote);
            }
        }

        private void DrawEvidence(PlayerView view)
        {
            foreach (var evidence in view.Evidence)
            {
                if (string.IsNullOrEmpty(evidence.Space))
                {
                    continue;
                }
                Token(evidence.Space, TokenArt.EvidenceToken(view.ScenarioId),
                    new Color(0.95f, 0.86f, 0.42f), evidence.Zone, 0.7f, 22,
                    "Evidence (" + _board.ZoneName(evidence.Zone) + ")" +
                    (evidence.Revealed ? " — revealed" : ""));
            }
        }

        private void DrawPoiTokens(PlayerView view)
        {
            foreach (var poi in view.PoiTokens)
            {
                // The printed POI space is public map data; the token's position may not be.
                Token(poi.PoiSpace, null, new Color(0.55f, 0.58f, 0.70f), "?", 0.5f, 20,
                    "Point of Interest space");
                if (poi.Collected || string.IsNullOrEmpty(poi.TokenSpace))
                {
                    continue;
                }
                string art = poi.CursedFront.HasValue
                    ? (poi.CursedFront.Value ? TokenArt.CursedFront : TokenArt.ItemFront)
                    : TokenArt.PoiBack;
                string label = poi.CursedFront.HasValue
                    ? (poi.CursedFront.Value ? "Cursed Item front" : "General Item front")
                    : "Point of Interest token (face hidden)";
                Token(poi.TokenSpace, art, new Color(0.72f, 0.62f, 0.90f), "P", 0.72f, 23,
                    label + (poi.ScoutedFaceDown ? " — Scouted, face-down" : ""));
            }
        }

        private void DrawMedicalItems(PlayerView view)
        {
            foreach (string space in view.MedicalItemSpaces)
            {
                Token(space, TokenArt.MedicalBack, new Color(0.86f, 0.42f, 0.44f), "+", 0.68f, 22,
                    "Medical Item");
            }
        }

        private void DrawBoardTokens(PlayerView view)
        {
            foreach (var pair in view.BoardTokens)
            {
                Token(pair.Value, TokenArt.ObjectiveToken(pair.Key),
                    new Color(0.80f, 0.55f, 0.86f), Short(pair.Key), 0.62f, 22, pair.Key);
            }
        }

        private void DrawObjectiveTokens(PlayerView view)
        {
            foreach (var pair in view.Objective.Tokens)
            {
                // Tokens carried by an Investigator ride with the figure, not the board.
                if (view.Objective.TokenCarriers.ContainsKey(pair.Key))
                {
                    continue;
                }
                Token(pair.Value, TokenArt.ObjectiveToken(pair.Key),
                    new Color(0.55f, 0.82f, 0.88f), Short(pair.Key), 0.72f, 22, pair.Key);
            }
        }

        private void DrawFlashlights(PlayerView view)
        {
            foreach (var flashlight in view.Flashlights)
            {
                var world = WorldOf(flashlight.Space);
                var beam = NewSprite(_dynamic, "Beam", UiSprites.RoundedRect,
                    new Color(1f, 0.86f, 0.55f, 0.10f), 12);
                float length = (float)(_board.Db.Flashlight.LengthInSpacePitches * _board.Map.SpacePitch);
                var spriteRenderer = beam.GetComponent<SpriteRenderer>();
                spriteRenderer.drawMode = SpriteDrawMode.Sliced;
                spriteRenderer.size = new Vector2(length, (float)_board.Map.SpacePitch * 1.1f);
                // The engine's angle is board-space (y down); world y is flipped.
                float degrees = (float)(-flashlight.AngleRadians * Mathf.Rad2Deg);
                beam.transform.position = world +
                    Quaternion.Euler(0, 0, degrees) * new Vector3(length / 2f, 0f, 0f);
                beam.transform.rotation = Quaternion.Euler(0, 0, degrees);
            }
        }

        private void DrawAdversary(PlayerView view)
        {
            var adversary = view.Adversary;
            var color = TokenArt.AdversaryColor(adversary.DefId);

            foreach (var pair in adversary.ShadowTokens)
            {
                // key is the token's own id ("main", "frayed", or a space), value is its space.
                Token(pair.Value, TokenArt.ShadowToken(adversary.DefId, faceUp: false),
                    new Color(0.22f, 0.22f, 0.28f), "S", 0.8f, 24,
                    "Shadow token (" + pair.Key + ")");
            }
            foreach (string space in adversary.NoiseTokens)
            {
                Token(space, TokenArt.NoiseToken(adversary.DefId),
                    new Color(0.72f, 0.70f, 0.42f), "N", 0.7f, 24, "Noise token");
            }
            foreach (var pair in adversary.SpineChill)
            {
                var target = view.Investigators.FirstOrDefault(i => i.DefId == pair.Key);
                if (target != null && pair.Value > 0)
                {
                    Token(target.Space, TokenArt.SpineChillMarker,
                        new Color(0.62f, 0.72f, 0.86f), "sc", 0.5f, 29,
                        "Spine Chill on " + _describe.Investigator(pair.Key));
                }
            }

            if (!string.IsNullOrEmpty(adversary.Space))
            {
                Figure(adversary.Space, TokenArt.AdversaryFace(adversary.DefId), color, "AD", 31,
                    Describe.Adversary(adversary.DefId) +
                    (adversary.Revealed ? " — REVEALED" : " — position known"));
            }
            foreach (var figure in adversary.Figures)
            {
                if (!figure.Alive || string.IsNullOrEmpty(figure.Space))
                {
                    continue;
                }
                Figure(figure.Space, TokenArt.CultistFace(figure.Id), color, Short(figure.Id), 30,
                    figure.Id + (figure.Revealed ? " — revealed" : ""));
            }
        }

        private void DrawInvestigators(PlayerView view)
        {
            var roster = _describe.BaseInvestigators;
            for (int i = 0; i < view.Investigators.Count; i++)
            {
                var panel = view.Investigators[i];
                if (panel.Escaped)
                {
                    continue;
                }
                int rosterIndex = 0;
                for (int r = 0; r < roster.Count; r++)
                {
                    if (roster[r].Id == panel.DefId)
                    {
                        rosterIndex = r;
                        break;
                    }
                }
                var color = TokenArt.InvestigatorColor(rosterIndex);
                bool isMe = panel.DefId == _myInvestigatorId;
                string tip = _describe.Investigator(panel.DefId) +
                    "  Stamina " + panel.Stamina + "  Charge " + panel.Charge +
                    "  Wounds " + panel.Wounds.Count(w => w.CardId != null || !w.FaceUp) +
                    (panel.Dead ? "  (dead)" : "") +
                    (panel.SpiritId != null ? "  Spirit: " + _describe.Card(panel.SpiritId) : "");

                var go = Figure(panel.Space, panel.Dead ? null : TokenArt.InvestigatorFace(panel.DefId),
                    panel.Dead ? new Color(0.45f, 0.45f, 0.48f) : color,
                    _describe.Initials(panel.DefId), isMe ? 33 : 32, tip);
                if (go == null)
                {
                    continue;
                }
                // Your own figure wears a ring so it is findable at any zoom.
                var ring = NewSprite(_dynamic, "Own", UiSprites.Ring,
                    isMe ? new Color(1f, 0.92f, 0.62f, 0.95f) : new Color(0f, 0f, 0f, 0.45f),
                    isMe ? 34 : 31);
                ring.transform.position = go.transform.position;
                Scale(ring, (float)_board.Map.SpaceRadius * (isMe ? 2.05f : 1.95f));

                foreach (var carrier in view.Objective.TokenCarriers.Where(c => c.Value == panel.DefId))
                {
                    PlaceToken(
                        go.transform.position + new Vector3((float)_board.Map.SpaceRadius, 0f, 0f),
                        TokenArt.ObjectiveToken(carrier.Key), new Color(0.55f, 0.82f, 0.88f),
                        Short(carrier.Key), 0.5f, 35);
                    Note(panel.Space, "Carrying " + carrier.Key);
                }
            }
        }

        private void DrawHighlights()
        {
            foreach (var pair in _moveTargets)
            {
                var world = WorldOf(pair.Key);
                var ring = NewSprite(_dynamic, "MoveTarget", UiSprites.Ring,
                    new Color(0.55f, 0.90f, 1f, 0.85f), 26);
                ring.transform.position = world;
                Scale(ring, (float)_board.Map.SpaceRadius * 2.2f);

                var label = new GameObject("Cost", typeof(TextMeshPro));
                label.transform.SetParent(_dynamic, false);
                var text = label.GetComponent<TextMeshPro>();
                text.font = UiKit.Font;
                text.text = pair.Value + " MP";
                text.fontSize = (float)_board.Map.SpaceRadius * 0.62f;
                text.alignment = TextAlignmentOptions.Center;
                text.color = new Color(0.75f, 0.95f, 1f);
                text.GetComponent<MeshRenderer>().sortingOrder = 27;
                label.transform.position = world + new Vector3(0f, -(float)_board.Map.SpaceRadius * 1.35f, 0f);
            }
        }

        // -------------------------------------------------------------- pieces

        private GameObject Figure(string spaceId, string artPath, Color color, string initials,
            int sortingOrder, string note)
        {
            var space = _board.SpaceOrNull(spaceId);
            if (space == null)
            {
                return null;
            }
            if (!string.IsNullOrEmpty(note))
            {
                Note(spaceId, note);
            }
            return PlaceToken(new Vector3((float)space.X, -(float)space.Y, 0f), artPath, color,
                initials, 1.35f, sortingOrder);
        }

        /// <summary>
        /// Pin a line of hover text to a space. Everything on a space shares one tooltip: with
        /// figures, shadow tokens and objective tokens overlapping at this zoom, per-sprite
        /// hover targets would fight each other.
        /// </summary>
        private void Note(string spaceId, string line)
        {
            if (!_notes.TryGetValue(spaceId, out var lines))
            {
                lines = new List<string>();
                _notes[spaceId] = lines;
            }
            if (!lines.Contains(line))
            {
                lines.Add(line);
            }
        }

        private GameObject Token(string spaceId, string artPath, Color color, string label,
            float sizeInRadii, int sortingOrder, string note = null)
        {
            var space = _board.SpaceOrNull(spaceId);
            if (space == null)
            {
                return null;
            }
            if (!string.IsNullOrEmpty(note))
            {
                Note(spaceId, note);
            }
            // Tokens sit up and to the left of a space center so a figure on the same space
            // stays readable.
            var offset = new Vector3(
                -(float)_board.Map.SpaceRadius * 0.55f, (float)_board.Map.SpaceRadius * 0.55f, 0f);
            return PlaceToken(new Vector3((float)space.X, -(float)space.Y, 0f) + offset, artPath,
                color, label, sizeInRadii, sortingOrder);
        }

        private GameObject PlaceToken(Vector3 world, string artPath, Color fallbackColor,
            string label, float sizeInRadii, int sortingOrder)
        {
            var sprite = artPath == null ? null : _art.Token(artPath);
            float size = (float)_board.Map.SpaceRadius * sizeInRadii;
            var go = NewSprite(_dynamic, "Token", sprite ?? UiSprites.Circle,
                sprite != null ? Color.white : fallbackColor, sortingOrder);
            go.transform.position = world;
            Scale(go, size);

            if (sprite == null && !string.IsNullOrEmpty(label))
            {
                var text = new GameObject("Label", typeof(TextMeshPro));
                text.transform.SetParent(go.transform, false);
                var tmp = text.GetComponent<TextMeshPro>();
                tmp.font = UiKit.Font;
                tmp.text = label;
                tmp.fontSize = size * 0.45f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = new Color(0.06f, 0.06f, 0.08f);
                tmp.GetComponent<MeshRenderer>().sortingOrder = sortingOrder + 1;
                text.transform.localPosition = Vector3.zero;
                // Undo the parent's sprite scaling so the text keeps world-space size.
                text.transform.localScale = Vector3.one / Mathf.Max(0.0001f, go.transform.localScale.x);
            }
            return go;
        }

        private static GameObject NewSprite(Transform parent, string name, Sprite sprite,
            Color color, int sortingOrder)
        {
            var go = new GameObject(name, typeof(SpriteRenderer));
            go.transform.SetParent(parent, false);
            var renderer = go.GetComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.color = color;
            renderer.sortingOrder = sortingOrder;
            return go;
        }

        /// <summary>Scale a unit sprite (its texture is square) to a world-space diameter.</summary>
        private static void Scale(GameObject go, float diameter)
        {
            var renderer = go.GetComponent<SpriteRenderer>();
            float native = renderer.sprite != null ? renderer.sprite.bounds.size.x : 1f;
            float scale = diameter / Mathf.Max(0.0001f, native);
            go.transform.localScale = new Vector3(scale, scale, 1f);
        }

        private static string Short(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "?";
            }
            var parts = id.Split('-');
            return parts[0].Length >= 2
                ? parts[0].Substring(0, 2).ToUpperInvariant()
                : parts[0].ToUpperInvariant();
        }

        // ------------------------------------------------------- aim the beam

        /// <summary>
        /// Enter aim mode. From here until a click, mouse movement around the figure re-runs
        /// the engine's own beam solver over the synced LoS mask and lights the result live.
        /// </summary>
        public void BeginAim(string fromSpace, Action<double> confirm, Action cancel)
        {
            _aimFrom = fromSpace;
            _aimConfirm = confirm;
            _aimCancel = cancel;
            _aiming = true;
            _aimAngle = double.NaN;
            BuildBeamIndicator();
            UpdateAim(force: true);
        }

        public void EndAim()
        {
            _aiming = false;
            _aimFrom = null;
            _aimConfirm = null;
            _aimCancel = null;
            _light.ClearPreview();
            if (_beamIndicator != null)
            {
                UnityEngine.Object.Destroy(_beamIndicator.gameObject);
                _beamIndicator = null;
            }
        }

        private void BuildBeamIndicator()
        {
            if (_aimFrom == null)
            {
                return;
            }
            float length = (float)(_board.Db.Flashlight.LengthInSpacePitches * _board.Map.SpacePitch);
            var go = NewSprite(_dynamic, "AimBeam", UiSprites.RoundedRect,
                new Color(1f, 0.88f, 0.58f, 0.22f), 15);
            var renderer = go.GetComponent<SpriteRenderer>();
            renderer.drawMode = SpriteDrawMode.Sliced;
            renderer.size = new Vector2(length, (float)_board.Map.SpacePitch * 1.15f);
            _beamIndicator = go.transform;
        }

        private void UpdateAim(bool force)
        {
            if (!_aiming || _aimFrom == null)
            {
                return;
            }
            var origin = WorldOf(_aimFrom);
            var mouse = _camera.ScreenToWorldPoint(Input.mousePosition);
            float dx = mouse.x - origin.x;
            float dy = mouse.y - origin.y;
            if (Mathf.Abs(dx) < 1f && Mathf.Abs(dy) < 1f)
            {
                return;
            }
            // The engine works in board coordinates, which run y-DOWN; world y runs up.
            double angle = Math.Atan2(-dy, dx);
            if (!force && !double.IsNaN(_aimAngle) && Math.Abs(Delta(angle, _aimAngle)) < 0.004)
            {
                return;
            }
            _aimAngle = angle;
            _light.SetPreview(_board.PreviewBright(_aimFrom, angle));
            if (_beamIndicator != null)
            {
                float length = (float)(_board.Db.Flashlight.LengthInSpacePitches * _board.Map.SpacePitch);
                float degrees = (float)(-angle * Mathf.Rad2Deg);
                _beamIndicator.rotation = Quaternion.Euler(0, 0, degrees);
                _beamIndicator.position = origin +
                    Quaternion.Euler(0, 0, degrees) * new Vector3(length / 2f, 0f, 0f);
            }
        }

        private static double Delta(double a, double b)
        {
            double d = a - b;
            while (d > Math.PI)
            {
                d -= 2 * Math.PI;
            }
            while (d < -Math.PI)
            {
                d += 2 * Math.PI;
            }
            return d;
        }

        /// <summary>The angle currently being aimed, in the engine's board-space radians.</summary>
        public double AimAngle => _aimAngle;

        // ---------------------------------------------------------- input loop

        public void Tick()
        {
            if (!_root.gameObject.activeSelf)
            {
                return;
            }
            bool overUi = EventSystem.current != null &&
                          EventSystem.current.IsPointerOverGameObject();

            HandleZoom(overUi);
            HandlePan(overUi);
            if (_aiming)
            {
                UpdateAim(force: false);
                if (!overUi && Input.GetMouseButtonUp(0) && !_dragMoved)
                {
                    var confirm = _aimConfirm;
                    double angle = _aimAngle;
                    EndAim();
                    if (confirm != null && !double.IsNaN(angle))
                    {
                        confirm(angle);
                    }
                }
                else if (Input.GetKeyDown(KeyCode.Escape))
                {
                    var cancel = _aimCancel;
                    EndAim();
                    cancel?.Invoke();
                }
            }
            else
            {
                HandleHoverAndClick(overUi);
            }
            _light.Tick();
        }

        private void HandleZoom(bool overUi)
        {
            float scroll = Input.mouseScrollDelta.y;
            if (overUi || Mathf.Abs(scroll) < 0.01f)
            {
                return;
            }
            var before = _camera.ScreenToWorldPoint(Input.mousePosition);
            _camera.orthographicSize = Mathf.Clamp(
                _camera.orthographicSize * Mathf.Pow(0.88f, scroll), MinOrthoSize, _fitOrthoSize * 1.2f);
            var after = _camera.ScreenToWorldPoint(Input.mousePosition);
            // Zoom toward the cursor: keep the world point under the mouse where it was.
            _camera.transform.position += before - after;
        }

        private void HandlePan(bool overUi)
        {
            bool panButton = Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if ((Input.GetMouseButtonDown(0) && !overUi) || Input.GetMouseButtonDown(1) ||
                Input.GetMouseButtonDown(2))
            {
                _dragging = true;
                _dragMoved = false;
                _dragOrigin = Input.mousePosition;
                _dragCameraOrigin = _camera.transform.position;
            }
            if (_dragging && (Input.GetMouseButton(0) || panButton))
            {
                var delta = Input.mousePosition - _dragOrigin;
                if (delta.magnitude > DragThreshold)
                {
                    _dragMoved = true;
                }
                if (_dragMoved && (panButton || !_aiming))
                {
                    float unitsPerPixel = _camera.orthographicSize * 2f / Screen.height;
                    _camera.transform.position = _dragCameraOrigin -
                        new Vector3(delta.x, delta.y, 0f) * unitsPerPixel;
                }
            }
            if (Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || Input.GetMouseButtonUp(2))
            {
                _dragging = false;
            }
        }

        private void HandleHoverAndClick(bool overUi)
        {
            if (overUi)
            {
                if (_hovered != null)
                {
                    _hovered = null;
                    Tooltip.Hide();
                }
                return;
            }
            var world = _camera.ScreenToWorldPoint(Input.mousePosition);
            string nearest = NearestSpace(world);
            if (nearest != _hovered)
            {
                _hovered = nearest;
                if (nearest == null)
                {
                    Tooltip.Hide();
                }
                else
                {
                    Tooltip.Show(SpaceTooltip(nearest));
                }
            }
            else if (nearest != null)
            {
                Tooltip.Follow();
            }
            if (Input.GetMouseButtonUp(0) && !_dragMoved && nearest != null)
            {
                SpaceClicked?.Invoke(nearest);
            }
        }

        private string NearestSpace(Vector3 world)
        {
            double best = double.MaxValue;
            string bestId = null;
            // Generous pick radius: a space circle plus a third, so clicks near a rim land.
            double limit = _board.Map.SpaceRadius * 1.3;
            foreach (var space in _board.Map.Spaces)
            {
                double dx = space.X - world.x;
                double dy = -space.Y - world.y;
                double distance = dx * dx + dy * dy;
                if (distance < best)
                {
                    best = distance;
                    bestId = space.Id;
                }
            }
            return best <= limit * limit ? bestId : null;
        }

        private string SpaceTooltip(string spaceId)
        {
            var space = _board.SpaceOrNull(spaceId);
            if (space == null)
            {
                return null;
            }
            var overlay = BoardModel.OverlayFrom(_view);
            var light = _board.LightOf(spaceId, overlay);
            string kind = Describe.SpaceKindName(space.Kind);
            var lines = new List<string>
            {
                "Space " + spaceId + (string.IsNullOrEmpty(kind) ? "" : "  ·  " + kind),
                _board.ZoneName(space.Zone) + "  ·  " + light,
            };
            if (_moveTargets.TryGetValue(spaceId, out int cost))
            {
                lines.Add("Move here: " + cost + " MP");
            }
            if (_notes.TryGetValue(spaceId, out var notes))
            {
                lines.AddRange(notes);
            }
            if (_view != null)
            {
                if (_view.Overlay.DoorStates.TryGetValue(spaceId, out var door) && door != DoorState.Open)
                {
                    lines.Add("Door: " + Describe.Door(door));
                }
            }
            return string.Join("\n", lines);
        }
    }
}
