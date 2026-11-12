using System.Numerics;
using OpenWestwood.Client.Graphics;
using OpenWestwood.Core.Rules;

namespace OpenWestwood.Client.UI;

/// <summary>A named rectangle of a texture page (the original "MappedImage"), e.g. a button or cameo.</summary>
public sealed record MappedImage(string Name, string TextureName, float U0, float V0, float U1, float V1, float PixelWidth, float PixelHeight, bool Rotated);

/// <summary>
/// All interface images: the original mapped images (rules/ui/mapped-images) plus any image that is
/// simply a texture file of that name (so modders and artists can add new cameos as single files).
/// </summary>
public sealed class ImageCatalog
{
    readonly Dictionary<string, MappedImage> images = new(StringComparer.OrdinalIgnoreCase);
    readonly TextureCache textures;

    public ImageCatalog(RuleSet rules, TextureCache textures)
    {
        this.textures = textures;
        foreach (var (name, node) in rules.Section("MappedImages").Entries)
        {
            float textureWidth = RuleValue.Float(node.GetString("TextureWidth"), 512);
            float textureHeight = RuleValue.Float(node.GetString("TextureHeight"), 512);
            var coords = ParseCoords(node.GetString("Coords"));
            bool rotated = node.GetString("Status").Contains("ROTATED_90_CLOCKWISE", StringComparison.OrdinalIgnoreCase);
            images[name] = new MappedImage(name, node.GetString("Texture"),
                coords.Left / textureWidth, coords.Top / textureHeight, coords.Right / textureWidth, coords.Bottom / textureHeight,
                coords.Right - coords.Left, coords.Bottom - coords.Top, rotated);
        }
    }

