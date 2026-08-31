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
uniform mat4 matModel;
out vec3 fragNormal;
out vec4 fragColor;
out vec3 fragPosition;
void main()
{
    fragNormal = vertexNormal;
    fragColor = vertexColor;
    fragPosition = vec3(matModel * vec4(vertexPosition, 1.0));
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}";

    private const string FragmentSource = @"#version 330
in vec3 fragNormal;
in vec4 fragColor;
in vec3 fragPosition;
uniform vec3 sunDirection;   // normiert, zeigt von der Sonne in die Welt
uniform vec3 sunColor;
uniform vec3 ambientColor;
uniform vec3 cameraPosition;
uniform vec3 fogColor;       // Himmelsfarbe, damit die Welt in den Horizont übergeht
uniform float fogStart;
uniform float fogEnd;
out vec4 finalColor;
void main()
{
    float diffuse = max(dot(normalize(fragNormal), -sunDirection), 0.0);
    vec3 lit = fragColor.rgb * (ambientColor + sunColor * diffuse);

    // Alpha der Vertex-Farbe transportiert Emissive (0 = normal beleuchtet, 1 = selbstleuchtend)
    vec3 color = mix(lit, fragColor.rgb, fragColor.a);

    float fog = smoothstep(fogStart, fogEnd, length(fragPosition - cameraPosition));
    color = mix(color, fogColor, fog);

    finalColor = vec4(color, 1.0);
}";

    private readonly Shader _shader;
    private readonly int _locSunDirection;
    private readonly int _locSunColor;
    private readonly int _locAmbientColor;
    private readonly int _locCameraPosition;
    private readonly int _locFogColor;
    private readonly int _locFogStart;
    private readonly int _locFogEnd;
    private Material _material;

    public Material Material => _material;

    public float FogStart { get; set; } = 100f;
    public float FogEnd { get; set; } = 230f;

    public TerrainShader()
    {
        _shader = Raylib.LoadShaderFromMemory(VertexSource, FragmentSource);
        _locSunDirection = Raylib.GetShaderLocation(_shader, "sunDirection");
        _locSunColor = Raylib.GetShaderLocation(_shader, "sunColor");
        _locAmbientColor = Raylib.GetShaderLocation(_shader, "ambientColor");
        _locCameraPosition = Raylib.GetShaderLocation(_shader, "cameraPosition");
        _locFogColor = Raylib.GetShaderLocation(_shader, "fogColor");
        _locFogStart = Raylib.GetShaderLocation(_shader, "fogStart");
        _locFogEnd = Raylib.GetShaderLocation(_shader, "fogEnd");

        _material = Raylib.LoadMaterialDefault();
        _material.Shader = _shader;
    }

    public void SetFrame(DayNightCycle dayNight, Vector3 cameraPosition)
    {
        Vector3 fogColor = new Vector3(dayNight.SkyColor.R, dayNight.SkyColor.G, dayNight.SkyColor.B) / 255f;

        Raylib.SetShaderValue(_shader, _locSunDirection, dayNight.SunDirection, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locSunColor, dayNight.SunlightColor, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locAmbientColor, dayNight.AmbientColor, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locCameraPosition, cameraPosition, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locFogColor, fogColor, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locFogStart, FogStart, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_shader, _locFogEnd, FogEnd, ShaderUniformDataType.Float);
    }

    public void Unload()
    {
        // UnloadMaterial gibt den zugewiesenen Shader mit frei
        Raylib.UnloadMaterial(_material);
    }
}
