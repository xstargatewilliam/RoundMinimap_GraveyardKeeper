// Round Minimap for Graveyard Keeper — original work by Abaddon Blackstar.
// Plugin.cs: settings, the minimap camera and the on-screen HUD.
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace RoundMinimap
{
    public enum MapShape { Round, Square }
    public enum UiLanguage { Auto, English, Portuguese }
    public enum LockedStyle { Dimmed, Hidden, Normal }

    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "abaddonblackstar.gyk.roundminimap";
        public const string Name = "Round Minimap";
        public const string Version = "2.2.0";

        // how much of the world (in "screen heights") each zoom level shows
        private static readonly float[] ZoomSpan = { 2.6f, 1.9f, 1.4f, 1.0f, 0.7f };
        private static readonly string[] ZoomEn = { "widest", "wide", "medium", "close", "closest" };
        private static readonly string[] ZoomPt = { "bem aberto", "aberto", "médio", "próximo", "bem próximo" };

        private const float Ring = 5f;
        private const int Segments = 96;

        // ---------- settings ----------
        private ConfigEntry<bool> _visible;
        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<MapShape> _shape;
        private ConfigEntry<int> _size;
        private ConfigEntry<float> _opacity;
        private ConfigEntry<UiLanguage> _language;
        private ConfigEntry<int> _zoomOut, _zoomIn;
        private ConfigEntry<bool> _indoorZoom;
        private ConfigEntry<bool> _useHead, _showNorth;
        private ConfigEntry<LockedStyle> _lockedStyle;
        private ConfigEntry<int> _playerSize, _iconSize;
        private ConfigEntry<bool> _layerWood, _layerMineral, _layerForage, _layerNpc;
        private ConfigEntry<bool> _keepLoaded;
        private ConfigEntry<float> _fps;
        private ConfigEntry<float> _posX, _posY;
        private ConfigEntry<bool> _debugIds;

        // ---------- runtime ----------
        private readonly NodeScanner _scanner = new NodeScanner();
        private Camera _cam;
        private RenderTexture _rt;
        private Material _texMat, _flatMat;
        private bool _noShader;
        private Texture2D _white, _tree, _rock, _shroom, _npc, _pin, _lock;
        private readonly Dictionary<Texture2D, Texture2D> _grey = new Dictionary<Texture2D, Texture2D>();
        private GUIStyle _btn, _row, _label, _north;
        private bool _active, _panelOpen, _pressed, _dragging;
        private Vector2 _grab, _pressAt;
        private float _nextFrame;
        private SpriteRenderer _head;

        private bool Round { get { return _shape.Value == MapShape.Round && !_noShader; } }
        private ConfigEntry<int> ZoomSetting { get { return _indoorZoom.Value && Game.Indoors ? _zoomIn : _zoomOut; } }
        private int ZoomIndex { get { return Mathf.Clamp(ZoomSetting.Value, 0, ZoomSpan.Length - 1); } }
        private bool Pt
        {
            get
            {
                if (_language.Value == UiLanguage.English) return false;
                if (_language.Value == UiLanguage.Portuguese) return true;
                return Game.LanguageIsPortuguese;
            }
        }
        private string L(string en, string pt) { return Pt ? pt : en; }
        private static string Both(string en, string pt) { return en + "\n[PT-BR] " + pt; }

        private void Awake()
        {
            _visible = Config.Bind("1. General", "Visible", true, Both("Show the minimap.", "Mostra o minimapa."));
            _toggleKey = Config.Bind("1. General", "Toggle Key", new KeyboardShortcut(KeyCode.F6),
                Both("Shows/hides the minimap. Avoid M: it opens the game's own map.", "Mostra/esconde o minimapa. Evite M: abre o mapa do jogo."));
            _shape = Config.Bind("1. General", "Shape", MapShape.Round, Both("Round or Square.", "Redondo (Round) ou Quadrado (Square)."));
            _size = Config.Bind("1. General", "Size", 240, new ConfigDescription(Both("Diameter in pixels.", "Diâmetro em pixels."), new AcceptableValueRange<int>(140, 480)));
            _opacity = Config.Bind("1. General", "Opacity", 0.95f, new ConfigDescription(Both("Map opacity.", "Opacidade do mapa."), new AcceptableValueRange<float>(0.25f, 1f)));
            _language = Config.Bind("1. General", "Language", UiLanguage.Auto, Both("Label language. Auto follows the game.", "Idioma dos textos. Auto segue o jogo."));

            _zoomOut = Config.Bind("2. Zoom", "Outdoor Zoom", 1, new ConfigDescription(
                Both("0 = widest ... 4 = closest. Also set with the + / - buttons.", "0 = bem aberto ... 4 = bem próximo. Também pelos botões + e -."), new AcceptableValueRange<int>(0, ZoomSpan.Length - 1)));
            _indoorZoom = Config.Bind("2. Zoom", "Separate Indoor Zoom", true,
                Both("Remember a different zoom inside buildings, so rooms fill the map.", "Guarda um zoom próprio dentro de construções, para o cômodo preencher o mapa."));
            _zoomIn = Config.Bind("2. Zoom", "Indoor Zoom", 3, new ConfigDescription(
                Both("Zoom used inside buildings.", "Zoom usado dentro de construções."), new AcceptableValueRange<int>(0, ZoomSpan.Length - 1)));

            _useHead = Config.Bind("3. Markers", "Character Head Marker", true, Both("Use your keeper's head as the marker.", "Usa a cabeça do seu personagem como marcador."));
            _playerSize = Config.Bind("3. Markers", "Player Marker Size", 26, new ConfigDescription(Both("Pixels.", "Pixels."), new AcceptableValueRange<int>(14, 48)));
            _iconSize = Config.Bind("3. Markers", "Icon Size", 16, new ConfigDescription(Both("Size of resource/NPC icons in pixels.", "Tamanho dos ícones de recursos/NPCs em pixels."), new AcceptableValueRange<int>(8, 32)));
            _showNorth = Config.Bind("3. Markers", "Show North", true, Both("Show the N on the ring.", "Mostra o N na moldura."));
            _lockedStyle = Config.Bind("3. Markers", "Locked Resources", LockedStyle.Dimmed,
                Both("Resources you can't work yet (research needed): Dimmed = grey with a padlock, Hidden, or Normal.",
                     "Recursos que você ainda não pode trabalhar (falta pesquisa): Dimmed = cinza com cadeado, Hidden = escondidos, Normal = iguais aos outros."));

            _layerWood = Config.Bind("4. Layers", "Wood", false, Both("Anything you cut with an axe.", "Tudo o que se corta com machado."));
            _layerMineral = Config.Bind("4. Layers", "Minerals", true, Both("Anything you mine with a pickaxe or dig with a shovel (not graves).", "Tudo o que se minera com picareta ou cava com pá (exceto túmulos)."));
            _layerForage = Config.Bind("4. Layers", "Forageables", true, Both("Anything you pick by hand (berries, mushrooms, flowers...).", "Tudo o que se colhe com a mão (frutinhas, cogumelos, flores...)."));
            _layerNpc = Config.Bind("4. Layers", "NPCs", true, Both("Villagers and other characters.", "Moradores e outros personagens."));

            _keepLoaded = Config.Bind("5. Performance", "Keep Map Area Loaded", true,
                Both("Keeps everything inside the minimap loaded, so nothing pops in when you run. Turn off on slow PCs.",
                     "Mantém carregado tudo o que aparece no minimapa, sem coisas surgindo quando você corre. Desligue em PCs fracos."));
            _fps = Config.Bind("5. Performance", "Refresh Rate", 30f, new ConfigDescription(Both("Map redraws per second.", "Atualizações do mapa por segundo."), new AcceptableValueRange<float>(10f, 60f)));

            _posX = Config.Bind("6. Position", "X", -1f, new ConfigDescription(Both("Saved by dragging. -1 = bottom-right corner.", "Salvo ao arrastar. -1 = canto inferior direito."), new AcceptableValueRange<float>(-1f, 1f)));
            _posY = Config.Bind("6. Position", "Y", -1f, new ConfigDescription(Both("Saved by dragging. -1 = bottom-right corner.", "Salvo ao arrastar. -1 = canto inferior direito."), new AcceptableValueRange<float>(-1f, 1f)));

            _debugIds = Config.Bind("7. Debug", "Log Classified Objects", false,
                Both("Writes the object ids found per layer to the BepInEx log (for bug reports).", "Grava no log do BepInEx os ids encontrados em cada camada (para relatar erros)."));
            _scanner.PassFinished += OnScanPass;

            BuildTextures();
            Logger.LogInfo(Name + " " + Version + " loaded.");
        }

        // =================================================================
        // Frame update
        // =================================================================
        private void Update()
        {
            if (_toggleKey.Value.IsDown()) _visible.Value = !_visible.Value;

            bool inWorld = SafeInWorld();
            _active = inWorld && _visible.Value && !Game.WindowOpen;

            if (!inWorld || !_visible.Value || !_keepLoaded.Value) ChunkRadius.Release();
            if (!inWorld) { _scanner.Reset(); _dragging = _pressed = false; _reportedThisLoad = false; return; }
            if (!_visible.Value) return;

            if (_keepLoaded.Value) ChunkRadius.Cover(ZoomSpan[ZoomIndex], 2);
            _scanner.CollectIds = _debugIds.Value || !_reportedThisLoad;
            _scanner.Step();

            if (!_active || Time.unscaledTime < _nextFrame) return;
            _nextFrame = Time.unscaledTime + 1f / _fps.Value;
            RenderMap();
        }

        private static bool SafeInWorld()
        {
            try { return Game.InWorld; }
            catch { return false; }
        }

        private bool _reportedThisLoad;

        // one short report per loaded save (and on demand), so bug reports show what each layer found
        private void OnScanPass()
        {
            if (_reportedThisLoad && !_debugIds.Value) return;
            int[] count = new int[4];
            foreach (MapNode n in _scanner.Nodes) count[(int)n.Kind]++;
            // wait for a pass that actually saw resources (right after loading, or indoors, the list can be empty)
            if (!_debugIds.Value && count[0] + count[1] + count[2] == 0) return;
            Logger.LogInfo(string.Format("Scan: wood={0} minerals={1} forage={2} npcs={3}", count[0], count[1], count[2], count[3]));
            foreach (KeyValuePair<NodeKind, HashSet<string>> kv in _scanner.SeenIds)
                Logger.LogInfo("[" + kv.Key + "] " + string.Join(", ", new List<string>(kv.Value).ToArray()));
            _reportedThisLoad = true;
            if (_debugIds.Value) _debugIds.Value = false;
        }

        private void RenderMap()
        {
            Camera main = Camera.main;
            if (main == null) return;
            int texSize = _size.Value > 220 ? 512 : 256;
            if (_rt == null || _rt.width != texSize)
            {
                if (_rt != null) { _rt.Release(); Destroy(_rt); }
                _rt = new RenderTexture(texSize, texSize, 16, RenderTextureFormat.ARGB32) { filterMode = FilterMode.Bilinear, name = "RoundMinimapRT" };
                _rt.Create();
            }
            if (_cam == null)
            {
                GameObject go = new GameObject("RoundMinimapCamera") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(go);
                _cam = go.AddComponent<Camera>();
                _cam.enabled = false;
            }

            _cam.CopyFrom(main);
            _cam.enabled = false;
            _cam.targetTexture = _rt;
            _cam.rect = new Rect(0f, 0f, 1f, 1f);
            _cam.aspect = 1f;
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.07f, 0.06f, 0.05f, 1f);
            // centred on the keeper, not on the (lagging) main camera
            Vector2 p = Game.PlayerPosition;
            _cam.transform.position = new Vector3(p.x, p.y, main.transform.position.z);
            _cam.transform.rotation = main.transform.rotation;
            float span = ZoomSpan[ZoomIndex];
            if (main.orthographic) _cam.orthographicSize = main.orthographicSize * span;
            else _cam.fieldOfView = Mathf.Min(170f, main.fieldOfView * span);

            // draw the world without the keeper (the marker replaces him)
            SpriteRenderer[] body = Game.PlayerSprites;
            for (int i = 0; i < body.Length; i++) if (body[i] != null) body[i].forceRenderingOff = true;
            try { _cam.Render(); }
            finally { for (int i = 0; i < body.Length; i++) if (body[i] != null) body[i].forceRenderingOff = false; }
        }

        // =================================================================
        // HUD
        // =================================================================
        private void OnGUI()
        {
            if (!_active || _rt == null || _cam == null) return;
            MakeStyles();

            Rect map = MapRect();
            Drag(ref map);

            if (Event.current.type == EventType.Repaint)
            {
                if (Round && Materials()) DrawRound(map);
                else DrawSquare(map);
                DrawNodes(map);
                DrawPlayer(map);
                if (_showNorth.Value) DrawNorth(map);
            }
            DrawButtons(map);
        }

        // ---- layout ----
        private Rect MapRect()
        {
            float s = _size.Value;
            float edge = Ring + 8f;
            float minX = edge, minY = edge + 12f;
            float maxX = Mathf.Max(minX, Screen.width - s - edge - 34f);
            float maxY = Mathf.Max(minY, Screen.height - s - edge);
            float x = _posX.Value < 0f ? maxX - 12f : Mathf.Lerp(minX, maxX, _posX.Value);
            float y = _posY.Value < 0f ? maxY - 12f : Mathf.Lerp(minY, maxY, _posY.Value);
            return new Rect(x, y, s, s);
        }

        private void StorePosition(Rect map, bool save)
        {
            float s = _size.Value;
            float edge = Ring + 8f;
            float minX = edge, minY = edge + 12f;
            float maxX = Mathf.Max(minX + 1f, Screen.width - s - edge - 34f);
            float maxY = Mathf.Max(minY + 1f, Screen.height - s - edge);
            bool auto = Config.SaveOnConfigSet;
            Config.SaveOnConfigSet = false;
            _posX.Value = Mathf.Clamp01((map.x - minX) / (maxX - minX));
            _posY.Value = Mathf.Clamp01((map.y - minY) / (maxY - minY));
            Config.SaveOnConfigSet = auto;
            if (save) Config.Save();
        }

        private bool OnMap(Vector2 m, Rect map)
        {
            return Round ? (m - map.center).sqrMagnitude <= Sq(map.width * 0.5f + Ring) : map.Contains(m);
        }

        private void Drag(ref Rect map)
        {
            Event e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && OnMap(e.mousePosition, map) && !OverButtons(e.mousePosition, map))
                    {
                        _pressed = true;
                        _pressAt = e.mousePosition;
                        _grab = e.mousePosition - map.position;
                        e.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (!_pressed) break;
                    if ((e.mousePosition - _pressAt).sqrMagnitude > 9f) _dragging = true;
                    if (_dragging)
                    {
                        map.position = e.mousePosition - _grab;
                        StorePosition(map, false);
                        map = MapRect();
                    }
                    e.Use();
                    break;
                case EventType.MouseUp:
                    if (!_pressed) break;
                    if (_dragging) StorePosition(map, true);
                    _pressed = _dragging = false;
                    e.Use();
                    break;
            }
        }

        // ---- drawing ----
        private bool Materials()
        {
            if (_texMat != null) return true;
            if (_noShader) return false;
            Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
            if (sh == null) { _noShader = true; Logger.LogWarning("No sprite shader found, using the square map."); return false; }
            _texMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            _flatMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave, mainTexture = _white };
            return true;
        }

        private void DrawRound(Rect map)
        {
            Vector2 c = map.center;
            float r = map.width * 0.5f;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, Screen.width, Screen.height, 0f);

            _flatMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            Fan(c, r + Ring + 2f, new Color(0.08f, 0.06f, 0.04f, 0.95f), false);
            Fan(c, r + Ring, new Color(0.72f, 0.57f, 0.31f, 1f), false);
            Fan(c, r + 1.5f, new Color(0.25f, 0.18f, 0.1f, 1f), false);
            GL.End();

            _texMat.mainTexture = _rt;
            _texMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            Fan(c, r, new Color(1f, 1f, 1f, _opacity.Value), true);
            GL.End();

            _flatMat.SetPass(0);
            GL.Begin(GL.TRIANGLES);
            for (int k = 0; k < 4; k++)
            {
                float a = k * Mathf.PI * 0.5f;
                Vector2 p = c + new Vector2(Mathf.Cos(a), -Mathf.Sin(a)) * (r + Ring * 0.5f);
                Fan(p, 4.5f, new Color(0.08f, 0.06f, 0.04f, 1f), false);
                Fan(p, 3f, new Color(0.95f, 0.82f, 0.5f, 1f), false);
            }
            GL.End();
            GL.PopMatrix();
        }

        // triangle fan as a list of triangles; uv maps the disc onto the render texture when textured
        private static void Fan(Vector2 c, float r, Color col, bool textured)
        {
            float step = Mathf.PI * 2f / Segments;
            for (int i = 0; i < Segments; i++)
            {
                float a0 = i * step, a1 = a0 + step;
                Vertex(c, 0f, 0f, 0f, col, textured);
                Vertex(c, r, Mathf.Cos(a0), Mathf.Sin(a0), col, textured);
                Vertex(c, r, Mathf.Cos(a1), Mathf.Sin(a1), col, textured);
            }
        }

        private static void Vertex(Vector2 c, float r, float cos, float sin, Color col, bool textured)
        {
            GL.Color(col);
            if (textured) GL.TexCoord2(0.5f + 0.5f * cos, 0.5f + 0.5f * sin);
            else GL.TexCoord2(0.5f, 0.5f);
            GL.Vertex3(c.x + r * cos, c.y - r * sin, 0f);
        }

        private void DrawSquare(Rect map)
        {
            Fill(new Rect(map.x - 4f, map.y - 4f, map.width + 8f, map.height + 8f), new Color(0.08f, 0.06f, 0.04f, 0.95f));
            Fill(new Rect(map.x - 2f, map.y - 2f, map.width + 4f, map.height + 4f), new Color(0.72f, 0.57f, 0.31f, 1f));
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, _opacity.Value);
            GUI.DrawTexture(map, _rt, ScaleMode.StretchToFill, false);
            GUI.color = old;
        }

        private bool Project(Rect map, Vector2 world, float inset, out Vector2 screen)
        {
            Vector3 v = _cam.WorldToViewportPoint(new Vector3(world.x, world.y, 0f));
            screen = new Vector2(map.x + v.x * map.width, map.y + (1f - v.y) * map.height);
            if (Round) return (screen - map.center).sqrMagnitude <= Sq(map.width * 0.5f - inset);
            return v.x >= 0f && v.x <= 1f && v.y >= 0f && v.y <= 1f;
        }

        private void DrawNodes(Rect map)
        {
            float s = _iconSize.Value;
            List<MapNode> nodes = _scanner.Nodes;
            for (int i = 0; i < nodes.Count; i++)
            {
                MapNode n = nodes[i];
                Texture2D icon;
                switch (n.Kind)
                {
                    case NodeKind.Wood: if (!_layerWood.Value) continue; icon = _tree; break;
                    case NodeKind.Mineral: if (!_layerMineral.Value) continue; icon = _rock; break;
                    case NodeKind.Forage: if (!_layerForage.Value) continue; icon = _shroom; break;
                    default: if (!_layerNpc.Value) continue; icon = _npc; break;
                }
                bool dim = n.Locked && _lockedStyle.Value == LockedStyle.Dimmed;
                if (n.Locked && _lockedStyle.Value == LockedStyle.Hidden) continue;
                Vector2 p;
                if (!Project(map, n.Pos, s * 0.5f, out p)) continue;
                Rect r = new Rect(p.x - s * 0.5f, p.y - s * 0.5f, s, s);
                if (!dim)
                {
                    GUI.DrawTexture(r, icon, ScaleMode.StretchToFill, true);
                    continue;
                }
                GUI.DrawTexture(r, _grey[icon], ScaleMode.StretchToFill, true);
                float l = Mathf.Max(7f, s * 0.55f);
                GUI.DrawTexture(new Rect(r.xMax - l * 0.75f, r.yMax - l * 0.8f, l, l), _lock, ScaleMode.StretchToFill, true);
            }
        }

        private void DrawPlayer(Rect map)
        {
            float s = _playerSize.Value;
            Vector2 c = map.center; // the camera follows the keeper, so he is always in the middle
            GUI.DrawTexture(new Rect(c.x - s * 0.5f, c.y - s * 0.5f, s, s), _pin, ScaleMode.StretchToFill, true);
            if (_useHead.Value && DrawHead(new Rect(c.x - s * 0.42f, c.y - s * 0.42f, s * 0.84f, s * 0.84f))) return;
            float d = s * 0.32f;
            Color old = GUI.color;
            GUI.color = new Color(0.25f, 0.15f, 0.06f, 1f);
            GUI.DrawTexture(new Rect(c.x - d * 0.5f, c.y - d * 0.5f, d, d), _pin, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        private bool DrawHead(Rect area)
        {
            SpriteRenderer head = FindHead();
            if (head == null) return false;
            try
            {
                Sprite sp = head.sprite;
                Texture2D tex = sp.texture;
                Rect px = sp.textureRect;
                Rect uv = new Rect(px.x / tex.width, px.y / tex.height, px.width / tex.width, px.height / tex.height);
                if (head.flipX) uv = new Rect(uv.xMax, uv.y, -uv.width, uv.height);
                float aspect = px.width / Mathf.Max(1f, px.height);
                Rect dst = aspect >= 1f
                    ? new Rect(area.x, area.center.y - area.width / aspect * 0.5f, area.width, area.width / aspect)
                    : new Rect(area.center.x - area.height * aspect * 0.5f, area.y, area.height * aspect, area.height);
                GUI.DrawTextureWithTexCoords(dst, tex, uv, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private SpriteRenderer FindHead()
        {
            if (_head != null && _head.enabled && _head.sprite != null && LooksLikeHead(_head.sprite.name)) return _head;
            _head = null;
            foreach (SpriteRenderer sr in Game.PlayerSprites)
                if (sr != null && sr.enabled && sr.sprite != null && LooksLikeHead(sr.sprite.name)) { _head = sr; break; }
            return _head;
        }

        private static bool LooksLikeHead(string sprite)
        {
            string n = sprite.ToLowerInvariant();
            return n.Contains("_hed_") || n.Contains("head");
        }

        private void DrawNorth(Rect map)
        {
            float y = Round ? map.y - Ring - 9f : map.y + 2f;
            Rect r = new Rect(map.center.x - 10f, y, 20f, 18f);
            Color c = _north.normal.textColor;
            _north.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), "N", _north);
            _north.normal.textColor = c;
            GUI.Label(r, "N", _north);
        }

        // ---- buttons and layer panel ----
        private Rect BtnRect(Rect map, int i)
        {
            // stacked along the ring's upper-right edge (or the square's right side)
            if (Round)
            {
                float r = map.width * 0.5f + Ring + 14f;
                float a = Mathf.Deg2Rad * (52f - i * 17f);
                Vector2 p = map.center + new Vector2(Mathf.Cos(a), -Mathf.Sin(a)) * r;
                return new Rect(p.x - 11f, p.y - 11f, 22f, 22f);
            }
            return new Rect(map.xMax + 6f, map.y + i * 25f, 22f, 22f);
        }

        private Rect PanelRect(Rect map)
        {
            Rect b = BtnRect(map, 2);
            float w = 170f, h = 146f;
            float x = b.x - w - 6f;
            if (x < 4f) x = b.xMax + 6f;
            return new Rect(x, Mathf.Clamp(b.y, 4f, Screen.height - h - 4f), w, h);
        }

        private bool OverButtons(Vector2 m, Rect map)
        {
            for (int i = 0; i < 3; i++) if (BtnRect(map, i).Contains(m)) return true;
            return _panelOpen && PanelRect(map).Contains(m);
        }

        private void DrawButtons(Rect map)
        {
            if (GUI.Button(BtnRect(map, 0), "+", _btn)) SetZoom(ZoomSetting.Value + 1);
            if (GUI.Button(BtnRect(map, 1), "-", _btn)) SetZoom(ZoomSetting.Value - 1);
            if (GUI.Button(BtnRect(map, 2), L("L", "C"), _btn)) _panelOpen = !_panelOpen;
            if (!_panelOpen) return;

            Rect p = PanelRect(map);
            Fill(new Rect(p.x - 2f, p.y - 2f, p.width + 4f, p.height + 4f), new Color(0.72f, 0.57f, 0.31f, 1f));
            Fill(p, new Color(0.13f, 0.1f, 0.07f, 0.97f));
            string zoom = (Pt ? ZoomPt : ZoomEn)[ZoomIndex] + (_indoorZoom.Value && Game.Indoors ? L(" (indoors)", " (interior)") : "");
            GUI.Label(new Rect(p.x + 8f, p.y + 3f, p.width - 12f, 18f), L("Layers · zoom: ", "Camadas · zoom: ") + zoom, _label);
            LayerRow(new Rect(p.x + 6f, p.y + 24f, p.width - 12f, 22f), _tree, L("Wood", "Madeira"), _layerWood);
            LayerRow(new Rect(p.x + 6f, p.y + 48f, p.width - 12f, 22f), _rock, L("Minerals", "Minérios"), _layerMineral);
            LayerRow(new Rect(p.x + 6f, p.y + 72f, p.width - 12f, 22f), _shroom, L("Forageables", "Coletáveis"), _layerForage);
            LayerRow(new Rect(p.x + 6f, p.y + 96f, p.width - 12f, 22f), _npc, "NPCs", _layerNpc);
            GUI.DrawTexture(new Rect(p.x + 12f, p.y + 124f, 12f, 12f), _lock, ScaleMode.StretchToFill, true);
            GUI.Label(new Rect(p.x + 30f, p.y + 121f, p.width - 70f, 18f), L("= needs research", "= falta pesquisa"), _label);
            GUI.Label(new Rect(p.xMax - 40f, p.y + 121f, 38f, 18f), "v" + Version, _label);
        }

        private void LayerRow(Rect r, Texture2D icon, string text, ConfigEntry<bool> layer)
        {
            if (GUI.Button(r, GUIContent.none, _row)) layer.Value = !layer.Value;
            Color old = GUI.color;
            if (!layer.Value) GUI.color = new Color(1f, 1f, 1f, 0.35f);
            GUI.DrawTexture(new Rect(r.x + 4f, r.y + 3f, 16f, 16f), icon, ScaleMode.StretchToFill, true);
            GUI.color = old;
            GUI.Label(new Rect(r.x + 26f, r.y + 2f, r.width - 64f, 18f), text, _label);
            GUI.Label(new Rect(r.xMax - 38f, r.y + 2f, 36f, 18f), layer.Value ? L("ON", "SIM") : L("off", "não"), _label);
        }

        private void SetZoom(int level)
        {
            ZoomSetting.Value = Mathf.Clamp(level, 0, ZoomSpan.Length - 1);
            _nextFrame = 0f;
        }

        // =================================================================
        // Assets made in code (no external files)
        // =================================================================
        private void MakeStyles()
        {
            if (_btn != null) return;
            Color gold = new Color(0.98f, 0.88f, 0.6f, 1f);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(0, 0, 0, 2), border = new RectOffset(2, 2, 2, 2) };
            _btn.normal.background = Framed(new Color(0.18f, 0.13f, 0.08f, 0.97f), new Color(0.72f, 0.57f, 0.31f, 1f));
            _btn.hover.background = _btn.active.background = Framed(new Color(0.3f, 0.22f, 0.12f, 0.97f), new Color(0.95f, 0.8f, 0.45f, 1f));
            _btn.normal.textColor = _btn.hover.textColor = _btn.active.textColor = gold;
            _row = new GUIStyle(GUI.skin.button);
            _row.normal.background = Solid(new Color(0.22f, 0.16f, 0.1f, 1f));
            _row.hover.background = _row.active.background = Solid(new Color(0.32f, 0.24f, 0.14f, 1f));
            _label = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
            _label.normal.textColor = new Color(0.95f, 0.88f, 0.7f, 1f);
            _north = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            _north.normal.textColor = gold;
        }

        private void BuildTextures()
        {
            _white = Solid(Color.white);
            _tree = PixelArt(new[] {
                "....GGGG....", "...GGgGGG...", "..GGGGGGgG..", ".GGgGGGGGGG.", "..GGGGGgGG..", ".GGGGGGGGgG.",
                "GGgGGGGGGGGG", ".GGGGGgGGGG.", "...GGGGGG...", ".....TT.....", ".....TT.....", "....TTTT...." });
            _rock = PixelArt(new[] {
                "............", "............", "....SSSS....", "...SsSSSS...", "..SssSSSSS..", ".SSsSSSSdSS.",
                ".SSSSSSSSdS.", "SSSSSdSSSSSS", "SSdSSSSSSSdS", ".SSSSSSSSSS.", "..dddddddd..", "............" });
            _shroom = PixelArt(new[] {
                "............", "....RRRR....", "..RRWRRRRR..", ".RRRRRRRWRR.", "RRWRRRRRRRRR", "RRRRRRWRRRRR",
                "....wwww....", "....wwww....", "....wwww....", "...wwwwww...", "............", "............" });
            _npc = PixelArt(new[] {
                "............", "....YYYY....", "...YYYYYY...", "...YkYYkY...", "...YYYYYY...", "....YYYY....",
                "..BBBBBBBB..", ".BBBBBBBBBB.", ".BBBBBBBBBB.", ".BBBBBBBBBB.", "............", "............" });
            _lock = PixelArt(new[] {
                "..kkkk..", ".k....k.", ".k....k.", "OOOOOOOO", "OOOkkOOO", "OOOkkOOO", "OOOOOOOO" });
            foreach (Texture2D t in new[] { _tree, _rock, _shroom }) _grey[t] = Greyscale(t);
            _pin = Disc(32, new Color(0.95f, 0.78f, 0.38f, 1f), new Color(0.1f, 0.07f, 0.04f, 1f));
        }

        private static Texture2D NewTex(int w, int h, FilterMode f)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, filterMode = f, wrapMode = TextureWrapMode.Clamp };
        }

        private static Texture2D Solid(Color c)
        {
            Texture2D t = NewTex(1, 1, FilterMode.Point);
            t.SetPixel(0, 0, c);
            t.Apply(false, true);
            return t;
        }

        private static Texture2D Framed(Color fill, Color edge)
        {
            Texture2D t = NewTex(8, 8, FilterMode.Point);
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                    t.SetPixel(x, y, x < 2 || y < 2 || x > 5 || y > 5 ? edge : fill);
            t.Apply(false, true);
            return t;
        }

        private static Color Palette(char ch)
        {
            switch (ch)
            {
                case 'G': return new Color(0.27f, 0.66f, 0.24f);
                case 'g': return new Color(0.55f, 0.85f, 0.35f);
                case 'T': return new Color(0.47f, 0.3f, 0.15f);
                case 'S': return new Color(0.62f, 0.64f, 0.68f);
                case 's': return new Color(0.85f, 0.87f, 0.9f);
                case 'd': return new Color(0.4f, 0.41f, 0.45f);
                case 'R': return new Color(0.86f, 0.2f, 0.17f);
                case 'W': return new Color(1f, 0.96f, 0.9f);
                case 'w': return new Color(0.93f, 0.88f, 0.78f);
                case 'Y': return new Color(0.98f, 0.82f, 0.62f);
                case 'k': return new Color(0.15f, 0.1f, 0.06f);
                case 'B': return new Color(0.3f, 0.5f, 0.85f);
                case 'O': return new Color(0.95f, 0.72f, 0.2f);
                default: return Color.clear;
            }
        }

        // pixel-art sprite from text rows, with an automatic 1-pixel dark outline
        private static Texture2D PixelArt(string[] rows)
        {
            int h = rows.Length, w = rows[0].Length;
            Texture2D t = NewTex(w + 2, h + 2, FilterMode.Point);
            Color outline = new Color(0.08f, 0.05f, 0.03f, 1f);
            Func<int, int, bool> solid = (x, y) => x >= 0 && y >= 0 && x < w && y < h && Palette(rows[h - 1 - y][x]).a > 0f;
            for (int y = -1; y <= h; y++)
                for (int x = -1; x <= w; x++)
                {
                    Color c = Color.clear;
                    if (solid(x, y)) c = Palette(rows[h - 1 - y][x]);
                    else
                        for (int dy = -1; dy <= 1 && c.a == 0f; dy++)
                            for (int dx = -1; dx <= 1 && c.a == 0f; dx++)
                                if (solid(x + dx, y + dy)) c = outline;
                    t.SetPixel(x + 1, y + 1, c);
                }
            t.Apply(false, false); // stays readable so a grey copy can be made
            return t;
        }

        // same icon in shades of grey (outline kept dark), used for resources that still need research
        private static Texture2D Greyscale(Texture2D src)
        {
            Texture2D t = NewTex(src.width, src.height, FilterMode.Point);
            Color[] px = src.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                float g = c.r * 0.3f + c.g * 0.59f + c.b * 0.11f;
                g = Mathf.Lerp(0.35f, 0.82f, g); // lift mid-tones so it reads as "grey", not "dark"
                if (c.r + c.g + c.b < 0.4f) g = c.r; // keep the outline dark
                px[i] = new Color(g, g, g * 1.04f, c.a);
            }
            t.SetPixels(px);
            t.Apply(false, true);
            return t;
        }

        private static Texture2D Disc(int n, Color fill, Color edge)
        {
            Texture2D t = NewTex(n, n, FilterMode.Bilinear);
            float r = n * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x + 0.5f - r, y + 0.5f - r).magnitude;
                    Color c = d <= r - 3f ? fill : d <= r ? edge : Color.clear;
                    if (d > r - 0.5f && d <= r) c.a = (r - d) * 2f;
                    t.SetPixel(x, y, c);
                }
            t.Apply(false, true);
            return t;
        }

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        private static float Sq(float v) { return v * v; }

        private void OnDestroy()
        {
            ChunkRadius.Release();
            if (_cam != null) Destroy(_cam.gameObject);
            if (_rt != null) { _rt.Release(); Destroy(_rt); }
            if (_texMat != null) Destroy(_texMat);
            if (_flatMat != null) Destroy(_flatMat);
        }
    }
}
