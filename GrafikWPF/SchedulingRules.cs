#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace GrafikWPF
{
    /// <summary>
    /// Wspólne zasady dla wszystkich solverów (Backtracking, A*, metaheurystyki).
    /// - BC (BardzoChce) może łamać: (a) zakaz dzień-po-dniu, (b) zakaz sąsiedztwa z „Dyżur (inny)”.
    /// - MW (Mogę warunkowo): co najwyżej 1/miesiąc na lekarza (nie jest obowiązkowy).
    /// - „Rezerwowanie” przyszłych CH/BC: tylko, gdy są „unikalne” (dzień ma ≤1 realnego kandydata CH/BC).
    /// - Priorytety użytkownika sterują kolejnością sortowania kandydatów i polityką rezerw (OFF/SOFT/HARD).
    /// - Ocena rozwiązania: (1) maks. obsada, (2) ciągłość prefiksu, (3) sprawiedliwość ∝ limitom,
    ///   (4) równomierność (karzemy skupiska).
    /// </summary>
    public static class SchedulingRules
    {
        public const int UNASSIGNED = int.MinValue;

        public enum ReservePolicy { Off, Soft, Hard }

        // „Jak bardzo wyjątkowe” muszą być przyszłe CH/BC, by je chronić (1 = unikalne)
        private const int UNIQUE_CHBC_THRESHOLD = 1;

        public sealed class Context
        {
            public IReadOnlyList<DateTime> Days { get; }
            public IReadOnlyList<Lekarz> Doctors { get; }
            public IReadOnlyDictionary<DateTime, Dictionary<string, TypDostepnosci>> Avail { get; }
            public IReadOnlyDictionary<string, int> LimitsBySymbol { get; }
            public IReadOnlyList<SolverPriority> Priorities { get; }

            // Stan (uzupełnia solver):
            public int[] Assign;  // dzień -> idx lekarza; UNASSIGNED=nieobsadzone, -1=PUSTO
            public int[] Work;    // ile dyżurów ma lekarz
            public int[] MwUsed;  // zużycia MW
            public bool IsPrefixActive;

            // Wygody:
            public readonly int Dn, Pn;
            public readonly Dictionary<string, int> DocIndexBySymbol;

            public Context(
                IReadOnlyList<DateTime> days,
                IReadOnlyList<Lekarz> doctors,
                IReadOnlyDictionary<DateTime, Dictionary<string, TypDostepnosci>> avail,
                IReadOnlyDictionary<string, int> limits,
                IReadOnlyList<SolverPriority> priorities,
                int[] assign,
                int[] work,
                int[] mwUsed,
                bool isPrefixActive)
            {
                Days = days;
                Doctors = doctors;
                Avail = avail;
                LimitsBySymbol = limits;
                Priorities = priorities;

                Assign = assign;
                Work = work;
                MwUsed = mwUsed;
                IsPrefixActive = isPrefixActive;

                Dn = days.Count;
                Pn = doctors.Count;
                DocIndexBySymbol = new Dictionary<string, int>(Pn);
                for (int i = 0; i < Pn; i++) DocIndexBySymbol[doctors[i].Symbol] = i;
            }

            public int LimitOf(int doc) =>
                LimitsBySymbol.TryGetValue(Doctors[doc].Symbol, out var lim) ? lim : Dn;

            public TypDostepnosci Av(int day, int doc)
            {
                if (!Avail.TryGetValue(Days[day], out var map)) return TypDostepnosci.Niedostepny;
                return map.TryGetValue(Doctors[doc].Symbol, out var t) ? t : TypDostepnosci.Niedostepny;
            }

            public int EarliestUnassigned()
            {
                for (int d = 0; d < Dn; d++) if (Assign[d] == UNASSIGNED) return d;
                return -1;
            }
        }

        // ================== Twarde reguły ==================
        public static bool IsHardFeasible(int day, int doc, Context ctx)
        {
            if (doc < 0 || doc >= ctx.Pn) return false;
            if (day < 0 || day >= ctx.Dn) return false;

            if (ctx.Work[doc] >= ctx.LimitOf(doc)) return false;

            var av = ctx.Av(day, doc);

            // Wykluczenia bezwzględne
            if (av is TypDostepnosci.Niedostepny or TypDostepnosci.Urlop or TypDostepnosci.DyzurInny)
                return false;

            bool isBC = av == TypDostepnosci.BardzoChce;

            // Zakaz dzień-po-dniu (BC może łamać)
            if (!isBC)
            {
                if (day > 0 && ctx.Assign[day - 1] == doc) return false;
                if (day + 1 < ctx.Dn && ctx.Assign[day + 1] == doc) return false;
            }

            // Zakaz sąsiedztwa z „Inny dyżur” (BC może łamać)
            if (!isBC && IsNextToOtherDuty(day, doc, ctx)) return false;

            // MW – maks. 1 na lekarza (nie wymuszamy użycia)
            if (av == TypDostepnosci.MogeWarunkowo && ctx.MwUsed[doc] >= 1) return false;

            return true;
        }

        private static bool IsNextToOtherDuty(int day, int doc, Context ctx)
        {
            var sym = ctx.Doctors[doc].Symbol;
            if (day > 0)
            {
                var m = ctx.Avail[ctx.Days[day - 1]];
                if (m.TryGetValue(sym, out var t) && t == TypDostepnosci.DyzurInny) return true;
            }
            if (day + 1 < ctx.Dn)
            {
                var m = ctx.Avail[ctx.Days[day + 1]];
                if (m.TryGetValue(sym, out var t) && t == TypDostepnosci.DyzurInny) return true;
            }
            return false;
        }

        // ================== Polityka rezerw CH/BC ==================
        public static ReservePolicy GetReservePolicy(int day, Context ctx)
        {
            var p1 = ctx.Priorities.Count > 0 ? ctx.Priorities[0] : SolverPriority.LacznaLiczbaObsadzonychDni;

            bool nextLooksBad = !NextDayHasAnyCandidateLoose(day, ctx);

            return p1 switch
            {
                SolverPriority.CiagloscPoczatkowa => (ctx.IsPrefixActive && day == ctx.EarliestUnassigned())
                                                      ? (nextLooksBad ? ReservePolicy.Hard : ReservePolicy.Off)
                                                      : ReservePolicy.Hard,
                SolverPriority.LacznaLiczbaObsadzonychDni => nextLooksBad ? ReservePolicy.Off : ReservePolicy.Soft,
                SolverPriority.SprawiedliwoscObciazenia => ReservePolicy.Soft,
                SolverPriority.RownomiernoscRozlozenia => ReservePolicy.Soft,
                _ => ReservePolicy.Soft,
            };
        }

        // HARD filtruje MG/MW tylko, gdy zjadają „unikalne” przyszłe CH/BC
        public static bool WouldStealFromFutureUniqueChBc(int day, int doc, Context ctx)
        {
            var avToday = ctx.Av(day, doc);
            if (avToday != TypDostepnosci.Moge && avToday != TypDostepnosci.MogeWarunkowo)
                return false;

            int uniqueNeed = CountFutureUniqueChBc(day, doc, ctx);
            int remainingAfter = ctx.LimitOf(doc) - ctx.Work[doc] - 1;
            return remainingAfter < uniqueNeed;
        }

        private static int CountFutureUniqueChBc(int fromDay, int doc, Context ctx)
        {
            int count = 0;
            for (int d = fromDay + 1; d < ctx.Dn; d++)
            {
                if (ctx.Assign[d] != UNASSIGNED) continue;
                var avDoc = ctx.Av(d, doc);
                bool isChBcHere = avDoc is TypDostepnosci.Chce or TypDostepnosci.BardzoChce;
                if (!isChBcHere) continue;

                // policz ilu realnych kandydatów CH/BC ma ten dzień
                int feasibleChBc = 0;
                for (int q = 0; q < ctx.Pn; q++)
                {
                    var av = ctx.Av(d, q);
                    if (av is not (TypDostepnosci.Chce or TypDostepnosci.BardzoChce)) continue;

                    bool isBC = av == TypDostepnosci.BardzoChce;
                    // lokalna wykonalność: dzień-po-dniu i Inny dyżur (BC może łamać)
                    if (!isBC)
                    {
                        if (d > 0 && ctx.Assign[d - 1] == q) continue;
                        if (d + 1 < ctx.Dn && ctx.Assign[d + 1] == q) continue;
                    }
                    if (!isBC && IsNextToOtherDuty(d, q, ctx)) continue;

                    feasibleChBc++;
                    if (feasibleChBc > UNIQUE_CHBC_THRESHOLD) break;
                }

                if (feasibleChBc <= UNIQUE_CHBC_THRESHOLD)
                    count++;
            }
            return count;
        }

        private static bool NextDayHasAnyCandidateLoose(int day, Context ctx)
        {
            int dn = day + 1;
            if (dn >= ctx.Dn) return true;
            if (ctx.Assign[dn] != UNASSIGNED) return true;
            for (int q = 0; q < ctx.Pn; q++)
                if (IsHardFeasible(dn, q, ctx)) return true;
            return false;
        }

        // ================== Prefiks: patrzymy kilka dni do przodu ==================
        /// <summary>
        /// Przybliżona długość ciągłego prefiksu, jeśli dziś wybierzemy „doc”.
        /// Szybkie okno look-ahead (domyślnie 4 dni).
        /// </summary>
        public static int ContinuityLookaheadSpan(int day, int doc, Context ctx, int window = 4)
        {
            int end = Math.Min(ctx.Dn - 1, day + window);
            // dziś: sprawdzamy, czy doc jest w ogóle legalny (tu heavy check robi solver)
            int span = 1;

            for (int d = day + 1; d <= end; d++)
            {
                if (ctx.Assign[d] != UNASSIGNED) { span++; continue; }

                bool feasibleThere = false;
                for (int q = 0; q < ctx.Pn; q++)
                {
                    // wpływ dzisiejszego wyboru: sąsiedztwo tylko dla q==doc i d==day+1
                    if (q == doc && d == day + 1)
                    {
                        var av0 = ctx.Av(day, doc);
                        if (!IsHardFeasibleWithHypo(d, q, day, doc, av0, ctx)) continue;
                        feasibleThere = true; break;
                    }
                    else
                    {
                        if (IsHardFeasible(d, q, ctx)) { feasibleThere = true; break; }
                    }
                }

                if (!feasibleThere) break;
                span++;
            }
            return span;
        }

        private static bool IsHardFeasibleWithHypo(int dayToCheck, int cand, int hypoDay, int hypoDoc, TypDostepnosci hypoAv, Context ctx)
        {
            if (ctx.Work[cand] >= ctx.LimitOf(cand)) return false;

            var av = ctx.Av(dayToCheck, cand);
            if (av is TypDostepnosci.Niedostepny or TypDostepnosci.Urlop or TypDostepnosci.DyzurInny)
                return false;

            bool isBC = av == TypDostepnosci.BardzoChce;

            // hipotetyczne dzień-po-dniu z wyborem w hypoDay (BC może łamać)
            if (!isBC && cand == hypoDoc && Math.Abs(dayToCheck - hypoDay) == 1)
                return false;

            if (!isBC && IsNextToOtherDuty(dayToCheck, cand, ctx))
                return false;

            int mw = ctx.MwUsed[cand];
            if (cand == hypoDoc && hypoAv == TypDostepnosci.MogeWarunkowo) mw++;
            if (av == TypDostepnosci.MogeWarunkowo && mw >= 1) return false;

            return true;
        }

        // ================== Porządkowanie i ocena ==================
        public static List<int> OrderCandidates(int day, Context ctx)
        {
            var policy = GetReservePolicy(day, ctx);
            var legal = new List<int>(ctx.Pn);
            for (int p = 0; p < ctx.Pn; p++)
                if (IsHardFeasible(day, p, ctx)) legal.Add(p);

            if (legal.Count == 0) return legal;

            if (policy == ReservePolicy.Hard)
            {
                legal = legal.Where(p =>
                {
                    var av = ctx.Av(day, p);
                    if (av == TypDostepnosci.Moge || av == TypDostepnosci.MogeWarunkowo)
                        return !WouldStealFromFutureUniqueChBc(day, p, ctx);
                    return true;
                }).ToList();
            }

            legal.Sort((a, b) => CompareCandidates(day, a, b, policy, ctx));
            return legal;
        }

        private static int CompareCandidates(int day, int a, int b, ReservePolicy policy, Context ctx)
        {
            var avA = ctx.Av(day, a);
            var avB = ctx.Av(day, b);

            // Rezerwacja zawsze pierwsza
            int rzA = avA == TypDostepnosci.Rezerwacja ? 1 : 0;
            int rzB = avB == TypDostepnosci.Rezerwacja ? 1 : 0;
            if (rzA != rzB) return rzB.CompareTo(rzA);

            foreach (var pr in ctx.Priorities)
            {
                int cmp = 0;
                switch (pr)
                {
                    case SolverPriority.CiagloscPoczatkowa:
                        int spanA = ContinuityLookaheadSpan(day, a, ctx, window: 4);
                        int spanB = ContinuityLookaheadSpan(day, b, ctx, window: 4);
                        cmp = spanB.CompareTo(spanA); // większy span lepiej
                        if (cmp != 0) return cmp;
                        break;

                    case SolverPriority.SprawiedliwoscObciazenia:
                        double ra = RatioAfter(a, ctx);
                        double rb = RatioAfter(b, ctx);
                        cmp = ra.CompareTo(rb); // mniejszy lepszy
                        if (cmp != 0) return cmp;
                        break;

                    case SolverPriority.RownomiernoscRozlozenia:
                        int da = NearestAssignedDistance(day, a, ctx);
                        int db = NearestAssignedDistance(day, b, ctx);
                        cmp = db.CompareTo(da); // większy dystans lepiej
                        if (cmp != 0) return cmp;
                        break;

                    case SolverPriority.LacznaLiczbaObsadzonychDni:
                        // brak lokalnego rozróżnienia – przechodzimy dalej
                        break;
                }
            }

            if (policy == ReservePolicy.Soft)
            {
                int pa = PrefRank(avA), pb = PrefRank(avB);
                if (pa != pb) return pb.CompareTo(pa);
            }

            int wCmp = ctx.Work[a].CompareTo(ctx.Work[b]);
            if (wCmp != 0) return wCmp;

            int remA = ctx.LimitOf(a) - ctx.Work[a];
            int remB = ctx.LimitOf(b) - ctx.Work[b];
            int remCmp = remB.CompareTo(remA);
            if (remCmp != 0) return remCmp;

            int prefCmp = PrefRank(avB).CompareTo(PrefRank(avA));
            if (prefCmp != 0) return prefCmp;

            return a.CompareTo(b);
        }

        public static long[] EvaluateSolution(RozwiazanyGrafik sol, IReadOnlyList<SolverPriority> priorities, Context ctx)
        {
            var perDoc = new int[ctx.Pn];
            int assigned = 0;
            for (int d = 0; d < ctx.Dn; d++)
            {
                var date = ctx.Days[d];
                if (!sol.Przypisania.TryGetValue(date, out var l) || l is null) continue;
                assigned++;
                if (ctx.DocIndexBySymbol.TryGetValue(l.Symbol, out var idx)) perDoc[idx]++;
            }

            long sObs = assigned;

            long sCont = 0;
            for (int d = 0; d < ctx.Dn; d++)
            {
                var date = ctx.Days[d];
                if (!sol.Przypisania.TryGetValue(date, out var l) || l is null) break;
                sCont++;
            }

            long sFair = ComputeProportionalFairness(perDoc, assigned, ctx);
            long sEven = ComputeEvenness(sol, ctx);

            var map = new Dictionary<SolverPriority, long>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, sObs },
                { SolverPriority.CiagloscPoczatkowa, sCont },
                { SolverPriority.SprawiedliwoscObciazenia, sFair },
                { SolverPriority.RownomiernoscRozlozenia, sEven }
            };

            var vec = new long[priorities.Count];
            for (int i = 0; i < priorities.Count; i++)
                vec[i] = map.TryGetValue(priorities[i], out var v) ? v : 0;
            return vec;
        }

        // ======= drobne narzędzia =======
        private static int PrefRank(TypDostepnosci a) => a switch
        {
            TypDostepnosci.Rezerwacja => 5,
            TypDostepnosci.BardzoChce => 4,
            TypDostepnosci.Chce => 3,
            TypDostepnosci.Moge => 2,
            TypDostepnosci.MogeWarunkowo => 1,
            _ => 0
        };

        private static double RatioAfter(int doc, Context ctx)
        {
            double lim = Math.Max(1, ctx.LimitOf(doc));
            return (ctx.Work[doc] + 1) / lim;
        }

        private static int NearestAssignedDistance(int day, int doc, Context ctx)
        {
            int best = int.MaxValue;
            for (int d = day - 1; d >= 0; d--)
                if (ctx.Assign[d] == doc) { best = Math.Min(best, day - d); break; }
            for (int d = day + 1; d < ctx.Dn; d++)
                if (ctx.Assign[d] == doc) { best = Math.Min(best, d - day); break; }
            return best == int.MaxValue ? 9999 : best;
        }

        private static long ComputeProportionalFairness(int[] perDoc, int total, Context ctx)
        {
            if (total <= 0) return 0;
            long sumLim = 0;
            var L = new long[ctx.Pn];
            for (int i = 0; i < ctx.Pn; i++) { L[i] = Math.Max(0, ctx.LimitOf(i)); sumLim += L[i]; }
            if (sumLim <= 0) return 0;

            double sumAbs = 0.0;
            for (int i = 0; i < ctx.Pn; i++)
            {
                double expected = total * (L[i] / (double)sumLim);
                sumAbs += Math.Abs(perDoc[i] - expected);
            }
            return -(long)Math.Round(sumAbs * 1000.0); // większe = lepsze
        }

        private static long ComputeEvenness(RozwiazanyGrafik sol, Context ctx)
        {
            var perDayDoc = new int[ctx.Dn];
            Array.Fill(perDayDoc, -1);
            for (int d = 0; d < ctx.Dn; d++)
            {
                var date = ctx.Days[d];
                if (!sol.Przypisania.TryGetValue(date, out var l) || l is null) continue;
                if (ctx.DocIndexBySymbol.TryGetValue(l.Symbol, out var idx))
                    perDayDoc[d] = idx;
            }
            int penalty = 0;
            for (int d = 0; d < ctx.Dn; d++)
            {
                int p = perDayDoc[d];
                if (p < 0) continue;
                if (d > 0 && perDayDoc[d - 1] == p) penalty++;
                if (d + 1 < ctx.Dn && perDayDoc[d + 1] == p) penalty++;
            }
            return -penalty; // większe = lepsze
        }
    }
}
