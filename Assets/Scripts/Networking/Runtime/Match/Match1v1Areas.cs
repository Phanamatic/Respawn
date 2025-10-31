// Assets/Scripts/Networking/Runtime/Match/Match1v1Areas.cs
// Adds GetTeamCollider() used by controller.
using System.Collections.Generic;
using UnityEngine;

namespace Game.Net
{
    public enum TeamId : byte { A = 0, B = 1 }

    [DisallowMultipleComponent]
    public sealed class Match1v1Areas : MonoBehaviour
    {
        [Header("Assign BoxColliders (legacy - unused at runtime)")]
        [SerializeField, Tooltip("Legacy fields kept only for prefab compatibility. Ignored at runtime.")] private BoxCollider teamAArea;
        [SerializeField, Tooltip("Legacy fields kept only for prefab compatibility. Ignored at runtime.")] private BoxCollider teamBArea;
        [SerializeField, Tooltip("Legacy fields kept only for prefab compatibility. Ignored at runtime.")] private BoxCollider neutralArea;
// Legacy fields preserved to avoid prefab breakage, but no longer used anywhere at runtime.

        [Header("Unified map area (new)")]
        [SerializeField, Tooltip("Single BoxCollider covering the whole playable map. If set, the script splits it 45/10/45 into TeamA/Neutral/TeamB.")]
        private BoxCollider mapArea;
        public enum SplitAxis { X, Z }
        [SerializeField] private SplitAxis splitAxis = SplitAxis.X;
        [SerializeField, Range(0.05f, 0.9f)] private float teamPercent = 0.45f;
        [SerializeField, Range(0.0f, 0.9f)] private float neutralPercent = 0.10f;

        [Header("Spawn Blocking")]
        [SerializeField] private string noSpawnTag = "No Spawn";
// Tag must exactly match the scene objects' Tag; fixes missed blockers due to casing/spacing.

        // Force unified mode; legacy is removed from runtime. If mapArea is missing we return empty bounds and log.
        bool UseUnified => true;
        bool _swapSides; // set by controller at halftime

        public bool HasAll => mapArea != null;
// We now require a single mapArea to be assigned.

        public string BlockingTag => noSpawnTag;

        // Halftime swap control (called by controller)
        public void SetSwapSides(bool swap) { _swapSides = swap; }

        // Public bounds accessors that abstract legacy vs unified modes
        public Bounds GetMapBounds()
        {
            if (mapArea)
                return mapArea.bounds;

            Debug.LogError("[Match1v1Areas] mapArea is not assigned. Assign a single BoxCollider that covers the whole playable map.");
            return new Bounds(transform.position, Vector3.zero);
        }
// Removes legacy fallback; mapArea is the single source of truth.

        public Bounds GetTeamBounds(TeamId team)
        {
            if (!mapArea)
                return new Bounds(transform.position, Vector3.zero);

            var (a, n, b) = ComputeSplitBounds(mapArea.bounds, splitAxis, teamPercent, neutralPercent, _swapSides);
            return team == TeamId.A ? a : b;
        }
// Always split the single map area 45/10/45; honors halftime swap.

        public Bounds GetNeutralBounds()
        {
            if (!mapArea)
                return new Bounds(transform.position, Vector3.zero);

            var (_, n, _) = ComputeSplitBounds(mapArea.bounds, splitAxis, teamPercent, neutralPercent, _swapSides);
            return n;
        }
// Neutral is the center 10% from the unified map bounds.

        public bool Contains(TeamId team, Vector3 worldPoint)
        {
            return GetTeamBounds(team).Contains(worldPoint);
        }

        public Vector3 GetRandomPoint(TeamId team)
        {
            var b = GetTeamBounds(team);
            return b.size.sqrMagnitude > 0f ? RandomPointInBounds(b) : transform.position;
        }

        public bool IsAreaBlocked(TeamId team)
        {
            if (string.IsNullOrEmpty(noSpawnTag)) return false;
            var b = GetTeamBounds(team);
            return BoundsContainsTag(b, noSpawnTag, null);
        }

        public Vector3 GetNeutralCenter()
        {
            var b = GetNeutralBounds();
            return b.size.sqrMagnitude > 0f ? b.center : transform.position;
        }

        [System.Obsolete("Legacy team colliders are no longer used at runtime. Use GetTeamBounds().")]
        public BoxCollider GetTeamCollider(TeamId team) => null;
// Prevents any accidental legacy usage without breaking existing serialized references.

