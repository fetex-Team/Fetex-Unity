using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fetex
{
    /// <summary>
    /// map.json(SUMO 차선·교차로)으로 강남 도시를 런타임 생성한다.
    /// - 도로: 차선 띠 + 교차로 다각형을 아스팔트 PBR(월드 UV)로, 차선 사이 점선/중앙선/가장자리선 도색
    /// - 보도: 도로 바깥 경계에 연석+보도블록, 주요 교차로에 횡단보도·정지선·신호등
    /// - 가로등·가로수: 보도를 따라 일정 간격
    /// - 건물: CityAssetLibrary의 판교 건물을 가로변에 도로 방향으로 정렬해 배치, 남는 블록 안쪽을 채움
    /// map.json에는 건물 정보가 없으므로 건물 위치·종류는 결정론적 랜덤(seed 고정)이다.
    /// </summary>
    public class GangnamCityBuilder
    {
        // ---------- 결과 ----------
        public GameObject RoadRoot { get; private set; }   // 도로·도색·보도·신호등·가로등
        public GameObject CityRoot { get; private set; }   // 지면·건물·가로수
        public int BuildingCount { get; private set; }

        // ---------- 입력 ----------
        private readonly MapData map;
        private readonly CityAssetLibrary lib;
        private readonly float groundY;
        private readonly Material fallbackRoad;
        private readonly Material fallbackGround;
        private readonly System.Random rng = new System.Random(42);

        // 높이 층: 지면 < 아스팔트 < 도색 < 보도
        private float YGround => groundY - 0.02f;
        private float YRoad => groundY + 0.01f;
        private float YPaint => groundY + 0.025f;
        private float YWalk => groundY + 0.16f;

        private const float LaneHalfWidthDefault = 1.6f;

        private class Lane
        {
            public string id;
            public string edge;
            public int index;          // SUMO 차선 번호 (0 = 가장 오른쪽)
            public int edgeLanes;      // 같은 도로(edge)의 차선 수
            public Vector2[] p;
            public float[] cum;        // 누적 길이
            public float hw;
            public float Length => cum[cum.Length - 1];
        }

        private struct WalkSample
        {
            public Vector2 boundary;   // 차로 바깥 경계(연석) 위치
            public Vector2 outward;    // 보도 쪽 단위 법선
            public Vector2 along;      // 차선 진행 방향
            public float s;            // 차선 위 누적 거리
        }

        private readonly List<Lane> lanes = new List<Lane>();
        private float maxLaneHalfWidth = LaneHalfWidthDefault;

        // 건물 배치: 차선 중심선에서 건물 바닥(+여유)까지 최소 거리 = 차로 반폭 + 보도 + 0.8m
        private const float BuildingGap = 0.8f;     // 건물끼리·건물과 보도 사이 여유 (강남 가로처럼 거의 붙여 세움)
        // 자리가 모자라면 같은 건물을 줄여서라도 채운다 (0.65배 ≈ 판교 최저층 33m → 약 21m, 6~7층)
        private static readonly float[] FitScales = { 1f, 1f, 1f, 0.8f, 0.8f, 0.65f };
        private float BuildingClear => maxLaneHalfWidth + (lib != null ? lib.sidewalkWidth : 3.5f) + 0.8f;
        private readonly SegmentGrid laneGrid = new SegmentGrid(16f);
        private readonly List<Vector2[]> junctions = new List<Vector2[]>();
        private readonly List<Rect> junctionRects = new List<Rect>();
        private readonly Dictionary<long, List<int>> junctionCells = new Dictionary<long, List<int>>();
        private const float JunctionCell = 32f;

        private readonly Dictionary<(int lane, int side), List<WalkSample>> walks = new Dictionary<(int, int), List<WalkSample>>();
        private readonly HashSet<long> propCells = new HashSet<long>();
        private readonly HashSet<long> buildingCells = new HashSet<long>();     // 건물 바닥 + 여유 (배치 충돌 검사용)
        private readonly HashSet<long> footprintCells = new HashSet<long>();    // 건물 바닥만 (밀도 측정용)
        private readonly List<Vector2> signalPositions = new List<Vector2>();

        // 차선 시작/끝이 신호 교차로(연결 도로 5개 이상)에 닿는지. 횡단보도·정지선·신호등과 도색 생략 구간에 쓴다.
        private int[] laneStartJ, laneEndJ;
        private bool[] startsAtMajor, endsAtMajor;
        private readonly Dictionary<int, HashSet<string>> edgesAtJunction = new Dictionary<int, HashSet<string>>();
        private const int MajorJunctionEdges = 5;
        private const float MinCrosswalkLaneLength = 6f;     // 짧은 차선도 횡단보도는 그려야 도로 전체 폭을 덮는다
        private const float StopLineBack = 6.0f;        // 교차로 경계에서 정지선까지
        private const float CrosswalkFar = 5.0f;        // 횡단보도 1~5m
        private const float NoChangeZone = 30f;         // 정지선 앞 진로변경 제한(실선) 구간
        private Rect mapRect;

        public GangnamCityBuilder(MapData map, CityAssetLibrary lib, float groundY, Material fallbackRoad, Material fallbackGround)
        {
            this.map = map;
            this.lib = lib;
            this.groundY = groundY;
            this.fallbackRoad = fallbackRoad;
            this.fallbackGround = fallbackGround;
        }

        public void Build(Transform parent)
        {
            RoadRoot = new GameObject("[GangnamRoadNetwork]");
            RoadRoot.transform.SetParent(parent, false);
            CityRoot = new GameObject("[GangnamBuildings]");
            CityRoot.transform.SetParent(parent, false);

            PrepareLanes();
            PrepareJunctions();
            ClassifyLaneEnds();

            BuildRoadSurface();
            BuildMarkingsAndSidewalks();
            BuildGround();

            if (lib == null)
            {
                Debug.LogWarning("[GangnamCityBuilder] CityAssetLibrary 없음 → 건물·가로시설물 생략 (메뉴 Fetex ▸ City ▸ Rebuild City Asset Library)");
                return;
            }

            BuildIntersections();
            BuildStreetProps();
            BuildBuildings();
        }

        // =====================================================================
        // 데이터 준비
        // =====================================================================
        private void PrepareLanes()
        {
            var edgeOf = new Dictionary<string, string>();
            if (map.lanes != null)
            {
                foreach (var l in map.lanes)
                {
                    if (l != null && l.id != null && l.e != null) edgeOf[l.id] = l.e;
                }
            }

            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            var edgeLaneCount = new Dictionary<string, int>();

            foreach (var road in map.roads)
            {
                if (road.points == null || road.points.Count < 2) continue;
                var pts = new List<Vector2>(road.points.Count);
                foreach (var pt in road.points)
                {
                    var v = new Vector2(pt.x, pt.z);
                    if (pts.Count == 0 || (v - pts[pts.Count - 1]).sqrMagnitude > 0.0001f) pts.Add(v);
                    minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                    minZ = Mathf.Min(minZ, v.y); maxZ = Mathf.Max(maxZ, v.y);
                }
                if (pts.Count < 2) continue;

                string id = road.id ?? lanes.Count.ToString();
                int us = id.LastIndexOf('_');
                int index = 0;
                if (us >= 0) int.TryParse(id.Substring(us + 1), out index);
                if (!edgeOf.TryGetValue(id, out string edge)) edge = us > 0 ? id.Substring(0, us) : id;

                var lane = new Lane
                {
                    id = id,
                    edge = edge,
                    index = index,
                    p = pts.ToArray(),
                    hw = road.width > 0.5f ? road.width * 0.5f : LaneHalfWidthDefault,
                };
                lane.cum = new float[lane.p.Length];
                for (int i = 1; i < lane.p.Length; i++) lane.cum[i] = lane.cum[i - 1] + Vector2.Distance(lane.p[i - 1], lane.p[i]);

                int li = lanes.Count;
                lanes.Add(lane);
                maxLaneHalfWidth = Mathf.Max(maxLaneHalfWidth, lane.hw);
                for (int i = 0; i < lane.p.Length - 1; i++) laneGrid.Add(lane.p[i], lane.p[i + 1], li);

                edgeLaneCount.TryGetValue(edge, out int c);
                edgeLaneCount[edge] = c + 1;
            }

            foreach (var l in lanes) l.edgeLanes = edgeLaneCount[l.edge];
            mapRect = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
        }

        private void PrepareJunctions()
        {
            if (map.junctions == null) return;
            foreach (var j in map.junctions)
            {
                if (j.points == null || j.points.Count < 3) continue;
                var pts = new List<Vector2>(j.points.Count);
                foreach (var pt in j.points)
                {
                    var v = new Vector2(pt.x, pt.z);
                    if (pts.Count == 0 || (v - pts[pts.Count - 1]).sqrMagnitude > 0.0001f) pts.Add(v);
                }
                if (pts.Count > 2 && (pts[0] - pts[pts.Count - 1]).sqrMagnitude < 0.0001f) pts.RemoveAt(pts.Count - 1);
                if (pts.Count < 3) continue;

                float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
                foreach (var v in pts)
                {
                    minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                    minZ = Mathf.Min(minZ, v.y); maxZ = Mathf.Max(maxZ, v.y);
                }
                int idx = junctions.Count;
                junctions.Add(pts.ToArray());
                var r = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
                junctionRects.Add(r);
                for (int cx = Mathf.FloorToInt(minX / JunctionCell); cx <= Mathf.FloorToInt(maxX / JunctionCell); cx++)
                {
                    for (int cz = Mathf.FloorToInt(minZ / JunctionCell); cz <= Mathf.FloorToInt(maxZ / JunctionCell); cz++)
                    {
                        long key = Key(cx, cz);
                        if (!junctionCells.TryGetValue(key, out var list)) junctionCells[key] = list = new List<int>();
                        list.Add(idx);
                    }
                }
            }
        }

        private int FindJunction(Vector2 p, float tolerance)
        {
            // 허용 거리만큼 이웃 셀도 본다 (셀 경계 바로 너머의 교차로를 놓치지 않게)
            int x0 = Mathf.FloorToInt((p.x - tolerance) / JunctionCell), x1 = Mathf.FloorToInt((p.x + tolerance) / JunctionCell);
            int z0 = Mathf.FloorToInt((p.y - tolerance) / JunctionCell), z1 = Mathf.FloorToInt((p.y + tolerance) / JunctionCell);
            int best = -1;
            float bestDist = tolerance;
            for (int cx = x0; cx <= x1; cx++)
            {
                for (int cz = z0; cz <= z1; cz++)
                {
                    if (!junctionCells.TryGetValue(Key(cx, cz), out var list)) continue;
                    foreach (int idx in list)
                    {
                        var r = junctionRects[idx];
                        if (p.x < r.xMin - tolerance || p.x > r.xMax + tolerance || p.y < r.yMin - tolerance || p.y > r.yMax + tolerance) continue;
                        var poly = junctions[idx];
                        if (PointInPolygon(p, poly)) return idx;
                        if (tolerance <= 0f) continue;
                        for (int i = 0; i < poly.Length; i++)
                        {
                            float d = SegmentGrid.DistanceToSegment(p, poly[i], poly[(i + 1) % poly.Length]);
                            if (d < bestDist) { bestDist = d; best = idx; }
                        }
                    }
                }
            }
            return best;
        }

        private bool InJunction(Vector2 p) => FindJunction(p, 0f) >= 0;

        private int[] junctionCluster;   // 교차로 다각형 → 하나의 실제 교차로(클러스터) 번호

        private void ClassifyLaneEnds()
        {
            // SUMO는 큰 교차로를 여러 노드(다각형)로 쪼개는 경우가 많다. 가까운 다각형끼리 묶어 한 교차로로 센다.
            int jn = junctions.Count;
            junctionCluster = new int[jn];
            for (int a = 0; a < jn; a++) junctionCluster[a] = a;
            int Root(int x) { while (junctionCluster[x] != x) x = junctionCluster[x] = junctionCluster[junctionCluster[x]]; return x; }
            const float mergeGap = 6f;
            for (int a = 0; a < jn; a++)
            {
                Rect ra = junctionRects[a];
                Rect grown = Rect.MinMaxRect(ra.xMin - mergeGap, ra.yMin - mergeGap, ra.xMax + mergeGap, ra.yMax + mergeGap);
                for (int b = a + 1; b < jn; b++)
                {
                    if (!grown.Overlaps(junctionRects[b])) continue;
                    if (PolygonGap(junctions[a], junctions[b]) > mergeGap) continue;
                    int ra2 = Root(a), rb2 = Root(b);
                    if (ra2 != rb2) junctionCluster[rb2] = ra2;
                }
            }
            for (int a = 0; a < jn; a++) junctionCluster[a] = Root(a);

            int n = lanes.Count;
            laneStartJ = new int[n];
            laneEndJ = new int[n];
            startsAtMajor = new bool[n];
            endsAtMajor = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var l = lanes[i];
                laneStartJ[i] = FindJunction(l.p[0], 2.5f);
                laneEndJ[i] = FindJunction(l.p[l.p.Length - 1], 2.5f);
                foreach (int j in new[] { laneStartJ[i], laneEndJ[i] })
                {
                    if (j < 0) continue;
                    int c = junctionCluster[j];
                    if (!edgesAtJunction.TryGetValue(c, out var set)) edgesAtJunction[c] = set = new HashSet<string>();
                    set.Add(l.edge);
                }
            }
            for (int i = 0; i < n; i++)
            {
                bool longEnough = lanes[i].Length >= MinCrosswalkLaneLength;
                startsAtMajor[i] = longEnough && IsMajor(laneStartJ[i]);
                endsAtMajor[i] = longEnough && IsMajor(laneEndJ[i]);
            }
        }

        private bool IsMajor(int j) => j >= 0 && edgesAtJunction.TryGetValue(junctionCluster[j], out var s) && s.Count >= MajorJunctionEdges;

        // 두 다각형 사이 최소 거리 (꼭짓점-변 기준, 겹치면 0)
        private static float PolygonGap(Vector2[] a, Vector2[] b)
        {
            if (PointInPolygon(a[0], b) || PointInPolygon(b[0], a)) return 0f;
            float best = float.MaxValue;
            for (int i = 0; i < a.Length; i++)
                for (int k = 0; k < b.Length; k++)
                    best = Mathf.Min(best, SegmentGrid.DistanceToSegment(a[i], b[k], b[(k + 1) % b.Length]));
            for (int k = 0; k < b.Length; k++)
                for (int i = 0; i < a.Length; i++)
                    best = Mathf.Min(best, SegmentGrid.DistanceToSegment(b[k], a[i], a[(i + 1) % a.Length]));
            return best;
        }

        // =====================================================================
        // 도로 표면
        // =====================================================================
        private void BuildRoadSurface()
        {
            var buf = new MeshBuffer(1);
            float tile = lib != null ? Mathf.Max(0.5f, lib.asphaltTileMeters) : 8f;

            foreach (var lane in lanes)
            {
                int n = lane.p.Length;
                int baseIdx = buf.Count;
                float w = lane.hw + 0.05f;   // 인접 차선 사이 틈 방지
                for (int i = 0; i < n; i++)
                {
                    Vector2 m = MiterNormal(lane.p, i);
                    Vector2 l = lane.p[i] + m * w;
                    Vector2 r = lane.p[i] - m * w;
                    buf.Add(V3(l, YRoad), l / tile);
                    buf.Add(V3(r, YRoad), r / tile);
                }
                for (int i = 0; i < n - 1; i++)
                {
                    int a = baseIdx + i * 2;
                    buf.Quad(0, a, a + 1, a + 3, a + 2, Vector3.up);
                }
            }

            foreach (var poly in junctions)
            {
                int baseIdx = buf.Count;
                foreach (var v in poly) buf.Add(V3(v, YRoad), v / tile);
                var tris = Triangulate(poly);
                for (int t = 0; t < tris.Count; t += 3) buf.Tri(0, baseIdx + tris[t], baseIdx + tris[t + 1], baseIdx + tris[t + 2], Vector3.up);
            }

            Material asphalt = lib != null && lib.roadAsphalt != null ? lib.roadAsphalt : fallbackRoad;
            CreateMeshObject("RoadSurface", RoadRoot.transform, buf, new[] { asphalt }, ShadowCastingMode.Off);
        }

        private static Vector2 MiterNormal(Vector2[] p, int i)
        {
            int n = p.Length;
            Vector2 nPrev = i > 0 ? Left((p[i] - p[i - 1]).normalized) : Vector2.zero;
            Vector2 nNext = i < n - 1 ? Left((p[i + 1] - p[i]).normalized) : Vector2.zero;
            if (i == 0) return nNext;
            if (i == n - 1) return nPrev;
            Vector2 m = (nPrev + nNext);
            if (m.sqrMagnitude < 1e-6f) return nNext;
            m.Normalize();
            float cos = Mathf.Max(0.5f, Vector2.Dot(m, nNext));
            return m / cos;
        }

        // =====================================================================
        // 차선 도색 + 보도
        // =====================================================================
        private void BuildMarkingsAndSidewalks()
        {
            var paint = new MeshBuffer(2);      // 0 = 흰색, 1 = 노란색
            var walk = new MeshBuffer(2);       // 0 = 보도 상판, 1 = 연석
            float walkW = lib != null ? lib.sidewalkWidth : 3.5f;
            float walkTile = lib != null ? Mathf.Max(0.5f, lib.sidewalkTileMeters) : 3f;
            const float chunk = 2f;
            const float lineW = 0.15f;

            for (int li = 0; li < lanes.Count; li++)
            {
                Lane lane = lanes[li];
                var leftWalk = new List<WalkSample>();
                var rightWalk = new List<WalkSample>();

                for (int k = 0; k < lane.p.Length - 1; k++)
                {
                    Vector2 a = lane.p[k];
                    Vector2 b = lane.p[k + 1];
                    float segLen = lane.cum[k + 1] - lane.cum[k];
                    if (segLen < 0.01f) continue;
                    Vector2 d = (b - a) / segLen;
                    Vector2 nl = Left(d);
                    int steps = Mathf.Max(1, Mathf.CeilToInt(segLen / chunk));

                    for (int j = 0; j < steps; j++)
                    {
                        float s0 = segLen * j / steps;
                        float s1 = segLen * (j + 1) / steps;
                        Vector2 mid = a + d * ((s0 + s1) * 0.5f);
                        if (InJunction(mid)) continue;
                        float g0 = lane.cum[k] + s0;
                        float g1 = lane.cum[k] + s1;

                        // 신호 교차로 쪽 끝에서는 정지선·횡단보도 구간에 차선 도색을 하지 않는다 (실제처럼 정지선에서 끊김)
                        float paintStart = startsAtMajor[li] ? CrosswalkFar + 0.5f : 0f;
                        float paintEnd = endsAtMajor[li] ? lane.Length - StopLineBack : lane.Length;
                        float solidFrom = endsAtMajor[li] ? paintEnd - NoChangeZone : float.MaxValue;
                        float c0 = Mathf.Max(g0, paintStart), c1 = Mathf.Min(g1, paintEnd);
                        bool paintable = c1 - c0 > 0.01f;
                        float ps0 = s0 + (c0 - g0), ps1 = s1 - (g1 - c1);

                        for (int side = -1; side <= 1; side += 2)
                        {
                            Vector2 sn = nl * side;
                            Vector2 bnd = mid + sn * lane.hw;
                            Vector2 probe = bnd + sn * 1.5f;

                            bool interior = InJunction(probe);
                            float nd = laneGrid.Nearest(probe, 2.2f, li, out int other, out Vector2 otherDir);
                            if (nd < 1.3f) interior = true;

                            if (interior)
                            {
                                if (side != 1 || other < 0 || !paintable) continue;   // 경계선은 왼쪽 차선 쪽에서 한 번만 그린다
                                bool sameEdge = lanes[other].edge == lane.edge;
                                bool opposite = Vector2.Dot(d, otherDir) < -0.3f;
                                if (!sameEdge && opposite)
                                {
                                    // 반대 방향 도로와 맞닿음: 각자 자기 쪽에 노란 실선 → 겹노란선
                                    LineQuad(paint, 1, a, d, sn, ps0, ps1, lane.hw - 0.14f, lineW);
                                }
                                else
                                {
                                    // 차선 사이: 점선, 정지선 앞 NoChangeZone 구간은 실선(진로변경 제한선)
                                    float split = Mathf.Clamp(solidFrom, c0, c1);
                                    if (split > c0) DashedQuads(paint, 0, a, d, sn, ps0, c0, split, lane.hw, lineW);
                                    if (split < c1) LineQuad(paint, 0, a, d, sn, ps0 + (split - c0), ps1, lane.hw, lineW);
                                }
                                continue;
                            }

                            // 바깥 경계: 흰 실선 + (공간이 되면) 연석·보도
                            if (paintable) LineQuad(paint, 0, a, d, sn, ps0, ps1, lane.hw - 0.25f, lineW);

                            Vector2 outer = bnd + sn * walkW;
                            Vector2 half = bnd + sn * (walkW * 0.5f);
                            if (InJunction(outer) || InJunction(half)) continue;
                            if (laneGrid.Nearest(outer, 3f, li, out _, out _) < lane.hw + 0.3f) continue;
                            if (laneGrid.Nearest(half, 3f, li, out _, out _) < lane.hw + 0.3f) continue;

                            SidewalkQuad(walk, a, d, sn, s0, s1, lane.hw, walkW, walkTile);
                            (side == 1 ? leftWalk : rightWalk).Add(new WalkSample { boundary = bnd, outward = sn, along = d, s = (g0 + g1) * 0.5f });
                        }
                    }
                }
                if (leftWalk.Count > 0) walks[(li, 1)] = leftWalk;
                if (rightWalk.Count > 0) walks[(li, -1)] = rightWalk;
            }

            if (lib != null && lib.laneWhite != null && lib.laneYellow != null)
            {
                CreateMeshObject("RoadMarkings", RoadRoot.transform, paint, new[] { lib.laneWhite, lib.laneYellow }, ShadowCastingMode.Off);
            }
            if (lib != null && lib.sidewalk != null)
            {
                CreateMeshObject("Sidewalks", RoadRoot.transform, walk, new[] { lib.sidewalk, lib.curb != null ? lib.curb : lib.sidewalk }, ShadowCastingMode.On);
            }
        }

        private void LineQuad(MeshBuffer buf, int sub, Vector2 a, Vector2 d, Vector2 sn, float s0, float s1, float offset, float width)
        {
            Vector2 p0 = a + d * s0 + sn * offset;
            Vector2 p1 = a + d * s1 + sn * offset;
            Vector2 h = sn * (width * 0.5f);
            int i0 = buf.Add(V3(p0 - h, YPaint), Vector2.zero);
            int i1 = buf.Add(V3(p0 + h, YPaint), Vector2.zero);
            int i2 = buf.Add(V3(p1 + h, YPaint), Vector2.zero);
            int i3 = buf.Add(V3(p1 - h, YPaint), Vector2.zero);
            buf.Quad(sub, i0, i1, i2, i3, Vector3.up);
        }

        // 3m 칠하고 5m 비우는 차선 점선 (누적 거리 기준이라 구간이 나뉘어도 패턴이 이어진다)
        private void DashedQuads(MeshBuffer buf, int sub, Vector2 a, Vector2 d, Vector2 sn, float s0, float g0, float g1, float offset, float width)
        {
            const float period = 8f, on = 3f;
            for (int k = Mathf.FloorToInt(g0 / period); k * period < g1; k++)
            {
                float i0 = Mathf.Max(g0, k * period);
                float i1 = Mathf.Min(g1, k * period + on);
                if (i1 - i0 < 0.05f) continue;
                LineQuad(buf, sub, a, d, sn, s0 + (i0 - g0), s0 + (i1 - g0), offset, width);
            }
        }

        private void SidewalkQuad(MeshBuffer buf, Vector2 a, Vector2 d, Vector2 sn, float s0, float s1, float hw, float walkW, float tile)
        {
            Vector2 in0 = a + d * s0 + sn * hw;
            Vector2 in1 = a + d * s1 + sn * hw;
            Vector2 out0 = in0 + sn * walkW;
            Vector2 out1 = in1 + sn * walkW;
            int t0 = buf.Add(V3(in0, YWalk), in0 / tile);
            int t1 = buf.Add(V3(in1, YWalk), in1 / tile);
            int t2 = buf.Add(V3(out1, YWalk), out1 / tile);
            int t3 = buf.Add(V3(out0, YWalk), out0 / tile);
            buf.Quad(0, t0, t1, t2, t3, Vector3.up);

            // 연석: 차도 쪽을 향하는 수직면
            float u0 = Vector2.Dot(in0, d), u1 = Vector2.Dot(in1, d);
            float h = YWalk - YRoad;
            int c0 = buf.Add(V3(in0, YRoad), new Vector2(u0, 0f));
            int c1 = buf.Add(V3(in1, YRoad), new Vector2(u1, 0f));
            int c2 = buf.Add(V3(in1, YWalk), new Vector2(u1, h));
            int c3 = buf.Add(V3(in0, YWalk), new Vector2(u0, h));
            buf.Quad(1, c0, c1, c2, c3, V3(-sn, 0f));
        }

        // =====================================================================
        // 지면
        // =====================================================================
        private void BuildGround()
        {
            float tile = lib != null ? Mathf.Max(0.5f, lib.groundTileMeters) : 6f;
            // 지도 밖도 지평선까지 이어 보이도록 넉넉하게 깐다
            Rect r = Rect.MinMaxRect(mapRect.xMin - 500f, mapRect.yMin - 500f, mapRect.xMax + 500f, mapRect.yMax + 500f);
            var buf = new MeshBuffer(1);
            Vector2[] c = { new Vector2(r.xMin, r.yMin), new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMax), new Vector2(r.xMax, r.yMin) };
            int i0 = buf.Add(V3(c[0], YGround), c[0] / tile);
            int i1 = buf.Add(V3(c[1], YGround), c[1] / tile);
            int i2 = buf.Add(V3(c[2], YGround), c[2] / tile);
            int i3 = buf.Add(V3(c[3], YGround), c[3] / tile);
            buf.Quad(0, i0, i1, i2, i3, Vector3.up);
            Material mat = lib != null && lib.ground != null ? lib.ground : fallbackGround;
            CreateMeshObject("CityGround", CityRoot.transform, buf, new[] { mat }, ShadowCastingMode.Off);
        }

        // =====================================================================
        // 교차로: 횡단보도·정지선·신호등
        // =====================================================================
        private void BuildIntersections()
        {
            var paint = new MeshBuffer(1);
            var signals = new GameObject("TrafficSignals");
            signals.transform.SetParent(RoadRoot.transform, false);
            int signalCount = 0;

            for (int i = 0; i < lanes.Count; i++)
            {
                Lane l = lanes[i];

                if (endsAtMajor[i])
                {
                    int n = l.p.Length;
                    Vector2 e = l.p[n - 1];
                    Vector2 d = (l.p[n - 1] - l.p[n - 2]).normalized;
                    Zebra(paint, e, -d, l.hw);
                    if (l.Length >= StopLineBack + 1f) StopLine(paint, e - d * StopLineBack, d, l.hw);

                    if (l.index == 0 && TryFarSideSignal(i, e, d, out Vector2 pos))
                    {
                        // 한국식: 신호등은 교차로 건너편 오른쪽 모퉁이.
                        // 프리팹의 긴 팔(+X)은 헤드가 +Z를 향하므로, +Z를 진입 차량 쪽(-d)으로 돌리면
                        // 긴 팔이 차로 위(진행 방향 왼쪽)로 뻗고 헤드가 다가오는 운전자를 본다.
                        GameObject prefab = l.edgeLanes >= 4 && lib.trafficSignalLarge != null ? lib.trafficSignalLarge : lib.trafficSignal;
                        if (prefab != null)
                        {
                            Place(prefab, signals.transform, pos, -d, 1f);
                            signalPositions.Add(pos);
                            MarkProp(pos);
                            signalCount++;
                        }
                    }
                }
                if (startsAtMajor[i])
                {
                    Vector2 s = l.p[0];
                    Vector2 d = (l.p[1] - l.p[0]).normalized;
                    Zebra(paint, s, d, l.hw);
                }
            }

            if (lib.laneWhite != null)
            {
                CreateMeshObject("Crosswalks", RoadRoot.transform, paint, new[] { lib.laneWhite }, ShadowCastingMode.Off);
            }
            int majors = 0;
            foreach (var kv in edgesAtJunction) if (kv.Value.Count >= MajorJunctionEdges) majors++;
            Debug.Log($"[GangnamCityBuilder] 신호 교차로 {majors}곳, 신호등 {signalCount}개 (건너편 배치)");
        }

        // 진입 차선 끝 e에서 진행 방향 d로 교차로를 가로질러 건너편 경계를 찾고, 그 너머 오른쪽 보도에 기둥 위치를 잡는다.
        private bool TryFarSideSignal(int laneIdx, Vector2 e, Vector2 d, out Vector2 pos)
        {
            pos = default;
            int j = laneEndJ[laneIdx];
            if (j < 0) return false;
            var poly = junctions[j];
            float exit = -1f;
            for (int k = 0; k < poly.Length; k++)
            {
                if (RaySegment(e - d * 0.5f, d, poly[k], poly[(k + 1) % poly.Length], out float t) && t > exit) exit = t;
            }
            if (exit < 2f || exit > 90f) return false;     // 건너편이 없거나 비정상적으로 큰 교차로

            Lane l = lanes[laneIdx];
            Vector2 right = -Left(d);
            float clear = l.hw + 0.4f;
            // 건너편 횡단보도 옆 모퉁이부터, 도로를 피할 수 있는 지점을 찾는다
            for (float along = 1.0f; along <= 7.0f; along += 2.0f)
            {
                for (float side = 1.2f; side <= 4.2f; side += 1.0f)
                {
                    Vector2 p = e + d * (exit - 0.5f + along) + right * (l.hw + side);
                    if (InJunction(p) || !PropFree(p)) continue;
                    if (laneGrid.Nearest(p, 3f, -1, out _, out _) < clear) continue;
                    pos = p;
                    return true;
                }
            }
            return false;
        }

        // 반직선 o + r*t 와 선분 ab의 교차 (t >= 0)
        private static bool RaySegment(Vector2 o, Vector2 r, Vector2 a, Vector2 b, out float t)
        {
            t = 0f;
            Vector2 s = b - a;
            float den = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(den) < 1e-6f) return false;
            Vector2 ao = a - o;
            t = (ao.x * s.y - ao.y * s.x) / den;
            float u = (ao.x * r.y - ao.y * r.x) / den;
            return t >= 0f && u >= 0f && u <= 1f;
        }

        // 교차로 경계 p에서 dirIntoLane 방향으로 1~5m 구간, 진행 방향과 나란한 막대 3개 (폭 0.5m, 간격 약 0.55m)
        private void Zebra(MeshBuffer buf, Vector2 p, Vector2 dirIntoLane, float hw)
        {
            Vector2 across = Left(dirIntoLane);
            const float barW = 0.5f;
            for (int k = 0; k < 3; k++)
            {
                float o = -hw + 0.55f + k * ((hw * 2f - 1.1f) / 2f);
                Vector2 c0 = p + dirIntoLane * 1.0f + across * o;
                Vector2 c1 = p + dirIntoLane * 5.0f + across * o;
                Vector2 h = across * (barW * 0.5f);
                int i0 = buf.Add(V3(c0 - h, YPaint + 0.002f), Vector2.zero);
                int i1 = buf.Add(V3(c0 + h, YPaint + 0.002f), Vector2.zero);
                int i2 = buf.Add(V3(c1 + h, YPaint + 0.002f), Vector2.zero);
                int i3 = buf.Add(V3(c1 - h, YPaint + 0.002f), Vector2.zero);
                buf.Quad(0, i0, i1, i2, i3, Vector3.up);
            }
        }

        private void StopLine(MeshBuffer buf, Vector2 p, Vector2 d, float hw)
        {
            Vector2 across = Left(d);
            Vector2 a = p + across * (hw - 0.1f);
            Vector2 b = p - across * (hw - 0.1f);
            Vector2 t = d * 0.45f;
            int i0 = buf.Add(V3(a, YPaint + 0.002f), Vector2.zero);
            int i1 = buf.Add(V3(b, YPaint + 0.002f), Vector2.zero);
            int i2 = buf.Add(V3(b + t, YPaint + 0.002f), Vector2.zero);
            int i3 = buf.Add(V3(a + t, YPaint + 0.002f), Vector2.zero);
            buf.Quad(0, i0, i1, i2, i3, Vector3.up);
        }

        // =====================================================================
        // 가로등·가로수
        // =====================================================================
        private void BuildStreetProps()
        {
            var lightsRoot = new GameObject("StreetLights");
            lightsRoot.transform.SetParent(RoadRoot.transform, false);
            var treesRoot = new GameObject("StreetTrees");
            treesRoot.transform.SetParent(CityRoot.transform, false);
            bool hasTrees = lib.streetTrees != null && lib.streetTrees.Length > 0;
            int lightCount = 0, treeCount = 0;

            foreach (var kv in walks)
            {
                Lane lane = lanes[kv.Key.lane];
                List<WalkSample> samples = kv.Value;
                float len = lane.Length;

                if (lib.streetLight != null)
                {
                    float next = (float)rng.NextDouble() * lib.streetLightSpacing;
                    foreach (var w in samples)
                    {
                        if (w.s < next || w.s < 6f || w.s > len - 6f) continue;
                        Vector2 pos = w.boundary + w.outward * 0.6f;
                        if (NearSignal(pos, 12f) || !PropFree(pos)) continue;
                        Place(lib.streetLight, lightsRoot.transform, pos, -w.outward, 1f);
                        MarkProp(pos);
                        lightCount++;
                        next = w.s + lib.streetLightSpacing;
                    }
                }

                if (hasTrees && lane.edgeLanes >= 2)
                {
                    float next = 4f + (float)rng.NextDouble() * lib.treeSpacing;
                    foreach (var w in samples)
                    {
                        if (w.s < next || w.s < 8f || w.s > len - 8f) continue;
                        Vector2 pos = w.boundary + w.outward * (lib.sidewalkWidth * 0.55f);
                        if (NearSignal(pos, 8f) || !PropFree(pos)) continue;
                        var prefab = lib.streetTrees[rng.Next(lib.streetTrees.Length)];
                        float yaw = (float)rng.NextDouble() * 360f;
                        float scale = Mathf.Lerp(lib.treeScale.x, lib.treeScale.y, (float)rng.NextDouble());
                        Vector2 f = new Vector2(Mathf.Sin(yaw * Mathf.Deg2Rad), Mathf.Cos(yaw * Mathf.Deg2Rad));
                        Place(prefab, treesRoot.transform, pos, f, scale, YWalk);
                        MarkProp(pos);
                        treeCount++;
                        next = w.s + lib.treeSpacing;
                    }
                }
            }
            Debug.Log($"[GangnamCityBuilder] 가로등 {lightCount}개, 가로수 {treeCount}그루");
        }

        private bool NearSignal(Vector2 p, float r)
        {
            float r2 = r * r;
            foreach (var s in signalPositions) if ((s - p).sqrMagnitude < r2) return true;
            return false;
        }

        private bool PropFree(Vector2 p)
        {
            int cx = Mathf.FloorToInt(p.x / 3f), cz = Mathf.FloorToInt(p.y / 3f);
            for (int x = cx - 1; x <= cx + 1; x++)
                for (int z = cz - 1; z <= cz + 1; z++)
                    if (propCells.Contains(Key(x, z))) return false;
            return true;
        }

        private void MarkProp(Vector2 p) => propCells.Add(Key(Mathf.FloorToInt(p.x / 3f), Mathf.FloorToInt(p.y / 3f)));

        // =====================================================================
        // 건물
        // =====================================================================
        private void BuildBuildings()
        {
            if (lib.buildings == null || lib.buildings.Length == 0) return;
            var root = new GameObject("Buildings");
            root.transform.SetParent(CityRoot.transform, false);
            float walkW = lib.sidewalkWidth;

            // 1) 가로변: 차로가 많은 도로(테헤란로·강남대로 등)부터 고층을 앞에 세운다
            var order = new List<(int lane, int side)>(walks.Keys);
            order.Sort((x, y) => lanes[y.lane].edgeLanes.CompareTo(lanes[x.lane].edgeLanes));
            foreach (var key in order)
            {
                Lane lane = lanes[key.lane];
                List<WalkSample> samples = walks[key];
                bool arterial = lane.edgeLanes >= 3;
                float cursor = samples[0].s + (float)rng.NextDouble() * 4f;
                int si = 0;
                while (true)
                {
                    while (si < samples.Count && samples[si].s < cursor) si++;
                    if (si >= samples.Count) break;
                    WalkSample w = samples[si];

                    bool placed = false;
                    for (int attempt = 0; attempt < FitScales.Length && !placed; attempt++)
                    {
                        var e = PickBuilding(arterial ? HeightPref.Tall : HeightPref.Low);
                        if (e == null) break;
                        float scale = FitScales[attempt];
                        bool swap = rng.NextDouble() < 0.5;
                        float width = (swap ? e.size.z : e.size.x) * scale;
                        float depth = (swap ? e.size.x : e.size.z) * scale;
                        if (cursor + width > lane.Length - 3f) continue;
                        // TryPlaceBuilding과 같은 기준: 바닥(+BuildingGap 여유)이 차선 중심에서 BuildingClear 이상 떨어지도록
                        float fromCenter = BuildingClear + BuildingGap + 0.3f + depth * 0.5f;
                        Vector2 center = w.boundary + w.outward * (fromCenter - lane.hw) + w.along * (width * 0.5f);
                        // 건물 로컬 X를 도로 방향에 맞춘다 (forward = 도로 법선)
                        Vector2 fwd = Left(w.along);
                        if (swap) fwd = w.along;
                        if (rng.NextDouble() < 0.5) fwd = -fwd;
                        if (TryPlaceBuilding(root.transform, e, center, fwd, scale))
                        {
                            cursor += width + BuildingGap * 2f + (float)rng.NextDouble() * 1.5f;
                            placed = true;
                        }
                    }
                    if (!placed) cursor += 2f;
                }
            }

            // 2) 블록 안쪽 빈 땅
            FillGrid(root.transform, 16f, 4f, attempt => (PickBuilding(HeightPref.Mid), FitScales[Mathf.Min(attempt + 2, FitScales.Length - 1)]), 4);
            // 3) 남은 틈: 작은 건물을 낮게(5~8층) 줄여 빽빽하게
            FillGrid(root.transform, 8f, 2f, attempt => (PickSmallBuilding(), 0.55f + (float)rng.NextDouble() * 0.2f), 3);
            FillGrid(root.transform, 6f, 1.5f, attempt => (PickSmallBuilding(), 0.45f + (float)rng.NextDouble() * 0.15f), 4);
            Debug.Log($"[GangnamCityBuilder] 판교 에셋 건물 {BuildingCount}동 배치, 건물 지을 수 있는 땅의 {LandCoverage() * 100f:F0}%를 덮음");
        }

        // 도로·보도·교차로를 뺀 땅(도로망에서 60m 이내) 중 건물 바닥이 덮은 비율. 4m 격자 표본.
        private float LandCoverage()
        {
            int land = 0, covered = 0;
            float clear = BuildingClear;
            for (float x = mapRect.xMin; x <= mapRect.xMax; x += 4f)
            {
                for (float z = mapRect.yMin; z <= mapRect.yMax; z += 4f)
                {
                    var q = new Vector2(x, z);
                    float d = laneGrid.Nearest(q, 60f, -1, out int near, out _);
                    if (near < 0 || d < clear || InJunction(q)) continue;
                    land++;
                    if (footprintCells.Contains(Key(Mathf.FloorToInt(x / 2f), Mathf.FloorToInt(z / 2f)))) covered++;
                }
            }
            return land > 0 ? (float)covered / land : 0f;
        }

        // step 간격 격자(± jitter)마다 가장 가까운 도로 방향에 맞춰 건물을 시도한다
        private void FillGrid(Transform parent, float step, float jitter,
                              System.Func<int, (CityAssetLibrary.BuildingEntry e, float scale)> pick, int attempts)
        {
            for (float x = mapRect.xMin; x <= mapRect.xMax; x += step)
            {
                for (float z = mapRect.yMin; z <= mapRect.yMax; z += step)
                {
                    Vector2 p = new Vector2(x + Jitter(jitter), z + Jitter(jitter));
                    float d = laneGrid.Nearest(p, 90f, -1, out int nearLane, out Vector2 dir);
                    if (nearLane < 0 || d < BuildingClear) continue;
                    if (buildingCells.Contains(Key(Mathf.FloorToInt(p.x / 2f), Mathf.FloorToInt(p.y / 2f)))) continue;
                    for (int attempt = 0; attempt < attempts; attempt++)
                    {
                        var (e, scale) = pick(attempt);
                        if (e == null) break;
                        Vector2 fwd = rng.NextDouble() < 0.5 ? dir : Left(dir);
                        if (TryPlaceBuilding(parent, e, p, fwd, scale)) break;
                    }
                }
            }
        }

        // 바닥 면적이 작은 건물 6종 가운데 하나 (틈 메우기용, 수가 많으니 가벼운 메시만)
        private List<CityAssetLibrary.BuildingEntry> smallBuildings;
        private CityAssetLibrary.BuildingEntry PickSmallBuilding()
        {
            if (smallBuildings == null)
            {
                smallBuildings = new List<CityAssetLibrary.BuildingEntry>();
                foreach (var b in lib.buildings) if (b != null && b.prefab != null && b.weight >= 0.75f) smallBuildings.Add(b);   // 폴리곤 많은 08·12 제외
                smallBuildings.Sort((a, b) => (a.size.x * a.size.z).CompareTo(b.size.x * b.size.z));
                if (smallBuildings.Count > 6) smallBuildings.RemoveRange(6, smallBuildings.Count - 6);
            }
            return smallBuildings.Count > 0 ? smallBuildings[rng.Next(smallBuildings.Count)] : null;
        }

        private enum HeightPref { Tall, Mid, Low }

        private CityAssetLibrary.BuildingEntry PickBuilding(HeightPref pref)
        {
            float total = 0f;
            var w = new float[lib.buildings.Length];
            for (int i = 0; i < w.Length; i++)
            {
                var e = lib.buildings[i];
                if (e == null || e.prefab == null) continue;
                float h = e.size.y;
                float f;
                switch (pref)
                {
                    case HeightPref.Tall: f = h >= 60f ? 3f : 0.6f; break;
                    case HeightPref.Low: f = h <= 60f ? 3f : 0.4f; break;
                    default: f = h <= 90f ? 1.5f : 0.3f; break;
                }
                w[i] = Mathf.Max(0f, e.weight) * f;
                total += w[i];
            }
            if (total <= 0f) return null;   // 유효한 프리팹이 하나도 없음
            float r = (float)rng.NextDouble() * total;
            int last = -1;
            for (int i = 0; i < w.Length; i++)
            {
                if (w[i] <= 0f) continue;
                last = i;
                r -= w[i];
                if (r <= 0f) return lib.buildings[i];
            }
            return lib.buildings[last];   // 부동소수 오차로 끝까지 온 경우
        }

        // fwd = 건물 로컬 +Z 방향. 바닥 사각형(로컬 X=size.x, Z=size.z에 scale, 여유 BuildingGap)이 도로·보도·교차로·다른 건물과 겹치지 않을 때만 배치.
        private bool TryPlaceBuilding(Transform parent, CityAssetLibrary.BuildingEntry e, Vector2 center, Vector2 fwd, float scale = 1f)
        {
            Vector2 alongDir = new Vector2(fwd.y, -fwd.x);   // 로컬 +X
            float hx = e.size.x * scale * 0.5f + BuildingGap;
            float hz = e.size.z * scale * 0.5f + BuildingGap;
            if (!mapRect.Contains(center)) return false;

            // 가장 넓은 차선 기준으로 차로+보도+여유를 비운다 (현재 map.json은 전 차선 3.2m)
            float clear = BuildingClear;
            float stepX = Mathf.Max(1f, (hx * 2f) / Mathf.Ceil(hx * 2f / 3f));
            float stepZ = Mathf.Max(1f, (hz * 2f) / Mathf.Ceil(hz * 2f / 3f));
            for (float u = -hx; u <= hx + 0.01f; u += stepX)
            {
                for (float v = -hz; v <= hz + 0.01f; v += stepZ)
                {
                    Vector2 q = center + alongDir * u + fwd * v;
                    if (buildingCells.Contains(Key(Mathf.FloorToInt(q.x / 2f), Mathf.FloorToInt(q.y / 2f)))) return false;
                    if (laneGrid.Nearest(q, clear, -1, out _, out _) < clear) return false;
                    if (InJunction(q)) return false;
                }
            }

            for (float u = -hx; u <= hx + 0.01f; u += 1f)
            {
                for (float v = -hz; v <= hz + 0.01f; v += 1f)
                {
                    Vector2 q = center + alongDir * u + fwd * v;
                    long key = Key(Mathf.FloorToInt(q.x / 2f), Mathf.FloorToInt(q.y / 2f));
                    buildingCells.Add(key);
                    if (Mathf.Abs(u) <= hx - BuildingGap && Mathf.Abs(v) <= hz - BuildingGap) footprintCells.Add(key);
                }
            }

            var go = Place(e.prefab, parent, center, fwd, scale, YGround);
            go.name = $"{e.prefab.name}_{BuildingCount}";
            BuildingCount++;
            return true;
        }

        private float Jitter(float r) => ((float)rng.NextDouble() * 2f - 1f) * r;

        // =====================================================================
        // 공통 유틸
        // =====================================================================
        private GameObject Place(GameObject prefab, Transform parent, Vector2 pos, Vector2 forward, float scale, float? y = null)
        {
            var go = Object.Instantiate(prefab, parent);
            Vector3 f = new Vector3(forward.x, 0f, forward.y);
            go.transform.SetPositionAndRotation(new Vector3(pos.x, y ?? YWalk, pos.y),
                f.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(f, Vector3.up) : Quaternion.identity);
            if (!Mathf.Approximately(scale, 1f)) go.transform.localScale = Vector3.one * scale;
            return go;
        }

        private static GameObject CreateMeshObject(string name, Transform parent, MeshBuffer buf, Material[] mats, ShadowCastingMode shadows)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var mesh = buf.ToMesh(name);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            var used = new Material[mesh.subMeshCount];
            for (int i = 0; i < used.Length; i++) used[i] = mats[Mathf.Min(i, mats.Length - 1)];
            mr.sharedMaterials = used;
            mr.shadowCastingMode = shadows;
            return go;
        }

        private static Vector2 Left(Vector2 d) => new Vector2(-d.y, d.x);
        private static Vector3 V3(Vector2 v, float y) => new Vector3(v.x, y, v.y);
        private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;

        private static bool PointInPolygon(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                    p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }

        // 귀 자르기 삼각분할 (오목한 교차로 다각형도 바깥으로 삐져나가지 않게)
        private static List<int> Triangulate(Vector2[] pts)
        {
            var result = new List<int>();
            int n = pts.Length;
            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(i);
            float area = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = pts[i], b = pts[(i + 1) % n];
                area += a.x * b.y - b.x * a.y;
            }
            float sign = area >= 0f ? 1f : -1f;

            int guard = n * n;
            while (idx.Count > 3 && guard-- > 0)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int ia = idx[(i + idx.Count - 1) % idx.Count], ib = idx[i], ic = idx[(i + 1) % idx.Count];
                    Vector2 a = pts[ia], b = pts[ib], c = pts[ic];
                    float cross = (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
                    if (cross * sign <= 1e-6f) continue;
                    bool contains = false;
                    foreach (int k in idx)
                    {
                        if (k == ia || k == ib || k == ic) continue;
                        if (PointInTriangle(pts[k], a, b, c)) { contains = true; break; }
                    }
                    if (contains) continue;
                    result.Add(ia); result.Add(ib); result.Add(ic);
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break;
            }
            if (idx.Count == 3)
            {
                result.Add(idx[0]); result.Add(idx[1]); result.Add(idx[2]);
            }
            else if (idx.Count > 3)
            {
                // 자기교차 등으로 실패하면 남은 부분은 부채꼴로
                for (int i = 1; i < idx.Count - 1; i++) { result.Add(idx[0]); result.Add(idx[i]); result.Add(idx[i + 1]); }
            }
            return result;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
            float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
            float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
            bool neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(neg && pos);
        }

        // =====================================================================
        // 보조 자료구조
        // =====================================================================
        private class MeshBuffer
        {
            private readonly List<Vector3> v = new List<Vector3>();
            private readonly List<Vector2> uv = new List<Vector2>();
            private readonly List<int>[] tris;

            public MeshBuffer(int subMeshes)
            {
                tris = new List<int>[subMeshes];
                for (int i = 0; i < subMeshes; i++) tris[i] = new List<int>();
            }

            public int Count => v.Count;

            public int Add(Vector3 p, Vector2 t)
            {
                v.Add(p);
                uv.Add(t);
                return v.Count - 1;
            }

            // 앞면(시계 방향)이 normal 쪽을 향하도록 정렬해서 추가
            public void Tri(int sub, int a, int b, int c, Vector3 normal)
            {
                Vector3 n = Vector3.Cross(v[b] - v[a], v[c] - v[a]);
                if (Vector3.Dot(n, normal) < 0f) { int t = b; b = c; c = t; }
                tris[sub].Add(a); tris[sub].Add(b); tris[sub].Add(c);
            }

            public void Quad(int sub, int a, int b, int c, int d, Vector3 normal)
            {
                Tri(sub, a, b, c, normal);
                Tri(sub, a, c, d, normal);
            }

            public Mesh ToMesh(string name)
            {
                var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(v);
                mesh.SetUVs(0, uv);
                int used = 0;
                for (int i = 0; i < tris.Length; i++) if (tris[i].Count > 0) used = i + 1;
                mesh.subMeshCount = Mathf.Max(1, used);
                for (int i = 0; i < mesh.subMeshCount; i++) mesh.SetTriangles(tris[i], i);
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                return mesh;
            }
        }

        /// <summary>선분 공간 해시: 점에서 가장 가까운 차선 중심선까지의 거리.</summary>
        private class SegmentGrid
        {
            private readonly float cell;
            private readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
            private readonly List<Vector2> a = new List<Vector2>();
            private readonly List<Vector2> b = new List<Vector2>();
            private readonly List<int> owner = new List<int>();

            public SegmentGrid(float cell) { this.cell = cell; }

            public void Add(Vector2 p0, Vector2 p1, int ownerId)
            {
                int id = a.Count;
                a.Add(p0); b.Add(p1); owner.Add(ownerId);
                int x0 = Mathf.FloorToInt(Mathf.Min(p0.x, p1.x) / cell), x1 = Mathf.FloorToInt(Mathf.Max(p0.x, p1.x) / cell);
                int z0 = Mathf.FloorToInt(Mathf.Min(p0.y, p1.y) / cell), z1 = Mathf.FloorToInt(Mathf.Max(p0.y, p1.y) / cell);
                for (int x = x0; x <= x1; x++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        long k = Key(x, z);
                        if (!cells.TryGetValue(k, out var list)) cells[k] = list = new List<int>();
                        list.Add(id);
                    }
                }
            }

            /// <summary>반경 radius 안에서 가장 가까운 선분 거리. 없으면 float.MaxValue, owner -1.</summary>
            public float Nearest(Vector2 p, float radius, int excludeOwner, out int nearestOwner, out Vector2 dir)
            {
                nearestOwner = -1;
                dir = Vector2.zero;
                float best = float.MaxValue;
                int x0 = Mathf.FloorToInt((p.x - radius) / cell), x1 = Mathf.FloorToInt((p.x + radius) / cell);
                int z0 = Mathf.FloorToInt((p.y - radius) / cell), z1 = Mathf.FloorToInt((p.y + radius) / cell);
                for (int x = x0; x <= x1; x++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        if (!cells.TryGetValue(Key(x, z), out var list)) continue;
                        foreach (int id in list)
                        {
                            if (owner[id] == excludeOwner) continue;
                            float d = DistanceToSegment(p, a[id], b[id]);
                            if (d < best && d <= radius)
                            {
                                best = d;
                                nearestOwner = owner[id];
                                dir = (b[id] - a[id]).normalized;
                            }
                        }
                    }
                }
                return best;
            }

            public static float DistanceToSegment(Vector2 p, Vector2 s0, Vector2 s1)
            {
                Vector2 ab = s1 - s0;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 1e-8f ? Mathf.Clamp01(Vector2.Dot(p - s0, ab) / len2) : 0f;
                return Vector2.Distance(p, s0 + ab * t);
            }
        }
    }
}
