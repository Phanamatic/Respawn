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
        [Header("Assign BoxColliders (isTrigger=true is fine)")]
        [SerializeField] private BoxCollider teamAArea;
        [SerializeField] private BoxCollider teamBArea;
        [SerializeField] private BoxCollider neutralArea;
        [Header("Spawn Blocking")]
        [SerializeField] private string noSpawnTag = "No Spawn";
        // Match required Tag casing exactly.

        public bool HasAll => teamAArea && teamBArea && neutralArea;

        public string BlockingTag => noSpawnTag;

        public bool Contains(TeamId team, Vector3 worldPoint)
        {
            var c = team == TeamId.A ? teamAArea : teamBArea;
            return c && c.bounds.Contains(worldPoint);
        }

        public Vector3 GetRandomPoint(TeamId team)
        {
            var c = team == TeamId.A ? teamAArea : teamBArea;
            return c ? RandomPointInBounds(c.bounds) : transform.position;
        }

        public bool IsAreaBlocked(TeamId team)
        {
            var c = GetTeamCollider(team);
            if (!c || string.IsNullOrEmpty(noSpawnTag)) return false;
            return BoundsContainsTag(c.bounds, noSpawnTag, c);
        }

        public Vector3 GetNeutralCenter() => neutralArea ? neutralArea.bounds.center : transform.position;

        public BoxCollider GetTeamCollider(TeamId team) => team == TeamId.A ? teamAArea : teamBArea;

        // NEW: fine-grained spawn blocking helpers + random sampler that avoids "No Spawn" triggers.
        public bool IsPointBlockedForTeam(TeamId team, Vector3 worldPoint)
        {
            var area = GetTeamCollider(team);
            if (!area) return true;
            var b = area.bounds;
            if (!b.Contains(worldPoint)) return true;
            return IsPointInAnyBlocker(worldPoint, b, noSpawnTag);
        }

        public bool IsPointBlockedInBounds(Bounds areaBounds, Vector3 worldPoint)
        {
            return IsPointInAnyBlocker(worldPoint, areaBounds, noSpawnTag);
        }

        public Vector3 GetRandomUnblockedPoint(TeamId team, int maxTries = 128)
        {
            var area = GetTeamCollider(team);
            if (!area) return transform.position;

            var b = area.bounds;
            int tries = Mathf.Max(8, maxTries);
            for (int i = 0; i < tries; i++)
            {
                var p = RandomPointInBounds(b);
                if (!IsPointInAnyBlocker(p, b, noSpawnTag))
                    return p;
            }

            // Deterministic fallback ring samples around center
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

            var hits = Physics.OverlapBox(area.center, area.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i];
                if (!col || col.tag != noSpawnTag) continue;

                var inter = IntersectXZ(area, col.bounds);
                if (inter.size.x > 0.001f && inter.size.z > 0.001f)
                {
                    inter.center = new Vector3(inter.center.x, area.center.y, inter.center.z);
                    into.Add(inter);
                }
            }
            return into;
        }

        // ---------- Internals ----------
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
            var overlaps = Physics.OverlapBox(searchArea.center, searchArea.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < overlaps.Length; i++)
            {
                var col = overlaps[i];
                if (!col || col.tag != tag) continue;
                if (!col.bounds.Contains(p)) continue;
                // ClosestPoint returns p when point lies inside a trigger volume
                var cp = col.ClosestPoint(p);
                if ((cp - p).sqrMagnitude <= 1e-6f)
                    return true;
            }
            return false;
        }

        public Vector3 GetFallbackSpawn(TeamId team)
        {
            var neutralCenter = GetNeutralCenter();
            if (!neutralArea)
            {
                return (team == TeamId.A ? transform.position + Vector3.left : transform.position + Vector3.right);
            }

            // Offset slightly left/right along world X so teams do not overlap exactly at neutral center.
            float offset = Mathf.Max(1f, neutralArea.bounds.extents.x * 0.5f);
            return neutralCenter + (team == TeamId.A ? Vector3.left : Vector3.right) * offset;
        }

        static bool BoundsContainsTag(Bounds bounds, string tag, Collider ignore)
        {
            var hits = Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i];
                if (!col || col == ignore) continue;
                if (col.tag == tag) return true;
            }
            return false;
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            void Draw(BoxCollider c, Color color)
            {
                if (!c) return;
                Gizmos.color = color;
                Gizmos.DrawWireCube(c.bounds.center, c.bounds.size);
            }
            Draw(teamAArea, new Color(0.2f, 0.8f, 0.2f, 1f));
            Draw(teamBArea, new Color(0.8f, 0.2f, 0.2f, 1f));
            Draw(neutralArea, new Color(0.2f, 0.6f, 1f, 1f));
        }
#endif

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
