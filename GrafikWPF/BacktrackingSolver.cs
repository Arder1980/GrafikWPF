using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GrafikWPF.Algorithms;

namespace GrafikWPF
{
    public enum StrictMode { Off, Half, Full }

    /// <summary>
    /// BacktrackingSolver – deterministyczny.
    /// F1: maksymalizacja prefiksu (ciągłość od początku).
    /// F2: dogęszczanie (heurystycznie) LUB (w trybie STRICT) branch-and-bound z gwarancją jakości.
    ///
    /// Integracja ze SchedulingRules:
    ///  - IsHardFeasible(...) – twarde reguły,
    ///  - OrderCandidates(...) – porządkowanie kandydatów,
    ///  - EvaluateSolution(...) – wspólny wektor jakości.
    /// </summary>
    public sealed class BacktrackingSolver : IGrafikSolver
    {
        // ======= PRZEŁĄCZNIK TRYBU ŚCISŁOŚCI =======
        private const StrictMode STRICT_MODE = StrictMode.Full;  // Off | Half | Full

        // Ograniczenia bezpieczeństwa dla STRICT (żeby nie "wyparować" czasu przypadkiem)
        private const int STRICT_MAX_PREFIXES = 10000;          // max liczba najlepszych prefiksów do rozpatrzenia
        private const int STRICT_NODE_LIMIT = 500_000;        // twardy limit węzłów B&B (Half/Full)
        // (Jeśli osiągnięty, solver kończy z najlepszym znalezionym dotąd wynikiem i loguje ostrzeżenie; formalna gwarancja wtedy OFF)

        // ======= Kody preferencji (mapowane z TypDostepnosci) =======
        private const byte PREF_NONE = 0;
        private const byte PREF_MW = 1; // Mogę warunkowo (max 1/mies.)
        private const byte PREF_MG = 2; // Mogę
        private const byte PREF_CH = 3; // Chcę
        private const byte PREF_BC = 4; // Bardzo chcę (może łamać sąsiedztwo)
        private const byte PREF_RZ = 5; // Rezerwacja (wycięta w wrapperze)
        private const byte PREF_OD = 6; // DyzurInny (blokuje ±1 dla nie-BC)

        private const int UNASSIGNED = int.MinValue;
        private const int EMPTY = -1;

        private readonly GrafikWejsciowy _input;
        private readonly IReadOnlyList<SolverPriority> _priorities;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _token;

        private readonly List<DateTime> _days;
        private readonly List<Lekarz> _docs;
        private readonly Dictionary<string, int> _docIdxBySymbol;
        private readonly Dictionary<DateTime, Dictionary<string, TypDostepnosci>> _av;
        private readonly int[] _limitsByDoc;
        private readonly byte[,] _pref; // [day, doc] -> PREF_*

        // Stan przypisań
        private readonly int[] _assign;     // day -> doc idx; EMPTY/UNASSIGNED
        private readonly int[] _workPerDoc; // globalne obciążenia
        private readonly int[] _mwUsed;
        private int _filled;

        // Najlepszy wynik / scoring
        private RozwiazanyGrafik? _best;
        private long[]? _bestScore;

        // F1 – prefiks
        private int _bestPrefixLen;
        private int[]? _bestPrefixAssign;

        // STRICT: kolekcja wszystkich najlepszych prefiksów (#1)
        private List<int[]>? _strictPrefixes;

        // UB/okna dla F1 i B&B
        private const bool F1_USE_FULL_UB = true;
        private const int F1_FULL_UB_EVERY = 8;
        private const int F1_WIN_SIZE = 10;
        private const int F1_WIN_UB_EVERY = 1;

        private readonly Dictionary<(int day, ulong capHash), int> _ubCache = new Dictionary<(int, ulong), int>(1024);
        private readonly Dictionary<(int start, int end, ulong capHash), int> _winUbCache = new Dictionary<(int, int, ulong), int>(2048);

        // Buckety kandydatów
        private List<int>[] _buckBC = Array.Empty<List<int>>();
        private List<int>[] _buckCH = Array.Empty<List<int>>();
        private List<int>[] _buckMG = Array.Empty<List<int>>();
        private List<int>[] _buckMW = Array.Empty<List<int>>();
        private bool _bucketsReady;

        // Throttling logów
        private const int LOG_EVERY_PRUNE_D1 = 100; // 0 = loguj każdy
        private const int LOG_EVERY_WINUB = 10;  // 0 = loguj każdy

