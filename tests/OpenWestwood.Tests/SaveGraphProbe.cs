using System.Reflection;
using OpenWestwood.Formats.Maps;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Core;
using Xunit;
using Xunit.Abstractions;

namespace OpenWestwood.Tests;

/// <summary>Probe (run by hand with SAVE_PROBE=1): what a running game's object graph holds, by type.</summary>
public class SaveGraphProbe(BaseGameFixture content, ITestOutputHelper output) : IClassFixture<BaseGameFixture>
{
    [Fact]
    public void WhatTheGameHolds()
    {
        if (!content.Available || Environment.GetEnvironmentVariable("SAVE_PROBE") != "1")
            return;
        var map = MapFile.Read(content.FileSystem!.ReadAllBytes("Maps/Tournament Desert/Tournament Desert.map"));
        var game = GameSetup.FromMap(map, content.Rules!, seed: 7,
        [
            new PlayerSlot("Alice", "FactionAmerica", IsHuman: false, StartPosition: 1, Color: 0x0000FF),
            new PlayerSlot("Bob", "FactionChina", IsHuman: false, StartPosition: 2, Color: 0xFF0000),
        ], models: content.Models, readFile: p => content.FileSystem!.Exists(p) ? content.FileSystem.ReadAllBytes(p) : null);
        for (int i = 0; i < 60 * LogicTime.FramesPerSecond; i++)
            game.Update();

        var stop = new HashSet<Type> { typeof(OpenWestwood.Core.Rules.RuleNode), typeof(OpenWestwood.Core.Rules.RuleSet),
            typeof(OpenWestwood.Logic.Templates.ObjectTemplate), typeof(OpenWestwood.Logic.Templates.TemplateStore),
            typeof(OpenWestwood.Logic.Templates.RulesCatalog) };
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var counts = new Dictionary<string, int>();
        var stopRefs = new Dictionary<string, int>();
        var delegates = new Dictionary<string, int>();
        var stack = new Stack<(object Obj, string Via)>();
        stack.Push((game, "game"));
        while (stack.Count > 0)
        {
            var (obj, via) = stack.Pop();
            var type = obj.GetType();
            if (type.IsPrimitive || obj is string || type.IsEnum || obj is Type || obj is MemberInfo)
                continue;
            if (stop.Contains(type) || type.Namespace?.StartsWith("OpenWestwood.Formats") == true)
            {
                string k = type.Name + " via " + via;
                stopRefs[k] = stopRefs.GetValueOrDefault(k) + 1;
                continue;
            }
            if (!type.IsValueType && !seen.Add(obj))
                continue;
            if (obj is Delegate d)
            {
                string k = $"{d.Method.DeclaringType?.Name}.{d.Method.Name} via {via}";
                delegates[k] = delegates.GetValueOrDefault(k) + 1;
                if (d.Target != null)
                    stack.Push((d.Target, "delegate " + via));
                continue;
            }
            string name = type.IsGenericType ? type.Name + "<" + string.Join(",", type.GetGenericArguments().Select(a => a.Name)) + ">" : type.Name;
            counts[name] = counts.GetValueOrDefault(name) + 1;
            if (obj is Array array)
            {
                if (!type.GetElementType()!.IsPrimitive)
                    foreach (var e in array)
                        if (e != null)
                            stack.Push((e, name));
                continue;
            }
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (f.GetValue(obj) is { } v && !f.FieldType.IsPrimitive)
                        stack.Push((v, $"{t.Name}.{f.Name}"));
        }
        output.WriteLine($"{seen.Count} objects");
        foreach (var (k, v) in counts.OrderByDescending(kv => kv.Value).Take(120))
            output.WriteLine($"{v,7} {k}");
        output.WriteLine("--- shared (by reference) ---");
        foreach (var (k, v) in stopRefs.OrderByDescending(kv => kv.Value).Take(60))
            output.WriteLine($"{v,7} {k}");
        output.WriteLine("--- delegates ---");
        foreach (var (k, v) in delegates.OrderByDescending(kv => kv.Value).Take(80))
            output.WriteLine($"{v,7} {k}");
    }
}
