namespace Terraformer.World;

/// <summary>
/// Die drei Voxel-Stufen. Alle arbeiten auf denselben Weltdaten, das Umschalten ist verlustfrei:
/// nur Werkzeug und Darstellung ändern sich, nicht die gespeicherte Geometrie.
/// </summary>
public enum TerrainMode
{
    /// <summary>Ganze Blöcke setzen/entfernen, kantige Darstellung</summary>
    Blocks,

    /// <summary>Kugel-Brush auf Sub-Voxel-Ebene, kantige Darstellung</summary>
    Sculpt,

    /// <summary>Kugel-Brush auf Sub-Voxel-Ebene, geglättete Marching-Cubes-Oberfläche</summary>
    Smooth,
}
