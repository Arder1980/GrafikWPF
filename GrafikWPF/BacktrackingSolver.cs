using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GrafikWPF.Heuristics;

namespace GrafikWPF
{
    public class BacktrackingSolver : IGrafikSolver
    {
        private const int UNASSIGNED = int.MinValue;

        private readonly GrafikWejsciowy _in;
        private readonly List<SolverPriority> _prio;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _ct;
        private readonly SolverOptions _opt;

        private readonly int _days, _docs;
        private readonly List<Lekarz> _docsMap;

        private readonly int[,] _av;
        private readonly int[] _limit;
        private readonly bool[,] _nextToOther;
        private readonly List<int>[] _staticCands;

        private readonly int[] _sufBC, _sufCh, _sufMg;
        private readonly int _totBC, _totCh, _totMg;

        private int[] _assign = Array.Empty<int>();
        private int[] _work = Array.Empty<int>();
        private bool[] _condUsed = Array.Empty<bool>();
        private int _assignedCount, _obs, _prefx, _bc, _ch, _mg;

        private long[] _bestVec = Array.Empty<long>();
        private int[] _bestAssign = Array.Empty<int>();

        private readonly bool _useTT;
        private readonly Dictionary<(int day, long hash), long[]> _tt = new(1 << 16);
        private long[,] _zDayDoc = new long[0, 0];
        private long[] _zEmpty = Array.Empty<long>();
        private long[] _zUnass = Array.Empty<long>();
        private long[,] _zCond = new long[0, 0];
        private long _hash;

        public BacktrackingSolver(GrafikWejsciowy dane, List<SolverPriority> kolej, IProgress<double>? pr = null, CancellationToken ct = default, SolverOptions? opt = null)
        {
            _in = dane; _prio = kolej; _progress = pr; _ct = ct; _opt = opt ?? new SolverOptions();
            _docsMap = _in.Lekarze.FindAll(l => l.IsAktywny);
            _days = _in.DniWMiesiacu.Count; _docs = _docsMap.Count;

            _av = new int[_days, _docs];
            _limit = new int[_docs];
            _nextToOther = new bool[_days, _docs];

            for (int p = 0; p < _docs; p++)
            {
                var sym = _docsMap[p].Symbol;
                _limit[p] = _in.LimityDyzurow.TryGetValue(sym, out var lim) ? lim : _days;
            }

            bool[] anyBC = new bool[_days], anyCh = new bool[_days], anyMg = new bool[_days];
            for (int d = 0; d < _days; d++)
            {
                var day = _in.DniWMiesiacu[d];
                for (int p = 0; p < _docs; p++)
                {
                    var sym = _docsMap[p].Symbol;
                    var a = _in.Dostepnosc[day].GetValueOrDefault(sym, TypDostepnosci.Niedostepny);
                    _av[d, p] = (int)a;
                    var y = day.AddDays(-1); var t = day.AddDays(1);
                    if ((_in.Dostepnosc.ContainsKey(y) && _in.Dostepnosc[y][sym] == TypDostepnosci.DyzurInny) ||
                        (_in.Dostepnosc.ContainsKey(t) && _in.Dostepnosc[t][sym] == TypDostepnosci.DyzurInny))
                        _nextToOther[d, p] = true;
                    if (a == TypDostepnosci.BardzoChce) anyBC[d] = true;
                    if (a == TypDostepnosci.Chce) anyCh[d] = true;
                    if (a == TypDostepnosci.Moge || a == TypDostepnosci.MogeWarunkowo) anyMg[d] = true;
                }
            }
            _sufBC = BuildSuffix(anyBC); _sufCh = BuildSuffix(anyCh); _sufMg = BuildSuffix(anyMg);
            _totBC = Count(anyBC); _totCh = Count(anyCh); _totMg = Count(anyMg);

            _staticCands = new List<int>[_days];
            for (int d = 0; d < _days; d++)
            {
                var v = new List<int>(Math.Max(2, _docs / 4));
                for (int p = 0; p < _docs; p++)
                {
                    var av = (TypDostepnosci)_av[d, p];
                    if (av is TypDostepnosci.Niedostepny or TypDostepnosci.Urlop or TypDostepnosci.DyzurInny) continue;
                    v.Add(p);
                }
                _staticCands[d] = v;
            }

            _useTT = _opt.UseTT;
            if (_useTT)
            {
                var rnd = new Random(12345);
                _zDayDoc = new long[_days, _docs];
                _zEmpty = new long[_days]; _zUnass = new long[_days]; _zCond = new long[_docs, 2];
                for (int d = 0; d < _days; d++)
                {
                    _zEmpty[d] = rnd.NextInt64(); _zUnass[d] = rnd.NextInt64();
                    for (int p = 0; p < _docs; p++) _zDayDoc[d, p] = rnd.NextInt64();
                }
                for (int p = 0; p < _docs; p++) { _zCond[p, 0] = rnd.NextInt64(); _zCond[p, 1] = rnd.NextInt64(); }
            }
        }

        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            int[] seedWl;
            int[] seed = GreedySeed.Generate(_in, out seedWl);
            if (_opt.UseLocalSearch) LocalSearch.Improve(seed, _in);

