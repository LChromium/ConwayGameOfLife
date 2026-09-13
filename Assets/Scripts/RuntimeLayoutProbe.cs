using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Writes layout measurements and screenshots from a real running Player.
    ///
    /// This exists because a PlayMode test host is locked at 640x480 and an Editor Game View cannot
    /// be forced smaller than its docked size: neither can show what the interface actually does at
    /// 1280x720 or 1920x1080. The Player is the only place with a true target resolution.
    ///
    /// Activated only when the build is launched with "-lifeLayoutProbe"; it is inert otherwise.
    /// </summary>
    public sealed class RuntimeLayoutProbe : MonoBehaviour
    {
        private const string CommandLineFlag = "-lifeLayoutProbe";

        private static bool enabledByCommandLine;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Detect()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg == CommandLineFlag)
                {
                    enabledByCommandLine = true;
                    break;
                }
            }

            if (enabledByCommandLine)
            {
                var host = new GameObject("Runtime Layout Probe");
                DontDestroyOnLoad(host);
                host.AddComponent<RuntimeLayoutProbe>();
            }
#endif
        }

        private IEnumerator Start()
        {
            string outputRoot = Path.Combine(Application.persistentDataPath, "layout-probe");
            Directory.CreateDirectory(outputRoot);

            // Load a large, non-trivial specimen so the screenshot shows the largest pattern the
            // archive contains (a 13x13 pulsar) rather than a three-cell blinker.
            yield return null;
            LoadPulsarIfAvailable();

            // Let the terminal bootstrap and lay out, then measure.
            for (int i = 0; i < 90; i++)
            {
                yield return null;
                if (FindAttachedRoot() != null)
                {
                    break;
                }
            }

            LoadPulsarIfAvailable();

            // A few more frames so fonts and the first grid repaint are settled before capture.
            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            string stamp = $"{Screen.width}x{Screen.height}";
            string shot = Path.Combine(outputRoot, $"player-{stamp}.png");
            ScreenCapture.CaptureScreenshot(shot, 1);

            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }

            yield return MeasureFrameCost();

            VisualElement root = FindAttachedRoot();
            WriteRecord(outputRoot, shot, root);

            yield return new WaitForSeconds(0.5f);
            Application.Quit(0);
        }
        /// <summary>
        /// Samples real frame times from the running Player: no synthetic call loop, so the numbers
        /// include game code, UI Toolkit layout and Painter2D repaint, and presentation. These are
        /// the figures that must never be conflated with the rule-engine micro-benchmark.
        /// </summary>
        private IEnumerator MeasureFrameCost()
        {
            // Discard the first frames after capture; they include the screenshot write.
            for (int i = 0; i < 30; i++)
            {
                yield return null;
            }

            frameSampleCount = 0;
            double total = 0.0;
            double worst = 0.0;
            for (int i = 0; i < 240; i++)
            {
                yield return null;
                double ms = Time.unscaledDeltaTime * 1000.0;
                total += ms;
                if (ms > worst)
                {
                    worst = ms;
                }

                frameSampleCount++;
            }

            frameSampleTotalMs = total;
            frameSampleWorstMs = worst;
        }

        private int frameSampleCount;
        private double frameSampleTotalMs;
        private double frameSampleWorstMs;

        /// <summary>
        /// Selects the pulsar through its real preset button so the capture shows the largest
        /// bundled specimen. Falls back silently if the interface is not up yet.
        /// </summary>
        private void LoadPulsarIfAvailable()
        {
            VisualElement root = FindAttachedRoot();
            if (root == null)
            {
                return;
            }

            foreach (Button button in root.Query<Button>(className: "preset").ToList())
            {
                if (button.text != null && button.text.Contains("脉冲星") && !button.ClassListContains("selected"))
                {
                    using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
                    {
                        submit.target = button;
                        button.SendEvent(submit);
                    }

                    return;
                }
            }
        }

        private static VisualElement FindAttachedRoot()        {
            foreach (UIDocument document in FindObjectsByType<UIDocument>(
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

        private void WriteRecord(string outputRoot, string screenshot, VisualElement root)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"source\":\"player\",");
            sb.Append($"\"screenWidth\":{Screen.width},");
            sb.Append($"\"screenHeight\":{Screen.height},");
            sb.Append($"\"screenshot\":\"{screenshot.Replace('\\', '/')}\",");
            sb.Append($"\"platform\":\"{Application.platform}\",");
            sb.Append($"\"isEditor\":{Application.isEditor.ToString().ToLowerInvariant()},");

            if (root != null)
            {
                Rect panel = root.panel.visualTree.worldBound;
                sb.Append($"\"compact\":{root.ClassListContains("compact").ToString().ToLowerInvariant()},");
                sb.Append($"\"panelWidth\":{panel.width:F2},");
                sb.Append($"\"panelHeight\":{panel.height:F2},");
                sb.Append($"\"rootWidth\":{root.resolvedStyle.width:F2},");
                sb.Append($"\"rootHeight\":{root.resolvedStyle.height:F2},");

                // Cross-check PanelScreenFit against what UI Toolkit actually laid out. This is the
                // only place the pure-function model meets the engine's real ScaleWithScreenSize
                // behaviour, so the deltas are recorded rather than asserted away.
                const int refWidth = 1600;
                const int refHeight = 900;
                (float predictedWidth, float predictedHeight) =
                    PanelScreenFit.VisiblePanelSize(Screen.width, Screen.height, refWidth, refHeight, 0.5f);

                sb.Append($"\"predictedPanelWidth\":{predictedWidth:F2},");
                sb.Append($"\"predictedPanelHeight\":{predictedHeight:F2},");
                sb.Append($"\"panelWidthDelta\":{(panel.width - predictedWidth):F2},");
                sb.Append($"\"panelHeightDelta\":{(panel.height - predictedHeight):F2},");
                sb.Append($"\"predictedCompact\":{(panel.width < 1100f || panel.height < 640f).ToString().ToLowerInvariant()},");

                VisualElement display = root.Q(className: "display");
                VisualElement library = root.Q(className: "library");
                VisualElement grid = root.Q(className: "life-grid");
                sb.Append($"\"displayWidth\":{(display != null ? display.worldBound.width : 0f):F2},");
                sb.Append($"\"displayHeight\":{(display != null ? display.worldBound.height : 0f):F2},");
                sb.Append($"\"libraryWidth\":{(library != null ? library.worldBound.width : 0f):F2},");
                sb.Append($"\"gridHeight\":{(grid != null ? grid.worldBound.height : 0f):F2},");
                sb.Append($"\"footerBottom\":{(root.Q(className: "footer")?.worldBound.yMax ?? 0f):F2},");
            }

            sb.Append($"\"frameSampleCount\":{frameSampleCount},");
            sb.Append($"\"frameMeanMs\":{(frameSampleCount > 0 ? frameSampleTotalMs / frameSampleCount : 0.0):F3},");
            sb.Append($"\"frameWorstMs\":{frameSampleWorstMs:F3},");
            sb.Append($"\"frameMeanFps\":{(frameSampleCount > 0 && frameSampleTotalMs > 0 ? frameSampleCount * 1000.0 / frameSampleTotalMs : 0.0):F1},");
            sb.Append($"\"utc\":\"{DateTime.UtcNow:O}\"");
            sb.Append('}');

            string file = Path.Combine(outputRoot, "measurements.jsonl");
            File.AppendAllText(file, sb + Environment.NewLine);
            Debug.Log($"[layout-probe] {sb}");
            Debug.Log($"[layout-probe] written to {file}");
        }
    }
}
