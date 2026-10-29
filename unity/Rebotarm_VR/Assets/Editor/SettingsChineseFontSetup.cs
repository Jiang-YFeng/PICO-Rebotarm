using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

// One-time setup limited to SettingCanvas / SettingsCanvas. Re-run from Tools if needed.
[InitializeOnLoad]
public static class SettingsChineseFontSetup
{
    const string FontPath = "Assets/Fonts/SourceHanSans/SourceHanSansSC-Regular.otf";
    const string AssetPath = "Assets/Fonts/SourceHanSans/SourceHanSansSC-Regular SDF.asset";
    const string ReportPath = "Library/SettingsChineseFontSetup.json";
    static SettingsChineseFontSetup() { EditorApplication.delayCall += AutoApply; }

    static void AutoApply()
    {
        if (File.Exists(ReportPath)) return;
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += AutoApply;
            return;
        }
        Apply();
    }

    [MenuItem("Tools/Rebotarm/Apply Chinese Font to Settings")]
    public static void Apply()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var panels = Resources.FindObjectsOfTypeAll<Canvas>().Where(c =>
            c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded &&
            (c.name == "SettingCanvas" || c.name == "SettingsCanvas")).ToArray();
        if (panels.Length == 0)
        {
            Debug.LogWarning("[Chinese Font] Open the scene containing SettingCanvas, then use Tools/Rebotarm/Apply Chinese Font to Settings.");
            return;
        }
        Font source = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (source == null) { Debug.LogError("[Chinese Font] Source font has not imported yet."); return; }
        TMP_FontAsset font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(AssetPath);
        if (font == null)
        {
            font = TMP_FontAsset.CreateFontAsset(source, 64, 8, GlyphRenderMode.SDFAA, 2048, 2048, AtlasPopulationMode.Dynamic, true);
            if (font == null) throw new InvalidOperationException("Cannot create Chinese TMP font.");
            font.name = "SourceHanSansSC-Regular SDF";
            AssetDatabase.CreateAsset(font, AssetPath);
            AssetDatabase.AddObjectToAsset(font.material, font);
            foreach (var texture in font.atlasTextures) AssetDatabase.AddObjectToAsset(texture, font);
        }
        font.atlasPopulationMode = AtlasPopulationMode.Dynamic;
        font.isMultiAtlasTexturesEnabled = true;
        var texts = panels.SelectMany(c => c.GetComponentsInChildren<TMP_Text>(true)).Distinct().ToArray();
        string characters = "设置关闭视频主机控制输入测试连接保存并主相机腕部侧视未已重连切换副画面状态等待操作启动停止工作姿态回零错误成功失败0123456789.:/- FPS" +
            string.Concat(texts.Select(t => t.text));
        font.TryAddCharacters(characters, out string missing);
        foreach (var texture in font.atlasTextures)
        {
            if (!AssetDatabase.Contains(texture)) AssetDatabase.AddObjectToAsset(texture, font);
            EditorUtility.SetDirty(texture);
        }
        Undo.RecordObjects(texts, "Configure settings Chinese font");
        foreach (var text in texts)
        {
            text.font = font;
            text.fontSharedMaterial = font.material;
            EditorUtility.SetDirty(text);
            PrefabUtility.RecordPrefabInstancePropertyModifications(text);
        }
        EditorUtility.SetDirty(font);
        EditorUtility.SetDirty(font.material);
        AssetDatabase.SaveAssets();
        // Preserve open scene edits in memory; do not silently save unrelated user changes.
        foreach (var panel in panels) EditorSceneManager.MarkSceneDirty(panel.gameObject.scene);
        File.WriteAllText(ReportPath, JsonUtility.ToJson(new Report {
            font = AssetPath, textsUpdated = texts.Length, missingCharacters = missing ?? "",
            sceneSaveRequired = true, time = DateTime.Now.ToString("s")
        }, true));
        Debug.Log($"[Chinese Font] Updated {texts.Length} text elements. Save the scene with Ctrl+S. Missing characters: {missing}");
    }

    [Serializable] class Report
    {
        public string font;
        public int textsUpdated;
        public string missingCharacters;
        public bool sceneSaveRequired;
        public string time;
    }
}
