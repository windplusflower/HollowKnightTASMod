using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Core.Ipc;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HollowKnightTAS.Companion.Tests;

[TestClass]
public sealed class RuntimePollRequestGateTests
{
    private static RuntimeSessionClient Client()
    {
        var client = (RuntimeSessionClient)RuntimeHelpers.GetUninitializedObject(typeof(RuntimeSessionClient));
        Connected(client, true); return client;
    }
    private static void Connected(RuntimeSessionClient client, bool value)
    {
        typeof(RuntimeSessionClient).GetProperty(nameof(RuntimeSessionClient.IsConnected))!.SetValue(client, value);
        (typeof(RuntimeSessionClient).GetField("ConnectionChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)
            as EventHandler)?.Invoke(client, EventArgs.Empty);
    }
    private static EventHandler<IpcEnvelope>? Handler(RuntimeSessionClient client) =>
        typeof(RuntimeSessionClient).GetField("EnvelopeReceived", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client) as EventHandler<IpcEnvelope>;
    private static IpcEnvelope Reply(string id, string type = IpcMessageTypes.WorldSnapshot) =>
        new(1, "fixture", 1, type, IpcPayloadCodec.Serialize(new Dictionary<string,string>{{"requestId", id}}));
    [TestMethod]
    public async Task Original_timeout_is_reported_but_automatic_retries_wait_for_the_real_matching_reply()
    {
        var client=Client(); using var gate=new RuntimePollRequestGate(); var calls=0;
        await Assert.ThrowsExactlyAsync<TimeoutException>(()=>gate.RequestAsync(client,
            new Dictionary<string,string>{{"requestId","late"}}, (_,_,_)=>{calls++;throw new TimeoutException("original two-second timeout");},CancellationToken.None));
        for(var i=0;i<100;i++) Assert.IsTrue(gate.IsPending(client));
        Assert.AreEqual(1,calls);
        Handler(client)!(client, Reply("wrong")); Assert.IsTrue(gate.IsPending(client));
        Handler(client)!(client, Reply("late",IpcMessageTypes.CommandAccepted)); Assert.IsTrue(gate.IsPending(client));
        Handler(client)!(client, Reply("late")); Assert.IsFalse(gate.IsPending(client));
    }
    [TestMethod]
    public void Old_session_or_old_generation_callbacks_cannot_release_the_current_request()
    {
        var old=Client();var current=Client();using var gate=new RuntimePollRequestGate();
        gate.Begin(old,"same-id",IpcMessageTypes.WorldSnapshot);var stale=Handler(old)!;
        Assert.IsFalse(gate.IsPending(current));gate.Begin(current,"same-id",IpcMessageTypes.WorldSnapshot);
        stale(old,Reply("same-id"));Assert.IsTrue(gate.IsPending(current));
        stale(current,Reply("same-id"));Assert.IsTrue(gate.IsPending(current));
        var priorGeneration=Handler(current)!;Connected(current,false);Assert.IsFalse(gate.IsPending(current));
        Connected(current,true);var obsolete=gate.Begin(current,"same-id",IpcMessageTypes.WorldSnapshot);
        gate.Reset();gate.Begin(current,"same-id",IpcMessageTypes.WorldSnapshot);gate.Abandon(current,"same-id",obsolete);
        Assert.IsTrue(gate.IsPending(current));
        priorGeneration(current,Reply("same-id"));Assert.IsTrue(gate.IsPending(current));
        Handler(current)!(current,Reply("same-id",IpcMessageTypes.CommandRejected));Assert.IsFalse(gate.IsPending(current));
    }
    [TestMethod]
    public async Task Cancellation_send_failure_success_and_disposal_release_only_their_own_subscription()
    {
        var client=Client();using var gate=new RuntimePollRequestGate();var fields=new Dictionary<string,string>{{"requestId","test"}};
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(()=>gate.RequestAsync(client,fields,(_,_,_)=>throw new OperationCanceledException(),CancellationToken.None));
        Assert.IsFalse(gate.IsPending(client));Assert.IsNull(Handler(client));
        await Assert.ThrowsExactlyAsync<IOException>(()=>gate.RequestAsync(client,fields,(_,_,_)=>throw new IOException(),CancellationToken.None));
        Assert.IsFalse(gate.IsPending(client));
        await gate.RequestAsync(client,fields,(_,_,_)=>Task.FromResult<IReadOnlyDictionary<string,string>>(fields),CancellationToken.None);
        Assert.IsFalse(gate.IsPending(client));
        gate.Begin(client,"pending",IpcMessageTypes.WorldSnapshot);var stale=Handler(client)!;gate.Dispose();
        Assert.IsNull(Handler(client));stale(client,Reply("pending"));
        Assert.ThrowsExactly<ObjectDisposedException>(()=>gate.Begin(client,"new",IpcMessageTypes.WorldSnapshot));
    }
    [TestMethod]
    public void Grid_status_and_both_snapshot_sources_remain_bounded_and_recover_on_disconnect()
    {
        var client=Client();using var fsm=new RuntimePollRequestGate();using var info=new RuntimePollRequestGate();using var grid=new RuntimePollRequestGate();
        fsm.Begin(client,"fsm",IpcMessageTypes.WorldSnapshot);info.Begin(client,"info",IpcMessageTypes.WorldSnapshot);
        grid.Begin(client,"grid",IpcMessageTypes.FullRunState);
        for(var i=0;i<100;i++){Assert.IsTrue(fsm.IsPending(client));Assert.IsTrue(info.IsPending(client));Assert.IsTrue(grid.IsPending(client));}
        Handler(client)!(client,Reply("grid",IpcMessageTypes.FullRunState));
        Assert.IsFalse(grid.IsPending(client));Assert.IsTrue(fsm.IsPending(client));Assert.IsTrue(info.IsPending(client));
        Connected(client,false);Assert.IsFalse(fsm.IsPending(client));Assert.IsFalse(info.IsPending(client));
        Connected(client,true);grid.Begin(client,"v1",IpcMessageTypes.RuntimeStatus);
        Handler(client)!(client,Reply("v1",IpcMessageTypes.RuntimeStatus));Assert.IsFalse(grid.IsPending(client));
    }
}
