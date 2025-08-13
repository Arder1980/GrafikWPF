namespace GrafikWPF
{
    public static class SolverFactory
    {
        public static IGrafikSolver CreateSolver(
            SolverType typ,
            GrafikWejsciowy daneWejsciowe,
            List<SolverPriority> kolejnoscPriorytetow,
            IProgress<double>? progress,
            CancellationToken token)
        {
            switch (typ)
            {
                case SolverType.Backtracking:
                    return new BacktrackingSolver(daneWejsciowe, kolejnoscPriorytetow, progress, token);
                case SolverType.AStar:
                    return new AStarSolver(daneWejsciowe, kolejnoscPriorytetow, progress, token);
                case SolverType.Genetic:
                    return new GeneticSolver(daneWejsciowe, kolejnoscPriorytetow, progress, token);
                case SolverType.SimulatedAnnealing:
                    return new SimulatedAnnealingSolver(daneWejsciowe, kolejnoscPriorytetow, progress, token);
                case SolverType.TabuSearch:
                    return new TabuSearchSolver(daneWejsciowe, kolejnoscPriorytetow, progress, token);
                case SolverType.AntColony:
                    return new AntColonySolver(daneWejsciowe, kolejnoscPriorytetow, progress, token);
                default:
                    return new BacktrackingSolver(daneWejsciowe, kolejnoscPriorytetow, progress, token);
            }
        }
    }
}