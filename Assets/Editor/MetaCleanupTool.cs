using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class MetaCleanupTool
{
    [MenuItem("Tools/Assets/Clean Orphan .meta Files")]
    public static void CleanOrphanMetas()
    {
        string assetsPath = Application.dataPath.Replace('\\','/');
        var metas = Directory.GetFiles(assetsPath, "*.meta", SearchOption.AllDirectories);
        int removed = 0;
        foreach (var metaPath in metas)
        {
            var assetPath = metaPath.Substring(0, metaPath.Length - 5); // strip .meta
            if (!File.Exists(assetPath))
            {
                var rel = "Assets" + assetPath.Substring(assetsPath.Length);
                var relMeta = rel + ".meta";
                File.Delete(metaPath);
                Debug.Log($"[MetaCleanup] Deleted orphan: {relMeta}");
                removed++;
            }
        }
        AssetDatabase.Refresh();
        Debug.Log($"[MetaCleanup] Orphan .meta removed: {removed}");
    }

    [MenuItem("Tools/Assets/Recreate Missing .meta For Existing Files")]
    public static void RecreateMissingMetas()
    {
        string assetsPath = Application.dataPath.Replace('\\','/');
        var allFiles = Directory.GetFiles(assetsPath, "*.*", SearchOption.AllDirectories)
                                .Where(p => !p.EndsWith(".meta")).ToArray();
        int imported = 0;
        foreach (var path in allFiles)
        {
            var metaPath = path + ".meta";
            if (!File.Exists(metaPath))
            {
                var rel = "Assets" + path.Substring(assetsPath.Length);
                AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceSynchronousImport);
                Debug.Log($"[MetaCleanup] Import to create meta: {rel}");
                imported++;
            }
        }
        AssetDatabase.Refresh();
        Debug.Log($"[MetaCleanup] Metas created: {imported}");
    }
}
// One-click cleanup. Run: Tools > Assets > Clean Orphan .meta Files.