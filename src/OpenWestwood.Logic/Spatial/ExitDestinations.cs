using System.Numerics;
using OpenWestwood.Logic.Core;
using OpenWestwood.Logic.Locomotion;
using OpenWestwood.Logic.Objects;

namespace OpenWestwood.Logic.Spatial;

/// <summary>
/// Where a unit leaving a building or transport goes to stand (the original's adjustToPossibleDestination with
/// updateGoal): the wanted spot if it is free, else the nearest free one around it. A spot is taken by an idle
/// unit standing there or by the goal another leaving unit was just given, so a squad getting out spreads into
/// a group instead of piling onto one point.
/// </summary>
public static class ExitDestinations
{
    const int GoalFrames = 10 * LogicTime.FramesPerSecond;
    const int MaxRings = 30;

    public static Vector3 Adjust(GameLogic game, GameObject unit, Surfaces surfaces, Vector3 wanted)
    {
        var goals = game.ExitGoals;
        var until = game.ExitGoalsUntil;
        foreach (uint id in until.Where(e => e.Value <= game.Frame || game.FindObject(e.Key) is not { IsEffectivelyDead: false }).Select(e => e.Key).ToList())
        {
            goals.Remove(id);
            until.Remove(id);
        }
        goals.Remove(unit.Id);
        until.Remove(unit.Id);

        float spacing = Math.Max(8, unit.BoundingCircleRadius * 2 + 2);
        bool Free(Vector3 p)
        {
            if (!game.Terrain.IsInsidePlayableArea(p.X, p.Y) || !game.Pathfinder.IsValidMovementTerrain(p, surfaces))
                return false;
            foreach (var goal in goals.Values)
                if (MathUtil.DistanceSquared2D(goal, p) < spacing * spacing)
                    return false;
            foreach (var other in game.ObjectsInRadius(p, spacing + 50))
                if (other != unit && !other.IsEffectivelyDead && !other.IsKindOf(Kind.Structure) && other.AI is { IsIdle: true }
                    && !other.IsContained && MathUtil.Distance2D(other.Position, p) < spacing / 2 + other.BoundingCircleRadius)
                    return false;
            return true;
        }

        var chosen = wanted;
        if (!Free(wanted))
        {
            chosen = Game_NearestPassable(game, wanted, surfaces);
            bool found = false;
            for (int ring = 1; ring <= MaxRings && !found; ring++)
            {
                float radius = ring * spacing;
                int samples = Math.Max(6, (int)(MathF.Tau * radius / spacing));
                for (int i = 0; i < samples; i++)
                {
                    // Alternating either side of "straight on", as the original's searches do.
                    float angle = (i % 2 == 0 ? 1 : -1) * ((i + 1) / 2) * MathF.Tau / samples;
                    var p = wanted + new Vector3(DMath.Cos(angle) * radius, DMath.Sin(angle) * radius, 0);
                    if (Free(p))
                    {
                        chosen = p;
                        found = true;
                        break;
                    }
                }
            }
        }
        chosen.Z = game.Terrain.LayerHeight(chosen.X, chosen.Y);
        goals[unit.Id] = chosen;
        until[unit.Id] = game.Frame + GoalFrames;
        return chosen;
    }

    static Vector3 Game_NearestPassable(GameLogic game, Vector3 wanted, Surfaces surfaces) =>
        game.Pathfinder.NearestPassable(wanted, surfaces) ?? wanted;
}
