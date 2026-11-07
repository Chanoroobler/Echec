using System;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Pvp;
using ChessArmy.Engine;
using ChessArmy.Engine.UI;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.UI.Text;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Fiche posée SUR le plateau (4 rangées vides juste au-dessus des lignes du joueur, cf. <see cref="LayoutSheet"/>),
/// ouverte seulement au survol ou en tenant quelque chose :
///  • pion / commandant : en-tête d'une ligne (sprite 32 net · NOM · coût, motif de déplacement à droite), puis trois
///    colonnes : stats compactes (icônes 16 px) et équipement porté | plateau 7×7 animé | fonctionnement des traits ;
///  • équipement : en-tête (case 32 · NOM · rareté · coût), effets et « sur ce pion » à gauche, traits à droite.
/// Icônes et sprites petits à dessein : toute la place va aux descriptions. Tout est mis en cache (_sheetKey) :
/// aucune allocation par frame.
/// </summary>
public sealed partial class PvpArmyScene
{
    private const int SheetStatsW = 124;    // colonne des stats (et de l'équipement porté)
    private const int SheetStatPitch = 20;  // une ligne de stat (icône 16 px)
    private const int SheetIcon = 16;       // icônes de stat à moitié de leur taille native (32)
    private const int SheetSprite = 32;     // sprite d'en-tête à moitié de sa taille native (64)
    private const int SheetEffectRowH = 20;

    private int SheetColumnGap => _lay.Full ? 16 : 10;
    private int SheetHeadH => Math.Max(SheetSprite, Font.LineHeight(2));

    /// <summary>Corps de la fiche : sous l'en-tête et son filet.</summary>
    private Rectangle SheetBodyRect(Rectangle area)
    {
        var y = area.Y + SheetHeadH + 13;
        return new Rectangle(area.X, y, area.Width, Math.Max(0, area.Bottom - y));
    }

    private int SheetGridX(Rectangle area) => area.X + SheetStatsW + SheetColumnGap;
    private int SheetTextX(Rectangle area) => SheetGridX(area) + MoveAttackGrid.BoardOuter + SheetColumnGap;

    /// <summary>Colonne de gauche d'une fiche d'équipement (case, effets, « sur ce pion ») : le reste va aux traits.</summary>
    private int EquipColRight(Rectangle area) => area.X + Math.Min(area.Width / 2, _lay.Full ? 180 : 170);

    private string _sheetName = "", _sheetCost = "", _sheetMove = "";
    private int _sheetNameScale = 1;

    /// <summary>Sprite / icône posé à MOITIÉ de sa taille : couche écran (chaque pixel reste net) si possible,
    /// sinon réduit dans le canvas (tuto ou erreur par-dessus, écran ×1).</summary>
    private void HalfSprite(SpriteBatch sb, Texture2D tex, Rectangle canvas)
    {
        if (NativeSprites.CanQueue && !_tutorialOpen && _error == null)
            NativeSprites.Queue(tex, canvas, Color.White);
        else
            sb.Draw(tex, canvas, Color.White);
    }

    /// <summary>En-tête commun : NOM (échelle 2, réduite si besoin) puis un texte jaune, un texte calé à droite.</summary>
    private void FitSheetName(string name, int room)
    {
        _sheetNameScale = PvpArt.ScaleToFit(Font, name, Math.Max(20, room), 2);
        _sheetName = PvpArt.Fit(Font, name, Math.Max(20, room), _sheetNameScale);
    }

    // ── Pion / commandant ────────────────────────────────────────────────────────

