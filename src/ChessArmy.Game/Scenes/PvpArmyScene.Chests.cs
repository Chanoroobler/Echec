using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.UI;
using ChessArmy.Engine.Localization;
using ChessArmy.Game.Net;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Vue COFFRES de l'écran Armée (même barre du haut) : coffre de PIONS (T2, T3, commandants) et coffre
/// d'ÉQUIPEMENTS, contenu montré case par case (obtenu en couleur, le reste en silhouette noire), bouton OUVRIR
/// avec son prix. Ouverture : points débités, animation des 4 images de <c>coffreAnimate.png</c>, puis la fenêtre
/// « NOUVEAU ! ». Ce qui sort est tout de suite dans la réserve du build (ou dans les flèches du commandant).
/// </summary>
public sealed partial class PvpArmyScene
{
    private const float ChestFrameSeconds = 0.17f;
    private const int ChestFrames = 4;

    private bool _opening;
    private bool _openUnits;
    private float _openT;
    private bool _revealOpen;

    // Roulette « casino » entre l'ouverture du coffre et la fenêtre « NOUVEAU ! » : une bande d'objets du coffre
    // défile sous un repère, ralentit et s'arrête sur l'objet gagné (déjà tiré à l'ouverture : la mise en scène
    // ne change jamais le résultat). Clic / Entrée / A : passer.
    private const float ReelSeconds = 4f;        // défilement
    private const float ReelHoldSeconds = 0.9f;  // arrêt sur le gagné (révélé en couleur, éclair) avant la fenêtre
    private const int ReelLength = 52;           // objets dans la bande
    private const int ReelWin = 44;              // place du gagné (il en reste derrière lui : la bande ne finit pas net)
    private const int ReelStart = 10;            // place sous le repère au départ (la bande est déjà pleine à gauche)
    private bool _rolling;
    private float _rollT;
    private int _reelTick;
    private readonly List<ChestItem> _reel = new();   // en silhouettes noires : seul le gagné se colore, à l'arrêt
    private ChestItem? _reelWon;

    private string? _reward;
    private bool _rewardUnits;
    private Rectangle _revealOk;

    private sealed class ChestItem
    {
        public required string Id;
        public required bool Commander;
        public required bool Equipment;
        public required string Asset;   // sprite du pion / commandant ; icône de l'équipement
        public bool Owned;
    }

    private readonly List<ChestItem> _chestUnits = new();
    private readonly List<ChestItem> _chestEquips = new();
    private int _chestVersion = -1;

    /// <summary>Contenu complet de chaque coffre (ce qu'il peut donner, obtenu ou non), relu après chaque ouverture.</summary>
    private void EnsureChestItems()
    {
        if (_chestVersion == _unlockVersion)
            return;
        _chestVersion = _unlockVersion;
        var config = PvpConfig.Current;
        _chestUnits.Clear();
        foreach (var (_, cls) in PvpCatalog.AllUnitClasses().Where(c => c.Class.Tier > 1)
                     .OrderBy(c => c.Class.Tier).ThenBy(c => config.CostOf(c.Class)).ThenBy(c => c.Class.Asset, StringComparer.Ordinal))
            _chestUnits.Add(new ChestItem { Id = cls.Asset, Commander = false, Equipment = false, Asset = cls.Asset });
        foreach (var c in PvpCatalog.Commanders().Where(c => !config.StartingCommanders.Contains(c.Id)))
            _chestUnits.Add(new ChestItem { Id = c.Id, Commander = true, Equipment = false, Asset = c.BaseClass.Asset });
        _chestEquips.Clear();
        foreach (var e in PvpCatalog.AllEquipment().OrderBy(e => e.Rarity).ThenBy(e => config.CostOf(e)).ThenBy(e => e.Id, StringComparer.Ordinal))
            _chestEquips.Add(new ChestItem { Id = e.Id, Commander = false, Equipment = true, Asset = e.Icon });
        _chestLeft[0] = Profile.LockedUnits().Count;
        _chestLeft[1] = Profile.LockedEquipment().Count;
        foreach (var item in _chestUnits)
            item.Owned = Profile.Owns(item.Id);
        foreach (var item in _chestEquips)
            item.Owned = Profile.Owns(item.Id);
    }

    private static int ChestCost(bool units) => units ? PvpConfig.Current.UnitChestCost : PvpConfig.Current.EquipmentChestCost;

    private readonly int[] _chestLeft = new int[2];

    /// <summary>Objets encore à débloquer dans un coffre (compté à chaque ouverture, pas à chaque frame).</summary>
    private int LeftIn(bool units) { EnsureChestItems(); return _chestLeft[units ? 0 : 1]; }

    private void OpenChests()
    {
        if (_view == View.Chests)
            return;
        CancelHeld();
        _view = View.Chests;
        _focus.Index = 0;
        Context.Sounds.Play("menu_click");
    }

    private void CloseChests()
    {
        if (_opening || _rolling)
            return;
        _revealOpen = false;
        _view = View.Build;
        _focus.Index = 0;
        Context.Sounds.Play("menu_close");
    }

    // ── Mise en page d'un panneau ────────────────────────────────────────────────

    private struct ChestLay
    {
        public Rectangle Inner, Chest, Grid, Button;
        public Point Shadow;
        public int ChestScale, Box, Pitch, PerRow, Capacity;
        public Point ContentLabel;
    }

