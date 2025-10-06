#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UI.Scripts;
using Game.Net;

[CustomEditor(typeof(WeaponData))]
public sealed class WeaponDataEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var so = serializedObject;
        so.Update();

        // Basic
        EditorGUILayout.LabelField("Basic Info", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("weaponName"), new GUIContent("Weapon Name"));
        EditorGUILayout.PropertyField(so.FindProperty("weaponIcon"), new GUIContent("Weapon Icon"));

        // Category
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Category", EditorStyles.boldLabel);
        var catProp = so.FindProperty("category");
        EditorGUILayout.PropertyField(catProp);

        // Enum binding (only show the one that applies)
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Enum Binding (used by Loadout)", EditorStyles.boldLabel);
        var cat = (WeaponCategory)catProp.enumValueIndex;
        switch (cat)
        {
            case WeaponCategory.Primary:
                EditorGUILayout.PropertyField(so.FindProperty("primaryType"), new GUIContent("Primary Type"));
                break;
            case WeaponCategory.Secondary:
                EditorGUILayout.PropertyField(so.FindProperty("secondaryType"), new GUIContent("Secondary Type"));
                break;
            case WeaponCategory.Utility:
                EditorGUILayout.PropertyField(so.FindProperty("utilityType"), new GUIContent("Utility Type"));
                break;
            case WeaponCategory.Melee:
                EditorGUILayout.HelpBox("Melee is fixed (Knife). No enum needed.", MessageType.Info);
                break;
        }

        // Stats
        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Stats", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("damage"));
        EditorGUILayout.PropertyField(so.FindProperty("ammo"));
        EditorGUILayout.PropertyField(so.FindProperty("fireRate"));

        so.ApplyModifiedProperties();
    }
}
#endif