        // Bufor F2 (mniej alokacji)
        private readonly List<int> _tmp = new List<int>(64);

        // (historyczne flagi – trzymamy dla kompatybilności)
        private readonly bool _bcBreaksAdjacent = true;
        private readonly int _mwMax = 1;

        public BacktrackingSolver(GrafikWejsciowy input,
                                  IReadOnlyList<SolverPriority> priorities,
                                  IProgress<double>? progress,
                                  CancellationToken token)
        {
            _input = input;
            _priorities = priorities;
            _progress = progress;
            _token = token;

            _days = _input.Dostepnosc.Keys.OrderBy(d => d).ToList();
            _docs = _input.Lekarze.OrderBy(l => l.Nazwisko).ThenBy(l => l.Imie).ThenBy(l => l.Symbol).ToList();

            _docIdxBySymbol = new Dictionary<string, int>(_docs.Count);
            for (int i = 0; i < _docs.Count; i++) _docIdxBySymbol[_docs[i].Symbol] = i;

            _av = _input.Dostepnosc;

            _limitsByDoc = new int[_docs.Count];
            for (int i = 0; i < _docs.Count; i++)
            {
                var sym = _docs[i].Symbol;
                _limitsByDoc[i] = _input.LimityDyzurow.TryGetValue(sym, out var lim) ? lim : _days.Count;
            }

            _pref = new byte[_days.Count, _docs.Count];
            PrecomputeAvailability();

            _assign = Enumerable.Repeat(UNASSIGNED, _days.Count).ToArray();
            _workPerDoc = new int[_docs.Count];
            _mwUsed = new int[_docs.Count];
            _filled = 0;

            _bestPrefixLen = 0;
        }

        // ===================== API =====================
        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            SolverDiagnostics.Start("BacktrackingSolver.Det+Strict");

            // F1 – ciągłość od początku (sekwencyjnie) + najlepszy prefiks
            F1_MaximizePrefix();

            // STRICT: zbierz wszystkie najlepsze prefiksy o długości _bestPrefixLen
            if (STRICT_MODE != StrictMode.Off)
            {
                _strictPrefixes = new List<int[]>(Math.Min(256, STRICT_MAX_PREFIXES));
                CollectAllBestPrefixes();
            }

            RozwiazanyGrafik sol;

            if (STRICT_MODE == StrictMode.Off)
            {
                // Heurystyczne F2
                if (_bestPrefixAssign != null)
                {
                    ApplyPrefixSnapshot(_bestPrefixAssign, _bestPrefixLen);
                    F2_MaximizeCoverageFrom(_bestPrefixLen);
                }
                else
                {
                    ApplyPrefixSnapshot(Array.Empty<int>(), 0);
                    F2_MaximizeCoverageFrom(0);
                }

                sol = BuildSolution();
                var score = EvaluateSolution(sol);
                if (_best is null || Better(score, _bestScore!))
                {
                    _best = sol; _bestScore = score;
                    SolverDiagnostics.Log($"[BEST] vec={FormatScore(score)}");
                }
            }
            else
            {
                // STRICT B&B (Half/Full)
                StrictRun();
                sol = BuildSolution(); // po StrictRun() w _assign mamy najlepszy znaleziony
            }

            SolverDiagnostics.Stop();
            return _best ?? sol;
        }

        // ===================== Prekomputacja =====================
        private void PrecomputeAvailability()
        {
            for (int d = 0; d < _days.Count; d++)
            {
                var date = _days[d];
                for (int j = 0; j < _docs.Count; j++)
                {
                    var sym = _docs[j].Symbol;
                    TypDostepnosci td = TypDostepnosci.Niedostepny;
                    Dictionary<string, TypDostepnosci> map;
                    if (_av.TryGetValue(date, out map) && map.TryGetValue(sym, out td)) { }
                    byte code;
                    switch (td)
                    {
                        case TypDostepnosci.MogeWarunkowo: code = PREF_MW; break;
                        case TypDostepnosci.Moge: code = PREF_MG; break;
                        case TypDostepnosci.Chce: code = PREF_CH; break;
                        case TypDostepnosci.BardzoChce: code = PREF_BC; break;
                        case TypDostepnosci.Rezerwacja: code = PREF_RZ; break;
                        case TypDostepnosci.DyzurInny: code = PREF_OD; break;
                        default: code = PREF_NONE; break;
                    }
                    _pref[d, j] = code;
                }
            }
        }