    static (float Left, float Top, float Right, float Bottom) ParseCoords(string text)
    {
        float left = 0, top = 0, right = 0, bottom = 0;
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"(Left|Top|Right|Bottom)\s*:\s*(-?\d+)"))
        {
            float value = float.Parse(match.Groups[2].Value);
            switch (match.Groups[1].Value)
            {
                case "Left": left = value; break;
                case "Top": top = value; break;
                case "Right": right = value; break;
                case "Bottom": bottom = value; break;
            }
        }
        return (left, top, right, bottom);
    }

    public int Count => images.Count;

    public MappedImage? Find(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Equals("NoImage", StringComparison.OrdinalIgnoreCase))
            return null;
        if (images.TryGetValue(name, out var image))
            return image;

        // OpenWESTWOOD: "name#u0,v0,u1,v1" is part of an image (fractions of it: a logo out of a wider panel).
        if (name.LastIndexOf('#') is int hash and > 0 && name[(hash + 1)..].Split(',') is { Length: 4 } part
            && part.All(p => float.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            && Find(name[..hash]) is { Rotated: false } whole)
        {
            var f = part.Select(p => float.Parse(p, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            float du = whole.U1 - whole.U0, dv = whole.V1 - whole.V0;
            return images[name] = new MappedImage(name, whole.TextureName, whole.U0 + du * f[0], whole.V0 + dv * f[1], whole.U0 + du * f[2], whole.V0 + dv * f[3],
                whole.PixelWidth * (f[2] - f[0]), whole.PixelHeight * (f[3] - f[1]), false);
        }

        // OpenWESTWOOD: "TDCAMEO:NAME" is a Tiberian Dawn sidebar cameo, from the original TD files.
        if (name.StartsWith(TiberianDawn.TdCameos.Prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (textures.GetOrCreate(name, () => TiberianDawn.TdCameos.Image(name[TiberianDawn.TdCameos.Prefix.Length..])) is not { } cameo)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, cameo.Width, cameo.Height, false);
        }

        // OpenWESTWOOD: "TSCAMEO:TYPE" is a Tiberian Sun / Firestorm build icon (its art's Cameo).
        if (name.StartsWith("TSCAMEO:", StringComparison.OrdinalIgnoreCase))
        {
            if (textures.GetOrCreate(name, () => RedAlert2.Ra2Art.GetTiberianSun()?.Cameo(name["TSCAMEO:".Length..])) is not { } tsCameo)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, tsCameo.Width, tsCameo.Height, false);
        }

        // OpenWESTWOOD: "RA2CAMEO:TYPE" is a Red Alert 2 / Yuri's Revenge build icon (its art's Cameo).
        if (name.StartsWith("RA2CAMEO:", StringComparison.OrdinalIgnoreCase))
        {
            if (textures.GetOrCreate(name, () => RedAlert2.Ra2Art.Get()?.Cameo(name["RA2CAMEO:".Length..])) is not { } ra2Cameo)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, ra2Cameo.Width, ra2Cameo.Height, false);
        }

        // OpenWESTWOOD: "WWSIDE:TS|RA2:archive:SHAPE:frame" is a frame of a Tiberian Sun / Red Alert 2 sidebar picture from
        // that side's sidebar archive (GDI's SIDEC01.MIX, Nod's SIDEC02.MIX ...), in its own SIDEBAR.PAL (FactionEmblems).
        if (name.StartsWith("WWSIDE:", StringComparison.OrdinalIgnoreCase) && name.Split(':') is [_, var sideGame, var sideMix, var sideShape, var sideFrame]
            && int.TryParse(sideFrame, out int sideFrameNumber))
        {
            if (textures.GetOrCreate(name, () => FactionEmblems.SidebarShape(sideGame, sideMix, sideShape, sideFrameNumber)) is not { } sidePicture)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, sidePicture.Width, sidePicture.Height, false);
        }

        // OpenWESTWOOD: "TDLOGO:frame" and "TSEMBLEM:SIDEC0n" are Tiberian Dawn's and Tiberian Sun's GDI / Nod emblems (FactionEmblems).
        if (name.StartsWith("TDLOGO:", StringComparison.OrdinalIgnoreCase) || name.StartsWith("TSEMBLEM:", StringComparison.OrdinalIgnoreCase))
        {
            string arg = name[(name.IndexOf(':') + 1)..];
            if (textures.GetOrCreate(name, () => name.StartsWith("TD", StringComparison.OrdinalIgnoreCase)
                    ? (int.TryParse(arg, out int logo) ? FactionEmblems.TdLogo(logo) : null)
                    : FactionEmblems.TsEmblem(arg)) is not { } emblem)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, emblem.Width, emblem.Height, false);
        }

        // OpenWESTWOOD: "RA2PCX:NAME" is a Red Alert 2 / Yuri's Revenge interface picture (NAME.PCX: the countries' flags,
        // USAI, RUSI...), its magenta see-through.
        if (name.StartsWith("RA2PCX:", StringComparison.OrdinalIgnoreCase))
        {
            string file = name["RA2PCX:".Length..] + ".PCX";
            if (textures.GetOrCreate(name, () => RedAlert2.Ra2Art.Get() is { } art && art.Content.Files.Exists(file)
                    ? OpenWestwood.Formats.Images.PcxDecoder.Decode(art.Content.Files.ReadAllBytes(file), (255, 0, 255)) : null) is not { } pcx)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, pcx.Width, pcx.Height, false);
        }

        // OpenWESTWOOD: "RACAMEO:NAME" is a Red Alert build icon (NAMEICON.SHP), from the original RA files.
        if (name.StartsWith("RACAMEO:", StringComparison.OrdinalIgnoreCase))
        {
            if (textures.GetOrCreate(name, () => RedAlertContent.Art is { } raArt && raArt.Cameo(name["RACAMEO:".Length..]) is { } raShape
                    ? raArt.Frame(raShape, 0, null) : null) is not { } raCameo)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, raCameo.Width, raCameo.Height, false);
        }

        // OpenWESTWOOD: "KWCAMEO:Portrait_X" is a Kane's Wrath portrait (a packed image on one of its texture pages).
        if (name.StartsWith("KWCAMEO:", StringComparison.OrdinalIgnoreCase))
        {
            var kw = textures.FileSystem.Packages.OfType<OpenWestwood.Formats.Sage.SageArtPackage>().FirstOrDefault();
            if (kw?.PackedImage(name["KWCAMEO:".Length..]) is not { } packed)
                return null;
            return images[name] = new MappedImage(name, packed.Texture, packed.Left / (float)packed.Width, packed.Top / (float)packed.Height,
                packed.Right / (float)packed.Width, packed.Bottom / (float)packed.Height, packed.Right - packed.Left, packed.Bottom - packed.Top, false);
        }

        // OpenWESTWOOD: "EMCAMEO:AT_Inf.tga" is an Emperor: Battle for Dune build icon (its Textures/ picture).
        if (name.StartsWith("EMCAMEO:", StringComparison.OrdinalIgnoreCase))
        {
            string path = OpenWestwood.Formats.Emperor.EmperorArtPackage.Folder + "Textures/" + name["EMCAMEO:".Length..];
            if (textures.GetOrCreate(name, () => textures.FileSystem.Exists(path)
                    ? OpenWestwood.Formats.Images.Image.Load(textures.FileSystem.ReadAllBytes(path), path) : null) is not { } emCameo)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, emCameo.Width, emCameo.Height, false);
        }

        // OpenWESTWOOD: "D2KCAMEO:image" is a Dune 2000 build icon (the image's "icon" sequence).
        if (name.StartsWith("D2KCAMEO:", StringComparison.OrdinalIgnoreCase))
        {
            if (textures.GetOrCreate(name, () => Dune2000.D2kArt.Get()?.Cameo(name["D2KCAMEO:".Length..])) is not { } d2kCameo)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, d2kCameo.Width, d2kCameo.Height, false);
        }

        // OpenWESTWOOD: "D2KCHROME:x,y,w,h" is a region of Dune 2000's interface art; "TDSHAPE:NAME:frame" a frame of a TD shape.
        if (name.StartsWith("D2KCHROME:", StringComparison.OrdinalIgnoreCase)
            && name["D2KCHROME:".Length..].Split(',') is [var cx, var cy, var cw, var ch])
        {
            if (textures.GetOrCreate(name, () => Dune2000.D2kArt.Get()?.Chrome(int.Parse(cx), int.Parse(cy), int.Parse(cw), int.Parse(ch))) is not { } region)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, region.Width, region.Height, false);
        }
        if ((name.StartsWith("TDSHAPE:", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RASHAPE:", StringComparison.OrdinalIgnoreCase))
            && name[8..].Split(':') is [var shapeName, var frameText])
        {
            // "RASHAPE:" is the same from Red Alert's files.
            var shapeArt = name.StartsWith("RA", StringComparison.OrdinalIgnoreCase) ? RedAlertContent.Art : OpenWestwood.Client.TiberianDawnContent.Art;
            if (textures.GetOrCreate(name, () => shapeArt is { } art && art.Shape(shapeName) is { } shape
                    ? art.Frame(shape, Math.Clamp(int.Parse(frameText), 0, shape.FrameCount - 1), null) : null) is not { } tdShape)
                return null;
            return images[name] = new MappedImage(name, name, 0, 0, 1, 1, tdShape.Width, tdShape.Height, false);
        }

        // A plain texture file with this name works as a whole-image "mapped image".
        if (textures.Get(name + ".tga", repeat: false) is { } texture)
        {
            image = new MappedImage(name, name + ".tga", 0, 0, 1, 1, texture.Width, texture.Height, false);
            images[name] = image;
            return image;
        }
        return null;
    }

    public void Draw(SpriteBatch batch, MappedImage image, Rect destination, Vector4? color = null)
    {
        var texture = textures.Get(image.TextureName, repeat: false);
        if (texture == null)
            return;
        if (image.Rotated)
        {
            // Stored turned 90 degrees clockwise in the page, so the image's top-left is the page's top-right.
            batch.DrawTextureCorners(texture, destination,
                new Vector2(image.U1, image.V0), new Vector2(image.U1, image.V1),
                new Vector2(image.U0, image.V1), new Vector2(image.U0, image.V0), color ?? Vector4.One);
            return;
        }
        batch.DrawTexture(texture, destination, image.U0, image.V0, image.U1, image.V1, color ?? Vector4.One);
    }

    public void Draw(SpriteBatch batch, string? name, Rect destination, Vector4? color = null)
    {
        if (Find(name) is { } image)
            Draw(batch, image, destination, color);
    }
}
