using System.Collections.Generic;
using ChessArmy.Engine.Localization;

namespace ChessArmy.Game.UI;

/// <summary>
/// Textes des mots-clés affichés sur les cartes d'unité : un libellé court + une description.
/// Les chaînes vivent dans <c>Assets/Config/strings.csv</c> (clés <c>kw.*.label</c> / <c>kw.*.desc</c>)
/// et sont résolues à la volée selon la langue active, donc un changement de langue se reflète
/// immédiatement. IMPORTANT : la police pixel (<see cref="ChessArmy.Engine.UI.Text.PixelFont"/>) n'a PAS
/// d'accents ni de virgule (mais gère l'apostrophe) — les valeurs CSV sont écrites sans diacritiques ni
/// virgule ; l'apostrophe d'élision (L'ALLIE, D'UNE ATTAQUE) est en revanche autorisée et affichée.
/// Les mécaniques de tous les traits sont implémentées dans <see cref="ChessArmy.Core.Battle.Match"/>.
/// </summary>
public static class UnitKeywords
{
    public readonly record struct Keyword(string Label, string Description);

    // Trait BRUT (tel qu'il apparaît dans units.json, accents d'origine) → préfixe de clé CSV.
    private static readonly Dictionary<string, string> KeyByTrait = new()
    {
        ["Rempart"] = "kw.rempart",
        ["Traverse allié"] = "kw.pierce_allies",   // porté par la CLASSE via PiercesAllies, mais octroyable par un équipement (Lance)
        ["Soin"] = "kw.soin",
        ["Soin parfait"] = "kw.soin_parfait",
        ["Dégâts de zone"] = "kw.zone",
        ["Franchissement"] = "kw.franchissement",
        ["Transpercement"] = "kw.transpercement",
        ["Interception"] = "kw.interception",
        ["Aura de rempart"] = "kw.aura_rempart",
        ["Aura de puissance"] = "kw.aura_puissance",
        ["Aura de surpuissance"] = "kw.aura_surpuissance",
        ["Riposte"] = "kw.riposte",
        ["Duelliste"] = "kw.duelliste",
        ["Rage"] = "kw.rage",
        ["Drain de vie"] = "kw.drain_vie",
        ["Zone morte"] = "kw.dead_zone",
        ["Balistique"] = "kw.balistique",
        ["Vol"] = "kw.vol",
        ["Formation"] = "kw.formation",
        ["Esquive"] = "kw.esquive",
        ["Embrochage"] = "kw.embrochage",
        ["Orage"] = "kw.orage",
        ["Tempête"] = "kw.tempete",
        ["Attaque libre"] = "kw.attaque_libre",
        ["Statique"] = "kw.statique",
        ["Séisme"] = "kw.seisme",
    };

    /// <summary>Mot-clé synthétisé pour <see cref="ChessArmy.Core.Battle.UnitClass.PiercesAllies"/>.</summary>
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