        // ===================== F1: Maksymalizacja prefiksu (SEKWENCYJNIE) =====================
        private void F1_MaximizePrefix()
        {
            var curA = new int[_days.Count]; Array.Fill(curA, UNASSIGNED);
            var curW = new int[_docs.Count];
            var curM = new int[_docs.Count];

            EnsureBucketsBuilt();

            int cntPrune = 0, cntWin = 0;

            void DFS(int day)
            {
                _token.ThrowIfCancellationRequested();
                if (_progress != null) _progress.Report(day / (double)_days.Count);

                if (day > _bestPrefixLen)
                {
                    _bestPrefixLen = day;
                    _bestPrefixAssign = new int[_bestPrefixLen];
                    Array.Copy(curA, _bestPrefixAssign, _bestPrefixLen);
                    SolverDiagnostics.Log($"[F1] Nowy najlepszy prefiks: {_bestPrefixLen} ({FormatDay(_bestPrefixLen - 1)})");
                }
                if (day >= _days.Count) return;

                // Okienkowe UB
                if (F1_WIN_UB_EVERY > 0 && (day % F1_WIN_UB_EVERY == 0))
                {
                    int W = Math.Min(F1_WIN_SIZE, _days.Count - day);
                    int end = day + W - 1;
                    int wub = F1_WindowUpperBoundCached(day, end, curW);
                    if (_bestPrefixLen >= day + wub)
                    {
                        if (LOG_EVERY_WINUB == 0 || (++cntWin % LOG_EVERY_WINUB == 0))
                            SolverDiagnostics.Log($"[F1][WIN-UB-cut] {FormatDay(day)}..{FormatDay(Math.Max(day, end))}: wub={wub}, bestPref={_bestPrefixLen} → stop");
                        return;
                    }
                }

                // Pełny UB
                if (F1_USE_FULL_UB && (day % F1_FULL_UB_EVERY == 0))
                {
                    int ub = F1_SuffixUpperBoundCached(day, curW);
                    if (day + ub <= _bestPrefixLen)
                    {
                        SolverDiagnostics.Log($"[F1][FULL-UB-cut] od {FormatDay(day)}: ub={ub}, bestPref={_bestPrefixLen} → stop");
                        return;
                    }
                }

                var cands = F1_OrderCandidates(day, curA, curW, curM);
                if (cands.Count == 0) return;

                foreach (var doc in cands)
                {
                    byte code = _pref[day, doc];

                    if (!F1_IsFeasible(day, doc, curA, curW, curM)) continue;

                    // mikrosito d+1 – ślepe zaułki odcinamy twardo
                    if (!KeepsNextFeasible(day, doc, curA, curW, curM))
                    {
                        if (LOG_EVERY_PRUNE_D1 == 0 || (++cntPrune % LOG_EVERY_PRUNE_D1 == 0))
                            SolverDiagnostics.Log($"[F1] Prune d+1: {FormatDay(day)} ← {_docs[doc].Symbol}");
                        continue;
                    }

                    // pick
                    curA[day] = doc;
                    curW[doc]++;
                    if (code == PREF_MW) curM[doc]++;

                    DFS(day + 1);

                    // backtrack
                    if (code == PREF_MW) curM[doc]--;
                    curW[doc]--;
                    curA[day] = UNASSIGNED;
                }
            }

            DFS(0);
        }

        // STRICT: zbieranie wszystkich najlepszych prefiksów (#1)
        private void CollectAllBestPrefixes()
        {
            if (_bestPrefixLen == 0) { _strictPrefixes!.Add(new int[0]); return; }

            var curA = new int[_days.Count]; Array.Fill(curA, UNASSIGNED);
            var curW = new int[_docs.Count];
            var curM = new int[_docs.Count];
            int collected = 0;

            void DFS(int day)
            {
                _token.ThrowIfCancellationRequested();
                if (day == _bestPrefixLen)
                {
                    if (collected < STRICT_MAX_PREFIXES)
                    {
                        var snap = new int[_bestPrefixLen];
                        Array.Copy(curA, snap, _bestPrefixLen);
                        _strictPrefixes!.Add(snap);
                        collected++;
                    }
                    else
                    {
                        SolverDiagnostics.Log($"[STRICT] Osiągnięto limit prefiksów ({STRICT_MAX_PREFIXES}). Gwarancja pełna może nie być udowodniona.");
                        return;
                    }
                    return;
                }

                // UB: jeśli nie da się dobić do _bestPrefixLen – tnij
                int ub = F1_SuffixUpperBoundCached(day, curW);
                if (day + ub < _bestPrefixLen) return;

                var cands = F1_OrderCandidates(day, curA, curW, curM);
                foreach (var doc in cands)
                {
                    if (!F1_IsFeasible(day, doc, curA, curW, curM)) continue;
                    if (!KeepsNextFeasible(day, doc, curA, curW, curM)) continue;

                    byte code = _pref[day, doc];
                    curA[day] = doc; curW[doc]++; if (code == PREF_MW) curM[doc]++;
                    DFS(day + 1);
                    if (code == PREF_MW) curM[doc]--; curW[doc]--; curA[day] = UNASSIGNED;

                    if (_strictPrefixes!.Count >= STRICT_MAX_PREFIXES) return;
                }
            }

            DFS(0);

            SolverDiagnostics.Log($"[STRICT] Zebrano prefiksów o dł. {_bestPrefixLen}: {_strictPrefixes!.Count}");
        }

