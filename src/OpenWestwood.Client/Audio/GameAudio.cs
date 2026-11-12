using System.Numerics;
using OpenWestwood.Client.World;
using OpenWestwood.Core.Rules;
using OpenWestwood.Logic;
using OpenWestwood.Logic.Players;
using OpenWestwood.Logic.Vision;

namespace OpenWestwood.Client.Audio;

/// <summary>
/// Connects a running game to the audio: plays the simulation's sounds, FX sounds and EVA
/// announcements for the local player, keeps the microphone with the camera, and tells the audio
/// system where objects are and who owns them.
/// </summary>
public sealed class GameAudio
{
    readonly AudioManager audio;
    readonly GameLogic game;
    readonly WorldView world;
    readonly Eva eva;
    int lastFrame = -1;
    /// <summary>A faction's own soundtrack (its PlayerTemplate's GameMusic: Kane's Wrath), played in turn instead of the map's script music.</summary>
    readonly List<string> playlist = new();
    int playlistAt;

    public GameAudio(AudioManager audio, GameLogic game, WorldView world, Player? localPlayer)
    {
        Logic.Scripting.ScriptConditions.MusicTrackCompleted = audio.HasMusicTrackCompleted;
        this.audio = audio;
        this.game = game;
        this.world = world;
        eva = new Eva(audio, game.Catalog.Rules.Section("EvaEvents"), game.Catalog.Rules.Section("SideEvaSounds"));
        if (localPlayer?.Faction?.GetString("GameMusic") is { Length: > 0 } music)
        {
            playlist.AddRange(RuleValue.Words(music).Where(t => audio.FindInfo(t) != null));
            Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(playlist));
        }
        audio.LocalPlayerIndex = localPlayer?.Index ?? -1;
        audio.ObjectPosition = id => game.FindObject(id) is { IsDestroyed: false } o ? o.Position : null;
        audio.ObjectOwnerIndex = id => game.FindObject(id)?.Owner.Index;
        audio.Relationship = (owner, local) =>
        {
            if (owner == local)
                return AudioRelationship.Self;
            var a = Find(owner);
            var b = Find(local);
            if (a == null || b == null)
                return AudioRelationship.Neutral;
            return a.RelationshipTo(b) switch
            {
                Relationship.Allies => AudioRelationship.Ally,
                Relationship.Enemies => AudioRelationship.Enemy,
                _ => AudioRelationship.Neutral,
            };
        };
        audio.IsPointShrouded = p => world.Viewer is { } viewer && game.Shroud.StatusAt(viewer, p) != CellShroud.Clear;
        audio.IsOnScreen = p => world.ToScreen(p, world.ViewportSize) is { } s && s.X >= 0 && s.Y >= 0 && s.X <= world.ViewportSize.X && s.Y <= world.ViewportSize.Y;

        game.Events.OnSound += r =>
            audio.AddAudioEvent(r.Name, r.Source == null ? r.Position : null, r.Source?.Id ?? 0, r.Player?.Index ?? r.Source?.Owner.Index ?? -1);
        game.Events.OnHandledSound += (handle, name, source) =>
            handledSounds[handle] = audio.AddAudioEvent(name, null, source.Id, source.Owner.Index);
        game.Events.OnStopHandledSound += handle =>
        {
            if (handledSounds.Remove(handle, out int playing))
                audio.RemoveAudioEvent(playing);
        };
        game.Events.OnEva += (name, player) =>
        {
            if (localPlayer != null && player == localPlayer)
                eva.SetShouldPlay(name);
        };
        game.Events.OnAudioCommand += c =>
        {
            int local = localPlayer?.Index ?? -1;
            switch (c.Kind)
            {
                case AudioCommandKind.PlaySound:
                    audio.AddAudioEvent(c.Name, c.Object == null ? c.Position : null, c.Object?.Id ?? 0, c.Object?.Owner.Index ?? local);
                    break;
                case AudioCommandKind.Speech:
                    audio.AddAudioEvent(c.Name, playerIndex: local, uninterruptable: !c.Flag);
                    break;
                case AudioCommandKind.Music:
                    if (playlist.Count == 0)
                        audio.ChangeMusic(c.Name, c.Flag, local);
                    break;
                case AudioCommandKind.SetVolume:
                    audio.SetScriptVolume(c.Name, c.Value);
                    break;
                case AudioCommandKind.EnableType:
                    audio.SetAudioEventEnabled(c.Name, c.Flag);
                    break;
                case AudioCommandKind.RemoveDisabled:
                    audio.RemoveAllDisabledAudio();
                    break;
                case AudioCommandKind.RemoveType:
                    audio.RemovePlayingAudio(c.Name);
                    break;
                case AudioCommandKind.OverrideVolume:
                    audio.SetAudioEventVolumeOverride(c.Name, c.Value < 0 ? -1 : c.Value);
                    break;
                case AudioCommandKind.PauseSounds:
                    audio.PauseSounds(c.Flag);
                    break;
                case AudioCommandKind.PauseAmbient:
                case AudioCommandKind.EnableObjectSound:
                    // Object ambient sounds are not played yet.
                    break;
            }
        };
        world.Effects.SoundSink = (name, position, source) =>
            audio.AddAudioEvent(name, source == null ? position : null, source?.Id ?? 0, source?.Owner.Index ?? -1);
        eva.LocalPlayer = localPlayer;
    }

    /// <summary>Logic sound handles to playing audio handles.</summary>
    readonly Dictionary<uint, int> handledSounds = new();

    Player? Find(int index) => game.Players.All.FirstOrDefault(p => p.Index == index);

    /// <summary>Call every rendered frame: follows the camera, and steps the audio once per logic frame.</summary>
    public void Update()
    {
        var camera = world.Camera;
        audio.SetCamera(camera.Target, camera.EarPosition, camera.Yaw + MathF.PI / 2, game.Terrain.GroundHeight(camera.Target.X, camera.Target.Y));
        if (game.Frame == lastFrame)
            return;
        lastFrame = game.Frame;
        eva.Update(game.Frame);
        if (playlist.Count > 0 && game.Frame % 30 == 0 && !audio.IsMusicPlaying)
            audio.ChangeMusic(playlist[playlistAt++ % playlist.Count], false, audio.LocalPlayerIndex);
        audio.Update();
    }

    /// <summary>A sound for the local player's interface (clicks, unit voices on selection and orders).</summary>
    public int PlayUiSound(string name, Logic.Objects.GameObject? unit = null) =>
        audio.AddAudioEvent(name, null, unit?.Id ?? 0, unit?.Owner.Index ?? audio.LocalPlayerIndex);
}

