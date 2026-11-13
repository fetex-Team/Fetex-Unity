using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Fetex
{
    // ==============================================================
    // 1. JSON Data Transfer Objects (SUMO / export_unity_replay.py)
    // ==============================================================
    [Serializable]
    public class ReplayData
    {
        public int version;
        public string strategy;
        public string algorithm;
        public string fingerprint;
        public float duration;
        public int startHour;
        public int stride;
        public List<EntityData> entities;
        public List<FrameData> frames;
        public MetricsData metrics;
    }

    [Serializable]
    public class MetricsData
    {
        public int n_total_passengers;
        public int n_measured;
        public int n_unpicked;
        public int n_timeout_removed;
        public float avg_wait_sec;
        public float true_avg_wait_sec_with_penalty;
        public float max_wait_sec;
        // v2 이후 추가 (없으면 기본값)
        public float p90_wait_sec;
        public float service_rate;
        public float est_revenue_won;
        public int n_scheduled_calls;
        public string demand_source;
    }

    [Serializable]
    public class EntityData
    {
        public string id;
        public string kind;
        public float len;   // v2: 차량 길이(m). 앞범퍼가 아니라 차 중심을 도로 위에 놓는 데 사용
    }

    [Serializable]
    public class FrameData
    {
        public float t;
        public List<VehicleState> vehicles;
        public List<PersonPos> people;
        public int requested;
        public int picked;
        public int waiting;
        public int arrived;
        public int timedOut;
        public float avgWait;
        public int moves;
        public int teleports;
        public int forecast;
    }

    [Serializable]
    public class VehicleState
    {
        public int i;
        public float x;     // 차 중심 x (v1 파일은 앞범퍼)
        public float z;
        public float a;     // 방향(도, 북쪽 0·시계방향)
        public int s;
        // v2: 차선 기반 이동 정보
        public int l;       // map.lanes 번호 (-1: 모름)
        public float p;     // 차선 위 앞범퍼 위치(m)
        public float v;     // 속도(m/s)
        public int[] w;     // 이전 프레임 이후 지나온 차선 번호들
    }

    [Serializable]
    public class PersonPos
    {
        public float x;
        public float z;
    }

    [Serializable]
    public class MapData
    {
        public int version;
        public string source;
        public float width;
        public float height;
        public float offsetX;
        public float offsetY;
        public bool grid;
        public List<RoadData> roads;
        public List<LaneData> lanes;     // v2: 교차로 내부 연결 차선까지 포함한 전체 차선 (차량 이동 경로용)
        public List<JunctionData> junctions;
        public List<CellData> cells;
    }

    [Serializable]
    public class LaneData
    {
        public string id;
        public string e;            // 소속 도로(edge) id — 같은 도로의 차선끼리는 차선 변경으로 처리
        public float len;           // SUMO 차선 길이
        public float w;             // 차선 폭
        public bool isInternal;     // 교차로 안쪽 연결 차선 여부
        public List<float> xz;      // 중심선 좌표 [x0, z0, x1, z1, ...]
    }

    [Serializable]
    public class RoadData
    {
        public string id;
        public float width;
        public List<Point2D> points;
    }

    [Serializable]
    public class JunctionData
    {
        public string id;
        public List<Point2D> points;
    }

    [Serializable]
    public class CellData
    {
        public string id;
        public Point2D center;
        public List<Point2D> points;
    }

    [Serializable]
    public class Point2D
    {
        public float x;
        public float z;
    }

    // ==============================================================
    // 2. Replay Player Engine & UI Controller
    // ==============================================================
    public class FetexReplayPlayer : MonoBehaviour
    {
        public static FetexReplayPlayer Instance { get; private set; }

        [Header("3D Prefabs")]
        public GameObject taxiPrefab;
        public GameObject civilianCarPrefab;
        public GameObject obstaclePrefab;

        [Header("Materials")]
        [Tooltip("상태 마커·탑뷰 아이콘·H3 선에 쓰는 HDRP/Unlit 머티리얼 원본. 에셋으로 참조해야 빌드에서 셰이더가 빠지지 않는다.")]
        public Material unlitTemplate;

        [Header("Simulation Playback")]
        public string replayFileName = "rl_prepositioned.json";
        public bool autoPlayOnStart = true;
        [Range(0.1f, 100f)] public float playbackSpeed = 5.0f;
        public float groundY = 0.05f;

        [Header("Real Gangnam Map Visualization")]
        [Tooltip("도로·건물·가로시설물 에셋 묶음 (Fetex ▸ City ▸ Rebuild City Asset Library 로 생성)")]
        public CityAssetLibrary cityAssets;
        public bool showGangnamRoadNetwork = true;
        public bool showGangnamBuildings = true;
        public bool showH3Cells = true;
        public bool hideSample3x3City = true;

        [Header("Live State")]
        public bool isLoaded = false;
        public bool isPlaying = false;
        public float currentTime = 0.0f;

        [Header("Camera Link")]
        public TwinCameraView cameraView;

        private ReplayData replayData;
        private MapData mapData;
        private List<MotionLane> motionLanes;   // v2 차선 경로 보간용 (map.json lanes)

        // 프레임마다 새로 만들지 않고 재사용하는 작업용 컬렉션 (GC 부담 감소)
        private readonly Dictionary<int, VehicleState> vLookupB = new Dictionary<int, VehicleState>();
        private readonly HashSet<int> activeInCurrentFrame = new HashSet<int>();
        private GameObject roadNetworkObject;
        private GameObject buildingsContainerObject;
        private readonly List<GameObject> h3CellObjects = new List<GameObject>();
        private GameObject sampleCityObject;

        private readonly Dictionary<int, GameObject> vehicleInstances = new Dictionary<int, GameObject>();
        private readonly Dictionary<int, Renderer> statusMarkers = new Dictionary<int, Renderer>();

        // 탑뷰 아이콘 (상태 색 핀 / 승객 마름모). 추적 시점에서는 숨기고 기존 구체 마커를 쓴다.
        [Header("Top-view icons (screen px radius)")]
        public float taxiIconPx = 9f;
        public float otherVehicleIconPx = 5f;
        public float personIconPx = 6f;
        private readonly Dictionary<int, Renderer> iconFills = new Dictionary<int, Renderer>();
        private readonly Dictionary<int, Transform> iconRoots = new Dictionary<int, Transform>();
        private readonly Dictionary<int, float> iconPx = new Dictionary<int, float>();
        private readonly List<Transform> personIcons = new List<Transform>();
        private bool iconMode = true;
        private bool iconScaleDirty = true;
        private float lastIconOrtho = -1f;
        private int lastIconScreenH = -1;
        private int selectedEntity = -1;        // 이전/다음 택시 버튼의 현재 대상 (로드 시 자동 선택)
        private int highlightedEntity = -1;     // 추적 시점에서 실제로 따라간 택시 → 탑뷰에서 굵은 테두리
        private readonly Dictionary<int, Transform> iconOutlines = new Dictionary<int, Transform>();
        private readonly int[] stateCounts = new int[8];
        private int waitingPeople;
        private Material matIconOutline;
        private Material matIconOutlineDark;
        private readonly Dictionary<int, int> lastVehicleStates = new Dictionary<int, int>();
        private readonly List<GameObject> personPool = new List<GameObject>();
        // 리플레이 전체에 등장하는 택시(교대 포함 수백 대)와, 지금 프레임에 도로 위에 있는 택시
        private readonly HashSet<int> taxiEntities = new HashSet<int>();
        private readonly List<int> onRoadTaxis = new List<int>();

        private Material matIdle;
        private Material matPickup;
        private Material matOccupied;
        private Material matReposition;
        private Material matCivilian;
        private Material matAuto;
        private Material matObstacle;
        private Material matPerson;
        private Material matRoadAsphalt;
        private Material matH3Boundary;
        private Material matGroundCity;

        public string CurrentClockString => ClockAt(currentTime, true);

        private void Awake()
        {
            Instance = this;
#if UNITY_EDITOR
            if (unlitTemplate == null)
                unlitTemplate = AssetDatabase.LoadAssetAtPath<Material>("Assets/Fetex/Materials/Mat_StatusUnlit.mat");
#endif
            InitializeMaterials();

            if (cameraView == null)
            {
                cameraView = FindFirstObjectByType<TwinCameraView>();
            }

            FindSampleCityObject();

#if UNITY_EDITOR
            if (taxiPrefab == null)
                taxiPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/object/car_taxi_1.prefab");
            if (civilianCarPrefab == null)
                civilianCarPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/object/obs_car_1.prefab");
            if (obstaclePrefab == null)
                obstaclePrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/object/obs_kickboard_q.prefab");
            if (cityAssets == null)
                cityAssets = AssetDatabase.LoadAssetAtPath<CityAssetLibrary>("Assets/Fetex/Settings/GangnamCityAssets.asset");
#endif
        }

        private void Start()
        {
            LoadMapAndReplay(replayFileName);
        }

        private void FindSampleCityObject()
        {
            sampleCityObject = GameObject.Find("street_main_1(Clone)");
            if (sampleCityObject == null) sampleCityObject = GameObject.Find("street_main_1");
            if (sampleCityObject == null)
            {
                var cityObj = GameObject.Find("City") ?? GameObject.Find("Map");
                if (cityObj != null) sampleCityObject = cityObj;
            }
        }

        private void InitializeMaterials()
        {
            // 빌드에서는 코드로만 찾는 셰이더(Shader.Find)가 제외될 수 있으므로 에셋으로 참조한 원본을 복제해 쓴다.
            Shader shader = unlitTemplate != null ? unlitTemplate.shader : null;
            if (shader == null)
            {
                Debug.LogWarning("[FetexReplayPlayer] unlitTemplate이 비어 있어 Shader.Find로 대체합니다. 빌드에서 마커·아이콘이 안 보일 수 있습니다.");
                shader = Shader.Find("HDRP/Unlit");
                if (shader == null) shader = Shader.Find("HDRP/Lit");
                if (shader == null) shader = Shader.Find("Unlit/Color");
            }

            matIdle = CreateStatusMaterial(shader, new Color(0.15f, 0.95f, 0.25f), "Mat_Idle");
            matPickup = CreateStatusMaterial(shader, new Color(1.0f, 0.85f, 0.1f), "Mat_Pickup");
            matOccupied = CreateStatusMaterial(shader, new Color(0.95f, 0.2f, 0.2f), "Mat_Occupied");
            matReposition = CreateStatusMaterial(shader, new Color(0.05f, 0.65f, 1.0f), "Mat_Reposition");
            matCivilian = CreateStatusMaterial(shader, new Color(0.7f, 0.7f, 0.7f), "Mat_Civilian");
            matAuto = CreateStatusMaterial(shader, new Color(1.0f, 0.5f, 0.05f), "Mat_Auto");
            matObstacle = CreateStatusMaterial(shader, new Color(0.85f, 0.15f, 0.95f), "Mat_Obstacle");
            matPerson = CreateStatusMaterial(shader, new Color(1.0f, 0.2f, 0.6f), "Mat_Person");
            matIconOutline = CreateStatusMaterial(shader, Color.white, "Mat_IconOutline");
            matIconOutlineDark = CreateStatusMaterial(shader, new Color(0.15f, 0.15f, 0.17f), "Mat_IconOutlineDark");

            matRoadAsphalt = CreateStatusMaterial(shader, new Color(0.12f, 0.13f, 0.16f), "Mat_RoadAsphalt");
            matH3Boundary = CreateStatusMaterial(shader, new Color(0.0f, 0.85f, 1.0f, 0.7f), "Mat_H3Boundary");

            Shader litShader = Shader.Find("HDRP/Lit");
            if (litShader == null) litShader = Shader.Find("Standard");
            if (litShader == null) litShader = Shader.Find("Universal Render Pipeline/Lit");
            if (litShader == null) litShader = shader;

            // cityAssets가 없을 때만 쓰는 대체 지면 머티리얼
            matGroundCity = CreateStatusMaterial(litShader, new Color(0.08f, 0.09f, 0.10f), "Mat_GroundCity");
        }

        private Material CreateStatusMaterial(Shader shader, Color color, string name)
        {
            // 같은 셰이더의 원본 에셋이 있으면 복제(키워드·렌더 상태까지 원본과 동일 → 빌드에 포함된 변형 사용)
            bool fromTemplate = unlitTemplate != null && unlitTemplate.shader == shader;
            Material mat = (fromTemplate ? new Material(unlitTemplate) : new Material(shader));
            mat.name = name;
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
            if (mat.HasProperty("_UnlitColor")) mat.SetColor("_UnlitColor", color);
            return mat;
        }

        public void LoadMapAndReplay(string fileName)
        {
            replayFileName = fileName;
            StartCoroutine(LoadAllCoroutine(fileName));
        }

        private IEnumerator LoadAllCoroutine(string fileName)
        {
            isLoaded = false;
            isPlaying = false;
            ClearAllInstances();

            if (mapData == null)
            {
                string mapPath = Path.Combine(Application.streamingAssetsPath, "map.json");
                if (File.Exists(mapPath))
                {
                    string mapJson = null;
                    var mapTask = System.Threading.Tasks.Task.Run(() => File.ReadAllText(mapPath, System.Text.Encoding.UTF8));
                    while (!mapTask.IsCompleted) yield return null;

                    if (mapTask.IsFaulted)
                    {
                        Debug.LogError($"[FetexReplayPlayer] map.json 읽기 실패 → 도로·건물 없이 재생합니다: {mapTask.Exception?.GetBaseException().Message}");
                    }
                    else
                    {
                        mapJson = mapTask.Result;
                        try { mapData = JsonUtility.FromJson<MapData>(mapJson); }
                        catch (Exception ex) { Debug.LogError($"[FetexReplayPlayer] map.json 파싱 실패 → 도로·건물 없이 재생합니다: {ex.Message}"); }
                        if (mapData != null && (mapData.roads == null || mapData.roads.Count == 0))
                        {
                            Debug.LogError("[FetexReplayPlayer] map.json에 roads가 없습니다 → 도로·건물 없이 재생합니다 (내보내기 버전 확인)");
                        }
                    }
                }
                else
                {
                    Debug.LogError($"[FetexReplayPlayer] map.json이 없습니다: {mapPath} → 도로·건물 없이 재생합니다");
                }
            }

            if (mapData != null && (roadNetworkObject == null || buildingsContainerObject == null))
            {
                BuildGangnamRoadNetwork(mapData);
            }
            if (mapData != null && motionLanes == null)
            {
                BuildMotionLanes(mapData);
            }

            string replayPath = ResolveReplayPath(fileName);
            if (replayPath == null)
            {
                Debug.LogError($"[FetexReplayPlayer] File not found: {Path.Combine(Application.streamingAssetsPath, fileName)} (.gz 포함)");
                yield break;
            }

            string replayJson = null;
            var replayTask = System.Threading.Tasks.Task.Run(() => ReadReplayText(replayPath));
            while (!replayTask.IsCompleted) yield return null;

            if (replayTask.IsFaulted)
            {
                Debug.LogError($"[FetexReplayPlayer] Read error: {replayTask.Exception}");
                yield break;
            }

            replayJson = replayTask.Result;
            try
            {
                replayData = JsonUtility.FromJson<ReplayData>(replayJson);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FetexReplayPlayer] Parse error: {ex}");
                yield break;
            }

            if (replayData == null || replayData.frames == null || replayData.frames.Count == 0)
            {
                Debug.LogError("[FetexReplayPlayer] ReplayData has no valid frames!");
                yield break;
            }

            currentTime = 0f;
            isLoaded = true;
            isPlaying = autoPlayOnStart;

            taxiEntities.Clear();
            onRoadTaxis.Clear();
            selectedEntity = -1;
            highlightedEntity = -1;
            if (replayData.entities != null)
            {
                for (int i = 0; i < replayData.entities.Count; i++)
                {
                    string k = replayData.entities[i].kind;
                    string id = replayData.entities[i].id;
                    if ((k != null && k.ToLower().Contains("taxi")) || (id != null && id.ToLower().Contains("taxi")))
                    {
                        taxiEntities.Add(i);
                    }
                }
            }

            bool lanePath = UseLanePath;
            Debug.Log($"[FetexReplayPlayer] Loaded {fileName} (v{replayData.version}, {replayData.frames.Count} frames, stride {replayData.stride}s) | " +
                      $"roads {mapData?.roads?.Count ?? 0}, lanes {motionLanes?.Count ?? 0} | 이동 방식: {(lanePath ? "차선 경로 보간" : "직선 보간(구버전 파일)")}");

            ApplyCityVisibility();
            UpdateSimulation(0f);
            SelectTaxiForCamera(0);
        }

        /// <summary>
        /// 재생 파일 경로 찾기. "patrol.json"을 요청하면 압축본 "patrol.json.gz"를 먼저 찾고, 없으면 원본 .json.
        /// (export_unity_replay.py 기본 출력이 .json.gz — 48MB → 약 8MB)
        /// </summary>
        private static string ResolveReplayPath(string fileName)
        {
            string basePath = Path.Combine(Application.streamingAssetsPath, fileName);
            if (!fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) && File.Exists(basePath + ".gz"))
                return basePath + ".gz";
            return File.Exists(basePath) ? basePath : null;
        }

        /// <summary>.json 또는 .json.gz를 UTF-8 문자열로 읽기 (백그라운드 스레드에서 호출).</summary>
        private static string ReadReplayText(string path)
        {
            if (!path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                return File.ReadAllText(path, System.Text.Encoding.UTF8);
            using (var file = File.OpenRead(path))
            using (var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress))
            using (var reader = new StreamReader(gz, System.Text.Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        /// <summary>map.json의 lanes를 차선 경로 보간용 구조로 변환 (교차로 내부 차선 포함).</summary>
        private void BuildMotionLanes(MapData map)
        {
            if (map == null || map.lanes == null || map.lanes.Count == 0)
            {
                motionLanes = null;   // 구버전 map.json → 직선 보간으로 재생
                return;
            }
            var edgeIndex = new Dictionary<string, int>();
            motionLanes = new List<MotionLane>(map.lanes.Count);
            for (int i = 0; i < map.lanes.Count; i++)
            {
                LaneData ld = map.lanes[i];
                string edge = ld.e ?? ld.id ?? i.ToString();
                if (!edgeIndex.TryGetValue(edge, out int ei))
                {
                    ei = edgeIndex.Count;
                    edgeIndex[edge] = ei;
                }
                motionLanes.Add(new MotionLane(ld.id, ei, ld.len, ld.w, ld.xz));
            }
        }

        /// <summary>v2 재생 파일 + 차선 정보가 있는 map.json일 때만 차선 경로 보간을 쓴다.</summary>
        private bool UseLanePath
        {
            get { return replayData != null && replayData.version >= 2 && motionLanes != null && motionLanes.Count > 0; }
        }

        private static MotionState ToMotionState(VehicleState v)
        {
            return new MotionState { Lane = v.l, Pos = v.p, Speed = v.v, X = v.x, Z = v.z, Angle = v.a, Via = v.w };
        }

        private float EntityLength(int entityIndex)
        {
            if (replayData != null && replayData.entities != null && entityIndex >= 0 && entityIndex < replayData.entities.Count)
            {
                return replayData.entities[entityIndex].len;
            }
            return 0f;
        }

        private int FindFrameIndex(float time)
        {
            int count = replayData.frames.Count;
            if (time <= replayData.frames[0].t) return 0;
            if (time >= replayData.frames[count - 1].t) return count - 1;
            int low = 0, high = count - 1, idx = 0;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (replayData.frames[mid].t <= time)
                {
                    idx = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }
            return idx;
        }

        private void BuildGangnamRoadNetwork(MapData map)
        {
            if (map == null || map.roads == null) return;

            if (roadNetworkObject != null) Destroy(roadNetworkObject);
            if (buildingsContainerObject != null) Destroy(buildingsContainerObject);
            h3CellObjects.Clear();

            // 도로·도색·보도·신호등·가로등 → roadNetworkObject, 지면·건물·가로수 → buildingsContainerObject
            var builder = new GangnamCityBuilder(map, cityAssets, groundY, matRoadAsphalt, matGroundCity);
            builder.Build(transform);
            roadNetworkObject = builder.RoadRoot;
            buildingsContainerObject = builder.CityRoot;

            if (map.cells != null)
            {
                for (int c = 0; c < map.cells.Count; c++)
                {
                    CellData cell = map.cells[c];
                    if (cell.points == null || cell.points.Count < 3) continue;

                    GameObject cellObj = new GameObject($"H3Cell_{cell.id}");
                    cellObj.transform.SetParent(roadNetworkObject.transform, false);

                    LineRenderer lr = cellObj.AddComponent<LineRenderer>();
                    lr.sharedMaterial = matH3Boundary;
                    lr.startWidth = 4.0f;
                    lr.endWidth = 4.0f;
                    lr.loop = true;
                    lr.useWorldSpace = true;
                    lr.positionCount = cell.points.Count;

                    for (int p = 0; p < cell.points.Count; p++)
                    {
                        lr.SetPosition(p, new Vector3(cell.points[p].x, groundY + 0.3f, cell.points[p].z));
                    }

                    h3CellObjects.Add(cellObj);
                }
            }

            roadNetworkObject.SetActive(showGangnamRoadNetwork);
            buildingsContainerObject.SetActive(showGangnamBuildings);
            UpdateH3Visibility();
        }

        // H3 경계선은 탑뷰 분석용이라 추적 시점(지상 높이)에서는 시야를 가리므로 숨긴다
        private bool H3ShouldShow => showH3Cells && (cameraView == null || cameraView.overview);
        private bool h3Shown;

        private void UpdateH3Visibility()
        {
            h3Shown = H3ShouldShow;
            foreach (var cellObj in h3CellObjects)
            {
                if (cellObj != null) cellObj.SetActive(h3Shown);
            }
        }

        private void ApplyCityVisibility()
        {
            if (sampleCityObject == null) FindSampleCityObject();
            if (sampleCityObject != null)
            {
                sampleCityObject.SetActive(!hideSample3x3City);
            }
        }

        private void Update()
        {
            if (h3Shown != H3ShouldShow) UpdateH3Visibility();   // 탑뷰/추적뷰 전환 감지
            bool wantIcons = cameraView == null || cameraView.overview;
            if (wantIcons != iconMode) SetIconMode(wantIcons);
            if (!wantIcons && selectedEntity != highlightedEntity) SetHighlight(selectedEntity);
            if (!isLoaded || replayData == null || replayData.frames.Count == 0) return;

            if (isPlaying)
            {
                currentTime += Time.deltaTime * playbackSpeed;
                if (currentTime >= replayData.duration)
                {
                    currentTime = replayData.duration;
                    isPlaying = false;
                }
                UpdateSimulation(currentTime);
            }
        }

        public void UpdateSimulation(float time)
        {
            if (replayData == null || replayData.frames == null || replayData.frames.Count == 0) return;

            int count = replayData.frames.Count;
            int idxA = FindFrameIndex(time);
            int idxB = Mathf.Min(idxA + 1, count - 1);
            float span = replayData.frames[idxB].t - replayData.frames[idxA].t;
            float factor = (idxA != idxB && span > 0.0001f)
                ? Mathf.Clamp01((time - replayData.frames[idxA].t) / span) : 0f;

            FrameData fA = replayData.frames[idxA];
            FrameData fB = replayData.frames[idxB];

            vLookupB.Clear();
            if (fB.vehicles != null)
            {
                for (int i = 0; i < fB.vehicles.Count; i++)
                {
                    vLookupB[fB.vehicles[i].i] = fB.vehicles[i];
                }
            }

            activeInCurrentFrame.Clear();
            onRoadTaxis.Clear();
            System.Array.Clear(stateCounts, 0, stateCounts.Length);
            bool lanePath = UseLanePath;

            if (fA.vehicles != null)
            {
                for (int i = 0; i < fA.vehicles.Count; i++)
                {
                    VehicleState vA = fA.vehicles[i];
                    activeInCurrentFrame.Add(vA.i);
                    if (taxiEntities.Contains(vA.i)) onRoadTaxis.Add(vA.i);

                    float px = vA.x, pz = vA.z, heading = vA.a;
                    int finalState = vA.s;

                    if (idxA != idxB && vLookupB.TryGetValue(vA.i, out VehicleState vB))
                    {
                        MotionState mA = ToMotionState(vA);
                        MotionState mB = ToMotionState(vB);
                        if (lanePath)
                        {
                            // 두 프레임 사이에 실제로 지나간 차선(교차로 내부 포함)을 따라 이동
                            ReplayMotion.Interpolate(motionLanes, mA, mB, factor, span, EntityLength(vA.i),
                                                     out px, out pz, out heading);
                        }
                        else
                        {
                            ReplayMotion.Linear(mA, mB, factor, out px, out pz, out heading);
                        }
                        finalState = factor >= 0.5f ? vB.s : vA.s;
                    }
                    else if (lanePath)
                    {
                        // 다음 프레임이 없음(곧 사라짐/마지막 프레임): 기록 위치 대신 차선 위로
                        ReplayMotion.Place(motionLanes, ToMotionState(vA), EntityLength(vA.i), out px, out pz, out heading);
                    }

                    GameObject vObj = GetOrCreateVehicle(vA.i);
                    vObj.transform.SetPositionAndRotation(new Vector3(px, groundY, pz), Quaternion.Euler(0f, heading, 0f));
                    if (!vObj.activeSelf) vObj.SetActive(true);

                    UpdateStatusMarker(vA.i, finalState);
                    if (finalState >= 0 && finalState < stateCounts.Length) stateCounts[finalState]++;
                }
            }

            foreach (var kvp in vehicleInstances)
            {
                if (!activeInCurrentFrame.Contains(kvp.Key))
                {
                    if (kvp.Value.activeSelf) kvp.Value.SetActive(false);
                }
            }

            UpdatePeople(fA.people);

            // 추적 중인 택시가 운행을 마치고 사라지면 다음 운행 택시로 넘어간다
            onRoadTaxis.Sort();
            if (selectedEntity >= 0 && onRoadTaxis.Count > 0 && !activeInCurrentFrame.Contains(selectedEntity))
            {
                SelectTaxiForCamera(0);
            }
        }

        private GameObject GetOrCreateVehicle(int entityIndex)
        {
            if (vehicleInstances.TryGetValue(entityIndex, out GameObject existing))
            {
                return existing;
            }

            string kind = "taxi";
            string id = $"veh_{entityIndex}";
            if (replayData != null && replayData.entities != null && entityIndex < replayData.entities.Count)
            {
                kind = replayData.entities[entityIndex].kind ?? "taxi";
                id = replayData.entities[entityIndex].id ?? id;
            }

            GameObject prefabToUse = taxiPrefab;
            if (kind.ToLower().Contains("obs") || kind.ToLower().Contains("kickboard"))
            {
                prefabToUse = obstaclePrefab != null ? obstaclePrefab : civilianCarPrefab;
            }
            else if (!kind.ToLower().Contains("taxi") && !id.ToLower().Contains("taxi"))
            {
                prefabToUse = civilianCarPrefab != null ? civilianCarPrefab : taxiPrefab;
            }

            GameObject instance;
            if (prefabToUse != null)
            {
                instance = Instantiate(prefabToUse, transform);
                foreach (var anim in instance.GetComponentsInChildren<Animator>())
                {
                    anim.enabled = false;
                }
            }
            else
            {
                instance = GameObject.CreatePrimitive(PrimitiveType.Cube);
                instance.transform.SetParent(transform, false);
                instance.transform.localScale = new Vector3(1.8f, 1.4f, 4.2f);
                Destroy(instance.GetComponent<Collider>());
            }

            instance.name = id;

            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "StatusMarker";
            marker.transform.SetParent(instance.transform, false);
            marker.transform.localPosition = new Vector3(0f, 2.3f, 0f);
            marker.transform.localScale = new Vector3(0.65f, 0.65f, 0.65f);
            Destroy(marker.GetComponent<Collider>());

            Renderer markerRenderer = marker.GetComponent<Renderer>();
            markerRenderer.sharedMaterial = matIdle;

            vehicleInstances[entityIndex] = instance;
            statusMarkers[entityIndex] = markerRenderer;

            // 탑뷰 아이콘: 택시·자율주행차는 진행 방향 핀, 일반 차량은 작은 원, 장애물은 마름모
            string k = kind.ToLower();
            bool isTaxi = k.Contains("taxi") || id.ToLower().Contains("taxi");
            bool bigPin = isTaxi || k.Contains("auto");
            Mesh shape = bigPin ? TopViewIcons.Pin : (prefabToUse == obstaclePrefab ? TopViewIcons.Diamond : TopViewIcons.Disc);
            Renderer fill = TopViewIcons.Create(instance.transform, shape, isTaxi ? matIdle : matCivilian,
                                                bigPin ? matIconOutline : matIconOutlineDark, out Transform iconRoot);
            iconFills[entityIndex] = fill;
            iconRoots[entityIndex] = iconRoot;
            iconOutlines[entityIndex] = iconRoot.Find(TopViewIcons.OutlineName);
            if (entityIndex == highlightedEntity) TopViewIcons.SetHighlighted(iconOutlines[entityIndex], true);
            iconPx[entityIndex] = bigPin ? taxiIconPx : otherVehicleIconPx;
            iconRoot.gameObject.SetActive(iconMode);
            marker.SetActive(!iconMode);
            iconScaleDirty = true;

            return instance;
        }

        private void UpdateStatusMarker(int entityIndex, int state)
        {
            if (!statusMarkers.TryGetValue(entityIndex, out Renderer renderer)) return;

            if (lastVehicleStates.TryGetValue(entityIndex, out int lastState) && lastState == state)
            {
                return;
            }

            lastVehicleStates[entityIndex] = state;
            Material mat = StateMaterial(state);
            renderer.sharedMaterial = mat;
            if (iconFills.TryGetValue(entityIndex, out Renderer fill)) fill.sharedMaterial = mat;
        }

        private Material StateMaterial(int state)
        {
            switch (state)
            {
                case 0: return matIdle;
                case 1: return matPickup;
                case 2: return matOccupied;
                case 3: return matReposition;
                case 4: return matCivilian;
                case 5: return matAuto;
                case 6: return matObstacle;
                default: return matIdle;
            }
        }

        private void SetHighlight(int entity)
        {
            if (iconOutlines.TryGetValue(highlightedEntity, out Transform old) && old != null) TopViewIcons.SetHighlighted(old, false);
            highlightedEntity = entity;
            if (iconOutlines.TryGetValue(entity, out Transform now) && now != null) TopViewIcons.SetHighlighted(now, true);
        }

        private void SetIconMode(bool overview)
        {
            iconMode = overview;
            foreach (var kv in iconRoots) if (kv.Value != null) kv.Value.gameObject.SetActive(overview);
            foreach (var kv in statusMarkers) if (kv.Value != null) kv.Value.gameObject.SetActive(!overview);
            foreach (var t in personIcons) if (t != null) t.gameObject.SetActive(overview);
            iconScaleDirty = true;
        }

        // 카메라 확대율이 바뀌면 아이콘을 화면상 일정한 크기로 다시 맞춘다 (실제 차보다 작아지지는 않게)
        private void LateUpdate()
        {
            if (!iconMode || cameraView == null) return;
            float ortho = cameraView.overviewSize;
            if (!iconScaleDirty && Mathf.Approximately(ortho, lastIconOrtho) && Screen.height == lastIconScreenH) return;
            lastIconOrtho = ortho;
            lastIconScreenH = Screen.height;
            iconScaleDirty = false;

            float metersPerPx = 2f * ortho / Mathf.Max(1, Screen.height);
            foreach (var kv in iconRoots)
            {
                if (kv.Value == null) continue;
                float px = iconPx[kv.Key];
                TopViewIcons.SetWorldRadius(kv.Value, Mathf.Max(px * metersPerPx, 2.4f));
            }
            float personR = Mathf.Max(personIconPx * metersPerPx, 0.8f);
            foreach (var t in personIcons) if (t != null) TopViewIcons.SetWorldRadius(t, personR);
        }

        private void UpdatePeople(List<PersonPos> people)
        {
            int targetCount = people != null ? people.Count : 0;
            waitingPeople = targetCount;

            while (personPool.Count < targetCount)
            {
                GameObject pin = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                pin.name = $"Person_{personPool.Count}";
                pin.transform.SetParent(transform, false);
                pin.transform.localScale = new Vector3(0.6f, 1.0f, 0.6f);
                Destroy(pin.GetComponent<Collider>());
                pin.GetComponent<Renderer>().sharedMaterial = matPerson;
                TopViewIcons.Create(pin.transform, TopViewIcons.Diamond, matPerson, matIconOutline, out Transform icon);
                icon.gameObject.SetActive(iconMode);
                personIcons.Add(icon);
                iconScaleDirty = true;
                personPool.Add(pin);
            }

            for (int i = 0; i < targetCount; i++)
            {
                GameObject pin = personPool[i];
                pin.transform.position = new Vector3(people[i].x, groundY + 0.8f, people[i].z);
                if (!pin.activeSelf) pin.SetActive(true);
            }

            for (int i = targetCount; i < personPool.Count; i++)
            {
                if (personPool[i].activeSelf) personPool[i].SetActive(false);
            }
        }

        public void SelectTaxiForCamera(int delta)
        {
            // 지금 도로 위에 있는 택시들 사이에서만 이동한다
            int n = onRoadTaxis.Count;
            if (n == 0) return;

            int idx = onRoadTaxis.IndexOf(selectedEntity);
            if (idx >= 0)
            {
                idx = ((idx + delta) % n + n) % n;
            }
            else
            {
                // 고르던 택시가 사라졌으면 번호상 바로 다음 운행 택시부터
                int next = onRoadTaxis.FindIndex(e => e > selectedEntity);
                if (next < 0) next = 0;
                idx = delta < 0 ? ((next - 1) % n + n) % n : next;
            }
            int taxiEntityId = onRoadTaxis[idx];
            selectedEntity = taxiEntityId;
            iconScaleDirty = true;

            if (vehicleInstances.TryGetValue(taxiEntityId, out GameObject taxiObj))
            {
                if (cameraView != null)
                {
                    cameraView.target = taxiObj.transform;
                }
            }
        }

        public void SelectFirstTaxiForCamera()
        {
            SelectTaxiForCamera(0);
        }

        private void ClearAllInstances()
        {
            foreach (var kvp in vehicleInstances)
            {
                if (kvp.Value != null) Destroy(kvp.Value);
            }
            vehicleInstances.Clear();
            statusMarkers.Clear();
            lastVehicleStates.Clear();
            iconFills.Clear();
            iconRoots.Clear();
            iconPx.Clear();
            iconOutlines.Clear();
            personIcons.Clear();

            foreach (var p in personPool)
            {
                if (p != null) Destroy(p);
            }
            personPool.Clear();

            // 건물과 도로는 리플레이 전환 시 파괴하지 않고 유지합니다.
        }

        private FrameData GetCurrentFrameData()
        {
            if (replayData == null || replayData.frames == null || replayData.frames.Count == 0) return null;
            return replayData.frames[FindFrameIndex(currentTime)];
        }

        // =====================================================================
        // HUD(FetexHud)용 API
        // =====================================================================
        public float Duration => replayData != null ? replayData.duration : 0f;
        public int WaitingPeople => waitingPeople;
        public int StateCount(int state) => state >= 0 && state < stateCounts.Length ? stateCounts[state] : 0;

        /// <summary>리플레이 시작 시각 기준 t초의 시계 문자열.</summary>
        public string ClockAt(float t, bool withSeconds)
        {
            int start = replayData != null ? replayData.startHour * 3600 : 0;
            float total = start + t;
            int h = Mathf.FloorToInt(total / 3600f) % 24;
            int m = Mathf.FloorToInt(total % 3600f / 60f);
            if (!withSeconds) return $"{h:D2}:{m:D2}";
            return $"{h:D2}:{m:D2}:{Mathf.FloorToInt(total % 60f):D2}";
        }

        /// <summary>현재 프레임의 서비스 지표. v1 파일은 최종 지표와 맞도록 비율 보정.</summary>
        public void GetServiceKpis(out int requested, out int picked, out float avgWait, out int waiting, out int moves, out int timedOut)
        {
            FrameData cur = GetCurrentFrameData();
            requested = picked = waiting = moves = timedOut = 0;
            avgWait = 0f;
            if (cur == null) return;
            requested = cur.requested;
            picked = cur.picked;
            avgWait = cur.avgWait;
            waiting = cur.waiting;
            moves = cur.moves;
            timedOut = cur.timedOut;

            // v1 파일은 프레임 집계와 최종 지표가 어긋나 비율로 보정했었다. v2는 기록 단계에서
            // 같은 기준(타임아웃 승객 제외)으로 집계하므로 원래 값을 그대로 보여준다.
            if (replayData.version < 2 && replayData.metrics != null && replayData.metrics.n_measured > 0)
            {
                FrameData last = replayData.frames[replayData.frames.Count - 1];
                float pickRatio = last.picked > 0 ? (float)replayData.metrics.n_measured / last.picked : 1f;
                float waitRatio = last.avgWait > 0 ? replayData.metrics.avg_wait_sec / last.avgWait : 1f;
                picked = Mathf.RoundToInt(cur.picked * pickRatio);
                avgWait = cur.avgWait * waitRatio;
            }
        }

        public void Seek(float time)
        {
            if (replayData == null) return;
            currentTime = Mathf.Clamp(time, 0f, replayData.duration);
            UpdateSimulation(currentTime);
        }

        public void SetH3Visible(bool visible)
        {
            showH3Cells = visible;
            UpdateH3Visibility();
        }

        public void SetBuildingsVisible(bool visible)
        {
            showGangnamBuildings = visible;
            if (buildingsContainerObject != null) buildingsContainerObject.SetActive(visible);
        }

        /// <summary>추적 중인 택시: 이름, 운행 중 택시 가운데 순번/운행 대수, 현재 상태.</summary>
        public bool TryGetFollowedTaxi(out string taxiId, out int index, out int count, out int state)
        {
            taxiId = null;
            index = onRoadTaxis.IndexOf(selectedEntity);
            count = onRoadTaxis.Count;
            state = -1;
            if (selectedEntity < 0 || index < 0) return false;
            taxiId = replayData != null && selectedEntity < replayData.entities.Count ? replayData.entities[selectedEntity].id : null;
            if (lastVehicleStates.TryGetValue(selectedEntity, out int st)) state = st;
            return true;
        }

        private void OnDestroy()
        {
            ClearAllInstances();
            if (roadNetworkObject != null) Destroy(roadNetworkObject);
            if (buildingsContainerObject != null) Destroy(buildingsContainerObject);
        }
    }
}
