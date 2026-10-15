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
    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;

    // Windows Native User32 APIs for Remote Mouse & Keyboard Input
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

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
            // 55% quality gives great visual clarity while keeping frame size ~40-70KB for fluid 6-8 FPS
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 55L);

            while (!token.IsCancellationRequested && hubConnection.State == HubConnectionState.Connected)
            {
                try
                {
                    int screenWidth = GetSystemMetrics(SM_CXSCREEN);
                    int screenHeight = GetSystemMetrics(SM_CYSCREEN);

                    if (screenWidth <= 0 || screenHeight <= 0)
                    {
                        screenWidth = 1920;
                        screenHeight = 1080;
                    }

                    // Target resolution for responsive remote assistance (max 1280 wide to save bandwidth)
                    int targetWidth = Math.Min(screenWidth, 1280);
                    int targetHeight = (int)((double)targetWidth / screenWidth * screenHeight);

                    using var fullBmp = new Bitmap(screenWidth, screenHeight, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(fullBmp))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, new Size(screenWidth, screenHeight), CopyPixelOperation.SourceCopy);
                    }

                    using var resizedBmp = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);
                    using (var gResize = Graphics.FromImage(resizedBmp))
                    {
                        gResize.InterpolationMode = InterpolationMode.Low;
                        gResize.DrawImage(fullBmp, 0, 0, targetWidth, targetHeight);
                    }

                    using var ms = new MemoryStream();
                    if (jpegCodec != null)
                    {
                        resizedBmp.Save(ms, jpegCodec, encoderParams);
                    }
                    else
                    {
                        resizedBmp.Save(ms, ImageFormat.Jpeg);
                    }

                    var base64Frame = Convert.ToBase64String(ms.ToArray());
                    await hubConnection.InvokeAsync("SendScreenFrame", targetAdminConnectionId, base64Frame, token);

                    // ~8-10 Frames Per Second (120ms interval)
                    await Task.Delay(120, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Screen capture frame dropped: {Msg}", ex.Message);
                    await Task.Delay(500, token);
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
            int screenWidth = GetSystemMetrics(SM_CXSCREEN);
            int screenHeight = GetSystemMetrics(SM_CYSCREEN);

            int x = (int)(input.NormalizedX * screenWidth);
            int y = (int)(input.NormalizedY * screenHeight);

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
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Error simulating remote input: {Msg}", ex.Message);
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
    }
}
