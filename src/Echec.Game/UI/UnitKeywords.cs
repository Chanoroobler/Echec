using System.Collections.Generic;
using Echec.Engine.Localization;

namespace Echec.Game.UI;

/// <summary>
/// Textes des mots-clés affichés sur les cartes d'unité : un libellé court + une description.
/// Les chaînes vivent dans <c>Assets/Config/strings.csv</c> (clés <c>kw.*.label</c> / <c>kw.*.desc</c>)
/// et sont résolues à la volée selon la langue active, donc un changement de langue se reflète
/// immédiatement. IMPORTANT : la police pixel (<see cref="Echec.Engine.UI.Text.PixelFont"/>) n'a NI
/// accents NI apostrophe/virgule — les valeurs CSV sont écrites sans diacritiques ni ponctuation.
/// Les mécaniques de tous les traits sont implémentées dans <see cref="Echec.Core.Battle.Match"/>.
/// </summary>
public static class UnitKeywords
{
    public readonly record struct Keyword(string Label, string Description);

    // Trait BRUT (tel qu'il apparaît dans units.json, accents d'origine) → préfixe de clé CSV.
    private static readonly Dictionary<string, string> KeyByTrait = new()
    {
        ["Rempart"] = "kw.rempart",
        ["Soin"] = "kw.soin",
        ["Dégâts de zone"] = "kw.zone",
        ["Franchissement"] = "kw.franchissement",
        ["Transpercement"] = "kw.transpercement",
        ["Interception"] = "kw.interception",
        ["Aura de rempart"] = "kw.aura_rempart",
        ["Riposte"] = "kw.riposte",
        ["Duelliste"] = "kw.duelliste",
        ["Rage"] = "kw.rage",
        ["Bouclier divin"] = "kw.bouclier_divin",
        ["Bénédiction"] = "kw.benediction",
    };

    /// <summary>Mot-clé synthétisé pour <see cref="Echec.Core.Battle.UnitClass.PiercesAllies"/>.</summary>
    public static Keyword PiercesAllies => FromKey("kw.pierce_allies");

    /// <summary>Mot-clé synthétisé pour une portée minimale &gt; 1 (archers).</summary>
    public static Keyword DeadZone => FromKey("kw.dead_zone");

    /// <summary>Libellé + description d'un trait ; repli sobre si le trait n'est pas répertorié.</summary>
    public static Keyword For(string trait) =>
        KeyByTrait.TryGetValue(trait, out var key)
            ? FromKey(key)
            : new Keyword(trait.ToUpperInvariant(), "");

    private static Keyword FromKey(string key) => new(Loc.T(key + ".label"), Loc.T(key + ".desc"));
}
