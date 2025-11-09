using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class CleanMissingScripts
{
    // Right-click a prefab in the Project window → Assets → Clean Missing Scripts (Prefab)
    [MenuItem("Assets/Clean Missing Scripts (Prefab)", true)]
    private static bool ValidateCleanSelectedPrefabs()
    {
        return Selection.assetGUIDs.Any(guid =>
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            return path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
        });
    }

    [MenuItem("Assets/Clean Missing Scripts (Prefab)")]
    private static void CleanSelectedPrefabs()
    {
        var prefabPaths = Selection.assetGUIDs
            .Select(AssetDatabase.GUIDToAssetPath)
            .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            .Distinct()
            .ToArray();

        int totalRemoved = 0;

        foreach (var path in prefabPaths)
        {
            var root = PrefabUtility.LoadPrefabContents(path);
            int removed = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);

            if (removed > 0)
            {
                PrefabUtility.SaveAsPrefabAsset(root, path);
                totalRemoved += removed;
                Debug.Log($"[CleanMissingScripts] {path}: removed {removed} missing components.");
            }

            PrefabUtility.UnloadPrefabContents(root);
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[CleanMissingScripts] Done. Total removed: {totalRemoved}");
    }
}