    /// <summary>
    /// Coffre au centre et contenu dessous (maquette) quand tout le contenu y tient ; sinon coffre à gauche et
    /// contenu à droite (les coffres réels comptent des dizaines d'objets). Cases de 64 si la place le permet,
    /// sinon de 32 (icônes d'équipement natives ; pions par la couche nette, cf. DrawHalfSprite). Au-delà de la
    /// place, la dernière case dit « +N ».
    /// </summary>
    private ChestLay ChestLayout(int panel)
    {
        var full = _lay.Full;
        var p = _lay.ChestPanels[panel];
        var inset = PvpArt.PanelFrame + (full ? 19 : 9);
        var inner = new Rectangle(p.X + inset, p.Y + inset, p.Width - 2 * inset, p.Height - 2 * inset);
        var c = new ChestLay { Inner = inner, ChestScale = full ? 3 : 2 };
        var headerH = Font.LineHeight(2) + 12 + Font.LineHeight(1);
        var btnH = full ? 56 : 40;
        c.Button = new Rectangle(inner.X, inner.Bottom - btnH - 4, inner.Width, btnH);
        var top = inner.Y + headerH + (full ? 16 : 10);
        var bottom = c.Button.Y - (full ? 16 : 10);
        var chest = 64 * c.ChestScale;
        var labelH = Font.LineHeight(1) + (full ? 12 : 8);
        var count = Math.Max(_chestUnits.Count, _chestEquips.Count);

        // A. Maquette : coffre centré, contenu sous lui.
        foreach (var box in new[] { 64, 32 })
        {
            var pitch = box + (box == 64 ? 10 : 4);
            var perRow = Math.Max(1, (inner.Width + pitch - box) / pitch);
            var gridTop = top + chest - (full ? 8 : 4) + labelH;
            var rows = Math.Max(0, (bottom - gridTop + pitch - box) / pitch);
            if (rows * perRow >= count)
            {
                c.Chest = new Rectangle(inner.Center.X - chest / 2, top - (full ? 12 : 6), chest, chest);
                c.Shadow = new Point(inner.Center.X, c.Chest.Bottom - chest / 8);
                c.ContentLabel = new Point(inner.X, top + chest - (full ? 8 : 4));
                c.Grid = new Rectangle(inner.X, gridTop, inner.Width, bottom - gridTop);
                (c.Box, c.Pitch, c.PerRow, c.Capacity) = (box, pitch, perRow, rows * perRow);
                return c;
            }
        }

        // B. Coffre à gauche, contenu à droite en cases de 32.
        c.Box = 32;
        c.Pitch = full ? 36 : 34;
        var gx = inner.X + chest + (full ? 20 : 12);
        c.Chest = new Rectangle(inner.X, top + (bottom - top - chest) / 2 - chest / 10, chest, chest);
        c.Shadow = new Point(c.Chest.Center.X, c.Chest.Bottom - chest / 8);
        c.ContentLabel = new Point(gx, top);
        c.Grid = new Rectangle(gx, top + labelH, inner.Right - gx, bottom - top - labelH);
        c.PerRow = Math.Max(1, (c.Grid.Width + c.Pitch - c.Box) / c.Pitch);
        c.Capacity = c.PerRow * Math.Max(1, (c.Grid.Height + c.Pitch - c.Box) / c.Pitch);
        return c;
    }

    private void AddChestTargets()
    {
        EnsureChestItems();
        if (_rolling)
            return;
        if (_revealOpen)
        {
            Add(_revealOk, Hit.RevealOk);
            return;
        }
        for (var i = 0; i < 2; i++)
            Add(ChestLayout(i).Button, Hit.ChestOpen, i);
    }

    // ── Mise à jour ──────────────────────────────────────────────────────────────

    private void UpdateChests(float dt)
    {
        var input = Context.Input;
        UpdateWow(dt);
        if (_opening)
        {
            _openT += dt;
            if (_openT >= ChestFrames * ChestFrameSeconds)
            {
                _opening = false;
                _rolling = true;   // le coffre est ouvert : la roulette tourne
                _rollT = 0f;
                _reelTick = ReelStart;
            }
            return;
        }
        if (_rolling)
        {
            UpdateReel(dt, input);
            return;
        }

        _focus.Navigate(input);
        _pointer = _focus.Pointer(input);
#if DEBUG
        if (DebugPointer is { } forced) _pointer = forced;
#endif
        _hover = HitIndexAt(_pointer);
        if (_revealOpen && _revealT < RevealInputDelay)
            return;   // l'éclat de la révélation d'abord : pas de « CONTINUER » involontaire

        if (input.WasKeyPressed(Keys.Escape) || input.WasCancelPressed)
        {
            if (_revealOpen) CloseReveal();
            else CloseChests();
            return;
        }
        if (_revealOpen && input.WasKeyPressed(Keys.Enter))
        {
            CloseReveal();
            return;
        }
        var clicked = input.UsingGamepad
            ? (input.WasConfirmPressed && _hover >= 0 ? _targets[_hover] : (Target?)null)
            : (input.WasLeftClicked ? HitAt(input.MousePosition) : null);
        if (clicked is not { } t)
            return;
        switch (t.Kind)
        {
            case Hit.RevealOk: CloseReveal(); break;
            case Hit.ChestOpen: OpenChest(t.Index == 0); break;
            case Hit.Back: CloseChests(); break;
            case Hit.Ranking: OpenRanking(); break;
        }
    }

    private void CloseReveal()
    {
        _revealOpen = false;
        ClearWow();
        _focus.Index = 0;
        Context.Sounds.Play("menu_click");
    }

    private void OpenChest(bool units)
    {
        if (_revealOpen || _rolling || LeftIn(units) == 0 || Profile.Points < ChestCost(units))
        {
            Context.Sounds.Play("menu_close");
            return;
        }
        EnsureChestItems();
        var pool = (units ? _chestUnits : _chestEquips).ToList();   // contenu du coffre AVANT ouverture
        var item = Profile.OpenChest(units, _rng);   // points débités, objet tiré (jamais de doublon)
        if (item == null)
            return;
        BuildReel(pool, item);
        PvpProfileStore.Save();   // contenu du coffre relu seulement à la révélation : rien ne trahit le gagné avant
        _reward = item;
        _rewardUnits = units;
        _opening = true;
        _openUnits = units;
        _openT = 0f;
        Context.Sounds.Play("menu_click");
    }

