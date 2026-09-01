# Games

Ein Spiel ist ein Ordner. Die Engine (`Engine/`, Assembly `VoxelEngine`) kennt keines davon —
sie stellt Welt, Rendering, Steuerung, Menüs und Audio bereit, die Spiele setzen darauf auf.

```
Games/<Id>/
  Scripts/          C#-Dateien des Spiels
  Assets/
    Sounds/         .wav, .ogg oder .mp3, benannt nach dem Sound-Namen im Code
```

`<Id>` ist zugleich der Ordnername im Ausgabeverzeichnis: `Context.AssetPath("Sounds/boom.wav")`
zeigt auf `Games/<Id>/Assets/Sounds/boom.wav`.

## Ein neues Spiel anlegen

1. Ordner `Games/MeinSpiel/Scripts/` anlegen.
2. Klasse von `VoxelEngine.Core.Game` ableiten (`Load`, `Update`, `DrawWorld`, `Camera`).
3. In `Program.cs` eine Zeile ergänzen:

```csharp
registry.Add("MeinSpiel", "Mein Spiel", "Kurzbeschreibung", () => new MeinSpielGame());
```

Danach steht es im ESC-Menü unter **Games**.

## Sounds

Jedes Spiel meldet seine Sounds in `Load` an:

```csharp
Context.Audio.Define("explosion", SfxShape.Explosion);
```

Liegt `Assets/Sounds/explosion.wav` vor, wird die Datei benutzt. Fehlt sie, erzeugt die Engine
aus der übergebenen `SfxShape` einen synthetischen Ersatzklang — ein Spiel klingt also auch
ohne mitgelieferte Audiodateien, und eine später hinzugelegte Datei ersetzt den Ersatz, ohne
dass sich Code ändert.

| Spiel | erwartete Dateien |
| --- | --- |
| `RocketStorm` | `launch`, `explosion`, `hit` |
| `SolarSystem` | `shot`, `impact`, `bump` |
| `FreeWalk` | — |
