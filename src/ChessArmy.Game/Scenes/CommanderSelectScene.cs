using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Campaign;
using ChessArmy.Engine;
using ChessArmy.Engine.Input;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Rendering;
using ChessArmy.Engine.Scenes;
using ChessArmy.Engine.UI;
using ChessArmy.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace ChessArmy.Game.Scenes;

/// <summary>
/// Écran de CRÉATION DE PARTIE, intercalé entre le menu principal et le jeu quand le joueur démarre une
/// nouvelle campagne dans un slot vide (une reprise va directement au jeu). Il fixe les deux choix qui
/// valent pour TOUTE la run et sont ensuite persistés avec elle : le COMMANDANT et la DIFFICULTÉ.
///
/// Présentation en VITRINE, pas en formulaire : le commandant occupe le centre, son sprite agrandi ×3
/// (facteur ENTIER — le pixel-art ne doit jamais être mis à l'échelle fractionnairement), encadré par les
/// flèches du carrousel, avec son nom en gros dessous. Ses chiffres sont répartis dans deux sous-panneaux
/// qui flanquent le portrait (stats à gauche, armée &amp; points à droite), ses pions de départ en bande
/// centrée, et la barre du bas porte la difficulté, l'arbre puis les deux actions.
///
/// MISE EN PAGE ÉLASTIQUE : le bloc du haut est ancré en HAUT, le pied en BAS, et la bande des pions de
/// départ se CENTRE dans ce qui reste. L'écran remplit donc aussi bien le canevas 1280×720 (1440p) que le
/// 960×540 (1080p, le plus serré) sans laisser un grand vide d'un côté.
///
/// Souris, clavier et manette, sur le modèle de <see cref="CodexView"/> : géométrie déterministe recalculée
/// en tête d'Update et de Draw, focusables reconstruits à chaque image, navigation par rangées.
/// </summary>
public sealed class CommanderSelectScene : Scene
{
    // Éléments focusables (navigation manette par rangées).
    private enum Kind { CommanderPrev, CommanderNext, StartTile, DiffLevel, Tree, Back, Start }

    private const int Margin = 8;
    /// <summary>TOUS les pions du carrousel sont à cette échelle : 64 → 192 px, facteur ENTIER.</summary>
    private const int SpriteScale = 3;
    private const int SpriteBox = 64 * SpriteScale;
    private const int PortraitPad = 10;         // zone de survol autour du pion central
    private const int SlotSpacing = 224;        // écart entre deux emplacements (192 + 32 de respiration)
    private const int ArrowW = 40, ArrowH = 56;
    private const int ArrowOffset = SlotSpacing + SpriteBox / 2 + 20 + ArrowW / 2;  // centre → axe de la flèche
    private const int PanelW = 260;             // sous-panneaux de stats (superposés, apparaissent au survol)
    private const int PanelGap = 16;
    private const int DomaineBadge = 39;
    private const float SlideDuration = 0.22f;

    /// <summary>
    /// Perte de luminosité par emplacement d'écart avec le centre. Comme la TAILLE ne change plus (les
    /// facteurs d'échelle doivent rester entiers, donc non interpolables), c'est ce fondu — lui, continu —
    /// qui désigne le pion choisi et fait disparaître celui qui sort, au lieu de le laisser déborder.
    /// </summary>
    private const float FadePerSlot = 0.55f;
    private int NameH => Context.Font.LineHeight(3);   // hauteur du nom (scale 3) : 21 latin / 36 cjk (police active)
    private const int TileGap = 10;
    private const int BtnH = 38;
    private const int BackW = 150, StartW = 220;
    private const int TreeIcon = 32;            // icône d'arbre, coin haut-droit du sprite du commandant
    private const int DiffBtn = 32, DiffGap = 6;
    private const int BarH = 32;
    private const int LineH = 11;               // interligne des textes à l'échelle 1
    private const int BodyIndent = 10;          // retrait du corps de texte sous son titre
    private const int Pad = 10;                 // marge interne des sous-panneaux

    private readonly int _saveSlot;

    private UnitCardRenderer _card = null!;
    private CommandTreeView _tree = null!;

    /// <summary>Icône d'accès à l'arbre (Assets/UI/arbre.png). Null = absente, on dessine un repli.</summary>
    private Texture2D? _treeIcon;

    /// <summary>Run d'APERÇU : ne sert qu'à porter l'arbre du commandant courant (0 point → rien d'achetable).</summary>
    private Run _preview = null!;

    private IReadOnlyList<CommandeDef> _commanders = Array.Empty<CommandeDef>();
    private int _index;
    private int _difficultyIndex;

    private int _focus;
    private readonly List<(Rectangle Rect, Kind Kind, int Data)> _focusables = new();

    // Animation du carrousel : _slideT va de 1 (on vient de changer) à 0 (posé), _slideDir = sens du pas.
    // Le dessin décale tous les emplacements de _slideDir * SlotSpacing * ease(_slideT), si bien que le
    // nouveau commandant part de la place qu'il occupait AVANT le pas et glisse jusqu'au centre.
    private float _slideT;
    private int _slideDir;

    /// <param name="saveSlot">Slot (0..2) dans lequel la nouvelle campagne sera sauvegardée.</param>
    public CommanderSelectScene(GameContext context, int saveSlot) : base(context) => _saveSlot = saveSlot;

    private CommandeDef Selected => _commanders[Math.Clamp(_index, 0, _commanders.Count - 1)];

    private Difficulty SelectedDifficulty =>
        DifficultySettings.AllLevels[Math.Clamp(_difficultyIndex, 0, DifficultySettings.AllLevels.Count - 1)];

    /// <summary>Vrai s'il y a de quoi faire défiler : sinon les flèches sont inertes et grisées.</summary>
    private bool HasChoice => _commanders.Count > 1;

