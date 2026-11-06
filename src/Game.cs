// Round Minimap for Graveyard Keeper — original work by Abaddon Blackstar.
// Game.cs: the only place that talks to the game's own classes (typed access, read-only).
using System.Collections.Generic;
using UnityEngine;

namespace RoundMinimap
{
    internal enum NodeKind { Wood = 0, Mineral = 1, Forage = 2, Npc = 3 }

    internal struct MapNode
    {
        public Vector2 Pos;
        public NodeKind Kind;
        public bool Locked; // the keeper hasn't researched what is needed to work it yet
    }

    internal static class Game
    {
        private static WorldGameObject _lastPlayer;
        private static SpriteRenderer[] _playerSprites;

        /// <summary>A save is loaded and the keeper exists in the world.</summary>
        public static bool InWorld
        {
            get
            {
                if (!MainGame.game_started) return false;
                MainGame mg = MainGame.me;
                return mg != null && mg.player != null && mg.player.gameObject.activeInHierarchy;
            }
        }

        public static WorldGameObject Player { get { return MainGame.me.player; } }

        public static Vector2 PlayerPosition { get { return Player.transform.position; } }

        /// <summary>The keeper is inside a building (house, church, morgue, cellar...).</summary>
        public static bool Indoors
        {
            get
            {
                BaseCharacterComponent ch = MainGame.me.player_char;
                return ch != null && ch.cur_environment == BaseCharacterComponent.Environment.Inside;
            }
        }

        /// <summary>Any game window (inventory, dialogue, crafting...) or the game's own map is open.</summary>
        public static bool WindowOpen
        {
            get
            {
                if (!BaseGUI.all_guis_closed) return true;
                GUIElements gui = GUIElements.me;
                return gui != null && gui.map != null && gui.map.is_shown;
            }
        }

        public static bool LanguageIsPortuguese
        {
            get
            {
                GameSettings s = GameSettings.me;
                string lng = s != null ? s.language : null;
                if (string.IsNullOrEmpty(lng)) return Application.systemLanguage == SystemLanguage.Portuguese;
                return lng.StartsWith("pt");
            }
        }

        public static List<WorldGameObject> WorldObjects { get { return WorldMap.objs; } }

        /// <summary>Same check the game uses for its "need technology" bubble.</summary>
        public static bool WorkUnlocked(WorldGameObject w)
        {
            try
            {
                GameSave save = MainGame.me.save;
                return save == null || save.IsWorkAvailible(w.obj_def);
            }
            catch
            {
                return true;
            }
        }

        /// <summary>All sprite renderers of the keeper (cached per player instance).</summary>
        public static SpriteRenderer[] PlayerSprites
        {
            get
            {
                WorldGameObject p = Player;
                if (p != _lastPlayer || _playerSprites == null)
                {
                    _lastPlayer = p;
                    _playerSprites = p.GetComponentsInChildren<SpriteRenderer>(true);
                }
                return _playerSprites;
            }
        }

        /// <summary>
        /// Decides what a world object is from the game's own data: the tool the object asks for.
        /// Axe = wood, pickaxe/shovel = mineral, bare hand = forageable; NPC definitions = NPC.
        /// </summary>
        private static bool Has(string s, string part) { return s.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0; }