    // ── Roulette ─────────────────────────────────────────────────────────────────

    /// <summary>Bande de la roulette : des objets du coffre tirés au hasard, le gagné à la place <see cref="ReelWin"/>
    /// (tous en silhouette noire pendant le défilement : seul le gagné se colore à l'arrêt).</summary>
    private void BuildReel(List<ChestItem> pool, string reward)
    {
        _reel.Clear();
        ChestItem Show(ChestItem i, bool owned) =>
            new() { Id = i.Id, Commander = i.Commander, Equipment = i.Equipment, Asset = i.Asset, Owned = owned };
        var won = pool.FirstOrDefault(i => i.Id == reward) ?? pool[0];
        var others = pool.Where(i => i.Id != reward).ToList();
        if (others.Count == 0)
            others.Add(won);
        for (var k = 0; k < ReelLength; k++)
            _reel.Add(Show(k == ReelWin ? won : others[_rng.Next(others.Count)], owned: false));
        _reelWon = Show(won, owned: true);
    }

    /// <summary>Avancée de la bande (en objets) : départ rapide, freinage de plus en plus doux jusqu'au gagné.</summary>
    private float ReelPosition()
    {
        var x = Math.Clamp(_rollT / ReelSeconds, 0f, 1f);
        var ease = 1f - (1f - x) * (1f - x) * (1f - x);
        return ReelStart + (ReelWin - ReelStart) * ease;
    }

    private void UpdateReel(float dt, ChessArmy.Engine.Input.InputManager input)
    {
        var wasSettled = _rollT >= ReelSeconds;
        // Passer : clic / Entrée / A (après un court délai, pour ne pas passer avec le clic d'ouverture).
        if (_rollT < ReelSeconds && _rollT > 0.15f
            && (input.WasLeftClicked || input.WasKeyPressed(Keys.Enter) || input.WasConfirmPressed))
            _rollT = ReelSeconds;
        else
            _rollT += dt;

        // « Tic » à chaque objet qui passe sous le repère (ralentit avec la bande).
        var tick = (int)MathF.Floor(ReelPosition() + 0.5f);   // départ à ReelStart : pas de tic parasite au lancement
        if (tick != _reelTick && _rollT < ReelSeconds)
            Context.Sounds.Play("chest_tick", 0.5f);
        _reelTick = tick;
        if (!wasSettled && _rollT >= ReelSeconds)
            OnReelSettled();   // arrêt : le gagné sort de l'ombre

        if (_rollT < ReelSeconds + ReelHoldSeconds)
            return;
        _rolling = false;
        _revealOpen = true;
        OnRevealOpened();
        _unlockVersion++;   // réserve, flèches du commandant, contenu des coffres : relus maintenant que c'est révélé
        _focus.Index = 0;
        Context.Sounds.Play("reward");
        if (!_rewardUnits && _reward != null && PvpCatalog.FindEquipment(_reward) is { Rarity: EquipmentRarity.Legendary })
            Context.Sounds.Play("reward_legendary");
    }

    private void DrawReel(SpriteBatch sb)
    {
        var L = _lay;
        PvpArt.Fill(sb, Px, new Rectangle(0, 0, L.W, L.H), Palette.Black1 * 0.8f);
        const int box = 64;
        var pitch = box + (L.Full ? 12 : 8);
        var band = new Rectangle(L.Pad, L.H / 2 - box / 2 - 14, L.W - 2 * L.Pad, box + 28);
        PvpArt.Panel(sb, Px, band);
        var inner = new Rectangle(band.X + PvpArt.PanelFrame + 4, band.Y, band.Width - 2 * (PvpArt.PanelFrame + 4), band.Height);
        var pos = ReelPosition();
        var settled = _rollT >= ReelSeconds;
        var y = band.Center.Y - box / 2;
        for (var k = 0; k < _reel.Count; k++)
        {
            var x = (int)MathF.Round(band.Center.X + (k - pos) * pitch - box / 2f);
            if (x < inner.X || x + box > inner.Right)
                continue;   // seules les cases entières dans la bande
            var r = new Rectangle(x, y, box, box);
            DrawChestBox(sb, r, settled && k == ReelWin && _reelWon != null ? _reelWon : _reel[k]);
        }

        // Repère fixe au centre : la case qui s'y arrête est gagnée.
        var mark = new Rectangle(band.Center.X - box / 2 - 4, y - 4, box + 8, box + 8);
        PvpArt.Border(sb, Px, mark, settled ? Palette.Yellow1 : Palette.Yellow2, settled ? 3 : 2);
        PvpArt.Fill(sb, Px, new Rectangle(band.Center.X - 1, band.Y - 10, 3, 10), Palette.Yellow2);
        PvpArt.Fill(sb, Px, new Rectangle(band.Center.X - 1, band.Bottom, 3, 10), Palette.Yellow2);
        DrawSettleFlash(sb, new Rectangle(band.Center.X - box / 2, y, box, box));
        DrawWowParticles(sb);
    }

    // ── Rendu ────────────────────────────────────────────────────────────────────

