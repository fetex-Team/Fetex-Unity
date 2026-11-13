using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace Fetex
{
    /// <summary>
    /// 리플레이 HUD (UI Toolkit, HUD.uxml/HUD.uss). FetexReplayPlayer·TwinCameraView의 상태를 보여주고 조작을 넘긴다.
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class FetexHud : MonoBehaviour
    {
        const string Selected = "is-selected";
        const string Hidden = "is-hidden";
        const string On = "is-on";
        const float RefreshInterval = 0.1f;

        static FetexHud instance;

        static readonly (string button, string file, string detail)[] Strategies =
        {
            ("strategyRl", "rl_prepositioned.json", "PPO 강화학습 선제 재배치 + 헝가리안 배차"),
            ("strategyStatic", "prepositioned.json", "수요 핫스팟 정적 선제 배치 + 헝가리안 배차"),
            ("strategyPatrol", "patrol.json", "일반 순찰 + 헝가리안 배차 (비교 기준)"),
        };
        static readonly float[] Speeds = { 1f, 2f, 5f, 10f, 25f, 50f };
        static readonly string[] StateNames = { "빈 차 대기", "픽업 이동", "승객 탑승", "AI 재배치" };
        static readonly string[] StateColors = { "#26F240", "#FFD91A", "#F23333", "#0DA6FF" };

        VisualElement root;
        readonly Button[] strategyButtons = new Button[Strategies.Length];
        readonly Button[] speedButtons = new Button[Speeds.Length];
        Label strategyDetail, clockLabel, elapsedLabel, timeLabel, endTimeLabel, taxiLabel, hintLabel, fleetMeta;
        Label kpiFulfill, kpiFulfillSub, kpiWait, kpiWaiting, kpiWaitingSub, kpiMoves;
        readonly Label[] stateCounts = new Label[4];
        readonly VisualElement[] fleetSegs = new VisualElement[4];
        Label personCount;
        VisualElement progressFill, timeline, timelineFill, timelineKnob, playGlyph, pauseIcon, taxiNav;
        Button chipH3, chipBuildings, viewOverview, viewFollow;
        float nextRefresh;

        // 전략 비교 카드 (행 순서 = Strategies 순서)
        readonly VisualElement[] cmpRows = new VisualElement[Strategies.Length];
        readonly Label[,] cmpCells = new Label[Strategies.Length, 4];   // 이행률, 평균 대기, 대기 승객, 취소
        Label compareMeta;
        bool comparisonRequested;

        static FetexReplayPlayer Player => FetexReplayPlayer.Instance;
        static TwinCameraView Cam => Player != null ? Player.cameraView : null;

        void OnEnable()
        {
            instance = this;
            root = GetComponent<UIDocument>().rootVisualElement;
            root.pickingMode = PickingMode.Ignore;   // 빈 화면은 카메라 조작이 받도록

            for (int i = 0; i < Strategies.Length; i++)
            {
                string file = Strategies[i].file;
                strategyButtons[i] = root.Q<Button>(Strategies[i].button);
                strategyButtons[i].clicked += () => { if (Player != null) Player.LoadMapAndReplay(file); };
            }
            for (int i = 0; i < Speeds.Length; i++)
            {
                float s = Speeds[i];
                speedButtons[i] = root.Q<Button>("speed" + (int)s);
                speedButtons[i].clicked += () => { if (Player != null) Player.playbackSpeed = s; };
            }

            strategyDetail = root.Q<Label>("strategyDetail");
            clockLabel = root.Q<Label>("clockLabel");
            elapsedLabel = root.Q<Label>("elapsedLabel");
            progressFill = root.Q("progressFill");
            kpiFulfill = root.Q<Label>("kpiFulfill");
            kpiFulfillSub = root.Q<Label>("kpiFulfillSub");
            kpiWait = root.Q<Label>("kpiWait");
            kpiWaiting = root.Q<Label>("kpiWaiting");
            kpiWaitingSub = root.Q<Label>("kpiWaitingSub");
            kpiMoves = root.Q<Label>("kpiMoves");
            fleetMeta = root.Q<Label>("fleetMeta");
            string[] keys = { "Idle", "Pickup", "Occupied", "Reposition" };
            for (int i = 0; i < 4; i++)
            {
                stateCounts[i] = root.Q<Label>("count" + keys[i]);
                fleetSegs[i] = root.Q("seg" + keys[i]);
            }
            personCount = root.Q<Label>("countPerson");

            chipH3 = root.Q<Button>("chipH3");
            chipH3.clicked += () => { if (Player != null) Player.SetH3Visible(!Player.showH3Cells); };
            chipBuildings = root.Q<Button>("chipBuildings");
            chipBuildings.clicked += () => { if (Player != null) Player.SetBuildingsVisible(!Player.showGangnamBuildings); };

            var playButton = root.Q<Button>("playButton");
            playButton.clicked += TogglePlay;
            playGlyph = root.Q("playGlyph");
            pauseIcon = root.Q("pauseIcon");

            timeLabel = root.Q<Label>("timeLabel");
            endTimeLabel = root.Q<Label>("endTimeLabel");
            timeline = root.Q("timeline");
            timelineFill = root.Q("timelineFill");
            timelineKnob = root.Q("timelineKnob");
            timeline.RegisterCallback<PointerDownEvent>(OnTimelineDown);
            timeline.RegisterCallback<PointerMoveEvent>(OnTimelineMove);
            timeline.RegisterCallback<PointerUpEvent>(OnTimelineUp);

            viewOverview = root.Q<Button>("viewOverview");
            viewOverview.clicked += () => { if (Cam != null) Cam.overview = true; };
            viewFollow = root.Q<Button>("viewFollow");
            viewFollow.clicked += () =>
            {
                if (Cam == null) return;
                if (Cam.target == null) Player.SelectFirstTaxiForCamera();
                Cam.overview = false;
            };
            taxiNav = root.Q("taxiNav");
            taxiLabel = root.Q<Label>("taxiLabel");
            root.Q<Button>("prevTaxi").clicked += () => { if (Player != null) Player.SelectTaxiForCamera(-1); };
            root.Q<Button>("nextTaxi").clicked += () => { if (Player != null) Player.SelectTaxiForCamera(1); };
            hintLabel = root.Q<Label>("hintLabel");

            string[] cmpKeys = { "cmpFulfill", "cmpWait", "cmpWaiting", "cmpCancel" };
            for (int i = 0; i < Strategies.Length; i++)
            {
                string file = Strategies[i].file;
                cmpRows[i] = root.Q("cmpRow" + i);
                cmpRows[i].RegisterCallback<ClickEvent>(_ => { if (Player != null && Player.replayFileName != file) Player.LoadMapAndReplay(file); });
                for (int c = 0; c < cmpKeys.Length; c++) cmpCells[i, c] = root.Q<Label>(cmpKeys[c] + i);
            }
            compareMeta = root.Q<Label>("compareMeta");

            // 버튼이 키보드 포커스를 가져가면 Space가 버튼 클릭으로도 들어가 재생이 두 번 토글된다
            root.Query<Button>().ForEach(b => b.focusable = false);
        }

        void OnDisable()
        {
            if (instance == this) instance = null;
        }

        /// <summary>화면 좌표(좌하단 원점)가 HUD 패널 위인지. 카메라 드래그/휠이 HUD 조작과 겹치지 않게 한다.</summary>
        public static bool IsPointerOverUi(Vector2 screenPos)
        {
            if (instance == null || instance.root == null || instance.root.panel == null) return false;
            var panel = instance.root.panel;
            Vector2 p = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(screenPos.x, Screen.height - screenPos.y));
            return panel.Pick(p) != null;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame) TogglePlay();

            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshInterval;
            Refresh();
        }

        void TogglePlay()
        {
            var p = Player;
            if (p == null || !p.isLoaded) return;
            if (!p.isPlaying && p.currentTime >= p.Duration - 0.01f) p.Seek(0f);   // 끝에서 누르면 처음부터
            p.isPlaying = !p.isPlaying;
            Refresh();
        }

        void Refresh()
        {
            var p = Player;
            if (p == null) return;

            for (int i = 0; i < Strategies.Length; i++)
            {
                bool sel = p.replayFileName == Strategies[i].file;
                strategyButtons[i].EnableInClassList(Selected, sel);
                if (sel) strategyDetail.text = Strategies[i].detail;
            }
            for (int i = 0; i < Speeds.Length; i++) speedButtons[i].EnableInClassList(Selected, Mathf.Approximately(p.playbackSpeed, Speeds[i]));
            chipH3.EnableInClassList(On, p.showH3Cells);
            chipBuildings.EnableInClassList(On, p.showGangnamBuildings);
            playGlyph.EnableInClassList(Hidden, p.isPlaying);
            pauseIcon.EnableInClassList(Hidden, !p.isPlaying);

            bool overview = Cam == null || Cam.overview;
            viewOverview.EnableInClassList(Selected, overview);
            viewFollow.EnableInClassList(Selected, !overview);
            taxiNav.EnableInClassList(Hidden, overview);
            hintLabel.EnableInClassList(Hidden, !overview);

            if (!p.isLoaded)
            {
                elapsedLabel.text = "리플레이 불러오는 중…";
                return;
            }

            float dur = Mathf.Max(1f, p.Duration);
            float t = p.currentTime;
            float frac = Mathf.Clamp01(t / dur);
            clockLabel.text = p.CurrentClockString;
            elapsedLabel.text = $"{FormatSpan(t)} 경과  ·  전체 {FormatSpan(dur)}";
            progressFill.style.width = Length.Percent(frac * 100f);
            timeLabel.text = p.ClockAt(t, false);
            endTimeLabel.text = p.ClockAt(dur, false);
            timelineFill.style.width = Length.Percent(frac * 100f);
            timelineKnob.style.left = Length.Percent(frac * 100f);

            p.GetServiceKpis(out int requested, out int picked, out float avgWait, out int waiting, out int moves, out int timedOut);
            float rate = requested > 0 ? picked * 100f / requested : 100f;
            kpiFulfill.text = $"{rate:F1}<size=15>%</size>";
            kpiFulfillSub.text = $"호출 {requested:N0} · 탑승 {picked:N0}";
            kpiWait.text = FormatWait(avgWait);
            kpiWaiting.text = $"{waiting:N0}<size=15>명</size>";
            kpiWaitingSub.text = timedOut > 0 ? $"취소·시간초과 누적 {timedOut:N0}명" : "취소·시간초과 없음";
            kpiMoves.text = $"{moves:N0}<size=15>건</size>";

            int total = 0;
            for (int i = 0; i < 4; i++)
            {
                int c = p.StateCount(i);
                total += c;
                stateCounts[i].text = c.ToString("N0");
                fleetSegs[i].style.flexGrow = c;
            }
            fleetMeta.text = $"운행 중 {total:N0}대";
            personCount.text = p.WaitingPeople.ToString("N0");

            RefreshComparison(p, t);

            if (!overview)
            {
                if (p.TryGetFollowedTaxi(out string taxiId, out int index, out int count, out int state))
                {
                    string dot = state >= 0 && state < 4 ? $"<color={StateColors[state]}>●</color> " : "";
                    string name = state >= 0 && state < 4 ? $" · {StateNames[state]}" : "";
                    // 운행 중 택시 가운데 몇 번째인지 (전체 등록 대수가 아니라 지금 도로 위 대수 기준)
                    taxiLabel.text = $"{dot}{TaxiNumber(taxiId)}{name}  <color=#808B9C>{index + 1}/{count}</color>";
                }
                else
                {
                    taxiLabel.text = "운행 중인 택시 없음";
                }
            }
        }

        // ---------- 전략 비교 ----------
        readonly StrategyKpiSeries.Kpi[] cmpKpi = new StrategyKpiSeries.Kpi[Strategies.Length];
        readonly bool[] cmpHas = new bool[Strategies.Length];

        void RefreshComparison(FetexReplayPlayer p, float t)
        {
            if (!comparisonRequested)
            {
                comparisonRequested = true;
                var files = new string[Strategies.Length];
                for (int i = 0; i < files.Length; i++) files[i] = Strategies[i].file;
                p.RequestComparison(files);
            }

            bool allSame = true;
            int ready = 0;
            for (int i = 0; i < Strategies.Length; i++)
            {
                cmpHas[i] = p.TryGetStrategyKpi(Strategies[i].file, t, out cmpKpi[i], out bool same);
                if (cmpHas[i]) { ready++; allSame &= same; }
                cmpRows[i].EnableInClassList("is-current", p.replayFileName == Strategies[i].file);
            }
            compareMeta.text = ready < Strategies.Length ? $"불러오는 중 {ready}/{Strategies.Length}"
                             : allSame ? $"{p.ClockAt(t, false)} 기준" : "주의: 시나리오가 다른 파일 포함";

            // 열마다 가장 좋은 값(이행률 최대, 나머지 최소)을 초록으로. 값이 모두 같으면 표시하지 않는다.
            for (int c = 0; c < 4; c++)
            {
                float best = float.NaN, worst = float.NaN;
                for (int i = 0; i < Strategies.Length; i++)
                {
                    if (!cmpHas[i]) continue;
                    float v = Score(cmpKpi[i], c);
                    if (float.IsNaN(best) || v > best) best = v;
                    if (float.IsNaN(worst) || v < worst) worst = v;
                }
                for (int i = 0; i < Strategies.Length; i++)
                {
                    Label cell = cmpCells[i, c];
                    if (!cmpHas[i]) { cell.text = "…"; cell.RemoveFromClassList("is-best"); continue; }
                    cell.text = Format(cmpKpi[i], c);
                    cell.EnableInClassList("is-best", ready > 1 && best > worst && Mathf.Approximately(Score(cmpKpi[i], c), best));
                }
            }
        }

        // 클수록 좋은 점수로 변환 (이행률은 그대로, 대기·대기 승객·취소는 음수)
        static float Score(StrategyKpiSeries.Kpi k, int column)
        {
            switch (column)
            {
                case 0: return k.FulfillRate;
                case 1: return -k.AvgWait;
                case 2: return -k.Waiting;
                default: return -k.TimedOut;
            }
        }

        static string Format(StrategyKpiSeries.Kpi k, int column)
        {
            switch (column)
            {
                case 0: return $"{k.FulfillRate:F1}%";
                case 1: return k.AvgWait < 60f ? $"{k.AvgWait:F0}초" : $"{Mathf.FloorToInt(k.AvgWait / 60f)}분 {Mathf.RoundToInt(k.AvgWait % 60f)}초";
                case 2: return $"{k.Waiting:N0}명";
                default: return $"{k.TimedOut:N0}명";
            }
        }

        // ---------- 타임라인 드래그 ----------
        void OnTimelineDown(PointerDownEvent e)
        {
            timeline.CapturePointer(e.pointerId);
            SeekTo(e.localPosition.x);
        }

        void OnTimelineMove(PointerMoveEvent e)
        {
            if (timeline.HasPointerCapture(e.pointerId)) SeekTo(e.localPosition.x);
        }

        void OnTimelineUp(PointerUpEvent e)
        {
            if (timeline.HasPointerCapture(e.pointerId)) timeline.ReleasePointer(e.pointerId);
        }

        void SeekTo(float localX)
        {
            var p = Player;
            float w = timeline.resolvedStyle.width;
            if (p == null || !p.isLoaded || w <= 0f) return;
            p.Seek(Mathf.Clamp01(localX / w) * p.Duration);
            Refresh();
        }

        // ---------- 표기 ----------
        // "taxi_19" → "19번 택시"
        static string TaxiNumber(string id)
        {
            if (string.IsNullOrEmpty(id)) return "택시";
            int us = id.LastIndexOf('_');
            return us >= 0 && us < id.Length - 1 ? $"{id.Substring(us + 1)}번 택시" : id;
        }

        static string FormatWait(float sec)
        {
            if (sec < 60f) return $"{sec:F0}<size=15>초</size>";
            int m = Mathf.FloorToInt(sec / 60f);
            int s = Mathf.RoundToInt(sec - m * 60f);
            return $"{m}<size=15>분</size> {s}<size=15>초</size>";
        }

        static string FormatSpan(float sec)
        {
            int h = Mathf.FloorToInt(sec / 3600f);
            int m = Mathf.FloorToInt(sec % 3600f / 60f);
            return h > 0 ? $"{h}시간 {m}분" : $"{m}분";
        }
    }
}
