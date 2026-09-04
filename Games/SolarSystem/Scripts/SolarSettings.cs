namespace Games.SolarSystem;

/// <summary>The ship and the cannon as dials, shown in the tuning menu under SHIP and saved with the game</summary>
public sealed class SolarSettings
{
    public float Thrust { get; set; } = 420f;
    public float BoostMultiplier { get; set; } = 6f;
    public float MaxSpeed { get; set; } = 2200f;

    /// <summary>Hold time for a full charge, in seconds</summary>
    public float FullChargeSeconds { get; set; } = 1.6f;
}
