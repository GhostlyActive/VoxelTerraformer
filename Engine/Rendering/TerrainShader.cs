using Raylib_cs;
using System.Numerics;

namespace VoxelEngine.Rendering;

/// <summary>A local light for the terrain shader: where it sits, how far it carries, what colour</summary>
public readonly record struct PointLight(Vector3 Position, float Range, Vector3 Color);

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

// Local lights: an explosion, a lava pool, a muzzle flash. xyz is the position, w the range.
#define MAX_POINT_LIGHTS 8
uniform int pointLightCount;
uniform vec4 pointLights[MAX_POINT_LIGHTS];
uniform vec4 pointLightColors[MAX_POINT_LIGHTS];

// The mode-switch wave: a glowing ring on the ground at waveRadius around waveCenter, with the
// terrain outside it muted so the two sides read as old and new. waveStrength 0 turns it off.
uniform vec3 waveCenter;
uniform float waveRadius;
uniform float waveWidth;
uniform float waveStrength;
uniform vec3 waveColor;

// A faint grid drawn into the surface near the camera, which is how Sculpt mode shows its
// 12.5 cm cells on terrain that looks like plain blocks otherwise. gridStrength 0 turns it off.
uniform float gridSpacing;
uniform float gridStrength;

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

vec3 pointLightAt(vec3 position, vec3 normal)
{
    vec3 sum = vec3(0.0);

    for (int i = 0; i < pointLightCount; i++)
    {
        vec3 toLight = pointLights[i].xyz - position;
        float distance = length(toLight);
        float range = pointLights[i].w;
        if (distance >= range) continue;

        // Quadratic falloff that actually reaches zero at the range, so a light has a clear edge
        float attenuation = 1.0 - distance / range;
        attenuation *= attenuation;

        float lambert = max(dot(normal, toLight / max(distance, 0.0001)), 0.0);
        sum += pointLightColors[i].rgb * (lambert * attenuation);
    }

    return sum;
}

