using System.Collections.Generic;
using System.Text;

namespace ChessArmy.Core.Text;

/// <summary>
/// Découpage et échappement d'une ligne CSV, à la manière d'un tableur (RFC 4180) : un champ peut être
/// entouré de guillemets, et alors contenir des VIRGULES ; un guillemet s'y écrit doublé (<c>""</c>).
/// Un champ sans guillemets est rendu tel quel, octet pour octet (aucun trim) : c'est à l'appelant de
/// décider — le jeu nettoie les espaces, l'éditeur de loc garde tout pour réécrire le fichier à l'identique.
///
/// Source UNIQUE du format de <c>strings.csv</c>, partagée par le jeu (<c>Loc.LoadCsv</c>), les tests et
/// l'éditeur de loc : avant, chacun découpait naïvement sur les virgules, ce qui interdisait toute virgule
/// dans un texte affiché.
/// </summary>
public static class Csv
{
    /// <summary>Champs de <paramref name="line"/>, guillemets retirés et <c>""</c> ramenés à <c>"</c>.</summary>
    public static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var i = 0;
        while (true)
        {
            if (i < line.Length && line[i] == '"')
            {
                // Champ ENTRE GUILLEMETS : tout jusqu'au guillemet fermant, virgules comprises.
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            field.Append('"');   // "" = un guillemet littéral
                            i += 2;
                            continue;
                        }
                        i++;   // guillemet fermant
                        break;
                    }
                    field.Append(line[i++]);
                }
                // Ce qui traînerait entre le guillemet fermant et la virgule suivante est gardé tel quel.
                while (i < line.Length && line[i] != ',')
                    field.Append(line[i++]);
            }
            else
            {
                while (i < line.Length && line[i] != ',')
                    field.Append(line[i++]);
            }

            fields.Add(field.ToString());
            field.Clear();
            if (i >= line.Length)
                return fields;
            i++;   // saute la virgule : un champ suit toujours, même vide
        }
    }

    /// <summary>
    /// Écrit <paramref name="value"/> comme champ CSV : entre guillemets seulement s'il le faut (virgule,
    /// guillemet), sinon tel quel — un texte ordinaire ressort donc identique à ce qu'il était.
    /// </summary>
    public static string Escape(string value) =>
        value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    /// <summary>Recompose une ligne à partir de ses champs (chacun échappé au besoin).</summary>
    public static string JoinLine(IEnumerable<string> fields)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var f in fields)
        {
            if (!first)
                sb.Append(',');
            sb.Append(Escape(f));
            first = false;
        }
        return sb.ToString();
    }
}
