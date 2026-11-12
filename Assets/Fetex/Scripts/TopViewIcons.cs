using UnityEngine;

namespace Fetex
{
    /// <summary>
    /// 탑뷰(정사영 수직 카메라)에서 차량·승객을 크게 보여주는 아이콘.
    /// 아이콘은 건물보다 높은 고도(IconAltitude)에 떠 있어 가려지지 않고, 수직 정사영이라 화면 위치는 차량 위치와 같다.
    /// 크기는 카메라 확대율에 맞춰 화면상 일정한 픽셀 크기로 유지한다.
    /// </summary>
    public static class TopViewIcons
    {
        public const float IconAltitude = 200f;

        private static Mesh pinMesh;
        private static Mesh discMesh;
        private static Mesh diamondMesh;

        /// <summary>원 + 앞쪽(+Z) 화살촉: 진행 방향이 보이는 차량 핀. 반지름 1 기준.</summary>
        public static Mesh Pin => pinMesh != null ? pinMesh : (pinMesh = BuildPin());
        public static Mesh Disc => discMesh != null ? discMesh : (discMesh = BuildDisc());
        public static Mesh Diamond => diamondMesh != null ? diamondMesh : (diamondMesh = BuildDiamond());

        /// <summary>
        /// parent 밑에 아이콘(테두리 + 채움)을 만든다. 반환값은 채움 렌더러(상태 색 교체용).
        /// </summary>
        public static Renderer Create(Transform parent, Mesh shape, Material fill, Material outline, out Transform root)
        {
            var go = new GameObject("TopViewIcon");
            root = go.transform;
            root.SetParent(parent, false);
            float sy = Mathf.Max(1e-4f, parent.lossyScale.y);
            root.localPosition = new Vector3(0f, (IconAltitude - parent.position.y) / sy, 0f);

            var border = NewPart("Outline", root, shape, outline);
            border.transform.localScale = Vector3.one * 1.3f;
            border.transform.localPosition = new Vector3(0f, -0.05f, 0f);

            return NewPart("Fill", root, shape, fill);
        }

        /// <summary>아이콘 월드 크기(반지름, m)를 맞춘다. 부모 스케일을 상쇄한다.</summary>
        public static void SetWorldRadius(Transform iconRoot, float radius)
        {
            Vector3 ps = iconRoot.parent != null ? iconRoot.parent.lossyScale : Vector3.one;
            iconRoot.localScale = new Vector3(radius / Mathf.Max(1e-4f, ps.x), 1f / Mathf.Max(1e-4f, ps.y), radius / Mathf.Max(1e-4f, ps.z));
        }

        private static Renderer NewPart(string name, Transform parent, Mesh mesh, Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return mr;
        }

        private static Mesh BuildPin()
        {
            const int seg = 24;
            var v = new Vector3[seg + 1 + 3];
            var t = new System.Collections.Generic.List<int>();
            v[0] = Vector3.zero;
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                v[i + 1] = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            }
            for (int i = 0; i < seg; i++) AddUp(t, v, 0, i + 1, (i + 1) % seg + 1);
            // 화살촉
            v[seg + 1] = new Vector3(0f, 0f, 1.75f);
            v[seg + 2] = new Vector3(-0.62f, 0f, 0.62f);
            v[seg + 3] = new Vector3(0.62f, 0f, 0.62f);
            AddUp(t, v, seg + 1, seg + 2, seg + 3);
            return Finish("TopViewPin", v, t);
        }

        private static Mesh BuildDisc()
        {
            const int seg = 20;
            var v = new Vector3[seg + 1];
            var t = new System.Collections.Generic.List<int>();
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                v[i + 1] = new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
            }
            for (int i = 0; i < seg; i++) AddUp(t, v, 0, i + 1, (i + 1) % seg + 1);
            return Finish("TopViewDisc", v, t);
        }

        private static Mesh BuildDiamond()
        {
            var v = new[] { Vector3.zero, new Vector3(0f, 0f, 1.2f), new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, -1.2f), new Vector3(-1f, 0f, 0f) };
            var t = new System.Collections.Generic.List<int>();
            for (int i = 1; i <= 4; i++) AddUp(t, v, 0, i, i % 4 + 1);
            return Finish("TopViewDiamond", v, t);
        }

        // 위(+Y)에서 볼 때 앞면이 되도록 감기 순서를 맞춘다
        private static void AddUp(System.Collections.Generic.List<int> t, Vector3[] v, int a, int b, int c)
        {
            if (Vector3.Cross(v[b] - v[a], v[c] - v[a]).y < 0f) { int x = b; b = c; c = x; }
            t.Add(a); t.Add(b); t.Add(c);
        }

        private static Mesh Finish(string name, Vector3[] v, System.Collections.Generic.List<int> t)
        {
            var m = new Mesh { name = name };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            // 고도 200m에 떠 있으니 부모 기준 바운드가 넓어도 컬링되지 않게 여유를 둔다
            m.bounds = new Bounds(Vector3.zero, new Vector3(4f, 1f, 4f));
            return m;
        }
    }
}
