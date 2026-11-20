using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HollowKnightTAS.Automation.Client;
using HollowKnightTAS.Companion.Automation;
using HollowKnightTAS.Companion.Services;
using HollowKnightTAS.Companion.ViewModels;
using HollowKnightTAS.Core.Automation;
using HollowKnightTAS.Core.Movie;

internal static partial class StudioScenarioHarness
{
    static async Task RunAutomationSyncAsync()
    {
        var vm = (MainViewModel)app.MainWindow.DataContext;
        var boot = Field<StartupBootController>(app, "startupBoot");
        var movies = Field<FullRunMovieCoordinator>(app, "fullRunMovies");
        var broker = Field<AutomationBroker>(vm, "automationBroker");
        void Set(string name, object value) => typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
        Set("worldlines", new StudioTimelineStore(Path.Combine(output, "timelines.json")));
        Set("initialSaveCacheRoot", Path.Combine(output, "initial-saves"));
        Set("activeSequenceDirectory", output);
        Set("activeAutoSaveSeconds", 0);
        Require(!app.MainWindow.IsVisible, "test host initially hidden");
        using (var secondary = new SingleInstanceCoordinator())
            Require(await secondary.ForwardLaunchAsync(@"D:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight.exe", CancellationToken.None), "external launch forwards to existing Studio");
        Require(app.MainWindow.IsVisible && boot.IsWaiting && boot.NativeCompletedFrames == 0, "AI launch shows Studio at native frame zero");
        await using var ai = new AutomationClient();
        await ai.ConnectAsync(new AutomationConnectOptions { ClientId = "studio-sync-live", BootstrapPath = broker.BootstrapPath }, CancellationToken.None);
        async Task<AutomationResultEnvelope> Call(string id, string scope, Dictionary<string, string>? arguments = null, string mode = "Paused")
        {
            var lease = "";
            if (scope.StartsWith("control."))
            {
                var acquired = await ai.ExecuteAsync(ai.CreateCommand(AutomationCommandIds.AcquireControl, scope,
                    new Dictionary<string, string> { ["scopes"] = scope }), CancellationToken.None);
                Require(acquired.Success, "lease " + acquired.Detail);
                lease = acquired.Data["leaseId"];
            }
            try
            {
                var result = await ai.ExecuteAsync(ai.CreateCommand(id, scope, arguments, lease, mode), CancellationToken.None);
                Require(result.Success, id + " " + result.ResultCode + " " + result.Detail);
                return result;
            }
            finally
            {
                if (lease.Length != 0)
                {
                    var released = await ai.ExecuteAsync(ai.CreateCommand(AutomationCommandIds.ReleaseControl, scope, leaseId: lease), CancellationToken.None);
                    Require(released.Success, "release lease " + released.Detail);
                }
            }
        }
        Dictionary<string, string> Frame() => new() { ["expectedNativeFrame"] = boot.NativeCompletedFrames.ToString() };
        var begin = Frame(); begin["mouseEnabled"] = "false";
        await Call(AutomationCommandIds.BeginFullRunRecording, AutomationScope.ControlRecording, begin);
        Require(vm.InputRows.Count == 500, "AI recording initializes Studio grid");
        await Call(AutomationCommandIds.FullRunStep, AutomationScope.ControlStep, Frame());
        await Until(() => vm.SelectedSession?.Client.IsConnected == true, "runtime connected", 120);
        // Establish title-screen progress using the ordinary human transport path.
        await vm.FrameMenuAsync("seek", 120);
        Require(Field<long>(vm, "currentFullRunMovieFrame") == 120, "human seek 120: " + vm.GridStatus);
        var native = boot.NativeCompletedFrames;
        vm.PaintGrid(130, 130, "Attack", true);
        var snapshot = await Call(AutomationCommandIds.FullRunSnapshot, AutomationScope.MovieRead);
        var codec = new MovieV2Codec();
        var draft = codec.Parse(new StringReader(File.ReadAllText(snapshot.Data["path"])), "draft").Document!;
        File.WriteAllText(Path.Combine(output, "human-draft.hktas"), vm.MovieText);
        File.WriteAllText(Path.Combine(output, "ai-snapshot.hktas"), codec.WriteCanonical(draft));
        var human = codec.Parse(new StringReader(vm.MovieText), "human").Document!;
        var slice = typeof(MainViewModel).GetMethod("SliceV2", BindingFlags.Static | BindingFlags.NonPublic)!;
        Require(codec.WriteCanonical((MovieV2Document)slice.Invoke(null, new object[] { draft, 120L, 380L })!)
            == codec.WriteCanonical((MovieV2Document)slice.Invoke(null, new object[] { human, 120L, 380L })!), "AI sees human future draft");
        var edited = MovieV2RangeEditor.Paint(draft, 140, 1, "Jump", true);
        var candidate = Path.Combine(movies.ShadowRoot, "ai-candidate.hktas");
        var text = codec.WriteCanonical(edited); File.WriteAllText(candidate, text);
        var update = Frame(); update["moviePath"] = candidate;
        await Call(AutomationCommandIds.FullRunUpdateMovie, AutomationScope.ControlPlayback, update);
        Require(vm.MovieText == text && boot.NativeCompletedFrames == native, "AI update immediately visible without advancing game");
        SaveAutomationStudioImage("ai-edit.png");
        vm.UndoGridCommand.Execute(null);
        Require(vm.MovieText != text, "human can undo AI edit");
        vm.RedoGridCommand.Execute(null);
        Require(vm.MovieText == text, "human can redo AI edit");
        vm.PaintGrid(150, 150, "Dash", true);
        await Call(AutomationCommandIds.FullRunStep, AutomationScope.ControlStep, Frame());
        Require(Field<long>(vm, "currentFullRunMovieFrame") == 121, "AI step immediately updates Studio Movie frame");
        Require(!Field<bool>(vm, "gridHasUserEdits"), "AI step applies pending human input");
        await Call(AutomationCommandIds.FullRunPlay, AutomationScope.ControlPlayback, Frame());
        await Task.Delay(120);
        await Call(AutomationCommandIds.FullRunPause, AutomationScope.ControlPlayback, Frame(), "Running");
        Require(boot.IsWaiting && vm.IsInputGridInteractive, "AI pause returns editable Studio");
        var state = await Call(AutomationCommandIds.FullRunStatus, AutomationScope.ObserveStatus);
        Require(state.Data["mismatchCount"] == "0" && boot.FullRunFaultCode == 0, "no mismatch or native fault");
        Require(Field<long>(vm, "currentFullRunMovieFrame").ToString() == state.Data["movieFrame"], "Studio and Runtime frame agree");
        SaveAutomationStudioImage("ai-paused.png");
        movies.VerifyOriginalSavesUnchanged();
        File.WriteAllText(Path.Combine(output, "final-state.json"), System.Text.Json.JsonSerializer.Serialize(state.Data));
        File.WriteAllText(Path.Combine(output, "final-movie.hktas"), vm.MovieText);
    }

    static void SaveAutomationStudioImage(string name)
    {
        app.MainWindow.UpdateLayout();
        var target = new System.Windows.Media.Imaging.RenderTargetBitmap((int)app.MainWindow.ActualWidth,
            (int)app.MainWindow.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        target.Render(app.MainWindow);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(target));
        using var file = File.Create(Path.Combine(output, name));
        encoder.Save(file);
    }
}
