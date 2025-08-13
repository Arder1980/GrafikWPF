using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using GrafikWPF.Heuristics;

namespace GrafikWPF
{
    public class AStarSolver : IGrafikSolver
    {
        private const int BOOTSTRAP_DEPTH = 8;
        private const int CLOSURE_EVERY = 1;
        private const int FLOW_EVERY_DEPTH = 6;

        private readonly GrafikWejsciowy _in;
        private readonly List<SolverPriority> _prio;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _ct;
        private readonly SolverOptions _opt;

        private readonly int _days, _docs;
        private readonly List<Lekarz> _docsMap;
        private readonly int[,] _av;
        private readonly int[] _limit;
        private readonly bool[,] _nextOther;

        private static long[,] _z = new long[0, 0];
        private static long[] _zEmpty = Array.Empty<long>();
        private static bool _zInit;

        private readonly int[] _sufBC, _sufCh, _sufMg;
        private readonly int _totBC, _totCh, _totMg;

        private readonly List<int>[] _staticCands;
        private readonly long[] _caps;

        private readonly Dictionary<(int nextDay, long capHash), int> _flowCache = new(1 << 12);
        private readonly NodePool _pool = new();

        private struct Node
        {
            public int Parent, Day, LastDoc;
            public long Hash;
            public long[] G;
            public int[] Work;
            public bool[] UsedCond;
            public int BC, CH, MG, Pref, Obs;
        }

        public AStarSolver(GrafikWejsciowy data, List<SolverPriority> kolej, IProgress<double>? progress = null, CancellationToken ct = default, SolverOptions? opt = null)
        {
            _in = data; _prio = kolej; _progress = progress; _ct = ct; _opt = opt ?? new SolverOptions();

            _docsMap = _in.Lekarze.FindAll(l => l.IsAktywny);
            _days = _in.DniWMiesiacu.Count; _docs = _docsMap.Count;

            _av = new int[_days, _docs]; _limit = new int[_docs]; _nextOther = new bool[_days, _docs];

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
                        _nextOther[d, p] = true;
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

            _caps = new long[_prio.Count + 4];
            int i = 0;

            var prioPart = _prio.Select(p => (p == SolverPriority.SprawiedliwoscObciazenia || p == SolverPriority.RownomiernoscRozlozenia) ? 0L : _days).ToArray();
            Array.Copy(prioPart, _caps, prioPart.Length);
            i += prioPart.Length;

            _caps[i++] = long.MaxValue; // Rezerwacje nie mają limitu
            _caps[i++] = _totBC;
            _caps[i++] = _totCh;
            _caps[i++] = _totMg;

            InitZobrist();
        }

        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            int[] wlSeed;
            int[] seed = GreedySeed.Generate(_in, out wlSeed);
            if (_opt.UseLocalSearch) LocalSearch.Improve(seed, _in);

            if (!CheckSeedFeasible(seed))
                seed = BuildGreedyFeasibleSeed();

            var bestMetrics = ToMetrics(seed);
            long[] bestVec = EvaluationAndScoringService.ToIntVector(bestMetrics, _prio);
            int[] bestAssign = (int[])seed.Clone();

            var start = NewRootNode();
            var budgetAt = DateTime.UtcNow.AddSeconds(_opt.TimeBudgetSeconds);

            double startW = _opt.UseARAStar ? _opt.EpsilonSchedule[0] : (_opt.WeightedAStarW > 1.0 ? _opt.WeightedAStarW : 1.0);
            var epsSchedule = _opt.UseARAStar ? _opt.EpsilonSchedule : new[] { startW };

            long steps = 0;

