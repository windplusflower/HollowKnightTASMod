using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HollowKnightTAS.Runtime.Media;
using HollowKnightTAS.Core.Media;
using HollowKnightTAS.Runtime.Observation;

namespace HollowKnightTAS.Runtime.FullRun
{
    public sealed partial class RuntimeFullRunSession
    {
        private volatile RuntimeVideoCapture? videoCapture;
        private FullRunVideoExportRange? videoRange;
        public bool IsVideoExportActive => videoCapture?.IsActive == true;

        public string StartVideoExport(string ffmpeg, string output, int maximumFrames,
            bool replayLoadedMovie, long endMovieFrame = -1, string infoOverlay = "")
        {
            InfoOverlayVideoSettings? overlaySettings = null;
            if (!string.IsNullOrEmpty(infoOverlay))
            {
                if (infoOverlay.Length > 65536) throw new ArgumentException("Video overlay settings are too large.");
                overlaySettings = Newtonsoft.Json.JsonConvert.DeserializeObject<InfoOverlayVideoSettings>(infoOverlay)
                    ?? throw new ArgumentException("Video overlay settings are missing.");
                overlaySettings.Validate();
            }
            // The worker must never touch Unity audio or rendering. The existing native
            // boundary queue services this on the Unity thread even while paused.
            return observationQueue.Invoke(frame =>
            {
                if (!clock.IsPaused || !inputReady || mode != "Replay" || replayMovie == null)
                    throw new InvalidOperationException("Pause an input-ready v2 replay before exporting video.");
                if (!replayLoadedMovie)
                    throw new InvalidOperationException("v2 export requires replayLoadedMovie=true.");
                if (IsVideoExportActive) throw new InvalidOperationException("A video export is already active.");
                // Each completed frame carries its actual clock duration, including loading frames.
                var range = new FullRunVideoExportRange(frame, movieFrame, replayLength,
                    endMovieFrame, maximumFrames);
                videoCapture?.Dispose();
                VideoInfoOverlay? overlay = null;
                try
                {
                    Action<byte[], int, int>? composite = null;
                    if (overlaySettings != null && overlaySettings.Rows.Count > 0)
                    {
                        overlay = new VideoInfoOverlay(overlaySettings);
                        var watches = overlaySettings.Watches();
                        string lastErrors = "";
                        composite = (rgb, width, height) =>
                        {
                            var values = RuntimeInfoObservation.CaptureValues(clock.CurrentFrameIndex, movieFrame, watches,
                                infoTiming.RealSeconds, infoTiming.GameSeconds, infoTiming.Error, out var errors);
                            overlay.Composite(rgb, width, height, values);
                            var detail = string.Join("; ", errors.Take(3).Select(e => e.Key + ": " + e.Value));
                            if (detail != lastErrors && detail.Length != 0) Modding.Logger.LogWarn("[HKTAS] Video overlay: " + detail);
                            lastErrors = detail;
                        };
                    }
                    videoCapture = new RuntimeVideoCapture(ffmpeg, output, maximumFrames,
                        message => Modding.Logger.Log("TAS v2 " + message),
                        finishAtFrameLimit: false, onFailure: clock.RequestPause,
                        frameDuration: () => clock.StepSeconds, nativeStartFrame: frame,
                        composite: composite, releaseComposite: () => overlay?.Dispose());
                }
                catch { overlay?.Dispose(); throw; }
                videoRange = range;
                // A previously scheduled seek must not interrupt the selected capture range.
                pauseAtMovieFrame = -1;
                return videoCapture.OperationId;
            });
        }

        public string CancelVideoExport(string operationId)
        {
            return observationQueue.Invoke(_ =>
            {
                var capture = videoCapture;
                if (capture == null || capture.OperationId != operationId)
                    throw new InvalidOperationException("Video export operationId does not match.");
                capture.Cancel();
                if (!clock.IsFinished) clock.RequestPause();
                return capture.OperationId;
            });
        }

        public void AppendVideoExportStatus(IDictionary<string, string> fields)
        {
            var capture = videoCapture;
            if (capture == null) return;
            capture.AppendStatus(fields);
            var range = videoRange;
            if (range != null)
            {
                fields["videoExport.startNativeFrame"] = range.StartNativeFrame.ToString(CultureInfo.InvariantCulture);
                fields["videoExport.startMovieFrame"] = range.StartMovieFrame.ToString(CultureInfo.InvariantCulture);
                fields["videoExport.endMovieFrame"] = range.EndMovieFrame.ToString(CultureInfo.InvariantCulture);
            }
            fields["videoExport.boundary"] = "native-post-player-loop";
        }

        private void FailVideoExport(string detail)
        {
            var capture = videoCapture;
            if (capture?.IsActive == true) capture.Fail("Full-run replay failed: " + detail);
        }

        private void CaptureVideoFrame(long completed)
        {
            var capture = videoCapture;
            if (capture?.State != "Capturing") return;
            try
            {
                if (mode == "Failed") { FailVideoExport(error); return; }
                var finished = (videoRange ?? throw new InvalidOperationException("Video range is unavailable."))
                    .CompleteNativeFrame(completed, movieFrame);
                capture.CaptureCompletedFrame(completed);
                // Capture the final rendered input once, then pause without a release frame.
                // The loaded Movie remains intact when the selected end is before its tail.
                if (capture.State == "Capturing" && finished)
                {
                    capture.Finish();
                    if (!clock.IsFinished) clock.RequestPause();
                }
            }
            catch (Exception exception) { capture.Fail(exception.Message); }
        }
    }
}