        // ===================== F1: pomocnicze =====================
        private List<int> F1_OrderCandidates(int day, int[] curAssign, int[] curWork, int[] curMW)
        {
            // Kontekst na bazie LOKALNEGO stanu F1
            var ctx = BuildCtx(true, curAssign, curWork, curMW);

            // 1) Globalny, deterministyczny porządek wg wspólnych reguł
            var orderedAll = SchedulingRules.OrderCandidates(day, ctx);

            // 2) Rezerwacje – jeśli wykonalne, tylko je
            var rz = new List<int>();
            for (int i = 0; i < orderedAll.Count; i++)
            {
                int p = orderedAll[i];
                if (_pref[day, p] == PREF_RZ && SchedulingRules.IsHardFeasible(day, p, ctx))
                    rz.Add(p);
            }
            if (rz.Count > 0) return rz;

            // 3) Szybkie sito bucketowe (BC→CH→MG→MW) + limity + twarda wykonalność
            var allowed = new HashSet<int>();
            Action<List<int>, byte> addFromBucket = (bucket, code) =>
            {
                if (bucket == null) return;
                for (int k = 0; k < bucket.Count; k++)
                {
                    int p = bucket[k];
                    if (_pref[day, p] != code) continue;
                    if (curWork[p] >= _limitsByDoc[p]) continue;
                    if (!SchedulingRules.IsHardFeasible(day, p, ctx)) continue;
                    allowed.Add(p);
                }
            };
            addFromBucket(_buckBC[day], PREF_BC);
            addFromBucket(_buckCH[day], PREF_CH);
            addFromBucket(_buckMG[day], PREF_MG);
            addFromBucket(_buckMW[day], PREF_MW);

            if (allowed.Count == 0) return new List<int>();

            // 4) KOLEJNOŚĆ: tylko ci, którzy przeszli sito, ale
            //    w kolejności ustalonej przez SchedulingRules.OrderCandidates(...)
            var result = new List<int>(allowed.Count);
            for (int i = 0; i < orderedAll.Count; i++)
            {
                int p = orderedAll[i];
                if (allowed.Contains(p)) result.Add(p);
            }
            return result;
        }

        private bool F1_IsFeasible(int day, int doc, int[] curAssign, int[] curWork, int[] curMW)
        {
            // Twarde reguły – w kontekście lokalnych tablic stanu
            var ctx = BuildCtx(true, curAssign, curWork, curMW);
            return SchedulingRules.IsHardFeasible(day, doc, ctx);
        }

        private bool KeepsNextFeasible(int day, int doc, int[] curAssign, int[] curWork, int[] curMW)
        {
            int dNext = day + 1;
            if (dNext >= _days.Count) return true;

            byte code = _pref[day, doc];
            curAssign[day] = doc;
            curWork[doc]++;
            if (code == PREF_MW) curMW[doc]++;

            bool ok = false;
            for (int q = 0; q < _docs.Count; q++)
            {
                if (F1_IsFeasible(dNext, q, curAssign, curWork, curMW)) { ok = true; break; }
            }

            if (code == PREF_MW) curMW[doc]--;
            curWork[doc]--;
            curAssign[day] = UNASSIGNED;
            return ok;
        }