    private void RefreshUnitSheet(UnitClass cls, Domaine dom, Equipment? eq, bool isCommander, Rectangle area)
    {
        var key = (cls.Asset, eq?.Id, (string?)null, isCommander ? 1 : 0, Loc.Current, _lay.Full, area);
        if (key == _sheetKey)
            return;
        _sheetKey = key;

        // Stats totales (classe multi + équipement), bonus rappelé à part en « +5 ».
        int[] baseV = { cls.MaxHp, cls.Damage, cls.MoveRange, cls.AttackRange };
        EquipStat[] stats = { EquipStat.Hp, EquipStat.Damage, EquipStat.MoveRange, EquipStat.AttackRange };
        for (var i = 0; i < 4; i++)
        {
            var bonus = eq?.BonusFor(stats[i]) ?? 0;
            _sheetValues[i] = Num(Math.Max(0, baseV[i] + bonus));
            _sheetBonus[i] = bonus == 0 ? "" : EquipmentNames.Signed(bonus);
            _sheetBoosted[i] = bonus != 0;
        }
        bool Has(string t) => cls.Traits.Contains(t) || (eq != null && eq.GrantsTrait(t));
        _sheetGrid = (cls.MoveRange + (eq?.BonusFor(EquipStat.MoveRange) ?? 0),
            Has(Trait.RepositionnementStrategique), Has(Trait.AttaqueLibre),
            cls.AttackRange + (eq?.BonusFor(EquipStat.AttackRange) ?? 0),
            Math.Max(cls.MinAttackRange, Has(Trait.ZoneMorte) ? 2 : 1));
        // Libellés : en entier s'ils tiennent tous, sinon aucun (pas de « PUIS… » à côté de « PV »).
        _sheetValueScale = 1;
        _sheetLabels = true;
        for (var i = 0; i < 4; i++)
        {
            _sheetLabel[i] = Loc.T(StatKeys[i]);
            if (SheetIcon + 4 + Font.Measure(_sheetLabel[i], 1) + 6 + StatValueW(i) + 4 > SheetStatsW)
                _sheetLabels = false;
        }

        // En-tête : nom, coût (case fixe pour le commandant), motif de déplacement à droite.
        string name;
        if (isCommander)
        {
            var cost = PvpConfig.Current.CommanderCostOf(Build.CommanderId);
            name = PvpUi.CommanderName(Build.CommanderId);
            _sheetCost = cost == 0 ? Loc.T("pvp.free_fixed") : Loc.T("pvp.cost_fixed", cost);
        }
        else
        {
            name = PvpUi.UnitName(cls);
            _sheetCost = Loc.T("pvp.cost", PvpConfig.Current.CostOf(cls));
        }
        _sheetMove = Loc.T("pvp.move." + dom.ToString().ToLowerInvariant());
        var room = area.Width - SheetSprite - 8 - 10 - Font.Measure(_sheetCost, 1) - 16 - Font.Measure(_sheetMove, 1);
        FitSheetName(name.ToUpperInvariant(), room);

        // Équipement porté : encart sous les stats (colonne de gauche) s'il y tient, sinon en tête des traits.
        var body = SheetBodyRect(area);
        var tx = SheetTextX(area);
        var tw = Math.Max(0, area.Right - tx);
        _sheetEquipLines.Clear();
        _sheetEquipH = 0;
        _sheetEquipBox = Rectangle.Empty;
        var traitsTop = body.Y;
        if (eq != null)
        {
            var statsBottom = body.Y + 4 * SheetStatPitch;
            EquipBoxLines(eq, SheetStatsW);
            if (statsBottom + 4 + _sheetEquipH <= body.Bottom && !_sheetEquipLines.Exists(l => l.Text.EndsWith('…')))
                _sheetEquipBox = new Rectangle(area.X, statsBottom + 4, SheetStatsW, _sheetEquipH);
            else
            {
                EquipBoxLines(eq, tw);
                _sheetEquipBox = new Rectangle(tx, body.Y, tw, _sheetEquipH);
                traitsTop += _sheetEquipH + 6;
            }
        }

        // Fonctionnement de chaque trait (classe en cyan, équipement en doré), rappel des traits cités.
        _sheetZone = new Rectangle(tx, traitsTop, Math.Max(0, tw - 2), Math.Max(0, body.Bottom - traitsTop));
        _sheetParas.Clear();
        _sheetReminders.Clear();
        var entries = TraitEntries(cls, eq);
        _sheetShown.Clear();
        foreach (var (_, trait, _) in entries)
            _sheetShown.Add(trait);
        foreach (var (kw, trait, fromEquip) in entries)
        {
            AddTraitLines(kw, trait, fromEquip ? Palette.Yellow2 : Palette.Cyan1);
            AddTraitReminders(trait);
        }
        FitSheetLines();
    }

    /// <summary>Lignes de l'encart d'équipement porté (badge 32 à gauche) : nom en couleur de rareté, puis effet.</summary>
    private void EquipBoxLines(Equipment eq, int boxW)
    {
        _sheetEquipLines.Clear();
        var textW = Math.Max(20, boxW - 6 - 32 - 6 - 4);
        foreach (var line in WrapCached(EquipmentNames.Localized(eq).ToUpperInvariant(), textW))
            _sheetEquipLines.Add((line, PvpArt.RarityColor(eq.Rarity)));
        var effect = EquipEffectSummary(eq);
        if (effect.Length > 0)
            foreach (var line in WrapCached(effect, textW))
                _sheetEquipLines.Add((line, Palette.White));
        _sheetEquipH = Math.Max(32, _sheetEquipLines.Count * SheetStep - 4) + 12;
    }

