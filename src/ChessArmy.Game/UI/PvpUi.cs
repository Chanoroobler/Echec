using System;
using System.Collections.Generic;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.Input;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.UI;

/// <summary>Petits outils partagés par les écrans du multijoueur (textes de rang, temps, boutons, focus manette).</summary>
public static class PvpUi
{
    private static readonly string[] Roman = { "", "I", "II", "III", "IV", "V" };

    /// <summary>« ARGENT II » (sans division pour Master).</summary>
    public static string RankName(PvpRank rank) =>
        Loc.T("pvp.rank." + rank.Id) + (rank.Division > 0 && rank.Division < Roman.Length ? " " + Roman[rank.Division] : "");

    public static string RankName(int rating) => RankName(PvpRanks.FromRating(rating));

    /// <summary>« 9:05 ».</summary>
    public static string Clock(float seconds)
    {
        var s = Math.Max(0, (int)Math.Ceiling(seconds));
        return $"{s / 60}:{s % 60:00}";
    }

    /// <summary>
    /// Texte en capitales (descriptions de traits de strings.csv) remis en casse de phrase, plus lisible : tout en
    /// minuscules, une majuscule en début de texte et après « . ! ? ». Les sigles sans voyelle (« PV », « LP »)
    /// restent en capitales. Minuscules de la langue courante (i sans point en turc).
    /// </summary>
    public static string SentenceCase(string text)
    {
        var culture = Loc.Current == Language.Turkce
            ? System.Globalization.CultureInfo.GetCultureInfo("tr-TR")
            : System.Globalization.CultureInfo.InvariantCulture;
        var chars = text.ToLower(culture).ToCharArray();
        // Sigles : mots de 2-3 lettres sans voyelle dans l'original.
        for (var i = 0; i < text.Length;)
        {
            if (!char.IsLetter(text[i]))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < text.Length && char.IsLetter(text[i]))
                i++;
            var len = i - start;
            if (len is >= 2 and <= 3 && text.IndexOfAny("AEIOUYÀÂÄÉÈÊËÎÏÔÖÙÛÜaeiouy".ToCharArray(), start, len) < 0
                && char.IsUpper(text[start]))
                for (var k = start; k < i; k++)
                    chars[k] = text[k];
        }
        var capital = true;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (capital && char.IsLetter(c))
            {
                chars[i] = char.ToUpper(c, culture);
                capital = false;
            }
            else if (c is '.' or '!' or '?')
                capital = true;
            else if (capital && char.IsDigit(c))
                capital = false;   // « 5 dégâts… » : pas de majuscule plus loin dans la phrase
        }
        return new string(chars);
    }

    public static string CommanderName(string id) => Loc.TOr("commander." + id, id).ToUpperInvariant();

    public static string UnitName(UnitClass cls) => UnitCardRenderer.NameOf(cls);

    /// <summary>Bouton du style du jeu ; renvoie son rectangle (pour le clic).</summary>
    public static void Button(GameContext ctx, SpriteBatch sb, Rectangle r, string label, bool focused, bool enabled = true)
    {
        var gp = ctx.Input.UsingGamepad;
        var hover = enabled && (gp ? focused : r.Contains(ctx.Input.MousePosition));
        var dy = ctx.Style.DrawButton(sb, r, UiStyle.StateOf(hover, enabled && !gp && ctx.Input.IsLeftDown));
        var area = r;
        area.Offset(0, dy);
        ctx.Font.DrawCentered(sb, label, area, 1, enabled ? Palette.White : Palette.Grey);
    }
}

/// <summary>
/// Focus manette générique : une liste de rectangles cliquables reconstruite à chaque frame ; les flèches
/// déplacent le focus vers le rectangle le plus proche dans la direction voulue, A « clique » sur le focus.
/// À la souris, rien ne change (le clic reste le clic).
/// </summary>
public sealed class PvpFocus
{
    private readonly List<Rectangle> _targets = new();
    public int Index { get; set; }

    public void Clear() => _targets.Clear();

    /// <summary>Ajoute une cible ; renvoie son indice.</summary>
    public int Add(Rectangle r)
    {
        _targets.Add(r);
        return _targets.Count - 1;
    }

    public bool IsFocused(int index) => index == Index;

    /// <summary>Déplace le focus selon les flèches/stick (manette uniquement).</summary>
    public void Navigate(InputManager input)
    {
        if (_targets.Count == 0)
            return;
        Index = Math.Clamp(Index, 0, _targets.Count - 1);
        if (!input.UsingGamepad)
            return;
        foreach (var (dir, dx, dy) in new[] { (NavDir.Up, 0, -1), (NavDir.Down, 0, 1), (NavDir.Left, -1, 0), (NavDir.Right, 1, 0) })
        {
            if (!input.Nav(dir))
                continue;
            var from = _targets[Index].Center;
            var best = -1;
            var bestScore = double.MaxValue;
            for (var i = 0; i < _targets.Count; i++)
            {
                if (i == Index)
                    continue;
                var c = _targets[i].Center;
                var along = (c.X - from.X) * dx + (c.Y - from.Y) * dy;
                if (along <= 0)
                    continue;
                var across = Math.Abs((c.X - from.X) * dy) + Math.Abs((c.Y - from.Y) * dx);
                var score = along + across * 2.5;
                if (score < bestScore) { bestScore = score; best = i; }
            }
            if (best >= 0)
                Index = best;
        }
    }

    /// <summary>Cible activée cette frame (clic souris dessus, ou A sur le focus), sinon -1.</summary>
    public int Activated(InputManager input)
    {
        if (input.UsingGamepad)
            return input.WasConfirmPressed && Index < _targets.Count ? Index : -1;
        if (!input.WasLeftClicked)
            return -1;
        for (var i = _targets.Count - 1; i >= 0; i--)
            if (_targets[i].Contains(input.MousePosition))
                return i;
        return -1;
    }

    /// <summary>Position du « pointeur » : la souris, ou le centre du focus à la manette.</summary>
    public Point Pointer(InputManager input) =>
        input.UsingGamepad && Index < _targets.Count ? _targets[Index].Center : input.MousePosition;
}
