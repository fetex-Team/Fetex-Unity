using System;
using System.Collections.Generic;

namespace Fetex
{
    /// <summary>
    /// 한 전략 리플레이의 서비스 지표 시계열 (프레임마다 누적 호출·탑승·평균 대기·대기 승객·취소·재배치).
    /// 전략 비교 카드가 같은 시각의 세 전략 값을 나란히 보여줄 때 쓴다. 차량 위치 등은 담지 않아 가볍다.
    /// </summary>
    public sealed class StrategyKpiSeries
    {
        public readonly string Fingerprint;
        readonly float[] t;
        readonly int[] requested, picked, waiting, timedOut, moves;
        readonly float[] avgWait;

        public StrategyKpiSeries(ReplayData data)
        {
            Fingerprint = data.fingerprint;
            List<FrameData> frames = data.frames;
            int n = frames.Count;
            t = new float[n];
            requested = new int[n];
            picked = new int[n];
            waiting = new int[n];
            timedOut = new int[n];
            moves = new int[n];
            avgWait = new float[n];
            for (int i = 0; i < n; i++)
            {
                FrameData f = frames[i];
                t[i] = f.t;
                requested[i] = f.requested;
                picked[i] = f.picked;
                waiting[i] = f.waiting;
                timedOut[i] = f.timedOut;
                moves[i] = f.moves;
                avgWait[i] = f.avgWait;
            }
        }

        public struct Kpi
        {
            public int Requested, Picked, Waiting, TimedOut, Moves;
            public float AvgWait;
            public float FulfillRate => Requested > 0 ? Picked * 100f / Requested : 100f;
        }

        /// <summary>시각 time(초)에 해당하는 프레임(그 시각 이전 마지막 프레임)의 지표.</summary>
        public Kpi At(float time)
        {
            int i = Array.BinarySearch(t, time);
            if (i < 0) i = Math.Max(0, ~i - 1);
            if (i >= t.Length) i = t.Length - 1;
            return new Kpi
            {
                Requested = requested[i], Picked = picked[i], Waiting = waiting[i],
                TimedOut = timedOut[i], Moves = moves[i], AvgWait = avgWait[i],
            };
        }
    }
}