            if (CheckSeedFeasible(seed))
            {
                var seedMetrics = ToMetrics(seed);
                _bestVec = EvaluationAndScoringService.ToIntVector(seedMetrics, _prio);
                _bestAssign = (int[])seed.Clone();
            }
            else
            {
                _bestVec = new long[_prio.Count + 4];
                for (int i = 0; i < _bestVec.Length; i++) _bestVec[i] = long.MinValue;
                _bestAssign = new int[_days];
                Array.Fill(_bestAssign, -1);
            }

            _assign = new int[_days]; Array.Fill(_assign, UNASSIGNED);
            _work = new int[_docs];
            _condUsed = new bool[_docs];
            _assignedCount = 0; _obs = 0; _prefx = 0; _bc = 0; _ch = 0; _mg = 0;

            if (_useTT)
            {
                _hash = 0; for (int d = 0; d < _days; d++) _hash ^= _zUnass[d];
                for (int p = 0; p < _docs; p++) _hash ^= _zCond[p, 0];
            }

            DFS(0);

            _progress?.Report(1.0);
            return ToMetrics(_bestAssign);
        }

        private void DFS(int depth)
        {
            _ct.ThrowIfCancellationRequested();

            if (_assignedCount == _days)
            {
                var m = ToMetrics(_assign);
                var vec = EvaluationAndScoringService.ToIntVector(m, _prio);
                if (Less(vec, _bestVec)) { Array.Copy(_assign, _bestAssign, _days); _bestVec = vec; }
                return;
            }

            if (!CanBeatBest()) return;

            int day = NextDayByMRV();

            var legal = new List<int>(Math.Max(1, _staticCands[day].Count));
            var baseList = _staticCands[day];
            for (int i = 0; i < baseList.Count; i++)
            {
                int p = baseList[i];
                if (IsValidDynamic(day, p)) legal.Add(p);
            }

            legal.Sort((a, b) => CompareDelta(day, a, b));

            var totalBranches = legal.Count + 1;

            for (int i = 0; i < legal.Count; i++)
            {
                if (depth == 0)
                {
                    _progress?.Report((double)i / totalBranches);
                }

                int p = legal[i];
                Make(day, p);
                DFS(depth + 1);
                Unmake(day, p);
            }

            if (depth == 0)
            {
                _progress?.Report((double)legal.Count / totalBranches);
            }
            Make(day, -1);
            DFS(depth + 1);
            Unmake(day, -1);
        }

