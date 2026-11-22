using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HollowKnightTAS.Core.Inspector;
using HollowKnightTAS.Runtime.Localization;
using UnityEngine;

namespace HollowKnightTAS.Runtime.Inspector
{
    public sealed class OverlayRenderer
    {
        private static readonly string[] Groups =
        {
            "tick",
            "scene",
            "hero",
            "fsm",
            "enemy",
            "collider",
            "rng",
            "failures"
        };

        private int groupIndex;
        private long cachedSequence = -1;
        private int cachedGroupIndex = -1;
        private bool cachedIsChinese;
        private string cachedBody = string.Empty;

        public OverlayRenderer(bool visible)
        {
            Visible = visible;
        }

        public bool Visible { get; private set; }
        public string CurrentGroup => Groups[groupIndex];
        public long RenderCount { get; private set; }
        public long LastRenderedFrameSequence { get; private set; } = -1;

        public void SetVisible(bool value)
        {
            Visible = value;
        }

        public void CycleGroup()
        {
            groupIndex = (groupIndex + 1) % Groups.Length;
            cachedGroupIndex = -1;
        }

        public void Draw(WatchFrame? frame)
        {
            if (!Visible
                || frame == null
                || Event.current.type != EventType.Repaint)
            {
                return;
            }

            var isChinese = RuntimeUiText.IsChinese();
            if (cachedSequence != frame.Sequence
                || cachedGroupIndex != groupIndex
                || cachedIsChinese != isChinese)
            {
                cachedBody = BuildBody(frame, CurrentGroup, isChinese);
                cachedSequence = frame.Sequence;
                cachedGroupIndex = groupIndex;
                cachedIsChinese = isChinese;
            }

            var width = Math.Min(520f, Screen.width - 24f);
            var lineCount = Math.Max(
                3,
                cachedBody.Count(character => character == '\n') + 1);
            var height = Math.Min(
                Screen.height - 24f,
                54f + lineCount * 18f);
            var area = new Rect(
                Screen.width - width - 12f,
                12f,
                width,
                height);
            GUI.Box(
                area,
                isChinese
                    ? "HollowKnightTAS 监视器 [F6 显示/隐藏, F7 切换分组]"
                    : "HollowKnightTAS Inspector [F6 show/hide, F7 group]");
            GUI.Label(
                new Rect(
                    area.x + 10f,
                    area.y + 24f,
                    area.width - 20f,
                    area.height - 30f),
                cachedBody);
            RenderCount++;
            LastRenderedFrameSequence = frame.Sequence;
        }

        private static string BuildBody(
            WatchFrame frame,
            string group,
            bool isChinese)
        {
            var builder = new StringBuilder(2048);
            builder.Append("group=");
            builder.Append(group);
            builder.Append(" | frame=");
            builder.Append(frame.Sequence);
            builder.Append(" | movie=");
            builder.Append(frame.MovieTick);
            builder.Append('\n');

            if (string.Equals(
                    group,
                    "failures",
                    StringComparison.Ordinal))
            {
                if (frame.Failures.Count == 0)
                {
                    builder.Append(isChinese ? "无提供者故障。" : "No provider failures.");
                }
                else
                {
                    foreach (var failure in frame.Failures.Take(12))
                    {
                        builder.Append(failure.ProviderId);
                        builder.Append(": ");
                        builder.Append(failure.Error);
                        builder.Append('\n');
                    }
                }

                return builder.ToString();
            }

            var entries = frame.Entries.Values
                .Where(
                    entry => string.Equals(
                        entry.Descriptor.Group,
                        group,
                        StringComparison.Ordinal))
                .Take(18)
                .ToArray();
            if (entries.Length == 0)
            {
                builder.Append(isChinese ? "该分组下暂无已注册监视项。" : "No registered watches in this group.");
                return builder.ToString();
            }

            foreach (var entry in entries)
            {
                builder.Append(
                    entry.Descriptor.Key.IsVerificationStable
                        ? "[V] "
                        : "[D] ");
                builder.Append(entry.Descriptor.Label);
                builder.Append(" = ");
                builder.Append(entry.Value.DisplayValue);
                if (!entry.IsFresh)
                {
                    builder.Append(" (stale +");
                    builder.Append(entry.AgeMovieTicks);
                    builder.Append(')');
                }

                builder.Append('\n');
            }

            return builder.ToString();
        }
    }
}
