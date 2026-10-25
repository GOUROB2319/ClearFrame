using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.MediaFoundation;

namespace ClearFrame;

/// <summary>
/// Phase 1 recorder: grabs a screen region (GDI) and encodes it to MP4 (H.264)
/// using Windows Media Foundation. No FFmpeg.
/// </summary>
public class ScreenRecorder
{
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
    private static extern void CopyMemory(IntPtr dest, IntPtr src, UIntPtr count);

    // MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS (lets the GPU encoder be used)
    private static readonly Guid EnableHardwareTransforms =
        new("A634A91C-822B-41B9-A494-4DE4643612B0");

    private Thread? _thread;
    private volatile bool _running;

    public string? Error { get; private set; }

    public void Start(int x, int y, int width, int height, int fps, string outputPath)
    {
        if (_running) return;

        // H.264 needs even numbers
        width &= ~1;
        height &= ~1;

        Error = null;
        _running = true;
        _thread = new Thread(() => RecordLoop(x, y, width, height, fps, outputPath))
        {
            IsBackground = true,
            Name = "ClearFrame-Recorder"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join();
        _thread = null;
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    private static long ElapsedHns(Stopwatch sw) =>
        (long)(sw.ElapsedTicks * (10_000_000.0 / Stopwatch.Frequency));

    private void RecordLoop(int x, int y, int width, int height, int fps, string outputPath)
    {
        IMFSinkWriter? writer = null;
        bool started = false;

        try
        {
            MediaFactory.MFStartup();
            started = true;

            // ---- Sink writer (MP4 file) ----
            IMFAttributes attrs = MediaFactory.MFCreateAttributes(1);
            attrs.Set(EnableHardwareTransforms, 1u);
            writer = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, attrs);

            uint bitrate = (uint)Math.Min(width * (double)height * fps * 0.25, 60_000_000);

            // Output type: H.264
            using IMFMediaType outType = MediaFactory.MFCreateMediaType();
            outType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            outType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
            outType.Set(MediaTypeAttributeKeys.AvgBitrate, bitrate);
            outType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            outType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
            outType.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
            outType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            int streamIndex = writer.AddStream(outType);

            // Input type: raw RGB32 frames (top-down)
            using IMFMediaType inType = MediaFactory.MFCreateMediaType();
            inType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
            inType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Rgb32);
            inType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
            inType.Set(MediaTypeAttributeKeys.FrameSize, Pack((uint)width, (uint)height));
            inType.Set(MediaTypeAttributeKeys.FrameRate, Pack((uint)fps, 1));
            inType.Set(MediaTypeAttributeKeys.PixelAspectRatio, Pack(1, 1));
            inType.Set(MediaTypeAttributeKeys.DefaultStride, (uint)(width * 4));
            inType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
            writer.SetInputMediaType(streamIndex, inType, null);

            writer.BeginWriting();

            // ---- Capture loop ----
            int frameBytes = width * height * 4;
            long frameDuration = 10_000_000L / fps;
            long frameIndex = 0;
            var sw = Stopwatch.StartNew();

            using var bmp = new System.Drawing.Bitmap(
                width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            var size = new System.Drawing.Size(width, height);

            while (_running)
            {
                // Wait until it's time for the next frame
                long target = frameIndex * frameDuration;
                long now = ElapsedHns(sw);
                if (now < target)
                    Thread.Sleep((int)((target - now) / 10_000));

                // Grab the screen
                g.CopyFromScreen(x, y, 0, 0, size,
                    System.Drawing.CopyPixelOperation.SourceCopy |
                    System.Drawing.CopyPixelOperation.CaptureBlt);

                long timestamp = ElapsedHns(sw);

                var data = bmp.LockBits(
                    new System.Drawing.Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppRgb);

                try
                {
                    using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(frameBytes);
                    buffer.Lock(out IntPtr dest, out _, out _);
                    CopyMemory(dest, data.Scan0, (UIntPtr)frameBytes);
                    buffer.Unlock();
                    buffer.CurrentLength = frameBytes;

                    using IMFSample sample = MediaFactory.MFCreateSample();
                    sample.AddBuffer(buffer);
                    sample.SampleTime = timestamp;
                    sample.SampleDuration = frameDuration;
                    writer.WriteSample(streamIndex, sample);
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                // If we fell behind, skip ahead instead of rushing
                frameIndex = Math.Max(frameIndex + 1, ElapsedHns(sw) / frameDuration);
            }

            writer.Finalize();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _running = false;
        }
        finally
        {
            writer?.Dispose();
            if (started) MediaFactory.MFShutdown();
        }
    }
}
