namespace Echec.Core.Battle;

/// <summary>Résultat d'une tentative de déplacement.</summary>
public enum MoveKind
{
    /// <summary>Coup illégal : rien n'a changé.</summary>
    Invalid,

    /// <summary>Déplacement vers une case vide.</summary>
    Moved,

    /// <summary>Attaque : la cible a survécu, l'attaquant reste sur place.</summary>
    Attacked,

    /// <summary>Attaque mortelle : la cible meurt et l'attaquant prend sa place.</summary>
    Killed
}
