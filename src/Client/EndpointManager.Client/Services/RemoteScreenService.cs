namespace EndpointManager.Client.Services;

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EndpointManager.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

public class RemoteScreenService : IDisposable
{
    private readonly ILogger<RemoteScreenService> _logger;
    private readonly SemaphoreSlim _captureSignal = new SemaphoreSlim(0, 10);
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;

    // Zero-Allocation Reusable Buffers (eliminates Garbage Collection pauses)
    private Bitmap? _reusableBmp;
    private Graphics? _reusableGraphics;
    private int _cachedWidth = 0;
    private int _cachedHeight = 0;
    private readonly object _bitmapLock = new();

    // In-flight network transmission guard (Eliminates TCP queueing & lag)
    private int _isTransmitting = 0;

    // === Win32 P/Invoke Declarations ===
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

    // Win32 Clipboard P/Invoke APIs for remote clipboard sync
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    // DPI Constants
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    // GDI / Metrics Constants
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int DESKTOPHORZRES = 118;
    private const int DESKTOPVERTRES = 117;
    private const int CURSOR_SHOWING = 0x00000001;
    private const uint MAPVK_VK_TO_VSC = 0;

    // Clipboard Constants
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    // SendInput Constants
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    public RemoteScreenService(ILogger<RemoteScreenService> logger)
    {
        _logger = logger;
        try
        {
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch
        {
            try { SetProcessDPIAware(); } catch { }
        }
    }

    public static (int Width, int Height) GetPhysicalScreenSize()
    {
        try
        {
            IntPtr hdc = GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                int w = GetDeviceCaps(hdc, DESKTOPHORZRES);
                int h = GetDeviceCaps(hdc, DESKTOPVERTRES);
                ReleaseDC(IntPtr.Zero, hdc);
                if (w > 0 && h > 0)
                {
                    return (w, h);
                }
            }
        }
        catch { }

        int fallbackW = GetSystemMetrics(SM_CXSCREEN);
        int fallbackH = GetSystemMetrics(SM_CYSCREEN);
        return (fallbackW > 0 ? fallbackW : 1920, fallbackH > 0 ? fallbackH : 1080);
    }

    private void EnsureBuffers(int width, int height)
    {
        lock (_bitmapLock)
        {
            if (_reusableBmp == null || _cachedWidth != width || _cachedHeight != height)
            {
                _reusableGraphics?.Dispose();
                _reusableBmp?.Dispose();

                _cachedWidth = width;
                _cachedHeight = height;

                _reusableBmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                _reusableGraphics = Graphics.FromImage(_reusableBmp);
                _reusableGraphics.InterpolationMode = InterpolationMode.Low;
                _reusableGraphics.CompositingQuality = CompositingQuality.HighSpeed;
                _reusableGraphics.SmoothingMode = SmoothingMode.HighSpeed;
            }
        }
    }

    public void StartScreenStream(string targetAdminConnectionId, HubConnection hubConnection)
    {
        StopScreenStream();

        _logger.LogInformation("Starting High-Performance Remote Screen Capture Stream -> Admin: {AdminId}", targetAdminConnectionId);
        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;

        _streamTask = Task.Run(async () =>
        {
            var jpegCodec = GetJpegEncoder();
            var encoderParams = new EncoderParameters(1);
            // 40% quality provides high compression, crystal clear readability for CRM/text, and 3x faster network transmission
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 40L);

            _logger.LogInformation("Screen stream capture loop active -> Admin: {AdminId}", targetAdminConnectionId);

            while (!token.IsCancellationRequested && hubConnection.State == HubConnectionState.Connected)
            {
                try
                {
                    // 1. BACKPRESSURE: If the network is still transmitting the previous frame,
                    // SKIP this capture tick! This prevents TCP buffer bloat and ensures the admin
                    // ALWAYS sees the latest real-time frame with ZERO lag or queue delay.
                    if (Interlocked.CompareExchange(ref _isTransmitting, 1, 0) != 0)
                    {
                        await _captureSignal.WaitAsync(35, token);
                        continue;
                    }

                    // 2. Zero-allocation desktop screen capture
                    var (screenWidth, screenHeight) = GetPhysicalScreenSize();
                    EnsureBuffers(screenWidth, screenHeight);

                    string? base64Frame = null;
                    using (var ms = new MemoryStream(65536))
                    {
                        lock (_bitmapLock)
                        {
                            if (_reusableGraphics != null && _reusableBmp != null)
                            {
                                try
                                {
                                    _reusableGraphics.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy);

                                    // Render Windows native mouse cursor directly onto the captured frame
                                    try
                                    {
                                        var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
                                        if (GetCursorInfo(ref ci) && ci.flags == CURSOR_SHOWING && ci.hCursor != IntPtr.Zero)
                                        {
                                            IntPtr hdc = _reusableGraphics.GetHdc();
                                            try
                                            {
                                                DrawIcon(hdc, ci.ptScreenPos.x, ci.ptScreenPos.y, ci.hCursor);
                                            }
                                            finally
                                            {
                                                _reusableGraphics.ReleaseHdc(hdc);
                                            }
                                        }
                                    }
                                    catch { }

                                    if (jpegCodec != null)
                                    {
                                        _reusableBmp.Save(ms, jpegCodec, encoderParams);
                                    }
                                    else
                                    {
                                        _reusableBmp.Save(ms, ImageFormat.Jpeg);
                                    }
                                }
                                catch (Exception capEx)
                                {
                                    _logger.LogDebug("CopyFromScreen error: {Msg}", capEx.Message);
                                }
                            }
                        }

                        if (ms.Length > 0)
                        {
                            base64Frame = Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
                        }
                    }

                    if (string.IsNullOrEmpty(base64Frame))
                    {
                        Interlocked.Exchange(ref _isTransmitting, 0);
                        await _captureSignal.WaitAsync(50, token);
                        continue;
                    }

                    // 3. Non-blocking asynchronous frame transmission
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await hubConnection.SendAsync("SendScreenFrame", targetAdminConnectionId, base64Frame, token);
                        }
                        catch { }
                        finally
                        {
                            Interlocked.Exchange(ref _isTransmitting, 0);
                        }
                    }, token);