    private void DrawChests(SpriteBatch sb)
    {
        EnsureChestItems();
        _overlayDim = _revealOpen || _rolling || _tutorialOpen || _error != null ? 1f : 0f;
        var L = _lay;
        var full = L.Full;
        var ts = full ? 4 : 3;
        var title = Loc.T("pvp.chests");
        Font.Draw(sb, title, new Vector2(L.ChestTitle.X, L.ChestTitle.Y), ts, Palette.Yellow2);
        var subX = L.ChestTitle.X + Font.Measure(title, ts) + (full ? 18 : 12);
        var sub = Loc.T("pvp.chests_sub");
        var subRoom = L.ChestTitle.Right - subX;
        var ss = PvpArt.ScaleToFit(Font, sub, subRoom, full ? 2 : 1);
        Font.Draw(sb, PvpArt.Fit(Font, sub, subRoom, ss), new Vector2(subX, L.ChestTitle.Bottom - Font.LineHeight(ss) - (full ? 2 : 1)),
            ss, Palette.Blue1, preserveCase: true);

        for (var i = 0; i < 2; i++)
            DrawChestPanel(sb, i);

        var gains = Loc.T("pvp.gains", PvpConfig.Current.PointsPerKill, PvpConfig.Current.PointsForWin, PvpConfig.Current.PointsForDraw);
        var gy = L.ChestPanels[0].Bottom + (L.H - L.ChestPanels[0].Bottom - Font.LineHeight(1)) / 2;
        Font.DrawCentered(sb, PvpArt.Fit(Font, gains, L.W - 2 * L.Pad, 1), new Rectangle(0, gy, L.W, Font.LineHeight(1)), 1, Palette.Yellow3);

    }

    private void DrawChestPanel(SpriteBatch sb, int panel)
    {
        var units = panel == 0;
        var full = _lay.Full;
        var c = ChestLayout(panel);
        PvpArt.Panel(sb, Px, _lay.ChestPanels[panel]);
        var inner = c.Inner;
        var left = LeftIn(units);

        // En-tête : titre, « N À DÉBLOQUER », description.
        var count = left > 0 ? Loc.T("pvp.chest_left", left) : Loc.T("pvp.chest_empty");
        var countW = Font.Measure(count, 1);
        var title = Loc.T(units ? "pvp.chest_units" : "pvp.chest_equipment");
        var titleRoom = inner.Width - countW - 12;
        var tScale = PvpArt.ScaleToFit(Font, title, titleRoom, 2);
        Font.Draw(sb, PvpArt.Fit(Font, title, titleRoom, tScale), new Vector2(inner.X, inner.Y), tScale, Palette.White);
        Font.Draw(sb, count, new Vector2(inner.Right - countW, inner.Y + (Font.LineHeight(2) - Font.LineHeight(1)) / 2), 1,
            left > 0 ? Palette.Cyan1 : Palette.Grey);
        var desc = Loc.T(units ? "pvp.chest_units_desc" : "pvp.chest_equip_desc");
        Font.Draw(sb, PvpArt.Fit(Font, desc, inner.Width, 1), new Vector2(inner.X, inner.Y + Font.LineHeight(2) + 12), 1, Palette.Blue1,
            preserveCase: true);

        // Coffre ×3 (×2 en compact) sur son ombre ; vidé : dernière image, grisée. En cours d'ouverture : animé.
        PvpArt.Oval(sb, Px, c.Shadow, c.Chest.Width * 3 / 4, Math.Max(8, c.Chest.Height / 10), Palette.Black1 * 0.6f);
        if (Tex("Objects/coffreAnimate") is { } anim)
        {
            var frames = Math.Max(1, anim.Width / anim.Height);
            var frame = 0;
            var tint = Color.White;
            if (_opening && _openUnits == units)
                frame = Math.Min(frames - 1, (int)(_openT / ChestFrameSeconds));
            else if (left == 0)
            {
                frame = frames - 1;
                tint = Palette.Grey;
            }
            var fw = anim.Height;
            sb.Draw(anim, new Rectangle(c.Chest.X, c.Chest.Y, fw * c.ChestScale, fw * c.ChestScale), new Rectangle(frame * fw, 0, fw, fw), tint);
        }

        // CONTENU : une case par objet, obtenu en couleur, le reste en silhouette.
        Font.Draw(sb, Loc.T("pvp.content"), c.ContentLabel.ToVector2(), 1, Palette.Blue1);
        var items = units ? _chestUnits : _chestEquips;
        var shown = Math.Min(items.Count, c.Capacity);
        for (var k = 0; k < shown; k++)
        {
            var box = new Rectangle(c.Grid.X + k % c.PerRow * c.Pitch, c.Grid.Y + k / c.PerRow * c.Pitch, c.Box, c.Box);
            if (k == shown - 1 && shown < items.Count)
            {
                PvpArt.Fill(sb, Px, box, Palette.Black5);
                Font.DrawCentered(sb, "+" + Num(items.Count - shown + 1), box, 1, Palette.White);
                continue;
            }
            DrawChestBox(sb, box, items[k]);
        }

        // OUVRIR · prix ; grisé s'il manque des points (« IL MANQUE N POINTS ») ; COMPLET si tout est obtenu.
        var cost = ChestCost(units);
        var b = c.Button;
        var hover = Hovered(b);
        var bs = 2;
        if (left == 0)
        {
            PvpArt.PrimaryDisabled(sb, Px, b);
            Font.DrawCentered(sb, Loc.T("pvp.complete"), b, bs, Palette.Grey);
        }
        else if (Profile.Points < cost)
        {
            PvpArt.PrimaryDisabled(sb, Px, b);
            var missing = Loc.T("pvp.missing", cost - Profile.Points);
            Font.DrawCentered(sb, PvpArt.Fit(Font, missing, b.Width - 12, bs), b, bs, Palette.Purple5);
        }
        else
        {
            var dy = PvpArt.Primary(sb, Px, b, hover, hover && Context.Input.IsLeftDown);
            var label = Loc.T("pvp.open", cost);
            var coin = PvpArt.CoinSize * 2;
            var w = coin + 10 + Font.Measure(label, bs);
            var x = b.Center.X - w / 2;
            PvpArt.Coin(sb, Px, new Point(x, b.Center.Y + dy - coin / 2), 2);
            Font.Draw(sb, label, new Vector2(x + coin + 10, b.Center.Y + dy - Font.LineHeight(bs) / 2), bs, Palette.Black1);
        }
        if (!full && Context.Input.UsingGamepad && Hovered(b))
            PvpArt.Border(sb, Px, new Rectangle(b.X - 3, b.Y - 3, b.Width + 6, b.Height + 10), Palette.Yellow2, 1);
    }