    private void DrawUnitSheet(SpriteBatch sb, Rectangle area, UnitClass cls, Domaine dom, Equipment? eq, bool isCommander)
    {
        RefreshUnitSheet(cls, dom, eq, isCommander, area);
        var lh = Font.LineHeight(1);
        var headH = SheetHeadH;

        // En-tête : sprite de face 32 px · NOM · coût · … · motif de déplacement ; filet dessous.
        if (UnitFront(cls.Asset) is { } front)
            HalfSprite(sb, front, new Rectangle(area.X, area.Y + (headH - SheetSprite) / 2, front.Width / 2, front.Height / 2));
        var x = area.X + SheetSprite + 8;
        var nh = Font.LineHeight(_sheetNameScale);
        Font.Draw(sb, _sheetName, new Vector2(x, area.Y + (headH - nh) / 2), _sheetNameScale, Palette.White);
        x += Font.Measure(_sheetName, _sheetNameScale) + 10;
        Font.Draw(sb, _sheetCost, new Vector2(x, area.Y + (headH - lh) / 2), 1, Palette.Yellow2);
        Font.Draw(sb, _sheetMove, new Vector2(area.Right - Font.Measure(_sheetMove, 1), area.Y + (headH - lh) / 2), 1, Palette.Blue1);
        PvpArt.Fill(sb, Px, new Rectangle(area.X, area.Y + headH + 6, area.Width, 1), Palette.Black5);

        var body = SheetBodyRect(area);
        var gx = SheetGridX(area);
        var tx = SheetTextX(area);
        PvpArt.Fill(sb, Px, new Rectangle(gx - SheetColumnGap / 2 - 1, body.Y, 1, body.Height), Palette.Black5);
        PvpArt.Fill(sb, Px, new Rectangle(tx - SheetColumnGap / 2 - 1, body.Y, 1, body.Height), Palette.Black5);

        // a) Stats compactes : icône 16, libellé gris, « +N » doré, valeur à droite (dorée si un objet la modifie).
        for (var i = 0; i < 4; i++)
        {
            var row = new Rectangle(area.X, body.Y + i * SheetStatPitch, SheetStatsW, SheetStatPitch - 2);
            PvpArt.Fill(sb, Px, row, Palette.Black3);
            if (Tex(StatIcons[i]) is { } icon)
                HalfSprite(sb, icon, new Rectangle(row.X + 1, row.Y + (row.Height - SheetIcon) / 2, SheetIcon, SheetIcon));
            var ty = row.Y + (row.Height - lh) / 2;
            if (_sheetLabels)
                Font.Draw(sb, _sheetLabel[i], new Vector2(row.X + SheetIcon + 4, ty), 1, Palette.Blue1);
            var vx = row.Right - 3 - Font.Measure(_sheetValues[i], 1);
            Font.Draw(sb, _sheetValues[i], new Vector2(vx, ty), 1, _sheetBoosted[i] ? Palette.Yellow2 : Palette.White);
            if (_sheetBonus[i].Length > 0)
                Font.Draw(sb, _sheetBonus[i], new Vector2(vx - 5 - Font.Measure(_sheetBonus[i], 1), ty), 1, Palette.Yellow2);
        }

        // d) Équipement porté : badge 32, nom en couleur de rareté, effet.
        if (eq != null)
        {
            var box = _sheetEquipBox;
            PvpArt.Fill(sb, Px, box, Palette.Black3);
            PvpArt.Border(sb, Px, box, Palette.Brown1, 1);
            DrawBadge(sb, new Rectangle(box.X + 6, box.Y + 6, 32, 32), eq, 1f, false);
            var textH = _sheetEquipLines.Count * SheetStep - 4;
            var ty = box.Y + 6 + Math.Max(0, (32 - textH) / 2);
            foreach (var (text, color) in _sheetEquipLines)
            {
                Font.Draw(sb, text, new Vector2(box.X + 6 + 32 + 6, ty), 1, color);
                ty += SheetStep;
            }
        }

        // b) Plateau 7×7 animé : le pion parcourt ses cases de déplacement, le tir en rouge.
        var g = _sheetGrid;
        MoveAttackGrid.DrawBoardWithPawn(sb, Px, new Rectangle(gx, body.Y, MoveAttackGrid.BoardOuter, MoveAttackGrid.BoardOuter),
            UnitFront(cls.Asset), revealed: true, dom, g.Move, g.Reposition,
            g.FreeAttack ? Domaine.Dame : cls.AttackDomaine ?? dom, g.Range, g.MinRange,
            allowNative: !_tutorialOpen && _error == null);

        // c) Traits, sur toute la hauteur.
        DrawSheetLines(sb);
    }

