using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace HollowKnightTAS.Companion.Services
{
    public sealed class FsmViewerController : IDisposable
    {
        private readonly Func<RuntimeSessionClient?> session;
        private readonly Func<bool> suspended;
        private readonly Func<RuntimeSessionClient, IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> request;
        private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
        private readonly CancellationTokenSource cancel = new();
        private readonly RuntimePollRequestGate pollGate = new();
        private RuntimeSessionClient? currentSession;
        private bool busy, disposed, refresh = true, wasSuspended;
        private long generation;
        private DateTime nextCatalog;
        private string scene = "";
        public ObservableCollection<FsmSelection> Targets { get; } = new();
        public ObservableCollection<FsmObject> Objects { get; } = new();
        public Dictionary<string, FsmCard> Cards { get; } = new();
        public string Status { get; private set; } = "";
        public event Action? Updated;
        public FsmViewerController(Func<RuntimeSessionClient?> session, Func<bool> suspended,
            Func<RuntimeSessionClient, IReadOnlyDictionary<string, string>, CancellationToken, Task<IReadOnlyDictionary<string, string>>> request)
        { this.session = session; this.suspended = suspended; this.request = request; timer.Tick += Tick; timer.Start(); }
        public void Refresh() { refresh = true; generation++; }
        private void Tick(object? sender, EventArgs e) => _ = PollAsync();
        private async Task<JsonDocument> Read(RuntimeSessionClient client, Dictionary<string, string> fields)
        {
            fields["requestId"] = "studio-fsm-" + Guid.NewGuid().ToString("N");
            var response = await pollGate.RequestAsync(client, fields, request, cancel.Token);
            return JsonDocument.Parse(response["snapshotJson"]);
        }
        private bool Valid(RuntimeSessionClient client, long epoch) => !disposed && epoch == generation && ReferenceEquals(client, session()) && client.IsConnected && !suspended();
        public async Task PollAsync()
        {
            if (disposed) return;
            bool isSuspended = suspended();
            if (isSuspended != wasSuspended) { wasSuspended = isSuspended; generation++; refresh = true; }
            var client = session();
            if (client == null || !client.IsConnected || isSuspended)
            { Invalidate(UiText.T("等待游戏连接或回档完成…")); refresh = true; return; }
            if (!ReferenceEquals(client, currentSession)) { currentSession = client; generation++; refresh = true; Invalidate(UiText.T("正在重新绑定状态机…")); }
            if (busy) return;
            if (pollGate.IsPending(client)) return;
            long epoch = generation;
            busy = true;
            try
            {
                if (refresh || DateTime.UtcNow >= nextCatalog)
                {
                    var objects = new List<FsmObject>(); var targets = new List<FsmSelection>();
                    string snapshot = ""; int offset = 0;
                    do
                    {
                        using var page = await Read(client, new() { ["view"] = "fsmCatalog", ["snapshotId"] = snapshot,
                            ["offset"] = offset.ToString(System.Globalization.CultureInfo.InvariantCulture), ["limit"] = "128" });
                        if (!Valid(client, epoch)) return;
                        var root = page.RootElement;
                        snapshot = FsmGraph.Text(root, "snapshotId");
                        foreach (var obj in root.GetProperty("objects").EnumerateArray())
                        {
                            if (!obj.TryGetProperty("fsms", out var fsms) || fsms.ValueKind != JsonValueKind.Array)
                                throw new InvalidOperationException("FSM catalog entry omitted; object is too large.");
                            var id = FsmGraph.Text(obj, "id"); var path = FsmGraph.Text(obj, "path"); var sceneName = FsmGraph.Text(obj.GetProperty("scene"), "name");
                            objects.Add(new(id, path, sceneName, FsmGraph.Text(obj, "kind") == "enemy", FsmGraph.Flag(obj, "activeInHierarchy")));
                            var ancestors = obj.GetProperty("ancestors").EnumerateArray().Select(a => a.GetString()!).ToArray();
                            foreach (var fsm in fsms.EnumerateArray()) targets.Add(new() { Id = FsmGraph.Text(fsm, "id"), ObjectId = id,
                                Path = path, Scene = sceneName, Name = FsmGraph.Text(fsm, "name"), Ancestors = ancestors });
                        }
                        offset = root.GetProperty("nextOffset").GetInt32();
                    } while (offset >= 0);
                    ApplyCatalog(objects, targets);
                    refresh = false; nextCatalog = DateTime.UtcNow.AddSeconds(3);
                }
                var selected = Targets.Where(t => t.Selected).Take(32).ToArray();
                foreach (var id in Cards.Keys.Where(k => selected.All(t => t.Id != k)).ToArray()) Cards.Remove(id);
                foreach (var target in selected) if (!Cards.ContainsKey(target.Id)) Cards.Add(target.Id, new() { Target = target });
                using var capture = await Read(client, new() { ["view"] = "fsms", ["watches"] = JsonSerializer.Serialize(selected.Select(t =>
                    t.Id + "|" + Cards[t.Id].Version).ToArray()) });
                if (!Valid(client, epoch)) return;
                var data = capture.RootElement;
                var currentScene = data.GetProperty("activeScene").GetProperty("handle").ToString();
                if (scene != "" && scene != currentScene) { refresh = true; Invalidate(UiText.T("场景已变化，正在重新绑定…")); scene = currentScene; return; }
                scene = currentScene;
                foreach (var state in data.GetProperty("fsms").EnumerateArray())
                {
                    if (!Cards.TryGetValue(FsmGraph.Text(state, "id"), out var card)) continue;
                    card.Live = FsmGraph.Flag(state, "available") && FsmGraph.Flag(state, "enabled") && FsmGraph.Flag(state, "activeInHierarchy") && FsmGraph.Flag(state, "initialized");
                    card.Current = FsmGraph.Text(state, "activeState");
                    card.Status = !FsmGraph.Flag(state, "available") ? FsmGraph.Text(state, "reason")
                        : !FsmGraph.Flag(state, "initialized") ? UiText.T("未初始化") : !FsmGraph.Flag(state, "enabled") ? UiText.T("已禁用")
                        : !FsmGraph.Flag(state, "activeInHierarchy") ? UiText.T("对象未激活") : UiText.T("当前：") + card.Current;
                    if (state.TryGetProperty("graph", out var graph)) { card.Graph = FsmGraph.Parse(graph); card.Version = FsmGraph.Text(state, "version"); }
                }
                Status = $"Movie {data.GetProperty("movieFrame")} · Native {data.GetProperty("nativeFrame")} · " + UiText.T("当前状态采样，不包含完整转移历史");
                Updated?.Invoke();
            }
            catch (OperationCanceledException) when (disposed) { }
            catch (Exception error) { if (Valid(client, epoch)) Invalidate(UiText.T("状态机读取失败：") + error.Message); }
            finally { busy = false; if (disposed) cancel.Dispose(); }
        }
        public void ApplyCatalog(IEnumerable<FsmObject> objects, IEnumerable<FsmSelection> targets)
        {
            var incoming = targets.ToArray();
            var old = Targets.ToDictionary(t => t.Id);
            // Only uniquely matching paths/names may be rebound across a cold reconstruction.
            var unique = incoming.GroupBy(t => t.StableKey).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
            var oldUnique = Targets.GroupBy(t => t.StableKey).Where(g => g.Count() == 1 && g.First().Selected).Select(g => g.Key).ToHashSet();
            var merged = incoming.Select(t => old.TryGetValue(t.Id, out var existing) ? existing : t).ToArray();
            foreach (var target in merged.Where(t => !old.ContainsKey(t.Id))) target.Selected = unique.Contains(target.StableKey) && oldUnique.Contains(target.StableKey);
            if (!Targets.Select(t => t.Id).SequenceEqual(merged.Select(t => t.Id)))
            {
                foreach (var target in Targets) target.PropertyChanged -= SelectionChanged;
                Targets.Clear();
                foreach (var target in merged) { target.PropertyChanged += SelectionChanged; Targets.Add(target); }
            }
            var list = objects.OrderByDescending(o => o.Enemy && o.Active).ThenByDescending(o => o.Active).ThenByDescending(o => o.Enemy).ThenBy(o => o.Path).ToArray();
            if (!Objects.SequenceEqual(list)) { Objects.Clear(); foreach (var obj in list) Objects.Add(obj); }
        }
        public void SelectObject(FsmObject obj)
        {
            foreach (var target in Targets.Where(t => t.ObjectId == obj.Id || t.Ancestors.Contains(obj.Id)))
                if (Targets.Count(t => t.Selected) < 32) target.Selected = true;
        }
        private void SelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => generation++;
        private void Invalidate(string message)
        { foreach (var card in Cards.Values) { card.Live = false; card.Status = UiText.T("数据已过期"); } Status = message; Updated?.Invoke(); }
        public void Dispose()
        { if (disposed) return; disposed = true; generation++; timer.Stop(); timer.Tick -= Tick; foreach (var target in Targets) target.PropertyChanged -= SelectionChanged; pollGate.Dispose(); cancel.Cancel(); if (!busy) cancel.Dispose(); }
    }
}
