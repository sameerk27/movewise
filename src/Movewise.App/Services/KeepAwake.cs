using System.Runtime.InteropServices;

namespace Movewise.App.Services;

/// <summary>Stops Windows from sleeping while a deployment runs. The screen may still turn off.</summary>
public sealed class KeepAwake : IDisposable
{
    const uint Continuous = 0x80000000;
    const uint SystemRequired = 0x00000001;

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    KeepAwake() => SetThreadExecutionState(Continuous | SystemRequired);

    /// <summary>Call and dispose on the same thread (the window's UI thread); the setting belongs to that thread.</summary>
    public static KeepAwake Start() => new();

    public void Dispose() => SetThreadExecutionState(Continuous);
}