    /// <summary>
    /// SEUL point de vérité du verrouillage d'un commandant : ouvert d'office par la donnée
    /// (<see cref="CommandeDef.StartsUnlocked"/>, champ <c>unlocked</c> de units.json) OU débloqué dans le
    /// profil (méta-progression, en battant son boss lié en dernière phase — cf. <c>SaveService.IsCommanderUnlocked</c>).
    /// </summary>
    private bool IsUnlocked(CommandeDef def) =>
        def.StartsUnlocked || Context.Saves.IsCommanderUnlocked(def.Id);

    public override void Load()
    {
        _card = new UnitCardRenderer(Context);
        _tree = new CommandTreeView(Context);
        _treeIcon = Textures.LoadPngOrNull(Context.GraphicsDevice,
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets/UI/arbre.png"));
        _commanders = Commandes.Playable;
        _difficultyIndex = DifficultySettings.AllLevels.ToList().IndexOf(Difficulty.Normal);
        RebuildPreview();
        // Pas de changement de musique : la piste du menu principal continue jusqu'au lancement de la partie.
    }

    public override void Unload()
    {
        _card.Unload();
        _tree.Unload();
        _treeIcon?.Dispose();
        _treeIcon = null;
    }

    /// <summary>Recrée la run d'aperçu sur le commandant courant (son arbre est lu depuis elle).</summary>
    private void RebuildPreview() => _preview = new Run(seed: 0, commander: Selected, difficulty: SelectedDifficulty);

    private Viewport VirtualViewport => new(0, 0, Context.VirtualResolution.X, Context.VirtualResolution.Y);

    // ── Mise en page ─────────────────────────────────────────────────────────────

    private struct Layout
    {
        public Rectangle Panel, Title;
        public Rectangle Portrait, Sprite, NamePrev, NameNext, Name, Index;
        public Rectangle Stats, Army;                        // sous-panneaux SUPERPOSÉS (survol seulement)
        public Rectangle StartLabel;
        public List<(UnitClass Cls, Domaine Domaine, Rectangle Rect)> StartTiles;
        public Rectangle DiffLabel;
        public Rectangle[] DiffLevels;                       // un bouton numéroté par niveau
        public Rectangle Tree, Back, Start, Hint;
    }

    /// <summary>
    /// Tout l'écran est une PILE VERTICALE centrée : titre → carrousel → nom → pions de départ → difficulté
    /// → actions. On ne fixe plus le haut en haut et le pied en bas ; sinon, sur un grand canevas (1280×720,
    /// le cas du 1440p), le surplus s'accumule en deux TROUS au milieu au lieu de marges équilibrées.
    ///
    /// Le surplus vertical est réparti entre deux respirations internes (sous le nom, sous les pions) et les
    /// marges haut/bas, dans cet ordre : les respirations sont bornées, et tout ce qui dépasse va aux marges.
    /// Sur un canevas serré (960×540), elles retombent à leur minimum et la pile tient quand même.
    /// </summary>
    private Layout BuildLayout(Viewport vp)
    {
        const int titleH = 18, gapTitle = 14, gapSprite = 10, gapName = 4, indexH = 10;
        const int labelH = 10, labelGap = 8, gapButtons = 14;

        var panel = new Rectangle(Margin, Margin, vp.Width - 2 * Margin, vp.Height - 2 * Margin);
        var l = new Layout { Panel = panel };
        var cx = panel.Center.X;
        var starts = Selected.StartingUnits;

        // L'indice de bas de page reste collé au bas : c'est une aide, pas un élément de la composition.
        l.Hint = new Rectangle(panel.X, panel.Bottom - 16, panel.Width, 10);

        var startBlockH = labelH + (starts.Count > 0 ? labelGap + UnitCardRenderer.TileH : 0);
        var stackH = titleH + gapTitle + SpriteBox + gapSprite + NameH + gapName + indexH
                     + startBlockH + BarH + gapButtons + BtnH;

        var usable = l.Hint.Y - 8 - (panel.Y + 8);
        var slack = Math.Max(0, usable - stackH);
        var breathe = Math.Clamp(slack / 4, 8, 40);            // respiration sous le nom ET sous les pions
        var y = panel.Y + 8 + Math.Max(0, (slack - 2 * breathe) / 2);

        l.Title = new Rectangle(panel.X, y, panel.Width, titleH);
        y += titleH + gapTitle;

        l.Sprite = new Rectangle(cx - SpriteBox / 2, y, SpriteBox, SpriteBox);
        // Zone de survol du commandant (elle ne se voit pas : les pions n'ont plus de cadre).
        l.Portrait = Inflate(l.Sprite, PortraitPad);
        // Flèches AU-DELÀ des voisins du carrousel, pour ne pas les recouvrir.
        var arrowY = y + (SpriteBox - ArrowH) / 2;
        l.NamePrev = new Rectangle(cx - ArrowOffset - ArrowW / 2, arrowY, ArrowW, ArrowH);
        l.NameNext = new Rectangle(cx + ArrowOffset - ArrowW / 2, arrowY, ArrowW, ArrowH);
        // Sous-panneaux de stats : SUPERPOSÉS au carrousel (ils recouvrent les voisins) et affichés
        // seulement au survol du portrait — ils ne réservent donc aucune place dans la pile.
        l.Stats = new Rectangle(l.Portrait.X - PanelGap - PanelW, l.Portrait.Y, PanelW, l.Portrait.Height);
        l.Army = new Rectangle(l.Portrait.Right + PanelGap, l.Portrait.Y, PanelW, l.Portrait.Height);
        // L'arbre n'est pas un bouton du pied : c'est une icône posée au coin haut-droit du commandant.
        l.Tree = new Rectangle(l.Sprite.Right - TreeIcon, l.Sprite.Y, TreeIcon, TreeIcon);
        y += SpriteBox + gapSprite;

        l.Name = new Rectangle(panel.X, y, panel.Width, NameH);
        y += NameH + gapName;
        l.Index = new Rectangle(panel.X, y, panel.Width, indexH);
        y += indexH + breathe;

        l.StartLabel = new Rectangle(panel.X, y, panel.Width, labelH);
        l.StartTiles = new List<(UnitClass, Domaine, Rectangle)>();
        var rowW = starts.Count * UnitCardRenderer.TileW + Math.Max(0, starts.Count - 1) * TileGap;
        var rowX = cx - rowW / 2;
        var rowY = l.StartLabel.Bottom + labelGap;
        for (var i = 0; i < starts.Count; i++)
            l.StartTiles.Add((Domaines.Of(starts[i]).BaseClass, starts[i],
                new Rectangle(rowX + i * (UnitCardRenderer.TileW + TileGap), rowY,
                    UnitCardRenderer.TileW, UnitCardRenderer.TileH)));
        y += startBlockH + breathe;

        // Difficulté : un bouton NUMÉROTÉ par niveau ; ce que chacun change sort au survol. Le groupe entier
        // (libellé + boutons) est CENTRÉ, largeur du libellé mesurée pour qu'il colle aux boutons.
        var levels = DifficultySettings.AllLevels.Count;
        var labelW = Context.Font.Measure(Loc.T("difficulty.label"), 1);
        var groupW = labelW + 12 + levels * DiffBtn + (levels - 1) * DiffGap;

        l.DiffLabel = new Rectangle(cx - groupW / 2, y, labelW, BarH);
        l.DiffLevels = new Rectangle[levels];
        for (var i = 0; i < levels; i++)
            l.DiffLevels[i] = new Rectangle(l.DiffLabel.Right + 12 + i * (DiffBtn + DiffGap),
                y + (BarH - DiffBtn) / 2, DiffBtn, DiffBtn);
        y += BarH + gapButtons;

        l.Back = new Rectangle(cx - (BackW + 24 + StartW) / 2, y, BackW, BtnH);
        l.Start = new Rectangle(l.Back.Right + 24, y, StartW, BtnH);

        return l;
    }

    // ── Mise à jour ─────────────────────────────────────────────────────────────

    public override void Update(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Le carrousel se repose, même quand l'arbre est ouvert par-dessus.
        if (_slideT > 0f)
            _slideT = Math.Max(0f, _slideT - dt / SlideDuration);

        // Arbre ouvert en CONSULTATION : il capte tout jusqu'à sa fermeture.
        if (_tree.IsOpen)
        {
            _tree.Update(_preview, VirtualViewport.Bounds, dt, canClose: true, readOnly: true);
            return;
        }

        var lay = BuildLayout(VirtualViewport);
        BuildFocusables(lay);
        _focus = Math.Clamp(_focus, 0, Math.Max(0, _focusables.Count - 1));

        // Retour au menu : Échap (clavier) ou B (manette).
        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasCancelPressed)
        {
            Back();
            return;
        }

        // Bumpers : changement de commandant quel que soit le périphérique.
        if (Context.Input.WasLeftShoulderPressed) { StepCommander(-1); return; }
        if (Context.Input.WasRightShoulderPressed) { StepCommander(+1); return; }

        if (Context.Input.UsingGamepad)
        {
            if (Context.Input.Nav(NavDir.Up)) MoveFocus(NavDir.Up);
            if (Context.Input.Nav(NavDir.Down)) MoveFocus(NavDir.Down);
            if (Context.Input.Nav(NavDir.Left)) MoveFocus(NavDir.Left);
            if (Context.Input.Nav(NavDir.Right)) MoveFocus(NavDir.Right);
            if (Context.Input.WasConfirmPressed && _focus < _focusables.Count)
                Activate(_focusables[_focus].Kind, _focusables[_focus].Data);
            return;
        }

        if (!Context.Input.WasLeftClicked)
            return;

        var p = Context.Input.MousePosition;
        if (lay.Tree.Contains(p)) { OpenTree(); return; }        // avant le carrousel : elle est POSÉE dessus
        for (var i = 0; i < lay.DiffLevels.Length; i++)
            if (lay.DiffLevels[i].Contains(p)) { PickDifficulty(i); return; }

        if (lay.NamePrev.Contains(p)) StepCommander(-1);
        else if (lay.NameNext.Contains(p)) StepCommander(+1);
        else if (lay.Back.Contains(p)) Back();
        else if (lay.Start.Contains(p)) StartGame();
    }

