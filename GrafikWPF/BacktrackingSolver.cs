using System.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using GrafikWPF.Algorithms;

namespace GrafikWPF
{
    /// <summary>
    /// "BTSolver Adam" – dwuetapowy backtracking
    /// Etap F1: maksymalizacja ciągłości od początku miesiąca (jeśli taki priorytet jest #1).
    ///          Przeszukiwanie gałęziami dzień po dniu: BC > CH > MG > MW, twarde zasady (RZ, limity).
    ///          Gdy brak obsady danego dnia – backtrack. Najlepszy znaleziony prefiks zapamiętywany.
    /// Etap F2: maksymalizacja łącznej obsady pozostałych dni (tie-break wg pozostałych priorytetów).
    ///          Preferencje: BC > CH > MG > MW. MW ≤ 1/osoba.
    /// Świętości: RZ zawsze przydzielane; limitów nie przekraczamy.
    /// Zasady sąsiedztwa: BC może łamać „dzień po dniu” i „Inny dyżur ±1”. MG/CH/MW – nie.
    /// Logowanie zgodne z SolverDiagnostics.
    /// </summary>
    public sealed class BacktrackingSolver : IGrafikSolver
    {
        // Preferencje / kody
        private const byte PREF_NONE = 0; // brak / niedostępny
        private const byte PREF_MW = 1;   // Mogę warunkowo (max 1 w miesiącu)
        private const byte PREF_MG = 2;   // Mogę
        private const byte PREF_CH = 3;   // Chcę
        private const byte PREF_BC = 4;   // Bardzo chcę
        private const byte PREF_RZ = 5;   // Rezerwacja (musi być)
        private const byte PREF_OD = 6;   // Dyżur (inny) – dzień +/-1 (blok sąsiedztwa dla MG/CH/MW)

        // Stany przypisań
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

        // Bieżący stan
        private readonly int[] _assign;     // day -> doc idx; EMPTY/UNASSIGNED
        private readonly int[] _workPerDoc; // przydzielone
        private readonly int[] _mwUsed;     // wykorzystane MW
        private int _filled;

        // Najlepszy wynik / scoring
        private RozwiazanyGrafik? _best;
        private long[]? _bestScore;

        // F1 – prefiks
        private int _bestPrefixLen;
        private int[]? _bestPrefixAssign; // snapshot assign dla najlepszego prefiksu

        // === KROK 1: Pruning na górnym oszacowaniu oraz mikrosito d+1 ===
        private const bool F1_USE_FLOW_UB = true;
        private const int F1_FLOW_EVERY = 8;

        // === KROK 2: Buckety kandydatów per dzień ===
        private List<int>[] _buckBC;
        private List<int>[] _buckCH;
        private List<int>[] _buckMG;
        private List<int>[] _buckMW;
        private bool _bucketsReady;

        // Polityki (na teraz: CHProtect OFF, BC może łamać sąsiedztwo, MW<=1)
        private readonly bool _chProtectEnabled = false;
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
            _docs = _input.Lekarze.OrderBy(l => l.Nazwisko).ThenBy(l => l.Imie).ToList();

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
            SolverDiagnostics.Start("BacktrackingSolver");

            // F1: maksymalizacja prefiksu (ciągłość, jeśli to #1)
            F1_MaximizePrefix();

            // Odtwórz najlepszy znaleziony prefiks i przejdź do F2
            if (_bestPrefixAssign != null)
            {
                ApplyPrefixSnapshot(_bestPrefixAssign, _bestPrefixLen);
                F2_MaximizeCoverageFrom(_bestPrefixLen);
            }
            else
            {
                // nic nie udało się ułożyć — próbuj od 0
                ApplyPrefixSnapshot(Array.Empty<int>(), 0);
                F2_MaximizeCoverageFrom(0);
            }

            var sol = BuildSolution();
            var score = EvaluateSolution(sol);
            if (_best is null || Better(score, _bestScore!))
            {
                _best = sol;
                _bestScore = score;
                SolverDiagnostics.Log($"[BEST] vec={FormatScore(score)}");

                SolverDiagnostics.Stop();
                return _best;
            }

