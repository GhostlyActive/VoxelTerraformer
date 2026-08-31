using Raylib_cs;
using System.Numerics;

namespace Terraformer.Rendering;

/// <summary>
/// Shader für die Chunk-Meshes: Vertex-Farben (Albedo + gebackenes AO) plus
/// dynamisches Sonnenlicht aus dem DayNightCycle. GLSL 330 läuft auf Windows, Linux und macOS.
/// </summary>
public sealed class TerrainShader
{
    private const string VertexSource = @"#version 330
in vec3 vertexPosition;
in vec3 vertexNormal;
in vec4 vertexColor;
uniform mat4 mvp;
out vec3 fragNormal;
out vec4 fragColor;
void main()
{
    fragNormal = vertexNormal;
    fragColor = vertexColor;
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}";

    private const string FragmentSource = @"#version 330
in vec3 fragNormal;
in vec4 fragColor;
uniform vec3 sunDirection;   // normiert, zeigt von der Sonne in die Welt
uniform vec3 sunColor;
uniform vec3 ambientColor;
out vec4 finalColor;
void main()
{
    float diffuse = max(dot(normalize(fragNormal), -sunDirection), 0.0);
    vec3 lit = fragColor.rgb * (ambientColor + sunColor * diffuse);

    // Alpha der Vertex-Farbe transportiert Emissive (0 = normal beleuchtet, 1 = selbstleuchtend)
    vec3 color = mix(lit, fragColor.rgb, fragColor.a);
    finalColor = vec4(color, 1.0);
}";

    private readonly Shader _shader;
    private readonly int _locSunDirection;
    private readonly int _locSunColor;
    private readonly int _locAmbientColor;
    private Material _material;

    public Material Material => _material;

    public TerrainShader()
    {
        _shader = Raylib.LoadShaderFromMemory(VertexSource, FragmentSource);
        _locSunDirection = Raylib.GetShaderLocation(_shader, "sunDirection");
        _locSunColor = Raylib.GetShaderLocation(_shader, "sunColor");
        _locAmbientColor = Raylib.GetShaderLocation(_shader, "ambientColor");

        _material = Raylib.LoadMaterialDefault();
        _material.Shader = _shader;
    }

    public void SetFrame(Vector3 sunDirection, Vector3 sunColor, Vector3 ambientColor)
    {
        Raylib.SetShaderValue(_shader, _locSunDirection, sunDirection, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locSunColor, sunColor, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locAmbientColor, ambientColor, ShaderUniformDataType.Vec3);
    }

    public void Unload()
    {
        // UnloadMaterial gibt den zugewiesenen Shader mit frei
        Raylib.UnloadMaterial(_material);
    }
}
