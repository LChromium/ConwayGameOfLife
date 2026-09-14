using System;
using System.Collections;
using System.Collections.Generic;
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

        /// <summary>
        /// Optional "-lifePattern &lt;ENGLISH_NAME&gt;" argument so a capture can select a specific
        /// specimen. Used to check that the longest name still fits the screen-bar title instead of
        /// being silently clipped - the archive list fitting it is a different claim.
        /// </summary>
        private const string PatternArgument = "-lifePattern";

        private static bool enabledByCommandLine;
        private static string requestedPattern;
        private static bool patternArgumentPending;
        private bool specimenSelected;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Detect()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            foreach (string arg in Environment.GetCommandLineArgs())
            {
                if (arg == CommandLineFlag)
                {
                    enabledByCommandLine = true;
                }
                else if (arg == PatternArgument)
                {
                    patternArgumentPending = true;
                }
                else if (patternArgumentPending)
                {
                    requestedPattern = arg;
                    patternArgumentPending = false;
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

            // Resolve the requested specimen up front and FAIL FAST if it cannot be resolved.
            // Carrying on would either produce a screenshot of the wrong specimen (silently) or
            // measurements attributed to the wrong one, and an automated caller would see exit 0.
            yield return null;

            if (!TrySelectSpecimen(out string selectionError))
            {
                Debug.LogError($"[layout-probe] FAILED: {selectionError}");
                Debug.LogError("[layout-probe] aborting without recording any measurement");

                // Non-zero exit so a caller cannot mistake this for a successful capture.
                Application.Quit(ProbeExitCodes.SelectionFailed);
                yield break;
            }

            // Let the terminal bootstrap and lay out, then measure.
            for (int i = 0; i < 90; i++)
            {
                yield return null;
                if (FindAttachedRoot() != null)
                {
                    break;
                }
            }

            // The first attempt can run before the interface exists; retry once now that it does.
            if (!specimenSelected && !TrySelectSpecimen(out selectionError))
            {
                Debug.LogError($"[layout-probe] FAILED: {selectionError}");
                Application.Quit(ProbeExitCodes.SelectionFailed);
                yield break;
            }

            // A few more frames so fonts and the first grid repaint are settled before capture.
            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            // Both scenarios are sampled with the SAME frame-rate cap, so their figures are
            // comparable. The cap is recorded because it makes the number "configured frame-time
            // budget observed", NOT "how long a frame costs to compute".
            Application.targetFrameRate = FrameRateCap;
            QualitySettings.vSyncCount = 0;
            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }

            // Scenario A: idle. The archive was just loaded, which pauses the clock, so this is the
            // "nothing is evolving" case - it must never be presented as gameplay performance.
            yield return Sample("paused", () => { });

            // Scenario B: evolving at the slider's maximum rate. This is the case that exercises
            // continuous rule advancement AND the per-generation grid repaint it triggers.
            int genBefore = ReadGeneration();
            yield return Sample("running-20gps", () => SetRunning(true, 20));
            int genAfter = ReadGeneration();
            SetRunning(false, 20);

            string stamp = $"{Screen.width}x{Screen.height}";
            string shot = Path.Combine(outputRoot, $"player-{stamp}.png");
            ScreenCapture.CaptureScreenshot(shot, 1);
            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }

            bool compact = FindAttachedRoot()?.ClassListContains("compact") ?? false;
            WriteRecord(outputRoot, shot, FindAttachedRoot(), genAfter - genBefore, compact);

            yield return new WaitForSeconds(0.5f);
            Application.Quit(ProbeExitCodes.Success);
        }

        /// <summary>Process exit codes for the probe, so a caller can detect a failed capture.</summary>
        internal static class ProbeExitCodes
        {
            /// <summary>Measurements and a screenshot were recorded.</summary>
            public const int Success = 0;

            /// <summary>The requested specimen does not exist, or no preset button matched it.</summary>
            public const int SelectionFailed = 3;
        }

        private const int FrameRateCap = 120;

        private int pausedFrames;
        private double pausedTotalMs;
        private double pausedWorstMs;
        private int runningFrames;
        private double runningTotalMs;
        private double runningWorstMs;
        private int pausedGenDelta;
        private int runningGenDelta;

        /// <summary>
        /// Samples real frame deltas for one scenario. The setup action runs first so the scenario
        /// is already active for the whole window, and the generation counter is recorded on both
        /// sides so the record proves whether anything was actually evolving.
        /// </summary>
        private IEnumerator Sample(string label, Action setup)
        {
            setup();

            // Settle: let the scenario take effect (and discard the frames disturbed by it).
            for (int i = 0; i < 30; i++)
            {
                yield return null;
            }

            int genStart = ReadGeneration();
            int frames = 0;
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

                frames++;
            }

            int genDelta = ReadGeneration() - genStart;

            if (label == "paused")
            {
                pausedFrames = frames;
                pausedTotalMs = total;
                pausedWorstMs = worst;
                pausedGenDelta = genDelta;
            }
            else
            {
                runningFrames = frames;
                runningTotalMs = total;
                runningWorstMs = worst;
                runningGenDelta = genDelta;
            }

            Debug.Log($"[layout-probe] sample '{label}': {frames} frames, mean {total / frames:F3} ms, " +
                      $"worst {worst:F3} ms, generations advanced {genDelta}");
        }

        private int ReadGeneration()
        {
            foreach (LifeTerminalController controller in FindObjectsByType<LifeTerminalController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var field = typeof(LifeTerminalController).GetField(
                    "simulation",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (field?.GetValue(controller) is LifeSimulation simulation)
                {
                    return simulation.Generation;
                }
            }

            return -1;
        }

        /// <summary>Drives the real controls: the play button and the speed slider.</summary>
        private void SetRunning(bool running, int generationsPerSecond)
        {
            VisualElement root = FindAttachedRoot();
            if (root == null)
            {
                return;
            }

            var speedField = typeof(LifeTerminalController).GetField(
                "speedSlider",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            foreach (LifeTerminalController controller in FindObjectsByType<LifeTerminalController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (speedField?.GetValue(controller) is SliderInt slider)
                {
                    slider.value = generationsPerSecond;
                }

                var runningField = typeof(LifeTerminalController).GetField(
                    "running",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                runningField?.SetValue(controller, running);
            }
        }

        /// <summary>
        /// Selects the specimen to display by pressing its real preset button. Uses the
        /// "-lifePattern" argument when given, otherwise the pulsar (the largest bundled pattern).
        ///
        /// Returns false with a reason when the specimen cannot be selected. The caller treats that
        /// as fatal: recording a measurement attributed to the wrong specimen, or a screenshot of
        /// one, would be worse than recording nothing.
        /// </summary>
        private bool TrySelectSpecimen(out string error)
        {
            VisualElement root = FindAttachedRoot();
            if (root == null)
            {
                error = "no attached UIDocument root yet";
                return false;
            }

            List<Button> presets = root.Query<Button>(className: "preset").ToList();
            if (presets.Count != LifePatterns.All.Length)
            {
                error = $"expected {LifePatterns.All.Length} preset buttons but found {presets.Count}; " +
                        "the archive has not been built yet";
                return false;
            }

            // Resolve the pattern to select.
            //
            // With no argument, default to the pulsar (the largest bundled pattern). With
            // "-lifePattern <EnglishName>", match the name EXACTLY against the archive. An earlier
            // version only checked whether the argument was non-empty and then walked a hard-coded
            // candidate list, so "-lifePattern GLIDER" silently selected the pentadecathlon - the
            // parameter looked like it worked while ignoring its value.
            string wantedName = string.IsNullOrEmpty(requestedPattern) ? "PULSAR" : requestedPattern;
            LifePattern pattern = FindPatternByEnglishName(wantedName);
            if (pattern == null)
            {
                error = $"unknown pattern '{wantedName}'. Known names: {string.Join(", ", KnownEnglishNames())}";
                return false;
            }

            // The buttons are labelled "<chinese>\n<kind>", so match on the pattern's own Chinese name.
            Button target = null;
            for (int i = 0; i < presets.Count; i++)
            {
                if (presets[i].text != null && presets[i].text.StartsWith(pattern.Name))
                {
                    target = presets[i];
                    break;
                }
            }

            if (target == null)
            {
                error = $"no preset button labelled '{pattern.Name}' ({pattern.EnglishName})";
                return false;
            }

            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = target;
                target.SendEvent(submit);
            }

            if (target.ClassListContains("selected") == false)
            {
                error = $"pressed '{pattern.EnglishName}' but it did not become the selected specimen";
                return false;
            }

            specimenSelected = true;
            error = null;
            Debug.Log($"[layout-probe] SelectSpecimen: selected {pattern.EnglishName} " +
                      $"('{target.text?.Replace("\n", "|")}')");
            return true;
        }

        private static LifePattern FindPatternByEnglishName(string englishName)
        {
            foreach (LifePattern pattern in LifePatterns.All)
            {
                if (string.Equals(pattern.EnglishName, englishName, StringComparison.Ordinal))
                {
                    return pattern;
                }
            }

            return null;
        }

        private static IEnumerable<string> KnownEnglishNames()
        {
            foreach (LifePattern pattern in LifePatterns.All)
            {
                yield return pattern.EnglishName;
            }
        }

        private static VisualElement FindAttachedRoot()
        {
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

        /// <summary>Which rule caused the stacked layout, or "none".</summary>
        private static string CompactTrigger(float panelWidth, float panelHeight)
        {
            if (panelWidth < 1280f)
            {
                return "width";
            }

            return "none";
        }

        private void WriteRecord(string outputRoot, string screenshot, VisualElement root, int generationDelta, bool compactObserved)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"source\":\"player\",");
            sb.Append($"\"screenWidth\":{Screen.width},");
            sb.Append($"\"screenHeight\":{Screen.height},");
            sb.Append($"\"screenshot\":\"{screenshot.Replace('\\', '/')}\",");
            sb.Append($"\"platform\":\"{Application.platform}\",");
            sb.Append($"\"isEditor\":{Application.isEditor.ToString().ToLowerInvariant()},");
            sb.Append($"\"targetFrameRate\":{Application.targetFrameRate},");
            sb.Append($"\"vSyncCount\":{QualitySettings.vSyncCount},");

            if (root != null)
            {
                Rect panel = root.panel.visualTree.worldBound;
                sb.Append($"\"compact\":{compactObserved.ToString().ToLowerInvariant()},");
                sb.Append($"\"panelWidth\":{panel.width:F2},");
                sb.Append($"\"panelHeight\":{panel.height:F2},");
                sb.Append($"\"rootWidth\":{root.resolvedStyle.width:F2},");
                sb.Append($"\"rootHeight\":{root.resolvedStyle.height:F2},");
                sb.Append($"\"compactTriggeredBy\":\"{CompactTrigger(panel.width, panel.height)}\",");

                // Cross-check PanelScreenFit against what UI Toolkit actually laid out. The predicted
                // side uses the FORMULA's output, not the observed panel, so the comparison is a real
                // cross-check rather than a restatement of the measurement.
                const int refWidth = 1600;
                const int refHeight = 900;
                (float predictedWidth, float predictedHeight) =
                    PanelScreenFit.VisiblePanelSize(Screen.width, Screen.height, refWidth, refHeight, 0.5f);

                sb.Append($"\"predictedPanelWidth\":{predictedWidth:F2},");
                sb.Append($"\"predictedPanelHeight\":{predictedHeight:F2},");
                sb.Append($"\"panelWidthDelta\":{(panel.width - predictedWidth):F2},");
                sb.Append($"\"panelHeightDelta\":{(panel.height - predictedHeight):F2},");
                sb.Append($"\"predictedCompact\":{(predictedWidth < 1280f).ToString().ToLowerInvariant()},");
                sb.Append($"\"compactPredictionMatches\":{(compactObserved == (predictedWidth < 1280f)).ToString().ToLowerInvariant()},");

                // The grid height is in PANEL UNITS (design space), not physical pixels.
                VisualElement grid = root.Q(className: "life-grid");
                sb.Append($"\"gridHeightPanelUnits\":{(grid != null ? grid.worldBound.height : 0f):F2},");

                // The screen-bar specimen caption is the longest text in the interface when the
                // pentadecathlon is selected. Record it with its measured and preferred widths so
                // "does the title fit?" is answered by a number, not by squinting at a screenshot.
                VisualElement screenBar = root.Q(className: "screen-bar");
                Label captionLabel = null;
                if (screenBar != null)
                {
                    foreach (VisualElement element in screenBar.Query<VisualElement>(className: "micro").ToList())
                    {
                        if (element is Label label &&
                            (label.text.StartsWith("样本 ") || label.text.StartsWith("自由样本")))
                        {
                            captionLabel = label;
                            break;
                        }
                    }
                }

                if (captionLabel != null)
                {
                    float measured = captionLabel.MeasureTextSize(
                        captionLabel.text, 0f, VisualElement.MeasureMode.Undefined,
                        0f, VisualElement.MeasureMode.Undefined).x;

                    sb.Append($"\"captionText\":\"{captionLabel.text.Replace("\\", "/").Replace("\"", "'")}\",");
                    sb.Append($"\"captionTextLength\":{captionLabel.text.Length},");
                    sb.Append($"\"captionBoxWidth\":{captionLabel.worldBound.width:F2},");
                    sb.Append($"\"captionPreferredWidth\":{measured:F2},");
                    sb.Append($"\"captionClipped\":{((measured > captionLabel.worldBound.width + 0.5f) && !captionLabel.style.whiteSpace.ToString().Contains("Normal")).ToString().ToLowerInvariant()},");
                }

                VisualElement display = root.Q(className: "display");
                VisualElement library = root.Q(className: "library");
                sb.Append($"\"displayWidth\":{(display != null ? display.worldBound.width : 0f):F2},");
                sb.Append($"\"displayHeight\":{(display != null ? display.worldBound.height : 0f):F2},");
                sb.Append($"\"libraryWidth\":{(library != null ? library.worldBound.width : 0f):F2},");
                sb.Append($"\"footerBottomPanelUnits\":{(root.Q(className: "footer")?.worldBound.yMax ?? 0f):F2},");
            }

            // Two clearly separated scenarios. Deliberately NOT averaged together: the paused case
            // does no rule work and triggers no grid repaint, so blending it into the running case
            // would flatter the numbers.
            sb.Append($"\"pausedFrames\":{pausedFrames},");
            sb.Append($"\"pausedMeanMs\":{(pausedFrames > 0 ? pausedTotalMs / pausedFrames : 0.0):F3},");
            sb.Append($"\"pausedWorstMs\":{pausedWorstMs:F3},");
            sb.Append($"\"pausedGenerationsAdvanced\":{pausedGenDelta},");

            sb.Append($"\"runningFrames\":{runningFrames},");
            sb.Append($"\"runningMeanMs\":{(runningFrames > 0 ? runningTotalMs / runningFrames : 0.0):F3},");
            sb.Append($"\"runningWorstMs\":{runningWorstMs:F3},");
            sb.Append($"\"runningGenerationsAdvanced\":{runningGenDelta},");
            sb.Append($"\"runningMeanFps\":{(runningFrames > 0 && runningTotalMs > 0 ? runningFrames * 1000.0 / runningTotalMs : 0.0):F1},");
            sb.Append($"\"runningFpsPerGeneration\":{(runningGenDelta > 0 && runningTotalMs > 0 ? runningGenDelta * 1000.0 / runningTotalMs : 0.0):F2},");

            sb.Append($"\"utc\":\"{DateTime.UtcNow:O}\"");
            sb.Append('}');

            string file = Path.Combine(outputRoot, "measurements.jsonl");
            File.AppendAllText(file, sb + Environment.NewLine);
            Debug.Log($"[layout-probe] {sb}");
            Debug.Log($"[layout-probe] written to {file}");
        }
    }
}
