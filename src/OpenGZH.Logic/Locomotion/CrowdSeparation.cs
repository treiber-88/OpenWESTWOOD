using System.Numerics;
using OpenGZH.Logic.Core;
using OpenGZH.Logic.Objects;

namespace OpenGZH.Logic.Locomotion;

/// <summary>
/// OpenGZH: ground units don't stand inside one another. The original keeps them apart through the pathfinder's
/// per-cell unit occupancy; here, each frame, two ground units overlapping by more than a little are eased apart
/// (a share of the overlap each frame, the lighter or the one standing still giving way more), never onto ground the
/// unit can't stand on. Units are visited in id order, so every machine does the same.
/// </summary>
public static class CrowdSeparation
{
    const float CellSize = 40f;
    /// <summary>How close two units may stand, as a share of their radii together.</summary>
    const float Tolerance = 0.7f;
    /// <summary>The share of the overlap resolved each frame (gentle: a crowd settles over a few frames).</summary>
    const float Strength = 0.25f;

    public static void Update(GameLogic game)
    {
        var units = new List<GameObject>();
        foreach (var obj in game.Objects)
            if (Separates(obj))
                units.Add(obj);
        if (units.Count < 2)
            return;
        units.Sort((a, b) => a.Id.CompareTo(b.Id));
        var grid = new Dictionary<(int, int), List<GameObject>>();
        foreach (var u in units)
        {
            var key = ((int)MathF.Floor(u.Position.X / CellSize), (int)MathF.Floor(u.Position.Y / CellSize));
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = new();
            list.Add(u);
        }
        foreach (var a in units)
        {
            int cx = (int)MathF.Floor(a.Position.X / CellSize), cy = (int)MathF.Floor(a.Position.Y / CellSize);
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy), out var near))
                        continue;
                    foreach (var b in near)
                    {
                        if (b.Id <= a.Id)
                            continue;
                        Push(game, a, b);
                    }
                }
        }
    }

    static bool Separates(GameObject o) =>
        o.IsMobile && !o.IsEffectivelyDead && !o.IsContained && !o.DrawableHidden && !o.IsHiddenInContainer && !o.IsKindOf(Kind.Aircraft) && !o.IsKindOf(Kind.Structure)
        && o.AI is { } ai && o.HeightAboveTerrain < 5 && !o.IsDisabledBy("DISABLED_HELD") && ai.CurrentLocomotor != null;

    static void Push(GameLogic game, GameObject a, GameObject b)
    {
        var d = (b.Position - a.Position) with { Z = 0 };
        float want = (a.Radius + b.Radius) * Tolerance;
        float distSq = d.LengthSquared();
        if (distSq >= want * want)
            return;
        float dist = MathF.Sqrt(distSq);
        // On the very same spot: apart along a direction fixed by their ids.
        var dir = dist > 0.001f ? d / dist : new Vector3(MathF.Cos(a.Id * 2.399f + b.Id), MathF.Sin(a.Id * 2.399f + b.Id), 0);
        float overlap = (want - dist) * Strength;
        // The one standing still, and the lighter one (infantry against a tank), gives way more.
        float wa = Weight(a), wb = Weight(b);
        float shareA = wb / (wa + wb), shareB = wa / (wa + wb);
        Move(game, a, -dir * overlap * shareA);
        Move(game, b, dir * overlap * shareB);
    }

    static float Weight(GameObject o) =>
        (o.IsKindOf(Kind.Infantry) ? 1f : 3f) * (o.AI is { IsWalking: true } ? 2f : 1f);

    static void Move(GameLogic game, GameObject o, Vector3 by)
    {
        if (by.LengthSquared() < 1e-6f)
            return;
        var to = o.Position + by;
        if (!game.Pathfinder.IsPassable(to, o.AI!.Surfaces))
            return;
        o.SetPosition(to with { Z = game.Terrain.GroundHeight(to) });
    }
}
