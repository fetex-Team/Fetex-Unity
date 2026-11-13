using UnityEngine;
using UnityEngine.InputSystem;
using Fetex;

// 같은 카메라를 운영 탑뷰와 선택 차량 후방 시점 사이에서 전환한다.
// 탑뷰: 마우스 휠 = 커서 기준 확대/축소, 드래그(왼쪽·가운데·오른쪽) = 이동, WASD/방향키 = 이동, Q/E = 확대/축소, R = 처음 화면
// Tab = 탑뷰/추적 전환. 화면 UI는 FetexHud(UI Toolkit)가 그린다.
[RequireComponent(typeof(Camera))]
public sealed class TwinCameraView : MonoBehaviour
{
    public Transform target;
    public Vector3 mapCenter = new Vector3(0, 0, -0.559f);
    public float overviewSize = 225f;
    public Vector3 followOffset = new Vector3(0, 3.5f, -9f);
    public bool overview = true;
    [Tooltip("추적 시점 회전 추종 속도 (클수록 빨리 따라감, 1/초)")]
    public float followTurnRate = 4f;

    [Header("Overview pan / zoom")]
    public float minOverviewSize = 25f;
    public float maxOverviewSize = 1100f;
    [Tooltip("휠 한 칸당 확대 비율")]
    [Range(0.02f, 0.5f)] public float wheelZoomStep = 0.15f;
    [Tooltip("키보드 이동 속도 (초당 화면 높이 비율)")]
    public float keyPanSpeed = 0.9f;
    [Tooltip("이동 가능 범위: 지도 중심에서 최대 거리(m)")]
    public float panLimit = 1400f;

    Camera view;
    Vector3 homeCenter;
    float homeSize;
    bool dragging;
    Vector3 dragAnchor;
    // 추적 시점 상태
    Transform followTarget;
    float followYaw;
    Vector3 lastTargetPos;
    bool wasOverview = true;

    void Awake()
    {
        view = GetComponent<Camera>();
        homeCenter = mapCenter;
        homeSize = overviewSize;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.tabKey.wasPressedThisFrame)
        {
            overview = !overview;
            if (!overview && target == null && FetexReplayPlayer.Instance != null) FetexReplayPlayer.Instance.SelectFirstTaxiForCamera();
        }
        if (overview) HandleOverviewInput();
    }

    void LateUpdate()
    {
        ApplyView();
    }

    public void ApplyView()
    {
        if (view == null) view = GetComponent<Camera>();
        view.orthographic = overview;
        if (overview)
        {
            view.orthographicSize = overviewSize;
            transform.SetPositionAndRotation(mapCenter + Vector3.up * 500f, Quaternion.Euler(90, 0, 0));
        }
        else if (target != null)
        {
            // 차 방향(yaw)만 부드럽게 따라간다. 위치는 정확히 따라가야 고배속에서도 차를 놓치지 않는다.
            float targetYaw = target.eulerAngles.y;
            bool snap = target != followTarget || wasOverview || (target.position - lastTargetPos).sqrMagnitude > 30f * 30f;
            followYaw = snap ? targetYaw : Mathf.LerpAngle(followYaw, targetYaw, 1f - Mathf.Exp(-followTurnRate * Time.unscaledDeltaTime));
            followTarget = target;
            lastTargetPos = target.position;

            Quaternion yaw = Quaternion.Euler(0f, followYaw, 0f);
            transform.position = target.position + yaw * followOffset;
            transform.LookAt(target.position + yaw * Vector3.forward * 5f + Vector3.up * 1.1f);
        }
        wasOverview = overview;
    }

    public void ResetOverview()
    {
        mapCenter = homeCenter;
        overviewSize = homeSize;
        overview = true;
        ApplyView();
    }

    void HandleOverviewInput()
    {
        var mouse = Mouse.current;
        var kb = Keyboard.current;

        if (mouse != null)
        {
            Vector2 mp = mouse.position.ReadValue();
            bool overUi = IsPointerOverUi(mp);

            // 휠: 커서 아래 지점을 고정한 채 확대/축소
            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) > 0.01f && !overUi && !dragging)
            {
                Vector3 before = ScreenToGround(mp);
                float factor = Mathf.Pow(1f - wheelZoomStep, Mathf.Sign(scroll));
                overviewSize = Mathf.Clamp(overviewSize * factor, minOverviewSize, maxOverviewSize);
                ApplyView();
                mapCenter += before - ScreenToGround(mp);
            }

            // 드래그 이동: 잡은 지점이 커서를 따라가게
            bool pressedThisFrame = mouse.leftButton.wasPressedThisFrame || mouse.middleButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame;
            bool held = mouse.leftButton.isPressed || mouse.middleButton.isPressed || mouse.rightButton.isPressed;
            if (pressedThisFrame && !overUi)
            {
                dragging = true;
                dragAnchor = ScreenToGround(mp);
            }
            if (dragging)
            {
                if (!held) dragging = false;
                else
                {
                    ApplyView();
                    mapCenter += dragAnchor - ScreenToGround(mp);
                }
            }
        }

        if (kb != null)
        {
            Vector2 move = Vector2.zero;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) move.y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) move.y -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) move.x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) move.x -= 1f;
            if (move != Vector2.zero)
            {
                float speed = overviewSize * 2f * keyPanSpeed * Time.unscaledDeltaTime;
                mapCenter += new Vector3(move.x, 0f, move.y).normalized * speed;
            }
            float zoom = 0f;
            if (kb.qKey.isPressed || kb.numpadPlusKey.isPressed) zoom -= 1f;
            if (kb.eKey.isPressed || kb.numpadMinusKey.isPressed) zoom += 1f;
            if (zoom != 0f)
            {
                overviewSize = Mathf.Clamp(overviewSize * Mathf.Exp(zoom * 1.5f * Time.unscaledDeltaTime), minOverviewSize, maxOverviewSize);
            }
            if (kb.rKey.wasPressedThisFrame) ResetOverview();
        }

        Vector3 offset = mapCenter - homeCenter;
        offset.y = 0f;
        if (offset.magnitude > panLimit) mapCenter = homeCenter + offset.normalized * panLimit;
        mapCenter.y = homeCenter.y;
    }

    Vector3 ScreenToGround(Vector2 screen)
    {
        Ray ray = view.ScreenPointToRay(new Vector3(screen.x, screen.y, 0f));
        float y = homeCenter.y;
        if (Mathf.Abs(ray.direction.y) < 1e-5f) return new Vector3(ray.origin.x, y, ray.origin.z);
        float t = (y - ray.origin.y) / ray.direction.y;
        return ray.origin + ray.direction * t;
    }

    // HUD(UI Toolkit) 패널 위에서는 휠·드래그를 카메라가 받지 않는다
    static bool IsPointerOverUi(Vector2 screen) => FetexHud.IsPointerOverUi(screen);
}
