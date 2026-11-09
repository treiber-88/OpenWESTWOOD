using System.Numerics;
using OpenWestwood.Client.Graphics;
using OpenWestwood.Core.Rules;
using Silk.NET.OpenGL;

namespace OpenWestwood.Client.World;

/// <summary>
/// Rain, as Kane's Wrath draws it (its Weather Rain settings, rules section "Rain" from GlobalData/Weather.xml):
/// a box (RainBoxWidth x RainBoxLength x RainBoxHeight) of NumRaindropsPerBox drops repeated around the view, each
/// a streak MinWidth-MaxWidth wide and MinHeight-MaxHeight tall falling at MinSpeed-MaxSpeed, at MinAlpha-MaxAlpha,
/// drawn with its RainTexture and leant by WindStrength. Zero Hour has snow but no rain; OpenWESTWOOD lets any map
/// have Kane's Wrath's (<see cref="WorldView.Raining"/>).
/// </summary>
public sealed unsafe class RainRenderer : IDisposable
{
    const int FloatsPerVertex = 6; // position, uv, alpha
    readonly GL gl;
    readonly ShaderProgram shader;
    readonly uint vao, vbo;
    readonly Texture? texture;
    readonly float boxWidth, boxLength, boxHeight, windStrength;
    readonly (Vector3 At, float Width, float Height, float Speed, float Alpha)[] drops;
    float[] vertices = [];

    const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec2 aUV;
        layout(location = 2) in float aAlpha;
        uniform mat4 uViewProjection;
        out vec2 vUV;
        out float vAlpha;
        void main()
        {
            vUV = aUV;
            vAlpha = aAlpha;
            gl_Position = uViewProjection * vec4(aPosition, 1.0);
        }
        """;

    const string FragmentSource = """
        #version 330 core
        in vec2 vUV;
        in float vAlpha;
        uniform sampler2D uTexture;
        out vec4 fragColor;
        void main()
        {
            vec4 c = texture(uTexture, vUV);
            c.a *= vAlpha;
            if (c.a < 0.01)
                discard;
            fragColor = c;
        }
        """;

    public RainRenderer(GL gl, RuleSet rules, TextureCache textures)
    {
        this.gl = gl;
        var r = rules.Section("Rain");
        float F(string key, float fallback) => RuleValue.Float(r.GetString(key), fallback);
        boxWidth = F("RainBoxWidth", 1000);
        boxLength = F("RainBoxLength", 1000);
        boxHeight = F("RainBoxHeight", 300);
        windStrength = F("WindStrength", 1);
        texture = textures.Get(r.GetString("RainTexture", "kw/FXRainDrop"));
        var random = new Random(1);
        float Between(float a, float b) => a + (float)random.NextDouble() * (b - a);
        drops = new (Vector3, float, float, float, float)[Math.Max(1, (int)F("NumRaindropsPerBox", 1024))];
        for (int i = 0; i < drops.Length; i++)
            drops[i] = (new Vector3(Between(0, boxWidth), Between(0, boxLength), Between(0, boxHeight)),
                Between(F("MinWidth", 1), F("MaxWidth", 4)), Between(F("MinHeight", 25), F("MaxHeight", 100)),
                Between(F("MinSpeed", 250), F("MaxSpeed", 450)), Between(F("MinAlpha", 0.2f), F("MaxAlpha", 0.5f)));

        shader = new ShaderProgram(gl, VertexSource, FragmentSource);
        vao = gl.GenVertexArray();
        vbo = gl.GenBuffer();
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        uint stride = FloatsPerVertex * sizeof(float);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, stride, (void*)(5 * sizeof(float)));
        gl.BindVertexArray(0);
    }

    /// <summary>The drops fall (wrapping to the top of the box).</summary>
    public void Update(float seconds)
    {
        for (int i = 0; i < drops.Length; i++)
        {
            ref var d = ref drops[i];
            d.At.Z -= d.Speed * seconds;
            if (d.At.Z < 0)
                d.At.Z += boxHeight;
        }
    }

    public void Draw(Matrix4x4 viewProjection, Camera camera, Func<float, float, float> groundHeight)
    {
        if (texture == null)
            return;
        // The box repeated over the ground around the view's centre (3 x 3 boxes).
        var centre = camera.Target;
        var right = camera.ScreenRight;
        var wind = new Vector3(0.15f * windStrength, 0.1f * windStrength, 0);
        int needed = drops.Length * 9 * 6 * FloatsPerVertex;
        if (vertices.Length < needed)
            vertices = new float[needed];
        int v = 0;
        float baseX = MathF.Floor(centre.X / boxWidth) * boxWidth - boxWidth, baseY = MathF.Floor(centre.Y / boxLength) * boxLength - boxLength;
        for (int bx = 0; bx < 3; bx++)
            for (int by = 0; by < 3; by++)
                foreach (var d in drops)
                {
                    float x = baseX + bx * boxWidth + d.At.X, y = baseY + by * boxLength + d.At.Y;
                    var bottom = new Vector3(x, y, groundHeight(x, y) + d.At.Z);
                    var top = bottom + (Vector3.UnitZ + wind) * d.Height;
                    var half = right * (d.Width / 2);
                    void Vertex(Vector3 p, float u, float t)
                    {
                        vertices[v++] = p.X; vertices[v++] = p.Y; vertices[v++] = p.Z;
                        vertices[v++] = u; vertices[v++] = t; vertices[v++] = d.Alpha;
                    }
                    Vertex(bottom - half, 0, 1); Vertex(bottom + half, 1, 1); Vertex(top + half, 1, 0);
                    Vertex(bottom - half, 0, 1); Vertex(top + half, 1, 0); Vertex(top - half, 0, 0);
                }
        shader.Use();
        shader.Set("uViewProjection", viewProjection);
        shader.Set("uTexture", 0);
        texture.Bind(0);
        gl.BindVertexArray(vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        fixed (float* p = vertices)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(v * sizeof(float)), p, BufferUsageARB.StreamDraw);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One); // drops catch the light: they brighten what is behind
        gl.Enable(EnableCap.DepthTest);
        gl.DepthFunc(DepthFunction.Lequal);
        gl.DepthMask(false);
        gl.Disable(EnableCap.CullFace);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(v / FloatsPerVertex));
        gl.DepthMask(true);
        gl.DepthFunc(DepthFunction.Less);
        gl.Disable(EnableCap.Blend);
        gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
        shader.Dispose();
    }
}
