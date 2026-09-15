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
        private const string BenchmarkOutputPath = "Builds/LifeTerminal-bench.exe";
        private const string ReleaseOutputPath = "Builds/LifeTerminal-release.exe";

        public static void BuildWindows64() => Build(OutputPath, BuildOptions.Development);

        /// <summary>
        /// The second configuration the stage-C benchmark needs. Two of its figures --
        /// GPU frame time and graphics-driver memory -- are only populated in a
        /// development build, and frame timings additionally need
        /// <c>PlayerSettings.enableFrameTimingStats</c>, which the project keeps off for
        /// the shipped player.
        ///
        /// <para>The setting is flipped for the build and put back afterwards, so the
        /// benchmark's diagnostics do not silently become part of the shipped
        /// configuration. Every record carries <c>isDevelopmentBuild</c> so a reader can
        /// tell which configuration a number came from.</para>
        /// </summary>
        public static void BuildBenchmarkWindows64()
        {
            bool previous = PlayerSettings.enableFrameTimingStats;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                Build(BenchmarkOutputPath, BuildOptions.Development);
            }
            finally
            {
                PlayerSettings.enableFrameTimingStats = previous;
            }
        }

        /// <summary>
        /// A release player, for one question the benchmark cannot answer from development
        /// builds: do the numbers hold in the configuration that ships? Two limits are
        /// expected and are recorded by the probe rather than worked around: without the
        /// development defines <c>FrameTimingManager</c> reports nothing and
        /// <c>Profiler.GetAllocatedMemoryForGraphicsDriver</c> stays at zero.
        /// </summary>
        public static void BuildReleaseWindows64() => Build(ReleaseOutputPath, BuildOptions.None);

        private static void Build(string outputPath, BuildOptions options)
        {
            try
            {
                Directory.CreateDirectory("Builds");

                var playerOptions = new BuildPlayerOptions
                {
                    scenes = new[] { "Assets/Scenes/SampleScene.unity" },
                    locationPathName = outputPath,
                    target = BuildTarget.StandaloneWindows64,
                    options = options,
                };

                BuildReport report = BuildPipeline.BuildPlayer(playerOptions);
                BuildSummary summary = report.summary;

                Debug.Log($"[PlayerBuild] result={summary.result} " +
                          $"size={summary.totalSize} bytes " +
                          $"errors={summary.totalErrors} warnings={summary.totalWarnings} " +
                          $"options={options} output={outputPath}");

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