            SolverDiagnostics.Stop();
            return sol;
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
                    var td = (_av.TryGetValue(date, out var map) && map.TryGetValue(sym, out var t))
                        ? t : TypDostepnosci.Niedostepny;
                    _pref[d, j] = td switch
                    {
                        TypDostepnosci.MogeWarunkowo => PREF_MW,
                        TypDostepnosci.Moge => PREF_MG,
                        TypDostepnosci.Chce => PREF_CH,
                        TypDostepnosci.BardzoChce => PREF_BC,
                        TypDostepnosci.Rezerwacja => PREF_RZ,
                        TypDostepnosci.DyzurInny => PREF_OD,
                        _ => PREF_NONE
                    };
                }
            }
        }

        // ===================== F1: Maksymalizacja prefiksu =====================

        // === KROK 1: Upper bound na pozostałe dni (max-flow na uproszczonej biparcie)
        private int F1_SuffixUpperBound(int startDay, int[] curWork)
        {
            return FlowUB.UBCount(
                days: _days.Count,
                docs: _docs.Count,
                avMask: (d, p) =>
                {
                    if (d < startDay) return AvMask.None;
                    var code = _pref[d, p];
                    return (code == PREF_NONE || code == PREF_OD) ? AvMask.None : AvMask.Any;
                },
                remCapPerDoc: p => Math.Max(0, _limitsByDoc[p] - curWork[p]),
                dayAllowed: d => d >= startDay
            );
        }

        private void F1_MaximizePrefix()
        {
            var curAssign = new int[_days.Count];
            Array.Fill(curAssign, UNASSIGNED);
            var curWork = new int[_docs.Count];
            var curMW = new int[_docs.Count];
            int prefixLen = 0;

            // KROK 2: zbuduj buckety kandydatów
            EnsureBucketsBuilt();

            void DFS(int day)
            {
                _token.ThrowIfCancellationRequested();
                _progress?.Report(day / (double)_days.Count);

                // Aktualizacja najlepszego prefiksu
                if (day > _bestPrefixLen)
                {
                    _bestPrefixLen = day;
                    _bestPrefixAssign = new int[_bestPrefixLen];
                    Array.Copy(curAssign, _bestPrefixAssign, _bestPrefixLen);
                    SolverDiagnostics.Log($"[F1] Nowy najlepszy prefiks: {_bestPrefixLen} ({FormatDay(_bestPrefixLen - 1)})");
                }
                if (day >= _days.Count) return;

                // KROK 1: pruning na optymistycznym UB (co kilka poziomów)
                if (F1_USE_FLOW_UB && (day % F1_FLOW_EVERY == 0))
                {
                    int ub = F1_SuffixUpperBound(day, curWork);
                    int potential = day + ub;
                    if (potential <= _bestPrefixLen)
                    {
                        SolverDiagnostics.Log($"[F1][UB-cut] {FormatDay(Math.Min(day, _days.Count - 1))}: UB={ub}, potential={potential} ≤ bestPref={_bestPrefixLen} → stop");
                        return;
                    }
                }

                var cands = F1_OrderCandidates(day, curAssign, curWork, curMW);
                LogCandidates(day, cands, curAssign, curWork);
                if (cands.Count == 0) return;

                foreach (var doc in cands)
                {
                    byte code = _pref[day, doc];
                    if (!F1_IsFeasible(day, doc, curAssign, curWork, curMW)) continue;

                    // KROK 1: mikrosito d+1 – twarde odcięcie
                    if (!KeepsNextFeasible(day, doc, curAssign, curWork, curMW))
                    {
                        SolverDiagnostics.Log($"[F1] Prune d+1: {FormatDay(day)} ← {_docs[doc].Symbol}");
                        continue;
                    }

                    // pick
                    curAssign[day] = doc;
                    curWork[doc]++;
                    if (code == PREF_MW) curMW[doc]++;

                    SolverDiagnostics.Log($"[F1] Pick: {FormatDay(day)} ← {_docs[doc].Symbol} [{PrefToString(code)}]");
                    DFS(day + 1);

                    // backtrack
                    if (code == PREF_MW) curMW[doc]--;
                    curWork[doc]--;
                    curAssign[day] = UNASSIGNED;
                    SolverDiagnostics.Log($"[F1][Backtrack] ← {FormatDay(day)}");
                }
            }

            DFS(0);
        }

        // === KROK 2: Budowa bucketów kandydatów per dzień ===
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

        // Mapowanie preferencji na wagę (mniejsza = lepsza) – używane w sortowaniu kandydatów F1
        private static int PrefWeight(byte code)
        {
            return code switch
            {
                PREF_BC => 0,
                PREF_CH => 1,
                PREF_MG => 2,
                PREF_MW => 3,
                _ => 4
            };
        }

        private List<int> F1_OrderCandidates(int day, int[] curAssign, int[] curWork, int[] curMW)
        {
            // Jeżeli są rezerwacje – wybierzemy spośród nich (z zachowaniem tie-breaków)
            var RZ = new List<int>();
            for (int p = 0; p < _docs.Count; p++)
            {
                if (_pref[day, p] == PREF_RZ && F1_IsFeasible(day, p, curAssign, curWork, curMW))
                    RZ.Add(p);
            }
            if (RZ.Count > 0)
            {
                RZ.Sort((a, b) => TieBreakF1(day, a, b, curAssign, curWork));
                SolverDiagnostics.Log($"[F1] RZ wymuszone – kandydaci: {string.Join(", ", RZ.Select(i => _docs[i].Symbol))}");
                return RZ;
            }

            // Buckety BC→CH→MG→MW zbudowane wcześniej
            var baseList = new List<int>(
                (_buckBC[day]?.Count ?? 0) +
                (_buckCH[day]?.Count ?? 0) +
                (_buckMG[day]?.Count ?? 0) +
                (_buckMW[day]?.Count ?? 0)
            );
            if (_buckBC[day] != null) baseList.AddRange(_buckBC[day]);
            if (_buckCH[day] != null) baseList.AddRange(_buckCH[day]);
            if (_buckMG[day] != null) baseList.AddRange(_buckMG[day]);
            if (_buckMW[day] != null) baseList.AddRange(_buckMW[day]);

            // Odrzuć od razu tych, którzy dobili do limitu
            for (int i = baseList.Count - 1; i >= 0; i--)
            {
                int p = baseList[i];
                if (curWork[p] >= _limitsByDoc[p]) baseList.RemoveAt(i);
            }

            // Przefiltruj przez twardą wykonalność
            var cands = new List<int>(baseList.Count);
            foreach (var doc in baseList)
            {
                if (F1_IsFeasible(day, doc, curAssign, curWork, curMW))
                    cands.Add(doc);
            }

            if (cands.Count <= 1) return cands;

            // Lokalny cache odległości – żeby nie liczyć tego wielokrotnie przy sortowaniu
            var nearestCache = new Dictionary<int, int>(cands.Count);
            int NearestCached(int doc)
            {
                if (!nearestCache.TryGetValue(doc, out var v))
                {
                    v = NearestAssignedDistance(day, doc, curAssign);
                    nearestCache[doc] = v;
                }
                return v;
            }

            cands.Sort((a, b) =>
            {
                // a) preferencja (BC<CH<MG<MW)
                int wa = PrefWeight(_pref[day, a]);
                int wb = PrefWeight(_pref[day, b]);
                if (wa != wb) return wa - wb;

                // b) mniejszy 'ratio after' bez double: (work+1)/limit ⇔ wna*lb vs wnb*la
                int la = _limitsByDoc[a];
                int lb = _limitsByDoc[b];
                int wna = curWork[a] + 1;
                int wnb = curWork[b] + 1;
                long left = (long)wna * lb;
                long right = (long)wnb * la;
                if (left != right) return left < right ? -1 : 1;

                // c) większa odległość od najbliższego własnego dyżuru
                int da = NearestCached(a);
                int db = NearestCached(b);
                if (da != db) return db - da;

                // d) stabilizator deterministyczny
                return a - b;
            });

            return cands;
        }

        private int TieBreakF1(int day, int a, int b, int[] curAssign, int[] curWork)
        {
            // mniejszy wskaźnik obciążenia (work+1)/limit – bez double
            int la = _limitsByDoc[a];
            int lb = _limitsByDoc[b];
            int wna = curWork[a] + 1;
            int wnb = curWork[b] + 1;
            long left = (long)wna * lb;
            long right = (long)wnb * la;
            int cmp = left.CompareTo(right);
            if (cmp != 0) return cmp;

            // większy dystans od istniejących dyżurów
            int da = NearestAssignedDistance(day, a, curAssign);
            int db = NearestAssignedDistance(day, b, curAssign);
            cmp = db.CompareTo(da);
            if (cmp != 0) return cmp;

            return a.CompareTo(b);
        }

        private bool F1_IsFeasible(int day, int doc, int[] curAssign, int[] curWork, int[] curMW)
        {
            if (curWork[doc] >= _limitsByDoc[doc]) return false;

            byte av = _pref[day, doc];
            if (av == PREF_NONE || av == PREF_OD) return false;

            bool isBC = (av == PREF_BC);

            // dzień-po-dniu: BC może łamać
            if (!_bcBreaksAdjacent || !isBC)
            {
                if (day > 0 && curAssign[day - 1] == doc) return false;
                if (day + 1 < _days.Count && _pref[day + 1, doc] == PREF_OD) return false;
            }

            if (av == PREF_MW && (curMW[doc] >= _mwMax)) return false;

            return true;
        }

        private bool KeepsNextFeasible(int day, int doc, int[] curAssign, int[] curWork, int[] curMW)
        {
            int dNext = day + 1;
            if (dNext >= _days.Count) return true;

            // Test: czy istnieje jakikolwiek kandydat na d+1 po hipotetycznym wyborze (day, doc)
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

        // ===================== F2: Maksymalizacja obsady =====================
        private void F2_MaximizeCoverageFrom(int startDay)
        {
            // Zainicjalizuj stan (na podstawie _assign – po F1 może zawierać przydział 0..start-1)
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
                else if (_assign[d] == EMPTY)
                {
                    // nic
                }
            }

            // 1) Wymuś rezerwacje w całym ogonie (świętość)
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;
                var rz = Enumerable.Range(0, _docs.Count)
                                   .Where(p => _pref[d, p] == PREF_RZ && IsHardFeasibleGlobal(d, p))
                                   .ToList();
                if (rz.Count > 0)
                {
                    int sel = SelectByTieBreakF2(d, rz);
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] RZ: {FormatDay(d)} ← {_docs[sel].Symbol}");
                }
            }

            // 2) Pętle heurystyczne: najpierw dni z unikalnym CH/BC, potem ogólnie CH/BC, potem MG, potem MW
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

            // 3) Dni wciąż puste oznacz jako EMPTY
            for (int d = startDay; d < _days.Count; d++)
                if (_assign[d] == UNASSIGNED) _assign[d] = EMPTY;
        }

        private bool TryFillUnique(int startDay, byte prefCode)
        {
            bool any = false;
            for (int d = startDay; d < _days.Count; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                var list = new List<int>();
                for (int p = 0; p < _docs.Count; p++)
                {
                    if (_pref[d, p] == prefCode && IsHardFeasibleGlobal(d, p))
                        list.Add(p);
                }
                if (list.Count == 1)
                {
                    int sel = list[0];
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

                var list = new List<int>();
                for (int p = 0; p < _docs.Count; p++)
                {
                    if (_pref[d, p] == prefCode && IsHardFeasibleGlobal(d, p))
                        list.Add(p);
                }
                if (list.Count > 0)
                {
                    int sel = SelectByTieBreakF2(d, list);
                    PlaceGlobal(d, sel);
                    SolverDiagnostics.Log($"[F2] {PrefToString(prefCode)}: {FormatDay(d)} ← {_docs[sel].Symbol}");
                    any = true;
                }
            }
            return any;
        }

        private int SelectByTieBreakF2(int day, List<int> candidates)
        {
            candidates.Sort((a, b) =>
            {
                // Priorytety użytkownika jako sekwencja tie-breakerów (poza tym, że cel główny F2 to maks. obsada)
                foreach (var pr in _priorities.Skip(1)) // #2, #3, #4
                {
                    int cmp = 0;
                    switch (pr)
                    {
                        case SolverPriority.SprawiedliwoscObciazenia:
                            {
                                double ra = RatioAfter(_workPerDoc[a], _limitsByDoc[a]);
                                double rb = RatioAfter(_workPerDoc[b], _limitsByDoc[b]);
                                cmp = ra.CompareTo(rb);
                                if (cmp != 0) return cmp;
                                break;
                            }
                        case SolverPriority.RownomiernoscRozlozenia:
                            {
                                int da = NearestAssignedDistanceGlobal(day, a);
                                int db = NearestAssignedDistanceGlobal(day, b);
                                cmp = db.CompareTo(da);
                                if (cmp != 0) return cmp;
                                break;
                            }
                        case SolverPriority.CiagloscPoczatkowa:
                            {
                                // w F2 nie zmieniamy prefiksu – pomijamy
                                break;
                            }
                    }
                }
                return a.CompareTo(b);
            });
            return candidates[0];
        }

        private bool IsHardFeasibleGlobal(int day, int doc)
        {
            if (_assign[day] != UNASSIGNED) return false;
            if (_workPerDoc[doc] >= _limitsByDoc[doc]) return false;

            byte av = _pref[day, doc];
            if (av == PREF_NONE || av == PREF_OD) return false;

            bool isBC = (av == PREF_BC);
            if (!_bcBreaksAdjacent || !isBC)
            {
                if (day > 0 && _assign[day - 1] == doc) return false;
                if (day + 1 < _days.Count && _pref[day + 1, doc] == PREF_OD) return false;
            }

            if (av == PREF_MW && (_mwUsed[doc] >= _mwMax)) return false;
            return true;
        }

        private void PlaceGlobal(int day, int doc)
        {
            _assign[day] = doc;
            _workPerDoc[doc]++;
            if (_pref[day, doc] == PREF_MW) _mwUsed[doc]++;
            _filled++;
        }

        private RozwiazanyGrafik BuildSolution()
        {
            var sol = new RozwiazanyGrafik { Przypisania = new Dictionary<DateTime, Lekarz?>() };
            for (int d = 0; d < _days.Count; d++)
                sol.Przypisania[_days[d]] = _assign[d] >= 0 ? _docs[_assign[d]] : null;
            return sol;
        }

        private long[] EvaluateSolution(RozwiazanyGrafik sol)
        {
            long sObs = 0, sCont = 0, sFair = 0, sEven = 0;

            var perDoc = new int[_docs.Count];
            for (int d = 0; d < _days.Count; d++)
            {
                if (sol.Przypisania.TryGetValue(_days[d], out var l) && l is not null)
                {
                    sObs++;
                    if (_docIdxBySymbol.TryGetValue(l.Symbol, out var idx))
                        perDoc[idx]++;
                }
            }
            for (int d = 0; d < _days.Count; d++)
            {
                if (!sol.Przypisania.TryGetValue(_days[d], out var l) || l is null) break;
                sCont++;
            }

            long sumLimits = 0;
            var lims = new long[_docs.Count];
            for (int i = 0; i < _docs.Count; i++)
            {
                lims[i] = _limitsByDoc[i];
                sumLimits += lims[i];
            }
            long sumWork = 0;
            for (int i = 0; i < _docs.Count; i++)
                sumWork += perDoc[i];

            // sprawiedliwość: sum |work_i/sumWork − lim_i/sumLimits|
            if (sumWork == 0) sFair = 0;
            else
            {
                double acc = 0.0;
                for (int i = 0; i < _docs.Count; i++)
                {
                    double w = perDoc[i] / (double)sumWork;
                    double L = lims[i] / (double)sumLimits;
                    acc += Math.Abs(w - L);
                }
                sFair = (long)Math.Round((1.0 - acc) * 1_000_000);
            }

            // równomierność: większe min-odległości lepsze
            int minDist = int.MaxValue;
            for (int d = 0; d < _days.Count; d++)
            {
                if (sol.Przypisania.TryGetValue(_days[d], out var l) && l is not null)
                {
                    var i = _docIdxBySymbol[l.Symbol];
                    int prev = int.MaxValue, next = int.MaxValue;
                    for (int k = d - 1; k >= 0; k--) if (sol.Przypisania[_days[k]]?.Symbol == l.Symbol) { prev = d - k; break; }
                    for (int k = d + 1; k < _days.Count; k++) if (sol.Przypisania[_days[k]]?.Symbol == l.Symbol) { next = k - d; break; }
                    minDist = Math.Min(minDist, Math.Min(prev, next));
                }
            }
            sEven = (minDist == int.MaxValue) ? 0 : minDist;

            // score wektorowy (max lexicograficznie wg priorytetów: #1, #2, #3, #4)
            long[] vec = new long[4];
            foreach (var pr in _priorities.Select((p, i) => (p, i)))
            {
                vec[pr.i] = pr.p switch
                {
                    SolverPriority.CiagloscPoczatkowa => sCont,
                    SolverPriority.LacznaLiczbaObsadzonychDni => sObs,
                    SolverPriority.SprawiedliwoscObciazenia => sFair,
                    SolverPriority.RownomiernoscRozlozenia => sEven,
                    _ => 0
                };
            }
            return vec;
        }

        private static string FormatScore(long[] v) => $"[{string.Join(", ", v)}]";

        // ===================== Utils =====================
        private double RatioAfter(int work, int limit)
        {
            double lim = Math.Max(1, limit);
            return (work + 1) / lim;
        }

        private int NearestAssignedDistance(int day, int doc, int[] curAssign)
        {
            int best = int.MaxValue;
            for (int d = day - 1; d >= 0; d--)
                if (curAssign[d] == doc) { best = Math.Min(best, day - d); break; }
            for (int d = day + 1; d < _days.Count; d++)
                if (curAssign[d] == doc) { best = Math.Min(best, d - day); break; }
            return best == int.MaxValue ? 9999 : best;
        }

        private int NearestAssignedDistanceGlobal(int day, int doc)
        {
            int best = int.MaxValue;
            for (int d = day - 1; d >= 0; d--)
                if (_assign[d] == doc) { best = Math.Min(best, day - d); break; }
            for (int d = day + 1; d < _days.Count; d++)
                if (_assign[d] == doc) { best = Math.Min(best, d - day); break; }
            return best == int.MaxValue ? 9999 : best;
        }

        private static int PrefRank(byte code) => code switch
        {
            PREF_RZ => 5,
            PREF_BC => 4,
            PREF_CH => 3,
            PREF_MG => 2,
            PREF_MW => 1,
            _ => 0
        };

        private string PrefToString(byte code) => code switch
        {
            PREF_BC => "BC",
            PREF_CH => "CH",
            PREF_MG => "MG",
            PREF_MW => "MW",
            PREF_RZ => "RZ",
            PREF_OD => "OD",
            _ => "--"
        };

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

        private string FormatDay(int dayIndex) => $"{_days[dayIndex]:yyyy-MM-dd}";

        private bool Better(long[] cur, long[] best)
        {
            for (int i = 0; i < Math.Min(cur.Length, best.Length); i++)
            {
                if (cur[i] > best[i]) return true;
                if (cur[i] < best[i]) return false;
            }
            return false;
        }

        private void LogInputSnapshot()
        {
            SolverDiagnostics.Log("--- Deklaracje (dzień → lekarz:deklaracja) ---");
            for (int d = 0; d < _days.Count; d++)
            {
                var sb = new StringBuilder();
                sb.Append($"{FormatDay(d)}: ");
                for (int p = 0; p < _docs.Count; p++)
                {
                    sb.Append(_docs[p].Symbol);
                    sb.Append(':');
                    sb.Append(PrefToString(_pref[d, p]));
                    if (p + 1 < _docs.Count) sb.Append(", ");
                }
                SolverDiagnostics.Log(sb.ToString());
            }
            SolverDiagnostics.Log("--- /Deklaracje (dzień → lekarz:deklaracja) ---");
        }

        private void LogCandidates(int day, List<int> cand, int[] curAssign, int[] curWork)
        {
            var prefix = day; // bo w F1 układamy 0..day-1
            SolverDiagnostics.Log($"--- [F1] Kandydaci dnia {FormatDay(day)} ---");
            SolverDiagnostics.Log($"Dzień {FormatDay(day)} (prefiks={prefix}) – kandydaci:");
            foreach (var p in cand)
                SolverDiagnostics.Log($"  ✓ {_docs[p].Symbol} [{PrefToString(_pref[day, p])}]  (pracuje={curWork[p]}, limit={_limitsByDoc[p]})");
            SolverDiagnostics.Log($"--- /[F1] Kandydaci dnia {FormatDay(day)} ---");
        }
    }
}
