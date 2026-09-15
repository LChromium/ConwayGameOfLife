using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ConwayGameOfLife
{
    /// <summary>
    /// Statistics for the stage-C benchmark, as pure functions so they can be tested with
    /// fixed data.
    ///
    /// <para><b>Two defects this exists to remove.</b> Round 2 sorted the sample list in
    /// place before taking the first and last tenth, so those two figures were "the fastest
    /// and slowest tenth" rather than "the beginning and the end of the window" -- measured,
    /// named as something else. And an even-sized sample took the upper of the two middle
    /// values instead of their average, which biases the median upwards.</para>
    ///
    /// <para>Every function here treats its input as read-only. The time order of a sample
    /// series is data, and a helper that quietly destroys it is worse than no helper.</para>
    /// </summary>
    public static class LifeBenchStatistics
    {
        /// <summary>
        /// Summary of one sample series. <see cref="MedianMs"/> averages the two middle
        /// values when the count is even.
        /// </summary>
        public readonly struct Summary
        {
            public readonly int Count;
            public readonly double MedianMs;
            public readonly double MeanMs;
            public readonly double MinMs;
            public readonly double MaxMs;

            public Summary(int count, double median, double mean, double min, double max)
            {
                Count = count;
                MedianMs = median;
                MeanMs = mean;
                MinMs = min;
                MaxMs = max;
            }

            public string Json() =>
                $"\"samples\": {Count}, \"medianMs\": {Format(MedianMs)}, \"meanMs\": {Format(MeanMs)}, " +
                $"\"minMs\": {Format(MinMs)}, \"maxMs\": {Format(MaxMs)}";
        }

        public static string Format(double value) => value.ToString("F4", CultureInfo.InvariantCulture);

        /// <summary>Summarises a series without touching the caller's list.</summary>
        public static Summary Summarise(IReadOnlyList<double> samples)
        {
            if (samples == null || samples.Count == 0)
                return new Summary(0, 0.0, 0.0, 0.0, 0.0);

            var sorted = new List<double>(samples);
            sorted.Sort();

            double total = 0.0;
            foreach (double sample in samples)
                total += sample;

            return new Summary(sorted.Count, Median(sorted), total / sorted.Count, sorted[0], sorted[^1]);
        }

        /// <summary>
        /// Median of an already-sorted list. Even counts average the two middle values:
        /// taking the upper one adds a positive bias to every even-sized sample, which is
        /// most of them.
        /// </summary>
        public static double Median(IReadOnlyList<double> sorted)
        {
            if (sorted == null || sorted.Count == 0)
                return 0.0;

            int middle = sorted.Count / 2;
            return sorted.Count % 2 == 1
                ? sorted[middle]
                : (sorted[middle - 1] + sorted[middle]) * 0.5;
        }

        /// <summary>
        /// Median of the first or last tenth of a series IN ITS ORIGINAL ORDER. This is the
        /// question "did the interval grow while the scenario ran?", which cannot be asked of
        /// a sorted list.
        /// </summary>
        public static double EdgeDecileMedian(IReadOnlyList<double> timeOrdered, bool fromEnd)
        {
            if (timeOrdered == null || timeOrdered.Count == 0)
                return 0.0;

            int slice = Math.Max(1, timeOrdered.Count / 10);
            int start = fromEnd ? timeOrdered.Count - slice : 0;
            var window = new List<double>(slice);
            for (int i = start; i < start + slice && i < timeOrdered.Count; i++)
                window.Add(timeOrdered[i]);

            window.Sort();
            return Median(window);
        }

        /// <summary>
        /// Ratio of the last tenth's median to the first tenth's, or null when either is zero.
        /// A value well above 1 means the window got slower as it ran.
        /// </summary>
        public static double? EdgeDecileRatio(IReadOnlyList<double> timeOrdered)
        {
            double first = EdgeDecileMedian(timeOrdered, fromEnd: false);
            double last = EdgeDecileMedian(timeOrdered, fromEnd: true);
            return first > 0.0 ? last / first : null;
        }

        public static string SummaryOrNull(Summary summary) =>
            summary.Count == 0 ? "null" : "{" + summary.Json() + "}";

        public static string Format(double? value) =>
            value.HasValue ? Format(value.Value) : "null";
    }
}