                    // 4. Smooth ~28 FPS loop interval (35ms), or woke immediately by user interactions!
                    await _captureSignal.WaitAsync(35, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _isTransmitting, 0);
                    _logger.LogWarning("Screen capture loop error: {Msg}", ex.Message);
                    await Task.Delay(200, token);
                }
            }
        }, token);
    }

    public void StopScreenStream()
    {
        if (_streamCts != null)
        {
            _logger.LogInformation("Stopping Remote Screen Stream.");
            try
            {
                _streamCts.Cancel();
                _streamCts.Dispose();
            }
            catch { }
            _streamCts = null;
            _streamTask = null;
        }

        lock (_bitmapLock)
        {
            _reusableGraphics?.Dispose();
            _reusableGraphics = null;
            _reusableBmp?.Dispose();
            _reusableBmp = null;
        }
    }

    public void ProcessRemoteInput(RemoteInputEvent input)
    {
        try
        {
            var (screenWidth, screenHeight) = GetPhysicalScreenSize();

            double nx = Math.Clamp(input.NormalizedX, 0.0, 1.0);
            double ny = Math.Clamp(input.NormalizedY, 0.0, 1.0);

            // Physical pixel coordinates for SetCursorPos
            int pixelX = (int)Math.Round(nx * (screenWidth - 1));
            int pixelY = (int)Math.Round(ny * (screenHeight - 1));
            pixelX = Math.Clamp(pixelX, 0, screenWidth - 1);
            pixelY = Math.Clamp(pixelY, 0, screenHeight - 1);

            // Windows normalized 0..65535 coordinates for MOUSEEVENTF_ABSOLUTE
            int normX = (int)Math.Round(nx * 65535.0);
            int normY = (int)Math.Round(ny * 65535.0);
            normX = Math.Clamp(normX, 0, 65535);
            normY = Math.Clamp(normY, 0, 65535);

            switch (input.EventType)
            {
                case RemoteInputType.MouseMove:
                    SetCursorPos(pixelX, pixelY);
                    SendMouseInput(normX, normY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, 0);
                    break;

                case RemoteInputType.MouseDown:
                    SetCursorPos(pixelX, pixelY);
                    uint downFlag = input.Button switch
                    {
                        2 => MOUSEEVENTF_RIGHTDOWN,
                        1 => MOUSEEVENTF_MIDDLEDOWN,
                        _ => MOUSEEVENTF_LEFTDOWN
                    };
                    SendMouseInput(normX, normY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | downFlag, 0);
                    TriggerBurstCapture();
                    break;

                case RemoteInputType.MouseUp:
                    SetCursorPos(pixelX, pixelY);
                    uint upFlag = input.Button switch
                    {
                        2 => MOUSEEVENTF_RIGHTUP,
                        1 => MOUSEEVENTF_MIDDLEUP,
                        _ => MOUSEEVENTF_LEFTUP
                    };
                    SendMouseInput(normX, normY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | upFlag, 0);
                    TriggerBurstCapture();
                    break;

                case RemoteInputType.MouseClick:
                    SetCursorPos(pixelX, pixelY);
                    uint cDown = input.Button == 2 ? MOUSEEVENTF_RIGHTDOWN : (input.Button == 1 ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_LEFTDOWN);
                    uint cUp   = input.Button == 2 ? MOUSEEVENTF_RIGHTUP   : (input.Button == 1 ? MOUSEEVENTF_MIDDLEUP   : MOUSEEVENTF_LEFTUP);
                    SendMouseInput(normX, normY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | cDown, 0);
                    SendMouseInput(normX, normY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | cUp, 0);
                    TriggerBurstCapture();
                    break;

                case RemoteInputType.DoubleClick:
                    SetCursorPos(pixelX, pixelY);
                    SendMouseInput(normX, normY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_LEFTDOWN, 0);
                    SendMouseInput(normX, normY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_LEFTUP, 0);
                    Thread.Sleep(25);
                    SendMouseInput(normX, normY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_LEFTDOWN, 0);
                    SendMouseInput(normX, normY, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_LEFTUP, 0);
                    TriggerBurstCapture();
                    break;

                case RemoteInputType.MouseWheel:
                    SetCursorPos(pixelX, pixelY);
                    int delta = input.WheelDelta != 0 ? input.WheelDelta : -120;
                    SendMouseInput(normX, normY, MOUSEEVENTF_WHEEL, (uint)delta);
                    TriggerImmediateCapture();
                    break;

                case RemoteInputType.KeyDown:
                    if (input.KeyCode > 0)
                    {
                        SendKeyboardEvent((ushort)input.KeyCode, true);
                    }
                    TriggerBurstCapture();
                    break;

                case RemoteInputType.KeyUp:
                    if (input.KeyCode > 0)
                    {
                        SendKeyboardEvent((ushort)input.KeyCode, false);
                    }
                    TriggerBurstCapture();
                    break;

                case RemoteInputType.TextInput:
                    if (!string.IsNullOrEmpty(input.Text))
                    {
                        // 1. Synchronize remote machine's Windows OS Clipboard for pasted strings
                        if (input.Text.Length > 1)
                        {
                            SetRemoteClipboardText(input.Text);
                        }

                        // 2. Direct Unicode injection with character simulation into active control (Run box, Chrome, Notepad, etc.)
                        SendUnicodeText(input.Text);
                    }
                    TriggerBurstCapture();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error simulating remote input: {Msg}", ex.Message);
        }
    }

    private static void SendMouseInput(int dx, int dy, uint flags, uint mouseData)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = dx,
                    dy = dy,
                    mouseData = mouseData,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendKeyboardEvent(ushort keyCode, bool isDown)
    {
        uint flags = isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP;

        if (keyCode is >= 33 and <= 46 or 91 or 92 or 93 or 144 or 145)
        {
            flags |= KEYEVENTF_EXTENDEDKEY;
        }

        // Map Virtual-Key code to hardware scan code so Windows Run dialog and native controls accept it!
        ushort scanCode = (ushort)MapVirtualKey(keyCode, MAPVK_VK_TO_VSC);

        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = keyCode,
                    wScan = scanCode,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = UIntPtr.Zero
                }
            }
        };

        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    private static void SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Release modifier keys so they don't corrupt Unicode characters (e.g. Ctrl+C instead of 'c')
        SendKeyboardEvent(0x11, false); // Ctrl up
        SendKeyboardEvent(0x12, false); // Alt up
        SendKeyboardEvent(0x10, false); // Shift up

        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n')
            {
                // Enter key down & up
                ushort enterScan = (ushort)MapVirtualKey(0x0D, MAPVK_VK_TO_VSC);
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0x0D,
                            wScan = enterScan,
                            dwFlags = KEYEVENTF_KEYDOWN,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                });
                inputs.Add(new INPUT
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0x0D,
                            wScan = enterScan,
                            dwFlags = KEYEVENTF_KEYUP,
                            time = 0,
                            dwExtraInfo = UIntPtr.Zero
                        }
                    }
                });
                continue;
            }

            // Character key down & key up using KEYEVENTF_UNICODE
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            });

            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = (ushort)c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = UIntPtr.Zero
                    }
                }
            });
        }

        if (inputs.Count > 0)
        {
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
        }
    }

    public static void SetRemoteClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (OpenClipboard(IntPtr.Zero))
                {
                    try
                    {
                        EmptyClipboard();
                        int bytes = (text.Length + 1) * 2;
                        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                        if (hMem != IntPtr.Zero)
                        {
                            IntPtr pMem = GlobalLock(hMem);
                            if (pMem != IntPtr.Zero)
                            {
                                Marshal.Copy(text.ToCharArray(), 0, pMem, text.Length);
                                Marshal.WriteInt16(pMem + (text.Length * 2), 0);
                                GlobalUnlock(hMem);
                                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                                {
                                    GlobalFree(hMem);
                                }
                            }
                            else
                            {
                                GlobalFree(hMem);
                            }
                        }
                    }
                    finally
                    {
                        CloseClipboard();
                    }
                    break;
                }
                Thread.Sleep(10);
            }
        }
        catch { }
    }

    private void TriggerImmediateCapture()
    {
        if (_captureSignal.CurrentCount < 3)
        {
            try
            {
                _captureSignal.Release();
            }
            catch (SemaphoreFullException) { }
        }
    }

    // Burst captures right after click or keystroke to capture window opening animations with 0 latency
    private void TriggerBurstCapture()
    {
        TriggerImmediateCapture();
        _ = Task.Run(async () =>
        {
            await Task.Delay(35);
            TriggerImmediateCapture();
            await Task.Delay(55);
            TriggerImmediateCapture();
            await Task.Delay(90);
            TriggerImmediateCapture();
        });
    }

    private static ImageCodecInfo? GetJpegEncoder()
    {
        var codecs = ImageCodecInfo.GetImageEncoders();
        foreach (var codec in codecs)
        {
            if (codec.FormatID == ImageFormat.Jpeg.Guid)
            {
                return codec;
            }
        }
        return null;
    }

    public void Dispose()
    {
        StopScreenStream();
        _captureSignal.Dispose();
    }
}
