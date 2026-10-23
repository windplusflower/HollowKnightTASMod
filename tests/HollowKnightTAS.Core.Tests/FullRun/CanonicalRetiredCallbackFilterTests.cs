using System;
using System.Linq;
using HollowKnightTAS.Runtime.FullRun;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace HollowKnightTAS.Core.Tests.FullRun;
[TestClass]
public sealed class CanonicalRetiredCallbackFilterTests
{
    private sealed class Target { public void Callback() { throw new Exception("Must never invoke"); } }
    [TestMethod]
    public void FiltersOnlyExactEligibleInvocationsAndPreservesOtherOrderDuplicatesAndIdentity()
    {
        var retired = new Target(); var live = new Target(); var foreign = new Target();
        Action a=retired.Callback,b=live.Callback,c=foreign.Callback;
        var original=Delegate.Combine(a,b,a,c,b);
        var filtered=CanonicalRetiredCallbackFilter.Filter(original,call=>ReferenceEquals(call.Target,retired),out var removed);
        Assert.AreEqual(2,removed);
        CollectionAssert.AreEqual(new object[]{live,foreign,live},filtered!.GetInvocationList().Select(x=>x.Target!).ToArray());
        Assert.AreSame(original,CanonicalRetiredCallbackFilter.Filter(original,_=>false,out removed)); Assert.AreEqual(0,removed);
        Assert.IsNull(CanonicalRetiredCallbackFilter.Filter(Delegate.Combine(a,a),_=>true,out removed));Assert.AreEqual(2,removed);
        Assert.IsNull(CanonicalRetiredCallbackFilter.Filter(null,_=>throw new Exception(),out removed));Assert.AreEqual(0,removed);
    }
}
