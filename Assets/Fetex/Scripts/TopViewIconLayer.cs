using System.Collections.Generic;
using UnityEngine;

namespace Fetex
{
    /// <summary>
    /// 탑뷰 아이콘 관리: 차량 핀·승객 마름모의 생성, 상태 색, 탑뷰/추적 전환 시 표시, 추적한 택시 강조,
    /// 카메라 확대율에 맞춘 화면상 고정 크기. (메시·머티리얼 모양은 TopViewIcons, 상태 색은 FetexReplayPlayer가 정한다)
    /// </summary>
    public sealed class TopViewIconLayer
    {
        const float MinVehicleRadius = 2.4f;   // 확대해도 실제 차보다 작아지지 않게 (m)
        const float MinPersonRadius = 0.8f;

        readonly Material outline;
        readonly Material outlineDark;
        readonly Dictionary<int, Renderer> fills = new Dictionary<int, Renderer>();
        readonly Dictionary<int, Transform> roots = new Dictionary<int, Transform>();
        readonly Dictionary<int, Transform> outlines = new Dictionary<int, Transform>();
        readonly Dictionary<int, bool> isBig = new Dictionary<int, bool>();
        readonly List<Transform> persons = new List<Transform>();

        bool scaleDirty = true;
        float lastOrtho = -1f;
        int lastScreenH = -1;
        Vector3 lastPx;

        public bool Visible { get; private set; } = true;
        public int Highlighted { get; private set; } = -1;

        public TopViewIconLayer(Material outline, Material outlineDark)
        {
            this.outline = outline;
            this.outlineDark = outlineDark;
        }

        /// <summary>차량 아이콘. bigPin = 택시·자율주행차(흰 테두리 핀), 아니면 작은 원/마름모(어두운 테두리).</summary>
        public void AddVehicle(int entity, Transform vehicle, Mesh shape, bool bigPin, Material fill)
        {
            Renderer r = TopViewIcons.Create(vehicle, shape, fill, bigPin ? outline : outlineDark, out Transform root);
            fills[entity] = r;
            roots[entity] = root;
            outlines[entity] = root.Find(TopViewIcons.OutlineName);
            isBig[entity] = bigPin;
            if (entity == Highlighted) TopViewIcons.SetHighlighted(outlines[entity], true);
            root.gameObject.SetActive(Visible);
            scaleDirty = true;
        }

        public void SetFill(int entity, Material mat)
        {
            if (fills.TryGetValue(entity, out Renderer r)) r.sharedMaterial = mat;
        }

        public void AddPerson(Transform pin, Material fill)
        {
            TopViewIcons.Create(pin, TopViewIcons.Diamond, fill, outline, out Transform root);
            root.gameObject.SetActive(Visible);
            persons.Add(root);
            scaleDirty = true;
        }

        public void SetVisible(bool visible)
        {
            Visible = visible;
            foreach (var kv in roots) if (kv.Value != null) kv.Value.gameObject.SetActive(visible);
            foreach (var t in persons) if (t != null) t.gameObject.SetActive(visible);
            scaleDirty = true;
        }

        /// <summary>추적한 택시 하나만 테두리를 굵게 (크기는 그대로).</summary>
        public void Highlight(int entity)
        {
            if (outlines.TryGetValue(Highlighted, out Transform old) && old != null) TopViewIcons.SetHighlighted(old, false);
            Highlighted = entity;
            if (outlines.TryGetValue(entity, out Transform now) && now != null) TopViewIcons.SetHighlighted(now, true);
        }

        /// <summary>정사영 크기·화면 높이·픽셀 설정이 바뀌었을 때만 아이콘을 화면상 일정한 크기로 다시 맞춘다.</summary>
        public void UpdateScale(float orthoSize, float bigPx, float smallPx, float personPx)
        {
            if (!Visible) return;
            var px = new Vector3(bigPx, smallPx, personPx);
            if (!scaleDirty && Mathf.Approximately(orthoSize, lastOrtho) && Screen.height == lastScreenH && px == lastPx) return;
            lastOrtho = orthoSize;
            lastScreenH = Screen.height;
            lastPx = px;
            scaleDirty = false;

            float metersPerPx = 2f * orthoSize / Mathf.Max(1, Screen.height);
            foreach (var kv in roots)
            {
                if (kv.Value == null) continue;
                float r = (isBig[kv.Key] ? bigPx : smallPx) * metersPerPx;
                TopViewIcons.SetWorldRadius(kv.Value, Mathf.Max(r, MinVehicleRadius));
            }
            float personR = Mathf.Max(personPx * metersPerPx, MinPersonRadius);
            foreach (var t in persons) if (t != null) TopViewIcons.SetWorldRadius(t, personR);
        }

        /// <summary>리플레이 전환 시 (차량·승객 오브젝트는 FetexReplayPlayer가 지운다).</summary>
        public void Clear()
        {
            fills.Clear();
            roots.Clear();
            outlines.Clear();
            isBig.Clear();
            persons.Clear();
            Highlighted = -1;
            scaleDirty = true;
        }
    }
}