    private void DrawChestBox(SpriteBatch sb, Rectangle box, ChestItem item)
    {
        var owned = item.Owned;
        PvpArt.Fill(sb, Px, box, owned ? Palette.Yellow4 : Palette.Black5);
        PvpArt.Border(sb, Px, box, owned ? Palette.Brown2 : Palette.Black1, 1);
        var tint = owned ? Color.White : Color.Black;
        if (item.Equipment)
        {
            var bg = Tex("Equipment/background");
            var icon = PvpCatalog.FindEquipment(item.Id) is { } eq ? EquipIcon(eq) : null;
            var scale = box.Width >= 64 ? 2 : 1;
            if (bg != null)
                sb.Draw(bg, new Rectangle(box.Center.X - bg.Width * scale / 2, box.Center.Y - bg.Height * scale / 2, bg.Width * scale, bg.Height * scale), owned ? Color.White : Color.White * 0.3f);
            if (icon != null)
                sb.Draw(icon, new Rectangle(box.Center.X - icon.Width * scale / 2, box.Center.Y - icon.Height * scale / 2,
                    icon.Width * scale, icon.Height * scale), tint);
            return;
        }
        var tex = UnitFront(item.Asset);
        if (box.Width >= 64)
        {
            if (tex != null)
                sb.Draw(tex, new Rectangle(box.X + (box.Width - tex.Width) / 2, box.Bottom - tex.Height, tex.Width, tex.Height), tint);
        }
        else
            DrawHalfSprite(sb, tex, box, tint);
    }

    // ── Fenêtre « NOUVEAU ! » ────────────────────────────────────────────────────
    //  En-tête centré (NOUVEAU !, objet, nom, « ÉQUIPEMENT · RARE » avec le seul mot de rareté / tier en couleur),
    //  filet, puis deux blocs alignés à gauche, chacun sous son titre gris : les bonus (équipement) ou les 4 stats
    //  en grille 2×2 (pion, commandant), une ligne encadrée par stat ; puis un encadré par trait, nom en haut,
    //  fonctionnement dessous. Plus aucun texte centré empilé sous la rareté.

    private const int RevealRowH = 32;      // ligne de stat : icône 32×32 native
    private const int RevealRowGap = 8;
    private const int RevealBoxPad = 10;    // marge intérieure d'un encadré de trait
    private const int RevealBorder = 2;
    private static readonly string[] RevealIconKeys = { "Icons/stat_pv", "Icons/stat_deg", "Icons/stat_dep", "Icons/stat_tir" };

    private sealed class RevealStat
    {
        public int Icon;
        public string Label = "", Value = "";
        public Color ValueColor;
    }

    private sealed class RevealTrait
    {
        public string Name = "";
        public Color NameColor;
        public List<string> Lines = new();
        /// <summary>Traits cités par la description, rappelés dans le même encadré (nom, fonctionnement).</summary>
        public readonly List<(string Name, List<string> Lines)> Refs = new();
    }

    private readonly HashSet<string> _revShown = new();

    private (string? Id, bool Units, Language Lang, bool Full, int H) _revKey;
    private string _revName = "", _revKindPre = "", _revKindWord = "", _revKindPost = "", _revStatTitle = "", _revTraitTitle = "";
    private Color _revNameColor, _revKindColor;
    private bool _revGrid, _revEquipment;
    private int _revArt, _revPh, _revNameScale, _revValueScale;
    private readonly List<RevealStat> _revStats = new();
    private readonly List<RevealTrait> _revTraits = new();

    private int RevealWidth => _lay.Full ? 460 : 380;
    private int RevealPad => _lay.Full ? 20 : 12;
    private int RevealMarginX => PvpArt.PanelFrame + (_lay.Full ? 22 : 12);
    private int RevealInnerW => RevealWidth - 2 * RevealMarginX;

