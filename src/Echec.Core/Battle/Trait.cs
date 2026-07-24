namespace Echec.Core.Battle;

/// <summary>
/// Noms canoniques des TRAITS (particularités de classe), tels qu'écrits dans <c>units.json</c> et
/// <see cref="UnitClass.Traits"/>. Centralisés ici pour éviter les chaînes magiques côté moteur.
/// « Traverse allié » est un cas à part côté CLASSE : il y est porté par <see cref="UnitClass.PiercesAllies"/>
/// (et non par la liste de traits). Le moteur le lit toutefois via <see cref="Unit.HasTrait"/>, donc — comme
/// tous les autres traits — il peut aussi être octroyé par un <see cref="Equip.Equipment"/>.
///
/// Les MÉCANIQUES vivent dans <see cref="Match"/> (résolution d'attaque / déplacement). Tous les traits
/// sont implémentés au niveau du moteur ; il suffit d'ajouter le trait à une classe pour qu'il agisse.
/// </summary>
public static class Trait
{
    public const string Rempart = "Rempart";                 // -RempartReduction partout SAUF au contact direct (orthogonal)
    public const string TraverseAllie = "Traverse allié";    // tir au travers des alliés (= PiercesAllies)
    public const string Soin = "Soin";                       // action : soigne un allié ciblé (MOITIÉ de la puissance)
    public const string SoinParfait = "Soin parfait";        // idem Soin, mais soigne la puissance ENTIÈRE
    public const string DegatsDeZone = "Dégâts de zone";     // l'attaque éclabousse les cases autour de la cible
    public const string Franchissement = "Franchissement";   // se déplace au travers des unités
    public const string Transpercement = "Transpercement";   // touche aussi l'unité juste derrière la cible
    public const string Interception = "Interception";       // attaque d'opportunité sur un ennemi entrant en portée
    public const string AuraDeRempart = "Aura de rempart";   // donne l'effet Rempart aux alliés adjacents
    public const string AuraDePuissance = "Aura de puissance"; // +AuraPuissanceBonus de puissance aux alliés adjacents
    public const string AuraDeSurpuissance = "Aura de surpuissance"; // +AuraSurpuissanceBonus de puissance aux alliés adjacents
    public const string Riposte = "Riposte";                 // contre-attaque si elle survit ET peut atteindre l'assaillant
    public const string Duelliste = "Duelliste";             // -DamageReduction si attaque au corps à corps
    public const string Rage = "Rage";                       // +1 puissance par ennemi tué, cumulé sur la run (cf. Unit.Kills)
    public const string DrainDeVie = "Drain de vie";         // soigne l'attaquant de 50 % des dégâts infligés
    public const string ZoneMorte = "Zone morte";            // ne peut pas frapper au contact (portée min = 2)
    public const string Balistique = "Balistique";           // tir indirect : ignore les obstacles (montagne) sur la ligne
    public const string Vol = "Vol";                         // déplacement : ignore les obstacles de terrain (eau/montagne)
    public const string Formation = "Formation";             // +FormationBonus de puissance par allié adjacent
    public const string Esquive = "Esquive";                 // EsquiveChance de chance d'annuler une attaque subie
    public const string Embrochage = "Embrochage";           // l'attaque touche aussi les ennemis adjacents à la cible
    public const string Orage = "Orage";                     // à l'attaque : foudroie 3 ennemis AU HASARD (dégât fixe)
    public const string Tempete = "Tempête";                 // idem Orage (3 ennemis au hasard), dégât fixe plus élevé
    public const string AttaqueLibre = "Attaque libre";      // AJOUTE le tir comme une Dame (8 directions en ligne) EN PLUS de l'attaque native (le cavalier garde son saut)
    public const string Statique = "Statique";               // ne prend JAMAIS la place de sa cible en la tuant : reste sur sa case

    /// <summary>Tous les traits (pour piocher / valider une configuration de classe).</summary>
    public static readonly string[] All =
    {
        Rempart, TraverseAllie, Soin, SoinParfait, DegatsDeZone, Franchissement, Transpercement, Interception,
        AuraDeRempart, AuraDePuissance, AuraDeSurpuissance, Riposte, Duelliste, Rage,
        DrainDeVie, ZoneMorte, Balistique, Vol, Formation, Esquive, Embrochage, Orage, Tempete, AttaqueLibre,
        Statique,
    };
}
