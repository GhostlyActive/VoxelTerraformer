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
    // Rotate the normal into world space: voxel bodies spin, and a body-local normal would
    // drag the lit side around with them
    fragNormal = mat3(matModel) * vertexNormal;
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

// Spheres between this surface and the sun, as xyz centre and w radius. A moon passing in front
// of its planet is exactly this, and it is cheap enough to test per fragment.
#define MAX_OCCLUDERS 8
uniform int occluderCount;
uniform vec4 occluders[MAX_OCCLUDERS];

out vec4 finalColor;

float sunVisibility(vec3 position)
{
    vec3 toSun = -sunDirection;
    float visibility = 1.0;

    for (int i = 0; i < occluderCount; i++)
    {
        vec3 relative = occluders[i].xyz - position;
        float along = dot(relative, toSun);
        if (along <= 0.0) continue; // occluder is behind us, not between us and the sun

        float perpendicular = length(relative - toSun * along);
        float radius = occluders[i].w;

        // A soft rim instead of a hard edge: the sun is a disc, not a point
        visibility *= smoothstep(radius * 0.80, radius * 1.20, perpendicular);
    }

    return visibility;
}

void main()
{
    float diffuse = max(dot(normalize(fragNormal), -sunDirection), 0.0);
    diffuse *= sunVisibility(fragPosition);
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
    private readonly int _locOccluderCount;
    private readonly int _locOccluders;
    private Material _material;

    public Material Material => _material;

    /// <summary>Has to match MAX_OCCLUDERS in the fragment shader</summary>
    public const int MaxShadowCasters = 8;

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
        _locOccluderCount = Raylib.GetShaderLocation(_shader, "occluderCount");
        _locOccluders = Raylib.GetShaderLocation(_shader, "occluders");

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

        // Default to no shadows; a caller that wants them sets them right after
        Raylib.SetShaderValue(_shader, _locOccluderCount, 0, ShaderUniformDataType.Int);
    }

    /// <summary>
    /// Spheres that block the sunlight for the next draw (xyz centre, w radius). Anything past
    /// <see cref="MaxShadowCasters"/> is ignored, so pass the nearest ones first.
    /// </summary>
    public void SetShadowCasters(ReadOnlySpan<Vector4> spheres)
    {
        int count = Math.Min(spheres.Length, MaxShadowCasters);
        Raylib.SetShaderValue(_shader, _locOccluderCount, count, ShaderUniformDataType.Int);

        if (count == 0) return;

        Span<Vector4> packed = stackalloc Vector4[MaxShadowCasters];
        spheres[..count].CopyTo(packed);

        Raylib.SetShaderValueV(_shader, _locOccluders, packed[..count], ShaderUniformDataType.Vec4, count);
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