    // ── Équipement ───────────────────────────────────────────────────────────────

    private string _eqName = "", _eqRarity = "", _eqCost = "", _eqOnUnit = "";
    private int _eqNameScale = 1;
    private readonly System.Collections.Generic.List<(string Label, string Value)> _eqEffects = new();
    /// <summary>« sur ce pion » (badge survolé) : stat, valeur sans l'objet, valeur avec.</summary>
    private readonly System.Collections.Generic.List<(string Label, string From, string To)> _eqOnUnitRows = new();

    private void RefreshEquipSheet(Rectangle area, Equipment eq, bool locked, UnitClass? carrier)
    {
        var key = ("#eq", (string?)eq.Id, carrier?.Asset, locked ? 3 : 2, Loc.Current, _lay.Full, area);
        if (key == _sheetKey)
            return;
        _sheetKey = key;
        _eqCost = Loc.T("pvp.cost", PvpConfig.Current.CostOf(eq));
        _eqRarity = Loc.T(eq.Rarity switch
        {
            EquipmentRarity.Rare => "equip.rarity.rare",
            EquipmentRarity.Legendary => "equip.rarity.legendary",
            _ => "equip.rarity.common",
        });
        var room = area.Width - 32 - 8 - 10 - Font.Measure(_eqRarity, 1) - 16 - Font.Measure(_eqCost, 1);
        var name = EquipmentNames.Localized(eq).ToUpperInvariant();
        _eqNameScale = PvpArt.ScaleToFit(Font, name, Math.Max(20, room), 2);
        _eqName = PvpArt.Fit(Font, name, Math.Max(20, room), _eqNameScale);

        // Une ligne compacte par effet : « PV +5 », « TRAIT Rempart ».
        var colW = EquipColRight(area) - area.X;
        _eqEffects.Clear();
        foreach (var e in eq.Effects)
        {
            if (e.Trait is { } tr)
                _eqEffects.Add((Loc.T("pvp.trait"), UnitKeywords.For(tr).Label));
            else
                _eqEffects.Add((StatLabel(e.Stat), EquipmentNames.Signed(e.Amount)));
        }
        for (var i = 0; i < _eqEffects.Count; i++)
        {
            var (label, value) = _eqEffects[i];
            value = PvpArt.Fit(Font, value, colW - 16 - Math.Min(Font.Measure(value, 1), colW / 2), 1);
            label = PvpArt.Fit(Font, label, colW - 16 - Font.Measure(value, 1) - 8, 1);
            _eqEffects[i] = (label, value);
        }

        // Badge d'un pion posé : ce que l'objet change SUR CE PION (« PV 13 → 18 »).
        _eqOnUnitRows.Clear();
        _eqOnUnit = "";
        if (carrier != null)
        {
            _eqOnUnit = PvpArt.Fit(Font, Loc.T("pvp.on_this_unit"), colW, 1);
            int[] baseV = { carrier.MaxHp, carrier.Damage, carrier.MoveRange, carrier.AttackRange };
            EquipStat[] stats = { EquipStat.Hp, EquipStat.Damage, EquipStat.MoveRange, EquipStat.AttackRange };
            for (var i = 0; i < 4; i++)
                if (eq.BonusFor(stats[i]) is var bonus and not 0)
                    _eqOnUnitRows.Add((PvpArt.Fit(Font, Loc.T(StatKeys[i]), colW - 16 - Font.Measure("00 → 00", 1) - 8, 1),
                        Num(baseV[i]), Num(Math.Max(0, baseV[i] + bonus))));
        }

        var body = SheetBodyRect(area);
        var zx = EquipColRight(area) + SheetColumnGap;
        _sheetZone = new Rectangle(zx, body.Y, Math.Max(0, area.Right - zx - 2), body.Height);
        _sheetParas.Clear();
        _sheetReminders.Clear();
        if (locked)
            _sheetParas.Add((Loc.T("pvp.locked_equip"), Palette.Purple5, 0, default));
        _sheetShown.Clear();
        foreach (var t in eq.Traits)
            _sheetShown.Add(t);
        foreach (var t in eq.Traits)
        {
            AddTraitLines(UnitKeywords.For(t), t, Palette.Cyan1);
            AddTraitReminders(t);
        }
        if (eq.IsThrown)
            AddTraitLines(UnitKeywords.Ephemeral, "", Palette.Cyan1);
        FitSheetLines();
    }

