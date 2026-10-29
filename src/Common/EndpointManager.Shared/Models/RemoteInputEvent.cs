namespace EndpointManager.Shared.Models;

public enum RemoteInputType
{
    MouseMove = 1,
    MouseDown = 2,
    MouseUp = 3,
    MouseClick = 4,
    DoubleClick = 5,
    KeyDown = 6,
    KeyUp = 7,
    TextInput = 8,
    MouseWheel = 9
}

public class RemoteInputEvent
{
    public RemoteInputType EventType { get; set; }
    public double NormalizedX { get; set; } // 0.0 to 1.0 (relative to remote screen width)
    public double NormalizedY { get; set; } // 0.0 to 1.0 (relative to remote screen height)
    public int Button { get; set; } // 0: Left, 1: Middle, 2: Right
    public int KeyCode { get; set; }
    public string? Text { get; set; }
    public int WheelDelta { get; set; } // >0 = scroll up, <0 = scroll down
}
