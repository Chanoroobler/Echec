using System;
using System.Collections.Generic;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.UI;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Pile ou face au début d'un combat multi : la pièce (<c>piece.png</c>, face = le pion ; revers = le même disque
/// uni, généré au chargement) est lancée, tourne en ralentissant, retombe sur le camp qui commence, puis le résultat
/// s'affiche. Le premier joueur est déjà tiré (graine de l'hôte, cf. PvpSetup.HostStarts) : l'animation ne fait que
/// le montrer, et laisse aux joueurs le temps de se préparer. Jeu et pendules sont gelés pendant ce temps.
/// Rendu pixel-perfect : la pièce est toujours à l'échelle entière <see cref="CoinScale"/> ; entre deux faces on
/// montre la tranche (aplats unis), jamais une pièce écrasée.
/// </summary>
public sealed partial class GameplayScene
{
    private const float CoinTossTime = 3f;      // en l'air
    private const float CoinResultTime = 1.4f;  // pièce posée + résultat affiché
    private const float CoinFadeTime = 0.3f;    // disparition
    private const int CoinScale = 4;            // 20 px d'art → 80 px canvas
    private const int CoinRise = 120;           // hauteur du lancer (px canvas)
    private const int CoinHalfTurns = 17;       // demi-tours pendant le lancer

    private float _coinFlip = -1f;              // temps écoulé (-1 : pas de pile ou face en cours)
    private Texture2D? _coinFace, _coinBack;
    private Color _coinGold = Palette.Brown3;    // teinte dominante de la pièce (tranche), lue au chargement
    private Language _coinTextLang = (Language)(-1);
    private string _coinText = "";

    private float CoinFlipTotal => CoinTossTime + CoinResultTime + CoinFadeTime;

