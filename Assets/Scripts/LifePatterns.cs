namespace ConwayGameOfLife
{
    /// <summary>
    /// Classification of a pattern by its behaviour over time.
    /// </summary>
    public enum LifePatternKind
    {
        /// <summary>Unchanged by every generation (period 1).</summary>
        StillLife,

        /// <summary>Returns to its own shape every 2 generations.</summary>
        Oscillator,

        /// <summary>Returns to its own shape after 3 or more generations but stays in place.</summary>
        PeriodicOscillator,

        /// <summary>Returns to its own shape after N generations, translated across the board.</summary>
        Spaceship
    }

    public sealed class LifePattern
    {
        public string Name { get; }
        public string EnglishName { get; }
        public LifePatternKind Kind { get; }
        public int Period { get; }
        public LifeCell[] Cells { get; }

        public LifePattern(string name, string englishName, LifePatternKind kind, int period, params LifeCell[] cells)
        {
            Name = name;
            EnglishName = englishName;
            Kind = kind;
            Period = period;
            Cells = cells;
        }
    }

    public static class LifePatterns
    {
        /// <summary>
        /// Built-in specimen archive. Every entry's <see cref="LifePattern.Period"/> is asserted
        /// by the EditMode test suite, so the declared values cannot drift from real behaviour.
        /// </summary>
        public static readonly LifePattern[] All =
        {
            // --- 稳定状态 / STILL LIFE (period 1) -----------------------------
            new("方块", "BLOCK", LifePatternKind.StillLife, 1,
                new(0, 0), new(1, 0), new(0, 1), new(1, 1)),
            new("蜂巢", "BEEHIVE", LifePatternKind.StillLife, 1,
                new(1, 0), new(2, 0), new(0, 1), new(3, 1), new(1, 2), new(2, 2)),

            // --- 振荡状态 / OSCILLATOR (period 2) -----------------------------
            new("闪烁器", "BLINKER", LifePatternKind.Oscillator, 2,
                new(0, 0), new(1, 0), new(2, 0)),
            new("蟾蜍", "TOAD", LifePatternKind.Oscillator, 2,
                new(1, 0), new(2, 0), new(3, 0), new(0, 1), new(1, 1), new(2, 1)),

            // --- 循环震荡 / PERIODIC OSCILLATOR (period >= 3) -----------------
            new("脉冲星", "PULSAR", LifePatternKind.PeriodicOscillator, 3,
                new(2, 0), new(3, 0), new(4, 0), new(8, 0), new(9, 0), new(10, 0),
                new(0, 2), new(5, 2), new(7, 2), new(12, 2),
                new(0, 3), new(5, 3), new(7, 3), new(12, 3),
                new(0, 4), new(5, 4), new(7, 4), new(12, 4),
                new(2, 5), new(3, 5), new(4, 5), new(8, 5), new(9, 5), new(10, 5),
                new(2, 7), new(3, 7), new(4, 7), new(8, 7), new(9, 7), new(10, 7),
                new(0, 8), new(5, 8), new(7, 8), new(12, 8),
                new(0, 9), new(5, 9), new(7, 9), new(12, 9),
                new(0, 10), new(5, 10), new(7, 10), new(12, 10),
                new(2, 12), new(3, 12), new(4, 12), new(8, 12), new(9, 12), new(10, 12)),
            new("十五周期振荡器", "PENTADECATHLON", LifePatternKind.PeriodicOscillator, 15,
                new(2, 0), new(7, 0),
                new(0, 1), new(1, 1), new(3, 1), new(4, 1), new(5, 1), new(6, 1), new(8, 1), new(9, 1),
                new(2, 2), new(7, 2)),

            // --- 飞船 / SPACESHIP (translated every period) -------------------
            new("滑翔机", "GLIDER", LifePatternKind.Spaceship, 4,
                new(1, 0), new(2, 1), new(0, 2), new(1, 2), new(2, 2)),
            new("轻型飞船", "LWSS", LifePatternKind.Spaceship, 4,
                new(1, 0), new(4, 0), new(0, 1), new(0, 2), new(4, 2),
                new(0, 3), new(1, 3), new(2, 3), new(3, 3))
        };
    }
}
