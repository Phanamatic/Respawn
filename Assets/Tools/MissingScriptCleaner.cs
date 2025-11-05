#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class MissingScriptCleaner
{
    [MenuItem("Tools/Cleanup/Remove Missing Scripts In Open Scenes")]
    public static void RemoveInOpenScenes()
    {
        int total = 0;
        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            var scn = SceneManager.GetSceneAt(i);
            if (!scn.isLoaded) continue;
            foreach (var root in scn.GetRootGameObjects())
                total += RemoveMissingOnHierarchy(root);
            EditorSceneManager.MarkSceneDirty(scn);
        }
        Debug.Log($"[Cleanup] Removed missing scripts in open scenes. Total components removed: {total}");
    }

    [MenuItem("Tools/Cleanup/Remove Missing Scripts In Project (All Prefabs)")]
    public static void RemoveInProjectPrefabs()
    {
        var guids = AssetDatabase.FindAssets("t:Prefab");
        int total = 0;
        for (int i = 0; i < guids.Length; i++)
        {
            var path = AssetDatabase.GUIDToAssetPath(guids[i]);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (!prefab) continue;

            // Open prefab stage silently
            var stage = PrefabUtility.LoadPrefabContents(path);
            total += RemoveMissingOnHierarchy(stage);
            PrefabUtility.SaveAsPrefabAsset(stage, path);
            PrefabUtility.UnloadPrefabContents(stage);
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"[Cleanup] Removed missing scripts from prefabs. Total components removed: {total}");
    }

    static int RemoveMissingOnHierarchy(GameObject root)
    {
        var stack = new Stack<Transform>();
        stack.Push(root.transform);
        int removed = 0;

        while (stack.Count > 0)
        {
            var t = stack.Pop();
            removed += GameObjectUtility.RemoveMonoBehavioursWithMissingScript(t.gameObject);
            for (int i = 0; i < t.childCount; i++) stack.Push(t.GetChild(i));
        }
        return removed;
    }
}
#endif