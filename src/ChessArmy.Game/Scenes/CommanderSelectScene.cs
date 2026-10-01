using System;
using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Campaign;
using ChessArmy.Engine;
using ChessArmy.Engine.Input;
using ChessArmy.Engine.Localization;
using ChessArmy.Engine.Persistence;
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
/// Deux moitiés (maquette docs/maquettes/selection-commandant.png, spec docs/selection-commandant.md) :
/// <list type="bullet">
/// <item>à GAUCHE, le commandant : titre, sprite ×4 encadré de ses voisins ×2 atténués (carrousel), nom avec
/// les flèches, pastilles de position, puis les trois boutons de difficulté et, toujours visible, ce que
/// change le niveau retenu ;</item>
/// <item>à DROITE, sa fiche dans un cadre toujours affiché : stats, gains de points, unités de départ, bouton
/// de l'arbre et historique ; sous le cadre, RETOUR et LANCER.</item>
/// </list>
///
/// MISE EN PAGE ÉLASTIQUE : les cotes de la spec sont celles du canevas 960×540 (1080p, le plus serré). Sur
/// un canevas plus grand, le cadre de droite s'élargit en proportion et reste ancré à droite, la colonne de
/// gauche prend le reste et son contenu s'y centre, et le surplus vertical se répartit haut/bas.
///
/// Souris, clavier et manette, sur le modèle de <see cref="CodexView"/> : géométrie déterministe recalculée
/// en tête d'Update et de Draw, focusables reconstruits à chaque image, navigation par rangées.
/// </summary>
public sealed class CommanderSelectScene : Scene
{
    // Éléments focusables (navigation manette par rangées). Le carrousel de commandants est UN seul focus
    // (Commander) : gauche/droite y fait défiler les pions au lieu de sauter de flèche en flèche.
    private enum Kind { Commander, StartTile, DiffLevel, Tree, Back, Start }

    // ── Cotes du canevas de référence 960×540 ──
    private const int RefW = 960, RefH = 540;
    private const int Top = 24;                 // haut du cadre de droite et du titre
    private const int EdgeRight = 32;           // marge droite du cadre
    private const int ColumnGap = 20;           // entre la colonne de gauche et le cadre
    private const int RefFrameW = 388;          // largeur du cadre à 960
    private const int FrameBottomGap = 84;      // bas du cadre = H - 84 (456 à 540)
    private const int BarBottomGap = 26;        // bas de la barre RETOUR/LANCER = H - 26 (514 à 540)
    private const int BarH = 40, BarGap = 12, RefBackW = 126;

    /// <summary>Pion choisi à l'échelle 4 (256 px), voisins à l'échelle 2 (128 px) : facteurs ENTIERS.</summary>
    private const int MainScale = 4, SideScale = 2;
    private const int MainBox = 64 * MainScale;
    private const int SlotSpacing = 192;        // centre → centre d'un voisin (il reste dans la moitié gauche)
    private const float SlideDuration = 0.22f;

    /// <summary>
    /// Perte de luminosité par emplacement d'écart avec le centre : les voisins sont ATTÉNUÉS, et le pion qui
    /// sort du carrousel s'efface au lieu de déborder. Continu, contrairement à l'échelle (entière).
    /// </summary>
    private const float FadePerSlot = 0.55f;

    private const int ArrowW = 36, ArrowH = 40;
    private const int ArrowLeftOffset = 212, ArrowRightOffset = 176;   // centre gauche → bord gauche des flèches
    private const int DotSize = 4, DotActiveW = 16, DotGap = 4;
    private const int DiffHalfW = 218;          // groupe de difficulté : 42 → 478 à 960
    private const int DiffH = 36, DiffGap = 9;
    private const int GaugeSquare = 6, GaugeGap = 2;
    private int NameH => Context.Font.LineHeight(3);   // hauteur du nom (scale 3) : 21 latin / 36 cjk (police active)

    // ── Cadre de droite ──
    private const int FramePadX = 16, FramePadY = 16;
    private const int BodyIndent = 8;           // retrait des lignes sous leur titre de section
    private const int StatLabelW = 84, StatValueW = 28;
    private const int DomaineBadge = 39;
    private const int TreeH = 36;
    private const int TileStep = 86;            // pas des vignettes de départ (cadre 76 + 10)
    private const int MinSectionGap = 3, MaxSectionGap = 20;

    private readonly int _saveSlot;

    private UnitCardRenderer _card = null!;
    private CommandTreeView _tree = null!;
    private MenuBackdrop _backdrop = null!;   // même fond que le menu principal, sur tout l'écran

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
        AvailableLevels[Math.Clamp(_difficultyIndex, 0, AvailableLevels.Count - 1)];

    /// <summary>
    /// Niveaux de difficulté SÉLECTIONNABLES : tous, sauf en mode démo où « Difficile » est retiré (réservé au
    /// jeu complet). C'est un préfixe de <see cref="DifficultySettings.AllLevels"/>, donc l'index reste valide.
    /// </summary>
    private System.Collections.Generic.IReadOnlyList<Difficulty> AvailableLevels =>
        Context.Settings.IsDemo
            ? DifficultySettings.AllLevels.Where(d => d != Difficulty.Difficile).ToList()
            : DifficultySettings.AllLevels;

    /// <summary>Vrai s'il y a de quoi faire défiler : sinon les flèches sont inertes et grisées.</summary>
    private bool HasChoice => _commanders.Count > 1;

    // Build PLAYTEST (compilée avec -p:PlaytestBuild=true, profil FolderProfile) : TOUS les commandants
    // débloqués d'office. Build JEU COMPLET (profil FolderProfileFull) : méta-progression normale.
#if PLAYTEST
    private const bool UnlockAllForPlaytest = true;
#else
    private const bool UnlockAllForPlaytest = false;
#endif

