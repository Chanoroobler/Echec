using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ChessArmy.LocEditor;

internal enum RowKind { Header, Comment, Blank, Entry }

/// <summary>Une ligne du CSV. Les non-<see cref="RowKind.Entry"/> sont réécrites VERBATIM (<see cref="Raw"/>).</summary>
internal sealed class LocRow
{
    public RowKind Kind;
    public string Raw = "";   // en-tête / commentaire / ligne vide : conservée telle quelle
    public string Key = "";   // Entry seulement
    public string Fr = "";
    public string En = "";

    public bool IsEntry => Kind == RowKind.Entry;
}

/// <summary>
/// Modèle du fichier <c>strings.csv</c> : la liste ORDONNÉE de ses lignes (en-tête, commentaires, vides,
/// entrées). L'édition ne touche qu'aux entrées ; tout le reste est préservé à l'enregistrement, ainsi que
/// la fin de ligne d'origine (CRLF/LF) et la présence d'un saut final. Écrit en UTF-8 SANS BOM (accents
/// conservés, aucun préfixe que le jeu n'attend pas).
/// </summary>
internal sealed class LocFile
{
    public List<LocRow> Rows { get; } = new();

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
                file.Rows.Add(new LocRow { Kind = RowKind.Header, Raw = line });
            else if (trimmed.Length == 0)
                file.Rows.Add(new LocRow { Kind = RowKind.Blank, Raw = line });
            else if (trimmed[0] == '#')
                file.Rows.Add(new LocRow { Kind = RowKind.Comment, Raw = line });
            else
            {
                // Découpage sur les virgules, comme le jeu (Loc.LoadCsv). Une entrée bien formée a 2 virgules :
                // tout ce qui suit la 2e est rejoint dans EN pour ne RIEN perdre d'un fichier mal formé (la
                // validation signalera alors la virgule à corriger).
                var parts = line.Split(',');
                file.Rows.Add(new LocRow
                {
                    Kind = RowKind.Entry,
                    Key = parts[0].Trim(),
                    Fr = parts.Length > 1 ? parts[1] : "",
                    En = parts.Length > 2 ? string.Join(",", parts, 2, parts.Length - 2) : "",
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
            sb.Append(r.IsEntry ? $"{r.Key},{r.Fr},{r.En}" : r.Raw);
            if (i < Rows.Count - 1 || _trailingNewline)
                sb.Append(_newline);
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
