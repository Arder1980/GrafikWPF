#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GrafikWPF
{
    public sealed class BacktrackingSolver : IGrafikSolver
    {
        private const int UNASSIGNED = int.MinValue;

        // Parametry heurystyk (łatwe do strojenia)
        private const int MATCH_WINDOW_BASE = 9;        // bazowa szerokość okna przepustowości
        private const int MATCH_WINDOW_MIN = 7;        // min
        private const int MATCH_WINDOW_MAX = 12;       // max
        private const int CH_RESERVE_HORIZON_DAYS = 7;  // horyzont „rezerwowania” dni CH (krótki)
        private const int SMALL_DEFICIT_MAX = 2;        // mały deficyt okna, który można „pokryć” pustką

        private readonly GrafikWejsciowy _in;
        private readonly List<SolverPriority> _prio;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _ct;

        private readonly int _days, _docs;
        private readonly List<Lekarz> _docsMap;

        private readonly int[,] _av;              // deklaracje jako TypDostepnosci
        private readonly int[] _limit;            // limity lekarzy
        private readonly bool[,] _nextToOther;    // sąsiedztwo „Dyżur (inny)”
        private readonly List<int>[] _staticCands;

        // Stan bieżący
        private int[] _assign = Array.Empty<int>();  // dzień -> lekarz (UNASSIGNED / -1=PUSTO / >=0 = indeks lekarza)
        private int[] _work = Array.Empty<int>();  // ile dyżurów na lekarza
        private int[] _mwCount = Array.Empty<int>(); // ile razy lekarz użył „Mogę warunkowo” (MW)
        private int _assignedCount, _obs, _prefx;

        // PUSTKI – „kredyty”
        private int _blankCreditsTotal;
        private int _blankCreditsUsed;

        // Najlepszy wynik
        private readonly object _bestLock = new();
        private long[] _bestVec = Array.Empty<long>();
        private int[] _bestAssign = Array.Empty<int>();

        private bool _diagStartedHere = false;

        public BacktrackingSolver(GrafikWejsciowy dane, List<SolverPriority> priorytety,
                                  IProgress<double>? progress = null, CancellationToken ct = default)
        {
            _in = dane; _prio = priorytety; _progress = progress; _ct = ct;

            _docsMap = _in.Lekarze.Where(l => l.IsAktywny).ToList();
            _days = _in.DniWMiesiacu.Count;
            _docs = _docsMap.Count;

            _av = new int[_days, _docs];
            _limit = new int[_docs];
            _nextToOther = new bool[_days, _docs];
            _staticCands = new List<int>[_days];

            // limity
            for (int p = 0; p < _docs; p++)
            {
                var sym = _docsMap[p].Symbol;
                _limit[p] = _in.LimityDyzurow.TryGetValue(sym, out var lim) ? lim : _days;
            }

            // macierze dostępności i sąsiedztwa
            for (int d = 0; d < _days; d++)
            {
                var date = _in.DniWMiesiacu[d];
                var prev = date.AddDays(-1);
                var next = date.AddDays(1);

                for (int p = 0; p < _docs; p++)
                {
                    var sym = _docsMap[p].Symbol;
                    var a = _in.Dostepnosc[date].GetValueOrDefault(sym, TypDostepnosci.Niedostepny);
                    _av[d, p] = (int)a;

                    if ((_in.Dostepnosc.ContainsKey(prev) && _in.Dostepnosc[prev].GetValueOrDefault(sym) == TypDostepnosci.DyzurInny) ||
                        (_in.Dostepnosc.ContainsKey(next) && _in.Dostepnosc[next].GetValueOrDefault(sym) == TypDostepnosci.DyzurInny))
                        _nextToOther[d, p] = true;
                }
            }

            // kandydaci statyczni (kolejność: Rezerwacja > BC > CH > MG > MW)
            for (int d = 0; d < _days; d++)
            {
                var rez = new List<int>(_docs);
                var zwykli = new List<int>(_docs);

                for (int p = 0; p < _docs; p++)
                {
                    var t = (TypDostepnosci)_av[d, p];
                    if (t == TypDostepnosci.Niedostepny || t == TypDostepnosci.Urlop || t == TypDostepnosci.DyzurInny) continue;
                    if (t == TypDostepnosci.Rezerwacja) rez.Add(p); else zwykli.Add(p);
                }

                var baseList = rez.Count > 0 ? rez : zwykli;
                baseList.Sort((a, b) =>
                {
                    int ra = PrefRank((TypDostepnosci)_av[d, a]);
                    int rb = PrefRank((TypDostepnosci)_av[d, b]);
                    if (ra != rb) return rb.CompareTo(ra);
                    return a.CompareTo(b);
                });
                _staticCands[d] = baseList;
            }
        }

        public RozwiazanyGrafik ZnajdzOptymalneRozwiazanie()
        {
            _ct.ThrowIfCancellationRequested();

            if (SolverDiagnostics.Enabled && !SolverDiagnostics.IsActive)
            {
                SolverDiagnostics.Start();
                _diagStartedHere = true;
            }
            LogHeaderInput();

            _assign = new int[_days]; Array.Fill(_assign, UNASSIGNED);
            _work = new int[_docs];
            _mwCount = new int[_docs];
            _assignedCount = 0; _obs = 0; _prefx = 0;

            // kredyty pustek: 1 na każde 10 dni (zaokrąglone w górę), minimum 1 dla 1–10 dni
            _blankCreditsTotal = Math.Max(1, (int)Math.Ceiling(_days / 10.0));
            _blankCreditsUsed = 0;
            SolverDiagnostics.Log($"Kredyty pustek: {_blankCreditsUsed}/{_blankCreditsTotal}");

            (_bestAssign, _bestVec) = BuildGreedyIncumbent();

            DFS();

            var res = new RozwiazanyGrafik();
            for (int d = 0; d < _days; d++)
            {
                var date = _in.DniWMiesiacu[d];
                int p = _bestAssign.Length == _days ? _bestAssign[d] : -1;
                res.Przypisania[date] = (p >= 0 ? _docsMap[p] : null);
            }

            _progress?.Report(1.0);
            SolverDiagnostics.Log("Zakończono generowanie.");
            if (_diagStartedHere) SolverDiagnostics.Stop();
            return res;
        }

        private void DFS()
        {
            _ct.ThrowIfCancellationRequested();

            if (_assignedCount == _days) { SubmitBest(); return; }
            if (!CanBeatBestUB()) { SolverDiagnostics.Log("Przycięto gałąź: górna granica (UB)."); return; }

            // 1) Jednokrokowa unit-propagacja: jeśli dokładnie jeden kandydat – ustaw i sprawdź
            {
                int dayUnit = NextDayByStrategy(out bool continuityActive);
                var leg = CollectLegal(dayUnit, continuityActive);
                if (leg.Count == 1)
                {
                    int p = leg[0];
                    var av = (TypDostepnosci)_av[dayUnit, p];
                    SolverDiagnostics.Log($"Unit-prop: dzień {FmtDay(dayUnit)} → jedyny kandydat {FmtDoc(p)} ({FmtPref(av)}).");

                    Make(dayUnit, p);

                    if (!ForwardFeasibleAfter(dayUnit, p, av, continuityActive, out string? reasonF, out _))
                    {
                        SolverDiagnostics.Log($"Odrzucam po unit-prop: forward-check fail → {reasonF}");
                        Unmake(dayUnit);
                        return;
                    }
                    if (!WindowFeasibleAfter(dayUnit, p, av, continuityActive, out string? reasonW, out int deficit) &&
                        !TrySpendBlankIfSmallDeficit(dayUnit, reasonW, deficit, continuityActive))
                    {
                        SolverDiagnostics.Log($"Odrzucam po unit-prop: okno nie do przejścia → {reasonW}");
                        Unmake(dayUnit);
                        return;
                    }

                    DFS();
                    Unmake(dayUnit);
                    return;
                }
            }

            // 2) Wybór dnia + kandydaci (dwufazowo: najpierw BC/CH/Rezerwacja, potem MG/MW)
            int day = NextDayByStrategy(out bool contActive);
            var legalAll = CollectLegal(day, contActive);
            LogCandidatesWithReasons(day, legalAll, contActive);

            if (legalAll.Count == 0)
            {
                // Nikt legalny → spróbuj PUSTO (nawet przy ciągłości)
                SolverDiagnostics.Log($"PRÓBA: {FmtDay(day)} ← PUSTO (brak legalnych kandydatów)");
                Make(day, -1);
                DFS();
                Unmake(day);
                return;
            }

            // Faza 1: tylko Rezerwacja/BC/CH
            var legalPhase1 = legalAll.Where(p =>
            {
                var t = (TypDostepnosci)_av[day, p];
                return t == TypDostepnosci.Rezerwacja || t == TypDostepnosci.BardzoChce || t == TypDostepnosci.Chce;
            }).ToList();

            // Faza 2: MG/MW
            var legalPhase2 = legalAll.Where(p =>
            {
                var t = (TypDostepnosci)_av[day, p];
                return t == TypDostepnosci.Moge || t == TypDostepnosci.MogeWarunkowo;
            }).ToList();

            bool progressed = false;
            bool anyWindowSmallDeficit = false;

            foreach (var phase in new[] { legalPhase1, legalPhase2 })
            {
                if (phase.Count == 0) { continue; }

                // Sortowanie: ranga deklaracji > mniej przyszłych CH (horyzont 7) > mniejsze obciążenie > mniejsza pozostała pojemność > indeks
                phase.Sort((a, b) =>
                {
                    int ra = PrefRank((TypDostepnosci)_av[day, a]);
                    int rb = PrefRank((TypDostepnosci)_av[day, b]);
                    if (ra != rb) return rb.CompareTo(ra);

                    int fca = FutureChceFeasibleCount(day, a, CH_RESERVE_HORIZON_DAYS);
                    int fcb = FutureChceFeasibleCount(day, b, CH_RESERVE_HORIZON_DAYS);
                    if (fca != fcb) return fca.CompareTo(fcb);

                    int wla = _work[a], wlb = _work[b];
                    if (wla != wlb) return wla.CompareTo(wlb);

                    int remA = _limit[a] - _work[a];
                    int remB = _limit[b] - _work[b];
                    if (remA != remB) return remA.CompareTo(remB);

                    return a.CompareTo(b);
                });

                foreach (int p in phase)
                {
                    var av = (TypDostepnosci)_av[day, p];
                    SolverDiagnostics.Log($"PRÓBA: {FmtDay(day)} ← {FmtDoc(p)} ({FmtPref(av)}), pracuje={_work[p]}, limit={_limit[p]}");

                    Make(day, p);

                    if (!ForwardFeasibleAfter(day, p, av, contActive, out string? reasonF, out _))
                    {
                        SolverDiagnostics.Log($"Odrzucono: forward-check fail → {reasonF}");
                        Unmake(day);
                        continue;
                    }

                    if (!WindowFeasibleAfter(day, p, av, contActive, out string? reasonW, out int deficit))
                    {
                        if (deficit > 0 && deficit <= SMALL_DEFICIT_MAX && TrySpendBlankIfSmallDeficit(day, reasonW, deficit, contActive))
                        {
                            // potraktuj jak przechodzące (pokryto kredytem pustek)
                            progressed = true;
                            DFS();
                            Unmake(day);
                            continue;
                        }
                        else
                        {
                            anyWindowSmallDeficit |= (deficit > 0 && deficit <= SMALL_DEFICIT_MAX);
                            SolverDiagnostics.Log($"Odrzucono: okno nie do przejścia → {reasonW}");
                            Unmake(day);
                            continue;
                        }
                    }

                    // OK – schodzimy w głąb
                    progressed = true;
                    SolverDiagnostics.Log("Akceptuję i schodzę głębiej.");
                    DFS();
                    Unmake(day);
                }

                if (progressed) break; // jeśli faza 1 dała wejście, nie schodzimy do fazy 2
            }

            // 3) PUSTO – kiedy?
            bool contNow = IsContinuityActive();
            if (!progressed)
            {
                SolverDiagnostics.Log($"PRÓBA: {FmtDay(day)} ← PUSTO (wszyscy kandydaci odrzuceni)");
                Make(day, -1);
                DFS();
                Unmake(day);
            }
            else if (contNow && legalAll.Count > 0 && anyWindowSmallDeficit && _blankCreditsUsed < _blankCreditsTotal)
            {
                SolverDiagnostics.Log($"PRÓBA: {FmtDay(day)} ← PUSTO (ciągłość aktywna, małe deficyty okna, wolny kredyt pustki {_blankCreditsUsed + 1}/{_blankCreditsTotal})");
                Make(day, -1);
                DFS();
                Unmake(day);
            }
            else
            {
                if (contNow && legalAll.Count > 0)
                    SolverDiagnostics.Log($"POMINIĘTO „PUSTO” (ciągłość aktywna i brak potrzeby/limitu na pustkę).");
            }
        }

        // === Pomocnicze: wybór dnia, legalność, forward, okno, itd. ===

        // Czy ciągłość od początku jest nadal „spójna” (bez dziur)?
        private bool IsContinuityActive()
        {
            int earliest = EarliestUnassigned();
            if (earliest < 0) return false;
            // _prefx = liczba dni od początku obsadzonych realnie (nie PUSTO)
            // ciągłość aktywna wtedy, gdy nie ma PUSTO przed earliest → _prefx == earliest
            return _prefx == earliest;
        }

        private int NextDayByStrategy(out bool continuityActive)
        {
            continuityActive = IsContinuityActive();
            if (continuityActive) return EarliestUnassigned();
            return NextDayByMRV();
        }

        private int EarliestUnassigned()
        {
            for (int d = 0; d < _days; d++) if (_assign[d] == UNASSIGNED) return d;
            return -1;
        }

        private int NextDayByMRV()
        {
            int best = -1, bestCnt = int.MaxValue, bestBC = int.MaxValue, bestCh = int.MaxValue, bestIndex = int.MaxValue;

            for (int d = 0; d < _days; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                int cnt = 0, bcCnt = 0, chCnt = 0;
                foreach (var p in _staticCands[d])
                {
                    if (IsValidDynamic(d, p, ignoreChceReserve: false))
                    {
                        cnt++;
                        var av = (TypDostepnosci)_av[d, p];
                        if (av == TypDostepnosci.BardzoChce) bcCnt++;
                        else if (av == TypDostepnosci.Chce) chCnt++;
                    }
                }
                if (cnt == 0) cnt = int.MaxValue / 2;

                bool better = (cnt < bestCnt)
                           || (cnt == bestCnt && bcCnt < bestBC)
                           || (cnt == bestCnt && bcCnt == bestBC && chCnt < bestCh)
                           || (cnt == bestCnt && bcCnt == bestBC && chCnt == bestCh && d < bestIndex);

                if (better) { best = d; bestCnt = cnt; bestBC = bcCnt; bestCh = chCnt; bestIndex = d; }
            }
            return best >= 0 ? best : 0;
        }

        private List<int> CollectLegal(int day, bool continuityActive)
        {
            var legal = new List<int>(_staticCands[day].Count);
            bool ignoreChReserveHere = continuityActive && (day == EarliestUnassigned());

            foreach (var p in _staticCands[day])
            {
                if (IsValidDynamic(day, p, ignoreChReserveHere))
                    legal.Add(p);
            }
            return legal;
        }

        private void Make(int day, int p)
        {
            _assign[day] = p; _assignedCount++;
            if (p >= 0)
            {
                _obs++; _work[p]++;
                if ((TypDostepnosci)_av[day, p] == TypDostepnosci.MogeWarunkowo) _mwCount[p]++;
            }
            else
            {
                // PUSTO – sam ruch PUSTO nie zużywa kredytu automatycznie
            }
            RecomputePrefix();
        }

        private void Unmake(int day)
        {
            int cur = _assign[day];
            _assign[day] = UNASSIGNED; _assignedCount--;
            if (cur >= 0)
            {
                _obs--; _work[cur]--;
                if ((TypDostepnosci)_av[day, cur] == TypDostepnosci.MogeWarunkowo && _mwCount[cur] > 0) _mwCount[cur]--;
            }
            RecomputePrefix();
        }

        private void RecomputePrefix()
        {
            int k = 0;
            while (k < _days)
            {
                int a = _assign[k];
                if (a == UNASSIGNED || a == -1) break; // PUSTO przerywa ciągłość
                k++;
            }
            _prefx = k;
        }

        private bool IsValidDynamic(int day, int p, bool ignoreChceReserve)
        {
            if (p < 0 || p >= _docs) return false;
            if (_work[p] >= _limit[p]) return false;

            var av = (TypDostepnosci)_av[day, p];
            bool bc = av == TypDostepnosci.BardzoChce;

            // Zakaz dni dzień-po-dniu + sąsiedztwo „Dyzur (inny)” – chyba że BC
            if (!bc)
            {
                if (day > 0 && _assign[day - 1] == p) return false;
                if (day + 1 < _days && _assign[day + 1] == p) return false;
                if (_nextToOther[day, p]) return false;
            }

            // twarde wykluczenia
            if (av == TypDostepnosci.Niedostepny || av == TypDostepnosci.Urlop || av == TypDostepnosci.DyzurInny) return false;

            // MW = maksymalnie 1 na lekarza
            if (av == TypDostepnosci.MogeWarunkowo && _mwCount[p] >= 1) return false;

            // Rezerwa „Chcę”: jeśli dziś tylko „Mogę/MW”, staramy się nie zjadać limitu,
            // jeżeli w najbliższych CH_RESERVE_HORIZON_DAYS są realne „CH” i pojemność by nie wystarczyła.
            if (!ignoreChceReserve && (av == TypDostepnosci.Moge || av == TypDostepnosci.MogeWarunkowo))
            {
                if (UniqueFutureFeasibleExists(day, p)) return false;

                int needChce = FutureChceFeasibleCount(day, p, CH_RESERVE_HORIZON_DAYS);
                if (needChce > 0)
                {
                    int remainingAfter = _limit[p] - _work[p] - 1;
                    if (remainingAfter < needChce) return false;
                }
            }
            return true;
        }

        private bool ForwardFeasibleAfter(int chosenDay, int chosenDoctor, TypDostepnosci avChosen, bool continuityActive,
                                          out string? reason, out int dummy)
        {
            // Tutaj nie mierzymy deficytu – zwracamy 0 jako „dummy”
            dummy = 0;
            reason = null;
            if (chosenDoctor < 0) return true;

            int earliest = EarliestUnassigned();
            bool ignoreChReserveForEarliest = continuityActive;

            // POPRAWKA: bez nazwanego parametru (żeby uniknąć CS1739)
            bool Check(int d) => CheckDayHasAnyCandidate(d, ignoreChReserveForEarliest && d == earliest);

            if (!Check(chosenDay - 1)) { reason = $"brak kandydata dla dnia {FmtDay(chosenDay - 1)} po przydziale"; return false; }
            if (!Check(chosenDay + 1)) { reason = $"brak kandydata dla dnia {FmtDay(chosenDay + 1)} po przydziale"; return false; }

            // nie zjedz limitu potrzebnego na przyszłe „CH” (krótki horyzont)
            if (avChosen == TypDostepnosci.Moge || avChosen == TypDostepnosci.MogeWarunkowo)
            {
                int futureChce = FutureChceFeasibleCount(chosenDay, chosenDoctor, CH_RESERVE_HORIZON_DAYS);
                int remaining = _limit[chosenDoctor] - _work[chosenDoctor];
                if (remaining < futureChce)
                {
                    reason = $"po przydziale braknie limitu na przyszłe „Chcę” ({futureChce}) dla {FmtDoc(chosenDoctor)}";
                    return false;
                }
            }
            return true;
        }

        private bool CheckDayHasAnyCandidate(int d, bool ignoreChceReserve)
        {
            if (d < 0 || d >= _days) return true;
            if (_assign[d] != UNASSIGNED) return true;

            foreach (var p in _staticCands[d])
                if (IsValidDynamic(d, p, ignoreChceReserve)) return true;

            return false;
        }

        // Czy istnieje w przyszłości dzień, gdzie TYLKO ten lekarz jest realny (dowolna deklaracja != wykluczona)?
        private bool UniqueFutureFeasibleExists(int fromDay, int p)
        {
            for (int d = fromDay + 1; d < _days; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;
                if (!FeasibleFor(p, d)) continue;

                bool anyOther = false;
                foreach (var q in _staticCands[d])
                {
                    if (q == p) continue;
                    if (FeasibleFor(q, d)) { anyOther = true; break; }
                }
                if (!anyOther) return true;
            }
            return false;
        }

        // Ile przyszłych dni „Chcę” dla p jest REALNIE wykonalnych – w krótkim horyzoncie (hDays)?
        private int FutureChceFeasibleCount(int fromDay, int p, int hDays)
        {
            int need = 0;
            int end = Math.Min(_days - 1, fromDay + hDays);
            for (int d = fromDay + 1; d <= end; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;

                var av = (TypDostepnosci)_av[d, p];
                if (av != TypDostepnosci.Chce) continue;

                // lokalna wykonalność dla „Chcę”
                if (d > 0 && _assign[d - 1] == p) continue;
                if (d + 1 < _days && _assign[d + 1] == p) continue;
                if (_nextToOther[d, p]) continue;

                need++;
            }
            return need;
        }

        private bool FeasibleFor(int p, int day)
        {
            if (_work[p] >= _limit[p]) return false;
            var av = (TypDostepnosci)_av[day, p];
            if (av == TypDostepnosci.Niedostepny || av == TypDostepnosci.Urlop || av == TypDostepnosci.DyzurInny) return false;

            bool bc = av == TypDostepnosci.BardzoChce;
            if (!bc)
            {
                if (day > 0 && _assign[day - 1] == p) return false;
                if (day + 1 < _days && _assign[day + 1] == p) return false;
                if (_nextToOther[day, p]) return false;
            }

            if (av == TypDostepnosci.MogeWarunkowo && _mwCount[p] >= 1) return false; // drugie MW – zabronione
            return true;
        }

        private bool WindowFeasibleAfter(int chosenDay, int chosenDoctor, TypDostepnosci avChosen, bool continuityActive,
                                         out string? reason, out int deficit)
        {
            reason = null;
            deficit = 0;

            // Okno stosujemy głównie przy utrzymaniu prefiksu (ciągłości) i dla „miękkich” deklaracji
            if (!continuityActive && !(avChosen == TypDostepnosci.Chce || avChosen == TypDostepnosci.Moge || avChosen == TypDostepnosci.MogeWarunkowo))
                return true;

            int start = EarliestUnassigned();
            if (start < 0) return true;

            // Adaptacyjna szerokość okna
            int win = AdaptMatchWindow(start, Math.Max(start, chosenDay));
            int end = Math.Min(_days - 1, Math.Max(start, chosenDay) + win - 1);

            var days = new List<int>();
            for (int d = start; d <= end; d++) if (_assign[d] == UNASSIGNED) days.Add(d);
            if (days.Count == 0) return true;

            // policz kandydatów wykonalnych w oknie
            int[] feasCnt = new int[days.Count];
            for (int i = 0; i < days.Count; i++)
            {
                int d = days[i];
                int cnt = 0;
                foreach (var p in _staticCands[d]) if (FeasibleFor(p, d)) cnt++;
                feasCnt[i] = cnt;
            }

            // liczba dni, które „muszą” być obsadzone w prefiksie okna
            int prefixNeed = 0;
            for (int i = 0; i < days.Count; i++)
            {
                if (feasCnt[i] == 0) break;
                prefixNeed++;
            }
            if (prefixNeed <= 0) return true;

            int sumCap = 0;
            for (int p = 0; p < _docs; p++)
            {
                int cap = _limit[p] - _work[p]; if (cap <= 0) continue;
                int feas = 0;
                for (int i = 0; i < days.Count; i++)
                {
                    int d = days[i];
                    if (FeasibleFor(p, d)) feas++;
                }
                if (feas > 0) sumCap += Math.Min(cap, feas);
            }

            if (sumCap < prefixNeed)
            {
                deficit = prefixNeed - sumCap;
                reason = $"suma pojemności {sumCap} < prefiks okna {prefixNeed} (deficyt {deficit}, okno={win})";
                return false;
            }

            // Dokładniejszy test max-flow (z limitem = prefixNeed)
            var dinic = new Dinic();
            int S = dinic.AddNode();
            int T = dinic.AddNode();

            int[] docNode = new int[_docs];
            for (int p = 0; p < _docs; p++)
            {
                int cap = _limit[p] - _work[p];
                if (cap <= 0) { docNode[p] = -1; continue; }
                int feas = 0;
                for (int i = 0; i < days.Count; i++) if (FeasibleFor(p, days[i])) feas++;
                if (feas == 0) { docNode[p] = -1; continue; }

                docNode[p] = dinic.AddNode();
                dinic.AddEdge(S, docNode[p], Math.Min(cap, feas));
            }

            int[] dayNode = new int[days.Count];
            for (int i = 0; i < days.Count; i++)
            {
                dayNode[i] = dinic.AddNode();
                dinic.AddEdge(dayNode[i], T, 1);
            }

            for (int p = 0; p < _docs; p++)
            {
                if (docNode[p] < 0) continue;
                for (int i = 0; i < days.Count; i++)
                {
                    int d = days[i];
                    if (FeasibleFor(p, d)) dinic.AddEdge(docNode[p], dayNode[i], 1);
                }
            }

            int flow = dinic.MaxFlow(S, T, limit: prefixNeed);
            if (flow < prefixNeed)
            {
                deficit = prefixNeed - flow;
                reason = $"max-flow={flow} < wymagany prefiks={prefixNeed} (deficyt {deficit}, okno={win})";
                return false;
            }
            return true;
        }

        private int AdaptMatchWindow(int start, int anchor)
        {
            // Prosta adaptacja: licz średnią „liczbę wykonalnych kandydatów” w najbliższych 7 dniach
            int look = Math.Min(_days - 1, anchor + 6);
            int totalDays = 0;
            int totalFeas = 0;
            for (int d = start; d <= look; d++)
            {
                if (_assign[d] != UNASSIGNED) continue;
                totalDays++;
                int cnt = 0;
                foreach (var p in _staticCands[d]) if (FeasibleFor(p, d)) cnt++;
                totalFeas += cnt;
            }
            if (totalDays == 0) return MATCH_WINDOW_BASE;

            double avg = totalFeas / (double)totalDays;
            int win = MATCH_WINDOW_BASE;
            if (avg < 1.5) win = Math.Min(MATCH_WINDOW_MAX, MATCH_WINDOW_BASE + 2);
            else if (avg > 3.5) win = Math.Max(MATCH_WINDOW_MIN, MATCH_WINDOW_BASE - 2);
            return win;
        }

        private bool TrySpendBlankIfSmallDeficit(int day, string? reasonW, int deficit, bool continuityActive)
        {
            if (!continuityActive) return false;
            if (deficit <= 0 || deficit > SMALL_DEFICIT_MAX) return false;
            if (_blankCreditsUsed >= _blankCreditsTotal) return false;

            _blankCreditsUsed++;
            SolverDiagnostics.Log($"[PUSTKA-KREDYT] deficyt={deficit} pokryty → wykorzystane pustki: {_blankCreditsUsed}/{_blankCreditsTotal} (dzień {FmtDay(day)}), powód: {reasonW}");
            // UWAGA: nie zmieniamy stanu przydziałów tutaj – ta metoda tylko pozwala „przepuścić” test okna
            return true;
        }

        private bool CanBeatBestUB()
        {
            if (_prio.Count == 0 || _prio[0] != SolverPriority.LacznaLiczbaObsadzonychDni) return true;

            int remDays = _days - _assignedCount;
            int remCap = 0;
            for (int p = 0; p < _docs; p++) remCap += Math.Max(0, _limit[p] - _work[p]);
            int ubObs = _obs + Math.Min(remDays, Math.Max(0, remCap));

            long bestFirst;
            lock (_bestLock) bestFirst = _bestVec.Length > 0 ? _bestVec[0] : long.MinValue;
            return ubObs >= bestFirst;
        }

        private void SubmitBest()
        {
            var sol = new RozwiazanyGrafik();
            for (int d = 0; d < _days; d++)
            {
                var date = _in.DniWMiesiacu[d];
                int p = _assign[d];
                sol.Przypisania[date] = (p >= 0 ? _docsMap[p] : null);
            }

            var vec = EvaluationAndScoringService.ToIntVector(sol, _prio);
            TryUpdateBest(_assign, vec);
        }

        private void TryUpdateBest(int[] assignCandidate, long[] vecCandidate)
        {
            lock (_bestLock)
            {
                if (_bestVec.Length == 0 || LexGreater(vecCandidate, _bestVec))
                {
                    _bestVec = (long[])vecCandidate.Clone();
                    _bestAssign = (int[])assignCandidate.Clone();
                    SolverDiagnostics.Log($"Nowy najlepszy wektor oceny: [{string.Join(", ", _bestVec)}], prefiks={_prefx}, obsadzonych={_obs}/{_days}, pustki={_blankCreditsUsed}/{_blankCreditsTotal}");
                }
            }
        }

        private (int[] assign, long[] vec) BuildGreedyIncumbent()
        {
            // Prosty rozbieg: MRV + ta sama logika legalności, ale bez kosztownego okna (tylko lokalne warunki).
            var assign = new int[_days]; Array.Fill(assign, UNASSIGNED);
            var work = new int[_docs];
            var mw = new int[_docs];
            int assigned = 0, obs = 0;

            bool IsValidLocal(int day, int p)
            {
                if (p < 0 || p >= _docs) return false;
                if (work[p] >= _limit[p]) return false;

                var av = (TypDostepnosci)_av[day, p];
                bool bc = av == TypDostepnosci.BardzoChce;

                if (!bc)
                {
                    if (day > 0 && assign[day - 1] == p) return false;
                    if (day + 1 < _days && assign[day + 1] == p) return false;
                    if (_nextToOther[day, p]) return false;
                }

                if (av == TypDostepnosci.Niedostepny || av == TypDostepnosci.Urlop || av == TypDostepnosci.DyzurInny) return false;
                if (av == TypDostepnosci.MogeWarunkowo && mw[p] >= 1) return false;

                if (av == TypDostepnosci.Moge || av == TypDostepnosci.MogeWarunkowo)
                {
                    int fut = 0;
                    int end = Math.Min(_days - 1, day + CH_RESERVE_HORIZON_DAYS);
                    for (int d = day + 1; d <= end; d++)
                    {
                        if (assign[d] != UNASSIGNED) continue;
                        var a2 = (TypDostepnosci)_av[d, p];
                        if (a2 != TypDostepnosci.Chce) continue;
                        if (work[p] >= _limit[p]) break;
                        if (d > 0 && assign[d - 1] == p) continue;
                        if (d + 1 < _days && assign[d + 1] == p) continue;
                        if (_nextToOther[d, p]) continue;
                        fut++;
                    }
                    if (fut > 0 && (_limit[p] - work[p] - 1) < fut) return false;
                }
                return true;
            }

            int NextByMRVLocal()
            {
                int best = -1, bestCnt = int.MaxValue, bestBC = int.MaxValue, bestCh = int.MaxValue, bestIndex = int.MaxValue;
                for (int d = 0; d < _days; d++)
                {
                    if (assign[d] != UNASSIGNED) continue;
                    int cnt = 0, bcCnt = 0, chCnt = 0;
                    foreach (var p in _staticCands[d])
                    {
                        if (IsValidLocal(d, p))
                        {
                            cnt++;
                            var av = (TypDostepnosci)_av[d, p];
                            if (av == TypDostepnosci.BardzoChce) bcCnt++;
                            else if (av == TypDostepnosci.Chce) chCnt++;
                        }
                    }
                    if (cnt == 0) cnt = int.MaxValue / 2;

                    bool better = (cnt < bestCnt)
                               || (cnt == bestCnt && bcCnt < bestBC)
                               || (cnt == bestCnt && bcCnt == bestBC && chCnt < bestCh)
                               || (cnt == bestCnt && bcCnt == bestBC && chCnt == bestCh && d < bestIndex);

                    if (better) { best = d; bestCnt = cnt; bestBC = bcCnt; bestCh = chCnt; bestIndex = d; }
                }
                return best >= 0 ? best : 0;
            }

            while (assigned < _days)
            {
                _ct.ThrowIfCancellationRequested();

                // Na rozbiegu nie forsujemy ciągłości – wybór MRV
                int day = NextByMRVLocal();
                if (day < 0) break;

                var legal = new List<int>();
                foreach (var p in _staticCands[day]) if (IsValidLocal(day, p)) legal.Add(p);

                if (legal.Count > 0)
                {
                    legal.Sort((a, b) =>
                    {
                        int ra = PrefRank((TypDostepnosci)_av[day, a]);
                        int rb = PrefRank((TypDostepnosci)_av[day, b]);
                        if (ra != rb) return rb.CompareTo(ra);

                        int fca = FutureChceFeasibleCount(day, a, CH_RESERVE_HORIZON_DAYS);
                        int fcb = FutureChceFeasibleCount(day, b, CH_RESERVE_HORIZON_DAYS);
                        if (fca != fcb) return fca.CompareTo(fcb);

                        int wa = work[a], wb = work[b];
                        if (wa != wb) return wa.CompareTo(wb);
                        return a.CompareTo(b);
                    });

                    int p = legal[0];
                    assign[day] = p; assigned++; obs++;
                    if ((TypDostepnosci)_av[day, p] == TypDostepnosci.MogeWarunkowo) mw[p]++;
                    work[p]++;
                }
                else
                {
                    assign[day] = -1; assigned++;
                }
            }

            var r = new RozwiazanyGrafik();
            for (int d = 0; d < _days; d++)
            {
                var date = _in.DniWMiesiacu[d];
                r.Przypisania[date] = (assign[d] >= 0 ? _docsMap[assign[d]] : null);
            }
            var vec = EvaluationAndScoringService.ToIntVector(r, _prio);
            SolverDiagnostics.Log($"Greedy incumbent: wektor= [{string.Join(", ", vec)}], obs={obs}/{_days}");
            return (assign, vec);
        }

        private static bool LexGreater(long[] a, long[] b)
        {
            for (int i = 0; i < a.Length; i++) { if (a[i] != b[i]) return a[i] > b[i]; }
            return false;
        }

        private static int PrefRank(TypDostepnosci a) => a switch
        {
            TypDostepnosci.Rezerwacja => 5,
            TypDostepnosci.BardzoChce => 4,
            TypDostepnosci.Chce => 3,
            TypDostepnosci.Moge => 2,
            TypDostepnosci.MogeWarunkowo => 1,
            _ => 0
        };

        private sealed class Dinic
        {
            private sealed class Edge
            {
                public int To, Rev;
                public int Cap;
                public Edge(int to, int rev, int cap) { To = to; Rev = rev; Cap = cap; }
            }

            private readonly List<List<Edge>> _g = new();
            private int[] _level = Array.Empty<int>();
            private int[] _it = Array.Empty<int>();

            public int AddNode() { _g.Add(new List<Edge>()); return _g.Count - 1; }

            public void AddEdge(int u, int v, int cap)
            {
                var e1 = new Edge(v, _g[v].Count, cap);
                var e2 = new Edge(u, _g[u].Count, 0);
                _g[u].Add(e1);
                _g[v].Add(e2);
            }

            private bool Bfs(int s, int t)
            {
                int n = _g.Count;
                _level = new int[n];
                Array.Fill(_level, -1);
                var q = new Queue<int>();
                _level[s] = 0; q.Enqueue(s);
                while (q.Count > 0)
                {
                    int u = q.Dequeue();
                    foreach (var e in _g[u])
                    {
                        if (e.Cap <= 0 || _level[e.To] >= 0) continue;
                        _level[e.To] = _level[u] + 1;
                        q.Enqueue(e.To);
                    }
                }
                return _level[t] >= 0;
            }

            private int Dfs(int u, int t, int f)
            {
                if (u == t) return f;
                for (; _it[u] < _g[u].Count; _it[u]++)
                {
                    var e = _g[u][_it[u]];
                    if (e.Cap <= 0 || _level[e.To] != _level[u] + 1) continue;
                    int got = Dfs(e.To, t, Math.Min(f, e.Cap));
                    if (got <= 0) continue;
                    e.Cap -= got;
                    _g[e.To][e.Rev].Cap += got;
                    return got;
                }
                return 0;
            }

            public int MaxFlow(int s, int t, int limit = int.MaxValue)
            {
                int flow = 0;
                while (flow < limit && Bfs(s, t))
                {
                    _it = new int[_g.Count];
                    int f;
                    while (flow < limit && (f = Dfs(s, t, limit - flow)) > 0) flow += f;
                }
                return flow;
            }
        }

        // ===== LOGOWANIE (wejście, kandydaci, itp.) =====

        private void LogHeaderInput()
        {
            if (!SolverDiagnostics.Enabled) return;

            try
            {
                SolverDiagnostics.Log("=== Start BacktrackingSolver ===");
                SolverDiagnostics.Log($"Dni w miesiącu: {_days}, lekarze: {_docs}");
                SolverDiagnostics.Log($"Priorytety: {SolverDiagnostics.JoinInline(_prio.Select(p => p.ToString()))}");

                var limLines = new List<string>();
                for (int p = 0; p < _docs; p++)
                    limLines.Add($"{_docsMap[p].Symbol}: limit={_limit[p]}");
                SolverDiagnostics.LogBlock("Limity lekarzy", limLines);

                SolverDiagnostics.Log("Legenda deklaracji: BC=BardzoChce, CH=Chce, MG=Moge, MW=MogeWarunkowo, RZ=Rezerwacja, --=brak/wykluczone");

                var lines = new List<string>(_days);
                for (int d = 0; d < _days; d++)
                {
                    var date = _in.DniWMiesiacu[d];
                    var parts = new List<string>(_docs);
                    for (int p = 0; p < _docs; p++)
                    {
                        var code = Code((TypDostepnosci)_av[d, p]);
                        parts.Add($"{_docsMap[p].Symbol}:{code}");
                    }
                    lines.Add($"{date:yyyy-MM-dd} | {string.Join(", ", parts)}");
                }
                SolverDiagnostics.LogBlock("Deklaracje (dzień → lekarz:deklaracja)", lines);
            }
            catch { }

            static string Code(TypDostepnosci t) => t switch
            {
                TypDostepnosci.BardzoChce => "BC",
                TypDostepnosci.Chce => "CH",
                TypDostepnosci.Moge => "MG",
                TypDostepnosci.MogeWarunkowo => "MW",
                TypDostepnosci.Rezerwacja => "RZ",
                TypDostepnosci.Urlop => "--",
                TypDostepnosci.Niedostepny => "--",
                TypDostepnosci.DyzurInny => "--",
                _ => "--"
            };
        }

        private void LogCandidatesWithReasons(int day, List<int> legal, bool continuityActive)
        {
            if (!SolverDiagnostics.Enabled) return;

            try
            {
                var date = _in.DniWMiesiacu[day];
                var all = _staticCands[day];

                var lines = new List<string>();
                lines.Add($"Dzień {date:yyyy-MM-dd} (prefiks={_prefx}, ciągłość={(continuityActive ? "TAK" : "NIE")}) – kandydaci:");

                foreach (var p in all)
                {
                    var pref = (TypDostepnosci)_av[day, p];
                    if (legal.Contains(p))
                    {
                        lines.Add($"  ✓ {FmtDoc(p)} [{FmtPref(pref)}]  (pracuje={_work[p]}, limit={_limit[p]}, MWused={_mwCount[p]})");
                    }
                    else
                    {
                        var reason = GetRejectionReason(day, p, continuityActive);
                        lines.Add($"  ✗ {FmtDoc(p)} [{FmtPref(pref)}]  → {reason}");
                    }
                }

                if (legal.Count == 0) lines.Add("  (brak legalnych kandydatów)");

                SolverDiagnostics.LogBlock($"Kandydaci dnia {date:yyyy-MM-dd}", lines);
            }
            catch { }
        }

        private string GetRejectionReason(int day, int p, bool continuityActive)
        {
            var reasons = new List<string>();

            if (p < 0 || p >= _docs) return "błędny indeks lekarza";
            if (_work[p] >= _limit[p]) reasons.Add("limit wyczerpany");

            var av = (TypDostepnosci)_av[day, p];
            bool bc = av == TypDostepnosci.BardzoChce;

            if (av == TypDostepnosci.Niedostepny || av == TypDostepnosci.Urlop || av == TypDostepnosci.DyzurInny)
                reasons.Add("deklaracja wyklucza dzień");

            if (!bc)
            {
                if (day > 0 && _assign[day - 1] == p) reasons.Add("dzień-1 ten sam lekarz");
                if (day + 1 < _days && _assign[day + 1] == p) reasons.Add("dzień+1 ten sam lekarz");
                if (_nextToOther[day, p]) reasons.Add("sąsiedztwo „Dyżur (inny)”");
            }

            if (av == TypDostepnosci.MogeWarunkowo && _mwCount[p] >= 1) reasons.Add("limit MW=1 już wykorzystany");

            if (av == TypDostepnosci.Moge || av == TypDostepnosci.MogeWarunkowo)
            {
                if (UniqueFutureFeasibleExists(day, p)) reasons.Add("jest przyszły dzień, gdzie tylko ten lekarz jest realny");
                int needChce = FutureChceFeasibleCount(day, p, CH_RESERVE_HORIZON_DAYS);
                int remainingAfter = _limit[p] - _work[p] - 1;
                if (needChce > 0 && remainingAfter < needChce) reasons.Add($"braknie limitu na przyszłe CH (horyzont {CH_RESERVE_HORIZON_DAYS} dni, potrzebne={needChce})");
            }

            if (reasons.Count == 0) reasons.Add("odpadł w innej weryfikacji");

            return string.Join("; ", reasons);
        }

        private string FmtDay(int d) => d >= 0 && d < _days ? _in.DniWMiesiacu[d].ToString("yyyy-MM-dd") : $"d={d}";
        private string FmtDoc(int p) => $"{_docsMap[p].Symbol}";
        private string FmtPref(TypDostepnosci t) => t switch
        {
            TypDostepnosci.BardzoChce => "BC",
            TypDostepnosci.Chce => "CH",
            TypDostepnosci.Moge => "MG",
            TypDostepnosci.MogeWarunkowo => "MW",
            TypDostepnosci.Rezerwacja => "RZ",
            _ => "--"
        };
    }
}
