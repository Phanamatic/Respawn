// Admin UI to list servers from SessionDirectory and send shutdown signals.
// Scene wiring: add to a desktop admin build (not headless).
// Requires: TMP_Text statusText, Button refreshButton, Button shutdownAllButton,
// Transform listRoot (with VerticalLayoutGroup), GameObject rowPrefab (contains Button + TMP_Text).

using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Net
{
    public sealed class ServerControlPanel : MonoBehaviour
    {
        [Header("UI")]
        [SerializeField] private TMP_Text statusText;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Button shutdownAllButton;
        [SerializeField] private Transform listRoot;
        [SerializeField] private GameObject rowPrefab;

        private List<SessionDirectory.Entry> _entries = new();

        private void OnEnable()
        {
            if (refreshButton) refreshButton.onClick.AddListener(Refresh);
            if (shutdownAllButton) shutdownAllButton.onClick.AddListener(ShutdownAll);
            Refresh();
        }

        private void OnDisable()
        {
            if (refreshButton) refreshButton.onClick.RemoveListener(Refresh);
            if (shutdownAllButton) shutdownAllButton.onClick.RemoveListener(ShutdownAll);
        }

        public void Refresh()
        {
            _entries = SessionDirectory.GetSnapshot(e => e.type == "lobby" || e.type == "1v1" || e.type == "2v2");
            ClearList();
            SetStatus($"{_entries.Count} servers");

            foreach (var e in _entries)
                AddRow(e);
        }

        private void AddRow(SessionDirectory.Entry e)
        {
            var go = Instantiate(rowPrefab, listRoot);
            var txt = go.GetComponentInChildren<TMP_Text>(true);
            var btn = go.GetComponentInChildren<Button>(true);

            if (txt) txt.text = $"{e.type.ToUpper()}  code:{e.code}  {e.current}/{e.max}  scene:{e.scene}";
            if (btn) btn.onClick.AddListener(() => SendShutdown(e.id));
        }

        private void ClearList()
        {
            for (int i = listRoot.childCount - 1; i >= 0; i--)
                Destroy(listRoot.GetChild(i).gameObject);
        }

        private void SendShutdown(string sessionId)
        {
            var path = BuildShutdownPath(sessionId);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? "");
                File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
                SetStatus($"Shutdown signaled: {sessionId}");
            }
            catch (Exception ex)
            {
                SetStatus("Error: " + ex.Message);
            }
        }

        private void ShutdownAll()
        {
            foreach (var e in _entries) SendShutdown(e.id);
        }

        private static string BuildShutdownPath(string id) =>
            Path.Combine(Application.persistentDataPath, "mps_control", id + ".shutdown");

        private void SetStatus(string s) { if (statusText) statusText.text = s; }
    }
}