        private int CompareDelta(int day, int pa, int pb)
        {
            if (pa == pb) return 0;
            var da = BuildDelta(day, pa);
            var db = BuildDelta(day, pb);
            for (int i = 0; i < da.Length; i++)
            {
                if (da[i] == db[i]) continue;
                return da[i] > db[i] ? -1 : 1;
            }
            int ra = PrefRank((TypDostepnosci)_av[day, pa]);
            int rb = PrefRank((TypDostepnosci)_av[day, pb]);
            if (ra != rb) return ra > rb ? -1 : 1;

            int wla = _work[pa], wlb = _work[pb];
            if (wla != wlb) return wla < wlb ? -1 : 1;
            return pa.CompareTo(pb);
        }

        private static int PrefRank(TypDostepnosci a)
        {
            return a switch
            {
                TypDostepnosci.Rezerwacja => 4,
                TypDostepnosci.BardzoChce => 3,
                TypDostepnosci.Chce => 2,
                TypDostepnosci.Moge => 1,
                _ => 0
            };
        }

        private long[] BuildDelta(int day, int p)
        {
            var res = new long[_prio.Count + 4];
            int k = 0;

            var deltaMetrics = new Dictionary<SolverPriority, long>();
            deltaMetrics[SolverPriority.LacznaLiczbaObsadzonychDni] = 1;
            deltaMetrics[SolverPriority.CiagloscPoczatkowa] = (_prefx == day ? 1 : 0);

            int max = 0, min = int.MaxValue;
            for (int d = 0; d < _docs; d++) { int w = _work[d]; if (w > max) max = w; if (w < min) min = w; }
            int nb = _work[p] + 1;
            int nmax = Math.Max(max, nb);
            int nmin = Math.Min(min, (p == IndexOfMin(_work, min) ? nb : min));
            deltaMetrics[SolverPriority.SprawiedliwoscObciazenia] = (max - min) - (nmax - nmin);

            int last = LastAssignedBefore(day, p);
            deltaMetrics[SolverPriority.RownomiernoscRozlozenia] = (last < 0) ? 100 : (day - last - 1);

            foreach (var pr in _prio)
            {
                res[k++] = deltaMetrics[pr];
            }

            var av = (TypDostepnosci)_av[day, p];
            res[k++] = av == TypDostepnosci.Rezerwacja ? 1 : 0;
            res[k++] = av == TypDostepnosci.BardzoChce ? 1 : 0;
            res[k++] = av == TypDostepnosci.Chce ? 1 : 0;
            res[k++] = (av == TypDostepnosci.Moge || av == TypDostepnosci.MogeWarunkowo) ? 1 : 0;
            return res;
        }

        private int IndexOfMin(int[] arr, int currentMin)
        {
            for (int i = 0; i < arr.Length; i++) if (arr[i] == currentMin) return i;
            return -1;
        }

        private bool IsValidDynamic(int day, int p)
        {
            if (p < 0 || p >= _docs) return false;
            if (_work[p] >= _limit[p]) return false;

            var av = (TypDostepnosci)_av[day, p];
            bool bc = av == TypDostepnosci.BardzoChce;

            if (!bc && day > 0)
            {
                int prev = _assign[day - 1];
                if (prev == p) return false;
            }
            if (!bc && _nextToOther[day, p]) return false;
            if (av == TypDostepnosci.MogeWarunkowo && _condUsed[p]) return false;
            return true;
        }

        private int NextDayByMRV()
        {
            int best = -1, bestCnt = int.MaxValue, bestBC = -1, bestCh = -1, firstUnassigned = -1;
            for (int d = 0; d < _days; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;
                if (firstUnassigned == -1) firstUnassigned = d;

                int cnt = 1; int bcCnt = 0; int chCnt = 0;
                var lst = _staticCands[d];
                for (int i = 0; i < lst.Count; i++)
                {
                    int p = lst[i];
                    if (IsValidDynamic(d, p))
                    {
                        cnt++;
                        var av = (TypDostepnosci)_av[d, p];
                        if (av == TypDostepnosci.BardzoChce) bcCnt++;
                        else if (av == TypDostepnosci.Chce) chCnt++;
                    }
                }
                bool better = cnt < bestCnt
                              || (cnt == bestCnt && bcCnt > bestBC)
                              || (cnt == bestCnt && bcCnt == bestBC && chCnt > bestCh);
                if (better) { best = d; bestCnt = cnt; bestBC = bcCnt; bestCh = chCnt; }
            }
            if (best == -1) best = firstUnassigned;
            return best;
        }

