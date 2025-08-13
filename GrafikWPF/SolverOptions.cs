namespace GrafikWPF
{
    public sealed class SolverOptions
    {
        public bool UseFlowUB { get; init; } = true;
        public int FlowCheckEveryDepth { get; init; } = 8;
        public bool UseGreedySeed { get; init; } = true;
        public bool UseLocalSearch { get; init; } = true;
        public bool UseTT { get; init; } = false;
        public int ProgressReportModulo { get; init; } = 0x7FF; // Przywrócono pierwotną wartość

        // A*
        public bool UseARAStar { get; init; } = false;
        public double[] EpsilonSchedule { get; init; } = new[] { 3.0, 2.0, 1.5, 1.0 };
        public double WeightedAStarW { get; init; } = 1.0;
        public int TimeBudgetSeconds { get; init; } = 60;

        // Beam (opcjonalnie)
        public int? BeamWidth { get; init; } = null;
    }
}