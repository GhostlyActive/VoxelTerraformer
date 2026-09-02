using Raylib_cs;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>
/// Shader for the chunk meshes: vertex colours (albedo plus baked ambient occlusion) and dynamic
/// sunlight. GLSL 330 runs on Windows, Linux and macOS.
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
uniform vec3 sunDirection;   // normalized, points from the sun into the world
uniform vec3 sunColor;
uniform vec3 ambientColor;
uniform vec3 cameraPosition;
uniform vec3 fogColor;       // sky colour, so the world blends into the horizon
uniform float fogStart;
uniform float fogEnd;
out vec4 finalColor;
void main()
{
    float diffuse = max(dot(normalize(fragNormal), -sunDirection), 0.0);
    vec3 lit = fragColor.rgb * (ambientColor + sunColor * diffuse);

    // The vertex colour's alpha carries emissive (0 = lit normally, 1 = self-lit)
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

    /// <summary>Set the lighting for the next draw. sunDirection points from the light into the world.</summary>
    public void SetLighting(Vector3 sunDirection, Vector3 sunColor, Vector3 ambientColor, Color fogColor, Vector3 cameraPosition)
    {
        var fog = new Vector3(fogColor.R, fogColor.G, fogColor.B) / 255f;

        Raylib.SetShaderValue(_shader, _locSunDirection, Vector3.Normalize(sunDirection), ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locSunColor, sunColor, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locAmbientColor, ambientColor, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locCameraPosition, cameraPosition, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locFogColor, fog, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locFogStart, FogStart, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_shader, _locFogEnd, FogEnd, ShaderUniformDataType.Float);
    }

    /// <summary>Take the lighting from the day cycle; fog uses the sky colour so the world blends into the horizon</summary>
    public void SetFrame(DayNightCycle dayNight, Vector3 cameraPosition)
        => SetLighting(dayNight.SunDirection, dayNight.SunlightColor, dayNight.AmbientColor, dayNight.SkyColor, cameraPosition);

    public void Unload()
    {
        // UnloadMaterial releases the assigned shader along with it
        Raylib.UnloadMaterial(_material);
    }
}
