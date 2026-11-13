using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Fetex.Tests
{
    /// <summary>
    /// ReplayMotion.cs 테스트. 시뮬레이션 저장소의 tests/test_replay_motion.py와 같은 상황·같은 기대값이다
    /// (두 구현이 같은 규칙이어야 하므로 한쪽 테스트를 바꾸면 다른 쪽도 같이 바꿀 것).
    /// 가짜 교차로: 남→북 직선 차선 A(0,-50→0,-5), 교차로 내부 곡선 차선 J(우회전), 서→동 차선 B(5,0→55,0),
    /// A의 옆 차선 A_1(x=3.2).
    /// </summary>
    public class ReplayMotionTests
    {
        const float Len = 4f;

        static List<MotionLane> Lanes()
        {
            var arc = new List<float>();
            for (int k = 0; k < 9; k++)
            {
                double th = Math.PI - k * (Math.PI / 2) / 8;
                arc.Add((float)(5 + 5 * Math.Cos(th)));
                arc.Add((float)(-5 + 5 * Math.Sin(th)));
            }
            float arcLen = (float)(5 * Math.PI / 2);
            return new List<MotionLane>
            {
                new MotionLane("A_0", 0, 45f, 3.2f, new float[] { 0, -50, 0, -5 }),
                new MotionLane(":J_0_0", 1, arcLen, 3.2f, arc),
                new MotionLane("B_0", 2, 50f, 3.2f, new float[] { 5, 0, 55, 0 }),
                new MotionLane("A_1", 0, 45f, 3.2f, new float[] { 3.2f, -50, 3.2f, -5 }),
            };
        }

        // 차선 위 앞범퍼 위치 pos에 있는 차의 기록 상태 (차 중심 = 앞범퍼에서 길이/2 뒤)
        static MotionState State(List<MotionLane> lanes, int lane, float pos, float speed, float length = Len, int[] via = null)
        {
            lanes[lane].PointAt(pos, out float fx, out float fz);
            lanes[lane].PointAt(pos - 0.5f, out float bx, out float bz);
            double ang = Math.Atan2(fx - bx, fz - bz) * 180.0 / Math.PI;
            if (ang < 0) ang += 360;
            double rad = ang * Math.PI / 180.0;
            return new MotionState
            {
                Lane = lane, Pos = pos, Speed = speed, Angle = (float)ang, Via = via,
                X = (float)(fx - length / 2 * Math.Sin(rad)),
                Z = (float)(fz - length / 2 * Math.Cos(rad)),
            };
        }

        static float DistToLanes(float x, float z, List<MotionLane> lanes)
        {
            float best = float.MaxValue;
            foreach (var l in lanes)
            {
                // 차선을 0.1m 간격으로 따라가며 최솟값 (MotionLane은 꼭짓점 배열을 공개하지 않음)
                for (float s = 0; s <= l.Length; s += 0.1f)
                {
                    l.PointAt(s, out float px, out float pz);
                    best = Math.Min(best, (float)Math.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz)));
                }
            }
            return best;
        }

        static float AngleDiff(float a, float b) => Math.Abs(((a - b) % 360 + 540) % 360 - 180);

        [Test]
        public void TurnFollowsLanesNotChord()
        {
            var lanes = Lanes();
            var a = State(lanes, 0, 35f, 8f);
            var b = State(lanes, 2, 20f, 8f, via: new[] { 1 });
            float worstPath = 0, worstLinear = 0;
            for (int k = 1; k < 20; k++)
            {
                float f = k / 20f;
                Assert.IsTrue(ReplayMotion.Interpolate(lanes, a, b, f, 5f, Len, out float x, out float z, out _));
                worstPath = Math.Max(worstPath, DistToLanes(x, z, lanes));
                ReplayMotion.Linear(a, b, f, out float lx, out float lz, out _);
                worstLinear = Math.Max(worstLinear, DistToLanes(lx, lz, lanes));
            }
            Assert.Less(worstPath, 0.6f, "차선 중심선 위");
            Assert.Greater(worstLinear, 5f, "직선 보간은 모서리를 가로지름");
        }

        [Test]
        public void EndpointsMatchRecordedPositions()
        {
            var lanes = Lanes();
            var a = State(lanes, 0, 35f, 8f);
            var b = State(lanes, 2, 20f, 8f, via: new[] { 1 });
            foreach (var (f, s) in new[] { (0f, a), (1f, b) })
            {
                ReplayMotion.Interpolate(lanes, a, b, f, 5f, Len, out float x, out float z, out float h);
                Assert.Less(Math.Sqrt((x - s.X) * (x - s.X) + (z - s.Z) * (z - s.Z)), 1e-4);
                Assert.Less(AngleDiff(h, s.Angle), 3f);
            }
        }

        [Test]
        public void HeadingTurnsSmoothly()
        {
            var lanes = Lanes();
            var a = State(lanes, 0, 35f, 8f);
            var b = State(lanes, 2, 20f, 8f, via: new[] { 1 });
            var hs = new List<float>();
            for (int k = 0; k <= 40; k++)
            {
                ReplayMotion.Interpolate(lanes, a, b, k / 40f, 5f, Len, out _, out _, out float h);
                hs.Add(h);
            }
            for (int i = 1; i < hs.Count; i++) Assert.Less(AngleDiff(hs[i], hs[i - 1]), 12f, "한 번에 확 꺾이지 않음");
            Assert.Less(AngleDiff(hs[0], 0f), 3f);
            Assert.Less(AngleDiff(hs[hs.Count - 1], 90f), 3f);
        }

        [Test]
        public void LaneChangeAndFallbacks()
        {
            var lanes = Lanes();
            var a = State(lanes, 0, 10f, 10f);
            var b = State(lanes, 3, 30f, 10f);
            Assert.IsTrue(ReplayMotion.Interpolate(lanes, a, b, 0.5f, 2f, Len, out float x, out _, out _));
            Assert.That(x, Is.InRange(0.5f, 2.7f), "두 차선 사이");

            // 텔레포트 표시(-1), 너무 먼 이동, 같은 차선에서 뒤로 감 → 직선 보간으로 대체
            var tele = b; tele.Via = new[] { -1 };
            Assert.IsFalse(ReplayMotion.Interpolate(lanes, a, tele, 0.5f, 2f, Len, out _, out _, out _));
            var far = State(lanes, 2, 49f, 1f, via: new[] { 1 });
            Assert.IsFalse(ReplayMotion.Interpolate(lanes, a, far, 0.5f, 1f, Len, out _, out _, out _));
            Assert.IsFalse(ReplayMotion.Interpolate(lanes, State(lanes, 0, 30f, 0f), State(lanes, 0, 10f, 0f), 0.5f, 5f, Len, out _, out _, out _));
        }

        [Test]
        public void StoppedVehicleKeepsHeading()
        {
            var lanes = Lanes();
            var a = State(lanes, 0, 20f, 0f);
            Assert.IsTrue(ReplayMotion.Interpolate(lanes, a, a, 0.5f, 5f, Len, out float x, out float z, out float h));
            Assert.Less(Math.Sqrt((x - a.X) * (x - a.X) + (z - a.Z) * (z - a.Z)), 1e-4);
            Assert.Less(AngleDiff(h, a.Angle), 1e-3f);
        }

        [Test]
        public void RoadsideStopStaysOnLane()
        {
            // SUMO 갓길 정차: 기록 위치가 차선 폭(3.2m)만큼 오른쪽(+x, 북쪽 진행 기준)으로 비켜 있음
            var lanes = Lanes();
            var a = State(lanes, 0, 20f, 0f);
            var parked = a; parked.X = a.X + 3.2f;
            foreach (float f in new[] { 0f, 0.5f, 1f })
            {
                Assert.IsTrue(ReplayMotion.Interpolate(lanes, parked, parked, f, 5f, Len, out float x, out float z, out float h));
                Assert.AreEqual(a.X + ReplayMotion.MaxLateralOffset, x, 1e-4, "차선 오른쪽 가장자리까지만");
                Assert.AreEqual(a.Z, z, 1e-4);
                Assert.Less(AngleDiff(h, a.Angle), 1e-3f);
            }
            // 다음 프레임이 없는 차량도 같은 규칙
            Assert.IsTrue(ReplayMotion.Place(lanes, parked, Len, out float px, out float pz, out _));
            Assert.AreEqual(a.X + ReplayMotion.MaxLateralOffset, px, 1e-4);
            Assert.AreEqual(a.Z, pz, 1e-4);
            // 진행 방향(앞뒤) 오차는 그대로 둔다
            var ahead = a; ahead.Z = a.Z + 2f;
            ReplayMotion.Place(lanes, ahead, Len, out float ax, out float az, out _);
            Assert.AreEqual(a.X, ax, 1e-4);
            Assert.AreEqual(a.Z + 2f, az, 1e-4);
        }

        [Test]
        public void SlowLaneChangeIsContinuousAcrossFrames()
        {
            // 저속 차선 변경: A(차선0) -> B(옆 차선3, 3m 전진) -> C(차선3). 앞 구간 끝과 다음 구간 시작이 같아야 한다.
            var lanes = Lanes();
            const float L = 4.5f;
            var a = State(lanes, 0, 10f, 0.6f, L);
            var b = State(lanes, 3, 13f, 0.6f, L);
            var c = State(lanes, 3, 16f, 0.6f, L);
            ReplayMotion.Interpolate(lanes, a, b, 1f, 5f, L, out float ex, out float ez, out _);
            ReplayMotion.Interpolate(lanes, b, c, 0f, 5f, L, out float sx, out float sz, out _);
            Assert.Less(Math.Sqrt((ex - sx) * (ex - sx) + (ez - sz) * (ez - sz)), 1e-4, "프레임 경계에서 튀지 않음");
            Assert.Less(Math.Sqrt((ex - b.X) * (ex - b.X) + (ez - b.Z) * (ez - b.Z)), 1e-4, "차선 위 기록 위치는 그대로");
        }

        [Test]
        public void LerpAngleShortestPath()
        {
            Assert.AreEqual(360f, ReplayMotion.LerpAngle(350f, 10f, 0.5f), 1e-4);
            Assert.AreEqual(0f, ReplayMotion.LerpAngle(10f, 350f, 0.5f), 1e-4);
        }
    }
}
