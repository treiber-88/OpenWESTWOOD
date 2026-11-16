using System.Numerics;
using OpenWestwood.Client.Graphics;
using OpenWestwood.Client.KanesWrath;
using OpenWestwood.Client.UI;
using OpenWestwood.Client.Platform;
using Silk.NET.OpenGL;

namespace OpenWestwood.Client.Screens;

/// <summary>
/// Debug (--view-apt tacticalhudmaincommandbar,tacticalhudradar [--apt-frame N]): Kane's Wrath interface movies from
/// Apt.big drawn on top of each other, their 1024 x 768 stage fitted to the window; the named placements are outlined.
/// </summary>
public sealed class AptViewerScreen : IScreen
{
    readonly GL gl;
    readonly SpriteBatch batch;
    readonly TextureCache textures;
    readonly List<AptRenderer> movies = new();
    readonly int frame;
    readonly bool outline = Environment.GetEnvironmentVariable("OW_APT_OUTLINE") is { Length: > 0 };
    int width = 1280, height = 720;

    public AptViewerScreen(GL gl, GameContent content, string names, int frame)
    {
        this.gl = gl;
        this.frame = frame;
        batch = new SpriteBatch(gl);
        textures = new TextureCache(gl, content.FileSystem);
        foreach (string name in names.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (KwApt.Load(content, name.Trim()) is { } movie)
            {
                // (Layers the game's scripts keep out of sight: the letter box, the input shield.)
                var hidden = (Environment.GetEnvironmentVariable("OW_APT_HIDE") ?? "letterBoxLayer,inputShield").Split(',');
                movies.Add(new AptRenderer(movie, textures) { Hide = path => hidden.Any(h => path.EndsWith(h, StringComparison.OrdinalIgnoreCase)) });
            }
            else
                Console.Error.WriteLine($"No APT movie {name}");
    }

    public void Resize(int width, int height)
    {
        this.width = width;
        this.height = height;
    }

    public void Update(float seconds) { }

    public void Render(float seconds)
    {
        gl.Viewport(0, 0, (uint)width, (uint)height);
        gl.ClearColor(0.15f, 0.15f, 0.18f, 1);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        batch.Begin(width, height);
        foreach (var movie in movies)
        {
            var stage = new Vector2(Math.Max(1, movie.Movie.Movie.Width), Math.Max(1, movie.Movie.Movie.Height));
            float scale = Math.Min(width / stage.X, height / stage.Y);
            var m = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation((width - stage.X * scale) / 2, (height - stage.Y * scale) / 2);
            movie.Draw(batch, m, frame);
            if (outline)
                foreach (var (path, (transform, _)) in movie.Placements)
                {
                    var at = Vector2.Transform(Vector2.Zero, transform);
                    batch.DrawRect(new Rect(at.X - 2, at.Y - 2, 4, 4), new Vector4(1, 1, 0, 1));
                }
        }
        batch.End();
    }

    public void Dispose()
    {
        batch.Dispose();
        textures.Dispose();
    }
}
