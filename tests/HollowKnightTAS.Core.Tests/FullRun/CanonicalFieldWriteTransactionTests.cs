using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun
{
    [TestClass]
    [DoNotParallelize]
    public sealed class CanonicalFieldWriteTransactionTests
    {
        private static object? staticAlias;

        [TestMethod]
        public void PartialCanonicalWriteFailureRestoresArraysAliasesAndStaticNullOwner()
        {
            var backing = new[] { 4, 8, 15 };
            var owner = new Holder { First = backing, Second = backing, Count = 17 };
            staticAlias = backing;
            Assert.ThrowsExactly<InvalidDataException>(() =>
            {
                using (CanonicalFieldWriteTransaction.Begin(new[] {
                    Target(owner, nameof(Holder.First)), Target(owner, nameof(Holder.Second)), Target(owner, nameof(Holder.Count)),
                    new CanonicalFieldWriteTransaction.Target(null, StaticAliasField()) }))
                {
                    owner.First = new[] { 16 }; owner.Second = new[] { 23 }; owner.Count = 42;
                    staticAlias = new object();
                    throw new InvalidDataException("Injected failure after partial metadata writes.");
                }
            });
            Assert.AreSame(backing, owner.First); Assert.AreSame(backing, owner.Second);
            Assert.AreSame(backing, staticAlias); Assert.AreSame(owner.First, owner.Second);
            Assert.AreEqual(17, owner.Count); CollectionAssert.AreEqual(new[] { 4, 8, 15 }, backing);
        }

        [TestMethod]
        public void CommitKeepsNewValuesAndDisposeIsIdempotent()
        {
            var oldBacking = new[] { 1 }; var newBacking = new[] { 2 };
            var owner = new Holder { First = oldBacking, Count = 3 };
            var transaction = CanonicalFieldWriteTransaction.Begin(new[] { Target(owner, nameof(Holder.First)), Target(owner, nameof(Holder.Count)) });
            owner.First = newBacking; owner.Count = 5;
            transaction.Commit(); transaction.Dispose(); transaction.Dispose();
            Assert.AreSame(newBacking, owner.First); Assert.AreEqual(5, owner.Count);
            Assert.ThrowsExactly<ObjectDisposedException>(() => transaction.Commit());
        }

        [TestMethod]
        public void PreparationReadsRawFieldsWithoutWritesOrProperties()
        {
            var backing = new[] { 6 }; var owner = new Holder { First = backing, Count = 9 };
            var field = new InstrumentedFieldInfo(Field(nameof(Holder.First)));
            var transaction = CanonicalFieldWriteTransaction.Begin(new[] { new CanonicalFieldWriteTransaction.Target(owner, field) });
            Assert.AreEqual(1, field.Reads); Assert.AreEqual(0, field.Writes);
            Assert.AreSame(backing, owner.First); Assert.AreEqual(9, owner.Count);
            transaction.Commit(); transaction.Dispose(); Assert.AreEqual(0, field.Writes);
        }

        [TestMethod]
        public void FieldBudgetRejectsPreparationBeforeAnyWrite()
        {
            var backing = new[] { 7 }; var owner = new Holder { First = backing };
            var field = new InstrumentedFieldInfo(Field(nameof(Holder.First)));
            var target = new CanonicalFieldWriteTransaction.Target(owner, field);
            Assert.ThrowsExactly<InvalidDataException>(() => CanonicalFieldWriteTransaction.Begin(
                Enumerable.Repeat(target, CanonicalFieldWriteTransaction.MaximumFields + 1)));
            Assert.AreEqual(CanonicalFieldWriteTransaction.MaximumFields, field.Reads);
            Assert.AreEqual(0, field.Writes); Assert.AreSame(backing, owner.First);
        }

        [TestMethod]
        public void InvalidOwnerAndLiteralAreRejectedDuringPreparation()
        {
            var owner = new Holder();
            Assert.ThrowsExactly<ArgumentException>(() => CanonicalFieldWriteTransaction.Begin(new[] {
                new CanonicalFieldWriteTransaction.Target(null, Field(nameof(Holder.Count))) }));
            Assert.ThrowsExactly<ArgumentException>(() => CanonicalFieldWriteTransaction.Begin(new[] {
                new CanonicalFieldWriteTransaction.Target(new object(), Field(nameof(Holder.Count))) }));
            Assert.ThrowsExactly<ArgumentException>(() => CanonicalFieldWriteTransaction.Begin(new[] {
                new CanonicalFieldWriteTransaction.Target(owner, StaticAliasField()) }));
            Assert.ThrowsExactly<ArgumentException>(() => CanonicalFieldWriteTransaction.Begin(new[] {
                new CanonicalFieldWriteTransaction.Target(null, typeof(Holder).GetField(nameof(Holder.Constant))!) }));
        }

        [TestMethod]
        public void FailedRollbackStillRestoresRemainingFieldsAndReportsWriteAndReadbackFailures()
        {
            var backing = new[] { 8 }; var owner = new Holder { First = backing, Count = 13, Other = 21 };
            var failing = new InstrumentedFieldInfo(Field(nameof(Holder.Count)));
            var transaction = CanonicalFieldWriteTransaction.Begin(new[] {
                Target(owner, nameof(Holder.First)), Target(owner, nameof(Holder.Other)),
                new CanonicalFieldWriteTransaction.Target(owner, failing) });
            owner.First = new[] { 34 }; owner.Count = 55; owner.Other = 89;
            failing.FailWrite = true;
            var failure = Assert.ThrowsExactly<AggregateException>(() => transaction.Dispose());
            Assert.AreEqual(2, failure.InnerExceptions.Count);
            StringAssert.Contains(failure.InnerExceptions[0].Message, "rollback write failed");
            StringAssert.Contains(failure.InnerExceptions[1].Message, "rollback verification failed");
            Assert.AreSame(backing, owner.First); Assert.AreEqual(21, owner.Other);
            Assert.AreEqual(55, owner.Count); Assert.AreEqual(1, failing.Writes);
            transaction.Dispose(); Assert.AreEqual(1, failing.Writes);
        }

        [TestMethod]
        public void RollbackReadFailureDoesNotSkipRemainingFields()
        {
            var owner = new Holder { Count = 3, Other = 7 };
            var failing = new InstrumentedFieldInfo(Field(nameof(Holder.Count)));
            var transaction = CanonicalFieldWriteTransaction.Begin(new[] {
                Target(owner, nameof(Holder.Other)), new CanonicalFieldWriteTransaction.Target(owner, failing) });
            owner.Count = 11; owner.Other = 13; failing.FailRead = true;
            var failure = Assert.ThrowsExactly<AggregateException>(() => transaction.Dispose());
            Assert.AreEqual(1, failure.InnerExceptions.Count);
            StringAssert.Contains(failure.InnerExceptions[0].Message, "rollback verification failed");
            Assert.AreEqual(3, owner.Count); Assert.AreEqual(7, owner.Other);
        }

        [TestMethod]
        public void RawStructReadbackRetainsNestedAliasesWithoutCallingCustomEquals()
        {
            var shared = new object(); var owner = new Holder { Value = new RawValue {
                Reference = shared, Number = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)) } };
            using (CanonicalFieldWriteTransaction.Begin(new[] { Target(owner, nameof(Holder.Value)) }))
                owner.Value = new RawValue { Reference = new object(), Number = 1 };
            Assert.AreSame(shared, owner.Value.Reference);
            Assert.AreEqual(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(owner.Value.Number));
        }

        private static FieldInfo Field(string name) => typeof(Holder).GetField(name)!;
        private static FieldInfo StaticAliasField() => typeof(CanonicalFieldWriteTransactionTests).GetField(
            nameof(staticAlias), BindingFlags.Static | BindingFlags.NonPublic)!;
        private static CanonicalFieldWriteTransaction.Target Target(Holder owner, string name) =>
            new CanonicalFieldWriteTransaction.Target(owner, Field(name));

        private sealed class Holder
        {
            public const int Constant = 1;
            public int[] First = Array.Empty<int>();
            public int[] Second = Array.Empty<int>();
            public int Count;
            public int Other;
            public RawValue Value;
            public int UnusedProperty => throw new InvalidOperationException("Properties must not be read.");
        }

        private struct RawValue
        {
            public object? Reference;
            public float Number;
            public override bool Equals(object? obj) => throw new InvalidOperationException("Custom Equals must not be called.");
            public override int GetHashCode() => throw new InvalidOperationException("Custom hashing must not be called.");
        }

        private sealed class InstrumentedFieldInfo : FieldInfo
        {
            private readonly FieldInfo inner;
            internal InstrumentedFieldInfo(FieldInfo inner) => this.inner = inner;
            internal int Reads;
            internal int Writes;
            internal bool FailRead;
            internal bool FailWrite;
            public override FieldAttributes Attributes => inner.Attributes;
            public override RuntimeFieldHandle FieldHandle => inner.FieldHandle;
            public override Type FieldType => inner.FieldType;
            public override Type? DeclaringType => inner.DeclaringType;
            public override string Name => inner.Name;
            public override Type? ReflectedType => inner.ReflectedType;
            public override object[] GetCustomAttributes(bool inherit) => inner.GetCustomAttributes(inherit);
            public override object[] GetCustomAttributes(Type attributeType, bool inherit) => inner.GetCustomAttributes(attributeType, inherit);
            public override bool IsDefined(Type attributeType, bool inherit) => inner.IsDefined(attributeType, inherit);
            public override object? GetValue(object? obj)
            {
                Reads++;
                if (FailRead) throw new InvalidOperationException("Injected rollback read failure.");
                return inner.GetValue(obj);
            }
            public override void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, CultureInfo? culture)
            {
                Writes++;
                if (FailWrite) throw new InvalidOperationException("Injected rollback write failure.");
                inner.SetValue(obj, value, invokeAttr, binder, culture);
            }
        }
    }
}
