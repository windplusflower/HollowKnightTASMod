using System;
using System.Globalization;
using System.IO;
using System.Text;
using HollowKnightTAS.Runtime.Rng;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private bool replayStateTraceEnabled = Environment.GetEnvironmentVariable("HKTAS_REPLAY_STATE_TRACE") == "1";
        private StreamWriter? replayStateTrace;
        private bool acceptanceFrameTraceEnabled = Environment.GetEnvironmentVariable("HKTAS_ACCEPTANCE_FRAME_TRACE") == "1";
        private StreamWriter? acceptanceFrameTrace;
        private UnityRandomStateCodec_1_5_78_11833? acceptanceRngCodec;
        private string acceptanceTraceSegment = "baseline";
        private int acceptanceRestoreSegmentCount;

        private void AdvanceAcceptanceTraceSegment(string checkpointId)
        {
            if (!acceptanceFrameTraceEnabled) return;
            acceptanceRestoreSegmentCount++;
            acceptanceTraceSegment = "s0-restore-"
                + acceptanceRestoreSegmentCount.ToString(CultureInfo.InvariantCulture)
                + ":" + checkpointId;
        }

        private void TraceReplayState(long nativeFrame)
        {
            if (!replayStateTraceEnabled || !frameInputEnabled || movieFrame > 20000) return;
            try
            {
                if (replayStateTrace == null)
                {
                    replayStateTrace = new StreamWriter(Path.Combine(sessionDirectory, "replay-state-trace.csv"));
                    replayStateTrace.WriteLine("native,movie,scene,x,y,bodyX,bodyY,vx,vy,time,fixedTime,timeDouble,fixedTimeDouble,dt,fixedDt,interpolation,cinematic");
                }
                var hero = HeroController.SilentInstance;
                var body = hero == null ? null : hero.GetComponent<Rigidbody2D>();
                var position = hero == null ? default(Vector3) : hero.transform.position;
                var bodyPosition = body == null ? default(Vector2) : body.position;
                var velocity = body == null ? default(Vector2) : body.velocity;
                replayStateTrace.WriteLine(string.Join(",", nativeFrame, movieFrame,
                    UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                    F(position.x), F(position.y), F(bodyPosition.x), F(bodyPosition.y), F(velocity.x), F(velocity.y),
                    F(Time.time), F(Time.fixedTime), Time.timeAsDouble.ToString("R", CultureInfo.InvariantCulture),
                    Time.fixedTimeAsDouble.ToString("R", CultureInfo.InvariantCulture), F(Time.deltaTime), F(Time.fixedDeltaTime),
                    body == null ? "none" : body.interpolation.ToString(), DeterministicCinematics.TraceState));
                replayStateTrace.Flush();
            }
            catch (Exception error)
            {
                replayStateTraceEnabled = false;
                Modding.Logger.LogWarn("[HKTAS] Replay state trace disabled: " + error.Message);
            }
        }

        private void TraceAcceptanceFrame(long nativeFrame, long executedMovieFrame)
        {
            if (!acceptanceFrameTraceEnabled || !frameInputEnabled || executedMovieFrame < 0) return;
            try
            {
                if (acceptanceFrameTrace == null)
                {
                    acceptanceFrameTrace = new StreamWriter(
                        Path.Combine(sessionDirectory, "acceptance-frame-trace.csv"),
                        append: false,
                        new UTF8Encoding(false));
                    acceptanceFrameTrace.WriteLine("segment,native,movie,scene,rngSha256,rngRolls,rngDelta,timeDouble,fixedTimeDouble,tftMs");
                }
                acceptanceRngCodec = acceptanceRngCodec ?? UnityRandomStateCodec_1_5_78_11833.Resolve().Codec
                    ?? throw new InvalidOperationException("RNG state codec unavailable.");
                var rngSha256 = acceptanceRngCodec.CaptureCurrent().Sha256;
                var rngRolls = infoRng.RollTimes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                var rngDelta = infoRng.LastDelta?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
                var timeDouble = Time.timeAsDouble;
                var fixedTimeDouble = Time.fixedTimeAsDouble;
                var tftMs = (timeDouble - fixedTimeDouble) * 1000.0;
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name ?? string.Empty;
                acceptanceFrameTrace.WriteLine(string.Join(",",
                    CsvCell(acceptanceTraceSegment),
                    nativeFrame.ToString(CultureInfo.InvariantCulture),
                    executedMovieFrame.ToString(CultureInfo.InvariantCulture),
                    CsvCell(scene),
                    rngSha256,
                    rngRolls,
                    rngDelta,
                    timeDouble.ToString("R", CultureInfo.InvariantCulture),
                    fixedTimeDouble.ToString("R", CultureInfo.InvariantCulture),
                    tftMs.ToString("R", CultureInfo.InvariantCulture)));
                acceptanceFrameTrace.Flush();
            }
            catch (Exception error)
            {
                acceptanceFrameTraceEnabled = false;
                Modding.Logger.LogWarn("[HKTAS] Acceptance frame trace disabled: " + error.Message);
            }
        }

        private static string CsvCell(string value)
        {
            if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
