using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using GlobalEnums;
using HollowKnightTAS.Runtime.Rng;
using HutongGames.PlayMaker;
using Newtonsoft.Json;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal sealed class LeanS0K3TraceWriter : IDisposable
    {
        private readonly string filePath;
        private readonly StreamWriter writer;
        private readonly UnityRandomStateCodec_1_5_78_11833 rngCodec;
        private int rowsWritten;

        internal LeanS0K3TraceWriter(string filePath)
        {
            this.filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            writer = new StreamWriter(filePath, append: false, new UTF8Encoding(false));
            rngCodec = UnityRandomStateCodec_1_5_78_11833.Resolve().Codec!;
        }

        internal int RowsWritten => rowsWritten;
        internal string FilePath => filePath;

        internal void WriteHeader(object metadata)
        {
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                recordType = "header",
                schema = "lean-s0-k3-trace-v1",
                metadata
            }));
            writer.Flush();
        }

        internal void SampleFrame(long movieFrame, long nativeFrame, string boundary)
        {
            GameManager? gm = GameManager.instance;
            PlayerData? pd = PlayerData.instance;
            HeroController? hero = HeroController.SilentInstance;
            Rigidbody2D? heroRb = hero != null ? hero.GetComponent<Rigidbody2D>() : null;
            HeroControllerStates? cs = hero != null ? hero.cState : null;

            string sceneName = gm != null ? (gm.sceneName ?? string.Empty) : string.Empty;
            string gameState = gm != null ? gm.gameState.ToString() : "Unknown";
            string rngSha = rngCodec.CaptureCurrent().Sha256;

            object? heroObj = null;
            if (hero != null)
            {
                Vector3 pos = hero.transform.position;
                Vector2 vel = heroRb != null ? heroRb.velocity : Vector2.zero;
                heroObj = new
                {
                    pos = new[] { pos.x, pos.y, pos.z },
                    vel = new[] { vel.x, vel.y },
                    facingRight = cs != null && cs.facingRight,
                    onGround = cs != null && cs.onGround,
                    jumping = cs != null && cs.jumping,
                    doubleJumping = cs != null && cs.doubleJumping,
                    dashing = cs != null && cs.dashing,
                    shadowDashing = cs != null && cs.shadowDashing,
                    attacking = cs != null && cs.attacking,
                    upAttacking = cs != null && cs.upAttacking,
                    downAttacking = cs != null && cs.downAttacking,
                    wallSliding = cs != null && cs.wallSliding,
                    invulnerable = cs != null && cs.invulnerable,
                    recoiling = cs != null && cs.recoiling,
                    dead = cs != null && cs.dead,
                    hazardDeath = cs != null && cs.hazardDeath,
                    nailCharging = cs != null && cs.nailCharging,
                    superDashing = cs != null && cs.superDashing,
                    casting = cs != null && cs.casting
                };
            }

            object? playerObj = null;
            if (pd != null)
            {
                int[] charms = pd.equippedCharms != null
                    ? pd.equippedCharms.OrderBy(c => c).ToArray()
                    : Array.Empty<int>();
                playerObj = new
                {
                    health = pd.health,
                    healthBlue = pd.healthBlue,
                    maxHealth = pd.maxHealth,
                    maxHealthBase = pd.maxHealthBase,
                    mpCharge = pd.MPCharge,
                    mpReserve = pd.MPReserve,
                    geo = pd.geo,
                    nailDamage = pd.nailDamage,
                    equippedCharms = charms
                };
            }

            var enemies = CaptureEnemies();
            var grimmchild = CaptureGrimmchildSummary();

            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                recordType = "frame",
                movieFrame,
                nativeFrame,
                boundary,
                scene = sceneName,
                gameState,
                rngSha256 = rngSha,
                hero = heroObj,
                player = playerObj,
                enemies,
                grimmchild
            }));
            rowsWritten++;
            if ((rowsWritten & 7) == 0)
            {
                writer.Flush();
            }
        }

        private static List<object> CaptureEnemies()
        {
            var list = new List<object>();
            HealthManager[] managers = UnityEngine.Object.FindObjectsOfType<HealthManager>();
            var ordered = managers
                .Where(m => m != null && m.gameObject.activeInHierarchy)
                .Select(m => new { Manager = m, Path = GetTransformPath(m.transform) })
                .OrderBy(x => x.Path, StringComparer.Ordinal)
                .ToList();

            foreach (var entry in ordered)
            {
                HealthManager hm = entry.Manager;
                Vector3 pos = hm.transform.position;
                var fsmStates = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (PlayMakerFSM fsm in hm.GetComponents<PlayMakerFSM>())
                {
                    if (fsm != null)
                    {
                        fsmStates[fsm.FsmName ?? string.Empty] = fsm.ActiveStateName ?? string.Empty;
                    }
                }
                list.Add(new
                {
                    path = entry.Path,
                    hp = hm.hp,
                    isDead = hm.isDead,
                    pos = new[] { pos.x, pos.y },
                    fsmStates
                });
            }
            return list;
        }

        private static object? CaptureGrimmchildSummary()
        {
            GameObject? gc = null;
            try
            {
                gc = GameObject.FindGameObjectWithTag("Grimmchild");
            }
            catch
            {
                gc = null;
            }
            if (gc == null) return null;

            Vector3 pos = gc.transform.position;
            Rigidbody2D? rb = gc.GetComponent<Rigidbody2D>();
            Vector2 vel = rb != null ? rb.velocity : Vector2.zero;
            PlayMakerFSM? ctrl = gc.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f != null && f.FsmName == "Control");
            FsmFloat? atk = ctrl?.FsmVariables?.FindFsmFloat("Attack Timer");
            return new
            {
                active = gc.activeInHierarchy,
                pos = new[] { pos.x, pos.y },
                vel = new[] { vel.x, vel.y },
                state = ctrl?.ActiveStateName ?? string.Empty,
                attackTimer = atk != null ? atk.Value : 0f
            };
        }

        private static string GetTransformPath(Transform transform)
        {
            var segments = new List<string>();
            for (Transform? cur = transform; cur != null; cur = cur.parent)
            {
                segments.Add($"{cur.name}#{cur.GetSiblingIndex()}");
            }
            segments.Reverse();
            return transform.gameObject.scene.name + "/" + string.Join("/", segments);
        }

        public void Dispose()
        {
            writer.Flush();
            writer.Dispose();
        }
    }
}
