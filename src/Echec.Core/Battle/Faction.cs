namespace Echec.Core.Battle;

/// <summary>Camp d'une unité.</summary>
public enum Faction
{
    Player,
    Enemy
}

public static class FactionExtensions
{
    public static Faction Opponent(this Faction faction) =>
        faction == Faction.Player ? Faction.Enemy : Faction.Player;
}
