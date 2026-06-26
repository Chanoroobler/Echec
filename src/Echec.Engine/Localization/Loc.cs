using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Echec.Engine.Localization;

/// <summary>
/// Table de localisation statique : clé → texte traduit, chargée depuis un CSV
/// (<c>Assets/Config/strings.csv</c>, colonnes <c>Key,FR,EN</c>). Accessible partout via
/// <see cref="T(string)"/> sans injection — même motif que <c>Domaines</c>/<c>Commandes</c>,
/// pratique car les textes sont dessinés depuis des renderers dispersés.
///
/// Tolérante aux pannes : fichier manquant / clé absente / colonne vide ⇒ repli sur la
/// colonne par défaut (Francais) puis sur la clé brute, plutôt que de planter le rendu.
/// </summary>
public static class Loc
{
    // Clé → valeurs par langue (index = (int)Language). Une entrée peut avoir moins de
    // colonnes que de langues si la ligne CSV est incomplète : le repli s'en charge.
    private static readonly Dictionary<string, string[]> Rows = new();

    /// <summary>Langue active. Fixée depuis les réglages au démarrage et au changement d'option.</summary>
    public static Language Current { get; set; } = Language.Francais;

    /// <summary>Charge le CSV depuis le disque (sans effet si absent / illisible).</summary>
    public static void Load(string csvPath)
    {
        try
        {
            if (File.Exists(csvPath))
                LoadCsv(File.ReadAllText(csvPath));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"strings.csv ignoré : {ex.Message}");
        }
    }

    /// <summary>
    /// Remplit la table depuis le contenu CSV. Format : 1re ligne = en-tête (ignorée), puis
    /// <c>cle,fr,en</c> par ligne. Les lignes vides ou commençant par <c>#</c> sont ignorées.
    /// La police pixel n'a pas de virgule, donc les valeurs n'en contiennent pas ; on découpe
    /// néanmoins sur les DEUX premières virgules seulement, laissant la dernière colonne libre.
    /// </summary>
    public static void LoadCsv(string content)
    {
        Rows.Clear();
        var first = true;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (first) { first = false; continue; }   // en-tête

            var parts = line.Split(',');
            var key = parts[0].Trim();
            if (key.Length == 0)
                continue;

            var values = new string[parts.Length - 1];
            for (var i = 1; i < parts.Length; i++)
                values[i - 1] = Unquote(parts[i]);
            Rows[key] = values;
        }
    }

    /// <summary>Texte traduit pour la langue active ; repli colonne 0 puis clé brute.</summary>
    public static string T(string key)
    {
        if (Rows.TryGetValue(key, out var cols))
        {
            var i = (int)Current;
            if (i < cols.Length && cols[i].Length > 0)
                return cols[i];
            if (cols.Length > 0 && cols[0].Length > 0)
                return cols[0];
        }
        return key;
    }

    /// <summary>Texte traduit avec arguments (<see cref="string.Format(string, object[])"/>).</summary>
    public static string T(string key, params object[] args) => string.Format(T(key), args);

    /// <summary>
    /// Texte traduit pour la langue active, ou <paramref name="fallback"/> si la clé est absente
    /// (utile pour des libellés issus de données : noms d'unités via leur asset).
    /// </summary>
    public static string TOr(string key, string fallback)
    {
        if (Rows.TryGetValue(key, out var cols))
        {
            var i = (int)Current;
            if (i < cols.Length && cols[i].Length > 0)
                return cols[i];
            if (cols.Length > 0 && cols[0].Length > 0)
                return cols[0];
        }
        return fallback;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
            s = s[1..^1];
        return s;
    }
}