        private void Make(int day, int p)
        {
            int old = _assign[day];
            if (_useTT)
            {
                if (old == UNASSIGNED) _hash ^= _zUnass[day];
                else if (old == -1) _hash ^= _zEmpty[day];
                else _hash ^= _zDayDoc[day, old];
            }

            _assign[day] = p;
            _assignedCount++;

            if (p != -1)
            {
                _obs++; _work[p]++;
                var av = (TypDostepnosci)_av[day, p];
                if (av == TypDostepnosci.BardzoChce) _bc++;
                else if (av == TypDostepnosci.Chce) _ch++;
                else _mg++;

                if (av == TypDostepnosci.MogeWarunkowo && !_condUsed[p]) { if (_useTT) _hash ^= _zCond[p, 0]; _condUsed[p] = true; if (_useTT) _hash ^= _zCond[p, 1]; }
            }

            if (_useTT)
            {
                if (p == -1) _hash ^= _zEmpty[day];
                else _hash ^= _zDayDoc[day, p];
            }

            RecomputePrefix();
        }

        private void Unmake(int day, int p)
        {
            if (p != -1)
            {
                _obs--; _work[p]--;
                var av = (TypDostepnosci)_av[day, p];
                if (av == TypDostepnosci.BardzoChce) _bc--;
                else if (av == TypDostepnosci.Chce) _ch--;
                else _mg--;
                if (av == TypDostepnosci.MogeWarunkowo && _condUsed[p]) { if (_useTT) _hash ^= _zCond[p, 1]; _condUsed[p] = false; if (_useTT) _hash ^= _zCond[p, 0]; }
            }
            if (_useTT)
            {
                if (p == -1) _hash ^= _zEmpty[day];
                else _hash ^= _zDayDoc[day, p];
                _hash ^= _zUnass[day];
            }
            _assign[day] = UNASSIGNED; _assignedCount--;
            RecomputePrefix();
        }

        private void RecomputePrefix()
        {
            int k = 0;
            while (k < _days)
            {
                int a = _assign[k];
                if (a == UNASSIGNED || a == -1) break;
                k++;
            }
            _prefx = k;
        }

        private int LastAssignedBefore(int day, int p)
        {
            for (int d = day - 1; d >= 0; d--) if (_assign[d] == p) return d;
            return -1;
        }

