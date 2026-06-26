namespace Echec.Core.Battle;

/// <summary>
/// Noms canoniques des TRAITS (particularités de classe), tels qu'écrits dans <c>units.json</c> et
/// <see cref="UnitClass.Traits"/>. Centralisés ici pour éviter les chaînes magiques côté moteur.
/// « Traverse allié » est un cas à part : il est porté par <see cref="UnitClass.PiercesAllies"/>
/// (et non par la liste de traits), mais on garde sa constante pour <see cref="Unit.HasTrait"/>.
///
/// Les MÉCANIQUES vivent dans <see cref="Match"/> (résolution d'attaque / déplacement). Tous les traits
/// sont implémentés au niveau du moteur ; il suffit d'ajouter le trait à une classe pour qu'il agisse.
/// </summary>
public static class Trait
{
    public const string Rempart = "Rempart";                 // -DamageReduction si attaque à portée >= 2
    public const string TraverseAllie = "Traverse allié";    // tir au travers des alliés (= PiercesAllies)
    public const string Soin = "Soin";                       // action : soigne un allié ciblé
    public const string DegatsDeZone = "Dégâts de zone";     // l'attaque éclabousse les cases autour de la cible
    public const string Franchissement = "Franchissement";   // se déplace au travers des unités
    public const string Transpercement = "Transpercement";   // touche aussi l'unité juste derrière la cible
    public const string Interception = "Interception";       // attaque d'opportunité sur un ennemi entrant en portée
    public const string AuraDeRempart = "Aura de rempart";   // donne l'effet Rempart aux alliés adjacents
    public const string Riposte = "Riposte";                 // contre-attaque en mêlée si l'unité survit
    public const string Duelliste = "Duelliste";             // -DamageReduction si attaque au corps à corps
    public const string Rage = "Rage";                       // +RageBonus de puissance sous RageHpThreshold PV
    public const string BouclierDivin = "Bouclier divin";    // un allié adjacent ne peut pas mourir (PV >= 1)
    public const string Benediction = "Bénédiction";         // +BenedictionBonus de puissance aux alliés adjacents

    /// <summary>Tous les traits (pour piocher / valider une configuration de classe).</summary>
    public static readonly string[] All =
    {
        Rempart, TraverseAllie, Soin, DegatsDeZone, Franchissement, Transpercement, Interception,
        AuraDeRempart, Riposte, Duelliste, Rage, BouclierDivin, Benediction,
    };
}
