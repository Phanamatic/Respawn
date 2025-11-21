using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Net
{
    public sealed class PlayerHudBinder : MonoBehaviour
    {
        [Header("Assign in Inspector")]
        [SerializeField] private Image sprintFill;
        [SerializeField] private TMP_Text sprintLabel;
        [SerializeField] private Image dashFill;
        [SerializeField] private TMP_Text dashLabel;
    [SerializeField] private Image healthFill;
    [SerializeField] private TMP_Text healthLabel;

        private void OnEnable() => StartCoroutine(BindWhenReady());

        private PlayerNetwork FindLocalOwner()
        {
            var players = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < players.Length; i++)
            {
                if (players[i] && players[i].IsOwner && players[i].IsSpawned)
                    return players[i];
            }
            return null;
        }

        private IEnumerator BindWhenReady()
        {
            PlayerNetwork bound = null;
            while (true)
            {
                var owner = FindLocalOwner();
                if (owner != bound)
                {
                    if (bound)
                        bound.ClearHud();

                    bound = owner;
                    if (bound)
                    {
                        bound.AssignHud(sprintFill, sprintLabel, dashFill, dashLabel, healthFill, healthLabel);
                        MatchScoreboardPanel.AttachToHud(transform, bound);
                    }
                }

                // Light polling to follow despawn/respawn between rounds without allocations.
                yield return new WaitForSecondsRealtime(0.25f);
            }
        }
    }
}
