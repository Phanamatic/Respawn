// Assets/Editor/QuickBuildAndRun.cs
#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using SProcess = System.Diagnostics.Process;
using SProcessStartInfo = System.Diagnostics.ProcessStartInfo;

public static class QuickBuildAndRun
{
    private const string RootDir   = "Build";
    private const string ServerDir = RootDir + "/WindowsServer";
    private const string ClientDir = RootDir + "/WindowsClient";
    private const string AdminDir  = RootDir + "/WindowsAdmin";
    private const string ServerExePreferred = "Server.exe";
    private const string ClientExePreferred = "Client.exe";
    private const string AdminExePreferred  = "Admin.exe";

    private const string SceneMainMenu = "MainMenu";
    private const string SceneLobby    = "Lobby";
    private const string Scene1v1      = "Match_1v1";
    private const string Scene2v2      = "Match_2v2";
    private const string SceneAdmin    = "Admin";

    // ---------- Build ----------
    [MenuItem("Build/Quick/Build Server (Dedicated)")]
    public static void BuildServer() => BuildStandalone(ServerDir, ServerExePreferred, true, null);

    [MenuItem("Build/Quick/Build Client")]
    public static void BuildClient() => BuildStandalone(ClientDir, ClientExePreferred, false, null);

    [MenuItem("Build/Quick/Build Admin")]
    public static void BuildAdmin()  => BuildStandalone(AdminDir,  AdminExePreferred,  false, SceneAdmin);

    [MenuItem("Build/Quick/Build Client+Server")]
    public static void BuildClientServer() { BuildServer(); BuildClient(); Reveal(RootDir); }

    [MenuItem("Build/Quick/Build All")]
    public static void BuildAll() { BuildServer(); BuildClient(); BuildAdmin(); Reveal(RootDir); }

