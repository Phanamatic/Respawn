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

        private void OnEnable() => StartCoroutine(BindWhenReady());

        private IEnumerator BindWhenReady()
        {
            PlayerNetwork p = null;
            while (p == null)
            {
                var players = FindObjectsByType<PlayerNetwork>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                for (int i = 0; i < players.Length; i++)
                {
                    if (players[i] && players[i].IsOwner) { p = players[i]; break; }
                }
                if (p == null) yield return null;
            }
            p.AssignHud(sprintFill, sprintLabel, dashFill, dashLabel);
        }
    }
}
