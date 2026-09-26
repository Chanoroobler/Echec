using System.Linq;
using ChessArmy.Core.Text;
using Xunit;

namespace ChessArmy.Core.Tests;

/// <summary>
/// Format de <c>strings.csv</c> : champs à la manière d'un tableur, une valeur entre guillemets pouvant
/// contenir des virgules. Vérifie le découpeur partagé ET le fichier livré (une valeur par langue, partout).
/// </summary>
public class CsvTests
{
    [Fact]
    public void PlainFields_AreSplitOnCommas_AndKeptVerbatim()
    {
        Assert.Equal(new[] { "key", "FR ", " EN", "" }, Csv.SplitLine("key,FR , EN,"));
    }

    [Fact]
    public void QuotedField_KeepsItsCommas()
    {
        var parts = Csv.SplitLine("tree.x.desc,\"À la mort, 2 soldats.\",\"When it dies, 2 soldiers.\"");

        Assert.Equal(3, parts.Count);
        Assert.Equal("À la mort, 2 soldats.", parts[1]);
        Assert.Equal("When it dies, 2 soldiers.", parts[2]);
    }

    [Fact]
    public void DoubledQuote_IsALiteralQuote()
    {
        Assert.Equal("Il dit \"stop\", puis part.", Csv.SplitLine("k,\"Il dit \"\"stop\"\", puis part.\"")[1]);
    }

    [Fact]
    public void Escape_OnlyQuotesWhenNeeded_AndRoundTrips()
    {
        Assert.Equal("SOLDAT", Csv.Escape("SOLDAT"));   // un texte ordinaire ressort identique
        var fields = new[] { "k", "a, b", "dit \"oui\"", "" };

        Assert.Equal(fields, Csv.SplitLine(Csv.JoinLine(fields)));
    }

    /// <summary>
    /// Chaque entrée du fichier LIVRÉ a exactement une valeur par langue de l'en-tête. Une virgule oubliée
    /// hors guillemets décalerait toutes les traductions de la ligne : ce test l'attrape avant le jeu.
    /// </summary>
    [Fact]
    public void ShippedStrings_EveryEntryHasOneValuePerLanguage()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "ChessArmy.Game")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var lines = System.IO.File.ReadAllLines(
            System.IO.Path.Combine(dir!.FullName, "src", "ChessArmy.Game", "Assets", "Config", "strings.csv"));

        var columns = Csv.SplitLine(lines[0]).Count;
        var broken = lines.Skip(1)
            .Select((line, i) => (line, number: i + 2))
            .Where(l => l.line.Trim().Length > 0 && l.line.TrimStart()[0] != '#')
            .Where(l => Csv.SplitLine(l.line).Count != columns)
            .Select(l => $"ligne {l.number} : {Csv.SplitLine(l.line).Count} champs au lieu de {columns}")
            .ToList();

        Assert.True(broken.Count == 0, string.Join("\n", broken));
    }
}
