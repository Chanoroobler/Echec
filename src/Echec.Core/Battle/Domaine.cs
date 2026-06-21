namespace Echec.Core.Battle;

/// <summary>
/// Domaine = motif de déplacement, nommé d'après la pièce d'échecs correspondante.
/// Le PION correspond au déplacement « 1 case dans toute direction » (pas de Roi : c'est
/// le Pion dans ce jeu). Le domaine ne porte QUE les directions et le type de déplacement ;
/// la distance (portée) vient de la classe.
/// </summary>
public enum Domaine
{
    Pion,
    Fou,
    Cavalier,
    Tour,
    Dame
}

/// <summary>Type de déplacement : glissé (s'arrête sur obstacle) ou sauté (par-dessus).</summary>
public enum MovementKind
{
    Slide,
    Jump
}
