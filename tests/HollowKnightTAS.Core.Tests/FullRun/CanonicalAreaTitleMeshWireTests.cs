using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass, DoNotParallelize]
public sealed class CanonicalAreaTitleMeshWireTests
{
    [TestMethod]
    public void SourceMeshOwnsEmptyAndPopulatedNativeBuffersBeforeAnyUnitySetter()
    {
        var api = new Api();
        foreach (var empty in new[] { false, true }) {
            var fixture = new Fixture(empty); var bytes = fixture.Bytes; var saved = (byte[])bytes.Clone();
            var snapshot = api.Decode(bytes); api.Validate(snapshot);
            Array.Clear(bytes);
            CollectionAssert.AreEqual(saved, api.Encode(snapshot));
        }
    }

    [TestMethod]
    public void SourceMeshRejectsInvalidStridesDescriptorsAndEveryIndexRangeBeforeNativeMutation()
    {
        var api = new Api(); var fixture = new Fixture(false);
        void Reject(string field, int value) {
            var bytes = (byte[])fixture.Bytes.Clone();
            Array.Copy(BitConverter.GetBytes(value), 0, bytes, fixture.Offsets[field], 4); api.Reject(bytes);
        }
        Reject("attribute", 99); Reject("attribute", 13); // unknown and blend weights
        Reject("format", 99); Reject("format", 2); // UNorm8 x3 violates four-byte native alignment
        Reject("dimension", 0); Reject("dimension", 4); // invalid and stride mismatch
        Reject("stream", 1); Reject("stream", -1);
        Reject("topology", 2); Reject("indexStart", 1); Reject("indexCount", 2);
        Reject("baseVertex", 1); Reject("firstVertex", 1); Reject("rangeVertexCount", 2);
        var escaped = (byte[])fixture.Bytes.Clone();
        Array.Copy(BitConverter.GetBytes((ushort)3), 0, escaped, fixture.Offsets["indices"], 2); api.Reject(escaped);
        api.Reject(fixture.Bytes.Concat(new byte[1]).ToArray());
        api.Reject(fixture.Bytes.Take(fixture.Bytes.Length - 1).ToArray());
        var empty = new Fixture(true); var impossible = (byte[])empty.Bytes.Clone();
        Array.Copy(BitConverter.GetBytes(1), 0, impossible, empty.Offsets["vertexCount"], 4); api.Reject(impossible);
    }

    private sealed class Api
    {
        private readonly MethodInfo decode, encode, validate;
        internal Api()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "HollowKnightTAS.sln"))) repo = repo.Parent;
            Assert.IsNotNull(repo);
            var assembly = Assembly.LoadFrom(Path.Combine(repo.FullName, "src/HollowKnightTAS.Runtime/bin/Release/HollowKnightTAS.dll"));
            var type = assembly.GetType("HollowKnightTAS.Runtime.FullRun.CanonicalPersistentSpriteMeshSnapshot", true)!;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public;
            decode = type.GetMethod("Decode", flags | BindingFlags.Static)!;
            encode = type.GetMethod("Encode", flags | BindingFlags.Instance)!;
            validate = type.GetMethod("RequireVariableTextPayload", flags | BindingFlags.Instance)!;
        }
        internal object Decode(byte[] bytes) => decode.Invoke(null, new object[] { bytes })!;
        internal byte[] Encode(object value) => (byte[])encode.Invoke(value, null)!;
        internal void Validate(object value) => validate.Invoke(value, null);
        internal void Reject(byte[] bytes)
        {
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => Validate(Decode(bytes)));
            Assert.IsTrue(error.InnerException is InvalidDataException or EndOfStreamException,
                "Malformed source should fail in the managed wire validator: " + error.InnerException);
        }
    }

    private sealed class Fixture
    {
        internal readonly Dictionary<string, int> Offsets = new(StringComparer.Ordinal);
        internal readonly byte[] Bytes;
        internal Fixture(bool empty)
        {
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            void Mark(string key) => Offsets[key] = checked((int)stream.Position);
            void Number(string key, int value) { Mark(key); writer.Write(value); }
            void Bounds(BinaryWriter output) { for (var i = 0; i < 6; i++) output.Write(0f); }
            void Blob(byte[] value) { writer.Write(value.Length); writer.Write(value); }
            writer.Write(1); writer.Write("TextMeshPro Mesh"); Number("vertexCount", empty ? 0 : 3);
            writer.Write(empty ? 0 : 1); writer.Write(0); writer.Write(0); writer.Write(empty ? 0 : 1);
            if (!empty) { Number("attribute", 0); Number("format", 0); Number("dimension", 3); Number("stream", 0); }
            Bounds(writer);
            if (!empty) {
                Bounds(writer); Number("topology", 0); Number("indexStart", 0); Number("indexCount", 3);
                Number("baseVertex", 0); Number("firstVertex", 0); Number("rangeVertexCount", 3);
            }
            writer.Write(empty ? 0 : 1); if (!empty) Blob(new byte[36]);
            writer.Write(empty ? 0 : 6); Mark("indices"); if (!empty) writer.Write(new byte[] { 0, 0, 1, 0, 2, 0 });
            using var projection = new MemoryStream(); using var publicValues = new BinaryWriter(projection);
            Bounds(publicValues); publicValues.Write(empty ? 0 : 3);
            if (!empty) for (var i = 0; i < 9; i++) publicValues.Write(0f);
            for (var i = 0; i < 11; i++) publicValues.Write(0); // normals, tangents, colors and eight UV arrays
            if (!empty) {
                Bounds(publicValues); foreach (var value in new[] { 0, 0, 3, 0, 0, 3, 0, 1, 2 }) publicValues.Write(value);
            }
            Blob(projection.ToArray()); Bytes = stream.ToArray();
        }
    }
}