        public static bool TryClassify(WorldGameObject w, out NodeKind kind)
        {
            kind = NodeKind.Forage;
            if (w == null || w.is_removed || w.is_dead || w.is_player) return false;
            ObjectDefinition def = w.obj_def;
            if (def == null) return false;

            if (def.IsNPC())
            {
                kind = NodeKind.Npc;
                return true;
            }
            if (def.type != ObjectDefinition.ObjType.Default) return false;
            if (def.interaction_type == ObjectDefinition.InteractionType.Grave) return false;

            ToolActions tools = def.tool_actions;
            if (tools == null || tools.no_actions) return false;

            if (tools.HasToolK(ItemDefinition.ItemType.Axe)) { kind = NodeKind.Wood; return true; }
            if (tools.HasToolK(ItemDefinition.ItemType.Pickaxe)) { kind = NodeKind.Mineral; return true; }
            string objId = w.obj_id ?? string.Empty;
            if (tools.HasToolK(ItemDefinition.ItemType.Shovel))
            {
                if (Has(objId, "grave")) return false;
                // stumps and bushes are dug out with a shovel but they are wood, not stone
                kind = Has(objId, "stump") || Has(objId, "bush") ? NodeKind.Wood : NodeKind.Mineral;
                return true;
            }
            if (tools.HasToolK(ItemDefinition.ItemType.Hand))
            {
                // picked by hand, but only "loose" things: no workstation/chest/script attached, and not inside buildings
                if (def.interaction_type != ObjectDefinition.InteractionType.None) return false;
                if (def.inventory_size > 0) return false;
                if (Has(objId, "well")) return false; // wells are used by hand but aren't something to collect
                kind = NodeKind.Forage;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The game only keeps objects active within a few "chunks" of the screen. When the minimap shows
    /// more than the screen, we widen that radius so its edges are not empty; restored when not needed.
    /// </summary>
    internal static class ChunkRadius
    {
        private static ChunkManager _manager;
        private static bool _widened;

        public static void Cover(float viewHeightInScreens, int spare)
        {
            if (_manager == null) _manager = Object.FindObjectOfType<ChunkManager>();
            if (_manager == null) return;
            // the game uses (screen pixels / 160) + 1 per axis; the minimap is square, so both axes follow the height
            int needed = Mathf.CeilToInt(Screen.height * viewHeightInScreens / 160f) + 1 + spare;
            if (_manager.visible_chunk_radius_x < needed) { _manager.visible_chunk_radius_x = needed; _widened = true; }
            if (_manager.visible_chunk_radius_y < needed) { _manager.visible_chunk_radius_y = needed; _widened = true; }
        }

        public static void Release()
        {
            if (!_widened) return;
            _widened = false;
            ChunkManager.RecalculateResolution(-1, -1);
        }
    }

    /// <summary>
    /// Walks the world object list a slice per frame (no big spikes), then publishes a fresh snapshot.
    /// </summary>
    internal sealed class NodeScanner
    {
        private const int PerFrame = 600;
        private const float RestBetweenPasses = 1.5f;

        private readonly List<MapNode> _building = new List<MapNode>(512);
        private List<MapNode> _published = new List<MapNode>(512);
        private int _cursor;
        private float _resumeAt;

        public List<MapNode> Nodes { get { return _published; } }

        // debug: obj_ids seen per kind during the last pass
        public readonly Dictionary<NodeKind, HashSet<string>> SeenIds = new Dictionary<NodeKind, HashSet<string>>();
        public bool CollectIds;
        public event System.Action PassFinished;

        public void Reset()
        {
            _building.Clear();
            _published.Clear();
            _cursor = 0;
            _resumeAt = 0f;
        }

        public void Step()
        {
            if (Time.unscaledTime < _resumeAt) return;
            List<WorldGameObject> objs = Game.WorldObjects;
            if (objs == null) return;

            if (_cursor == 0 && CollectIds) SeenIds.Clear();
            int end = Mathf.Min(objs.Count, _cursor + PerFrame);
            for (int i = _cursor; i < end; i++)
            {
                WorldGameObject w = objs[i];
                NodeKind kind;
                if (w == null || !w.gameObject.activeInHierarchy || !Game.TryClassify(w, out kind)) continue;
                bool locked = kind != NodeKind.Npc && !Game.WorkUnlocked(w);
                _building.Add(new MapNode { Pos = w.transform.position, Kind = kind, Locked = locked });
                if (CollectIds)
                {
                    HashSet<string> set;
                    if (!SeenIds.TryGetValue(kind, out set)) SeenIds[kind] = set = new HashSet<string>();
                    if (set.Count < 60) set.Add(w.obj_id + (locked ? "(L)" : "") + (Game.Indoors ? "*" : ""));
                }
            }
            _cursor = end;
            if (_cursor < objs.Count) return;

            // pass complete: swap lists
            List<MapNode> old = _published;
            _published = new List<MapNode>(_building);
            _building.Clear();
            old.Clear();
            _cursor = 0;
            _resumeAt = Time.unscaledTime + RestBetweenPasses;
            if (PassFinished != null) PassFinished();
        }
    }
}
