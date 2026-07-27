using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace StiflingDark.UnityEditorTools
{
    /// <summary>
    /// One-shot URP configuration (Unity 6.5 deprecates the Built-In Render Pipeline). The
    /// pipeline assets are committed under Assets/Settings and ProjectSettings already points
    /// at them, so this is a repair tool rather than a first-run step — run it if the project
    /// ever opens with a magenta board.
    ///
    ///   Unity -batchmode -executeMethod StiflingDark.UnityEditorTools.UrpSetup.Configure -quit
    ///
    /// or from the menu: Stifling Dark > Configure URP.
    /// </summary>
    public static class UrpSetup
    {
        private const string SettingsFolder = "Assets/Settings";
        private const string RendererPath = SettingsFolder + "/Renderer2D.asset";
        private const string PipelinePath = SettingsFolder + "/UrpPipeline.asset";

        [MenuItem("Stifling Dark/Configure URP")]
        public static void Configure()
        {
            if (!AssetDatabase.IsValidFolder(SettingsFolder))
            {
                AssetDatabase.CreateFolder("Assets", "Settings");
            }

            var rendererData = AssetDatabase.LoadAssetAtPath<Renderer2DData>(RendererPath);
            if (rendererData == null)
            {
                rendererData = ScriptableObject.CreateInstance<Renderer2DData>();
                AssetDatabase.CreateAsset(rendererData, RendererPath);
            }

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(pipeline, PipelinePath);
            }

            GraphicsSettings.defaultRenderPipeline = pipeline;

            // Assign to every quality level, restoring the active one afterwards.
            int activeLevel = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, applyExpensiveChanges: false);
                QualitySettings.renderPipeline = pipeline;
            }
            QualitySettings.SetQualityLevel(activeLevel, applyExpensiveChanges: false);

            AssetDatabase.SaveAssets();
            Debug.Log("URP configured: " + PipelinePath);
        }
    }
}
