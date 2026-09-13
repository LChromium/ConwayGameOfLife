using System;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Mirrors Unity's UI Toolkit <c>PanelScaleMode.ScaleWithScreenSize</c> fit transform so the
    /// layout can be reasoned about at any resolution without resizing an actual window.
    ///
    /// UI Toolkit scales the panel with, for a match value of m:
    ///     scale = screenW^m * screenH^(1-m) / (refW^m * refH^(1-m))
    /// which is the geometric interpolation Unity documents for MatchWidthOrHeight. The visible
    /// region of the panel, expressed in panel units, is then physical size / scale.
    ///
    /// This exists because the PlayMode test host renders at a fixed 640x480: the math lets the
    /// 1280x720 and 1920x1080 cases be verified as arithmetic, while an actual screenshot remains
    /// required to sign off on appearance.
    /// </summary>
    public static class PanelScreenFit
    {
        /// <summary>Panel-space scale factor for a given screen and reference resolution.</summary>
        public static float ScaleFactor(int screenWidth, int screenHeight, int referenceWidth, int referenceHeight, float match)
        {
            if (screenWidth <= 0 || screenHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(screenWidth), "screen dimensions must be positive");
            if (referenceWidth <= 0 || referenceHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(referenceWidth), "reference dimensions must be positive");

            match = Math.Clamp(match, 0f, 1f);

            double logWidth = Math.Log((double)screenWidth / referenceWidth);
            double logHeight = Math.Log((double)screenHeight / referenceHeight);
            return (float)Math.Exp(match * logHeight + (1.0 - match) * logWidth);
        }

        /// <summary>
        /// The part of the panel that is actually on screen, in panel units. A taller panel than
        /// this is cropped: content beyond the returned height is not visible.
        /// </summary>
        public static (float Width, float Height) VisiblePanelSize(
            int screenWidth, int screenHeight, int referenceWidth, int referenceHeight, float match)
        {
            float scale = ScaleFactor(screenWidth, screenHeight, referenceWidth, referenceHeight, match);
            return (screenWidth / scale, screenHeight / scale);
        }

        /// <summary>True when content of the given height fits inside the visible panel.</summary>
        public static bool FitsVertically(
            float contentHeight, int screenWidth, int screenHeight, int referenceWidth, int referenceHeight, float match)
        {
            (_, float visibleHeight) = VisiblePanelSize(screenWidth, screenHeight, referenceWidth, referenceHeight, match);
            return contentHeight <= visibleHeight;
        }
    }
}