    /// <summary>Contenu de la fenêtre (textes, stats, traits découpés, taille de l'objet, hauteur), relu seulement
    /// si l'objet, la langue ou le format change : rien n'est alloué d'une image à l'autre.</summary>
    private void RefreshReveal()
    {
        var L = _lay;
        var full = L.Full;
        var key = (_reward, _rewardUnits, Loc.Current, full, L.H);
        if (key == _revKey)
            return;
        _revKey = key;
        _revStats.Clear();
        _revTraits.Clear();
        var innerW = RevealInnerW;
        _revValueScale = full ? 2 : 1;

        var eq = _rewardUnits ? null : PvpCatalog.FindEquipment(_reward!);
        var unit = _rewardUnits ? PvpCatalog.FindClass(_reward!) : null;
        _revEquipment = eq != null;
        string kindTemplate, word = "";
        var wordColor = Palette.White;
        UnitClass? cls = null;
        if (eq != null)
        {
            _revName = EquipmentNames.Localized(eq).ToUpperInvariant();
            _revNameColor = PvpArt.RarityColor(eq.Rarity);
            kindTemplate = Loc.T("pvp.kind_equipment");
            word = Loc.T(eq.Rarity switch
            {
                EquipmentRarity.Rare => "equip.rarity.rare",
                EquipmentRarity.Legendary => "equip.rarity.legendary",
                _ => "equip.rarity.common",
            });
            wordColor = PvpArt.RarityColor(eq.Rarity);
        }
        else if (unit is { } u)
        {
            cls = u.Class;
            _revName = PvpUi.UnitName(u.Class);
            _revNameColor = Palette.Yellow2;
            kindTemplate = Loc.T("pvp.kind_unit") + " · {0}";
            word = Loc.T("pvp.reveal_tier", u.Class.Tier);
        }
        else
        {
            cls = PvpCatalog.CommanderClass(_reward!)?.Class;
            _revName = PvpUi.CommanderName(_reward!);
            _revNameColor = Palette.Yellow1;
            kindTemplate = Loc.T("pvp.commander");
        }
        var at = kindTemplate.IndexOf("{0}", StringComparison.Ordinal);
        _revKindPre = at < 0 ? kindTemplate : kindTemplate[..at];
        _revKindWord = at < 0 ? "" : word;
        _revKindPost = at < 0 ? "" : kindTemplate[(at + 3)..];
        _revKindColor = wordColor;
        _revNameScale = PvpArt.ScaleToFit(Font, _revName, innerW, full ? 3 : 2);
        _revName = PvpArt.Fit(Font, _revName, innerW, _revNameScale);

        // Bonus (équipement) : une ligne par stat modifiée, « +2 » doré. Pion / commandant : les 4 stats en 2×2, valeurs brutes.
        var traitBoxW = innerW - 2 * RevealBorder - 2 * RevealBoxPad;
        void AddTrait(UnitKeywords.Keyword kw, string trait, bool fromEquip)
        {
            if (kw.Label.Length == 0)
                return;
            var t = new RevealTrait { Name = PvpArt.Fit(Font, kw.Label, traitBoxW, 1), NameColor = fromEquip ? Palette.Yellow2 : Palette.Cyan1 };
            if (kw.Description.Length > 0)
                t.Lines = WrapCached(PvpUi.SentenceCase(TraitDescription(kw, trait)), traitBoxW);
            // Trait cité par la description (Aura de rempart → Rempart) : rappelé dans le même encadré, sous un filet
            // de couleur, sauf s'il a déjà son propre encadré.
            foreach (var r in ChessArmy.Core.Battle.Trait.ReferencedBy(trait))
                if (_revShown.Add(r))
                {
                    var rk = UnitKeywords.For(r);
                    t.Refs.Add((PvpArt.Fit(Font, rk.Label, traitBoxW, 1),
                        rk.Description.Length > 0 ? WrapCached(PvpUi.SentenceCase(TraitDescription(rk, r)), traitBoxW) : new List<string>()));
                }
            _revTraits.Add(t);
        }
        _revShown.Clear();
        if (eq != null)
        {
            foreach (var e in eq.Effects)
                if (e.Trait is { } tr)
                    _revShown.Add(tr);
        }
        else if (cls != null)
        {
            foreach (var (_, trait, _) in TraitEntries(cls, null))
                _revShown.Add(trait);
        }
        if (eq != null)
        {
            _revGrid = false;
            foreach (var e in eq.StatBonuses)
                _revStats.Add(new RevealStat
                {
                    Icon = e.Stat switch { EquipStat.Hp => 0, EquipStat.Damage => 1, EquipStat.MoveRange => 2, _ => 3 },
                    Value = EquipmentNames.Signed(e.Amount),
                    ValueColor = Palette.Yellow2,
                });
            foreach (var e in eq.Effects)
                if (e.Trait is { } tr)
                    AddTrait(UnitKeywords.For(tr), tr, true);
            if (eq.IsThrown)   // objet à lancer : brisé à la première attaque, un trait comme un autre
                AddTrait(UnitKeywords.Ephemeral, "", true);
            _revStatTitle = Loc.T("pvp.reveal_bonus");
            _revTraitTitle = Loc.T(_revTraits.Count > 1 ? "pvp.reveal_traits_given" : "pvp.reveal_trait_given");
        }
        else
        {
            _revGrid = true;
            if (cls != null)
            {
                int[] values = { cls.MaxHp, cls.Damage, cls.MoveRange, cls.AttackRange };
                for (var i = 0; i < 4; i++)
                    _revStats.Add(new RevealStat { Icon = i, Value = Num(values[i]), ValueColor = Palette.White });
                foreach (var (kw, trait, _) in TraitEntries(cls, null))
                    AddTrait(kw, trait, false);
            }
            _revStatTitle = Loc.T("pvp.reveal_stats");
            _revTraitTitle = Loc.T(_revTraits.Count > 1 ? "pvp.reveal_traits" : "pvp.trait");
        }

        // Libellés : coupés à la place laissée par l'icône et la valeur (jamais de débordement).
        var rowW = _revGrid ? (innerW - RevealRowGap) / 2 : innerW;
        foreach (var s in _revStats)
        {
            var room = rowW - 2 * RevealBorder - RevealRowH - 6 - 8 - Font.Measure(s.Value, _revValueScale) - 8;
            s.Label = PvpArt.Fit(Font, Loc.T(StatKeys[s.Icon]), Math.Max(0, room), 1);
        }

        // Taille de l'objet : ×6 / ×3 (192), ×2 (128) dès 3 traits ; réduite d'un cran tant que la fenêtre ne tient pas.
        _revArt = full ? (_revTraits.Count >= 3 ? 128 : 192) : (_revTraits.Count >= 3 ? 64 : 128);
        _revPh = RevealHeight(_revArt);
        if (_revPh > L.H - 8)   // les rappels de traits cités passent après l'objet : retirés d'abord
        {
            foreach (var t in _revTraits)
                t.Refs.Clear();
            _revPh = RevealHeight(_revArt);
        }
        while (_revPh > L.H - 8 && _revArt > 64)
        {
            _revArt -= 64;
            _revPh = RevealHeight(_revArt);
        }
    }

