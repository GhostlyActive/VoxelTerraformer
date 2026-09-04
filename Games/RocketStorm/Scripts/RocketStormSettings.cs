namespace Games.RocketStorm;

/// <summary>The game's own dials, shown in the tuning menu under ROCKET STORM and saved with the game</summary>
public sealed class RocketStormSettings
{
    /// <summary>Seconds between two waves: the time you get to dig in</summary>
    public float WavePause { get; set; } = 4.5f;

    public int RocketsInFirstWave { get; set; } = 3;

    /// <summary>Crater radius in metres, and with it the zone where a hit lands at full force</summary>
    public float CraterRadius { get; set; } = 4.5f;

    /// <summary>Distance beyond which an impact does no damage at all</summary>
    public float DamageRadius { get; set; } = 13f;
}