            foreach (var eps in epsSchedule)
            {
                var open = new PriorityQueue<Node, (long, long, long, long, long, long, long, long, long)>();
                var closed = new Dictionary<(int, long), long[]>(1 << 18);
                Enqueue(open, start, eps);

                while (open.Count > 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    if (DateTime.UtcNow > budgetAt) goto RETURN;

                    var cur = open.Dequeue();

                    var curF = AddWithCaps(cur.G, Heur(cur, eps));
                    var key = (cur.Day, cur.Hash);
                    if (closed.TryGetValue(key, out var bestF) && Less(bestF, curF)) continue;
                    closed[key] = curF;

                    steps++;
                    if ((_opt.ProgressReportModulo > 0) && ((steps & _opt.ProgressReportModulo) == 0))
                        _progress?.Report((double)(cur.Day + 1) / Math.Max(1, _days));

                    if ((steps % CLOSURE_EVERY) == 0)
                    {
                        var (cand, vec) = GreedyClosure(cur);
                        if (Less(vec, bestVec)) { bestVec = vec; bestAssign = cand; }
                    }

                    if (cur.Day == _days - 1)
                    {
                        var a = Reconstruct(cur);
                        var m = ToMetrics(a);
                        var v = EvaluationAndScoringService.ToIntVector(m, _prio);
                        if (Less(v, bestVec)) { bestVec = v; bestAssign = a; }
                        continue;
                    }

                    int nextDay = cur.Day + 1;

                    var baseList = _staticCands[nextDay];
                    var legal = new List<int>(baseList.Count);
                    for (int i = 0; i < baseList.Count; i++)
                    {
                        int p = baseList[i];
                        if (IsValidDynamic(nextDay, p, cur)) legal.Add(p);
                    }

                    legal.Sort((a, b) => CompareDelta(cur, nextDay, a, b));

                    bool anyEnqueued = false;
                    for (int k = 0; k < legal.Count; k++)
                    {
                        int p = legal[k];
                        var nxt = Make(cur, nextDay, p);
                        var fVec = AddWithCaps(nxt.G, Heur(nxt, eps));
                        if (nextDay >= BOOTSTRAP_DEPTH)
                        {
                            if (!Less(fVec, bestVec) && !Equal(fVec, bestVec)) continue;
                        }
                        Enqueue(open, nxt, eps, fVec);
                        anyEnqueued = true;
                    }

                    if (legal.Count == 0)
                    {
                        var nxt = Make(cur, nextDay, -1);
                        var fVec = AddWithCaps(nxt.G, Heur(nxt, eps));
                        if (nextDay < BOOTSTRAP_DEPTH || Less(fVec, bestVec) || Equal(fVec, bestVec))
                        {
                            Enqueue(open, nxt, eps, fVec);
                            anyEnqueued = true;
                        }
                    }

                    if (!anyEnqueued && legal.Count > 0)
                    {
                        int p = legal[0];
                        var nxt = Make(cur, nextDay, p);
                        var fVec = AddWithCaps(nxt.G, Heur(nxt, eps));
                        Enqueue(open, nxt, eps, fVec);
                    }
                }
            }

        RETURN:
            return ToMetrics(bestAssign);
        }

        private void InitZobrist()
        {
            if (_zInit && _z.GetLength(0) >= _days && _z.GetLength(1) >= _docs) return;
            _z = new long[_days, _docs]; _zEmpty = new long[_days];
            var rnd = new Random(12345);
            for (int d = 0; d < _days; d++) { _zEmpty[d] = rnd.NextInt64(); for (int p = 0; p < _docs; p++) _z[d, p] = rnd.NextInt64(); }
            _zInit = true;
        }

        private Node NewRootNode()
        {
            return new Node
            {
                Parent = -1,
                Day = -1,
                LastDoc = -2,
                Hash = 0,
                G = new long[_prio.Count + 4],
                Work = new int[_docs],
                UsedCond = new bool[_docs],
                BC = 0,
                CH = 0,
                MG = 0,
                Pref = 0,
                Obs = 0
            };
        }

        private void Enqueue(PriorityQueue<Node, (long, long, long, long, long, long, long, long, long)> pq, Node n, double eps, long[]? fOverrideVec = null)
        {
            var F = fOverrideVec ?? AddWithCaps(n.G, Heur(n, eps));
            var key = ToKeyForMinHeap(F, n);
            pq.Enqueue(n, key);
        }

