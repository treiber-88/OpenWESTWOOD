namespace OpenWestwood.Logic.Commands;

/// <summary>
/// Player orders are not applied immediately: they are queued and executed at the start of the next
/// logic frame. In multiplayer every machine receives the same commands for the same frame, which
/// keeps all simulations identical.
/// </summary>
public sealed class CommandQueue
{
    readonly GameLogic game;
    readonly List<GameCommand> pending = new();

    public CommandQueue(GameLogic game) => this.game = game;

    /// <summary>Raised for every executed command (used by replays and network sync).</summary>
    public event Action<int, GameCommand>? OnExecuted;

    /// <summary>
    /// In a network game, takes the player's orders for the network (returns true) instead of running them next frame;
    /// they come back through <see cref="EnqueueDirect"/> on the frame they are scheduled for, on every machine.
    /// </summary>
    public Func<GameCommand, bool>? Intercept { get; set; }

    public void Enqueue(GameCommand command)
    {
        if (Intercept?.Invoke(command) == true)
            return;
        pending.Add(command);
    }

    /// <summary>Queues an order for the next frame, past any <see cref="Intercept"/> (the network's own delivery).</summary>
    public void EnqueueDirect(GameCommand command) => pending.Add(command);

    internal void ExecutePending()
    {
        if (pending.Count == 0)
            return;
        var commands = pending.ToArray();
        pending.Clear();
        foreach (var command in commands)
        {
            command.Execute(game);
            OnExecuted?.Invoke(game.Frame, command);
        }
    }
}

/// <summary>An order from a player (move, attack, build, ...). See Commands/GameCommands.cs.</summary>
public abstract record GameCommand(int PlayerIndex)
{
    public abstract void Execute(GameLogic game);

    protected Players.Player? Player(GameLogic game) => PlayerIndex >= 0 && PlayerIndex < game.Players.All.Count ? game.Players.All[PlayerIndex] : null;

    /// <summary>The objects a command applies to, keeping only those the issuing player controls.</summary>
    protected IEnumerable<Objects.GameObject> Owned(GameLogic game, IEnumerable<uint> ids)
    {
        var player = Player(game);
        foreach (uint id in ids)
            if (game.FindObject(id) is { } obj && obj.Owner == player && !obj.IsEffectivelyDead)
                yield return obj;
    }
}