    private void DrawEquipSheet(SpriteBatch sb, Rectangle area, Equipment eq, bool locked, UnitClass? carrier)
    {
        RefreshEquipSheet(area, eq, locked, carrier);
        var rarity = PvpArt.RarityColor(eq.Rarity);
        var lh = Font.LineHeight(1);
        var headH = SheetHeadH;

        // En-tête : case 32 (fond + icône natifs, liseré de rareté) · NOM · rareté · … · coût ; filet dessous.
        var sq = new Rectangle(area.X, area.Y + (headH - 32) / 2, 32, 32);
        if (Tex("Equipment/background") is { } bg)
            sb.Draw(bg, sq, Color.White);
        if (EquipIcon(eq) is { } icon)
            sb.Draw(icon, new Rectangle(sq.Center.X - icon.Width / 2, sq.Center.Y - icon.Height / 2, icon.Width, icon.Height), Color.White);
        PvpArt.Border(sb, Px, sq, rarity, 1);
        if (locked)
            PvpArt.Fill(sb, Px, sq, Palette.Black1 * 0.55f);
        var x = sq.Right + 8;
        var nh = Font.LineHeight(_eqNameScale);
        Font.Draw(sb, _eqName, new Vector2(x, area.Y + (headH - nh) / 2), _eqNameScale, rarity);
        x += Font.Measure(_eqName, _eqNameScale) + 10;
        Font.Draw(sb, _eqRarity, new Vector2(x, area.Y + (headH - lh) / 2), 1, rarity);
        Font.Draw(sb, _eqCost, new Vector2(area.Right - Font.Measure(_eqCost, 1), area.Y + (headH - lh) / 2), 1, Palette.Yellow2);
        PvpArt.Fill(sb, Px, new Rectangle(area.X, area.Y + headH + 6, area.Width, 1), Palette.Black5);

        // À gauche : une ligne par effet, puis « sur ce pion » ; à droite : fonctionnement des traits.
        var body = SheetBodyRect(area);
        var colRight = EquipColRight(area);
        PvpArt.Fill(sb, Px, new Rectangle(colRight + SheetColumnGap / 2 - 1, body.Y, 1, body.Height), Palette.Black5);
        var y = body.Y;
        foreach (var (label, value) in _eqEffects)
        {
            var r = new Rectangle(area.X, y, colRight - area.X, SheetEffectRowH);
            PvpArt.Fill(sb, Px, r, Palette.Black3);
            var ty = r.Y + (r.Height - lh) / 2;
            Font.Draw(sb, label, new Vector2(r.X + 6, ty), 1, Palette.Blue1);
            Font.Draw(sb, value, new Vector2(r.Right - 6 - Font.Measure(value, 1), ty), 1, Palette.Yellow2);
            y += SheetEffectRowH + 2;
        }
        if (_eqOnUnitRows.Count > 0 && y + 6 + lh + _eqOnUnitRows.Count * SheetStep <= body.Bottom)
        {
            y += 6;
            Font.Draw(sb, _eqOnUnit, new Vector2(area.X, y), 1, Palette.Blue1);
            y += lh + 5;
            foreach (var (label, from, to) in _eqOnUnitRows)
            {
                Font.Draw(sb, label, new Vector2(area.X + 6, y), 1, Palette.Blue1);
                var toX = colRight - 6 - Font.Measure(to, 1);
                Font.Draw(sb, to, new Vector2(toX, y), 1, Palette.Yellow2);
                var arrowX = toX - 4 - Font.Measure("→", 1);
                Font.Draw(sb, "→", new Vector2(arrowX, y), 1, Palette.Blue1);
                Font.Draw(sb, from, new Vector2(arrowX - 4 - Font.Measure(from, 1), y), 1, Palette.White);
                y += SheetStep;
            }
        }
        DrawSheetLines(sb);
    }
}
