using System.Collections.Generic;
using NUnit.Framework;

namespace ConwayGameOfLife.Tests
{
    /// <summary>
    /// The benchmark's statistics, tested with fixed data because two of them were wrong in
    /// round 2 in ways that only show up on specific inputs:
    ///
    ///   * the sample list was sorted in place before the first/last tenth were taken, so
    ///     those figures described the fastest and slowest tenth, not the window's beginning
    ///     and end;
    ///   * an even-sized sample took the upper middle value instead of averaging the two.
    /// </summary>
    public sealed class LifeBenchStatisticsTests
    {
        [Test]
        public void Summarise_DoesNotReorderTheCallersList()
        {
            // The exact shape the round-2 bug needed: a slow start and a fast finish. Sorted
            // in place, the "last tenth" would come out fast instead of slow.
            var samples = new List<double> { 50.0, 40.0, 30.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0 };
            var before = new List<double>(samples);

            LifeBenchStatistics.Summarise(samples);

            CollectionAssert.AreEqual(before, samples,
                "the summariser must treat the sample series as read-only: its order is data");
        }

        [Test]
        public void EdgeDecile_ReportsSlowStartAndFastFinish_InTimeOrder()
        {
            // Twenty samples so a "tenth" is two of them: the first tenth is {50, 40} and the
            // last is {1, 1}. The medians are what the fields report.
            var samples = new List<double>
            {
                50.0, 40.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0,
                1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0,
            };

            double first = LifeBenchStatistics.EdgeDecileMedian(samples, fromEnd: false);
            double last = LifeBenchStatistics.EdgeDecileMedian(samples, fromEnd: true);

            Assert.AreEqual(45.0, first, 1e-9,
                "the first tenth is the beginning of the window, however slow it was");
            Assert.AreEqual(1.0, last, 1e-9, "the last tenth is the end of the window");
            Assert.Less(last, first, "this series got faster, and the figures must say so");

            double? ratio = LifeBenchStatistics.EdgeDecileRatio(samples);
            Assert.IsTrue(ratio.HasValue);
            Assert.AreEqual(1.0 / 45.0, ratio.Value, 1e-9);
        }

        [Test]
        public void EdgeDecile_ReportsFastStartAndSlowFinish()
        {
            // The mirror case, which is what "did the scenario degrade?" actually asks.
            var samples = new List<double> { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 20.0, 30.0, 40.0 };

            Assert.AreEqual(1.0, LifeBenchStatistics.EdgeDecileMedian(samples, fromEnd: false), 1e-9);
            Assert.AreEqual(40.0, LifeBenchStatistics.EdgeDecileMedian(samples, fromEnd: true), 1e-9);
            Assert.Greater(LifeBenchStatistics.EdgeDecileRatio(samples) ?? 0.0, 1.0);
        }

        [Test]
        public void Median_AveragesTheTwoMiddleValues_OnEvenCounts()
        {
            var samples = new List<double> { 1.0, 2.0, 3.0, 10.0 };

            var summary = LifeBenchStatistics.Summarise(samples);

            Assert.AreEqual(4, summary.Count);
            Assert.AreEqual(2.5, summary.MedianMs, 1e-9,
                "an even count averages the two middle values; taking the upper one biases the median up");
        }

        [Test]
        public void Median_IsTheMiddleValue_OnOddCounts()
        {
            var summary = LifeBenchStatistics.Summarise(new List<double> { 5.0, 1.0, 3.0 });

            Assert.AreEqual(3.0, summary.MedianMs, 1e-9);
        }

        [Test]
        public void Summarise_HandlesTheDegenerateCases()
        {
            var empty = LifeBenchStatistics.Summarise(new List<double>());
            Assert.AreEqual(0, empty.Count);
            Assert.AreEqual(0.0, empty.MedianMs, 1e-9);
            Assert.AreEqual("null", LifeBenchStatistics.SummaryOrNull(empty));

            var single = LifeBenchStatistics.Summarise(new List<double> { 7.0 });
            Assert.AreEqual(1, single.Count);
            Assert.AreEqual(7.0, single.MedianMs, 1e-9);
            Assert.AreEqual(7.0, single.MeanMs, 1e-9);
            Assert.AreEqual(7.0, single.MinMs, 1e-9);
            Assert.AreEqual(7.0, single.MaxMs, 1e-9);
        }

        [Test]
        public void Summarise_ReportsMeanSeparatelyFromMedian()
        {
            // The distinction the review asked for: medians can look flat while the mean and
            // the tail are not.
            var samples = new List<double> { 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 1.0, 100.0 };

            var summary = LifeBenchStatistics.Summarise(samples);

            Assert.AreEqual(1.0, summary.MedianMs, 1e-9);
            Assert.AreEqual(10.9, summary.MeanMs, 1e-9);
            Assert.AreEqual(100.0, summary.MaxMs, 1e-9);
        }

        [Test]
        public void EdgeDecile_OfFewerThanTenSamples_UsesTheWholeSeries()
        {
            var samples = new List<double> { 2.0, 4.0 };

            Assert.AreEqual(2.0, LifeBenchStatistics.EdgeDecileMedian(samples, fromEnd: false), 1e-9);
            Assert.AreEqual(4.0, LifeBenchStatistics.EdgeDecileMedian(samples, fromEnd: true), 1e-9);
        }
    }
}