    /// <summary>Lance le pile ou face (début du combat multi).</summary>
    private void StartCoinFlip()
    {
        if (_coinFace == null)
        {
            _coinFace = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath("Assets/Icons/piece.png"));
            if (_coinFace != null)
                _coinBack = MakeCoinBack(_coinFace, out _coinGold);
        }
        _coinTextLang = (Language)(-1);
        _coinFlip = 0f;
    }

    /// <summary>Fait avancer le pile ou face. Vrai tant qu'il joue : la frame est consommée (rien d'autre ne bouge).</summary>
    private bool UpdateCoinFlip(float dt)
    {
        if (_coinFlip < 0f)
            return false;
        var before = _coinFlip;
        _coinFlip += dt;
        if (before < CoinTossTime && _coinFlip >= CoinTossTime)
            Context.Sounds.Play("menu_click");   // la pièce retombe
        if (_coinFlip >= CoinFlipTotal)
        {
            _coinFlip = -1f;
            StartPvpAnalysis();   // puis le temps d'étudier l'armée adverse (GameplayScene.PvpAnalysis.cs)
        }
        return true;
    }

    private void DrawCoinFlip(SpriteBatch sb, Viewport viewport)
    {
        if (_coinFlip < 0f || _coinFace == null || _coinBack == null)
            return;
        var t = _coinFlip;
        var alpha = 1f - MathHelper.Clamp((t - CoinTossTime - CoinResultTime) / CoinFadeTime, 0f, 1f);
        var size = _coinFace.Width * CoinScale;
        var x = (viewport.Width - size) / 2;
        var restY = (viewport.Height - size) / 2 - 20;

        // Lancer : arc (montée puis chute) et rotation qui ralentit, posée sur la face du camp qui commence.
        var p = MathHelper.Clamp(t / CoinTossTime, 0f, 1f);
        var y = restY - (int)(CoinRise * 4f * p * (1f - p));
        var phase = (1f - (1f - p) * (1f - p)) * CoinHalfTurns;
        var turns = (int)MathF.Round(phase);
        var localStarts = _pvpInfo!.LocalStarts;
        // Face (pion) = toi, revers = l'adversaire. Après le dernier demi-tour, la pièce montre le résultat : on
        // voit donc le résultat quand il reste un nombre PAIR de demi-tours, l'autre face sinon.
        var showFace = ((CoinHalfTurns - turns) % 2 == 0) == localStarts;
        var onEdge = p < 1f && MathF.Abs(phase - MathF.Floor(phase) - 0.5f) < 0.14f;

        if (onEdge)
        {
            // Tranche : aplats unis (contour sombre, cœur doré), centrée sur la pièce.
            var edgeH = 2 * CoinScale;
            var edge = new Rectangle(x, y + (size - edgeH) / 2, size, edgeH);
            DrawRect(sb, edge, Palette.Black1 * alpha);
            DrawRect(sb, new Rectangle(edge.X + CoinScale, edge.Y + CoinScale / 2, edge.Width - 2 * CoinScale, edgeH - CoinScale),
                _coinGold * alpha);
        }
        else
        {
            sb.Draw(showFace ? _coinFace : _coinBack, new Rectangle(x, y, size, size), Color.White * alpha);
        }

        if (t < CoinTossTime)
            return;
        // Résultat sous la pièce posée.
        RefreshCoinText();
        var font = Context.Font;
        const int scale = 2;
        var w = Math.Min(viewport.Width - 16, font.Measure(_coinText, scale) + 32);
        var h = font.LineHeight(scale) + 16;
        var box = new Rectangle((viewport.Width - w) / 2, restY + size + 16, w, h);
        DrawRect(sb, box, Palette.Black1 * (0.9f * alpha));
        font.DrawCentered(sb, PvpArt.Fit(font, _coinText, w - 8, scale), box, scale,
            (localStarts ? Palette.Yellow2 : Palette.Purple5) * alpha);
    }

    private void RefreshCoinText()
    {
        if (_coinTextLang == Loc.Current)
            return;
        _coinTextLang = Loc.Current;
        _coinText = _pvpInfo!.LocalStarts
            ? Loc.T("pvp.coin.you_start")
            : Loc.T("pvp.coin.they_start", _pvpInfo.DisplayName);
    }

    /// <summary>
    /// Revers de la pièce : le même disque, contour gardé, intérieur rempli de sa teinte dominante (le pion
    /// disparaît). Fait une fois au chargement. <paramref name="gold"/> = cette teinte (sert aussi à la tranche).
    /// </summary>
    private Texture2D MakeCoinBack(Texture2D face, out Color gold)
    {
        var w = face.Width;
        var h = face.Height;
        var pixels = new Color[w * h];
        face.GetData(pixels);

        // Teinte dominante parmi les pixels opaques et clairs (le fond doré, pas le contour noir).
        var counts = new Dictionary<uint, int>();
        foreach (var c in pixels)
            if (c.A == 255 && c.R + c.G + c.B > 240)
                counts[c.PackedValue] = counts.GetValueOrDefault(c.PackedValue) + 1;
        uint best = 0;
        var bestCount = -1;
        foreach (var (packed, n) in counts)
            if (n > bestCount)
            {
                best = packed;
                bestCount = n;
            }
        gold = bestCount > 0 ? new Color(best) : Palette.Brown3;

        // Intérieur du disque (sous le contour) rempli d'un aplat.
        var cx = w / 2f;
        var cy = h / 2f;
        var inner = Math.Min(w, h) / 2f - 1.4f;
        for (var yy = 0; yy < h; yy++)
            for (var xx = 0; xx < w; xx++)
            {
                var dx = xx + 0.5f - cx;
                var dy = yy + 0.5f - cy;
                var i = yy * w + xx;
                if (pixels[i].A > 0 && dx * dx + dy * dy < inner * inner)
                    pixels[i] = gold;
            }
        var back = new Texture2D(Context.GraphicsDevice, w, h);
        back.SetData(pixels);
        return back;
    }

    private void UnloadCoinFlip()
    {
        _coinFace?.Dispose();
        _coinFace = null;
        _coinBack?.Dispose();
        _coinBack = null;
        _coinFlip = -1f;
    }
}
