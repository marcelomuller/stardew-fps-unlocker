namespace FpsUnlocker;

internal sealed class ModConfig
{
    /// <summary>Decouple rendering from the 60 Hz update loop. Off = vanilla behaviour.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Render cap in frames per second. 0 = no cap (vsync, if on, limits it to the monitor's refresh rate).</summary>
    public int MaxFps { get; set; } = 0;

    /// <summary>Blend the camera and characters between the last two ticks on in-between frames. Without it the extra frames are copies.</summary>
    public bool Interpolate { get; set; } = true;

    public bool ShowFpsCounter { get; set; } = false;
}
