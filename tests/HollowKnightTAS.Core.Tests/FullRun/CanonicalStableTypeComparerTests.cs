using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Core.Tests.FullRun;

[TestClass]
public sealed class CanonicalStableTypeComparerTests
{
    [TestMethod]
    public void Type_keys_keep_original_reference_equality_and_sdk_lookup_semantics()
    {
        var comparer = CanonicalStableTypeComparer.Instance; var owner = new object();
        var table = new Dictionary<Type, object>(1, comparer) { [typeof(CanonicalStableTypeComparerTests)] = owner };
        Assert.AreSame(owner, table[typeof(CanonicalStableTypeComparerTests)]);
        Assert.IsFalse(table.ContainsKey(typeof(object))); Assert.IsFalse(comparer.Equals(typeof(int), typeof(uint)));
        Assert.IsTrue(comparer.Equals(typeof(int), typeof(int)));
    }
    [TestMethod]
    public void Hash_is_the_registered_module_identity_algorithm_without_RuntimeType_GetHashCode()
    {
        var type = typeof(CanonicalStableTypeComparerTests);
        // Fixed original-game metadata vector; no process address or test-build
        // MVID participates in this expected value.
        Assert.AreEqual(-745147644, CanonicalStableTypeComparer.HashIdentity(
            Guid.Parse("2b54cdaf-7a93-4f0e-b297-d50c4090ea9f"), "InControl.NativeInputDeviceManager"));
        Assert.AreEqual(CanonicalStableTypeComparer.Instance.GetHashCode(type), new CanonicalStableTypeComparer().GetHashCode(type));
    }
    [TestMethod]
    public void Null_and_dynamic_type_keys_remain_outside_the_original_sdk_contract()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CanonicalStableTypeComparer.Instance.GetHashCode(null!));
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CanonicalDynamicKey"), AssemblyBuilderAccess.Run);
        var type = assembly.DefineDynamicModule("module").DefineType("Key").CreateType()!;
        Assert.ThrowsExactly<InvalidOperationException>(() => CanonicalStableTypeComparer.Instance.GetHashCode(type));
    }
}
