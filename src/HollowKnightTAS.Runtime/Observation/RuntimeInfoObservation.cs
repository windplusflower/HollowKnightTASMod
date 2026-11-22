using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using HollowKnightTAS.Core.Inspector;
using GlobalEnums;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HollowKnightTAS.Runtime.Observation
{
    // Called only by FrameObservationQueue at the native frame boundary, including pause.
    // No world traversal, property invocation, input sampling, or gameplay mutation.
    internal static class RuntimeInfoObservation
    {
        private static readonly FieldInfo? Dash = Field("dashCooldownTimer");
        private static readonly FieldInfo? Shade = Field("shadowDashTimer");
        private static readonly FieldInfo? Attack = Field("attack_cooldown");
        private static readonly ObservationValues RawFields = new ObservationValues(_ => throw new NotSupportedException());
        private static FieldInfo? Field(string name) => typeof(HeroController).GetField(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        public static Dictionary<string, string> Capture(long nativeFrame, long movieFrame, string[]? watches = null,
            double? realSeconds = null, double? gameSeconds = null, string timingError = "",
            string? rng = null, long? rngRolls = null, int? rngDelta = null)
        {
            var values = CaptureValues(nativeFrame, movieFrame, watches, realSeconds, gameSeconds, timingError, out var errors,
                rng, rngRolls, rngDelta);
            return new Dictionary<string, string>
            {
                ["snapshotId"] = "info-" + Guid.NewGuid().ToString("N"),
                ["snapshotJson"] = JsonConvert.SerializeObject(new { schemaVersion = 1, nativeFrame, movieFrame, values, errors })
            };
        }

        public static Dictionary<string, object?> CaptureValues(long nativeFrame, long movieFrame, string[]? watches,
            double? realSeconds, double? gameSeconds, string timingError, out Dictionary<string, string> errors,
            string? rng = null, long? rngRolls = null, int? rngDelta = null)
        {
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var values = new Dictionary<string, object?>
            {
                ["frame"] = movieFrame, ["nativeFrame"] = nativeFrame, ["room"] = scene,
                ["rt"] = realSeconds, ["gt"] = gameSeconds
            };
            var tftMs = (Time.timeAsDouble - Time.fixedTimeAsDouble) * 1000.0;
            if (!double.IsNaN(tftMs) && !double.IsInfinity(tftMs)) values["tft"] = tftMs;
            if (rng != null) values["rng"] = rng;
            if (rngRolls.HasValue) values["rngRolls"] = rngRolls.Value;
            if (rngDelta.HasValue) values["rngDelta"] = rngDelta.Value;
            var hero = HeroController.SilentInstance;
            var manager = GameManager.instance;
            var valid = hero != null && hero.gameObject.activeInHierarchy && manager != null
                && (manager.gameState == GameState.PLAYING || manager.gameState == GameState.PAUSED)
                && scene != "Quit_To_Menu" && !scene.StartsWith("Menu_", StringComparison.Ordinal);
            if (valid)
            {
                var position = hero!.transform.position;
                values["x"] = position.x; values["y"] = position.y;
                var body = hero.GetComponent<Rigidbody2D>();
                if (body != null) { values["vx"] = body.velocity.x; values["vy"] = body.velocity.y; }
                values["dash"] = ReadTimer(Dash, hero);
                values["shade"] = ReadTimer(Shade, hero);
                values["attack"] = ReadTimer(Attack, hero);
                values["grounded"] = hero.cState.onGround;
                values["facingRight"] = hero.cState.facingRight;
                values["jumping"] = hero.cState.jumping;
                values["dashing"] = hero.cState.dashing;
                var player = PlayerData.instance;
                if (player != null)
                {
                    values["health"] = player.health; values["maxHealth"] = player.maxHealth;
                    values["soul"] = player.MPCharge; values["reserveSoul"] = player.MPReserve;
                    if (!player.hasDash) values["dash"] = null;
                    if (!player.hasShadowDash) values["shade"] = null;
                }
            }
            errors = new Dictionary<string, string>();
            if (timingError.Length != 0) errors["gt"] = timingError;
            foreach (var expression in watches ?? Array.Empty<string>())
            {
                try
                {
                    var parsed = InfoWatchExpression.Parse(expression);
                    values["watch:" + expression] = parsed.Evaluate(ReadQuery,
                        key => values.TryGetValue(key, out var value) ? value : null);
                }
                catch (Exception error)
                {
                    values["watch:" + expression] = null;
                    errors[expression] = error.Message;
                }
            }
            object? ReadQuery(InfoWatchQuery query)
            {
                object? root;
                switch (query.Root)
                {
                    case "hero": root = valid ? hero : null; break;
                    case "player": root = valid ? PlayerData.instance : null; break;
                    case "game": root = manager; break;
                    case "position": root = valid ? (object)hero!.transform.position : null; break;
                    case "velocity": root = valid ? (object?)hero!.GetComponent<Rigidbody2D>()?.velocity : null; break;
                    default: root = ResolveTarget(query); break;
                }
                return query.Read(root);
            }
            return values;
        }

        private static object? ResolveTarget(InfoWatchQuery query)
        {
            // Explicit active object path only; no global component/FSM enumeration.
            var target = GameObject.Find(query.ObjectPath);
            if (target == null) throw new InvalidOperationException("Active object not found: " + query.ObjectPath);
            if (query.Root == "component")
            {
                var components = target.GetComponents<Component>().Where(c => c != null
                    && (c.GetType().FullName == query.ComponentName || c.GetType().Name == query.ComponentName)).ToArray();
                if (components.Length != 1) throw new InvalidOperationException("Expected exactly one component: " + query.ComponentName);
                return components[0];
            }
            // PlayMakerFSM.Fsm can assign Owner; never initialize/access a FSM through its getters.
            var fsms = target.GetComponents<PlayMakerFSM>().Select(f => RawFields.Read(f, "fsm"))
                .Where(f => (RawFields.Read(f, "name") as string) == query.FsmName).ToArray();
            if (fsms.Length != 1) throw new InvalidOperationException("Expected exactly one FSM: " + query.FsmName);
            var variables = RawFields.Read(fsms[0], "variables");
            if (variables == null) throw new InvalidOperationException("FSM variables are not initialized.");
            var matches = new List<object>();
            foreach (var field in RawFields.Fields(variables.GetType()))
            {
                if (!field.FieldType.IsArray || !typeof(HutongGames.PlayMaker.NamedVariable).IsAssignableFrom(field.FieldType.GetElementType()!)) continue;
                if (!(field.GetValue(variables) is Array array)) continue;
                foreach (var variable in array)
                    if ((RawFields.Read(variable, "name") as string) == query.VariableName) matches.Add(variable);
            }
            if (matches.Count != 1) throw new InvalidOperationException("Expected exactly one FSM variable: " + query.VariableName);
            return RawFields.Read(matches[0], "value");
        }

        private static object? ReadTimer(FieldInfo? field, HeroController hero)
        {
            if (!(field?.GetValue(hero) is float value) || float.IsNaN(value) || float.IsInfinity(value)) return null;
            return Math.Max(0f, value);
        }
    }
}
