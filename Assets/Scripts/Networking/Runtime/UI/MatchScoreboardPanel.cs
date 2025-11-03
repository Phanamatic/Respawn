using System;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Net
{
    [DisallowMultipleComponent]
    public sealed class MatchScoreboardPanel : MonoBehaviour
    {
        [Serializable]
        public sealed class PlayerRowBinding
        {
            public RectTransform root;
            public TMP_Text nameLabel;
            public TMP_Text pingLabel;
            public Image background;
            public Image iconImage;

            public void SetActive(bool active)
            {
                if (!root) return;
                var go = root.gameObject;
                if (go.activeSelf != active) go.SetActive(active);
            }
        }

        [SerializeField] CanvasGroup canvasGroup;
        [SerializeField] RectTransform rowsRoot;
        [SerializeField] TMP_Text headerLabel;
        [SerializeField] Color localHighlight = new Color(0.22f, 0.54f, 0.84f, 0.35f);
        [SerializeField] Color evenRowColor = new Color(1f, 1f, 1f, 0.08f);
        [SerializeField] Color oddRowColor = new Color(1f, 1f, 1f, 0.02f);
        [SerializeField, Min(0.1f)] float refreshInterval = 0.35f;
    [Header("Team Icon Targets")]
    [SerializeField] Image teamAIconImage;
    [SerializeField] Image teamBIconImage;
    [SerializeField] Sprite fallbackIcon;
    [SerializeField] bool hideTeamIconWhenMissing = true;

        readonly List<PlayerRowBinding> _rows = new List<PlayerRowBinding>();
        readonly List<Entry> _entries = new List<Entry>();

        PlayerNetwork _owner;
        bool _visible;
        float _nextRefresh;
    Sprite _teamAIconResolved;
    Sprite _teamBIconResolved;

        struct Entry
        {
            public ulong clientId;
            public string name;
            public int ping;
            public bool isLocal;
            public TeamId team;
            public Sprite icon;
            public string iconId;
            public int health;
            public int kills;
            public int deaths;
            public int damage;
        }

        void Awake()
        {
            canvasGroup ??= GetComponent<CanvasGroup>();
            if (canvasGroup == null)
            {
                canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            if (rowsRoot == null)
            {
                rowsRoot = LocateRowsRoot(transform);
            }
            if (rowsRoot == null)
            {
                rowsRoot = CreateRowsRoot(transform);
            }

            if (headerLabel == null)
            {
                headerLabel = LocateHeader(transform);
            }
            if (headerLabel == null)
            {
                headerLabel = CreateHeader(transform);
            }

            if (headerLabel != null && string.IsNullOrEmpty(headerLabel.text))
            {
                headerLabel.text = "Scoreboard";
            }

            AutoAssignTeamIconTargets();
            if (!fallbackIcon)
                fallbackIcon = Game.Services.ProfileIconLookup.Resolve(null);
        }

        void OnDestroy()
        {
            if (_owner != null)
            {
                _owner.UnregisterScoreboard(this);
                _owner = null;
            }
        }

        public void SetOwner(PlayerNetwork owner)
        {
            if (_owner == owner) return;
            if (_owner != null) _owner.UnregisterScoreboard(this);
            _owner = owner;
            if (_owner != null) _owner.RegisterScoreboard(this);
            Refresh();
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (canvasGroup != null)
            {
                canvasGroup.alpha = visible ? 1f : 0f;
                canvasGroup.interactable = visible;
                canvasGroup.blocksRaycasts = visible;
            }
            else
            {
                gameObject.SetActive(visible);
            }

            if (visible)
            {
                Refresh();
                _nextRefresh = Time.unscaledTime + refreshInterval;
            }
            else
            {
                _nextRefresh = 0f;
            }
        }

        void Update()
        {
            if (!_visible) return;
            if (Time.unscaledTime < _nextRefresh) return;
            Refresh();
            _nextRefresh = Time.unscaledTime + refreshInterval;
        }

        void Refresh()
        {
            if (rowsRoot == null) return;

            _entries.Clear();
            Sprite teamAIcon = null;
            Sprite teamBIcon = null;

            var manager = NetworkManager.Singleton;
            if (manager != null && manager.IsClient)
            {
                var clients = manager.ConnectedClientsList;
                for (int i = 0; i < clients.Count; i++)
                {
                    var client = clients[i];
                    if (client == null) continue;

                    ulong clientId = client.ClientId;
                    var playerObj = client.PlayerObject;
                    var player = playerObj ? playerObj.GetComponent<PlayerNetwork>() : null;
                    string name = player != null ? player.GetDisplayName() : $"Player {clientId}";
                    int ping = QueryPing(manager, clientId);
                    bool isLocal = _owner != null && clientId == _owner.OwnerClientId;
                    TeamId team = player != null ? player.GetTeam() : TeamId.A;
                    string iconId = player != null ? player.GetIconId() : null;
                    var icon = Game.Services.ProfileIconLookup.Resolve(iconId) ?? fallbackIcon;
                    var stats = player != null ? player.GetCombatStats() : default;
                    int health = player != null ? Mathf.RoundToInt(player.GetHealth()) : 0;

                    if (team == TeamId.A && teamAIcon == null) teamAIcon = icon;
                    if (team == TeamId.B && teamBIcon == null) teamBIcon = icon;

                    _entries.Add(new Entry
                    {
                        clientId = clientId,
                        name = name,
                        ping = ping,
                        isLocal = isLocal,
                        team = team,
                        icon = icon,
                        iconId = iconId,
                        health = health,
                        kills = stats.kills,
                        deaths = stats.deaths,
                        damage = stats.damage
                    });
                }
            }

            if (_entries.Count > 1)
            {
                _entries.Sort(CompareEntries);
            }

            _teamAIconResolved = teamAIcon;
            _teamBIconResolved = teamBIcon;

            if (headerLabel != null)
            {
                headerLabel.text = _entries.Count > 0 ? $"Players ({_entries.Count})" : "Scoreboard";
            }

            ApplyEntries();
            ApplyTeamIcons();
        }

        static int CompareEntries(Entry a, Entry b)
        {
            int teamCompare = a.team.CompareTo(b.team);
            if (teamCompare != 0) return teamCompare;
            if (a.isLocal != b.isLocal) return a.isLocal ? -1 : 1;
            int nameCompare = string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            if (nameCompare != 0) return nameCompare;
            return a.clientId.CompareTo(b.clientId);
        }

        void ApplyEntries()
        {
            EnsureRowCapacity(_entries.Count);

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool active = i < _entries.Count;
                row.SetActive(active);
                if (!active) continue;

                var entry = _entries[i];
                if (row.nameLabel != null)
                {
                    int healthValue = Mathf.Clamp(entry.health, 0, 999);
                    row.nameLabel.text = $"{entry.name}  HP:{healthValue.ToString("D3")}";
                }
                if (row.pingLabel != null)
                {
                    var pingText = entry.ping >= 0 ? $"{entry.ping} ms" : "—";
                    row.pingLabel.text = $"K:{entry.kills} D:{entry.deaths} DMG:{entry.damage}   {pingText}";
                }
                if (row.background != null)
                {
                    var baseColor = entry.team == TeamId.A ? evenRowColor : oddRowColor;
                    row.background.color = entry.isLocal ? localHighlight : baseColor;
                }
                if (row.iconImage != null)
                {
                    var iconToUse = entry.icon != null ? entry.icon : fallbackIcon;
                    if (iconToUse != null)
                    {
                        row.iconImage.sprite = iconToUse;
                        row.iconImage.enabled = true;
                    }
                    else
                    {
                        row.iconImage.sprite = null;
                        row.iconImage.enabled = false;
                    }
                }
            }
        }

        void ApplyTeamIcons()
        {
            AssignTeamIcon(teamAIconImage, _teamAIconResolved);
            AssignTeamIcon(teamBIconImage, _teamBIconResolved);
        }

        void AssignTeamIcon(Image target, Sprite sprite)
        {
            if (!target) return;
            var resolved = sprite ? sprite : fallbackIcon;
            if (resolved)
            {
                target.sprite = resolved;
                target.enabled = true;
            }
            else if (hideTeamIconWhenMissing)
            {
                target.enabled = false;
            }
            else
            {
                target.sprite = null;
            }
        }

        void EnsureRowCapacity(int needed)
        {
            if (rowsRoot == null) return;
            for (int i = _rows.Count; i < needed; i++)
            {
                _rows.Add(CreateRow(i));
            }
        }

        void AutoAssignTeamIconTargets()
        {
            var images = GetComponentsInChildren<Image>(true);
            for (int i = 0; i < images.Length; i++)
            {
                var img = images[i];
                if (!img) continue;
                var name = img.name;
                if (teamAIconImage == null && string.Equals(name, "icon image (team 1)", StringComparison.OrdinalIgnoreCase))
                {
                    teamAIconImage = img;
                    continue;
                }
                if (teamBIconImage == null && string.Equals(name, "icon image (team 2)", StringComparison.OrdinalIgnoreCase))
                {
                    teamBIconImage = img;
                }
            }
        }

        PlayerRowBinding CreateRow(int index)
        {
            var go = new GameObject($"Row{index}", typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(rowsRoot, false);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(0f, 32f);

            var image = go.AddComponent<Image>();
            image.color = evenRowColor;
            image.raycastTarget = false;

            var layout = go.AddComponent<LayoutElement>();
            layout.preferredHeight = 32f;
            layout.minHeight = 32f;
            layout.flexibleHeight = 0f;

            var iconGo = new GameObject("Icon", typeof(RectTransform));
            var iconRect = iconGo.GetComponent<RectTransform>();
            iconRect.SetParent(rect, false);
            iconRect.anchorMin = new Vector2(0f, 0f);
            iconRect.anchorMax = new Vector2(0f, 1f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.offsetMin = new Vector2(6f, 4f);
            iconRect.offsetMax = new Vector2(42f, -4f);
            iconRect.sizeDelta = new Vector2(36f, 0f);

            var iconImage = iconGo.AddComponent<Image>();
            iconImage.preserveAspect = true;
            iconImage.raycastTarget = false;

            var nameGo = new GameObject("Name", typeof(RectTransform));
            var nameRect = nameGo.GetComponent<RectTransform>();
            nameRect.SetParent(rect, false);
            nameRect.anchorMin = new Vector2(0f, 0f);
            nameRect.anchorMax = new Vector2(0.7f, 1f);
            nameRect.offsetMin = new Vector2(50f, 4f);
            nameRect.offsetMax = new Vector2(-8f, -4f);

            var nameText = nameGo.AddComponent<TextMeshProUGUI>();
            nameText.fontSize = 22f;
            nameText.alignment = TextAlignmentOptions.MidlineLeft;
            nameText.textWrappingMode = TextWrappingModes.NoWrap;
            nameText.color = Color.white;

            var pingGo = new GameObject("Ping", typeof(RectTransform));
            var pingRect = pingGo.GetComponent<RectTransform>();
            pingRect.SetParent(rect, false);
            pingRect.anchorMin = new Vector2(0.7f, 0f);
            pingRect.anchorMax = new Vector2(1f, 1f);
            pingRect.offsetMin = new Vector2(0f, 4f);
            pingRect.offsetMax = new Vector2(-12f, -4f);

            var pingText = pingGo.AddComponent<TextMeshProUGUI>();
            pingText.fontSize = 22f;
            pingText.alignment = TextAlignmentOptions.MidlineRight;
            pingText.textWrappingMode = TextWrappingModes.NoWrap;
            pingText.color = new Color(0.8f, 0.85f, 0.95f, 1f);

            return new PlayerRowBinding
            {
                root = rect,
                background = image,
                iconImage = iconImage,
                nameLabel = nameText,
                pingLabel = pingText
            };
        }

        static RectTransform LocateRowsRoot(Transform root)
        {
            if (!root) return null;
            var groups = root.GetComponentsInChildren<VerticalLayoutGroup>(true);
            for (int i = 0; i < groups.Length; i++)
            {
                var candidate = groups[i];
                if (!candidate) continue;
                var rect = candidate.transform as RectTransform;
                if (rect != null && rect != root) return rect;
            }
            return null;
        }

        TMP_Text LocateHeader(Transform root)
        {
            if (!root) return null;
            var texts = root.GetComponentsInChildren<TMP_Text>(true);
            for (int i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (!text) continue;
                if (text.transform == root) continue;
                var parent = text.transform.parent;
                if (parent == root || text.name.IndexOf("header", StringComparison.OrdinalIgnoreCase) >= 0)
                    return text;
            }
            return null;
        }

        static TMP_Text CreateHeader(Transform parent)
        {
            var go = new GameObject("Header", typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(12f, -40f);
            rect.offsetMax = new Vector2(-12f, -8f);

            var text = go.AddComponent<TextMeshProUGUI>();
            text.fontSize = 26f;
            text.alignment = TextAlignmentOptions.Center;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.color = Color.white;
            text.text = "Scoreboard";
            return text;
        }

        static RectTransform CreateRowsRoot(Transform parent)
        {
            var go = new GameObject("Rows", typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(12f, 12f);
            rect.offsetMax = new Vector2(-12f, -60f);

            var layoutGroup = go.AddComponent<VerticalLayoutGroup>();
            layoutGroup.spacing = 4f;
            layoutGroup.childControlWidth = true;
            layoutGroup.childControlHeight = true;
            layoutGroup.childForceExpandHeight = false;
            layoutGroup.childForceExpandWidth = true;
            layoutGroup.padding = new RectOffset(0, 0, 0, 0);
            return rect;
        }

        static int QueryPing(NetworkManager manager, ulong clientId)
        {
            try
            {
                if (manager == null) return -1;
                if (manager.NetworkConfig.NetworkTransport is UnityTransport utp)
                {
                    ulong rttRaw = utp.GetCurrentRtt(clientId);
                    if (rttRaw <= (ulong)int.MaxValue)
                    {
                        return (int)rttRaw;
                    }
                    return int.MaxValue;
                }
            }
            catch
            {
            }
            return -1;
        }

        public static void AttachToHud(Transform hudRoot, PlayerNetwork owner)
        {
            if (hudRoot == null || owner == null) return;

            var panel = hudRoot.GetComponentInChildren<MatchScoreboardPanel>(true);
            if (panel == null)
            {
                panel = CreateRuntimePanel(hudRoot);
            }

            panel?.SetOwner(owner);
        }

        static MatchScoreboardPanel CreateRuntimePanel(Transform hudRoot)
        {
            var go = new GameObject("ScoreboardPanel", typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(hudRoot, false);
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -120f);
            rect.sizeDelta = new Vector2(420f, 260f);

            var image = go.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.55f);
            image.raycastTarget = false;

            var canvasGroup = go.AddComponent<CanvasGroup>();
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;

            var header = CreateHeader(rect);
            var rows = CreateRowsRoot(rect);

            var team1 = new GameObject("Icon Image (Team 1)", typeof(RectTransform));
            var team1Rect = team1.GetComponent<RectTransform>();
            team1Rect.SetParent(rect, false);
            team1Rect.anchorMin = new Vector2(0f, 1f);
            team1Rect.anchorMax = new Vector2(0f, 1f);
            team1Rect.pivot = new Vector2(0f, 1f);
            team1Rect.offsetMin = new Vector2(12f, -92f);
            team1Rect.offsetMax = new Vector2(64f, -40f);
            var team1Image = team1.AddComponent<Image>();
            team1Image.preserveAspect = true;
            team1Image.raycastTarget = false;

            var team2 = new GameObject("Icon Image (Team 2)", typeof(RectTransform));
            var team2Rect = team2.GetComponent<RectTransform>();
            team2Rect.SetParent(rect, false);
            team2Rect.anchorMin = new Vector2(1f, 1f);
            team2Rect.anchorMax = new Vector2(1f, 1f);
            team2Rect.pivot = new Vector2(1f, 1f);
            team2Rect.offsetMin = new Vector2(-64f, -92f);
            team2Rect.offsetMax = new Vector2(-12f, -40f);
            var team2Image = team2.AddComponent<Image>();
            team2Image.preserveAspect = true;
            team2Image.raycastTarget = false;

            var panel = go.AddComponent<MatchScoreboardPanel>();
            panel.canvasGroup = canvasGroup;
            panel.headerLabel = header;
            panel.rowsRoot = rows;
            panel.teamAIconImage = team1Image;
            panel.teamBIconImage = team2Image;
            if (!panel.fallbackIcon)
                panel.fallbackIcon = Game.Services.ProfileIconLookup.Resolve(null);
            return panel;
        }
    }
}