    private void BuildFocusables(Layout lay)
    {
        _focusables.Clear();
        if (HasChoice)
        {
            _focusables.Add((lay.NamePrev, Kind.CommanderPrev, 0));
            _focusables.Add((lay.NameNext, Kind.CommanderNext, 0));
        }
        _focusables.Add((lay.Tree, Kind.Tree, 0));
        for (var i = 0; i < lay.StartTiles.Count; i++)
            _focusables.Add((lay.StartTiles[i].Rect, Kind.StartTile, i));
        for (var i = 0; i < lay.DiffLevels.Length; i++)
            _focusables.Add((lay.DiffLevels[i], Kind.DiffLevel, i));
        _focusables.Add((lay.Back, Kind.Back, 0));
        _focusables.Add((lay.Start, Kind.Start, 0));
    }

    private void Activate(Kind kind, int data)
    {
        switch (kind)
        {
            case Kind.CommanderPrev: StepCommander(-1); break;
            case Kind.CommanderNext: StepCommander(+1); break;
            case Kind.DiffLevel: PickDifficulty(data); break;
            case Kind.Tree: OpenTree(); break;
            case Kind.Back: Back(); break;
            case Kind.Start: StartGame(); break;
            // StartTile : rien à activer (le focus suffit à afficher la carte du pion).
        }
    }

    /// <summary>Carrousel : le choix BOUCLE (comme le sélecteur de domaine du Codex).</summary>
    private void StepCommander(int dir)
    {
        if (!HasChoice)
            return;
        var n = _commanders.Count;
        _index = (_index + dir % n + n) % n;
        _slideDir = Math.Sign(dir);
        _slideT = 1f;                 // le glissement repart à fond, même si le précédent n'est pas fini
        RebuildPreview();
        Context.Sounds.Play("menu_click");
    }

