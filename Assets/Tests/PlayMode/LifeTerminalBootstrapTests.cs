using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// PlayMode coverage for the presentation layer: the runtime bootstrap must build a complete
    /// interface, and the real UI controls must actually drive the simulation.
    ///
    /// This is the layer EditMode tests cannot reach. A malformed style sheet, a missing resource,
    /// or a null reference inside Awake all surface here. Any logged error fails these tests
    /// automatically, so a silent regression cannot slip through.
    ///
    /// Note: elements are looked up by CSS class via Q(name: null, className: ...), because the
    /// controller tags regions with AddToClassList rather than assigning element names.
    /// </summary>
    public sealed class LifeTerminalBootstrapTests
    {
        [UnityTest]
        public IEnumerator Bootstrap_BuildsCompleteInterface()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            Assert.Greater(root.childCount, 0, "the interface was never built (root has no children)");
            Assert.IsTrue(root.ClassListContains("app"), "root is missing the 'app' class");

            // Every layout region the controller assembles.
            foreach (string region in new[] { "header", "machine", "workspace", "display", "library", "controls", "footer" })
            {
                Assert.IsNotNull(FindByClass(root, region), $"missing layout region '{region}'");
            }

            // Title and specimen archive.
            Label title = FindByClass(root, "title") as Label;
            Assert.IsNotNull(title, "missing title label");
            Assert.AreEqual("生命演算所", title.text);

            VisualElement library = FindByClass(root, "library");
            Assert.AreEqual(LifePatterns.All.Length, library.Query<Button>(className: "preset").ToList().Count,
                "the specimen archive should expose one button per pattern");

            // The grid must exist and be a bound LifeGridElement.
            VisualElement grid = FindByClass(root, "life-grid");
            Assert.IsNotNull(grid, "missing 'life-grid'");
            Assert.IsInstanceOf<LifeGridElement>(grid);

            // Three readouts: generation, population, state.
            Assert.AreEqual(3, FindByClass(root, "readouts").Query<VisualElement>(className: "readout").ToList().Count,
                "expected generation / population / state readouts");
        }

        [UnityTest]
        public IEnumerator Bootstrap_LoadsTheRuntimeThemeAndAppliesStyles()
        {
            yield return Settle();

            UIDocument document = Document();
            Assert.IsNotNull(document.panelSettings, "UIDocument has no PanelSettings");

            ThemeStyleSheet theme = document.panelSettings.themeStyleSheet;
            Assert.IsNotNull(theme,
                "PanelSettings.themeStyleSheet is null - Resources/LifeRuntimeTheme.tss failed to load");
            Assert.AreEqual("LifeRuntimeTheme", theme.name,
                "PanelSettings is using an unexpected theme asset");

            // Layout is the real proof that styles applied: the grid only gets a non-zero rect if
            // LifeTerminal.uss loaded and the flex layout resolved. A broken style sheet leaves
            // every region at zero size, which would still pass a pure hierarchy check.
            VisualElement grid = FindByClass(GetRoot(), "life-grid");
            Assert.Greater(grid.worldBound.width, 0f, "the grid has no width - styles/layout did not apply");
            Assert.Greater(grid.worldBound.height, 0f, "the grid has no height - styles/layout did not apply");

            VisualElement display = FindByClass(GetRoot(), "display");
            Assert.Greater(display.worldBound.width, 0f, "the display panel collapsed to zero width");
        }

        [UnityTest]
        public IEnumerator Bootstrap_IsIdempotent()
        {
            yield return Settle();

            LifeTerminalController[] controllers = Object.FindObjectsByType<LifeTerminalController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            Assert.AreEqual(1, controllers.Length, "the runtime bootstrap created more than one controller");
        }

        [UnityTest]
        public IEnumerator SteppingThroughTheInterface_AdvancesTheGenerationCounter()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            Label generation = ReadoutValue(root, "GENERATION");
            Assert.IsNotNull(generation, "could not find the GENERATION readout");
            Assert.AreEqual("0000", generation.text, "a freshly started terminal should read generation 0000");

            Button step = FindButton(root, "▸ 单步");
            Assert.IsNotNull(step, "could not find the single-step button");

            Press(step);
            Assert.AreEqual("0001", generation.text, "single-step did not advance the generation counter");

            Press(step);
            Assert.AreEqual("0002", generation.text, "single-step did not advance the generation counter again");
        }

        [UnityTest]
        public IEnumerator LoadingAPreset_UpdatesTheReadouts()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            List<Button> presets = FindByClass(root, "library").Query<Button>(className: "preset").ToList();
            Assert.AreEqual(LifePatterns.All.Length, presets.Count);

            Label population = ReadoutValue(root, "POPULATION");
            Label generation = ReadoutValue(root, "GENERATION");

            // Load each preset in turn; the population readout must match the pattern's cell count.
            for (int i = 0; i < LifePatterns.All.Length; i++)
            {
                Press(presets[i]);
                yield return null;

                LifePattern pattern = LifePatterns.All[i];
                Assert.AreEqual(pattern.Cells.Length.ToString("0000"), population.text,
                    $"loading '{pattern.EnglishName}' did not update the population readout");
                Assert.AreEqual("0000", generation.text,
                    $"loading '{pattern.EnglishName}' should reset the generation counter");
            }
        }

        [UnityTest]
        public IEnumerator RunButton_StartsAndStopsTheSimulation()
        {
            yield return Settle();

            VisualElement root = GetRoot();
            Label status = FindByClass(root, "status") as Label;
            Button play = FindButton(root, "▶ 运行");
            Assert.IsNotNull(status, "missing status label");
            Assert.IsNotNull(play, "missing play button");
            Assert.AreEqual("●  待机中", status.text);

            Press(play);
            Assert.AreEqual("●  演算进行中", status.text, "clicking play should start the simulation");
            Assert.AreEqual("Ⅱ 暂停", play.text, "the button should now offer to pause");

            Press(play);
            Assert.AreEqual("●  待机中", status.text, "clicking again should pause the simulation");
            Assert.AreEqual("▶ 运行", play.text, "the button should offer to play again");
        }

        // --- helpers ----------------------------------------------------------

        /// <summary>Lets RuntimeInitializeOnLoadMethod run and the panel complete its first layout.</summary>
        private static IEnumerator Settle()
        {
            yield return null;
            yield return null;
        }

        private static UIDocument Document()
        {
            LifeTerminalController controller = Object.FindAnyObjectByType<LifeTerminalController>();
            Assert.IsNotNull(controller, "the runtime bootstrap did not create a LifeTerminalController");

            UIDocument document = controller.GetComponent<UIDocument>();
            Assert.IsNotNull(document, "controller has no UIDocument");
            return document;
        }

        private static VisualElement GetRoot()
        {
            VisualElement root = Document().rootVisualElement;
            Assert.IsNotNull(root, "UIDocument has no root visual element");
            return root;
        }

        private static VisualElement FindByClass(VisualElement scope, string className)
        {
            return scope.Q(name: null, className: className);
        }

        private static Button FindButton(VisualElement scope, string text)
        {
            foreach (Button button in scope.Query<Button>().ToList())
            {
                if (button.text == text)
                {
                    return button;
                }
            }

            return null;
        }

        /// <summary>
        /// Presses a button the way the runtime does. Clickable.click() is internal to
        /// UnityEngine.UIElements, so the public path is to dispatch the submit event that a
        /// Button listens for.
        /// </summary>
        private static void Press(Button button)
        {
            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                submit.target = button;
                button.SendEvent(submit);
            }
        }

        /// <summary>Finds a readout's value label by its caption, e.g. "GENERATION / 世代".</summary>
        private static Label ReadoutValue(VisualElement root, string captionPrefix)
        {
            foreach (VisualElement readout in FindByClass(root, "readouts").Query<VisualElement>(className: "readout").ToList())
            {
                Label caption = FindByClass(readout, "readout-caption") as Label;
                if (caption != null && caption.text.StartsWith(captionPrefix))
                {
                    return readout.Query<Label>().ToList().Find(label => label != caption);
                }
            }

            return null;
        }
    }
}