        // NEW: fine-grained spawn blocking helpers + random sampler that avoids "No Spawn" triggers.
        public bool IsPointBlockedForTeam(TeamId team, Vector3 worldPoint)
        {
            var b = GetTeamBounds(team);
            if (!b.Contains(worldPoint)) return true;
            return IsPointInAnyBlocker(worldPoint, b, noSpawnTag);
        }

        public bool IsPointBlockedInBounds(Bounds areaBounds, Vector3 worldPoint)
        {
            return IsPointInAnyBlocker(worldPoint, areaBounds, noSpawnTag);
        }

        public Vector3 GetRandomUnblockedPoint(TeamId team, int maxTries = 128)
        {
            var b = GetTeamBounds(team);
            if (b.size.sqrMagnitude <= 0f) return transform.position;

            int tries = Mathf.Max(8, maxTries);
            for (int i = 0; i < tries; i++)
            {
                var p = RandomPointInBounds(b);
                if (!IsPointInAnyBlocker(p, b, noSpawnTag))
                    return p;
            }
            // Deterministic rings around center...
            Vector3 c = b.center;
            float rx = Mathf.Max(1f, b.extents.x * 0.25f);
            float rz = Mathf.Max(1f, b.extents.z * 0.25f);
            for (int t = 0; t < 32; t++)
            {
                float ang = (t / 32f) * Mathf.PI * 2f;
                var p = new Vector3(c.x + Mathf.Cos(ang) * rx, c.y, c.z + Mathf.Sin(ang) * rz);
                if (b.Contains(p) && !IsPointInAnyBlocker(p, b, noSpawnTag))
                    return p;
                rx *= 1.08f; rz *= 1.08f;
            }
            return GetFallbackSpawn(team);
        }

        // World-space AABB (XZ) for each "No Spawn" trigger overlapping the given area bounds.
        // Used by client to paint red holes.
        public List<Bounds> GetBlockerIntersectionsFor(Bounds area, List<Bounds> into = null)
        {
            if (into == null) into = new List<Bounds>(8);
            into.Clear();

            // Ignore Y. Treat blockers as columns over the area.
            var tallCenter  = new Vector3(area.center.x, 0f, area.center.z);
            var tallExtents = new Vector3(area.extents.x, 5000f, area.extents.z);
            var hits = Physics.OverlapBox(tallCenter, tallExtents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i];
                if (!col || !col.CompareTag(noSpawnTag)) continue;

                var inter = IntersectXZ(area, col.bounds);
                if (inter.size.x > 0.001f && inter.size.z > 0.001f)
                {
                    inter.center = new Vector3(inter.center.x, area.center.y, inter.center.z);
                    into.Add(inter);
                }
            }
// XZ-only carve-outs so red holes always appear even if Y doesn’t overlap.
            return into;
        }

        // ---------- Internals ----------
        static (Bounds teamA, Bounds neutral, Bounds teamB) ComputeSplitBounds(Bounds map, SplitAxis axis, float teamPct, float neutralPct, bool swap)
        {
            teamPct = Mathf.Clamp01(teamPct);
            neutralPct = Mathf.Clamp01(neutralPct);
            float totalTeam = teamPct * 2f + neutralPct;
            if (totalTeam <= 0f) totalTeam = 1f;

            // Normalize to exactly 45/10/45 if values not summing to 1
            float L = axis == SplitAxis.X ? map.size.x : map.size.z;
            float teamLen = L * (teamPct / (teamPct * 2f + neutralPct));
            float neutralLen = L * (neutralPct / (teamPct * 2f + neutralPct));

            // Left-to-right or bottom-to-top depending on axis
            Vector3 min = map.min;
            Vector3 max = map.max;

            Bounds a = map, n = map, b = map;

            if (axis == SplitAxis.X)
            {
                float x0 = min.x;
                float x1 = x0 + teamLen;
                float x2 = x1 + neutralLen;
                float x3 = max.x;

                // A | Neutral | B in world, then swap if needed
                a.SetMinMax(new Vector3(x0, min.y, min.z), new Vector3(x1, max.y, max.z));
                n.SetMinMax(new Vector3(x1, min.y, min.z), new Vector3(x2, max.y, max.z));
                b.SetMinMax(new Vector3(x2, min.y, min.z), new Vector3(x3, max.y, max.z));
            }
            else
            {
                float z0 = min.z;
                float z1 = z0 + teamLen;
                float z2 = z1 + neutralLen;
                float z3 = max.z;

                a.SetMinMax(new Vector3(min.x, min.y, z0), new Vector3(max.x, max.y, z1));
                n.SetMinMax(new Vector3(min.x, min.y, z1), new Vector3(max.x, max.y, z2));
                b.SetMinMax(new Vector3(min.x, min.y, z2), new Vector3(max.x, max.y, z3));
            }

            if (swap)
                return (b, n, a);
            return (a, n, b);
        }

