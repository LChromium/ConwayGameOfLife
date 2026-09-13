using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace ConwayGameOfLife.EditorTools
{
    /// <summary>
    /// Builds the Windows Player used for the layout screenshots and the runtime measurements.
    ///
    /// It forces a DEVELOPMENT build on purpose. The runtime probe is compiled behind
    /// `#if DEVELOPMENT_BUILD || UNITY_EDITOR`, so a release build silently omits it - which is
    /// exactly what happened when the probe "did not run" in the Player. (A development build is
    /// still a real Player at a real resolution; it only keeps the debug defines.)
    ///
    /// Run headlessly:
    ///   Unity.exe -batchmode -quit -projectPath . -executeMethod
    ///       ConwayGameOfLife.EditorTools.PlayerBuild.BuildWindows64
    /// </summary>
    public static class PlayerBuild
    {
        private const string OutputPath = "Builds/LifeTerminal.exe";

        public static void BuildWindows64()
        {
            try
            {
                Directory.CreateDirectory("Builds");

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                    locationPathName = OutputPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = BuildOptions.Development,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                Debug.Log($"[PlayerBuild] result={summary.result} " +
                          $"size={summary.totalSize} bytes " +
                          $"errors={summary.totalErrors} warnings={summary.totalWarnings} " +
                          $"output={OutputPath}");

                if (summary.result != BuildResult.Succeeded)
                {
                    Debug.LogError($"[PlayerBuild] FAILED: {summary.result}");
                    EditorApplication.Exit(1);
                    return;
                }

                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError($"[PlayerBuild] exception: {e}");
                EditorApplication.Exit(1);
            }
        }
    }
}
