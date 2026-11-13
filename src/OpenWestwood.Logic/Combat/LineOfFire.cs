using System.Numerics;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Logic.Combat;

/// <summary>
/// OpenWESTWOOD (Sole Survivor Remix): what stands between a shooter and what it shoots at. Buildings, trees, rocks, walls
/// and other things fixed to the ground stop shots: an instant shot ends on the first one in its way and hurts it instead,
/// a flying one goes off on touching one. Something behind one is safe from shots that can't arc over it.
/// </summary>
public static class LineOfFire
{
    /// <summary>Whether shots stop on obstacles in this game (only in Sole Survivor Remix's arenas).</summary>
    public static bool Applies(GameLogic game) => game.SoleSurvivor != null;

    /// <summary>Where a unit's shots leave it from: a little way up it (as the client draws them).</summary>
    public static Vector3 Muzzle(GameObject shooter) => shooter.Position + new Vector3(0, 0, Math.Max(6, shooter.Height * 0.7f));

    /// <summary>The point a shot at an object goes for: its middle.</summary>
    public static Vector3 AimPoint(GameObject target) =>
        target.Position + new Vector3(0, 0, target.Geometry.Type == GeometryType.Sphere ? 0 : target.Geometry.Height * 0.5f);

    /// <summary>
    /// Something fixed to the ground and tall enough to stand behind: a building, a tree, a rock, a wall. Not resources,
    /// bridges, mines, crates, flags or anything already dying.
    /// </summary>
    public static bool IsObstacle(GameObject o)
    {
        if (o.IsEffectivelyDead || o.IsDestroyed || o.IsContained || o.IsMobile || o.Geometry.MaxHeightAbovePosition < 8)
            return false;
        if (!(o.IsKindOf(Kind.Structure) || o.IsKindOf(Kind.Immobile) || o.IsKindOf("SHRUBBERY")))
            return false;
        return !(o.IsKindOf("SUPPLY_SOURCE") || o.IsKindOf("BRIDGE") || o.IsKindOf("BRIDGE_TOWER") || o.IsKindOf("MINE")
            || o.IsKindOf("CRATE") || o.IsKindOf("CAPTURABLE") || o.IsKindOf("PROJECTILE") || o.IsKindOf("NO_COLLIDE") && !o.IsKindOf("SHRUBBERY")
            || o.IsKindOf("INERT") || o.IsKindOf("CLEARED_BY_BUILD"));
    }

    /// <summary>
    /// The first obstacle a straight shot from <paramref name="from"/> to <paramref name="to"/> runs into, and where; null
    /// when the way is clear. The shooter, the target, and anything the shot starts inside of (a soldier among trees, a
    /// unit firing out of a building) never count.
    /// </summary>
    public static (GameObject Obstacle, Vector3 Point)? FirstObstacle(GameLogic game, Vector3 from, Vector3 to, GameObject? shooter, GameObject? target)
    {
        var line = to - from;
        float length2D = new Vector2(line.X, line.Y).Length();
        if (length2D < 1)
            return null;
        var middle = (from + to) * 0.5f;
        GameObject? best = null;
        float bestT = 1;
        foreach (var o in game.Partition.Query(middle, length2D * 0.5f + 10))
        {
            if (o == shooter || o == target || !IsObstacle(o) || shooter != null && shooter.ContainedById == o.Id)
                continue;
            if (Entry(o, from, line) is { } t && t < bestT)
            {
                bestT = t;
                best = o;
            }
        }
        return best == null ? null : (best, from + line * bestT);
    }

    /// <summary>
    /// Where (0..1 along the line) the line first enters the object's shape, or null if it misses it or starts inside it.
    /// </summary>
    static float? Entry(GameObject o, Vector3 from, Vector3 line)
    {
        var g = o.Geometry;
        float bottom = o.Position.Z - g.MaxHeightBelowPosition, top = o.Position.Z + g.MaxHeightAbovePosition;
        // The footprint, in the object's own frame (a box's long side along its facing).
        float c = DMath.Cos(-o.Orientation), s = DMath.Sin(-o.Orientation);
        float px = from.X - o.Position.X, py = from.Y - o.Position.Y;
        var p = new Vector2(px * c - py * s, px * s + py * c);
        var d = new Vector2(line.X * c - line.Y * s, line.X * s + line.Y * c);
        float t0, t1;
        if (g.Type == GeometryType.Box)
        {
            if (!Slab(p.X, d.X, g.MajorRadius, out float ax, out float bx) || !Slab(p.Y, d.Y, g.MinorRadius, out float ay, out float by))
                return null;
            t0 = Math.Max(ax, ay);
            t1 = Math.Min(bx, by);
        }
        else
        {
            float r = g.MajorRadius;
            float a = Vector2.Dot(d, d), b = 2 * Vector2.Dot(p, d), cc = Vector2.Dot(p, p) - r * r;
            if (cc <= 0)
                return null; // starts inside its footprint
            float disc = b * b - 4 * a * cc;
            if (a <= 0 || disc < 0)
                return null;
            float root = MathF.Sqrt(disc);
            t0 = (-b - root) / (2 * a);
            t1 = (-b + root) / (2 * a);
        }
        if (t0 <= 0 && g.Type == GeometryType.Box)
            return null; // starts inside its footprint
        // Heights: the part of the line between the object's bottom and top.
        float z0 = from.Z, dz = line.Z, h0, h1;
        if (MathF.Abs(dz) < 1e-5f)
        {
            if (z0 < bottom || z0 > top)
                return null;
            h0 = float.MinValue;
            h1 = float.MaxValue;
        }
        else
        {
            float ta = (bottom - z0) / dz, tb = (top - z0) / dz;
            h0 = Math.Min(ta, tb);
            h1 = Math.Max(ta, tb);
        }
        float enter = Math.Max(Math.Max(t0, h0), 0), leave = Math.Min(Math.Min(t1, h1), 1);
        return enter <= leave ? enter : null;
    }

    /// <summary>The part of a line (start p, step d) inside -extent..extent on one axis.</summary>
    static bool Slab(float p, float d, float extent, out float t0, out float t1)
    {
        if (MathF.Abs(d) < 1e-6f)
        {
            t0 = float.MinValue;
            t1 = float.MaxValue;
            return MathF.Abs(p) <= extent;
        }
        float a = (-extent - p) / d, b = (extent - p) / d;
        t0 = Math.Min(a, b);
        t1 = Math.Max(a, b);
        return t0 <= t1;
    }

    /// <summary>
    /// Whether a flying shot touching <paramref name="thing"/> goes off on it as an obstacle: unless its launcher stands
    /// in or against it (a soldier firing from among trees, a unit firing out of a building).
    /// </summary>
    public static bool StopsProjectile(GameObject? launcher, GameObject thing)
    {
        if (!Applies(thing.Game) || !IsObstacle(thing))
            return false;
        if (launcher == null)
            return true;
        if (launcher.ContainedById == thing.Id)
            return false;
        return MathUtil.Distance2D(launcher.Position, thing.Position) > thing.Radius + launcher.Radius + 4;
    }
}