        static Bounds IntersectXZ(Bounds a, Bounds b)
        {
            var min = new Vector3(Mathf.Max(a.min.x, b.min.x), 0f, Mathf.Max(a.min.z, b.min.z));
            var max = new Vector3(Mathf.Min(a.max.x, b.max.x), 0f, Mathf.Min(a.max.z, b.max.z));
            if (max.x < min.x || max.z < min.z) return new Bounds(Vector3.zero, Vector3.zero);
            var size = new Vector3(max.x - min.x, 0f, max.z - min.z);
            var center = new Vector3(min.x + size.x * 0.5f, 0f, min.z + size.z * 0.5f);
            return new Bounds(center, size);
        }

        static bool IsPointInAnyBlocker(Vector3 p, Bounds searchArea, string tag)
        {
            // XZ-only test. Very tall overlap, then horizontal containment.
            var tallCenter  = new Vector3(searchArea.center.x, 0f, searchArea.center.z);
            var tallExtents = new Vector3(searchArea.extents.x, 5000f, searchArea.extents.z);
            var overlaps = Physics.OverlapBox(tallCenter, tallExtents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < overlaps.Length; i++)
            {
                var col = overlaps[i];
                if (!col || !col.CompareTag(tag)) continue;

                var b = col.bounds;
                if (p.x >= b.min.x && p.x <= b.max.x &&
                    p.z >= b.min.z && p.z <= b.max.z)
                    return true;
            }
            return false;
        }
// Prevents green where a blocker exists at different Y. Stops invalid clicks.

        public Vector3 GetFallbackSpawn(TeamId team)
        {
            var n = GetNeutralBounds();
            if (n.size.sqrMagnitude <= 0f)
                return (team == TeamId.A ? transform.position + Vector3.left : transform.position + Vector3.right);

            // Offset slightly left/right along world X so teams do not overlap exactly at neutral center.
            float offset = Mathf.Max(1f, n.extents.x * 0.5f);
            return n.center + (team == TeamId.A ? Vector3.left : Vector3.right) * offset;
        }
// Uses unified neutral bounds instead of legacy neutralArea collider.

        static bool BoundsContainsTag(Bounds bounds, string tag, Collider ignore)
        {
            // Area considered blocked if any blocker overlaps in XZ regardless of Y.
            var tallCenter  = new Vector3(bounds.center.x, 0f, bounds.center.z);
            var tallExtents = new Vector3(bounds.extents.x, 5000f, bounds.extents.z);
            var hits = Physics.OverlapBox(tallCenter, tallExtents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i];
                if (!col || col == ignore) continue;
                if (col.CompareTag(tag)) return true;
            }
            return false;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (!mapArea) return;

            // Draw the single map area
            Gizmos.color = new Color(1f, 1f, 1f, 0.6f);
            Gizmos.DrawWireCube(mapArea.bounds.center, mapArea.bounds.size);

            // Draw computed 45/10/45 split for quick visual verification
            var (a, n, b) = ComputeSplitBounds(mapArea.bounds, splitAxis, teamPercent, neutralPercent, _swapSides);

            Gizmos.color = new Color(0.2f, 0.8f, 0.2f, 0.9f); Gizmos.DrawWireCube(a.center, a.size);
            Gizmos.color = new Color(0.2f, 0.6f, 1f, 0.9f); Gizmos.DrawWireCube(n.center, n.size);
            Gizmos.color = new Color(0.8f, 0.2f, 0.2f, 0.9f); Gizmos.DrawWireCube(b.center, b.size);
        }
#endif
// Gizmos now preview the unified split instead of legacy colliders.

        static Vector3 RandomPointInBounds(Bounds b)
        {
            return new Vector3(
                Random.Range(b.min.x, b.max.x),
                Random.Range(b.min.y, b.max.y),
                Random.Range(b.min.z, b.max.z)
            );
        }
    }
}