    private static void BuildStandalone(string outDir, string exeName, bool dedicated, string firstSceneOverride)
    {
        Directory.CreateDirectory(outDir);

        var scenes = EditorBuildSettings.scenes;
        if (scenes == null || scenes.Length == 0) { Debug.LogError("No scenes in Build Settings."); return; }

        var scenePaths = GetSceneList(firstSceneOverride);
        if (scenePaths == null || scenePaths.Length == 0) { Debug.LogError("No enabled scenes to build."); return; }

        var opts = new BuildPlayerOptions
        {
            scenes = scenePaths,
            locationPathName = Path.Combine(outDir, exeName),
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.CompressWithLz4HC
        };
        opts.subtarget = dedicated ? (int)StandaloneBuildSubtarget.Server : (int)StandaloneBuildSubtarget.Player;

        var report = BuildPipeline.BuildPlayer(opts);
        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"Build failed: {report.summary.result}");
        }
        else
        {
            var kind = dedicated ? "Server" : (outDir.Contains("Admin") ? "Admin" : "Client");
            Debug.Log($"{kind} build OK: {opts.locationPathName}");
        }
    }

    private static string[] GetSceneList(string firstSceneNameOrNull)
    {
        var enabled = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToList();
        if (enabled.Count == 0) return Array.Empty<string>();

        if (!string.IsNullOrEmpty(firstSceneNameOrNull))
        {
            var wanted = enabled.FirstOrDefault(p =>
                string.Equals(Path.GetFileNameWithoutExtension(p), firstSceneNameOrNull, StringComparison.OrdinalIgnoreCase));

            if (wanted == null)
            {
                Debug.LogError($"Requested first scene '{firstSceneNameOrNull}' not found in Build Settings.");
            }
            else
            {
                enabled.Remove(wanted);
                enabled.Insert(0, wanted); // ensure requested scene is first
            }
        }
        return enabled.ToArray();
    }

    // ---------- Run seeds ----------
    private const string DefaultEnv = "production";
    private const string ServerProfile = "Server"; // <= short, valid, reused across runs
    private const string DefaultRegion = "auto";   // relay will select if supported

    [MenuItem("Build/Quick/Run Seeds/Run Lobby")]
    public static void RunLobbySeed() =>
        RunServer(ArgsForServer("lobby", 16, SceneLobby, DefaultEnv, "lobby_seed.log"));

    [MenuItem("Build/Quick/Run Seeds/Run 1v1")]
    public static void Run1v1Seed() =>
        RunServer(ArgsForServer("1v1", 2, Scene1v1, DefaultEnv, "1v1_seed.log"));

    [MenuItem("Build/Quick/Run Seeds/Run 2v2")]
    public static void Run2v2Seed() =>
        RunServer(ArgsForServer("2v2", 4, Scene2v2, DefaultEnv, "2v2_seed.log"));

    [MenuItem("Build/Quick/Run Seeds/Run All")]
    public static void RunAllSeeds() { RunLobbySeed(); Run1v1Seed(); Run2v2Seed(); }

    // Relay hosting: remove direct IP flags. Add profile and region.
    private static string ArgsForServer(string type, int max, string scene, string env, string logfile)
        => $"-batchmode -nographics -mpsHost -serverType {type} -max {max} -scene {scene} -env {env} -profile {ServerProfile} -region {DefaultRegion} -logfile .\\{logfile}";

    private static void RunServer(string args)
    {
        var exe = FindExe(ServerDir, ServerExePreferred);
        if (exe == null) { Debug.LogError("Server exe not found. Build first."); return; }
        StartHidden(exe, args);
    }

    // ---------- Run clients/admin ----------
    [MenuItem("Build/Quick/Run Client")]
    public static void RunClient() => RunClientInternal("");

    // optional: launch a client with a join code for targeted tests
    [MenuItem("Build/Quick/Run Client (Prompt Join Code)")]
    public static void RunClientWithCode()
    {
        var code = EditorUtility.DisplayDialogComplex("Join Code", "Enter Relay Join Code in Console and press Continue.", "Continue", "Cancel", "Paste From Clipboard");
        if (code == 1) return;
        string joinCode = "";
        try { joinCode = GUIUtility.systemCopyBuffer.Trim(); } catch { }
        if (code == 0) Debug.Log("Enter join code in Console: e.g. ABCDEF");
        var arg = string.IsNullOrWhiteSpace(joinCode) ? "" : $"-autoJoin -mpsJoin {joinCode}";
        RunClientInternal(arg);
    }

    [MenuItem("Build/Quick/Run 2 Clients")]
    public static void RunTwoClients() { RunClient(); RunClient(); }

    [MenuItem("Build/Quick/Run Admin")]
    public static void RunAdmin() => RunAdminInternal("");

    private static void RunClientInternal(string args)
    {
        var exe = FindExe(ClientDir, ClientExePreferred);
        if (exe == null) { Debug.LogError("Client exe not found. Build first."); return; }
        StartNormal(exe, args);
    }

    private static void RunAdminInternal(string args)
    {
        var exe = FindExe(AdminDir, AdminExePreferred);
        if (exe == null) { Debug.LogError("Admin exe not found. Build first."); return; }
        StartNormal(exe, args);
    }

    // ---------- Logs ----------
    [MenuItem("Build/Quick/Logs/Tail Lobby")]
    public static void TailLobby() => TailLog(Path.Combine(ServerDir, "lobby_seed.log"));
    [MenuItem("Build/Quick/Logs/Tail 1v1")]
    public static void Tail1v1() => TailLog(Path.Combine(ServerDir, "1v1_seed.log"));
    [MenuItem("Build/Quick/Logs/Tail 2v2")]
    public static void Tail2v2() => TailLog(Path.Combine(ServerDir, "2v2_seed.log"));

    private static void TailLog(string logPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath) ?? ".");
            if (!File.Exists(logPath)) File.WriteAllText(logPath, "");
            var psi = new SProcessStartInfo("powershell.exe",
                $"-NoExit -Command Get-Content -Path '{Path.GetFullPath(logPath)}' -Wait")
            { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(logPath) };
            SProcess.Start(psi);
        }
        catch (Exception e) { Debug.LogError($"Tail failed: {e.Message}"); }
    }

    // ---------- Kill seeds ----------
    [MenuItem("Build/Quick/Seeds/Kill All Server Processes")]
    public static void KillAllServers()
    {
        var exe = FindExe(ServerDir, ServerExePreferred);
        if (exe == null) { Debug.LogWarning("No server exe found."); return; }
        var target = Path.GetFullPath(exe);
        int killed = 0;

        foreach (var p in SProcess.GetProcesses())
        {
            try
            {
                var m = p.MainModule; if (m == null) continue;
                var path = m.FileName;
                if (string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill();
                    try { p.WaitForExit(3000); } catch { }
                    killed++;
                }
            }
            catch { }
        }
        Debug.Log($"Killed {killed} server process(es).");
    }

    // ---------- Helpers ----------
    private static string FindExe(string dir, string preferredName)
    {
        var absDir = Path.GetFullPath(dir);
        if (!Directory.Exists(absDir)) return null;

        var preferred = Path.Combine(absDir, preferredName);
        if (File.Exists(preferred)) return preferred;

        var files = Directory.GetFiles(absDir, "*.exe", SearchOption.TopDirectoryOnly);
        foreach (var f in files)
        {
            var dataDir = Path.Combine(absDir, Path.GetFileNameWithoutExtension(f) + "_Data");
            if (Directory.Exists(dataDir)) return f;
        }
        return files.Length > 0 ? files[0] : null;
    }

    private static void StartHidden(string exe, string args)
    {
        try
        {
            var psi = new SProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            SProcess.Start(psi);
        }
        catch (Exception e) { Debug.LogError($"Failed to start: {exe}\n{e.Message}"); }
    }

    private static void StartNormal(string exe, string args)
    {
        try
        {
            var psi = new SProcessStartInfo(exe, args)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
            SProcess.Start(psi);
        }
        catch (Exception e) { Debug.LogError($"Failed to start: {exe}\n{e.Message}"); }
    }

    private static void Reveal(string path) => UnityEditor.EditorUtility.RevealInFinder(path);
}
#endif
