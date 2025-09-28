// Simple cross-process directory using a JSON file in persistentDataPath.
// All servers write heartbeats; clients read to find an open server.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Game.Net
{
    public static class SessionDirectory
    {
        [Serializable]
        private sealed class EntryList { public List<Entry> entries = new(); }

        [Serializable]
        public sealed class Entry
        {
            public string id;
            public string code;
            public string type;        // "lobby" | "1v1" | "2v2"
            public int   max;
            public int   threshold;
            public int   current;      // connections (clients)
            public string scene;
            public string exe;
            public long  updatedUnix;  // seconds
        }

        private static readonly string FilePath = Path.Combine(Application.persistentDataPath, "mps_directory.json");
        private static readonly Mutex FileMutex = new Mutex(false, "Global\\MpsDirectoryMutex");

        public static void Upsert(Entry e)
        {
            SafeEdit(list =>
            {
                var ix = list.entries.FindIndex(x => x.id == e.id);
                if (ix >= 0) list.entries[ix] = e; else list.entries.Add(e);
            });
        }

        public static void Remove(string id)
        {
            SafeEdit(list => list.entries.RemoveAll(e => e.id == id));
        }

        public static List<Entry> GetSnapshot(Func<Entry, bool> filter = null)
        {
            TryLock(1500);
            try
            {
                var data = Load();
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // purge stale (> 45s)
                data.entries.RemoveAll(e => now - e.updatedUnix > 45);
                Save(data);
                if (filter == null) return new List<Entry>(data.entries);
                return data.entries.FindAll(new Predicate<Entry>(filter));
            }
            finally { Unlock(); }
        }

        private static void SafeEdit(Action<EntryList> edit)
        {
            TryLock(1500);
            try
            {
                var data = Load();
                edit?.Invoke(data);
                Save(data);
            }
            finally { Unlock(); }
        }

        private static EntryList Load()
        {
            if (!File.Exists(FilePath)) return new EntryList();
            try
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var data = JsonUtility.FromJson<EntryList>(string.IsNullOrWhiteSpace(json) ? "{}" : json);
                return data ?? new EntryList();
            }
            catch { return new EntryList(); }
        }

        private static void Save(EntryList data)
        {
            var json = JsonUtility.ToJson(data, true);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json, Encoding.UTF8);
        }

        private static void TryLock(int ms) { try { FileMutex.WaitOne(ms); } catch { } }
        private static void Unlock() { try { FileMutex.ReleaseMutex(); } catch { } }
    }
}