    private int RevealBlockGap => _lay.Full ? 12 : 8;
    private int RevealTitleGap => _lay.Full ? 6 : 4;
    private int RevealLineStep => Font.LineHeight(1) + 3;

    private int RevealTraitH(RevealTrait t)
    {
        var h = 2 * RevealBorder + 2 * RevealBoxPad + TraitBlockH(t.Lines.Count);
        foreach (var (_, lines) in t.Refs)
            h += RevealRefGap + TraitBlockH(lines.Count);
        return h;
    }

    /// <summary>Nom + fonctionnement d'un trait dans un encadré de la fenêtre.</summary>
    private int TraitBlockH(int lines) => Font.LineHeight(1) + (lines > 0 ? 5 + lines * RevealLineStep - 3 : 0);

    /// <summary>Écart entre un trait et le rappel du trait qu'il cite (même encadré).</summary>
    private const int RevealRefGap = 8;

    /// <summary>Hauteur de la fenêtre pour une taille d'objet donnée (même enchaînement que <see cref="DrawReveal"/>).</summary>
    private int RevealHeight(int art)
    {
        var full = _lay.Full;
        var lh = Font.LineHeight(1);
        var g = RevealBlockGap;
        var h = RevealPad + Font.LineHeight(2) + (full ? 10 : 6) + art + (full ? 12 : 8)
                + Font.LineHeight(_revNameScale) + (full ? 10 : 6) + lh + g + 2;
        if (_revStats.Count > 0)
        {
            var rows = _revGrid ? (_revStats.Count + 1) / 2 : _revStats.Count;
            h += g + lh + RevealTitleGap + rows * RevealRowH + (rows - 1) * RevealRowGap;
        }
        if (_revTraits.Count > 0)
        {
            h += g + lh + RevealTitleGap;
            foreach (var t in _revTraits)
                h += RevealTraitH(t);
            h += (_revTraits.Count - 1) * RevealRowGap;
        }
        return h + (full ? 16 : 10) + (full ? 48 : 36) + 4 + RevealPad;
    }

