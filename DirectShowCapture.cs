using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using DirectShowLib;

namespace MuteBarBridge;

internal sealed class DirectShowCapture : ISampleGrabberCB, IDisposable
{
    public event Action<byte[], int, int>? FrameArrived;

    public int Width  { get; private set; }
    public int Height { get; private set; }

    private IFilterGraph2?  _graph;
    private IMediaControl?  _control;
    private IBaseFilter?    _sourceFilter;
    private IBaseFilter?    _grabberFilter;
    private IBaseFilter?    _nullRenderer;
    private ISampleGrabber? _grabber;
    private bool            _disposed;

    private int  _bitCount = 24;
    private Guid _subType  = Guid.Empty;
    private bool _isBottomUp = false;

    private int _frameDiagCount = 0;

    int ISampleGrabberCB.SampleCB(double sampleTime, IMediaSample pSample) => 0;

    int ISampleGrabberCB.BufferCB(double sampleTime, IntPtr pBuffer, int bufferLen)
    {
        if (_disposed || pBuffer == IntPtr.Zero || bufferLen <= 0)
            return 0;

        int width  = Width;
        int height = Height;
        int bpp    = _bitCount == 12 ? 12 : _bitCount / 8;
        if (bpp == 0) bpp = 3;

        if (System.Threading.Interlocked.Increment(ref _frameDiagCount) <= 3)
        {
            // Console.WriteLine($"[BufferCB] frame#{_frameDiagCount} bufferLen={bufferLen} width={width} height={height} _bitCount={_bitCount} initial_bpp={bpp}");

        }
        int expectedLen = (bpp == 12 || _bitCount == 12) ? (width * height * 3) / 2 : (width * height * bpp);

        // Fix: Remove "bufferLen < expectedLen" so 32-bit buffers (8,294,400 bytes) are properly recognized
        if (bufferLen != expectedLen)
        {
            if      (bufferLen == 3686400 || bufferLen == 8294400) bpp = 4;  // 720p or 1080p BGRA32
            else if (bufferLen == 2764800 || bufferLen == 6220800) bpp = 3;  // 720p or 1080p RGB24
            else if (bufferLen == 1843200 || bufferLen == 4147200) bpp = 2;  // 720p or 1080p YUY2/UYVY
            else if (bufferLen == 1382400 || bufferLen == 3110400) bpp = 12; // 720p or 1080p NV12/I420
            else
            {
                if (bufferLen * 2 == width * height * 3) bpp = 12;
                else
                {
                    int calcBpp = bufferLen / (width * height);
                    if (calcBpp is 2 or 3 or 4) bpp = calcBpp;
                }
            }
        }

        if (_frameDiagCount <= 3)
        {
            // Console.WriteLine($"[BufferCB] final bpp={bpp} pixels={width*height} expected_bgra={width*height*4}");

        }
        int pixels = width * height;
        byte[] bgra = new byte[pixels * 4];

        unsafe
        {
            byte* src = (byte*)pBuffer;

            if (bpp == 12 || _bitCount == 12)
            {
                int yPlaneSize = pixels;
                byte* yPlane  = src;
                byte* uvPlane = src + yPlaneSize;

                for (int row = 0; row < height; row++)
                {
                    int dstOff = row * width * 4;
                    for (int col = 0; col < width; col++)
                    {
                        int yIdx  = row * width + col;
                        int uvIdx = (row / 2) * width + (col & ~1);

                        int y = yPlane[yIdx] - 16;
                        int u = uvPlane[uvIdx + 0] - 128;
                        int v = uvPlane[uvIdx + 1] - 128;

                        int r = (298 * y + 409 * v + 128) >> 8;
                        int g = (298 * y - 100 * u - 208 * v + 128) >> 8;
                        int b = (298 * y + 516 * u + 128) >> 8;

                        bgra[dstOff + col * 4 + 0] = (byte)Math.Clamp(b, 0, 255);
                        bgra[dstOff + col * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
                        bgra[dstOff + col * 4 + 2] = (byte)Math.Clamp(r, 0, 255);
                        bgra[dstOff + col * 4 + 3] = 0xFF;
                    }
                }
            }
            else if (bpp == 4)
            {
                int srcStride = width * 4;
                for (int row = 0; row < height; row++)
                {
                    int srcRow   = row;
                    byte* srcPtr = src + srcRow * srcStride;
                    int dstOff   = row * width * 4;
                    for (int col = 0; col < width; col++)
                    {
                        bgra[dstOff + col * 4 + 0] = srcPtr[col * 4 + 0]; // B
                        bgra[dstOff + col * 4 + 1] = srcPtr[col * 4 + 1]; // G
                        bgra[dstOff + col * 4 + 2] = srcPtr[col * 4 + 2]; // R
                        bgra[dstOff + col * 4 + 3] = 0xFF;
                    }
                }
            }
            else if (bpp == 3)
            {
                int srcStride = ((width * 3 + 3) / 4) * 4;
                for (int row = 0; row < height; row++)
                {
                    int srcRow   = _isBottomUp ? (height - 1 - row) : row;
                    byte* srcPtr = src + srcRow * srcStride;
                    int dstOff   = row * width * 4;
                    for (int col = 0; col < width; col++)
                    {
                        bgra[dstOff + col * 4 + 0] = srcPtr[col * 3 + 0]; // B
                        bgra[dstOff + col * 4 + 1] = srcPtr[col * 3 + 1]; // G
                        bgra[dstOff + col * 4 + 2] = srcPtr[col * 3 + 2]; // R
                        bgra[dstOff + col * 4 + 3] = 0xFF;
                    }
                }
            }
            else if (bpp == 2)
            {
                int srcStride = width * 2;
                for (int row = 0; row < height; row++)
                {
                    int srcRow   = _isBottomUp ? (height - 1 - row) : row;
                    byte* srcPtr = src + srcRow * srcStride;
                    int dstOff   = row * width * 4;
                    for (int col = 0; col < width; col += 2)
                    {
                        int y0 = srcPtr[col * 2 + 0] - 16;
                        int u  = srcPtr[col * 2 + 1] - 128;
                        int y1 = srcPtr[col * 2 + 2] - 16;
                        int v  = srcPtr[col * 2 + 3] - 128;

                        int r0 = (298 * y0 + 409 * v + 128) >> 8;
                        int g0 = (298 * y0 - 100 * u - 208 * v + 128) >> 8;
                        int b0 = (298 * y0 + 516 * u + 128) >> 8;

                        int r1 = (298 * y1 + 409 * v + 128) >> 8;
                        int g1 = (298 * y1 - 100 * u - 208 * v + 128) >> 8;
                        int b1 = (298 * y1 + 516 * u + 128) >> 8;

                        bgra[dstOff + col * 4 + 0] = (byte)Math.Clamp(b0, 0, 255);
                        bgra[dstOff + col * 4 + 1] = (byte)Math.Clamp(g0, 0, 255);
                        bgra[dstOff + col * 4 + 2] = (byte)Math.Clamp(r0, 0, 255);
                        bgra[dstOff + col * 4 + 3] = 0xFF;

                        bgra[dstOff + (col + 1) * 4 + 0] = (byte)Math.Clamp(b1, 0, 255);
                        bgra[dstOff + (col + 1) * 4 + 1] = (byte)Math.Clamp(g1, 0, 255);
                        bgra[dstOff + (col + 1) * 4 + 2] = (byte)Math.Clamp(r1, 0, 255);
                        bgra[dstOff + (col + 1) * 4 + 3] = 0xFF;
                    }
                }
            }
        }

        FrameArrived?.Invoke(bgra, width, height);
        return 0;
    }

    private static byte Clamp(int val) => (byte)(val < 0 ? 0 : (val > 255 ? 255 : val));

    public static List<string> EnumerateDevices()
    {
        var names = new List<string>();
        var devEnum = (ICreateDevEnum)new CreateDevEnum()
            ?? throw new COMException("Cannot create device enumerator");

        Guid videoInputCategory = FilterCategory.VideoInputDevice;
        int hr = devEnum.CreateClassEnumerator(videoInputCategory, out IEnumMoniker? enumMon, 0);
        if (hr != 0 || enumMon == null) return names;

        var mon = new IMoniker?[1];
        Guid propBagGuid = typeof(IPropertyBag).GUID;

        while (enumMon.Next(1, mon!, IntPtr.Zero) == 0 && mon[0] != null)
        {
            mon[0]!.GetDisplayName(null!, null!, out string name);
            names.Add(name);

            mon[0]!.BindToStorage(null!, null!, ref propBagGuid, out object pbObj);
            if (pbObj is IPropertyBag pb)
            {
                if (pb.Read("FriendlyName", out object? val, null) == 0)
                    names[^1] = val?.ToString() ?? name;
                Marshal.ReleaseComObject(pb);
            }
            Marshal.ReleaseComObject(mon[0]!);
        }
        Marshal.ReleaseComObject(enumMon);
        Marshal.ReleaseComObject(devEnum);
        return names;
    }

    public void Start(string friendlyName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _sourceFilter = FindCaptureFilter(friendlyName)
            ?? throw new Exception($"DirectShow device not found: \"{friendlyName}\"");

        _graph   = new FilterGraph() as IFilterGraph2
                   ?? throw new COMException("Cannot create FilterGraph");
        _control = (IMediaControl)_graph;

        _graph.AddFilter(_sourceFilter, "Source");

        _grabberFilter = new SampleGrabber() as IBaseFilter
                         ?? throw new COMException("Cannot create SampleGrabber");
        _grabber = (ISampleGrabber)_grabberFilter;

        // Try to negotiate 32-bit RGB (MediaSubType.RGB32) first to match SoftCam natively
        var mt = new AMMediaType
        {
            majorType  = MediaType.Video,
            subType    = MediaSubType.RGB32,
            formatType = FormatType.VideoInfo
        };
        int hr = _grabber.SetMediaType(mt);
        if (hr != 0)
        {
            // Fallback to empty subtype if RGB32 is rejected
            mt.subType = Guid.Empty;
            _grabber.SetMediaType(mt);
        }
        DsUtils.FreeAMMediaType(mt);

        _grabber.SetBufferSamples(true);
        _grabber.SetOneShot(false);
        _grabber.SetCallback(this, 1);

        _graph.AddFilter(_grabberFilter, "SampleGrabber");

        _nullRenderer = new NullRenderer() as IBaseFilter
                        ?? throw new COMException("Cannot create NullRenderer");
        _graph.AddFilter(_nullRenderer, "NullRenderer");

        var capture = new CaptureGraphBuilder2() as ICaptureGraphBuilder2
                      ?? throw new COMException("Cannot create CaptureGraphBuilder2");
        capture.SetFiltergraph(_graph);

        Guid mediaType = MediaType.Video;
        Guid pin       = PinCategory.Capture;
        int buildHr = capture.RenderStream(pin, mediaType, _sourceFilter, _grabberFilter, _nullRenderer);
        Marshal.ReleaseComObject(capture);

        if (buildHr < 0)
            throw new COMException($"RenderStream failed (HRESULT 0x{buildHr:X8})", buildHr);

        var connectedMt = new AMMediaType();
        _grabber.GetConnectedMediaType(connectedMt);
        _subType = connectedMt.subType;
        if (connectedMt.formatPtr != IntPtr.Zero)
        {
            var vi = Marshal.PtrToStructure<VideoInfoHeader>(connectedMt.formatPtr)!;

            Width = vi.BmiHeader.Width;

            // Positive height means bottom-up bitmap.
            // Negative height means top-down bitmap.
            _isBottomUp = vi.BmiHeader.Height > 0;

            Height = Math.Abs(vi.BmiHeader.Height);
            _bitCount = vi.BmiHeader.BitCount;

            /* Console.WriteLine(
                $"[Negotiated] SubType={connectedMt.subType} " +
                $"BitCount={_bitCount} " +
                $"RawHeight={vi.BmiHeader.Height} " +
                $"Width={Width} Height={Height} " +
                $"BottomUp={_isBottomUp}"); */
        }
        DsUtils.FreeAMMediaType(connectedMt);

        if (Width == 0 || Height == 0)
            throw new Exception("Could not determine frame dimensions from capture filter.");

        _control.Run();
    }

    public void Stop()
    {
        _control?.Stop();
    }

    private static IBaseFilter? FindCaptureFilter(string friendlyName)
    {
        var devEnum = (ICreateDevEnum)new CreateDevEnum()
            ?? throw new COMException("Cannot create device enumerator");

        Guid cat = FilterCategory.VideoInputDevice;
        devEnum.CreateClassEnumerator(cat, out IEnumMoniker? enumMon, 0);
        if (enumMon == null) return null;

        var mon = new IMoniker?[1];
        Guid propBagGuid = typeof(IPropertyBag).GUID;
        Guid baseFilterGuid = typeof(IBaseFilter).GUID;

        while (enumMon.Next(1, mon!, IntPtr.Zero) == 0 && mon[0] != null)
        {
            string name = "";
            mon[0]!.BindToStorage(null!, null!, ref propBagGuid, out object pbObj);
            if (pbObj is IPropertyBag pb)
            {
                pb.Read("FriendlyName", out object? val, null);
                name = val?.ToString() ?? "";
                Marshal.ReleaseComObject(pb);
            }

            if (string.Equals(name, friendlyName, StringComparison.OrdinalIgnoreCase))
            {
                mon[0]!.BindToObject(null!, null!, ref baseFilterGuid, out object filterObj);
                Marshal.ReleaseComObject(enumMon);
                Marshal.ReleaseComObject(devEnum);
                Marshal.ReleaseComObject(mon[0]!);
                return filterObj as IBaseFilter;
            }
            Marshal.ReleaseComObject(mon[0]!);
        }

        Marshal.ReleaseComObject(enumMon);
        Marshal.ReleaseComObject(devEnum);
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _control?.Stop(); } catch { /* ignore */ }

        if (_grabber       != null) Marshal.ReleaseComObject(_grabber);
        if (_grabberFilter != null) Marshal.ReleaseComObject(_grabberFilter);
        if (_nullRenderer  != null) Marshal.ReleaseComObject(_nullRenderer);
        if (_sourceFilter  != null) Marshal.ReleaseComObject(_sourceFilter);
        if (_graph         != null) Marshal.ReleaseComObject(_graph);
    }
}
