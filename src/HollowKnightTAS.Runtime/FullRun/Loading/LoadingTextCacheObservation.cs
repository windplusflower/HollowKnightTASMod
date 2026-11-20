using System;
using System.Linq;
using UnityEngine;

namespace HollowKnightTAS.Runtime.FullRun
{
    internal static class LoadingTextCacheObservation
    {
        // Only called for the six known differing menu Text roots. Do not access
        // cachedTextGenerator or TextGenerator.verts: they populate caches.
        internal static object Capture(UnityEngine.UI.Text text, LoadingFieldDigest fields, Func<UnityEngine.Object, string> identity)
        {
            object? Generator(string field)
            {
                var value = fields.Read(text, field) as TextGenerator;
                if (value == null) return null;
                var settings = (TextGenerationSettings)fields.Read(value, "m_LastSettings")!;
                var pointer = (IntPtr)fields.Read(value, "m_Ptr")!;
                return new { lastString = fields.Read(value, "m_LastString"),
                    generated = fields.Read(value, "m_HasGenerated"), valid = fields.Read(value, "m_LastValid"),
                    settings = new { font = settings.font == null ? null : identity(settings.font), settings.fontSize,
                        settings.scaleFactor, settings.lineSpacing, settings.richText, settings.resizeTextForBestFit,
                        settings.resizeTextMinSize, settings.resizeTextMaxSize, settings.fontStyle, settings.textAnchor,
                        settings.horizontalOverflow, settings.verticalOverflow, settings.updateBounds,
                        settings.generateOutOfBounds, settings.alignByGeometry,
                        extents = new[] { settings.generationExtents.x, settings.generationExtents.y },
                        pivot = new[] { settings.pivot.x, settings.pivot.y },
                        color = new[] { settings.color.r, settings.color.g, settings.color.b, settings.color.a } },
                    nativeVertices = pointer == IntPtr.Zero ? null : Vertices(value.GetVerticesArray()),
                    managedVertices = fields.Read(value, "m_Verts") is System.Collections.Generic.List<UIVertex> managed
                        ? Vertices(managed.ToArray()) : null };
            }
            return new { text.text, cache = Generator("m_TextCache"), layout = Generator("m_TextCacheForLayout"),
                temporary = fields.Read(text, "m_TempVerts") is UIVertex[] temp ? Vertices(temp) : null };
        }

        private static object Vertices(UIVertex[] values)
        {
            if (values.Length > 65536) throw new InvalidOperationException("P0 text vertex diagnostic budget exceeded.");
            var columns = new { positions = values.Select(v => v.position).ToArray(),
                colors = values.Select(v => v.color).ToArray(), normals = values.Select(v => v.normal).ToArray(),
                tangents = values.Select(v => v.tangent).ToArray(), uv0 = values.Select(v => v.uv0).ToArray(),
                uv1 = values.Select(v => v.uv1).ToArray(), uv2 = values.Select(v => v.uv2).ToArray(), uv3 = values.Select(v => v.uv3).ToArray() };
            // Isolated digest instance prevents diagnostic arrays from changing
            // the main graph's alias discovery order.
            using (var digest = new LoadingFieldDigest(_ => null, _ => false))
                return new { count = values.Length,
                    groups = digest.Fields(columns.GetType()).Select(field => new {
                        field = field.Name, digest = digest.CaptureFields(columns, "text-vertices", field.Name).Sha256 }).ToArray(),
                    prefix = values.Take(4).Select(v => new { position = new[] { v.position.x, v.position.y, v.position.z },
                        uv = new[] { v.uv0.x, v.uv0.y } }).ToArray() };
        }
    }
}