    private void DrawReveal(SpriteBatch sb)
    {
        if (_reward == null)
            return;
        RefreshReveal();
        var L = _lay;
        var full = L.Full;
        PvpArt.Fill(sb, Px, new Rectangle(0, 0, L.W, L.H), Palette.Black1 * 0.8f);

        var pw = RevealWidth;
        var ph = _revPh;
        var art = _revArt;
        var lh = Font.LineHeight(1);
        var g = RevealBlockGap;
        var btnH = full ? 48 : 36;
        var panel = new Rectangle((L.W - pw) / 2, (L.H - ph) / 2 + RevealDropOffset(), pw, ph);
        var headY = RevealPad + Font.LineHeight(2) + (full ? 10 : 6);
        var artCenter = new Point(panel.Center.X, (L.H - ph) / 2 + headY + art / 2);
        DrawRays(sb, artCenter);   // rayons derrière la fenêtre, centrés sur l'objet
        SpawnPendingBurst(ref _revealBurstPending, artCenter, reveal: true);
        PvpArt.Panel(sb, Px, panel);
        var y = panel.Y + RevealPad;
        Font.DrawCentered(sb, Loc.T("pvp.new"), new Rectangle(panel.X, y, pw, Font.LineHeight(2)), 2, Palette.Cyan1);
        y = panel.Y + headY;

        // L'objet : équipement = fond + icône (×6 / ×4) ; pion / commandant = sprite de face (×3 / ×2).
        var artRect = new Rectangle(panel.Center.X - art / 2, y, art, art);
        if (_revEquipment && PvpCatalog.FindEquipment(_reward) is { } eq)
        {
            var scale = art / 32;
            if (Tex("Equipment/background") is { } bg)
                sb.Draw(bg, new Rectangle(artRect.X, artRect.Y, bg.Width * scale, bg.Height * scale), Color.White);
            if (EquipIcon(eq) is { } icon)
                sb.Draw(icon, new Rectangle(artRect.Center.X - icon.Width * scale / 2, artRect.Center.Y - icon.Height * scale / 2,
                    icon.Width * scale, icon.Height * scale), Color.White);
            PvpArt.Border(sb, Px, new Rectangle(artRect.X - 3, artRect.Y - 3, art + 6, art + 6), Palette.Black1, 3);
        }
        else
        {
            var asset = PvpCatalog.FindClass(_reward)?.Class.Asset ?? Commandes.ById(_reward)?.BaseClass.Asset ?? _reward;
            if (UnitFront(asset) is { } tex)
            {
                var scale = art / 64;
                sb.Draw(tex, new Rectangle(artRect.Center.X - tex.Width * scale / 2, artRect.Bottom - tex.Height * scale,
                    tex.Width * scale, tex.Height * scale), Color.White);
            }
        }
        y += art + (full ? 12 : 8);

        // Nom, puis « ÉQUIPEMENT · RARE » : seul le mot de rareté (ou « TIER 3 ») en couleur.
        Font.DrawCentered(sb, _revName, new Rectangle(panel.X, y, pw, Font.LineHeight(_revNameScale)), _revNameScale, _revNameColor);
        y += Font.LineHeight(_revNameScale) + (full ? 10 : 6);
        var w1 = Font.Measure(_revKindPre, 1);
        var w2 = Font.Measure(_revKindWord, 1);
        var kx = panel.Center.X - (w1 + w2 + Font.Measure(_revKindPost, 1)) / 2;
        Font.Draw(sb, _revKindPre, new Vector2(kx, y), 1, Palette.Blue1);
        if (_revKindWord.Length > 0)
        {
            Font.Draw(sb, _revKindWord, new Vector2(kx + w1, y), 1, _revKindColor);
            Font.Draw(sb, _revKindPost, new Vector2(kx + w1 + w2, y), 1, Palette.Blue1);
        }
        y += lh + g;

        // Filet sur toute la largeur intérieure.
        var x0 = panel.X + RevealMarginX;
        var innerW = RevealInnerW;
        PvpArt.Fill(sb, Px, new Rectangle(x0, y, innerW, 2), Palette.Black5);
        y += 2;

        // Bloc des bonus / statistiques.
        if (_revStats.Count > 0)
        {
            y += g;
            Font.Draw(sb, PvpArt.Fit(Font, _revStatTitle, innerW, 1), new Vector2(x0, y), 1, Palette.Blue1);
            y += lh + RevealTitleGap;
            var rowW = _revGrid ? (innerW - RevealRowGap) / 2 : innerW;
            for (var i = 0; i < _revStats.Count; i++)
            {
                var col = _revGrid ? i % 2 : 0;
                var row = _revGrid ? i / 2 : i;
                var x = col == 0 ? x0 : x0 + innerW - rowW;
                DrawRevealStat(sb, new Rectangle(x, y + row * (RevealRowH + RevealRowGap), rowW, RevealRowH), _revStats[i]);
            }
            var rows = _revGrid ? (_revStats.Count + 1) / 2 : _revStats.Count;
            y += rows * RevealRowH + (rows - 1) * RevealRowGap;
        }

        // Bloc des traits : un encadré par trait, nom en haut, fonctionnement dessous.
        if (_revTraits.Count > 0)
        {
            y += g;
            Font.Draw(sb, PvpArt.Fit(Font, _revTraitTitle, innerW, 1), new Vector2(x0, y), 1, Palette.Blue1);
            y += lh + RevealTitleGap;
            foreach (var t in _revTraits)
            {
                var box = new Rectangle(x0, y, innerW, RevealTraitH(t));
                PvpArt.Fill(sb, Px, box, Palette.Black3);
                PvpArt.Border(sb, Px, box, Palette.Black5, RevealBorder);
                var tx = box.X + RevealBorder + RevealBoxPad;
                var ty = box.Y + RevealBorder + RevealBoxPad;
                Font.Draw(sb, t.Name, new Vector2(tx, ty), 1, t.NameColor);
                ty += lh + 5;
                foreach (var line in t.Lines)
                {
                    Font.Draw(sb, line, new Vector2(tx, ty), 1, Palette.White, preserveCase: true);
                    ty += RevealLineStep;
                }
                var by = box.Y + RevealBorder + RevealBoxPad + TraitBlockH(t.Lines.Count);
                foreach (var (refName, refLines) in t.Refs)   // rappel du trait cité : autre couleur, texte en retrait de ton
                {
                    ty = by + RevealRefGap;
                    by = ty + TraitBlockH(refLines.Count);
                    Font.Draw(sb, refName, new Vector2(tx, ty), 1, UnitKeywords.ReminderColor);
                    ty += lh + 5;
                    foreach (var line in refLines)
                    {
                        Font.Draw(sb, line, new Vector2(tx, ty), 1, Palette.Blue1, preserveCase: true);
                        ty += RevealLineStep;
                    }
                }
                y += box.Height + RevealRowGap;
            }
            y -= RevealRowGap;
        }

        y += full ? 16 : 10;
        _revealOk = new Rectangle(panel.Center.X - (full ? 120 : 90), y, full ? 240 : 180, btnH);
        var hover = Hovered(_revealOk) || Context.Input.UsingGamepad;
        var dy = PvpArt.Primary(sb, Px, _revealOk, hover, hover && Context.Input.IsLeftDown && !Context.Input.UsingGamepad);
        var ok = (Context.Input.UsingGamepad ? "(A) " : "") + Loc.T("pvp.continue");
        Font.DrawCentered(sb, ok, new Rectangle(_revealOk.X, _revealOk.Y + dy, _revealOk.Width, _revealOk.Height), 2, Palette.Black1);
        DrawWowParticles(sb);
        DrawRevealFlash(sb);
    }

    /// <summary>Ligne de stat encadrée : icône 32×32 native à gauche, libellé gris, valeur calée à droite.</summary>
    private void DrawRevealStat(SpriteBatch sb, Rectangle r, RevealStat s)
    {
        PvpArt.Fill(sb, Px, r, Palette.Black3);
        PvpArt.Border(sb, Px, r, Palette.Black5, RevealBorder);
        if (Tex(RevealIconKeys[s.Icon]) is { } icon)
            sb.Draw(icon, new Vector2(r.X, r.Y + (r.Height - icon.Height) / 2), Color.White);
        var lh = Font.LineHeight(1);
        Font.Draw(sb, s.Label, new Vector2(r.X + RevealRowH + 6, r.Y + (r.Height - lh) / 2), 1, Palette.Blue1);
        var vs = _revValueScale;
        Font.Draw(sb, s.Value, new Vector2(r.Right - RevealBorder - 8 - Font.Measure(s.Value, vs), r.Y + (r.Height - Font.LineHeight(vs)) / 2),
            vs, s.ValueColor);
    }

    /// <summary>Fonctionnement d'un trait, avec la valeur de base du moteur à la place de « {0} » (pas de bonus d'arbre en multi).</summary>
    private static string TraitDescription(UnitKeywords.Keyword kw, string trait)
    {
        var value = trait switch
        {
            ChessArmy.Core.Battle.Trait.Rempart => Match.BaseRempartReduction,
            ChessArmy.Core.Battle.Trait.Formation => Match.BaseFormationBonus,
            ChessArmy.Core.Battle.Trait.Impact => Match.BaseImpactDamage,
            ChessArmy.Core.Battle.Trait.TueurDeGeants => Match.BaseGiantSlayerBonus,
            _ => (int?)null,
        };
        return value is { } v && kw.Description.Contains("{0}") ? string.Format(kw.Description, v) : kw.Description;
    }
}
