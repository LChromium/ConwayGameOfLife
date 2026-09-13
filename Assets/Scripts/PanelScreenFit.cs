using System;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Mirrors Unity's UI Toolkit <c>PanelScaleMode.ScaleWithScreenSize</c> fit transform so the
    /// layout can be reasoned about at any resolution without resizing an actual window.
    ///
    /// The scale is a LINEAR interpolation between the width ratio and the height ratio:
    ///     scale = lerp(screenW / refW, screenH / refH, match)
    ///
    /// <para>
    /// <b>This contradicts Unity's documentation</b>, which describes MatchWidthOrHeight as a
    /// logarithmic interpolation (screenW^m * screenH^(1-m) / ...). The formula above was derived
    /// from measurement, not from the docs: at 600x1000 against a 1600x900 reference the
    /// logarithmic form predicts a scale of 0.6454 and a panel width of 929.5, while the running
    /// Player reported 807.48. The linear form gives lerp(0.375, 1.1111, 0.5) = 0.7431, and
    /// 600 / 0.7431 = 807.4 - matching the Player to within rounding.
    ///
    /// The two forms agree at 16:9 aspect ratios, which is why every earlier 16:9 sample looked
    /// like confirmation. They diverge on any other aspect ratio.
    /// </para>
    ///
    /// This exists because the PlayMode test host renders at a fixed 640x480: the math lets other
    /// resolutions be reasoned about without resizing a window, while an actual screenshot remains
    /// required to sign off on appearance. The formula is re-verified against a running Player by
    /// <c>RuntimeLayoutProbe</c>, which records the predicted and observed panel side by side.
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

            float widthRatio = (float)screenWidth / referenceWidth;
            float heightRatio = (float)screenHeight / referenceHeight;
            return widthRatio + (heightRatio - widthRatio) * match;
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
