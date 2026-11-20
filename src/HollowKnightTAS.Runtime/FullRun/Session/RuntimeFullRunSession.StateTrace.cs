using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private bool replayStateTraceEnabled = Environment.GetEnvironmentVariable("HKTAS_REPLAY_STATE_TRACE") == "1";
        private StreamWriter? replayStateTrace;

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

        private static string F(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    }
}
