using OpenWestwood.Core.Installs;
using OpenWestwood.Logic.Multiplayer;

namespace OpenWestwood.Client.Network;

/// <summary>
/// A network game's lobby as the host keeps it and sends it round: the skirmish settings (<see cref="GameInfo"/>), which
/// machine sits in each slot, who has accepted, and which of the games that must be installed every player has (only
/// factions and maps all of them can load are offered).
/// </summary>
public sealed class NetLobby
{
    public GameInfo Info { get; set; } = new();

    /// <summary>The machine (client id) in each slot; -1 for an open, closed or computer slot.</summary>
    public int[] SlotClient { get; } = Enumerable.Repeat(-1, GameInfo.MaxSlots).ToArray();

    /// <summary>Whether each slot's player has accepted the settings (the host always has).</summary>
    public bool[] Accepted { get; } = new bool[GameInfo.MaxSlots];

    public string GameName { get; set; } = "";

    /// <summary>Whether every player has Zero Hour / Kane's Wrath installed.</summary>
    public bool AllHaveZeroHour { get; set; } = true;
    public bool AllHaveKanesWrath { get; set; } = true;

    public bool AllHave(OriginalGame game) => game switch
    {
        OriginalGame.ZeroHour => AllHaveZeroHour,
        OriginalGame.KanesWrath => AllHaveKanesWrath,
        _ => true,
    };

    /// <summary>The slot a machine sits in (-1: none).</summary>
    public int SlotOf(int clientId) => Array.IndexOf(SlotClient, clientId);

    /// <summary>The original game a map needs: Zero Hour's own maps (no prefix), Kane's Wrath's ("kw:"); else none.</summary>
    public static OriginalGame? MapNeeds(string map)
    {
        if (map.StartsWith("kw:", StringComparison.OrdinalIgnoreCase))
            return OriginalGame.KanesWrath;
        int colon = map.IndexOf(':');
        return colon is > 0 and < 5 ? null : OriginalGame.ZeroHour;
    }

    public bool IsMapAllowed(string map) => MapNeeds(map) is not { } game || AllHave(game);

    public bool IsFactionAllowed(Core.Rules.RuleNode template) => GameInfo.RequiredGame(template) is not { } game || AllHave(game);

    public void Write(BinaryWriter w)
    {
        w.Write(GameName);
        w.Write(AllHaveZeroHour);
        w.Write(AllHaveKanesWrath);
        WriteInfo(w, Info);
        for (int i = 0; i < GameInfo.MaxSlots; i++)
        {
            w.Write((sbyte)SlotClient[i]);
            w.Write(Accepted[i]);
        }
    }

    public static NetLobby Read(BinaryReader r)
    {
        var lobby = new NetLobby
        {
            GameName = r.ReadString(),
            AllHaveZeroHour = r.ReadBoolean(),
            AllHaveKanesWrath = r.ReadBoolean(),
        };
        lobby.Info = ReadInfo(r);
        for (int i = 0; i < GameInfo.MaxSlots; i++)
        {
            lobby.SlotClient[i] = r.ReadSByte();
            lobby.Accepted[i] = r.ReadBoolean();
        }
        return lobby;
    }

    public static void WriteInfo(BinaryWriter w, GameInfo info)
    {
        w.Write(info.Map);
        w.Write(info.StartingCash);
        w.Write(info.SuperweaponRestriction);
        w.Write(info.ExploredMap);
        w.Write(info.NoFogOfWar);
        w.Write((byte)info.GameSpeed);
        w.Write(info.Seed);
        foreach (var slot in info.Slots)
        {
            w.Write((byte)slot.State);
            w.Write(slot.Name);
            w.Write((short)slot.Color);
            w.Write((short)slot.PlayerTemplate);
            w.Write((sbyte)slot.StartPos);
            w.Write((sbyte)slot.TeamNumber);
        }
    }

    public static GameInfo ReadInfo(BinaryReader r)
    {
        var info = new GameInfo
        {
            Map = r.ReadString(),
            StartingCash = r.ReadInt32(),
            SuperweaponRestriction = r.ReadBoolean(),
            ExploredMap = r.ReadBoolean(),
            NoFogOfWar = r.ReadBoolean(),
            GameSpeed = r.ReadByte(),
            Seed = r.ReadUInt64(),
        };
        foreach (var slot in info.Slots)
        {
            var state = (SlotState)r.ReadByte();
            string name = r.ReadString();
            slot.SetState(state, name);
            slot.Name = name;
            slot.Color = r.ReadInt16();
            slot.PlayerTemplate = r.ReadInt16();
            slot.StartPos = r.ReadSByte();
            slot.TeamNumber = r.ReadSByte();
        }
        return info;
    }

    public NetLobby Clone()
    {
        var copy = Read(NetConnection.Unpack(NetConnection.Pack(Write)));
        return copy;
    }
}
