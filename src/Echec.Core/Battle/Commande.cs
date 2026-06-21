namespace Echec.Core.Battle;

/// <summary>Rôle d'une unité COMMANDE : meneur dont la mort décide la partie.</summary>
public enum CommandeRole
{
    Commander, // commandant du joueur : sa mort = défaite
    Boss       // boss ennemi : le tuer = victoire du combat de boss
}

/// <summary>
/// Définition d'une unité COMMANDE (commandant joueur ou boss ennemi). COMMANDE est un
/// RÔLE à part, hors des 5 domaines : l'unité emprunte le motif de déplacement d'un des
/// 5 domaines via <see cref="Movement"/> — et ce motif peut varier d'un meneur à l'autre.
/// Les stats (asset + PV + dégâts + portées) sont portées par <see cref="BaseClass"/>.
/// Chargée depuis units.json (repli codé dans <see cref="Commandes"/>).
/// </summary>
public sealed class CommandeDef
{
    public CommandeDef(CommandeRole role, Domaine movement, UnitClass baseClass)
    {
        Role = role;
        Movement = movement;
        BaseClass = baseClass;
    }

    public CommandeRole Role { get; }

    /// <summary>Domaine (parmi les 5) dont l'unité emprunte le motif de déplacement.</summary>
    public Domaine Movement { get; }

    public UnitClass BaseClass { get; }

    public string Name => BaseClass.Name;
}
