using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Fetex;
using UnityEditor;
using UnityEngine;

// Kakao 에셋으로 런타임 도시 생성용 정규화 프리팹과 CityAssetLibrary를 만든다.
// 원본 FBX는 Z-up(루트 X 270°)이라 그대로 쓰면 눕거나 피벗이 블록 기준으로 어긋난다.
// 여기서 Y-up, 바닥 y=0, 바닥 중심 원점으로 맞춰 두면 런타임은 위치·Yaw만 정하면 된다.
public static class CityAssetLibraryBuilder
{
    public const string LibraryPath = "Assets/Fetex/Settings/GangnamCityAssets.asset";
    const string PrefabDir = "Assets/Fetex/Prefabs/City";
    const string MaterialDir = "Assets/Fetex/Materials";

    const string ModelDir = "Assets/00_Model/02_Kakao/02_Model";
    const string KakaoPrefabDir = "Assets/00_Model/02_Kakao/03_Prefabs";
    const string PangyoFbx = ModelDir + "/Kakao_08_PanGyo.fbx";
    const string SampleStreet = "Assets/Map/street_main_1.prefab";

    // 폴리곤이 지나치게 많은 건물은 덜 뽑히게 한다 (Building_08 7.2만, Building_12 2.6만 삼각형).
    static readonly Dictionary<string, float> WeightOverride = new Dictionary<string, float>
    {
        { "Building_08", 0.25f },
        { "Building_12", 0.5f },
    };

    [MenuItem("Fetex/City/Rebuild City Asset Library")]
    public static void Build()
    {
        Directory.CreateDirectory(PrefabDir);
        Directory.CreateDirectory(MaterialDir);
        Directory.CreateDirectory(Path.GetDirectoryName(LibraryPath));

        var lib = AssetDatabase.LoadAssetAtPath<CityAssetLibrary>(LibraryPath);
        if (lib == null)
        {
            lib = ScriptableObject.CreateInstance<CityAssetLibrary>();
            AssetDatabase.CreateAsset(lib, LibraryPath);
        }

        lib.roadAsphalt = LoadMat("Asphalt_01");
        lib.sidewalk = LoadMat("TileFloor_01_01");
        lib.curb = LoadMat("Concrete_01");
        lib.ground = LoadMat("TileFloor_02_01");
        lib.laneWhite = PaintMaterial("Mat_RoadPaintWhite", new Color(0.92f, 0.92f, 0.9f));
        lib.laneYellow = PaintMaterial("Mat_RoadPaintYellow", new Color(1.0f, 0.72f, 0.08f));

        lib.buildings = BuildBuildingPrefabs();
        lib.streetLight = WrapProp("StreetLight",
            PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(KakaoPrefabDir + "/RoadLight_04.prefab")) as GameObject,
            armAngle: 0f);
        lib.trafficSignal = WrapProp("TrafficSignal", CloneFromSample("RoadTrafficLight_03"), armAngle: 45f);
        lib.trafficSignalLarge = WrapProp("TrafficSignalLarge", CloneFromSample("RoadTrafficLight_04"), armAngle: 45f);
        lib.streetTrees = new[] { "SM_Elm04", "SM_AmurCork04", "SM_Elm01" }
            .Select(n => AssetDatabase.LoadAssetAtPath<GameObject>($"{KakaoPrefabDir}/{n}.prefab"))
            .Where(g => g != null)
            .ToArray();

