using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ChessArmy.LocEditor;

internal enum RowKind { Header, Comment, Blank, Entry }

/// <summary>Une ligne du CSV. Les non-<see cref="RowKind.Entry"/> sont réécrites VERBATIM (<see cref="Raw"/>).</summary>
internal sealed class LocRow
{
    public RowKind Kind;
    public string Raw = "";           // en-tête / commentaire / ligne vide : conservée telle quelle
    public string Key = "";           // Entry seulement
    public List<string> Values = new(); // une valeur par langue (colonnes après la clé), dans l'ordre du CSV

    public bool IsEntry => Kind == RowKind.Entry;
}

/// <summary>
/// Modèle du fichier <c>strings.csv</c> : la liste ORDONNÉE de ses lignes (en-tête, commentaires, vides,
/// entrées). L'édition ne touche qu'aux entrées ; tout le reste est préservé à l'enregistrement, ainsi que
/// la fin de ligne d'origine (CRLF/LF) et la présence d'un saut final. Écrit en UTF-8 SANS BOM (accents
/// conservés, aucun préfixe que le jeu n'attend pas).
///
/// Générique sur le nombre de langues : les colonnes après <c>Key</c> sont lues depuis l'en-tête
/// (<see cref="Langs"/>) et chaque entrée porte une valeur par colonne (<see cref="LocRow.Values"/>).
/// Ajouter une langue = ajouter une colonne au CSV ; l'éditeur s'y adapte tout seul.
/// </summary>
internal sealed class LocFile
{
    public List<LocRow> Rows { get; } = new();

    /// <summary>Noms des colonnes de langue (ex. FR EN IT DE ES PL TR), lus dans l'en-tête.</summary>
    public List<string> Langs { get; private set; } = new() { "FR", "EN" };

    private string _newline = "\r\n";
    private bool _trailingNewline = true;

    public static LocFile Load(string path)
    {
        var text = File.ReadAllText(path);
        var file = new LocFile
        {
            _newline = text.Contains("\r\n") ? "\r\n" : "\n",
            _trailingNewline = text.EndsWith('\n'),
        };

        var lines = text.Replace("\r\n", "\n").Split('\n');
        // Le dernier élément vide provient du saut de ligne final : réémis via _trailingNewline, pas une ligne.
        var count = lines.Length;
        if (count > 0 && lines[count - 1].Length == 0)
            count--;

        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            if (i == 0)
            {
                file.Rows.Add(new LocRow { Kind = RowKind.Header, Raw = line });
                // En-tête = Key,FR,EN,... : les colonnes de langue sont tout ce qui suit la clé.
                var head = line.Split(',');
                if (head.Length > 1)
                    file.Langs = head.Skip(1).Select(h => h.Trim()).ToList();
            }
            else if (trimmed.Length == 0)
                file.Rows.Add(new LocRow { Kind = RowKind.Blank, Raw = line });
            else if (trimmed[0] == '#')
                file.Rows.Add(new LocRow { Kind = RowKind.Comment, Raw = line });
            else
            {
                // Découpage sur les virgules comme le jeu (Loc.LoadCsv) : les valeurs n'en contiennent pas.
                // On garde les valeurs VERBATIM (pas de trim) pour un round-trip octet à octet.
                var parts = line.Split(',');
                file.Rows.Add(new LocRow
                {
                    Kind = RowKind.Entry,
                    Key = parts[0].Trim(),
                    Values = parts.Skip(1).ToList(),
                });
            }
        }
        return file;
    }

    public void Save(string path)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            sb.Append(r.IsEntry ? r.Key + "," + string.Join(",", r.Values) : r.Raw);
            if (i < Rows.Count - 1 || _trailingNewline)
                sb.Append(_newline);
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
