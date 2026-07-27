using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace StiflingDark.UnityEditorTools
{
    /// <summary>
    /// One-click standalone builds for playtesting against a real server. Ensures the (nearly
    /// empty) boot scene exists — the app spawns itself via RuntimeInitializeOnLoadMethod — and
    /// configures a windowed, resizable player so two seats can sit side by side.
    /// </summary>
    public static class BuildTools
    {
        private const string ScenePath = "Assets/Scenes/Main.unity";

        [MenuItem("Stifling Dark/Build macOS Player")]
        public static void BuildMac() =>
            Build(BuildTarget.StandaloneOSX, "Build/TheStiflingDark.app");

        /// <summary>Needs "Windows Build Support (Mono)" installed via Unity Hub.</summary>
        [MenuItem("Stifling Dark/Build Windows Player")]
        public static void BuildWindows() =>
            Build(BuildTarget.StandaloneWindows64, "Build/Windows/TheStiflingDark.exe");

        private static void Build(BuildTarget target, string outputPath)
        {
            EnsureBootScene();

            PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            PlayerSettings.defaultScreenWidth = 1600;
            PlayerSettings.defaultScreenHeight = 900;
            PlayerSettings.resizableWindow = true;
            PlayerSettings.runInBackground = true; // keep the socket alive when unfocused

            var options = new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = outputPath,
                target = target,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(options);
            Debug.Log($"Build {report.summary.result}: {outputPath} " +
                      $"({report.summary.totalSize / (1024 * 1024)} MB)");
        }

        /// <summary>
        /// Import TMP Essential Resources without the interactive dialog. Assets/TextMesh Pro is
        /// committed, so this is only needed if that folder is ever lost.
        /// </summary>
        [MenuItem("Stifling Dark/Import TMP Essentials")]
        public static void ImportTmpEssentials()
        {
            AssetDatabase.ImportPackage(
                "Packages/com.unity.ugui/Package Resources/TMP Essential Resources.unitypackage",
                false);
            AssetDatabase.SaveAssets();
            Debug.Log("TMP Essential Resources imported.");
        }

        /// <summary>Create and register the minimal boot scene if it does not exist yet.</summary>
        private static void EnsureBootScene()
        {
            if (!File.Exists(ScenePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
                var scene = EditorSceneManager.NewScene(
                    NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