        private bool CanBeatBest()
        {
            var g = new long[_prio.Count + 4];
            int gi = 0;

            var currentMetrics = new Dictionary<SolverPriority, long>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, _obs },
                { SolverPriority.CiagloscPoczatkowa, _prefx },
                { SolverPriority.SprawiedliwoscObciazenia, 0 },
                { SolverPriority.RownomiernoscRozlozenia, 0 }
            };

            foreach (var p in _prio) g[gi++] = currentMetrics[p];

            g[gi++] = 0; // Rezerwacje
            g[gi++] = _bc;
            g[gi++] = _ch;
            g[gi++] = _mg;

            int remaining = _days - _assignedCount;

            int remCap = 0; for (int p = 0; p < _docs; p++) remCap += Math.Max(0, _limit[p] - _work[p]);
            int ubObs = Math.Min(remaining, remCap);
            int ubPref = (_prefx == FirstNonBrokenIndex()) ? remaining : 0;

            var add = new long[_prio.Count + 4];
            gi = 0;

            var heuMetrics = new Dictionary<SolverPriority, long>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, ubObs },
                { SolverPriority.CiagloscPoczatkowa, ubPref },
                { SolverPriority.SprawiedliwoscObciazenia, 0 },
                { SolverPriority.RownomiernoscRozlozenia, 0 }
            };
            foreach (var p in _prio) add[gi++] = heuMetrics[p];

            int next = EarliestOpenIndex();
            add[gi++] = 0; // Rezerwacje
            add[gi++] = _sufBC[next];
            add[gi++] = _sufCh[next];
            add[gi++] = _sufMg[next];

            var caps = new long[_prio.Count + 4];
            gi = 0;

            foreach (var p in _prio) caps[gi++] = (p == SolverPriority.SprawiedliwoscObciazenia || p == SolverPriority.RownomiernoscRozlozenia) ? 0 : _days;

            caps[gi++] = long.MaxValue; // Rezerwacje
            caps[gi++] = _totBC;
            caps[gi++] = _totCh;
            caps[gi++] = _totMg;

            var ub = new long[_prio.Count + 4];
            for (int i = 0; i < ub.Length; i++)
            {
                long s = g[i] + add[i];
                long c = caps[i];
                ub[i] = c > 0 ? (s > c ? c : s) : s;
            }

            if (_useTT)
            {
                var key = (_assignedCount, _hash);
                if (_tt.TryGetValue(key, out var bestG) && !LessSpan(ub, bestG)) return false;
                _tt[key] = ub.ToArray();
            }

            return LessSpan(ub, _bestVec);
        }

        private int FirstNonBrokenIndex()
        {
            for (int i = 0; i < _prefx; i++) if (_assign[i] == -1) return -1;
            return _prefx;
        }

        private int EarliestOpenIndex()
        {
            for (int i = 0; i < _days; i++)
            {
                int a = _assign[i];
                if (a == UNASSIGNED) return i;
                if (a == -1) return i + 1;
            }
            return _days;
        }

        private static int[] BuildSuffix(bool[] any) { int n = any.Length; var s = new int[n + 1]; for (int i = n - 1; i >= 0; i--) s[i] = s[i + 1] + (any[i] ? 1 : 0); return s; }
        private static int Count(bool[] a) { int c = 0; for (int i = 0; i < a.Length; i++) if (a[i]) c++; return c; }

        private RozwiazanyGrafik ToMetrics(int[] assign)
        {
            var map = new Dictionary<DateTime, Lekarz?>(assign.Length);
            var ob = new Dictionary<string, int>(_docs);
            for (int p = 0; p < _docs; p++) ob[_docsMap[p].Symbol] = 0;
            for (int d = 0; d < _days; d++)
            {
                var day = _in.DniWMiesiacu[d];
                int p = assign[d];
                if (p >= 0) { var L = _docsMap[p]; map[day] = L; ob[L.Symbol]++; }
                else map[day] = null;
            }
            return EvaluationAndScoringService.CalculateMetrics(map, ob, _in);
        }

        private static bool Less(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++) { if (a[i] == b[i]) continue; return a[i] > b[i]; }
            return false;
        }
        private static bool LessSpan(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++) { if (a[i] == b[i]) continue; return a[i] > b[i]; }
            return false;
        }

        private bool CheckSeedFeasible(int[] a)
        {
            var wl = new int[_docs];
            var usedCond = new bool[_docs];
            for (int d = 0; d < _days; d++)
            {
                int p = a[d];
                if (p < 0) continue;
                wl[p]++;
                if (wl[p] > _limit[p]) return false;
                var av = (TypDostepnosci)_av[d, p];
                if (av != TypDostepnosci.BardzoChce && d > 0 && a[d - 1] == p) return false;
                if (av != TypDostepnosci.BardzoChce && _nextToOther[d, p]) return false;
                if (av == TypDostepnosci.MogeWarunkowo)
                {
                    if (usedCond[p]) return false;
                    usedCond[p] = true;
                }
            }
            return true;
        }
    }
}