/// <summary>
/// The announcer (the original's Eva): game events mark a message to play; each message then waits
/// TimeBetweenChecksMS before it can play again, expires if it could not play within
/// ExpirationTimeMS, and the highest-priority waiting message plays when the last one has finished,
/// using the local player's side's voice.
/// </summary>
public sealed class Eva
{
    sealed class Info
    {
        public required string Name;
        public int Priority = 1, FramesBetweenChecks = 900, FramesToExpire = 150;
        public Dictionary<string, string[]> SideSounds = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class Check
    {
        public required Info Info;
        public int TriggeredOnFrame, TimeForNextCheck;
        public bool AlreadyPlayed;
    }

    readonly AudioManager audio;
    readonly Dictionary<string, Info> infos = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Check> checks = new();
    readonly HashSet<string> shouldPlay = new(StringComparer.OrdinalIgnoreCase);
    int speechHandle;

    public Player? LocalPlayer { get; set; }
    public bool Enabled { get; set; } = true;

    public Eva(AudioManager audio, RuleNode events, RuleNode? sideSounds = null)
    {
        this.audio = audio;
        foreach (var (name, node) in events.Entries)
        {
            var info = new Info
            {
                Name = name,
                Priority = RuleValue.Int(node.GetString("Priority"), 1),
                FramesBetweenChecks = (int)MathF.Ceiling(RuleValue.Float(node.GetString("TimeBetweenChecksMS"), 30000) * 30 / 1000f),
                FramesToExpire = (int)MathF.Ceiling(RuleValue.Float(node.GetString("ExpirationTimeMS"), 5000) * 30 / 1000f),
            };
            if (node.TryGet("SideSounds", out var sides))
                foreach (var side in sides.AsList())
                    info.SideSounds[side.GetString("Side")] = RuleValue.Words(side.GetString("Sounds"));
            infos[name] = info;
        }
        // OpenWESTWOOD: more sides' announcers (SideEvaSounds: Event: Side: Sound), for these events or new ones
        // (the Tiberian Dawn and Dune 2000 houses).
        if (sideSounds != null)
            foreach (var (name, sides) in sideSounds.Entries)
            {
                if (!infos.TryGetValue(name, out var info))
                    infos[name] = info = new Info
                    {
                        Name = name,
                        Priority = RuleValue.Int(sides.GetString("Priority"), 1),
                        FramesBetweenChecks = (int)MathF.Ceiling(RuleValue.Float(sides.GetString("TimeBetweenChecksMS"), 3000) * 30 / 1000f),
                        FramesToExpire = (int)MathF.Ceiling(RuleValue.Float(sides.GetString("ExpirationTimeMS"), 5000) * 30 / 1000f),
                    };
                foreach (var (side, sound) in sides.Entries)
                    if (side is not ("Priority" or "TimeBetweenChecksMS" or "ExpirationTimeMS") && sound.Value is { Length: > 0 } words)
                        info.SideSounds[side] = RuleValue.Words(words);
            }
    }

    public void SetShouldPlay(string message) => shouldPlay.Add(message);

    public void Update(int frame)
    {
        if (!Enabled || LocalPlayer == null || frame < 2)
        {
            shouldPlay.Clear();
            return;
        }
        foreach (var info in infos.Values)
        {
            if (checks.Any(c => c.Info == info))
                continue;
            bool play = info.Name.Equals("LowPower", StringComparison.OrdinalIgnoreCase)
                ? !LocalPlayer.Energy.HasSufficientPower
                : shouldPlay.Contains(info.Name);
            if (play)
                checks.Add(new Check { Info = info, TriggeredOnFrame = frame, TimeForNextCheck = frame + info.FramesBetweenChecks });
        }
        shouldPlay.Clear();

        checks.RemoveAll(c => (c.TimeForNextCheck <= frame + 1 && c.AlreadyPlayed) || (c.TriggeredOnFrame + c.Info.FramesToExpire <= frame && !c.AlreadyPlayed));
        if (checks.Count == 0 || (speechHandle != 0 && audio.IsCurrentlyPlaying(speechHandle)))
            return;
        Check? best = null;
        foreach (var c in checks)
            if (!c.AlreadyPlayed && c.Info.Priority > (best?.Info.Priority ?? 0))
                best = c;
        if (best == null)
            return;
        string sound = "";
        if (best.Info.SideSounds.TryGetValue(LocalPlayer.Side, out var sounds) && sounds.Length > 0)
            sound = sounds[Random.Shared.Next(sounds.Length)];
        best.AlreadyPlayed = true;
        best.TimeForNextCheck = frame + best.Info.FramesBetweenChecks;
        speechHandle = audio.AddAudioEvent(sound, playerIndex: LocalPlayer.Index);
    }
}