        // ===================== F2: Heurystyczne (gdy STRICT=Off) =====================
        private void F2_MaximizeCoverageFrom(int startDay)
        {
            _filled = 0;
            Array.Fill(_workPerDoc, 0);
            Array.Fill(_mwUsed, 0);
            for (int d = 0; d < _days.Count; d++)
            {
                if (_assign[d] >= 0)
                {
                    _workPerDoc[_assign[d]]++;
                    if (_pref[d, _assign[d]] == PREF_MW) _mwUsed[_assign[d]]++;
                    _filled++;
                }
            }

            // Rezerwacje (gdyby jakieś przeszły)
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;
                _tmp.Clear();
                for (int p = 0; p < _docs.Count; p++)
                    if (_pref[d, p] == PREF_RZ && IsHardFeasibleGlobal(d, p)) _tmp.Add(p);
                if (_tmp.Count > 0)
                {
                    int sel = SelectByTieBreakF2(d, _tmp);
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] RZ: {FormatDay(d)} ← {_docs[sel].Symbol}");
                }
            }

            bool progress;
            do
            {
                progress = TryFillUnique(startDay, PREF_BC);
                progress |= TryFillUnique(startDay, PREF_CH);
                progress |= TryFillAny(startDay, PREF_BC);
                progress |= TryFillAny(startDay, PREF_CH);
                progress |= TryFillAny(startDay, PREF_MG);
                progress |= TryFillAny(startDay, PREF_MW);
            }
            while (progress);

            for (int d = startDay; d < _days.Count; d++)
                if (_assign[d] == UNASSIGNED) _assign[d] = EMPTY;
        }

        private bool TryFillUnique(int startDay, byte prefCode)
        {
            bool any = false;
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                _tmp.Clear();
                for (int p = 0; p < _docs.Count; p++)
                    if (_pref[d, p] == prefCode && IsHardFeasibleGlobal(d, p))
                        _tmp.Add(p);

                if (_tmp.Count == 1)
                {
                    int sel = _tmp[0];
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] unique-{PrefToString(prefCode)}: {FormatDay(d)} ← {_docs[sel].Symbol}");
                    any = true;
                }
            }
            return any;
        }

        private bool TryFillAny(int startDay, byte prefCode)
        {
            bool any = false;
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                _tmp.Clear();
                for (int p = 0; p < _docs.Count; p++)
                    if (_pref[d, p] == prefCode && IsHardFeasibleGlobal(d, p))
                        _tmp.Add(p);

                if (_tmp.Count > 0)
                {
                    int sel = SelectByTieBreakF2(d, _tmp);
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] {PrefToString(prefCode)}: {FormatDay(d)} ← {_docs[sel].Symbol}");
                    any = true;
                }
            }
            return any;
        }

        // F2: wybór kandydata w obrębie bieżącego bucketu według wspólnego porządku
        private int SelectByTieBreakF2(int day, List<int> candidates)
        {
            var ctx = BuildCtx(false);
            var orderedAll = SchedulingRules.OrderCandidates(day, ctx);
            var set = new HashSet<int>(candidates);
            for (int i = 0; i < orderedAll.Count; i++)
            {
                int p = orderedAll[i];
                if (set.Contains(p)) return p;
            }
            // Awaryjnie – stabilny wybór (nie powinno się zdarzyć)
            int best = candidates[0];
            for (int i = 1; i < candidates.Count; i++) if (candidates[i] < best) best = candidates[i];
            return best;
        }

        private bool IsHardFeasibleGlobal(int day, int doc)
        {
            var ctx = BuildCtx(false);
            return SchedulingRules.IsHardFeasible(day, doc, ctx);
        }

        private void PlaceGlobal(int day, int doc)
        {
            _assign[day] = doc;
            _workPerDoc[doc]++;
            if (_pref[day, doc] == PREF_MW) _mwUsed[doc]++;
            _filled++;
        }

        // ===================== STRICT: Branch & Bound =====================
        private int _bbNodes;
        private long[]? _incumbentVec;        // najlepszy wektor (lex) znaleziony w STRICT
        private int[]? _incumbentA;          // odpowiadające przypisania (snapshot)
        private bool _proofComplete = true;

        private void StrictRun()
        {
            // iteruj po wszystkich najlepszych prefiksach
            var prefixes = (_strictPrefixes != null && _strictPrefixes.Count > 0)
                           ? _strictPrefixes
                           : new List<int[]> { _bestPrefixAssign ?? Array.Empty<int>() };

            _incumbentVec = null;
            _incumbentA = null;

            foreach (var pref in prefixes)
            {
                // 1) Zastosuj prefiks do stanu globalnego
                ApplyPrefixSnapshot(pref, _bestPrefixLen);

                // 2) Incumbent z heurystycznego F2 (szybki, bardzo dobry start)
                var bakA = (int[])_assign.Clone();
                var bakW = (int[])_workPerDoc.Clone();
                var bakM = (int[])_mwUsed.Clone();
                int bakF = _filled;

                F2_MaximizeCoverageFrom(_bestPrefixLen);
                var sol0 = BuildSolution();
                var vec0 = EvaluateSolution(sol0);
                UpdateIncumbent(vec0, (int[])_assign.Clone());

                // Przywróć prefiks (wracamy do czystego stanu przed B&B)
                Array.Copy(bakA, _assign, _assign.Length);
                Array.Copy(bakW, _workPerDoc, _workPerDoc.Length);
                Array.Copy(bakM, _mwUsed, _mwUsed.Length);
                _filled = bakF;

                // 3) Uruchom B&B na reszcie dni
                _bbNodes = 0;
                StrictBB_From(_bestPrefixLen,
                              (int[])_assign.Clone(),
                              (int[])_workPerDoc.Clone(),
                              (int[])_mwUsed.Clone());
            }

            // Po wszystkich prefiksach – ustaw najlepsze znalezione przypisania do stanu globalnego
            if (_incumbentA != null)
            {
                ApplyFromAssignArray(_incumbentA);
                var sol = BuildSolution();
                var vec = EvaluateSolution(sol);
                _best = sol; _bestScore = vec;
                SolverDiagnostics.Log($"[STRICT] BEST vec={FormatScore(vec)} nodes={_bbNodes} proof={_proofComplete}");
            }
        }

        private void StrictBB_From(int day, int[] curA, int[] curW, int[] curM)
        {
            _token.ThrowIfCancellationRequested();
            if (_bbNodes++ > STRICT_NODE_LIMIT)
            {
                _proofComplete = false;
                return;
            }

            // Znajdź kolejny "otwarty" dzień
            int d = day;
            while (d < _days.Count && curA[d] != UNASSIGNED) d++;
            if (d >= _days.Count)
            {
                // Liść – oceń
                var vec = EvaluateVectorFromArrays(curA, curW, curM);
                UpdateIncumbent(vec, (int[])curA.Clone());
                return;
            }

            // Oblicz UB dla #2 (łączna obsada) od d do końca
            int assignedSoFar = CountAssignedUpTo(curA, d);              // pełne dni [0..d-1]
            int remUB = F1_SuffixUpperBoundCached(d, curW);              // górne ograniczenie obsady "od d"
            int potential2 = assignedSoFar + remUB + CountAssignedFrom(curA, d + 1);

            if (_incumbentVec != null)
            {
                // Jeżeli nie przebijemy #2 – tnij
                int idx2 = IndexOfPriority(SolverPriority.LacznaLiczbaObsadzonychDni);
                if (potential2 < _incumbentVec[idx2]) return;
            }

            // Kandydaci (wg wspólnych reguł), + opcja "pusty dzień"
            var ctx = BuildCtx(false, curA, curW, curM);
            var ordered = SchedulingRules.OrderCandidates(d, ctx);
            var opts = new List<int>(ordered.Count + 1);
            for (int i = 0; i < ordered.Count; i++)
            {
                int p = ordered[i];
                if (SchedulingRules.IsHardFeasible(d, p, ctx))
                    opts.Add(p);
            }
            // Dodaj "pusty" wybór – potrzebny dla optimum #2 w obecności ograniczeń globalnych
            const int EMPTY_SENTINEL = -999999;
            opts.Add(EMPTY_SENTINEL);

            // Heurystyka kolejności: najpierw realne przydziały, na końcu "pusty"
            foreach (var choice in opts)
            {
                if (choice == EMPTY_SENTINEL)
                {
                    // "pusty" dzień
                    curA[d] = EMPTY;
                    StrictBB_From(d + 1, curA, curW, curM);
                    curA[d] = UNASSIGNED;
                }
                else
                {
                    byte code = _pref[d, choice];
                    curA[d] = choice; curW[choice]++; if (code == PREF_MW) curM[choice]++;
                    StrictBB_From(d + 1, curA, curW, curM);
                    if (code == PREF_MW) curM[choice]--; curW[choice]--; curA[d] = UNASSIGNED;
                }
            }
        }

        private int CountAssignedUpTo(int[] a, int endExclusive)
        {
            int c = 0;
            for (int i = 0; i < endExclusive; i++) if (a[i] >= 0) c++;
            return c;
        }
        private int CountAssignedFrom(int[] a, int startInclusive)
        {
            int c = 0;
            for (int i = startInclusive; i < a.Length; i++) if (a[i] >= 0) c++;
            return c;
        }

        private void UpdateIncumbent(long[] vec, int[] assignSnap)
        {
            if (_incumbentVec == null || Better(vec, _incumbentVec))
            {
                // FULL: porównujemy pełny wektor (lex wg _priorities).
                // HALF: gwarantujemy #2 przez to samo porównanie (jeśli #2 równe, stabilne tie-breaki wybiorą deterministycznie).
                _incumbentVec = vec;
                _incumbentA = assignSnap;
                SolverDiagnostics.Log($"[STRICT][INCUMBENT] vec={FormatScore(vec)}");
            }
        }

        private void ApplyFromAssignArray(int[] A)
        {
            for (int d = 0; d < _days.Count; d++) _assign[d] = UNASSIGNED;
            Array.Fill(_workPerDoc, 0);
            Array.Fill(_mwUsed, 0);
            _filled = 0;

            for (int d = 0; d < _days.Count; d++)
            {
                int doc = A[d];
                if (doc >= 0)
                {
                    _assign[d] = doc;
                    _workPerDoc[doc]++;
                    if (_pref[d, doc] == PREF_MW) _mwUsed[doc]++;
                    _filled++;
                }
                else if (doc == EMPTY)
                {
                    _assign[d] = EMPTY;
                }
            }
        }

        private long[] EvaluateVectorFromArrays(int[] curA, int[] curW, int[] curM)
        {
            var ctx = BuildCtx(false, curA, curW, curM);

            var sol = new RozwiazanyGrafik { Przypisania = new Dictionary<DateTime, Lekarz?>() };
            for (int d = 0; d < _days.Count; d++)
                sol.Przypisania[_days[d]] = curA[d] >= 0 ? _docs[curA[d]] : null;

            return SchedulingRules.EvaluateSolution(sol, _priorities, ctx);
        }

        // ===================== Build/Eval/Utils =====================
        private RozwiazanyGrafik BuildSolution()
        {
            var sol = new RozwiazanyGrafik { Przypisania = new Dictionary<DateTime, Lekarz?>() };
            for (int d = 0; d < _days.Count; d++)
                sol.Przypisania[_days[d]] = _assign[d] >= 0 ? _docs[_assign[d]] : null;
            return sol;
        }

        private long[] EvaluateSolution(RozwiazanyGrafik sol)
        {
            var ctx = BuildCtx(false);
            return SchedulingRules.EvaluateSolution(sol, _priorities, ctx);
        }

        private SchedulingRules.Context BuildCtx(bool isPrefixActive, int[] assign = null, int[] work = null, int[] mw = null)
        {
            var limits = new Dictionary<string, int>(_docs.Count);
            for (int i = 0; i < _docs.Count; i++)
            {
                var sym = _docs[i].Symbol;
                int lim;
                if (!_input.LimityDyzurow.TryGetValue(sym, out lim)) lim = _days.Count;
                limits[sym] = lim;
            }

            return new SchedulingRules.Context(
                days: _days,
                doctors: _docs,
                avail: _av,
                limits: limits,
                priorities: _priorities,
                assign: assign ?? _assign,
                work: work ?? _workPerDoc,
                mwUsed: mw ?? _mwUsed,
                isPrefixActive: isPrefixActive
            );
        }

        private static int IndexOfPriority(SolverPriority p)
        {
            // zakładamy, że _priorities zawiera wszystkie 4; jeśli nie – fallback 0
            switch (p)
            {
                case SolverPriority.CiagloscPoczatkowa: return 0;
                case SolverPriority.LacznaLiczbaObsadzonychDni: return 1;
                case SolverPriority.SprawiedliwoscObciazenia: return 2;
                case SolverPriority.RownomiernoscRozlozenia: return 3;
                default: return 0;
            }
        }

        private double RatioAfter(int work, int limit)
        {
            double lim = Math.Max(1, limit);
            return (work + 1) / lim;
        }

        private string PrefToString(byte code)
        {
            switch (code)
            {
                case PREF_BC: return "BC";
                case PREF_CH: return "CH";
                case PREF_MG: return "MG";
                case PREF_MW: return "MW";
                case PREF_RZ: return "RZ";
                case PREF_OD: return "OD";
                default: return "--";
            }
        }

        private void ApplyPrefixSnapshot(int[] snap, int len)
        {
            for (int d = 0; d < _days.Count; d++) _assign[d] = UNASSIGNED;
            Array.Fill(_workPerDoc, 0);
            Array.Fill(_mwUsed, 0);
            _filled = 0;

            for (int d = 0; d < len; d++)
            {
                int doc = snap[d];
                if (doc >= 0)
                {
                    _assign[d] = doc;
                    _workPerDoc[doc]++;
                    if (_pref[d, doc] == PREF_MW) _mwUsed[doc]++;
                    _filled++;
                }
                else if (doc == EMPTY)
                {
                    _assign[d] = EMPTY;
                }
            }
        }

        private string FormatDay(int dayIndex)
        {
            return _days[dayIndex].ToString("yyyy-MM-dd");
        }

        private static string FormatScore(long[] v)
        {
            return "[" + string.Join(", ", v) + "]";
        }

        private bool Better(long[] cur, long[] best)
        {
            // porównanie leksykograficzne wg kolejności _priorities
            int n = Math.Min(cur.Length, best.Length);
            for (int i = 0; i < n; i++)
            {
                if (cur[i] > best[i]) return true;
                if (cur[i] < best[i]) return false;
            }
            return false;
        }

        private void EnsureBucketsBuilt()
        {
            if (_bucketsReady) return;

            int D = _days.Count;
            int P = _docs.Count;

            _buckBC = new List<int>[D];
            _buckCH = new List<int>[D];
            _buckMG = new List<int>[D];
            _buckMW = new List<int>[D];

            for (int d = 0; d < D; d++)
            {
                _buckBC[d] = new List<int>(Math.Max(4, P / 6));
                _buckCH[d] = new List<int>(Math.Max(4, P / 6));
                _buckMG[d] = new List<int>(Math.Max(4, P / 6));
                _buckMW[d] = new List<int>(Math.Max(4, P / 6));

                for (int p = 0; p < P; p++)
                {
                    byte code = _pref[d, p];
                    if (code == PREF_BC) _buckBC[d].Add(p);
                    else if (code == PREF_CH) _buckCH[d].Add(p);
                    else if (code == PREF_MG) _buckMG[d].Add(p);
                    else if (code == PREF_MW) _buckMW[d].Add(p);
                }
            }

            _bucketsReady = true;
        }

        // ====== UB (flow) – współdzielone przez F1 i STRICT B&B ======
        private int F1_SuffixUpperBoundCached(int startDay, int[] curWork)
        {
            var key = (startDay, HashRemCap(curWork));
            int ub;
            if (_ubCache.TryGetValue(key, out ub)) return ub;

            ub = FlowUB.UBCount(
                days: _days.Count,
                docs: _docs.Count,
                avMask: (d, p) =>
                {
                    if (d < startDay) return AvMask.None;
                    byte code = _pref[d, p];
                    return (code == PREF_NONE || code == PREF_OD) ? AvMask.None : AvMask.Any;
                },
                remCapPerDoc: p => Math.Max(0, _limitsByDoc[p] - curWork[p]),
                dayAllowed: d => d >= startDay
            );
            _ubCache[key] = ub;
            return ub;
        }

        private int F1_WindowUpperBoundCached(int startDay, int endDay, int[] curWork)
        {
            if (endDay < startDay) return 0;
            var key = (startDay, endDay, HashRemCap(curWork));
            int ub;
            if (_winUbCache.TryGetValue(key, out ub)) return ub;

            ub = FlowUB.UBCount(
                days: _days.Count,
                docs: _docs.Count,
                avMask: (d, p) =>
                {
                    if (d < startDay || d > endDay) return AvMask.None;
                    byte code = _pref[d, p];
                    return (code == PREF_NONE || code == PREF_OD) ? AvMask.None : AvMask.Any;
                },
                remCapPerDoc: p => Math.Max(0, _limitsByDoc[p] - curWork[p]),
                dayAllowed: d => d >= startDay && d <= endDay
            );
            _winUbCache[key] = ub;
            return ub;
        }

        private ulong HashRemCap(int[] curWork)
        {
            // FNV-1a 64
            ulong h = 1469598103934665603ul;
            const ulong P = 1099511628211ul;
            for (int p = 0; p < _docs.Count; p++)
            {
                uint rem = (uint)Math.Max(0, _limitsByDoc[p] - curWork[p]);
                h ^= (ulong)rem ^ (ulong)((p + 1) * 16777619);
                h *= P;
            }
            return h;
        }
    }
}
