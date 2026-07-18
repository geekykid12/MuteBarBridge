using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MuteBarBridge;

internal sealed class UnityCaptureSink : IDisposable
{
    private const string BaseMutex     = "UnityCapture_Mutx";
    private const string BaseWantEvent = "UnityCapture_Want";
    private const string BaseSentEvent = "UnityCapture_Sent";
    private const string BaseData      = "UnityCapture_Data";

    // Matching shared.inl MAX_SHARED_IMAGE_SIZE (4K RGBA 16bit per pixel)
    private const int MaxSharedImageSize = 3840 * 2160 * 4 * 2;
    private const int HeaderSize         = 32;  // 8 DWORD/int fields (8 * 4 bytes) before data[]

    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;

    private IntPtr _hWantEvent  = IntPtr.Zero;
    private IntPtr _hSentEvent  = IntPtr.Zero;
    private IntPtr _hMutex      = IntPtr.Zero;
    private IntPtr _hSharedFile = IntPtr.Zero;
    private IntPtr _pSharedBuf  = IntPtr.Zero;

    private bool _eventsCreated = false;
    private bool _disposed      = false;

    public bool IsDriverConnected => _pSharedBuf != IntPtr.Zero && !_disposed;

    public UnityCaptureSink(int width, int height)
    {
        _width  = width;
        _height = height;
        _stride = _width * 4;  // tight BGRA, no padding
    }

