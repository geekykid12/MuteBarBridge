using System;
using System.Runtime.InteropServices;

namespace MuteBarBridge;

internal enum BridgeStatus { Stopped, Starting, Running, Error }

internal sealed class BridgeApp : IDisposable
{
    private readonly DirectShowCapture _capture = new();
    private UnityCaptureSink? _sink;

    private int  _captureWidth;
    private int  _captureHeight;
    private bool _disposed;

    // Output resolution pushed to UnityCapture — must match Discord's camera resolution.
    // Change these if Discord expects a different size.
    // UnityCapture linearly scales this 1080p render to the resolution the
    // consuming application negotiates (for example, Discord's 1280x720).
    private const int OutputWidth  = 1920;
    private const int OutputHeight = 1080;

    // Keep-alive: re-push the last scaled frame at 30fps so the UnityCapture driver
    // never hits its watchdog timeout when MuteBar mutes and Softcam goes quiet.
    private byte[]? _lastScaledFrame;
    private readonly object _lastFrameLock = new();
    private System.Threading.Timer? _keepAliveTimer;
    private const int KeepAliveIntervalMs = 33;  // ~30 fps

    public BridgeStatus Status        { get; private set; } = BridgeStatus.Stopped;
    public string       StatusMessage { get; private set; } = "Ready";

    public BridgeApp()
    {
        _capture.FrameArrived += OnFrameArrived;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void StartBridge(string deviceName = "OAW CAM")
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Status is BridgeStatus.Running or BridgeStatus.Starting) return;

        SetStatus(BridgeStatus.Starting, $"Initializing capture for '{deviceName}'...");

        try
        {
            DiagnosticCheckMemory();

            _capture.Start(deviceName);
            _captureWidth  = _capture.Width;
            _captureHeight = _capture.Height;

            // Sink is always at the fixed output resolution regardless of capture size
            _sink?.Dispose();
            _sink = new UnityCaptureSink(OutputWidth, OutputHeight);
            _sink.Open();

            _keepAliveTimer = new System.Threading.Timer(
                _ => PushKeepAliveFrame(), null, KeepAliveIntervalMs, KeepAliveIntervalMs);

            SetStatus(BridgeStatus.Running,
                $"Running — capture {_captureWidth}x{_captureHeight} → output {OutputWidth}x{OutputHeight}");
        }
        catch (Exception ex)
        {
            SetStatus(BridgeStatus.Error, $"Start failed: {ex.Message}");
            StopBridge();
            throw;
        }
    }

    public void StopBridge()
    {
        if (Status == BridgeStatus.Stopped) return;

        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;

        _capture.Stop();

        _sink?.Dispose();
        _sink = null;

        lock (_lastFrameLock) _lastScaledFrame = null;

        SetStatus(BridgeStatus.Stopped, "Bridge stopped.");
    }

    // ── Frame pipeline ────────────────────────────────────────────────────────

    private void OnFrameArrived(byte[] frameData, int width, int height)
    {
        
        if (_disposed || _sink == null) return;

        // Track capture resolution changes (sink stays at fixed output size)
        if (width != _captureWidth || height != _captureHeight)
        {
            _captureWidth  = width;
            _captureHeight = height;
            lock (_lastFrameLock) _lastScaledFrame = null;
            SetStatus(BridgeStatus.Running,
                $"Capture resized — {_captureWidth}x{_captureHeight} → output {OutputWidth}x{OutputHeight}");
        }

        // Scale to output resolution and cache for keep-alive
        if (_lastScaledFrame == null)
        {
            // Console.WriteLine($"[Scaler] Input {width}x{height} ({frameData.Length} bytes) → Output {OutputWidth}x{OutputHeight}");
        }
        byte[] scaled = ScaleBilinear(frameData, width, height, OutputWidth, OutputHeight);

        lock (_lastFrameLock)
            _lastScaledFrame = scaled;
        /* Console.WriteLine(
    $"Scaled samples: " +
    $"0={scaled[0]}, " +
    $"mid={scaled[(scaled.Length / 2)]}, " +
    $"end={scaled[^4]}");
    Console.WriteLine(
    $"Input samples: " +
    $"0={frameData[0]}, " +
    $"mid={frameData[frameData.Length / 2]}, " +
    $"end={frameData[^4]}"); */
        PushFrame(scaled);
    }

    private void PushKeepAliveFrame()
    {
        if (_disposed || _sink == null) return;
        byte[]? frame;
        lock (_lastFrameLock) frame = _lastScaledFrame;
        if (frame != null) PushFrame(frame);
    }

    private void PushFrame(byte[] frameData)
    {
        try   { _sink?.WriteFrame(frameData); }
        catch (Exception ex) { SetStatus(BridgeStatus.Error, $"Write error: {ex.Message}"); }
    }

    // ── Bilinear downscale ────────────────────────────────────────────────────
    // Simple but fast enough for 1080p → 720p at 30fps on a background thread.

    private static unsafe byte[] ScaleBilinear(
        byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        // Fast path: already the right size
        if (srcW == dstW && srcH == dstH) return src;

        byte[] dst = new byte[dstW * dstH * 4];

        // Map each destination pixel back to a source coordinate.
        // Source is already top-down BGRA (flipped by DirectShowCapture).
        // Pin edges: dy=0 → sy=0, dy=dstH-1 → sy=srcH-1.
        float xScale = (dstW > 1) ? (float)(srcW - 1) / (dstW - 1) : 0f;
        float yScale = (dstH > 1) ? (float)(srcH - 1) / (dstH - 1) : 0f;

        fixed (byte* pSrc = src)
        fixed (byte* pDst = dst)
        {
            for (int dy = 0; dy < dstH; dy++)
            {
                float fy  = dy * yScale;
                int   sy0 = (int)fy;
                int   sy1 = Math.Min(sy0 + 1, srcH - 1);
                float yt  = fy - sy0;

                byte* srcRow0 = pSrc + sy0 * srcW * 4;
                byte* srcRow1 = pSrc + sy1 * srcW * 4;
                byte* dstRow  = pDst + dy  * dstW * 4;

                for (int dx = 0; dx < dstW; dx++)
                {
                    float fx  = dx * xScale;
                    int   sx0 = (int)fx;
                    int   sx1 = Math.Min(sx0 + 1, srcW - 1);
                    float xt  = fx - sx0;

                    for (int c = 0; c < 4; c++)
                    {
                        float top    = srcRow0[sx0 * 4 + c] * (1 - xt) + srcRow0[sx1 * 4 + c] * xt;
                        float bottom = srcRow1[sx0 * 4 + c] * (1 - xt) + srcRow1[sx1 * 4 + c] * xt;
                        dstRow[dx * 4 + c] = (byte)(top * (1 - yt) + bottom * yt);
                    }
                }
            }
        }

        return dst;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(BridgeStatus status, string message)
    {
        Status        = status;
        StatusMessage = message;
        // Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{Status}] {StatusMessage}");
    }

    private static void DiagnosticCheckMemory()
    {
        IntPtr hMap = OpenFileMappingA(0x0002, false, "UnityCapture_Data");
        if (hMap != IntPtr.Zero)
        {
            // Console.WriteLine("[Diagnostic] UnityCapture shared memory found — driver is running.");
            CloseHandle(hMap);
        }
        else
        {
            // Console.WriteLine("[Diagnostic] UnityCapture driver not yet active — will connect when it starts.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopBridge();
        _capture.FrameArrived -= OnFrameArrived;
        _capture.Dispose();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr OpenFileMappingA(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
