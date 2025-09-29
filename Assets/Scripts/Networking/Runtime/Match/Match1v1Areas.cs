// Assets/Scripts/Networking/Runtime/Match/Match1v1Areas.cs
// Adds GetTeamCollider() used by controller.
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

        public bool HasAll => teamAArea && teamBArea && neutralArea;

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

        public Vector3 GetNeutralCenter() => neutralArea ? neutralArea.bounds.center : transform.position;

        public BoxCollider GetTeamCollider(TeamId team) => team == TeamId.A ? teamAArea : teamBArea;

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