        private (long, long, long, long, long, long, long, long, long) ToKeyForMinHeap(long[] F, Node n)
        {
            long tieA = -n.Pref;
            long tieB = (n.LastDoc == -1 ? 1L : 0L);
            long tie = (tieA << 32) ^ (tieB & 0xFFFFFFFFL);

            var fValues = new long[8];
            Array.Copy(F, fValues, F.Length);

            return (-fValues[0], -fValues[1], -fValues[2], -fValues[3], -fValues[4], -fValues[5], -fValues[6], -fValues[7], tie);
        }

        private long[] Heur(Node n, double eps)
        {
            int next = n.Day + 1;
            int rem = _days - (n.Day + 1);
            if (rem <= 0) return new long[_prio.Count + 4];

            int remCap = 0; for (int p = 0; p < _docs; p++) remCap += Math.Max(0, _limit[p] - n.Work[p]);
            int ubObs = Math.Min(rem, remCap);

            if ((next % FLOW_EVERY_DEPTH) == 0)
            {
                int matchUB = MatchingUB(next, n.Work);
                if (matchUB < ubObs) ubObs = matchUB;
            }

            int ubPref = (n.Pref == next ? rem : 0);

            var h = new long[_prio.Count + 4];
            int i = 0;

            var prioHeuristics = new Dictionary<SolverPriority, int>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, ubObs },
                { SolverPriority.CiagloscPoczatkowa, ubPref },
                { SolverPriority.SprawiedliwoscObciazenia, 0 },
                { SolverPriority.RownomiernoscRozlozenia, 0 }
            };

            foreach (var p in _prio) h[i++] = prioHeuristics[p];

            h[i++] = 0; // Rezerwacje
            h[i++] = _sufBC[next];
            h[i++] = _sufCh[next];
            h[i++] = _sufMg[next];

            if (eps != 1.0) for (int k = 0; k < h.Length; k++) h[k] = (long)Math.Ceiling(h[k] * eps);
            for (int k = 0; k < h.Length; k++) if (_caps[k] > 0 && h[k] > _caps[k]) h[k] = _caps[k];
            return h;
        }

        private long[] AddWithCaps(long[] g, long[] h)
        {
            var r = new long[g.Length];
            for (int i = 0; i < g.Length; i++)
            {
                long s = g[i] + h[i];
                long c = _caps[i];
                r[i] = (c > 0 && s > c) ? c : s;
            }
            return r;
        }

        private static bool Less(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++) { if (a[i] == b[i]) continue; return a[i] > b[i]; }
            return false;
        }
        private static bool Equal(long[] a, long[] b) { for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }

        private Node Make(Node cur, int day, int doc)
        {
            var n = new Node
            {
                Parent = _pool.Add(cur),
                Day = day,
                LastDoc = doc,
                Hash = cur.Hash,
                G = (long[])cur.G.Clone(),
                Work = (int[])cur.Work.Clone(),
                UsedCond = (bool[])cur.UsedCond.Clone(),
                BC = cur.BC,
                CH = cur.CH,
                MG = cur.MG,
                Pref = cur.Pref,
                Obs = cur.Obs
            };
            if (doc != -1)
            {
                n.Obs++; n.Work[doc]++;
                var av = (TypDostepnosci)_av[day, doc];
                if (av == TypDostepnosci.BardzoChce) n.BC++;
                else if (av == TypDostepnosci.Chce) n.CH++;
                else n.MG++;
                if (av == TypDostepnosci.MogeWarunkowo && !n.UsedCond[doc]) n.UsedCond[doc] = true;
                n.Hash ^= _z[day, doc];
            }
            else n.Hash ^= _zEmpty[day];

            if (n.Pref == day) n.Pref = (doc == -1) ? n.Pref : n.Pref + 1;

            int i = 0;
            var currentMetrics = new Dictionary<SolverPriority, int>
            {
                { SolverPriority.LacznaLiczbaObsadzonychDni, n.Obs },
                { SolverPriority.CiagloscPoczatkowa, n.Pref },
                { SolverPriority.SprawiedliwoscObciazenia, 0 },
                { SolverPriority.RownomiernoscRozlozenia, 0 }
            };

            foreach (var p in _prio) n.G[i++] = currentMetrics[p];

            n.G[i++] = 0; // Rezerwacje
            n.G[i++] = n.BC;
            n.G[i++] = n.CH;
            n.G[i++] = n.MG;
            return n;
        }

        private bool IsValidDynamic(int day, int p, Node n)
        {
            if (p < 0) return true;
            if (n.Work[p] >= _limit[p]) return false;
            var av = (TypDostepnosci)_av[day, p];
            bool bc = av == TypDostepnosci.BardzoChce;

            if (!bc && day > 0 && AssignedAtDay(n, day - 1) == p) return false;
            if (!bc && _nextOther[day, p]) return false;
            if (av == TypDostepnosci.MogeWarunkowo && n.UsedCond[p]) return false;
            return true;
        }

        private int AssignedAtDay(Node n, int day)
        {
            while (n.Parent >= 0)
            {
                if (n.Day == day) return n.LastDoc;
                n = _pool.Get(n.Parent);
            }
            return -3;
        }

        private int CompareDelta(Node cur, int day, int pa, int pb)
        {
            if (pa == pb) return 0;
            var da = BuildDelta(cur, day, pa);
            var db = BuildDelta(cur, day, pb);
            for (int i = 0; i < da.Length; i++)
            {
                if (da[i] == db[i]) continue;
                return da[i] > db[i] ? -1 : 1;
            }
            int wla = cur.Work[pa], wlb = cur.Work[pb];
            if (wla != wlb) return wla < wlb ? -1 : 1;
            return pa.CompareTo(pb);
        }

        private long[] BuildDelta(Node cur, int day, int p)
        {
            var res = new long[_prio.Count + 4];
            int k = 0;

            var deltaMetrics = new Dictionary<SolverPriority, long>();
            deltaMetrics[SolverPriority.LacznaLiczbaObsadzonychDni] = 1;
            deltaMetrics[SolverPriority.CiagloscPoczatkowa] = (cur.Pref == day ? 1 : 0);

            int max = 0, min = int.MaxValue;
            for (int d = 0; d < _docs; d++) { int w = cur.Work[d]; if (w > max) max = w; if (w < min) min = w; }
            int nb = cur.Work[p] + 1;
            int nmax = Math.Max(max, nb);
            int nmin = Math.Min(min, (p == IndexOfMin(cur.Work, min) ? nb : min));
            deltaMetrics[SolverPriority.SprawiedliwoscObciazenia] = (max - min) - (nmax - nmin);

            int last = LastDayForDoc(cur, p);
            deltaMetrics[SolverPriority.RownomiernoscRozlozenia] = (last < 0) ? 100 : (day - last - 1);

            foreach (var pr in _prio)
            {
                res[k++] = deltaMetrics[pr];
            }

            var av = (TypDostepnosci)_av[day, p];
            res[k++] = 0; // Rezerwacje
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

        private int LastDayForDoc(Node n, int doc)
        {
            if (n.Parent < 0) return -1;
            while (n.Parent >= 0)
            {
                if (n.LastDoc == doc) return n.Day;
                n = _pool.Get(n.Parent);
            }
            return -1;
        }

        private int[] Reconstruct(Node leaf)
        {
            var res = new int[_days]; Array.Fill(res, -1);
            var n = leaf;
            while (n.Parent >= 0) { res[n.Day] = n.LastDoc; n = _pool.Get(n.Parent); }
            return res;
        }

        private RozwiazanyGrafik ToMetrics(int[] a)
        {
            var map = new Dictionary<DateTime, Lekarz?>(a.Length);
            var ob = new Dictionary<string, int>(_docs);
            for (int p = 0; p < _docs; p++) ob[_docsMap[p].Symbol] = 0;
            for (int d = 0; d < _days; d++)
            {
                var day = _in.DniWMiesiacu[d];
                int p = a[d];
                if (p >= 0) { var L = _docsMap[p]; map[day] = L; ob[L.Symbol]++; } else map[day] = null;
            }
            return EvaluationAndScoringService.CalculateMetrics(map, ob, _in);
        }

        private static int[] BuildSuffix(bool[] any) { int n = any.Length; var s = new int[n + 1]; for (int i = n - 1; i >= 0; i--) s[i] = s[i + 1] + (any[i] ? 1 : 0); return s; }
        private static int Count(bool[] a) { int c = 0; for (int i = 0; i < a.Length; i++) if (a[i]) c++; return c; }

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
                if (av != TypDostepnosci.BardzoChce && _nextOther[d, p]) return false;
                if (av == TypDostepnosci.MogeWarunkowo)
                {
                    if (usedCond[p]) return false;
                    usedCond[p] = true;
                }
            }
            return true;
        }

        private int[] BuildGreedyFeasibleSeed()
        {
            var assign = new int[_days]; Array.Fill(assign, -1);
            var work = new int[_docs];
            var used = new bool[_docs];

            for (int d = 0; d < _days; d++)
            {
                int best = -1;
                long[] bestDelta = null!;

                var lst = _staticCands[d];
                for (int i = 0; i < lst.Count; i++)
                {
                    int p = lst[i];
                    if (work[p] >= _limit[p]) continue;
                    var av = (TypDostepnosci)_av[d, p];
                    bool bc = av == TypDostepnosci.BardzoChce;
                    if (!bc && (d > 0 && assign[d - 1] == p)) continue;
                    if (!bc && _nextOther[d, p]) continue;
                    if (av == TypDostepnosci.MogeWarunkowo && used[p]) continue;

                    var fake = new Node { Parent = -1, Work = work, UsedCond = used, Pref = LongestPrefix(assign, d, p) };
                    var delta = BuildDelta(fake, d, p);

                    if (best == -1 || LexGreater(delta, bestDelta))
                    {
                        best = p; bestDelta = delta;
                    }
                }
                if (best != -1)
                {
                    assign[d] = best;
                    work[best]++;
                    var av = (TypDostepnosci)_av[d, best];
                    if (av == TypDostepnosci.MogeWarunkowo && !used[best]) used[best] = true;
                }
            }
            return assign;
        }

        private (int[] assign, long[] vec) GreedyClosure(Node baseNode)
        {
            var assign = Reconstruct(baseNode);
            var work = (int[])baseNode.Work.Clone();
            var used = (bool[])baseNode.UsedCond.Clone();

            for (int d = baseNode.Day + 1; d < _days; d++)
            {
                int best = -1;
                long[] bestDelta = null!;

                var lst = _staticCands[d];
                for (int i = 0; i < lst.Count; i++)
                {
                    int p = lst[i];
                    if (work[p] >= _limit[p]) continue;
                    var av = (TypDostepnosci)_av[d, p];
                    bool bc = av == TypDostepnosci.BardzoChce;
                    if (!bc && (d > 0 && assign[d - 1] == p)) continue;
                    if (!bc && _nextOther[d, p]) continue;
                    if (av == TypDostepnosci.MogeWarunkowo && used[p]) continue;

                    var fake = new Node { Parent = -1, Work = work, UsedCond = used, Pref = LongestPrefix(assign, d, p) };
                    var delta = BuildDelta(fake, d, p);

                    if (best == -1 || LexGreater(delta, bestDelta))
                    {
                        best = p; bestDelta = delta;
                    }
                }
                if (best != -1)
                {
                    assign[d] = best;
                    work[best]++;
                    var av = (TypDostepnosci)_av[d, best];
                    if (av == TypDostepnosci.MogeWarunkowo && !used[best]) used[best] = true;
                }
                else assign[d] = -1;
            }

            var m = ToMetrics(assign);
            return (assign, EvaluationAndScoringService.ToIntVector(m, _prio));
        }

        private static bool LexGreater(long[] a, long[] b)
        {
            if (b == null) return true;
            for (int i = 0; i < a.Length; i++) { if (a[i] == b[i]) continue; return a[i] > b[i]; }
            return false;
        }

        private static int LongestPrefix(int[] assign, int day, int candidate)
        {
            int k = 0;
            while (k < day)
            {
                int a = assign[k];
                if (a < 0) break;
                k++;
            }
            if (k == day && candidate != -1) k++;
            return k;
        }

        private int MatchingUB(int nextDay, int[] workNow)
        {
            long h = 1469598103934665603L;
            for (int p = 0; p < _docs; p++)
            {
                int cap = Math.Max(0, _limit[p] - workNow[p]);
                unchecked { h ^= (uint)cap; h *= 1099511628211L; }
            }
            var key = (nextDay, h);
            if (_flowCache.TryGetValue(key, out var cached)) return cached;

            var rightIndex = new List<(int doc, int slot)>();
            var docStart = new int[_docs];
            int R = 0;
            for (int p = 0; p < _docs; p++)
            {
                docStart[p] = R;
                int cap = Math.Max(0, _limit[p] - workNow[p]);
                for (int s = 0; s < cap; s++) { rightIndex.Add((p, s)); R++; }
            }
            if (R == 0) { _flowCache[key] = 0; return 0; }

            int L = _days - nextDay;
            var adj = new List<int>[L];
            for (int li = 0; li < L; li++)
            {
                int day = nextDay + li;
                var edges = new List<int>();
                for (int p = 0; p < _docs; p++)
                {
                    var av = (TypDostepnosci)_av[day, p];
                    if (av is TypDostepnosci.Niedostepny or TypDostepnosci.Urlop or TypDostepnosci.DyzurInny) continue;
                    int cap = Math.Max(0, _limit[p] - workNow[p]);
                    for (int s = 0; s < cap; s++) edges.Add(docStart[p] + s);
                }
                adj[li] = edges;
            }

            int match = HopcroftKarp(L, R, adj);
            _flowCache[key] = match;
            return match;
        }

        private static int HopcroftKarp(int L, int R, List<int>[] adj)
        {
            var pairU = new int[L]; Array.Fill(pairU, -1);
            var pairV = new int[R]; Array.Fill(pairV, -1);
            var dist = new int[L];

            bool BFS()
            {
                var q = new Queue<int>();
                for (int u = 0; u < L; u++)
                {
                    if (pairU[u] == -1) { dist[u] = 0; q.Enqueue(u); }
                    else dist[u] = int.MaxValue;
                }
                bool reachableFree = false;
                while (q.Count > 0)
                {
                    int u = q.Dequeue();
                    foreach (var v in adj[u])
                    {
                        int u2 = pairV[v];
                        if (u2 != -1 && dist[u2] == int.MaxValue)
                        {
                            dist[u2] = dist[u] + 1;
                            q.Enqueue(u2);
                        }
                        if (u2 == -1) reachableFree = true;
                    }
                }
                return reachableFree;
            }

            bool DFS(int u)
            {
                foreach (var v in adj[u])
                {
                    int u2 = pairV[v];
                    if (u2 == -1 || (dist[u2] == dist[u] + 1 && DFS(u2)))
                    {
                        pairU[u] = v; pairV[v] = u; return true;
                    }
                }
                dist[u] = int.MaxValue;
                return false;
            }

            int result = 0;
            while (BFS())
                for (int u = 0; u < L; u++)
                    if (pairU[u] == -1 && DFS(u)) result++;
            return result;
        }

        private sealed class NodePool
        {
            private readonly List<Node> _buf = new(1 << 20);
            public int Add(Node n) { _buf.Add(n); return _buf.Count - 1; }
            public Node Get(int i) => _buf[i];
        }
    }
}