    public void Open()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_eventsCreated)
        {
            _hWantEvent = CreateEventA(IntPtr.Zero, false, false, BaseWantEvent);
            if (_hWantEvent == IntPtr.Zero)
                throw new Exception($"Cannot create WantFrame event (error {Marshal.GetLastWin32Error()}).");

            _hSentEvent = CreateEventA(IntPtr.Zero, false, false, BaseSentEvent);
            if (_hSentEvent == IntPtr.Zero)
                throw new Exception($"Cannot create SentFrame event (error {Marshal.GetLastWin32Error()}).");

            _eventsCreated = true;
            // Console.WriteLine("[UnityCaptureSink] Producer events created. Waiting for driver...");
        }

        if (!IsDriverConnected)
            TryConnectDriver();
    }

    private unsafe bool TryConnectDriver()
    {
        if (_hMutex == IntPtr.Zero)
        {
            _hMutex = OpenMutexA(SYNCHRONIZE | MUTEX_MODIFY_STATE, false, BaseMutex);
            if (_hMutex == IntPtr.Zero)
                return false; 
        }

        if (_hSharedFile == IntPtr.Zero)
        {
            _hSharedFile = OpenFileMappingA(FILE_MAP_WRITE, false, BaseData);
            if (_hSharedFile == IntPtr.Zero)
            {
                CloseHandle(_hMutex);
                _hMutex = IntPtr.Zero;
                return false;
            }
        }

        if (_pSharedBuf == IntPtr.Zero)
        {
            _pSharedBuf = MapViewOfFile(_hSharedFile, FILE_MAP_WRITE, 0, 0, UIntPtr.Zero);
            // Console.WriteLine($"[Driver Diagnostic] Driver MaxSize: {*(int*)_pSharedBuf}, App MaxSize: {MaxSharedImageSize}");
            if (_pSharedBuf == IntPtr.Zero)
            {
                CloseHandle(_hSharedFile); _hSharedFile = IntPtr.Zero;
                CloseHandle(_hMutex);      _hMutex      = IntPtr.Zero;
                return false;
            }
        }

        // Console.WriteLine($"[UnityCaptureSink] Driver connected — {_width}x{_height} BGRA");
        return true;
    }

    public unsafe void WriteFrame(ReadOnlySpan<byte> bgraFrame)
    {
        if (_disposed || !_eventsCreated) return;

        if (!IsDriverConnected)
        {
            TryConnectDriver();
            if (!IsDriverConnected) return; 
        }

        int rowBytes = _width * 4;
        int dataSize = rowBytes * _height;

        if (bgraFrame.Length < dataSize)
            return;

        uint waitResult = WaitForSingleObject(_hMutex, 200);
        if (waitResult != WAIT_OBJECT_0 && waitResult != WAIT_ABANDONED)
            return;

        try
        {
            byte* buf = (byte*)_pSharedBuf;
            // Clear the destination data area before writing the new frame
            System.Runtime.CompilerServices.Unsafe.InitBlock(buf + HeaderSize, 0, (uint)dataSize);
            // The receiver owns maxSize at offset 0. It is the shared buffer
            // capacity, not the current frame byte count, so leave it unchanged.
            // we wrote — writing MaxSharedImageSize (4K) here causes it to treat
            WriteInt32(buf, 4,  _width);               // width
            WriteInt32(buf, 8,  _height);              // height
            WriteInt32(buf, 12, _width * 4);           // stride — tight, no padding
            // UnityCapture uses stride as a pixel count, not a byte count.
            WriteInt32(buf, 12, _width);               // tight BGRA row
            WriteInt32(buf, 16, 0);                    // format: FORMAT_UINT8
            WriteInt32(buf, 20, 1);                    // resizemode: LINEAR
            WriteInt32(buf, 24, 0);                    // mirrormode: DISABLED
            WriteInt32(buf, 28, 0);                    // timeout

            fixed (byte* src = bgraFrame)
            {
                // DirectShow provides BGRA, while UnityCapture's FORMAT_UINT8
                // input is RGBA. Convert while copying into shared memory.
                byte* dest = buf + HeaderSize;
                for (int i = 0; i < dataSize; i += 4)
                {
                    dest[i + 0] = src[i + 2]; // R
                    dest[i + 1] = src[i + 1]; // G
                    dest[i + 2] = src[i + 0]; // B
                    dest[i + 3] = src[i + 3]; // A
                }
            }

        /* Console.WriteLine(
                $"Shared: width={*(int*)(buf + 4)}, height={*(int*)(buf + 8)}, " +
                $"stride={*(int*)(buf + 12)}, " +
                $"first={buf[HeaderSize]}, " +
                $"last={buf[HeaderSize + dataSize - 4]}"); */
        }
        finally
        {
            ReleaseMutex(_hMutex);
        }

        SetEvent(_hSentEvent);
    }

    private static unsafe void WriteInt32(byte* buf, int offset, int value)
        => *(int*)(buf + offset) = value;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_pSharedBuf  != IntPtr.Zero) { UnmapViewOfFile(_pSharedBuf);  _pSharedBuf  = IntPtr.Zero; }
        if (_hSharedFile != IntPtr.Zero) { CloseHandle(_hSharedFile);     _hSharedFile = IntPtr.Zero; }
        if (_hMutex      != IntPtr.Zero) { CloseHandle(_hMutex);          _hMutex      = IntPtr.Zero; }
        if (_hSentEvent  != IntPtr.Zero) { CloseHandle(_hSentEvent);      _hSentEvent  = IntPtr.Zero; }
        if (_hWantEvent  != IntPtr.Zero) { CloseHandle(_hWantEvent);      _hWantEvent  = IntPtr.Zero; }
    }

    // ── Win32 interop ────────────────────────────────────────────────────────
    private const uint SYNCHRONIZE        = 0x00100000;
    private const uint MUTEX_MODIFY_STATE = 0x00000001;
    private const uint FILE_MAP_WRITE     = 0x00000002;
    private const uint WAIT_OBJECT_0      = 0x00000000;
    private const uint WAIT_ABANDONED     = 0x00000080;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr CreateEventA(IntPtr lpEventAttributes, bool bManualReset,
        bool bInitialState, string lpName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr OpenMutexA(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr OpenFileMappingA(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReleaseMutex(IntPtr hMutex);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);
}
