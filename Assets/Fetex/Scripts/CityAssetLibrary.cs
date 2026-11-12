using System;
using UnityEngine;

namespace Fetex
{
    /// <summary>
    /// 런타임 도시 생성(GangnamCityBuilder)에 쓰는 에셋 묶음.
    /// 메뉴 Fetex ▸ City ▸ Rebuild City Asset Library 가 Kakao 에셋(판교 건물, 가로 시설물, 가로수,
    /// HDRP 머티리얼)으로 정규화 프리팹을 만들고 이 에셋을 채운다.
    /// </summary>
    [CreateAssetMenu(fileName = "GangnamCityAssets", menuName = "Fetex/City Asset Library")]
    public class CityAssetLibrary : ScriptableObject
    {
        [Serializable]
        public class BuildingEntry
        {
            [Tooltip("Y-up, 바닥 y=0, 바닥면 중심이 원점인 프리팹")]
            public GameObject prefab;
            [Tooltip("월드 크기(m). x=폭, y=높이, z=깊이")]
            public Vector3 size;
            [Tooltip("선택 가중치 (폴리곤이 많은 건물은 낮게)")]
            public float weight = 1f;
        }

        [Header("Road surface (world-space UV, m per tile)")]
        public Material roadAsphalt;
        public float asphaltTileMeters = 8f;
        public Material laneWhite;
        public Material laneYellow;

        [Header("Sidewalk / ground")]
        public Material sidewalk;
        public float sidewalkTileMeters = 3f;
        public Material curb;
        public Material ground;
        public float groundTileMeters = 6f;

        [Header("Buildings (Kakao_08_PanGyo)")]
        public BuildingEntry[] buildings;

        [Header("Street furniture")]
        [Tooltip("가로등. 팔이 로컬 +Z(도로 쪽)를 향함")]
        public GameObject streetLight;
        [Tooltip("교차로 신호등 기둥. 주 팔이 로컬 +Z, 보조 팔이 로컬 +X")]
        public GameObject trafficSignal;
        [Tooltip("차로 4개 이상 도로용 큰 신호등 기둥")]
        public GameObject trafficSignalLarge;
        public GameObject[] streetTrees;

        [Header("Placement")]
        public float sidewalkWidth = 3.5f;
        public float streetLightSpacing = 32f;
        public float treeSpacing = 15f;
        public Vector2 treeScale = new Vector2(1.8f, 2.4f);
    }
}