        EditorUtility.SetDirty(lib);
        AssetDatabase.SaveAssets();
        Debug.Log($"[CityAssetLibraryBuilder] {LibraryPath}: buildings {lib.buildings.Length}, trees {lib.streetTrees.Length}, " +
                  $"light {(lib.streetLight ? "ok" : "missing")}, signal {(lib.trafficSignal ? "ok" : "missing")}/{(lib.trafficSignalLarge ? "ok" : "missing")}");
    }

    static Material LoadMat(string name)
    {
        var m = AssetDatabase.LoadAssetAtPath<Material>($"{ModelDir}/Materials/{name}.mat");
        if (m == null) Debug.LogWarning($"[CityAssetLibraryBuilder] material not found: {name}");
        return m;
    }

    static Material PaintMaterial(string name, Color color)
    {
        string path = $"{MaterialDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("HDRP/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }
        mat.SetColor("_BaseColor", color);
        mat.SetFloat("_Smoothness", 0.35f);
        mat.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // 판교 블록 안의 건물 중 유형(Building_XX)별 첫 번째 것을 정규화 프리팹으로 저장.
    static CityAssetLibrary.BuildingEntry[] BuildBuildingPrefabs()
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(PangyoFbx);
        var rootRot = model.transform.localRotation;
        var seen = new SortedDictionary<string, Transform>();
        foreach (Transform block in model.transform)
        {
            foreach (Transform b in block)
            {
                var m = Regex.Match(b.name, @"^[Bb]uilding_(\d+)");
                if (!m.Success) continue;
                string key = "Building_" + m.Groups[1].Value.PadLeft(2, '0');
                if (!seen.ContainsKey(key)) seen[key] = b;
            }
        }

        var farMat = PaintMaterial("Mat_BuildingFar", new Color(0.78f, 0.80f, 0.83f));
        var entries = new List<CityAssetLibrary.BuildingEntry>();
        foreach (var kv in seen)
        {
            var root = new GameObject(kv.Key);
            var body = Object.Instantiate(kv.Value.gameObject, root.transform);
            body.name = kv.Value.name;
            body.transform.localPosition = Vector3.zero;
            body.transform.localRotation = rootRot * kv.Value.localRotation;
            body.transform.localScale = kv.Value.localScale;

            Bounds b = RendererBounds(root);
            body.transform.localPosition = new Vector3(-b.center.x, -b.min.y, -b.center.z);
            b = RendererBounds(root);
            AddFarLod(root, body, b, farMat);

            string path = $"{PrefabDir}/{kv.Key}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            entries.Add(new CityAssetLibrary.BuildingEntry
            {
                prefab = prefab,
                size = b.size,
                weight = WeightOverride.TryGetValue(kv.Key, out float w) ? w : 1f,
            });
        }
        return entries.ToArray();
    }

    // 멀리서(화면 높이의 FarLodScreenHeight 미만) 보일 때는 바운드 크기 상자 하나로 그린다.
    // LOD 크기는 건물의 가장 긴 변(대개 높이) 기준. 탑뷰 기본 배율(화면 450m)에서는 높이 31.5m 이상 원본,
    // 최대 축소(화면 1040m)에서는 73m 이상만 원본 → 축소할수록 저층부터 상자로 바뀐다.
    const float FarLodScreenHeight = 0.07f;

    static void AddFarLod(GameObject root, GameObject body, Bounds b, Material farMat)
    {
        var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(box.GetComponent<Collider>());
        box.name = "LOD1_Box";
        box.transform.SetParent(root.transform, false);
        box.transform.localPosition = new Vector3(b.center.x, b.size.y * 0.5f, b.center.z);
        box.transform.localScale = b.size;
        var boxRenderer = box.GetComponent<MeshRenderer>();
        boxRenderer.sharedMaterial = farMat;

        var lod = root.AddComponent<LODGroup>();
        lod.SetLODs(new[]
        {
            new LOD(FarLodScreenHeight, body.GetComponentsInChildren<Renderer>(true)),
            new LOD(0f, new Renderer[] { boxRenderer }),
        });
        lod.RecalculateBounds();
    }

    // street_main_1 샘플에서 신호등 헤드까지 조립된 기둥을 그대로 복제.
    static GameObject CloneFromSample(string name)
    {
        var sample = AssetDatabase.LoadAssetAtPath<GameObject>(SampleStreet);
        var src = sample.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == name && t.childCount > 0)
               ?? sample.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name.StartsWith(name) && t.childCount > 0);
        if (src == null)
        {
            Debug.LogWarning($"[CityAssetLibraryBuilder] {name} not found in {SampleStreet}");
            return null;
        }
        var clone = Object.Instantiate(src.gameObject);
        clone.name = name;
        clone.transform.localScale = Vector3.one;
        clone.transform.rotation = Quaternion.Euler(270f, 0f, 0f);   // FBX 기본 방향(Y-up)
        return clone;
    }

    // 소품을 바닥 중심 원점에 두고, 팔 방향(바운드 중심 방향)이 armAngle(로컬 +Z 기준 시계방향 각)을 향하게 회전.
    static GameObject WrapProp(string name, GameObject body, float armAngle)
    {
        if (body == null) return null;
        var root = new GameObject(name);
        body.transform.SetParent(root.transform, true);
        body.transform.position = Vector3.zero;

        // 원본 피벗(기둥 밑동)은 수평 위치 그대로 두고, 높이만 바닥 y=0에 맞춘다.
        Bounds b = RendererBounds(root);
        Vector3 c = b.center;
        float current = Mathf.Atan2(c.x, c.z) * Mathf.Rad2Deg;
        float delta = Mathf.Round((armAngle - current) / 90f) * 90f;
        if (Mathf.Abs(new Vector2(c.x, c.z).magnitude) > 0.3f)
        {
            body.transform.RotateAround(Vector3.zero, Vector3.up, delta);
        }
        b = RendererBounds(root);
        body.transform.position += new Vector3(0f, -b.min.y, 0f);

        string path = $"{PrefabDir}/{name}.prefab";
        var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        return prefab;
    }

    static Bounds RendererBounds(GameObject go)
    {
        var rs = go.GetComponentsInChildren<Renderer>(true);
        if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }
}