    /// <summary>Décalage horizontal courant du carrousel (0 au repos). Amorti : rapide puis ralenti.</summary>
    private int SlideOffset() => (int)Math.Round(_slideDir * SlotSpacing * (_slideT * _slideT));

    /// <summary>Commandant à <paramref name="slot"/> emplacements du centre (le carrousel BOUCLE).</summary>
    private CommandeDef CommanderAt(int slot)
    {
        var n = _commanders.Count;
        return _commanders[((_index + slot) % n + n) % n];
    }

    /// <summary>Choix direct d'un niveau par son bouton numéroté.</summary>
    private void PickDifficulty(int level)
    {
        var next = Math.Clamp(level, 0, DifficultySettings.AllLevels.Count - 1);
        if (next == _difficultyIndex)
            return;
        _difficultyIndex = next;
        Context.Sounds.Play("menu_click");
    }

    private void OpenTree()
    {
        if (!IsUnlocked(Selected))
            return;
        RebuildPreview();
        _tree.Open();
    }

    private void Back() => Context.Scenes.Change(new MainMenuScene(Context));

    private void StartGame()
    {
        // Garde-fou : le bouton est déjà éteint, mais la manette et la souris passent par ici tous les deux.
        if (!IsUnlocked(Selected))
        {
            Context.Sounds.Play("menu_close");   // refus sec, comme un achat impossible dans l'arbre
            return;
        }

        Context.Sounds.Play("menu_click");
        Context.Scenes.Change(new GameplayScene(Context, _saveSlot, run: null,
            commander: Selected, difficulty: SelectedDifficulty));
    }

    // ── Navigation manette par rangées (même logique que CodexView) ───────────────

    private void MoveFocus(NavDir dir)
    {
        if (_focusables.Count == 0)
            return;
        var rows = BuildFocusRows();
        var (ri, ci) = LocateFocus(rows);
        if (ri < 0)
            return;

        var target = dir switch
        {
            NavDir.Left => ci > 0 ? rows[ri][ci - 1] : -1,
            NavDir.Right => ci < rows[ri].Count - 1 ? rows[ri][ci + 1] : -1,
            NavDir.Up => ri > 0 ? ClosestInRow(rows[ri - 1], _focusables[_focus].Rect.Center.X) : -1,
            NavDir.Down => ri < rows.Count - 1 ? ClosestInRow(rows[ri + 1], _focusables[_focus].Rect.Center.X) : -1,
            _ => -1,
        };

        if (target >= 0 && target != _focus)
        {
            _focus = target;
            Context.Sounds.Play("menu_click");
        }
    }

    private List<List<int>> BuildFocusRows()
    {
        var order = Enumerable.Range(0, _focusables.Count).ToList();
        order.Sort((a, b) => _focusables[a].Rect.Center.Y.CompareTo(_focusables[b].Rect.Center.Y));
        var rows = new List<List<int>>();
        foreach (var i in order)
        {
            var cy = _focusables[i].Rect.Center.Y;
            if (rows.Count == 0 || Math.Abs(cy - _focusables[rows[^1][0]].Rect.Center.Y) > 24)
                rows.Add(new List<int>());
            rows[^1].Add(i);
        }
        foreach (var row in rows)
            row.Sort((a, b) => _focusables[a].Rect.Center.X.CompareTo(_focusables[b].Rect.Center.X));
        return rows;
    }

