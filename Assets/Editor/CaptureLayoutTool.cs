using System;
using System.Collections;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ConwayGameOfLife.EditorTools
{
    /// <summary>
    /// Captures the running terminal at explicit resolutions so the responsive layout can be
    /// reviewed visually (a PlayMode test host is fixed at 640x480 and cannot show this).
    ///
    /// Run from the command line against a live Editor:
    ///   unity command capture_layout -r Assets/Editor/CaptureLayoutTool.cs --width 1280 --height 720
    /// or via the Tools menu.
    /// </summary>
    public static class CaptureLayoutTool
    {
        private const string OutputDirectory = "Screenshots";

        [MenuItem("Tools/Conway/Capture Layout 1280x720")]
        public static void Capture720() => Capture(1280, 720);

        [MenuItem("Tools/Conway/Capture Layout 1920x1080")]
        public static void Capture1080() => Capture(1920, 1080);

        public static void Capture(int width, int height)
        {
            if (!EditorApplication.isPlaying)
            {
                // The terminal bootstraps at runtime, so Play Mode is required.
                EditorApplication.EnterPlaymode();
                EditorApplication.delayCall += () => WaitThenCapture(width, height);
                return;
            }

            WaitThenCapture(width, height);
        }

        private static void WaitThenCapture(int width, int height)
        {
            EditorCoroutineRunner.Start(Routine(width, height));
        }

        private static IEnumerator Routine(int width, int height)
        {
            TrySetGameViewSize(width, height);

            // Let the panel build and settle. The controller bootstraps during scene load, so the
            // UIDocument may not be attached to a panel on the first few frames.
            for (int i = 0; i < 60; i++)
            {
                yield return null;
                if (FindAttachedRoot() != null)
                {
                    break;
                }
            }

            Directory.CreateDirectory(OutputDirectory);

            string path = Path.Combine(OutputDirectory, $"terminal-{Screen.width}x{Screen.height}.png");

            // 'screen' includes the UI Toolkit overlay; a camera render would miss it entirely.
            ScreenCapture.CaptureScreenshot(path, 1);

            for (int i = 0; i < 8; i++)
            {
                yield return null;
            }

            ReportLayout(width, height, path);
        }

        /// <summary>The first UIDocument root that is actually attached to a live panel.</summary>
        private static VisualElement FindAttachedRoot()
        {
            foreach (UIDocument document in UnityEngine.Object.FindObjectsByType<UIDocument>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                VisualElement root = document.rootVisualElement;
                if (root?.panel != null && root.panel.visualTree != null)
                {
                    return root;
                }
            }

            return null;
        }

        private static void ReportLayout(int width, int height, string path)
        {
            VisualElement root = FindAttachedRoot();
            string layout = "no attached UIDocument root";
            string compact = "n/a";

            if (root != null)
            {
                Rect panel = root.panel.visualTree.worldBound;

                layout = $"Screen={Screen.width}x{Screen.height} " +
                         $"panel={panel.width:F0}x{panel.height:F0} " +
                         $"root={root.resolvedStyle.width:F0}x{root.resolvedStyle.height:F0} " +
                         $"display={root.Q(className: "display")?.worldBound.width:F0} " +
                         $"library={root.Q(className: "library")?.worldBound.width:F0} " +
                         $"grid={root.Q(className: "life-grid")?.worldBound.height:F0}";

                compact = root.ClassListContains("compact") ? "ON" : "OFF";
            }

            Debug.Log($"[capture] requested={width}x{height} file={path}\n" +
                      $"[capture] compact={compact}\n" +
                      $"[capture] {layout}");

            WriteMeasurement(width, height, path, compact, root);
        }

        /// <summary>
        /// Appends the measured layout to Logs/layout-measurements.jsonl so an EditMode test can
        /// cross-check PanelScreenFit against what UI Toolkit actually did, instead of only
        /// asserting the pure function against itself.
        /// </summary>
        private static void WriteMeasurement(int width, int height, string path, string compact, VisualElement root)
        {
            try
            {
                Directory.CreateDirectory("Logs");
                string file = Path.Combine("Logs", "layout-measurements.jsonl");

                var record = new System.Text.StringBuilder();
                record.Append('{');
                record.Append($"\"requestedWidth\":{width},");
                record.Append($"\"requestedHeight\":{height},");
                record.Append($"\"screenWidth\":{Screen.width},");
                record.Append($"\"screenHeight\":{Screen.height},");
                record.Append($"\"compact\":{(compact == "ON" ? "true" : "false")},");
                record.Append($"\"screenshot\":\"{path.Replace('\\', '/')}\",");

                if (root != null)
                {
                    Rect panel = root.panel.visualTree.worldBound;
                    record.Append($"\"panelWidth\":{panel.width:F2},");
                    record.Append($"\"panelHeight\":{panel.height:F2},");
                    record.Append($"\"rootWidth\":{root.resolvedStyle.width:F2},");
                    record.Append($"\"rootHeight\":{root.resolvedStyle.height:F2},");
                }

                record.Append($"\"utc\":\"{DateTime.UtcNow:O}\"");
                record.Append('}');

                File.AppendAllText(file, record + Environment.NewLine);
                Debug.Log($"[capture] measurement appended to {file}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[capture] could not append measurement: {e.Message}");
            }
        }

        /// <summary>
        /// Resizes the Game View via the internal GameViewSizes API so the UI Toolkit panel is laid
        /// out against the target resolution. Uses reflection because the API is internal; failure
        /// is tolerated and the achieved resolution is recorded in the log either way.
        /// </summary>
        private static void TrySetGameViewSize(int width, int height)
        {
            try
            {
                Type gameViewType = Type.GetType("UnityEditor.GameView, UnityEditor");
                if (gameViewType == null)
                {
                    Debug.LogWarning("[capture] UnityEditor.GameView not found; using the current size");
                    return;
                }

                EditorWindow gameView = EditorWindow.GetWindow(gameViewType);
                gameView.Show();
                gameView.Focus();

                object sizes = gameViewType
                    .GetProperty("sizes", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(gameView);

                if (sizes == null)
                {
                    Debug.LogWarning("[capture] GameView.sizes unavailable; using the current size");
                    return;
                }

                Type sizesType = sizes.GetType();
                var group = sizesType.GetProperty("currentGroup")?.GetValue(sizes);
                if (group == null)
                {
                    Debug.LogWarning("[capture] GameViewSizes.currentGroup unavailable");
                    return;
                }

                Type groupType = group.GetType();
                var custom = groupType.GetProperty("customSize")?.GetValue(group);
                if (custom != null)
                {
                    custom.GetType().GetField("width")?.SetValue(custom, width);
                    custom.GetType().GetField("height")?.SetValue(custom, height);
                }

                // Index 0 is the "Fixed Resolution" entry in a fresh Game View.
                System.Reflection.MethodInfo selection = gameViewType.GetMethod(
                    "SizeSelectionCallback",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

                if (selection == null)
                {
                    Debug.LogWarning("[capture] GameView.SizeSelectionCallback unavailable");
                    return;
                }

                selection.Invoke(gameView, new object[] { 0, null });
                gameView.Repaint();
                Debug.Log($"[capture] requested Game View {width}x{height}");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[capture] could not resize the Game View: {e.Message}");
            }
        }
    }

    /// <summary>Minimal editor coroutine driver (avoids depending on the test framework).</summary>
    internal static class EditorCoroutineRunner
    {
        public static void Start(IEnumerator routine)
        {
            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                bool moved;
                try
                {
                    moved = routine.MoveNext();
                }
                catch (Exception e)
                {
                    Debug.LogError($"[capture] coroutine failed: {e}");
                    EditorApplication.update -= tick;
                    return;
                }

                if (!moved)
                {
                    EditorApplication.update -= tick;
                }
            };

            EditorApplication.update += tick;
        }
    }
}
