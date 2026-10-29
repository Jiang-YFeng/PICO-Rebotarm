using System;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Converts only the imported indoor environment from URP/missing shaders to
/// the Built-in Render Pipeline used by Rebotarm_VR.
/// </summary>
[InitializeOnLoad]
public static class IndoorMaterialConverter
{
    const string IndoorRoot = "Assets/Environment/indoor";

    static IndoorMaterialConverter()
    {
        EditorApplication.delayCall += ConvertIfNeeded;
    }

    [MenuItem("Tools/Rebotarm/Convert Indoor Materials To Built-in")]
    public static void ConvertIfNeeded()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling ||
            EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += ConvertIfNeeded;
            return;
        }

        Shader standard = Shader.Find("Standard");
        if (!standard)
        {
            Debug.LogError("[Indoor Material Converter] Built-in Standard shader was not found.");
            return;
        }

        string[] materialGuids = AssetDatabase.FindAssets("t:Material", new[] { IndoorRoot });
        int converted = 0;

        foreach (string guid in materialGuids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (!material || !NeedsConversion(material)) continue;

            // Read URP surface flags from serialized saved properties. This also
            // works when the original URP shader is missing from this project.
            float surface = ReadSavedFloat(material, "_Surface", 0f);
            float alphaClip = ReadSavedFloat(material, "_AlphaClip", 0f);

            material.shader = standard;
            ConfigureStandardSurface(material, surface > 0.5f, alphaClip > 0.5f);

            if (material.GetTexture("_BumpMap"))
                material.EnableKeyword("_NORMALMAP");
            if (material.GetTexture("_MetallicGlossMap"))
                material.EnableKeyword("_METALLICGLOSSMAP");
            if (material.GetTexture("_EmissionMap") ||
                (material.HasProperty("_EmissionColor") && material.GetColor("_EmissionColor").maxColorComponent > 0f))
                material.EnableKeyword("_EMISSION");

            EditorUtility.SetDirty(material);
            converted++;
        }

        if (converted <= 0) return;
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"[Indoor Material Converter] Converted {converted} indoor material(s) to Built-in Standard.");
    }

    static bool NeedsConversion(Material material)
    {
        if (!material.shader) return true;
        string shaderName = material.shader.name ?? string.Empty;
        return shaderName.Equals("Hidden/InternalErrorShader", StringComparison.OrdinalIgnoreCase) ||
               shaderName.IndexOf("Universal Render Pipeline", StringComparison.OrdinalIgnoreCase) >= 0 ||
               shaderName.IndexOf("Shader Graph", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static float ReadSavedFloat(Material material, string propertyName, float fallback)
    {
        SerializedObject serialized = new SerializedObject(material);
        SerializedProperty floats = serialized.FindProperty("m_SavedProperties.m_Floats");
        if (floats == null || !floats.isArray) return fallback;

        for (int i = 0; i < floats.arraySize; i++)
        {
            SerializedProperty pair = floats.GetArrayElementAtIndex(i);
            SerializedProperty key = pair.FindPropertyRelative("first");
            if (key != null && key.stringValue == propertyName)
            {
                SerializedProperty value = pair.FindPropertyRelative("second");
                return value != null ? value.floatValue : fallback;
            }
        }
        return fallback;
    }

    static void ConfigureStandardSurface(Material material, bool transparent, bool alphaClip)
    {
        material.SetFloat("_Mode", transparent ? 3f : alphaClip ? 1f : 0f);
        material.SetFloat("_ZWrite", transparent ? 0f : 1f);
        material.SetFloat("_Cutoff", alphaClip ? 0.5f : 0f);

        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHABLEND_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");

        if (transparent)
        {
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.EnableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        else if (alphaClip)
        {
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            material.EnableKeyword("_ALPHATEST_ON");
            material.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
        }
        else
        {
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            material.renderQueue = -1;
        }
    }
}
