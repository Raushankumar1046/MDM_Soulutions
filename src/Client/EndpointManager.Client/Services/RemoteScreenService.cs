namespace EndpointManager.Client.Services;

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using EndpointManager.Shared.Models;
using Microsoft.AspNetCore.SignalR.Client;

public class RemoteScreenService : IDisposable
{
    private readonly ILogger<RemoteScreenService> _logger;
    private readonly SemaphoreSlim _captureSignal = new SemaphoreSlim(0, 1);
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;

    // Windows Native User32 & GDI32 APIs for DPI-Aware Screen Capture & Precise Cursor
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int DESKTOPHORZRES = 118;
    private const int DESKTOPVERTRES = 117;

    private const uint MOUSEEVENTF_LEFTDOWN   = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP     = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN  = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP    = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP   = 0x0040;

    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP   = 0x0002;

    public RemoteScreenService(ILogger<RemoteScreenService> logger)
    {
        _logger = logger;
        try { SetProcessDPIAware(); } catch { }
    }

    public static (int Width, int Height) GetPhysicalScreenSize()
    {
        try
        {
            SetProcessDPIAware();
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

    public void StartScreenStream(string targetAdminConnectionId, HubConnection hubConnection)
    {
        StopScreenStream();

        _logger.LogInformation("Starting Remote Screen Capture Stream -> Admin: {AdminId}", targetAdminConnectionId);
        _streamCts = new CancellationTokenSource();
        var token = _streamCts.Token;

        _streamTask = Task.Run(async () =>
        {
            var jpegCodec = GetJpegEncoder();
            var encoderParams = new EncoderParameters(1);
            // 50% quality provides crystal clear native desktop readability with low bandwidth
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 50L);

            _logger.LogInformation("Screen stream capture loop active -> Admin: {AdminId}", targetAdminConnectionId);

            while (!token.IsCancellationRequested && hubConnection.State == HubConnectionState.Connected)
            {
                try
                {
                    // 100% Native Physical Screen Resolution (No clipping, no forced 1280 downscaling)
                    var (screenWidth, screenHeight) = GetPhysicalScreenSize();

                    using var fullBmp = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(fullBmp))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy);
                    }

                    using var ms = new MemoryStream();
                    if (jpegCodec != null)
                    {
                        fullBmp.Save(ms, jpegCodec, encoderParams);
                    }
                    else
                    {
                        fullBmp.Save(ms, ImageFormat.Jpeg);
                    }

                    var base64Frame = Convert.ToBase64String(ms.ToArray());
                    // Non-blocking fire-and-forget frame dispatch
                    await hubConnection.SendAsync("SendScreenFrame", targetAdminConnectionId, base64Frame, token);

                    // Wait 75ms for regular stream (~13 FPS), OR wake up immediately when user clicks or types!
                    await _captureSignal.WaitAsync(75, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Screen capture error: {Msg}", ex.Message);
                    await Task.Delay(300, token);
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
    }

    public void ProcessRemoteInput(RemoteInputEvent input)
    {
        try
        {
            // Exact 100% physical hardware display coordinates
            var (screenWidth, screenHeight) = GetPhysicalScreenSize();

            int x = (int)Math.Round(input.NormalizedX * (screenWidth - 1));
            int y = (int)Math.Round(input.NormalizedY * (screenHeight - 1));

            x = Math.Max(0, Math.Min(screenWidth - 1, x));
            y = Math.Max(0, Math.Min(screenHeight - 1, y));

            switch (input.EventType)
            {
                case RemoteInputType.MouseMove:
                    SetCursorPos(x, y);
                    break;

                case RemoteInputType.MouseDown:
                    SetCursorPos(x, y);
                    if (input.Button == 0) mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    else if (input.Button == 2) mouse_event(MOUSEEVENTF_RIGHTDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    else if (input.Button == 1) mouse_event(MOUSEEVENTF_MIDDLEDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    break;

                case RemoteInputType.MouseUp:
                    SetCursorPos(x, y);
                    if (input.Button == 0) mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    else if (input.Button == 2) mouse_event(MOUSEEVENTF_RIGHTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    else if (input.Button == 1) mouse_event(MOUSEEVENTF_MIDDLEUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    break;

                case RemoteInputType.MouseClick:
                    SetCursorPos(x, y);
                    if (input.Button == 0)
                    {
                        mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                        mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    }
                    else if (input.Button == 2)
                    {
                        mouse_event(MOUSEEVENTF_RIGHTDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                        mouse_event(MOUSEEVENTF_RIGHTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    }
                    break;

                case RemoteInputType.DoubleClick:
                    SetCursorPos(x, y);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    Thread.Sleep(50);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, (uint)x, (uint)y, 0, UIntPtr.Zero);
                    break;

                case RemoteInputType.KeyDown:
                    if (input.KeyCode > 0)
                    {
                        keybd_event((byte)input.KeyCode, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                    }
                    break;

                case RemoteInputType.KeyUp:
                    if (input.KeyCode > 0)
                    {
                        keybd_event((byte)input.KeyCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    }
                    break;

                case RemoteInputType.TextInput:
                    if (!string.IsNullOrEmpty(input.Text))
                    {
                        // Simulate characters
                        foreach (char c in input.Text)
                        {
                            short vk = (short)c;
                            keybd_event((byte)vk, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                            keybd_event((byte)vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        }
                    }
                    break;
            }

            // Immediately capture and transmit frame if user interacted (clicked, pressed key, typed)
            if (input.EventType != RemoteInputType.MouseMove)
            {
                TriggerImmediateCapture();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error simulating remote input: {Msg}", ex.Message);
        }
    }

    private void TriggerImmediateCapture()
    {
        if (_captureSignal.CurrentCount == 0)
        {
            try
            {
                _captureSignal.Release();
            }
            catch (SemaphoreFullException) { }
        }
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