    private (int row, int col) LocateFocus(List<List<int>> rows)
    {
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++)
                if (rows[r][c] == _focus)
                    return (r, c);
        return (-1, -1);
    }

    private int ClosestInRow(List<int> row, int x)
    {
        var best = row[0];
        var bestDist = int.MaxValue;
        foreach (var i in row)
        {
            var d = Math.Abs(_focusables[i].Rect.Center.X - x);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        return best;
    }

    // ── Rendu ────────────────────────────────────────────────────────────────────

    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var vp = VirtualViewport;
        var lay = BuildLayout(vp);
        BuildFocusables(lay);
        var gp = Context.Input.UsingGamepad;
        // Arbre ouvert : on neutralise le survol du fond pour ne pas allumer les boutons à travers l'overlay.
        var mouse = _tree.IsOpen ? new Point(int.MinValue, int.MinValue) : Context.Input.MousePosition;
        var def = Selected;

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(Context.Pixel, new Rectangle(0, 0, vp.Width, vp.Height), Palette.Navy2);
        Context.Style.DrawPanel(sb, lay.Panel);

        Context.Font.DrawCentered(sb, Loc.T("commander.title"), lay.Title, 2, Palette.Yellow2);

        // Kind sous le focus manette : pilote la surbrillance (le « curseur »).
        Kind? fk = gp && _focus < _focusables.Count ? _focusables[_focus].Kind : null;
        var hoverIndex = HoverIndex(gp, mouse);

        DrawCarousel(sb, lay);
        var unlocked = IsUnlocked(def);

        // Nom en grand (masqué si verrouillé) + position dans le carrousel.
        Context.Font.DrawCentered(sb, unlocked ? CommanderName(def) : "???", lay.Name, 3,
            unlocked ? Palette.White : Palette.Grey);
        if (HasChoice)
            Context.Font.DrawCentered(sb, Loc.T("commander.index", _index + 1, _commanders.Count),
                lay.Index, 1, Palette.Blue1);

        // L'arbre n'est consultable que sur un commandant débloqué (il révélerait ses effets).
        if (unlocked)
            DrawTreeIcon(sb, lay.Tree, mouse, gp, fk == Kind.Tree);

        Arrow(sb, lay.NamePrev, "<", mouse, gp, fk == Kind.CommanderPrev);
        Arrow(sb, lay.NameNext, ">", mouse, gp, fk == Kind.CommanderNext);

        // Pions de départ, en bande centrée — en silhouette si le commandant est verrouillé, et le titre
        // de la section cède la place à l'annonce du verrou.
        Context.Font.DrawCentered(sb, Loc.T(unlocked ? "commander.starting_units" : "commander.locked"),
            lay.StartLabel, 1, unlocked ? Palette.Yellow1 : Palette.Grey);
        if (unlocked && lay.StartTiles.Count == 0)
            Context.Font.DrawCentered(sb, Loc.T("commander.alone"),
                new Rectangle(lay.Panel.X, lay.StartLabel.Bottom + 10, lay.Panel.Width, 10), 1, Palette.Grey);
        for (var i = 0; i < lay.StartTiles.Count; i++)
            _card.DrawTile(sb, lay.StartTiles[i].Cls, lay.StartTiles[i].Rect,
                IsTileHighlighted(i, hoverIndex), revealed: unlocked);

        // Barre du bas : les niveaux de difficulté en boutons numérotés, puis les deux actions.
        Context.Font.Draw(sb, Loc.T("difficulty.label"),
            new Vector2(lay.DiffLabel.X, lay.DiffLabel.Y + (lay.DiffLabel.Height - 7) / 2), 1, Palette.Yellow1);
        for (var i = 0; i < lay.DiffLevels.Length; i++)
            DrawDifficultyButton(sb, lay.DiffLevels[i], i, mouse, gp, fk == Kind.DiffLevel && FocusData() == i);

        Button(sb, lay.Back, Loc.T("commander.back"), mouse, gp, fk == Kind.Back, 1, Palette.White);
        // LANCER : action principale — distinguée par sa TAILLE et son texte à l'échelle 2, sans liseré
        // permanent (le liseré doit rester le signe du survol / du focus, sinon il ne veut plus rien dire).
        // Éteint sur un commandant verrouillé : le bouton doit dire ce qu'il fait.
        Button(sb, lay.Start, Loc.T("commander.start"), mouse, gp, fk == Kind.Start, 2, Palette.Yellow2,
            enabled: unlocked);

        Context.Font.DrawCentered(sb, Loc.T(gp ? "commander.hint_gp" : "commander.hint"),
            lay.Hint, 1, Palette.Blue1);

        // Détail du commandant : SUPERPOSÉ, seulement quand on le survole (ou, à la manette, quand le focus
        // est sur le carrousel). Le reste du temps la place est rendue au carrousel.
        if (ShowDetails(lay, gp, mouse, fk))
        {
            DrawStatsPanel(sb, lay.Stats, def);
            DrawArmyPanel(sb, lay.Army, def);
        }

        // Carte du pion de départ survolé/focus, par-dessus le reste.
        if (TileUnderPointer(lay, hoverIndex) is { } tile)
            _card.DrawCardNear(sb, tile.Cls, tile.Domaine, tile.Rect, lay.Panel);

        // Niveau de difficulté survolé/focus : son nom et ce qu'il change, au-dessus de son bouton.
        if (DifficultyUnderPointer(lay, hoverIndex) is { } level)
            DrawDifficultyTooltip(sb, level, lay.DiffLevels[level], lay.Panel);
        sb.End();

        if (_tree.IsOpen)
            _tree.Draw(sb, vp, vp.Bounds, _preview);
    }

    /// <summary>
    /// Le carrousel : tous les pions à la MÊME taille, alignés, la rangée entière glissant d'un cran à
    /// chaque pas. Seule la LUMINOSITÉ distingue le pion choisi, et elle varie continûment avec la distance
    /// au centre : pendant le glissement le pion sortant s'efface pendant que l'entrant s'éclaire, sans
    /// aucun changement de taille (donc aucun à-coup).
    ///
    /// On dessine jusqu'aux emplacements ±2 pendant le glissement, sinon un trou apparaîtrait du côté d'où
    /// arrive le nouveau pion. Le plus lointain est de toute façon rendu invisible par le fondu.
    /// </summary>
    private void DrawCarousel(SpriteBatch sb, Layout lay)
    {
        var offset = SlideOffset();
        var cx = lay.Panel.Center.X;
        var far = _slideT > 0f ? 2 : 1;

        // Du plus lointain au plus proche : le pion courant se dessine en dernier, donc par-dessus.
        for (var rank = far; rank >= 0; rank--)
            for (var slot = -rank; slot <= rank; slot += Math.Max(1, 2 * rank))
                DrawSlot(sb, lay, CommanderAt(slot), cx + slot * SlotSpacing + offset, cx);
    }

    /// <summary>
    /// Un emplacement du carrousel : le sprite seul, SANS cadre — les pions posent à même le panneau. Le
    /// badge de domaine suit le sprite (calculé ici et non dans la mise en page statique : sinon il resterait
    /// planté au centre pendant que le pion glisse).
    /// </summary>
    private void DrawSlot(SpriteBatch sb, Layout lay, CommandeDef def, int x, int centerX)
    {
        var distance = Math.Abs(x - centerX) / (float)SlotSpacing;
        var alpha = Math.Clamp(1f - distance * FadePerSlot, 0f, 1f);
        if (alpha <= 0f)
            return;

        // VERROUILLÉ : silhouette noire, comme un pion non découvert du Codex. Le fondu du carrousel
        // s'applique par-dessus, donc un voisin verrouillé s'estompe comme les autres.
        var unlocked = IsUnlocked(def);
        var sprite = new Rectangle(x - SpriteBox / 2, lay.Sprite.Center.Y - SpriteBox / 2, SpriteBox, SpriteBox);
        _card.DrawScaled(sb, def.BaseClass, sprite.Center, SpriteScale,
            (unlocked ? Color.White : Color.Black) * alpha);

        // Le badge de domaine RÉVÈLE une information : on le tait sur un commandant verrouillé. Et il
        // n'accompagne que le pion (quasi) centré, sinon il brouillerait la lecture.
        if (distance < 0.5f && unlocked)
            _card.DrawDomaineBadge(sb, def.Movement,
                new Rectangle(sprite.X, sprite.Bottom - DomaineBadge, DomaineBadge, DomaineBadge));
    }

    /// <summary>
    /// Vrai si les panneaux de détail doivent s'afficher : survol du portrait à la souris, ou focus sur le
    /// carrousel à la manette (où il n'y a pas de pointeur pour « survoler »).
    /// </summary>
    private bool ShowDetails(Layout lay, bool gp, Point mouse, Kind? focused) =>
        IsUnlocked(Selected)   // rien à révéler sur un commandant verrouillé
        && (gp ? focused is Kind.CommanderPrev or Kind.CommanderNext
               : lay.Portrait.Contains(mouse));

    /// <summary>Sous-panneau gauche : PV (barre) puis puissance / mouvement / portée.</summary>
    private void DrawStatsPanel(SpriteBatch sb, Rectangle area, CommandeDef def)
    {
        if (area.Width < 120)
            return;   // canevas trop étroit : on renonce plutôt que de dessiner un panneau illisible
        Context.Style.DrawPanel(sb, area);
        var inner = new Rectangle(area.X + Pad, area.Y + Pad, area.Width - 2 * Pad, area.Height - 2 * Pad);
        var c = def.BaseClass;

        Context.Font.Draw(sb, Loc.T("commander.stats"), new Vector2(inner.X, inner.Y), 1, Palette.Yellow1);
        var y = inner.Y + 16;

        _card.DrawHp(sb, new Rectangle(inner.X, y, inner.Width, 14), c.MaxHp);
        y += 16;
        // PV CENTRÉS sous leur barre (comme sur la carte de pion), pas calés à gauche.
        Context.Font.DrawCentered(sb, $"{c.MaxHp}/{c.MaxHp}", new Rectangle(inner.X, y, inner.Width, 14), 2,
            Palette.White);
        y += 22;

        var rowH = Math.Min(36, Math.Max(24, (inner.Bottom - y) / 3));
        _card.DrawStatRow(sb, new Rectangle(inner.X, y, inner.Width, rowH),
            "deg", Loc.T("stat.power"), c.Damage.ToString(), Palette.Brown3);
        _card.DrawStatRow(sb, new Rectangle(inner.X, y + rowH, inner.Width, rowH),
            "dep", Loc.T("stat.movement"), c.MoveRange.ToString(), Palette.Cyan2);
        _card.DrawStatRow(sb, new Rectangle(inner.X, y + 2 * rowH, inner.Width, rowH),
            "tir", Loc.T("stat.range"), c.AttackRange.ToString(), Palette.Yellow2);
    }

    /// <summary>Sous-panneau droit : plafonds d'armée et sources de points de commandement.</summary>
    private void DrawArmyPanel(SpriteBatch sb, Rectangle area, CommandeDef def)
    {
        if (area.Width < 120)
            return;
        Context.Style.DrawPanel(sb, area);
        var inner = new Rectangle(area.X + Pad, area.Y + Pad, area.Width - 2 * Pad, area.Height - 2 * Pad);

        // Les TITRES sont calés à gauche ; leur corps de texte est mis EN RETRAIT, ce qui donne au panneau
        // sa hiérarchie sans avoir à ajouter de trait ni de cadre.
        var body = new Rectangle(inner.X + BodyIndent, inner.Y, inner.Width - BodyIndent, inner.Height);

        Context.Font.Draw(sb, Loc.T("commander.army"), new Vector2(inner.X, inner.Y), 1, Palette.Yellow1);
        var y = inner.Y + 16;

        // Valeurs alignées en COLONNE juste après le plus long libellé — pas plaquées au bord droit du
        // panneau, où le chiffre se retrouvait détaché de ce qu'il qualifie.
        var valueX = body.X + 16 + Math.Max(Context.Font.Measure(Loc.T("commander.deploy"), 1),
                                            Context.Font.Measure(Loc.T("commander.reserve"), 1));
        y = LabelValue(sb, body, y, valueX, Loc.T("commander.deploy"), def.Deployments.ToString(), Palette.White);
        y = LabelValue(sb, body, y, valueX, Loc.T("commander.reserve"), def.ReserveSize.ToString(), Palette.White);

        // Gain de points : une PHRASE par source, la commune (mission) puis celle propre au commandant.
        y += 10;
        Context.Font.Draw(sb, Loc.T("commander.points"), new Vector2(inner.X, y), 1, Palette.Yellow1);
        y += 16;
        foreach (var line in _card.Wrap(Loc.T("commander.points_mission", Run.PointsPerMission), body.Width, 1))
        {
            Context.Font.Draw(sb, line, new Vector2(body.X, y), 1, Palette.White);
            y += LineH;
        }
        if (def.FusionPoints > 0)
            foreach (var line in _card.Wrap(Loc.T("commander.points_fusion", def.FusionPoints), body.Width, 1))
            {
                Context.Font.Draw(sb, line, new Vector2(body.X, y), 1, Palette.Cyan1);
                y += LineH;
            }
    }

    /// <summary>
    /// Icône d'accès à l'arbre, posée au coin haut-droit du commandant. Le PNG attendu est
    /// <c>Assets/UI/arbre.png</c> (32×32) ; à défaut on dessine un petit arbre à trois nœuds.
    /// </summary>
    private void DrawTreeIcon(SpriteBatch sb, Rectangle r, Point mouse, bool gp, bool focusedGp)
    {
        var active = focusedGp || (!gp && r.Contains(mouse));
        var tint = active ? Palette.Yellow2 : Palette.White;

        if (_treeIcon != null)
        {
            sb.Draw(_treeIcon, r, tint);
        }
        else
        {
            // Repli : deux nœuds hauts reliés en Y à un nœud bas — lisible même en 32 px.
            const int n = 8;
            var mid = r.Center.X;
            Fill(sb, new Rectangle(mid - 1, r.Y + n, 2, r.Height - 2 * n), tint);
            Fill(sb, new Rectangle(r.X + n / 2, r.Y + r.Height / 2, r.Width - n, 2), tint);
            Fill(sb, new Rectangle(r.X + n / 2 - 1, r.Y + r.Height / 2, 2, r.Height / 2 - n), tint);
            Fill(sb, new Rectangle(r.Right - n / 2 - 1, r.Y + r.Height / 2, 2, r.Height / 2 - n), tint);
            Fill(sb, new Rectangle(mid - n / 2, r.Y, n, n), tint);
            Fill(sb, new Rectangle(r.X, r.Bottom - n, n, n), tint);
            Fill(sb, new Rectangle(r.Right - n, r.Bottom - n, n, n), tint);
        }

        if (active)
            Border(sb, Inflate(r, 3), Palette.Yellow2, 2);
    }

    /// <summary>Un niveau de difficulté, en bouton NUMÉROTÉ. Le niveau retenu garde un liseré or.</summary>
    private void DrawDifficultyButton(SpriteBatch sb, Rectangle r, int level, Point mouse, bool gp, bool focusedGp)
    {
        var selected = level == _difficultyIndex;
        var hover = !gp && r.Contains(mouse);
        var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(hover || selected || focusedGp,
            hover && Context.Input.IsLeftDown));
        var area = r; area.Offset(0, dy);
        Context.Font.DrawCentered(sb, Loc.T($"difficulty.level_{level}"), area, 2,
            selected ? DifficultyColor(DifficultySettings.AllLevels[level]) : Palette.White);
        if (selected)
            Border(sb, Inflate(r, 1), Palette.Yellow1, 2);
        if (focusedGp)
            Border(sb, Inflate(r, 3), Palette.Yellow2, 2);
    }

    /// <summary>
    /// Ce que change le niveau survolé : son nom, la précision de l'IA, et — s'il envoie des vagues plus
    /// fortes que le niveau juste en dessous — une mention QUALITATIVE de ce renfort. Volontairement sans
    /// chiffre : le joueur n'a pas à savoir qu'un seul pion est promu, seulement que ça monte.
    /// </summary>
    private void DrawDifficultyTooltip(SpriteBatch sb, int level, Rectangle anchor, Rectangle bounds)
    {
        // Trois modifications empilées se lisaient comme un pavé : un tiret par ligne et un interligne plus
        // large en font une LISTE, qu'on parcourt d'un coup d'œil.
        const int pad = 8, bullet = 10;
        // Espacements dérivés de la police active (titre CJK plus haut) ; identiques au latin (titre 22 / ligne 13).
        var lineH = Context.Font.GlyphHeight + 6;
        var titleBlock = Context.Font.LineHeight(2) + 8;
        var difficulty = DifficultySettings.AllLevels[level];
        var title = DifficultyName(difficulty);

        // L'IA : même grammaire que les autres leviers — on dit ce qui CHANGE par rapport au palier du
        // dessous. Le niveau le plus bas n'a rien en dessous, il se décrit donc lui-même.
        var lines = new List<string>
        {
            Loc.T(!SharperAi(level) ? "difficulty.ai_low"
                : SharperAi(level - 1) ? "difficulty.ai_fewer_again"
                : "difficulty.ai_fewer"),
        };
        // « Encore plus » dès qu'un niveau INFÉRIEUR faisait déjà monter le même levier : c'est un cran de
        // plus sur une échelle qui monte, pas un premier palier.
        if (RaisesWaves(level))
            lines.Add(Loc.T(RaisesWaves(level - 1) ? "difficulty.more_evolved_again" : "difficulty.more_evolved"));
        if (EquipsEnemies(level))
            lines.Add(Loc.T(EquipsEnemies(level - 1) ? "difficulty.equipped_again" : "difficulty.equipped"));
        if (DemandsPaysans(level))
            lines.Add(Loc.T(DemandsPaysans(level - 1)
                ? "difficulty.special_quota_again" : "difficulty.special_quota"));
        // Run sans filet : « Recommencer la mission » est retiré du menu pause (cf. DifficultySettings.AllowRestart).
        if (!DifficultySettings.For(difficulty).AllowRestart)
            lines.Add(Loc.T("difficulty.no_restart"));

        var w = Context.Font.Measure(title, 2);
        foreach (var line in lines)
            w = Math.Max(w, bullet + Context.Font.Measure(line, 1));
        w += 2 * pad;
        var h = pad + titleBlock + lines.Count * lineH + pad;

        var x = Math.Clamp(anchor.Center.X - w / 2, bounds.X, Math.Max(bounds.X, bounds.Right - w));
        // Au-DESSUS du bouton : la barre de difficulté est déjà en bas de l'écran.
        var y = Math.Max(bounds.Y, anchor.Y - h - 6);

        var box = new Rectangle(x, y, w, h);
        Context.Style.DrawPanel(sb, box);
        Context.Font.Draw(sb, title, new Vector2(box.X + pad, box.Y + pad), 2, DifficultyColor(difficulty));
        var ty = box.Y + pad + titleBlock;
        foreach (var line in lines)
        {
            // preserveCase : ce sont des PHRASES, pas des libellés d'UI — et la police ne dessine les
            // accents QU'EN MINUSCULES (en capitales, « é » retombe sur « E »).
            Context.Font.Draw(sb, "-", new Vector2(box.X + pad, ty), 1, Palette.Yellow1);
            Context.Font.Draw(sb, line, new Vector2(box.X + pad + bullet, ty), 1, Palette.White,
                preserveCase: true);
            ty += lineH;
        }
    }

    /// <summary>
    /// Vrai si <paramref name="level"/> envoie des vagues plus fortes que le niveau juste en dessous.
    /// Comparé au PRÉCÉDENT et non à Normal : la mention reste juste si d'autres niveaux s'ajoutent plus
    /// tard. Le niveau le plus bas n'a rien sous lui, donc rien à annoncer.
    /// </summary>
    /// <summary>Vrai si l'IA de <paramref name="level"/> se trompe moins que celle du niveau juste en dessous.</summary>
    private static bool SharperAi(int level) =>
        Climbs(level, s => s.AiAccuracy);

    private static bool RaisesWaves(int level) =>
        Climbs(level, s => s.TierShift);

    /// <summary>Vrai si <paramref name="level"/> exige plus de paysans sauvés que le niveau juste en dessous.</summary>
    private static bool DemandsPaysans(int level) =>
        Climbs(level, s => s.PaysansRequired);

    /// <summary>Vrai si <paramref name="level"/> équipe plus d'ennemis que le niveau juste en dessous.</summary>
    private static bool EquipsEnemies(int level) =>
        Climbs(level, s => s.EnemyEquipBonus is { } b ? b.Sum() : -1);   // null (aucun équipement) se classe sous tous les autres

    /// <summary>Vrai si un levier de difficulté MONTE entre le niveau précédent et celui-ci.</summary>
    private static bool Climbs(int level, Func<DifficultySettings, double> lever) =>
        level > 0 && level < DifficultySettings.AllLevels.Count
        && lever(DifficultySettings.For(DifficultySettings.AllLevels[level]))
         > lever(DifficultySettings.For(DifficultySettings.AllLevels[level - 1]));

    /// <summary>Index du focusable courant (manette), ou -1.</summary>
    private int FocusData() => _focus < _focusables.Count ? _focusables[_focus].Data : -1;

    /// <summary>Niveau de difficulté survolé/focus, ou null.</summary>
    private int? DifficultyUnderPointer(Layout lay, int hoverIndex)
    {
        if (hoverIndex < 0 || hoverIndex >= _focusables.Count)
            return null;
        var f = _focusables[hoverIndex];
        return f.Kind == Kind.DiffLevel && f.Data < lay.DiffLevels.Length ? f.Data : null;
    }

    /// <summary>
    /// Ligne « libellé, puis valeur en colonne ». Valeur à la MÊME échelle que le libellé : dans ce panneau
    /// tout le reste est en petit, un chiffre à l'échelle 2 y détonnait (la grammaire « valeur en gros » de
    /// la carte de pion ne se transpose pas ici). C'est la couleur qui la distingue. Renvoie le Y suivant.
    /// </summary>
    private int LabelValue(SpriteBatch sb, Rectangle body, int y, int valueX, string label, string value, Color color)
    {
        const int rowH = 16;
        Context.Font.Draw(sb, label, new Vector2(body.X, y), 1, Palette.Blue1);
        Context.Font.Draw(sb, value, new Vector2(valueX, y), 1, color);
        return y + rowH;
    }

    /// <summary>Index dans _focusables de l'élément survolé (souris) ou sous le focus (manette), ou -1.</summary>
    private int HoverIndex(bool gp, Point mouse)
    {
        if (gp)
            return _focus < _focusables.Count ? _focus : -1;
        for (var i = 0; i < _focusables.Count; i++)
            if (_focusables[i].Rect.Contains(mouse))
                return i;
        return -1;
    }

    private bool IsTileHighlighted(int tileIndex, int hoverIndex)
    {
        if (hoverIndex < 0 || hoverIndex >= _focusables.Count)
            return false;
        var f = _focusables[hoverIndex];
        return f.Kind == Kind.StartTile && f.Data == tileIndex;
    }

    private (UnitClass Cls, Domaine Domaine, Rectangle Rect)? TileUnderPointer(Layout lay, int hoverIndex)
    {
        if (hoverIndex < 0 || hoverIndex >= _focusables.Count)
            return null;
        var f = _focusables[hoverIndex];
        return f.Kind == Kind.StartTile && f.Data < lay.StartTiles.Count ? lay.StartTiles[f.Data] : null;
    }

    private void Arrow(SpriteBatch sb, Rectangle r, string glyph, Point mouse, bool gp, bool focusedGp)
    {
        // Un seul commandant : les flèches restent visibles mais éteintes, pour que l'écran ne mente pas
        // sur ce qui est cliquable.
        if (!HasChoice)
        {
            Context.Style.DrawRecessed(sb, r);
            Context.Font.DrawCentered(sb, glyph, r, 2, Palette.Grey);
            return;
        }

        var hover = !gp && r.Contains(mouse);
        var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(hover || focusedGp, hover && Context.Input.IsLeftDown));
        var area = r; area.Offset(0, dy);
        Context.Font.DrawCentered(sb, glyph, area, 3, focusedGp ? Palette.Yellow2 : Palette.White);
        if (focusedGp)
            Border(sb, Inflate(r, 3), Palette.Yellow2, 2);
    }

    private void Button(SpriteBatch sb, Rectangle r, string label, Point mouse, bool gp, bool focusedGp,
        int scale, Color color, bool enabled = true)
    {
        var hover = enabled && !gp && r.Contains(mouse);
        var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(hover || (enabled && focusedGp),
            hover && Context.Input.IsLeftDown));
        var area = r; area.Offset(0, dy);
        var text = !enabled ? Palette.Grey : hover || focusedGp ? Palette.Yellow2 : color;
        Context.Font.DrawCentered(sb, label, area, scale, text);
        if (focusedGp)
            Border(sb, Inflate(r, 3), enabled ? Palette.Yellow2 : Palette.Grey, 2);
    }

    private static string CommanderName(CommandeDef def) =>
        Loc.TOr("commander." + def.Id, def.Name).ToUpperInvariant();

    private static string DifficultyName(Difficulty d) => Loc.T("difficulty." + d.ToString().ToLowerInvariant());

    private static Color DifficultyColor(Difficulty d) => d switch
    {
        Difficulty.Facile => Palette.Green1,
        Difficulty.Difficile => Palette.Purple5,
        _ => Palette.Cyan1,
    };

    private void Fill(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(Context.Pixel, r, c);

    private void Border(SpriteBatch sb, Rectangle r, Color c, int t)
    {
        Fill(sb, new Rectangle(r.X, r.Y, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
        Fill(sb, new Rectangle(r.X, r.Y, t, r.Height), c);
        Fill(sb, new Rectangle(r.Right - t, r.Y, t, r.Height), c);
    }

    private static Rectangle Inflate(Rectangle r, int by) =>
        new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);
}
