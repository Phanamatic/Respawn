using UnityEngine;
using UnityEditor;
using System.Linq;

/// <summary>
/// Editor utility to convert HDRP/Lit materials to Unlit to avoid lighting buffer errors.
/// Use: Tools > Convert HDRP/Lit Materials to Unlit
/// </summary>
public class ConvertHDRPLitToUnlit : EditorWindow
{
    [MenuItem("Tools/Convert HDRP/Lit Materials to Unlit")]
    static void ConvertMaterials()
    {
        string[] guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
        int converted = 0;
        
        var unlit = Shader.Find("Unlit/Texture");
        if (!unlit)
        {
            Debug.LogError("Could not find Unlit/Texture shader!");
            return;
        }

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            
            if (mat && mat.shader && mat.shader.name.Contains("HDRP/Lit"))
            {
                Debug.Log($"Converting {path} from {mat.shader.name} to Unlit/Texture");
                
                // Preserve base color texture if it exists
                Texture mainTex = mat.GetTexture("_BaseColorMap");
                if (!mainTex) mainTex = mat.GetTexture("_MainTex");
                
                Color baseColor = mat.GetColor("_BaseColor");
                
                mat.shader = unlit;
                
                if (mainTex) mat.SetTexture("_MainTex", mainTex);
                mat.SetColor("_Color", baseColor);
                
                EditorUtility.SetDirty(mat);
                converted++;
            }
        }
        
        AssetDatabase.SaveAssets();
        Debug.Log($"Converted {converted} materials from HDRP/Lit to Unlit/Texture");
        
        if (converted > 0)
        {
            EditorUtility.DisplayDialog("Conversion Complete", 
                $"Converted {converted} materials from HDRP/Lit to Unlit/Texture.\n\n" +
                "This resolves 'g_vLightListCluster' buffer errors.", "OK");
        }
        else
        {
            EditorUtility.DisplayDialog("No Materials Found", 
                "No HDRP/Lit materials found to convert.", "OK");
        }
    }
}
