namespace ChessArmy.Core.Battle;

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
    public const string Franchissement = "Franchissement";   // se déplace au travers des unités
    public const string Transpercement = "Transpercement";   // touche aussi l'unité juste derrière la cible
    public const string Interception = "Interception";       // attaque d'opportunité sur un ennemi entrant en portée
    public const string AuraDeRempart = "Aura de rempart";   // donne l'effet Rempart aux alliés adjacents
    public const string AuraDePuissance = "Aura de puissance"; // +AuraPuissanceBonus de puissance aux alliés adjacents
    public const string AuraDeCelerite = "Aura de célérité"; // +1 Déplacement aux alliés qui COMMENCENT leur tour au CONTACT DIRECT (orthogonal)
    public const string Riposte = "Riposte";                 // contre-attaque si elle survit ET peut atteindre l'assaillant
    public const string Duelliste = "Duelliste";             // -DamageReduction si attaque au corps à corps
    public const string Berserk = "Berserk";                 // +1 puissance par ennemi tué, cumulé sur la run (cf. Unit.Kills)
    public const string DrainDeVie = "Drain de vie";         // soigne l'attaquant de 50 % des dégâts infligés
    public const string ZoneMorte = "Zone morte";            // ne peut pas frapper au contact (portée min = 2)
    public const string Balistique = "Balistique";           // tir indirect : ignore les obstacles (montagne) sur la ligne
    public const string Vol = "Vol";                         // déplacement : ignore les obstacles de terrain (eau/montagne)
    public const string Formation = "Formation";             // +FormationBonus de puissance par allié adjacent
    public const string Esquive = "Esquive";                 // encaisse puis se replie aussitôt dans sa portée de déplacement, vers une case non menacée
    public const string Orage = "Orage";                     // à l'attaque : foudroie 3 ennemis AU HASARD (dégât fixe)
    public const string Tempete = "Tempête";                 // idem Orage (même dégât fixe) mais sur 5 ennemis au hasard au lieu de 3
    public const string AttaqueLibre = "Attaque libre";      // REMPLACE l'attaque native par le tir de Dame (8 directions en ligne) : le cavalier n'attaque plus au saut, mais se DÉPLACE toujours en L
    public const string Statique = "Statique";               // ne prend JAMAIS la place de sa cible en la tuant : reste sur sa case
    public const string Seisme = "Séisme";                   // à la FIN du tour adverse : frappe les ennemis adjacents pour sa puissance
    public const string Impact = "Impact";                   // à son PROPRE déplacement/attaque : 5 dégâts fixes aux ennemis autour de soi (jamais relayé par un autre trait)
    public const string Recule = "Recule";                   // à l'attaque : repousse la cible survivante d'une case ; +5 dégâts si un obstacle l'arrête
    public const string Renaissance = "Renaissance";         // ÉQUIPEMENT « Queue de phénix » : à la mort, ressuscite à 1 PV et se brise (une fois)
    public const string TueurDeGeants = "Tueur de géants";   // à l'attaque : +5 dégâts si la cible a PLUS de PV ACTUELS que l'attaquant (attaquant plus blessé)
    public const string Rage = "Rage";                       // combat : +7 puissance à la PREMIÈRE mort d'un allié (non cumulable, une seule fois par combat, cf. Unit.RagePower)
    public const string LienDePuissance = "Lien de puissance"; // +LienPuissanceBonus de puissance par allié dans sa PORTÉE DE DÉPLACEMENT (contextuel au placement, cf. Match)
    public const string RepositionnementStrategique = "Repositionnement stratégique"; // déplacement : ajoute un pas d'UNE case à gauche/droite quel que soit le domaine
    public const string LoupSolitaire = "Loup solitaire";    // ISOLÉ (aucun allié sur les 8 cases voisines) : +LoupSolitairePower de puissance et -LoupSolitaireReduction aux dégâts subis
    public const string Epines = "Épines";                   // renvoie à l'assaillant la MOITIÉ des dégâts réellement encaissés (jamais relayé : le renvoi ne renvoie pas)
    public const string RenaissanceUltime = "Renaissance ultime"; // ARBRE (Marchand) : à la mort, revient à PV PLEINS — UNE fois par PARTIE (cf. Run.UltimateReviveUsed)

    /// <summary>Tous les traits (pour piocher / valider une configuration de classe).</summary>
    public static readonly string[] All =
    {
        Rempart, TraverseAllie, Soin, SoinParfait, Franchissement, Transpercement, Interception,
        AuraDeRempart, AuraDePuissance, AuraDeCelerite, Riposte, Duelliste, Berserk,
        DrainDeVie, ZoneMorte, Balistique, Vol, Formation, Esquive, Orage, Tempete, AttaqueLibre,
        Statique, Seisme, Impact, Recule, Renaissance, TueurDeGeants, Rage,
        LienDePuissance, RepositionnementStrategique, LoupSolitaire, Epines, RenaissanceUltime,
    };
}