    /// <summary>
    /// SEUL point de vérité du verrouillage d'un commandant : ouvert d'office par la donnée
    /// (<see cref="CommandeDef.StartsUnlocked"/>, champ <c>unlocked</c> de units.json) OU débloqué dans le
    /// profil (méta-progression, en battant son boss lié en dernière phase — cf. <c>SaveService.IsCommanderUnlocked</c>).
    /// </summary>
    private bool IsUnlocked(CommandeDef def) =>
        // Mode démo : SEULS les commandants ouverts d'office sont jouables ; les autres restent en vitrine
        // (silhouette verrouillée) et le hack playtest comme la méta-progression sont ignorés.
        Context.Settings.IsDemo
            ? def.StartsUnlocked || def.DemoUnlocked
            : (UnlockAllForPlaytest || def.StartsUnlocked || Context.Saves.IsCommanderUnlocked(def.Id));

    public override void Load()
    {
        _card = new UnitCardRenderer(Context);
        _backdrop = new MenuBackdrop(Context.GraphicsDevice, Context.Pixel);
        _tree = new CommandTreeView(Context);
        _treeIcon = Textures.LoadPngOrNull(Context.GraphicsDevice,
            System.IO.Path.Combine(AppContext.BaseDirectory, "Assets/UI/arbre.png"));
        // Débloqués en tête du carrousel, verrouillés ensuite ; tri STABLE, donc chaque groupe garde l'ordre
        // de units.json (le commandant de base, premier débloqué, reste sélectionné à l'ouverture).
        _commanders = Commandes.Playable.OrderBy(c => IsUnlocked(c) ? 0 : 1).ToList();
        _difficultyIndex = AvailableLevels.ToList().IndexOf(Difficulty.Normal);
        RebuildPreview();
        // Pas de changement de musique : la piste du menu principal continue jusqu'au lancement de la partie.
    }

    public override void Unload()
    {
        _card.Unload();
        _tree.Unload();
        _treeIcon?.Dispose();
        _treeIcon = null;
        _backdrop.Dispose();
    }

