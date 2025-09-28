// Assets/Scripts/Networking/Runtime/SessionDirectory.cs
// Simple cross-process directory using a JSON file.
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

        private static readonly string FilePath;
        internal static readonly string BaseDirectory;
        private static readonly Mutex FileMutex = new Mutex(false, "Global\\MpsDirectoryMutex");

        static SessionDirectory()
        {
            FilePath = ResolveFilePath(out var baseDir);
            BaseDirectory = baseDir;
#if UNITY_EDITOR
            Debug.Log($"[SessionDirectory] Using directory: {FilePath}");
#endif
        }

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

        private static string ResolveFilePath(out string baseDir)
        {
            // Allow explicit overrides first.
            var overridePath = Environment.GetEnvironmentVariable("MPS_DIRECTORY_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                try
                {
                    var full = Path.GetFullPath(overridePath);
                    baseDir = Path.GetDirectoryName(full) ?? Application.persistentDataPath;
                    return full;
                }
                catch { }
            }

            var overrideRoot = Environment.GetEnvironmentVariable("MPS_DIRECTORY_ROOT");
            if (!string.IsNullOrWhiteSpace(overrideRoot))
            {
                try
                {
                    var fullRoot = Path.GetFullPath(overrideRoot);
                    Directory.CreateDirectory(fullRoot);
                    baseDir = fullRoot;
                    return Path.Combine(fullRoot, "mps_directory.json");
                }
                catch { }
            }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var shared = TryBuildSharedDirectory(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrEmpty(shared))
            {
                baseDir = shared;
                return Path.Combine(shared, "mps_directory.json");
            }
#endif

            baseDir = Application.persistentDataPath;
            return Path.Combine(baseDir, "mps_directory.json");
        }

        private static string TryBuildSharedDirectory(Environment.SpecialFolder folder)
        {
            try
            {
                var root = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(root)) return null;

                var company = Sanitize(Application.companyName, "Company");
                var product = Sanitize(Application.productName, "Game");
                var dir = Path.Combine(root, company, product, "Mps");
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { return null; }
        }

        private static string Sanitize(string input, string fallback)
        {
            if (string.IsNullOrWhiteSpace(input)) input = fallback;
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(input.Length);
            foreach (var ch in input)
            {
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            }
            return sb.ToString();
        }

        internal static string ControlDirectory => Path.Combine(BaseDirectory, "mps_control");
    }
}