void main()
{
    vec3 normal = normalize(fragNormal);

    float diffuse = max(dot(normal, -sunDirection), 0.0);
    diffuse *= sunVisibility(fragPosition);

    vec3 lit = fragColor.rgb * (ambientColor + sunColor * diffuse + pointLightAt(fragPosition, normal));

    // The vertex colour's alpha carries emissive (0 = lit normally, 1 = self-lit)
    vec3 color = mix(lit, fragColor.rgb, fragColor.a);

    float eyeDistance = length(fragPosition - cameraPosition);

    if (gridStrength > 0.0)
    {
        // One-pixel lines on the cell borders of each face, fading out with distance so the far
        // terrain stays clean. The axis the face is perpendicular to is masked out: fwidth of a
        // constant is zero and would put a line everywhere.
        vec3 p = fragPosition / gridSpacing;
        vec3 w = max(fwidth(p), 1e-4);
        vec3 g = abs(fract(p - 0.5) - 0.5) / w + abs(normal) * 10.0;
        float line = clamp(1.0 - min(min(g.x, g.y), g.z), 0.0, 1.0);
        float near = 1.0 - smoothstep(6.0, 14.0, eyeDistance);
        color *= 1.0 - 0.16 * line * near * gridStrength;
    }

    if (waveStrength > 0.0)
    {
        float width = max(waveWidth, 0.01);
        float d = length(fragPosition.xz - waveCenter.xz);

        float band = 1.0 - smoothstep(0.0, width, abs(d - waveRadius));
        vec3 toEye = normalize(cameraPosition - fragPosition);
        float rim = pow(1.0 - max(dot(normal, toEye), 0.0), 2.0);
        color += waveColor * band * waveStrength * (0.3 + 0.5 * rim);

        float outside = smoothstep(waveRadius, waveRadius + width, d) * waveStrength;
        color = mix(color, vec3(dot(color, vec3(0.299, 0.587, 0.114))), 0.3 * outside);
    }

    float fog = smoothstep(fogStart, fogEnd, eyeDistance);
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
    private readonly int _locPointLightCount;
    private readonly int _locPointLights;
    private readonly int _locPointLightColors;
    private readonly int _locWaveCenter;
    private readonly int _locWaveRadius;
    private readonly int _locWaveWidth;
    private readonly int _locWaveStrength;
    private readonly int _locWaveColor;
    private readonly int _locGridSpacing;
    private readonly int _locGridStrength;
    private Material _material;

    public Material Material => _material;

    /// <summary>The raw shader, for <see cref="MeshDrawer"/></summary>
    public Shader Shader => _shader;

    /// <summary>Has to match MAX_OCCLUDERS in the fragment shader</summary>
    public const int MaxShadowCasters = 8;

    /// <summary>Has to match MAX_POINT_LIGHTS in the fragment shader</summary>
    public const int MaxPointLights = 8;

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
        _locPointLightCount = Raylib.GetShaderLocation(_shader, "pointLightCount");
        _locPointLights = Raylib.GetShaderLocation(_shader, "pointLights");
        _locPointLightColors = Raylib.GetShaderLocation(_shader, "pointLightColors");
        _locWaveCenter = Raylib.GetShaderLocation(_shader, "waveCenter");
        _locWaveRadius = Raylib.GetShaderLocation(_shader, "waveRadius");
        _locWaveWidth = Raylib.GetShaderLocation(_shader, "waveWidth");
        _locWaveStrength = Raylib.GetShaderLocation(_shader, "waveStrength");
        _locWaveColor = Raylib.GetShaderLocation(_shader, "waveColor");
        _locGridSpacing = Raylib.GetShaderLocation(_shader, "gridSpacing");
        _locGridStrength = Raylib.GetShaderLocation(_shader, "gridStrength");

        _material = Raylib.LoadMaterialDefault();
        _material.Shader = _shader;

        // Both effects start switched off; a shader that is never told about them draws plain terrain
        SetWave(Vector3.Zero, 1e9f, 1f, 0f, Vector3.One);
        SetGrid(1f, 0f);
    }

    /// <summary>The mode-switch ring for the next draws; strength 0 hides it</summary>
    public void SetWave(Vector3 center, float radius, float width, float strength, Vector3 color)
    {
        Raylib.SetShaderValue(_shader, _locWaveCenter, center, ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(_shader, _locWaveRadius, radius, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_shader, _locWaveWidth, width, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_shader, _locWaveStrength, strength, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_shader, _locWaveColor, color, ShaderUniformDataType.Vec3);
    }

    /// <summary>The surface grid near the camera; strength 0 hides it</summary>
    public void SetGrid(float spacing, float strength)
    {
        Raylib.SetShaderValue(_shader, _locGridSpacing, MathF.Max(spacing, 0.01f), ShaderUniformDataType.Float);
        Raylib.SetShaderValue(_shader, _locGridStrength, strength, ShaderUniformDataType.Float);
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

        // Default to no shadows and no local lights; a caller that wants them sets them right after
        Raylib.SetShaderValue(_shader, _locOccluderCount, 0, ShaderUniformDataType.Int);
        Raylib.SetShaderValue(_shader, _locPointLightCount, 0, ShaderUniformDataType.Int);
    }

    /// <summary>
    /// Local lights for the next draw. Anything past <see cref="MaxPointLights"/> is ignored, so
    /// pass the brightest or nearest ones first.
    /// </summary>
    public void SetPointLights(ReadOnlySpan<PointLight> lights)
    {
        int count = Math.Min(lights.Length, MaxPointLights);
        Raylib.SetShaderValue(_shader, _locPointLightCount, count, ShaderUniformDataType.Int);

        if (count == 0) return;

        Span<Vector4> positions = stackalloc Vector4[MaxPointLights];
        Span<Vector4> colors = stackalloc Vector4[MaxPointLights];

        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector4(lights[i].Position, lights[i].Range);
            colors[i] = new Vector4(lights[i].Color, 0f);
        }

        Raylib.SetShaderValueV(_shader, _locPointLights, positions[..count], ShaderUniformDataType.Vec4, count);
        Raylib.SetShaderValueV(_shader, _locPointLightColors, colors[..count], ShaderUniformDataType.Vec4, count);
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
