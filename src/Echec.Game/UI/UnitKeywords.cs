using System.Collections.Generic;

namespace Echec.Game.UI;

/// <summary>
/// Textes des mots-clés affichés sur les cartes d'unité : un libellé court + une description.
/// IMPORTANT : la police pixel (<see cref="Echec.Engine.UI.Text.PixelFont"/>) ne possède NI accents NI
/// apostrophe/virgule. Les chaînes sont donc volontairement écrites sans diacritiques ni ponctuation
/// (l'apostrophe est remplacée par une espace). Descriptions PROVISOIRES : les mécaniques ne sont pas
/// encore implémentées (cf. units.json — données seules, à venir avec les tiers 2).
/// </summary>
public static class UnitKeywords
{
    public readonly record struct Keyword(string Label, string Description);

    // Clé = trait BRUT tel qu'il apparaît dans units.json (avec ses accents d'origine).
    private static readonly Dictionary<string, Keyword> ByTrait = new()
    {
        ["Rempart"] = new("REMPART",
            "ENCAISSE LES COUPS ET PROTEGE LES ALLIES PROCHES"),
        ["Soin"] = new("SOIN",
            "PEUT RENDRE DES PV A UN ALLIE A PORTEE"),
        ["Dégâts de zone"] = new("DEGATS DE ZONE",
            "TOUCHE AUSSI LES CASES AUTOUR DE LA CIBLE"),
        ["Franchissement"] = new("FRANCHISSEMENT",
            "IGNORE LES UNITES SUR SON CHEMIN DE DEPLACEMENT"),
        ["Transpercement"] = new("TRANSPERCEMENT",
            "L ATTAQUE TRAVERSE ET FRAPPE LES ENNEMIS ALIGNES DERRIERE LA CIBLE"),
        ["Interception"] = new("INTERCEPTION",
            "FRAPPE L ENNEMI QUI ENTRE DANS SA PORTEE"),
    };

    /// <summary>Mot-clé synthétisé pour <see cref="Echec.Core.Battle.UnitClass.PiercesAllies"/>.</summary>
    public static readonly Keyword PiercesAllies = new("TRAVERSE ALLIE",
        "TIRE A TRAVERS SES ALLIES SANS LES TOUCHER SEULS LES ENNEMIS BLOQUENT LA LIGNE");

    /// <summary>Mot-clé synthétisé pour une portée minimale &gt; 1 (archers).</summary>
    public static readonly Keyword DeadZone = new("ZONE MORTE",
        "NE FRAPPE PAS AU CORPS A CORPS DIRECT EN LIGNE DROITE (TIR EN DIAGONALE POSSIBLE)");

    /// <summary>Libellé + description d'un trait ; repli sobre si le trait n'est pas répertorié.</summary>
    public static Keyword For(string trait) =>
        ByTrait.TryGetValue(trait, out var k) ? k : new Keyword(trait.ToUpperInvariant(), "");
}