    /// <summary>
    /// Bandes du letterbox : le dégradé du menu continue sur tout l'écran, avec le voile de l'arbre de
    /// commandement quand il est ouvert (= celui de CommandTreeView.Draw).
    /// </summary>
    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale) =>
        _backdrop.DrawBands(Context.SpriteBatch, realScreen, canvasOffset, canvasScale, Context.VirtualResolution,
            _tree.IsOpen ? new[] { Palette.Black1 * 0.62f } : Array.Empty<Color>());

    /// <summary>Recrée la run d'aperçu sur le commandant courant (son arbre est lu depuis elle).</summary>
    private void RebuildPreview() => _preview = new Run(seed: 0, commander: Selected, difficulty: SelectedDifficulty);

    private Viewport VirtualViewport => new(0, 0, Context.VirtualResolution.X, Context.VirtualResolution.Y);

    // ── Mise en page ─────────────────────────────────────────────────────────────

    private struct Layout
    {
        public Rectangle Left, Title, Sprite, Portrait, NamePrev, NameNext, Name, Dots;
        public Rectangle[] DiffLevels;
        public Point Effects;                                 // haut-gauche de la liste des effets
        public Rectangle Frame, Inner;
        public int StatsY, Sep1Y, PointsY, Sep2Y, UnitsY, HistoryY;
        public List<string> PointLines;
        public List<(UnitClass Cls, Domaine Domaine, Rectangle Rect)> StartTiles;
        public Rectangle Tree, Back, Start;
    }

    private int TextH => Context.Font.LineHeight(1);
    private int RowH => Math.Max(16, TextH + 6);             // lignes libellé/valeur (stats, historique)
    private int TitleStep => TextH + 9;                      // titre de section → première ligne
    private int PointStep => TextH + 5;                      // lignes de gain de points
    private int EffectStep => Context.Font.GlyphHeight + 6;  // liste des effets de difficulté

    private Layout BuildLayout(Viewport vp)
    {
        var l = new Layout();
        var w = vp.Width;
        var h = vp.Height;
        var def = Selected;

        // ── Cadre de droite et barre du bas ──
        var frameW = Math.Max(RefFrameW, w * RefFrameW / RefW);
        var frameX = w - EdgeRight - frameW;
        l.Frame = new Rectangle(frameX, Top, frameW, h - FrameBottomGap - Top);
        l.Inner = new Rectangle(l.Frame.X + FramePadX, l.Frame.Y + FramePadY,
            l.Frame.Width - 2 * FramePadX, l.Frame.Height - 2 * FramePadY);
        var backW = frameW * RefBackW / RefFrameW;
        var barY = h - BarBottomGap - BarH;
        l.Back = new Rectangle(frameX, barY, backW, BarH);
        l.Start = new Rectangle(l.Back.Right + BarGap, barY, l.Frame.Right - l.Back.Right - BarGap, BarH);

        // ── Colonne de gauche : pile verticale ──
        l.Left = new Rectangle(0, 0, frameX - ColumnGap, h);
        var cx = l.Left.Center.X;
        const int gapTitle = 26, gapSprite = 26, gapName = 25, gapDots = 12, gapDiff = 12;
        var titleH = Context.Font.LineHeight(2);
        var stackH = titleH + gapTitle + MainBox + gapSprite + NameH + gapName + DotSize + gapDots + DiffH
                     + gapDiff + (MaxEffectLines() + 1) * EffectStep;
        // Le titre s'aligne sur le haut du cadre à 540 ; au-delà, la moitié du surplus passe au-dessus.
        var y = Math.Max(8, Math.Min(Top + (h - RefH) / 2, h - stackH - 4));

        l.Title = new Rectangle(l.Left.X, y, l.Left.Width, titleH);
        y += titleH + gapTitle;
        l.Sprite = new Rectangle(cx - MainBox / 2, y, MainBox, MainBox);
        l.Portrait = l.Sprite;
        y += MainBox + gapSprite;

        l.Name = new Rectangle(cx - ArrowRightOffset, y, 2 * ArrowRightOffset, NameH);
        var arrowY = l.Name.Center.Y - ArrowH / 2;
        l.NamePrev = new Rectangle(cx - ArrowLeftOffset, arrowY, ArrowW, ArrowH);
        l.NameNext = new Rectangle(cx + ArrowRightOffset, arrowY, ArrowW, ArrowH);
        y += NameH + gapName;

        var n = _commanders.Count;
        var dotsW = DotActiveW + Math.Max(0, n - 1) * (DotSize + DotGap);
        l.Dots = new Rectangle(cx - dotsW / 2, y, dotsW, DotSize);
        y += DotSize + gapDots;

        var levels = AvailableLevels.Count;
        var btnW = (2 * DiffHalfW - (levels - 1) * DiffGap) / levels;
        l.DiffLevels = new Rectangle[levels];
        for (var i = 0; i < levels; i++)
            l.DiffLevels[i] = new Rectangle(cx - DiffHalfW + i * (btnW + DiffGap), y, btnW, DiffH);
        y += DiffH + gapDiff;
        l.Effects = new Point(cx - DiffHalfW, y);

        // ── Contenu du cadre ──
        var inner = l.Inner;
        var unlocked = IsUnlocked(def);
        l.PointLines = unlocked ? PointLines(def, inner.Width - BodyIndent) : new List<string> { "???" };

        var statsH = TitleStep + Math.Max(4 * RowH, 2 * RowH + DomaineBadge);
        var pointsH = TitleStep + l.PointLines.Count * PointStep;
        var unitsH = TitleStep + UnitCardRenderer.TileH;
        var historyH = TitleStep + 2 * RowH;
        var fixedH = statsH + 2 + pointsH + 2 + unitsH + TreeH + historyH;
        // Si le contenu déborde (beaucoup de sources de points), ce sont les ÉCARTS qui cèdent, jamais le texte.
        var gap = Math.Clamp((inner.Height - fixedH) / 6, MinSectionGap, MaxSectionGap);

        var fy = inner.Y;
        l.StatsY = fy; fy += statsH + gap;
        l.Sep1Y = fy; fy += 2 + gap;
        l.PointsY = fy; fy += pointsH + gap;
        l.Sep2Y = fy; fy += 2 + gap;
        l.UnitsY = fy; fy += unitsH + gap;
        l.Tree = new Rectangle(inner.X, fy, inner.Width, TreeH); fy += TreeH + gap;
        l.HistoryY = fy;

        // Pions de départ : la classe de base de chaque domaine déclaré, plus — pour un commandant DUO — son
        // SECOND MENEUR en tête de rangée (il ne se recrute ni ne se fusionne, mais c'est bien avec lui qu'on part).
        l.StartTiles = new List<(UnitClass, Domaine, Rectangle)>();
        var tiles = new List<(UnitClass Cls, Domaine Domaine)>();
        if (Commandes.CompanionById(def.CompanionId) is { } companion)
            tiles.Add((companion.BaseClass, companion.Movement));
        foreach (var d in def.StartingUnits)
            tiles.Add((Domaines.Of(d).BaseClass, d));
        // DrawTile pose son cadre 10 px à l'intérieur du rectangle : on recule d'autant pour l'aligner au retrait.
        var tileX = inner.X + BodyIndent - 10;
        var tileY = l.UnitsY + TitleStep;
        for (var i = 0; i < tiles.Count; i++)
            l.StartTiles.Add((tiles[i].Cls, tiles[i].Domaine,
                new Rectangle(tileX + i * TileStep, tileY, UnitCardRenderer.TileW, UnitCardRenderer.TileH)));

        return l;
    }

    /// <summary>Nombre maximal de lignes d'effets parmi les niveaux proposés : la pile de gauche le réserve.</summary>
    private int MaxEffectLines()
    {
        var max = 0;
        for (var i = 0; i < AvailableLevels.Count; i++)
            max = Math.Max(max, DifficultyLines(i).Count);
        return max;
    }

    /// <summary>
    /// Sources de points de commandement, déjà coupées à <paramref name="width"/> : la mission EN PREMIER (tous
    /// les commandants, valeur lue dans la donnée), puis celles propres au commandant, puis le rappel du duo.
    /// </summary>
    private List<string> PointLines(CommandeDef def, int width)
    {
        var texts = new List<string> { Loc.T("commander.points_mission", def.MissionPoints) };
        if (def.FusionPoints > 0) texts.Add(Loc.T("commander.points_fusion", def.FusionPoints));
        if (def.OnHitPoints > 0) texts.Add(Loc.T("commander.points_onhit", def.OnHitPoints));
        if (def.RangedHitPoints > 0) texts.Add(Loc.T("commander.points_ranged", def.RangedHitPoints));
        if (def.JumpPoints > 0) texts.Add(Loc.T("commander.points_jump", def.JumpPoints));
        if (def.LootPoints > 0) texts.Add(Loc.T("commander.points_loot", def.LootPoints));
        if (def.HealPoints > 0) texts.Add(Loc.T("commander.points_heal", def.HealPoints));
        // Le plafond est annoncé pour la mise à mort à deux : à 2 par combat il pèse sur la façon de jouer.
        if (def.PairKillPoints > 0) texts.Add(Loc.T("commander.points_pairkill", def.PairKillPoints, def.PairKillCap));
        if (def.AllyDeathPoints > 0) texts.Add(Loc.T("commander.points_allydeath", def.AllyDeathPoints));

        var lines = new List<string>();
        foreach (var t in texts)
            lines.AddRange(_card.Wrap(t, width, 1));
        // DUO : le rappel « deux commandants » est marqué d'un préfixe pour être dessiné en Yellow2.
        if (def.CompanionId != null)
            lines.AddRange(_card.Wrap(Loc.T("commander.duo_leaders"), width, 1).Select(s => DuoMark + s));
        return lines;
    }

    private const char DuoMark = '\u0001';

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
        if (lay.Tree.Contains(p)) { OpenTree(); return; }
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
        // Le commandant EN TÊTE (index 0) : le focus reste dessus quand on fait défiler le carrousel, alors même
        // que la liste se reconstruit à chaque frame (le nombre de pions de départ change d'un commandant à l'autre).
        _focusables.Add((lay.Portrait, Kind.Commander, 0));
        for (var i = 0; i < lay.DiffLevels.Length; i++)
            _focusables.Add((lay.DiffLevels[i], Kind.DiffLevel, i));
        for (var i = 0; i < lay.StartTiles.Count; i++)
        {
            // Zone de survol limitée au pas des vignettes, pour que deux voisines ne se chevauchent pas.
            var r = lay.StartTiles[i].Rect;
            _focusables.Add((new Rectangle(r.X + 5, r.Y, TileStep, r.Height), Kind.StartTile, i));
        }
        _focusables.Add((lay.Tree, Kind.Tree, 0));
        _focusables.Add((lay.Back, Kind.Back, 0));
        _focusables.Add((lay.Start, Kind.Start, 0));
    }

    private void Activate(Kind kind, int data)
    {
        switch (kind)
        {
            case Kind.DiffLevel: PickDifficulty(data); break;
            case Kind.Tree: OpenTree(); break;
            case Kind.Back: Back(); break;
            case Kind.Start: StartGame(); break;
            // Commander : A ne fait rien (le défilement se fait à GAUCHE/DROITE, cf. MoveFocus).
            // StartTile : rien à activer non plus (le focus suffit à afficher la carte du pion).
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

    /// <summary>Choix direct d'un niveau par son bouton.</summary>
    private void PickDifficulty(int level)
    {
        var next = Math.Clamp(level, 0, AvailableLevels.Count - 1);
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

    private void Back()
    {
        Context.Sounds.Play("menu_click");   // bouton RETOUR, Échap ou B : même clic que les boutons du menu
        Context.Scenes.Change(new MainMenuScene(Context));
    }

    private void StartGame()
    {
        // Garde-fou : le bouton est déjà éteint, mais la manette et la souris passent par ici tous les deux.
        if (!IsUnlocked(Selected))
        {
            Context.Sounds.Play("menu_close");   // refus sec, comme un achat impossible dans l'arbre
            return;
        }

        Context.Sounds.Play("menu_click");
        Context.Saves.RecordCommanderRunStarted(Selected.Id);   // historique : une partie lancée
        Context.Scenes.Change(new GameplayScene(Context, _saveSlot, run: null,
            commander: Selected, difficulty: SelectedDifficulty));
    }

    // ── Navigation manette par rangées ────────────────────────────────────────────

    private void MoveFocus(NavDir dir)
    {
        if (_focusables.Count == 0)
            return;

        // Sur le commandant, GAUCHE/DROITE fait DÉFILER le carrousel (le focus reste sur le commandant, on
        // enchaîne les pions). Haut/bas quitte normalement la rangée vers les autres éléments.
        if (_focusables[_focus].Kind == Kind.Commander && (dir == NavDir.Left || dir == NavDir.Right))
        {
            StepCommander(dir == NavDir.Right ? +1 : -1);
            return;
        }

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

    /// <summary>
    /// Rangées EXPLICITES (les deux colonnes de l'écran se mélangeraient si on triait par Y) : carrousel →
    /// difficultés → unités de départ → arbre → retour/lancer. Une rangée vide (commandant seul) est sautée.
    /// </summary>
    private List<List<int>> BuildFocusRows()
    {
        var order = new[]
        {
            new[] { Kind.Commander }, new[] { Kind.DiffLevel }, new[] { Kind.StartTile },
            new[] { Kind.Tree }, new[] { Kind.Back, Kind.Start },
        };
        var rows = new List<List<int>>();
        foreach (var kinds in order)
        {
            var row = Enumerable.Range(0, _focusables.Count).Where(i => kinds.Contains(_focusables[i].Kind)).ToList();
            if (row.Count == 0)
                continue;
            row.Sort((a, b) => _focusables[a].Rect.Center.X.CompareTo(_focusables[b].Rect.Center.X));
            rows.Add(row);
        }
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
        var unlocked = IsUnlocked(def);

        // Kind sous le focus manette : l'élément focus est dessiné à l'état SURVOLÉ (pas de cadre).
        Kind? fk = gp && _focus < _focusables.Count ? _focusables[_focus].Kind : null;
        var hoverIndex = HoverIndex(gp, mouse);

        sb.Begin(samplerState: SamplerState.PointClamp);
        _backdrop.Draw(sb, vp.Width, vp.Height);

        // ── Moitié gauche ──
        DrawCarousel(sb, lay);
        Context.Font.DrawCentered(sb, Loc.T("commander.title"), lay.Title, 2, Palette.Yellow2);

        // Nom en grand (masqué si verrouillé), repli à l'échelle 2 s'il ne tient pas entre les flèches.
        var name = unlocked ? CommanderName(def) : "???";
        var nameScale = Context.Font.Measure(name, 3) <= lay.Name.Width ? 3 : 2;
        Context.Font.DrawCentered(sb, name, lay.Name, nameScale, unlocked ? Palette.White : Palette.Grey);

        // Les deux flèches s'enfoncent ensemble quand le commandant est focus : elles montrent que
        // GAUCHE/DROITE fait défiler le carrousel.
        Arrow(sb, lay.NamePrev, "<", mouse, gp, fk == Kind.Commander);
        Arrow(sb, lay.NameNext, ">", mouse, gp, fk == Kind.Commander);
        DrawDots(sb, lay.Dots);

        for (var i = 0; i < lay.DiffLevels.Length; i++)
            DrawDifficultyButton(sb, lay.DiffLevels[i], i, mouse, gp, fk == Kind.DiffLevel && FocusData() == i);
        DrawDifficultyEffects(sb, lay.Effects);

        // ── Cadre de droite ──
        DrawSheet(sb, lay, def, unlocked, mouse, gp, fk, hoverIndex);

        // ── Barre du bas ──
        Button(sb, lay.Back, Loc.T("commander.back"), mouse, gp, fk == Kind.Back, Palette.White);
        // LANCER : éteint sur un commandant verrouillé (le bouton doit dire ce qu'il fait).
        Button(sb, lay.Start, Loc.T("commander.start"), mouse, gp, fk == Kind.Start, Palette.Yellow2,
            enabled: unlocked);

        // Carte du pion de départ survolé/focus, par-dessus le reste.
        if (unlocked && TileUnderPointer(lay, hoverIndex) is { } tile)
            _card.DrawCardNear(sb, tile.Cls, tile.Domaine, tile.Rect, vp.Bounds);
        sb.End();

        if (_tree.IsOpen)
            _tree.Draw(sb, vp, vp.Bounds, _preview);
    }

    /// <summary>
    /// Le carrousel : le pion choisi à l'échelle 4, ses voisins à l'échelle 2, tous centrés verticalement sur
    /// le sprite choisi. Pendant le glissement la POSITION et le FONDU varient continûment ; l'échelle, qui
    /// doit rester entière, bascule à mi-course (au passage de la demi-distance entre deux emplacements).
    ///
    /// On dessine jusqu'aux emplacements ±2 pendant le glissement, sinon un trou apparaîtrait du côté d'où
    /// arrive le nouveau pion. Le plus lointain est de toute façon rendu invisible par le fondu.
    /// </summary>
    private void DrawCarousel(SpriteBatch sb, Layout lay)
    {
        var offset = SlideOffset();
        var cx = lay.Sprite.Center.X;
        var far = _slideT > 0f ? 2 : 1;

        // Du plus lointain au plus proche : le pion courant se dessine en dernier, donc par-dessus.
        for (var rank = far; rank >= 0; rank--)
            for (var slot = -rank; slot <= rank; slot += Math.Max(1, 2 * rank))
                DrawSlot(sb, lay, CommanderAt(slot), cx + slot * SlotSpacing + offset, cx);
    }

    /// <summary>Un emplacement du carrousel : le sprite seul, sans socle ni cadre.</summary>
    private void DrawSlot(SpriteBatch sb, Layout lay, CommandeDef def, int x, int centerX)
    {
        var distance = Math.Abs(x - centerX) / (float)SlotSpacing;
        var alpha = Math.Clamp(1f - distance * FadePerSlot, 0f, 1f);
        if (alpha <= 0f)
            return;

        // VERROUILLÉ : silhouette noire, comme un pion non découvert du Codex. Le fondu du carrousel
        // s'applique par-dessus, donc un voisin verrouillé s'estompe comme les autres.
        var unlocked = IsUnlocked(def);
        var scale = distance < 0.5f ? MainScale : SideScale;
        var center = new Point(x, lay.Sprite.Center.Y);
        _card.DrawScaled(sb, def.BaseClass, center, scale, (unlocked ? Color.White : Color.Black) * alpha);

        // En démo, tampon DEMO sur les commandants verrouillés (réservés au jeu complet).
        if (Context.Settings.IsDemo && !unlocked)
        {
            var box = 64 * scale;
            Context.Font.DrawCentered(sb, Loc.T("menu.demo"),
                new Rectangle(x - box / 2, center.Y - box / 2, box, box), scale / 2, Palette.Yellow2 * alpha);
        }
    }

    /// <summary>Pastilles de position : une barre par commandant, celle du courant plus large et dorée.</summary>
    private void DrawDots(SpriteBatch sb, Rectangle area)
    {
        if (!HasChoice)
            return;
        var x = area.X;
        for (var i = 0; i < _commanders.Count; i++)
        {
            var current = i == _index;
            var w = current ? DotActiveW : DotSize;
            Fill(sb, new Rectangle(x, area.Y, w, DotSize), current ? Palette.Yellow2 : Palette.Black5);
            x += w + DotGap;
        }
    }

    // ── Difficulté ──

    /// <summary>
    /// Campagne déjà GAGNÉE à ce niveau avec le commandant COURANT du carrousel — un niveau plus dur compte
    /// pour tous ceux du dessous (cf. <see cref="SaveService.HasWonWith"/>).
    /// </summary>
    private bool AlreadyWon(int level) =>
        level >= 0 && level < DifficultySettings.AllLevels.Count
        && Context.Saves.HasWonWith(Selected.Id, DifficultySettings.AllLevels[level]);

    /// <summary>
    /// Un niveau de difficulté : son nom à gauche, sa jauge de danger à droite (1 à 3 carrés allumés, en
    /// <c>Purple5</c>, ou en <c>Grey</c> si ce niveau est déjà gagné avec ce commandant). Le niveau retenu a
    /// le fond clair « sélection » et son nom en <c>Yellow2</c>.
    /// </summary>
    private void DrawDifficultyButton(SpriteBatch sb, Rectangle r, int level, Point mouse, bool gp, bool focusedGp)
    {
        var selected = level == _difficultyIndex;
        var hover = !gp && r.Contains(mouse);
        var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(hover || focusedGp,
            hover && Context.Input.IsLeftDown), selected);

        var textY = r.Y + (r.Height - Context.Font.GlyphHeight) / 2 + dy;
        Context.Font.Draw(sb, DifficultyName(DifficultySettings.AllLevels[level]), new Vector2(r.X + 8, textY), 1,
            selected ? Palette.Yellow2 : Palette.White);

        var lit = level + 1;
        var litColor = AlreadyWon(level) ? Palette.Grey : Palette.Purple5;
        var gx = r.Right - 10 - 3 * GaugeSquare - 2 * GaugeGap;
        var gy = r.Y + (r.Height - GaugeSquare) / 2 + dy;
        for (var i = 0; i < 3; i++)
            Fill(sb, new Rectangle(gx + i * (GaugeSquare + GaugeGap), gy, GaugeSquare, GaugeSquare),
                i < lit ? litColor : Palette.Black1);
    }

    /// <summary>
    /// Sous les boutons, TOUJOURS visible : ce que change le niveau retenu (un tiret par ligne), puis, s'il est
    /// déjà gagné avec ce commandant, une ligne dorée « à cette difficulté » ou « tous les niveaux ».
    /// </summary>
    private void DrawDifficultyEffects(SpriteBatch sb, Point at)
    {
        const int bullet = 10;
        var y = at.Y;
        foreach (var line in DifficultyLines(_difficultyIndex))
        {
            // preserveCase : ce sont des PHRASES, pas des libellés d'UI — et la police ne dessine les
            // accents QU'EN MINUSCULES (en capitales, « é » retombe sur « E »).
            Context.Font.Draw(sb, "-", new Vector2(at.X, y), 1, Palette.White);
            Context.Font.Draw(sb, line, new Vector2(at.X + bullet, y), 1, Palette.White, preserveCase: true);
            y += EffectStep;
        }
        if (AlreadyWon(_difficultyIndex))
        {
            var allWon = AlreadyWon(DifficultySettings.AllLevels.Count - 1);
            Context.Font.Draw(sb, Loc.T(allWon ? "difficulty.already_won" : "difficulty.already_won_level"),
                new Vector2(at.X, y + 3), 1, Palette.Yellow2, preserveCase: true);
        }
    }

    /// <summary>
    /// Ce que change un niveau : la précision de l'IA, puis — s'ils montent par rapport au niveau juste en
    /// dessous — les vagues, l'équipement ennemi, le quota de la mission spéciale et l'absence de recommencer.
    /// Volontairement sans chiffre : le joueur n'a pas à savoir qu'un seul pion est promu, seulement que ça monte.
    /// </summary>
    private List<string> DifficultyLines(int level)
    {
        var difficulty = DifficultySettings.AllLevels[level];
        // L'IA : même grammaire que les autres leviers — on dit ce qui CHANGE par rapport au palier du
        // dessous. Le niveau le plus bas n'a rien en dessous, il se décrit donc lui-même.
        var lines = new List<string>
        {
            Loc.T(!SharperAi(level) ? "difficulty.ai_low"
                : SharperAi(level - 1) ? "difficulty.ai_fewer_again"
                : "difficulty.ai_fewer"),
        };
        // « Encore plus » dès qu'un niveau INFÉRIEUR faisait déjà monter le même levier.
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
        return lines;
    }

    /// <summary>Vrai si l'IA de <paramref name="level"/> se trompe moins que celle du niveau juste en dessous.</summary>
    private static bool SharperAi(int level) =>
        Climbs(level, s => s.AiAccuracy);

    /// <summary>Vrai si <paramref name="level"/> envoie des vagues plus fortes que le niveau juste en dessous.</summary>
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

    // ── Fiche du commandant (cadre de droite) ──

    /// <summary>
    /// La fiche, toujours visible : stats, gains de points, unités de départ, bouton de l'arbre, historique.
    /// Commandant VERROUILLÉ : les valeurs sont tues (« ? »), les unités en silhouette et toute la fiche passe
    /// sous un voile SOMBRE ; seule l'annonce du verrou reste lisible par-dessus.
    /// </summary>
    private void DrawSheet(SpriteBatch sb, Layout lay, CommandeDef def, bool unlocked, Point mouse, bool gp,
        Kind? fk, int hoverIndex)
    {
        Context.Style.DrawPanel(sb, lay.Frame);
        var inner = lay.Inner;
        var body = inner.X + BodyIndent;
        string V(string value) => unlocked ? value : "?";

        // 1. STATS, sur deux colonnes séparées par un filet vertical.
        Context.Font.Draw(sb, Loc.T("commander.stats"), new Vector2(inner.X, lay.StatsY), 1, Palette.Yellow1);
        var rowsY = lay.StatsY + TitleStep;
        var c = def.BaseClass;
        var leftLabels = new[] { Loc.T("stat.hp"), Loc.T("stat.power"), Loc.T("stat.movement"), Loc.T("stat.range") };
        var leftValues = new[] { c.MaxHp.ToString(), c.Damage.ToString(), c.MoveRange.ToString(), c.AttackRange.ToString() };
        var leftColors = new[] { Palette.White, Palette.Brown3, Palette.Cyan2, Palette.Yellow2 };
        var labelW = LabelWidth(leftLabels);
        for (var i = 0; i < 4; i++)
            LabelValue(sb, body, rowsY + i * RowH, labelW, leftLabels[i], V(leftValues[i]), leftColors[i]);

        var statsBottom = rowsY + Math.Max(4 * RowH, 2 * RowH + DomaineBadge);
        var vx = body + labelW + StatValueW + 12;
        Fill(sb, new Rectangle(vx, rowsY - 2, 1, statsBottom - rowsY), Palette.Black1);
        Fill(sb, new Rectangle(vx + 1, rowsY - 2, 1, statsBottom - rowsY), Palette.Black5);

        var col2 = vx + 14;
        var rightLabels = new[] { Loc.T("commander.deploy"), Loc.T("commander.reserve"), Loc.T("commander.movement_pattern") };
        var rightW = LabelWidth(rightLabels);
        LabelValue(sb, col2, rowsY, rightW, rightLabels[0], V(def.Deployments.ToString()), Palette.White);
        LabelValue(sb, col2, rowsY + RowH, rightW, rightLabels[1], V(def.ReserveSize.ToString()), Palette.White);
        var badge = new Rectangle(col2 + rightW, rowsY + 2 * RowH, DomaineBadge, DomaineBadge);
        Context.Font.Draw(sb, rightLabels[2],
            new Vector2(col2, badge.Center.Y - Context.Font.GlyphHeight / 2), 1, Palette.White);
        if (unlocked)
            _card.DrawDomaineBadge(sb, def.Movement, badge);   // le domaine RÉVÈLE une information : tu si verrouillé
        else
            Context.Font.DrawCentered(sb, "?", badge, 1, Palette.White);

        HSeparator(sb, inner, lay.Sep1Y);

        // 2. GAIN DE POINT DE COMMANDE.
        Context.Font.Draw(sb, Loc.T("commander.points"), new Vector2(inner.X, lay.PointsY), 1, Palette.Yellow1);
        var py = lay.PointsY + TitleStep;
        foreach (var line in lay.PointLines)
        {
            var duo = line.Length > 0 && line[0] == DuoMark;
            Context.Font.Draw(sb, duo ? line[1..] : line, new Vector2(body, py), 1, duo ? Palette.Yellow2 : Palette.White);
            py += PointStep;
        }

        HSeparator(sb, inner, lay.Sep2Y);

        // 3. UNITÉS DE DÉPART (titre remplacé par l'annonce du verrou, dessinée après le voile).
        if (unlocked)
            Context.Font.Draw(sb, Loc.T("commander.starting_units"), new Vector2(inner.X, lay.UnitsY), 1, Palette.Yellow1);
        if (unlocked && lay.StartTiles.Count == 0)
            Context.Font.Draw(sb, Loc.T("commander.alone"), new Vector2(body, lay.UnitsY + TitleStep), 1, Palette.Grey);
        for (var i = 0; i < lay.StartTiles.Count; i++)
            _card.DrawTile(sb, lay.StartTiles[i].Cls, lay.StartTiles[i].Rect,
                unlocked && IsTileHighlighted(i, hoverIndex), revealed: unlocked);

        // 4. ARBRE DE COMPÉTENCE (consultable seulement sur un commandant débloqué : il révélerait ses effets).
        DrawTreeButton(sb, lay.Tree, mouse, gp, fk == Kind.Tree, unlocked);

        // 5. HISTORIQUE, grille 2×2.
        var history = Context.Saves.CommanderHistory(def.Id);
        Context.Font.Draw(sb, Loc.T("commander.history"), new Vector2(inner.X, lay.HistoryY), 1, Palette.Yellow1);
        var hy = lay.HistoryY + TitleStep;
        var hcol2 = inner.X + inner.Width / 2 + BodyIndent;
        var histLabels = new[] { Loc.T("commander.runs"), Loc.T("commander.wins"), Loc.T("commander.kills"), Loc.T("commander.playtime") };
        var histW = Math.Max(100, histLabels.Max(s => Context.Font.Measure(s, 1)) + 12);
        HistoryItem(sb, body, hy, histW, histLabels[0], history.RunsStarted.ToString(), Palette.White);
        HistoryItem(sb, hcol2, hy, histW, histLabels[1], history.RunsWon.ToString(), Palette.Yellow1);
        HistoryItem(sb, body, hy + RowH, histW, histLabels[2], history.EnemiesKilled.ToString(), Palette.White);
        HistoryItem(sb, hcol2, hy + RowH, histW, histLabels[3], TimeText.Hours(history.PlayTimeSeconds), Palette.White);

        if (!unlocked)
        {
            // Fiche d'un commandant verrouillé : tout le contenu s'assombrit, seule l'annonce du verrou ressort.
            Fill(sb, new Rectangle(lay.Frame.X + 3, lay.Frame.Y + 3, lay.Frame.Width - 6, lay.Frame.Height - 6),
                Palette.Black1 * 0.6f);
            Context.Font.Draw(sb, Loc.T(Context.Settings.IsDemo ? "commander.locked_demo" : "commander.locked"),
                new Vector2(inner.X, lay.UnitsY), 1, Palette.Grey);
        }
    }

    /// <summary>Largeur de la colonne des libellés : au moins la cote de la spec, plus si une langue l'exige.</summary>
    private int LabelWidth(IEnumerable<string> labels) =>
        Math.Max(StatLabelW, labels.Max(s => Context.Font.Measure(s, 1)) + 8);

    /// <summary>Ligne « libellé, puis valeur alignée à droite sur <see cref="StatValueW"/> ».</summary>
    private void LabelValue(SpriteBatch sb, int x, int y, int labelW, string label, string value, Color color)
    {
        Context.Font.Draw(sb, label, new Vector2(x, y), 1, Palette.White);
        var vw = Context.Font.Measure(value, 1);
        Context.Font.Draw(sb, value, new Vector2(x + labelW + StatValueW - vw, y), 1, color);
    }

    /// <summary>Case d'historique : libellé puis valeur calée à gauche en colonne.</summary>
    private void HistoryItem(SpriteBatch sb, int x, int y, int labelW, string label, string value, Color color)
    {
        Context.Font.Draw(sb, label, new Vector2(x, y), 1, Palette.White);
        Context.Font.Draw(sb, value, new Vector2(x + labelW, y), 1, color);
    }

    /// <summary>Filet horizontal : 1 px <c>Black1</c> + 1 px <c>Black5</c> dessous, sur la largeur utile.</summary>
    private void HSeparator(SpriteBatch sb, Rectangle inner, int y)
    {
        Fill(sb, new Rectangle(inner.X, y, inner.Width, 1), Palette.Black1);
        Fill(sb, new Rectangle(inner.X, y + 1, inner.Width, 1), Palette.Black5);
    }

    /// <summary>
    /// Bouton ARBRE DE COMPÉTENCE, toute la largeur du cadre : icône (<c>Assets/UI/arbre.png</c>, ou un petit
    /// arbre à trois nœuds dessiné à défaut) puis le libellé, le tout centré.
    /// </summary>
    private void DrawTreeButton(SpriteBatch sb, Rectangle r, Point mouse, bool gp, bool focusedGp, bool enabled)
    {
        var hover = enabled && !gp && r.Contains(mouse);
        var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(hover || (enabled && focusedGp),
            hover && Context.Input.IsLeftDown));
        var color = enabled ? Palette.White : Palette.Grey;
        var label = Loc.T("commander.tree");

        var png = _treeIcon != null && _treeIcon.Height <= r.Height - 8 ? _treeIcon : null;
        var iconW = png?.Width ?? 11;
        var iconH = png?.Height ?? 9;
        const int iconGap = 10;
        var total = iconW + iconGap + Context.Font.Measure(label, 1);
        var x = r.Center.X - total / 2;
        var iy = r.Y + (r.Height - iconH) / 2 + dy;

        if (png != null)
        {
            sb.Draw(png, new Vector2(x, iy), enabled ? Color.White : Palette.Grey);
        }
        else
        {
            // Repli : un nœud haut relié par un T à deux nœuds bas — lisible même en 11×9.
            Fill(sb, new Rectangle(x + 4, iy, 3, 3), color);
            Fill(sb, new Rectangle(x + 5, iy + 3, 1, 2), color);
            Fill(sb, new Rectangle(x + 1, iy + 4, 9, 1), color);
            Fill(sb, new Rectangle(x + 1, iy + 5, 1, 1), color);
            Fill(sb, new Rectangle(x + 9, iy + 5, 1, 1), color);
            Fill(sb, new Rectangle(x, iy + 6, 3, 3), color);
            Fill(sb, new Rectangle(x + 8, iy + 6, 3, 3), color);
        }
        Context.Font.Draw(sb, label, new Vector2(x + iconW + iconGap,
            r.Y + (r.Height - Context.Font.GlyphHeight) / 2 + dy), 1, color);
    }

    // ── Aides ──

    /// <summary>Index du focusable courant (manette), ou -1.</summary>
    private int FocusData() => _focus < _focusables.Count ? _focusables[_focus].Data : -1;

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

    /// <summary>Flèche du carrousel. <paramref name="focusedGp"/> : dessinée enfoncée (commandant focus à la manette).</summary>
    private void Arrow(SpriteBatch sb, Rectangle r, string glyph, Point mouse, bool gp, bool focusedGp)
    {
        // Un seul commandant : les flèches restent visibles mais éteintes, pour que l'écran ne mente pas
        // sur ce qui est cliquable.
        if (!HasChoice)
        {
            Context.Style.DrawRecessed(sb, r);
            Context.Font.DrawCentered(sb, glyph, r, 3, Palette.Grey);
            return;
        }

        var hover = !gp && r.Contains(mouse);
        var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(hover || focusedGp, hover && Context.Input.IsLeftDown));
        var area = r; area.Offset(0, dy);
        Context.Font.DrawCentered(sb, glyph, area, 3, Palette.White);
    }

    /// <summary>Bouton texte (échelle 1) : survol / focus manette = enfoncé + fond clair, jamais de cadre.</summary>
    private void Button(SpriteBatch sb, Rectangle r, string label, Point mouse, bool gp, bool focusedGp,
        Color color, bool enabled = true)
    {
        var hover = enabled && !gp && r.Contains(mouse);
        var dy = Context.Style.DrawButton(sb, r, UiStyle.StateOf(hover || (enabled && focusedGp),
            hover && Context.Input.IsLeftDown));
        var area = r; area.Offset(0, dy);
        Context.Font.DrawCentered(sb, label, area, 1, enabled ? color : Palette.Grey);
    }

    private static string CommanderName(CommandeDef def) =>
        Loc.TOr("commander." + def.Id, def.Name).ToUpperInvariant();

    private static string DifficultyName(Difficulty d) => Loc.T("difficulty." + d.ToString().ToLowerInvariant());

    private void Fill(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(Context.Pixel, r, c);
}
