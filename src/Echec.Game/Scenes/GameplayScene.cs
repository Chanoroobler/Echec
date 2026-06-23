using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Campaign;
using Echec.Core.Map;
using Echec.Engine;
using Echec.Engine.Rendering;
using Echec.Engine.Scenes;
using Echec.Engine.UI;
using Echec.Game.Dev;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Echec.Game.Scenes;

/// <summary>
/// Scène de campagne (première boucle de gameplay) : terrain 8×8, boucle
/// Placement → Combat → Recrutement → … sur 6 combats, le dernier étant le boss.
/// Le commandant (mort = game over) est posé d'office ; le joueur déploie le reste de
/// son inventaire par glisser-déposer depuis le panneau de droite, puis combat l'IA.
/// Échap = menu pause, F1 = visualiseur d'arbres (dev).
/// </summary>
public sealed class GameplayScene : Scene
{
    private const int Columns = 8;
    private const int Rows = 8;
    private const double AiDelaySeconds = 0.45;

    // Remontée du sprite (fraction de la case) pour centrer le socle sur la case. 0 = dans la case.
    private const float SpriteLiftFraction = 0.25f;

    // Panneau latéral droit (inventaire au placement, infos en combat).
    private const int RightPanelWidth = 220;
    private const int PanelPad = 12;
    // Inventaire en grille : portraits 64×64 NATIFS (jamais redimensionnés), 2 colonnes.
    private const int InvIconSize = 64;
    private const int InvCols = 2;
    private const int InvGapX = 8;
    private const int InvCellH = InvIconSize + 14; // portrait + libellé dessous
    private const int InvGapY = 6;
    private const int PanelListTop = 110;

    // Ordre des colonnes du centre vers les bords (déploiement groupé au milieu).
    private static readonly int[] CenterOut = { 3, 4, 2, 5, 1, 6, 0, 7 };

    // Chemins d'assets résolus depuis le dossier de l'exe (indépendant du répertoire de travail).
    private static string AssetPath(string relative) =>
        System.IO.Path.Combine(System.AppContext.BaseDirectory, relative);

    // Terrain régénéré à CHAQUE combat (obstacles eau/montagne aléatoires) — voir BeginPlacement.
    private Battlefield _battlefield = Battlefield.CreateFlat(Columns, Rows);

    // Texture de tuile par type de terrain (PNG Assets/Tiles, repli sur un aplat coloré 64×80).
    private readonly Dictionary<TerrainType, Texture2D> _tiles = new();
    private WaterRenderer _water = null!;
    private Texture2D _waterNoise = null!;
    private float _time;
    private PauseMenu _pauseMenu = null!;
    private PauseMenuRenderer _pauseRenderer = null!;
    private DomaineTreeRenderer _treeRenderer = null!;

    // Effets de combat shader (dissolution / flash) + animation d'attaque en cours.
    private CombatFxRenderer _combatFx = null!;
    private readonly MeleeStrikeFx _fx = new();
    // Étincelles d'impact (particules poolées) + garde-fou « émises une seule fois par coup ».
    private readonly SparkBurst _sparks = new();
    private bool _sparksEmitted;

    // Sprites d'unités 64×64 chargés depuis Assets/Units/<asset>.png (null = pas d'asset → placeholder).
    private const string UnitAssetFolder = "Assets/Units";
    private readonly Dictionary<string, Texture2D?> _unitSprites = new();

    private Run _run = null!;
    private Match _match = null!;

    // Orientation visuelle par unité : true = regarde vers le bas (face caméra). Suit la dernière
    // action verticale (déplacement/attaque) ; défaut = vers l'adversaire (cf. DefaultFacesDown).
    private readonly Dictionary<Unit, bool> _facesDown = new();

    // Lien unité déployée → gabarit d'inventaire, pour calculer les pertes après combat.
    private readonly Dictionary<Unit, UnitSpec> _playerSpec = new();
    // Lien unité ennemie → son gabarit, pour proposer les vaincus au recrutement.
    private readonly Dictionary<Unit, UnitSpec> _enemySpec = new();
    // Ennemis tués pendant le combat, DANS L'ORDRE de leur mort (le recrutement prend les 3 derniers).
    private readonly List<UnitSpec> _enemyKillOrder = new();
    // Unités du joueur encore dans l'inventaire (non déployées).
    private readonly List<UnitSpec> _pending = new();
    private string _defeatReason = "";

    // Glisser-déposer du placement.
    private UnitSpec? _dragSpec;
    private Cell? _dragFrom; // origine si on déplace une unité déjà posée (null = vient de l'inventaire)

    private Cell? _selected;
    // Buffers RÉUTILISÉS (remplis par les variantes sans-alloc du Match) : évitent une allocation
    // de liste à chaque sélection / chaque frame de survol.
    private readonly List<Cell> _legalMoves = new();
    private readonly List<Cell> _attackTargets = new();   // cases avec un ennemi réellement à portée
    private readonly List<Cell> _attackReach = new();     // toute la PORTÉE de tir (cases atteintes, même vides)
    private readonly List<Cell> _threatCells = new();
    // Aperçu au SURVOL d'un pion joueur (rien de sélectionné) : buffers distincts de la sélection.
    private readonly List<Cell> _hoverMoves = new();
    private readonly List<Cell> _hoverAttackTargets = new();
    private readonly List<Cell> _hoverReach = new();
    private double _aiTimer;
    private bool _showTrees;

    // Cache du GridLayout : déterministe selon la résolution virtuelle, donc recalculé seulement
    // au changement de taille (au lieu de plusieurs allocations de GridLayout par frame).
    private GridLayout? _layoutCache;
    private Point _layoutCacheFor = new(-1, -1);
    // Invalidé en plus de la résolution quand le zoom ou la caméra changent (pan / molette).
    private bool _layoutDirty = true;

    // Caméra : un SEUL cran de zoom supplémentaire (zoom entier +1, pixel-perfect) et un décalage
    // de pan (px canvas) ajouté à l'origine centrée. Le pan n'a d'effet que si le plateau déborde
    // la zone de jeu (terrain trop grand ou zoomé) ; sinon il reste verrouillé au centre.
    private bool _zoomedIn;
    private Vector2 _camera;
    private const float CameraPanSpeed = 540f;   // px canvas / s au clavier

    // Animation d'entrée en combat : le panneau de droite glisse hors écran et le plateau se recentre
    // sur toute la largeur, de façon fluide. Compte à rebours (s) ; > 0 = glissement en cours.
    private double _battleIntroTimer;
    private const double BattleIntroDuration = 0.35;

    // Animation « pose » : la dernière case où un pion s'est posé rebondit brièvement.
    // Un seul pion bouge à la fois (jeu au tour par tour) → un seul état suffit.
    private Cell? _landingCell;
    private double _landingTimer;
    private const double LandingDuration = 0.20;
    // Soulèvement du pion sélectionné (« tenu en main »), en fraction de la case.
    private const float HeldLiftFraction = 0.09f;
    // Amplitude du rebond de pose, en fraction de la case.
    private const float LandingLiftFraction = 0.13f;

    // Glisser-déposer en COMBAT : case d'origine du pion soulevé à la souris (null = aucun).
    private Cell? _combatDragFrom;
    // Soulèvement du pion PORTÉ à la souris (plus marqué que la simple sélection).
    private const float CarriedLiftFraction = 0.22f;

    // Ombre PROJETÉE (silhouette du sprite) : cisaillement latéral + bascule/aplatissement vers le bas,
    // ancrée à la base du socle. Une vraie ombre portée plutôt qu'une ellipse posée.
    private const float ShadowShear = 0.55f;          // inclinaison latérale (0 = tout droit)
    private const float ShadowFlatten = -0.45f;       // < 0 : rabat la silhouette au sol vers l'avant + aplatit
    private const float ShadowAlpha = 0.38f;          // opacité de l'ombre (au sol)
    private const float ShadowAnchorFraction = 0.94f; // hauteur de la base du socle dans le sprite (0 haut … 1 bas)
    // Réaction au soulèvement : quand le pion est en l'air, l'ombre GLISSE (direction lumière) et S'ÉCLAIRCIT.
    private const float ShadowLiftSlide = 0.85f;      // px de glissement de l'ombre par px de soulèvement
    private const float ShadowLiftFade = 0.5f;        // part d'opacité perdue à pleine hauteur (0 = aucune)

    // Slot de sauvegarde piloté depuis le menu principal : la progression est auto-sauvegardée en
    // phase de placement et le slot est effacé à la fin de la run (victoire/défaite).
    private readonly int _saveSlot;
    private Run? _initialRun;

    /// <param name="saveSlot">Index du slot (0..2) où sauvegarder la progression.</param>
    /// <param name="run">Run à reprendre (depuis une sauvegarde), ou null pour une nouvelle partie.</param>
    public GameplayScene(GameContext context, int saveSlot, Run? run = null) : base(context)
    {
        _saveSlot = saveSlot;
        _initialRun = run;
    }

    /// <summary>Viewport logique (espace virtuel) dans lequel l'UI se met en page.</summary>
    private Viewport VirtualViewport =>
        new(0, 0, Context.VirtualResolution.X, Context.VirtualResolution.Y);

    public override void Load()
    {
        LoadTiles();
        _water = LoadWater();

        var native = Context.GraphicsDevice.Adapter.CurrentDisplayMode;
        _pauseMenu = new PauseMenu(Context.Settings, new Point(native.Width, native.Height));
        _pauseRenderer = new PauseMenuRenderer(Context.Pixel, Context.Font, Context.Style);
        _treeRenderer = new DomaineTreeRenderer(Context.Pixel, Context.Font, Context.Style);
        _combatFx = LoadCombatFx();

        StartRun();
    }

    public override void Unload()
    {
        foreach (var tile in _tiles.Values)
            tile.Dispose();
        _tiles.Clear();
        _waterNoise.Dispose();
        _water.Dispose();
        foreach (var sprite in _unitSprites.Values)
            sprite?.Dispose();
        _unitSprites.Clear();
    }

    /// <summary>
    /// Charge le shader d'eau (repli silencieux si le content pipeline n'a pas produit le .xnb)
    /// et génère la texture de bruit qui supporte le défilement du courant.
    /// </summary>
    private WaterRenderer LoadWater()
    {
        Effect? effect = null;
        try { effect = Context.Content.Load<Effect>("Effects/Water"); }
        catch { effect = null; }

        _waterNoise = Textures.CreateNoise(Context.GraphicsDevice);
        return new WaterRenderer(Context.GraphicsDevice, effect, _waterNoise, Context.Pixel);
    }

    /// <summary>
    /// Charge la texture de chaque type de terrain depuis Assets/Tiles (grass/water/mountain.png),
    /// avec repli sur un aplat coloré 64×80 (palette) si le PNG est absent — le jeu reste jouable
    /// avant d'avoir l'art définitif.
    /// </summary>
    private void LoadTiles()
    {
        _tiles[TerrainType.Grass] = Textures.LoadTileOrPlaceholder(
            Context.GraphicsDevice, AssetPath("Assets/Tiles/grass.png"));
        // Eau : placeholder TRANSLUCIDE (test) → on voit le shader d'eau animé sous la case. Surface
        // très légère + liseré plus net pour repérer la case (l'eau bloque le déplacement).
        _tiles[TerrainType.Water] =
            Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath("Assets/Tiles/water.png"))
            ?? Textures.CreateTransparentTile(Context.GraphicsDevice,
                WithAlpha(Palette.WaterShallow, 48), WithAlpha(Palette.WaterShallow, 140));
        _tiles[TerrainType.Mountain] = LoadTile("mountain.png", Palette.Blue1, Palette.Black4);
    }

    private Texture2D LoadTile(string file, Color surface, Color side) =>
        Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath($"Assets/Tiles/{file}"))
        ?? Textures.CreateColorTile(Context.GraphicsDevice, surface, side);

    /// <summary>Couleur de la palette avec un alpha imposé (placeholders translucides).</summary>
    private static Color WithAlpha(Color c, byte alpha) => new(c.R, c.G, c.B, alpha);

    /// <summary>Charge le shader d'effets de combat (repli silencieux : dissolution en fondu si absent).</summary>
    private CombatFxRenderer LoadCombatFx()
    {
        Effect? effect = null;
        try { effect = Context.Content.Load<Effect>("Effects/CombatFx"); }
        catch { effect = null; }
        return new CombatFxRenderer(effect);
    }

    // ── Cycle de campagne ─────────────────────────────────────────────────────────

    private void StartRun()
    {
        _run = _initialRun ?? new Run();   // reprise depuis une sauvegarde, ou nouvelle campagne
        _initialRun = null;                // ne sert qu'au tout premier chargement de la scène
        BeginPlacement();
    }

    /// <summary>Prépare la phase de placement : nouveau terrain, commandant posé d'office.</summary>
    private void BeginPlacement()
    {
        // Terrain propre à ce combat : herbe + obstacles eau/montagne aléatoires (zone neutre, symétrique).
        _battlefield = _run.BuildBattlefield(Columns, Rows);
        _match = new Match(Columns, Rows, _battlefield);
        _facesDown.Clear();
        _playerSpec.Clear();
        _enemySpec.Clear();
        _enemyKillOrder.Clear();
        _pending.Clear();
        _dragSpec = null;
        _dragFrom = null;
        ClearSelection();
        ResetCamera();
        _aiTimer = 0;

        var commander = _run.Commander;
        PlacePlayer(commander, new Cell(Columns / 2, Rows - 1));

        foreach (var spec in _run.Roster)
            if (spec != commander)
                _pending.Add(spec);

        // La vague ennemie est posée dès le placement : le joueur voit le déploiement
        // adverse avant de positionner ses pièces (rangées 0-1, hors zone joueur).
        PlaceEnemies(_run.BuildEnemyWave());

        // Auto-sauvegarde : la progression n'est persistée qu'ici (phase de placement), jamais en
        // plein combat — on reprend toujours proprement au placement du combat courant.
        Context.Saves.SaveSlot(_saveSlot, RunSave.From(_run));
    }

    /// <summary>Fin du placement : lance le combat (la vague ennemie est déjà posée).</summary>
    private void BeginBattle()
    {
        CancelDrag();
        _run.StartBattle();
        ClearSelection();
        // Le panneau de droite glisse hors écran et le plateau se recentre : animation d'entrée.
        _battleIntroTimer = BattleIntroDuration;
        MarkLayoutDirty();
        _aiTimer = 0;
        Context.Sounds.Play("battle_start");
    }

    private void PlacePlayer(UnitSpec spec, Cell cell)
    {
        var unit = spec.Spawn(Faction.Player);
        _match.Place(cell, unit);
        _playerSpec[unit] = spec;
        TriggerLanding(cell);
    }

    private void PlaceEnemies(List<UnitSpec> wave)
    {
        var cells = EnemyDeployCells().ToList();
        var i = 0;
        foreach (var spec in wave)
        {
            while (i < cells.Count
                && (_match.UnitAt(cells[i]) != null || _battlefield[cells[i]].Terrain.BlocksMovement()))
                i++;
            if (i >= cells.Count) break;
            var unit = spec.Spawn(Faction.Enemy);
            _match.Place(cells[i], unit);
            _enemySpec[unit] = spec;          // pour retrouver le gabarit à la mort (recrutement)
            i++;
        }
    }

    private static bool IsPlayerZone(Cell cell) => cell.Row >= Rows - 2;

    private static IEnumerable<Cell> PlayerDeployCells()
    {
        for (var row = Rows - 1; row >= Rows - 2; row--)
            foreach (var col in CenterOut)
                yield return new Cell(col, row);
    }

    private static IEnumerable<Cell> EnemyDeployCells()
    {
        for (var row = 0; row <= 1; row++)
            foreach (var col in CenterOut)
                yield return new Cell(col, row);
    }

    // ── Mise à jour ─────────────────────────────────────────────────────────────
    public override void Update(GameTime gameTime)
    {
        // Le courant d'eau avance en continu (même en pause / menus).
        _time += (float)gameTime.ElapsedGameTime.TotalSeconds;

        if (_landingTimer > 0)
            _landingTimer -= gameTime.ElapsedGameTime.TotalSeconds;

        // Outil dev (F1) : prioritaire, fige le reste.
        if (Context.Input.WasKeyPressed(Keys.F1) && !_pauseMenu.IsOpen)
            _showTrees = !_showTrees;
        if (_showTrees)
        {
            if (Context.Input.WasKeyPressed(Keys.Escape))
                _showTrees = false;
            return;
        }

        if (Context.Input.WasKeyPressed(Keys.Escape))
        {
            if (_pauseMenu.IsOpen) { _pauseMenu.Back(); Context.Sounds.Play("menu_close"); }
            else { _pauseMenu.Open(); Context.Sounds.Play("menu_open"); }
        }

        if (_pauseMenu.IsOpen) { UpdatePauseMenu(); return; }

        // Zoom (molette) + pan (flèches / ZQSD) uniquement sur les phases avec plateau, et pas pendant
        // le glissement d'entrée en combat (l'animation pilote seule le cadrage à ce moment-là).
        if (_run.Phase is RunPhase.Placement or RunPhase.Battle && _battleIntroTimer <= 0)
            UpdateCamera(gameTime);

        switch (_run.Phase)
        {
            case RunPhase.Placement: UpdatePlacement(); break;
            case RunPhase.Battle: UpdateBattle(gameTime); break;
            case RunPhase.Recruitment: UpdateRecruitment(); break;
            case RunPhase.Victory:
            case RunPhase.Defeat:
                // Run terminée (slot déjà effacé) : un clic ramène au menu principal.
                if (Context.Input.WasLeftClicked)
                    Context.Scenes.Change(new MainMenuScene(Context));
                break;
        }
    }

    private void UpdatePlacement()
    {
        if (Context.Input.WasKeyPressed(Keys.Enter))
        {
            BeginBattle();
            return;
        }

        var mouse = Context.Input.MousePosition;
        if (Context.Input.WasLeftClicked)
            BeginDrag(mouse);
        else if (Context.Input.WasLeftReleased && _dragSpec != null)
            EndDrag(mouse);
    }

    private void BeginDrag(Point mouse)
    {
        // 1. Prise depuis l'inventaire (carte du panneau de droite).
        if (PanelCardAt(mouse) is { } i)
        {
            _dragSpec = _pending[i];
            _pending.RemoveAt(i);
            _dragFrom = null;
            Context.Sounds.Play("unit_pick");
            return;
        }

        // 2. Prise d'une unité déjà posée (on la retire du terrain en attendant le drop).
        if (CellUnderMouse() is { } cell
            && _match.UnitAt(cell) is { Faction: Faction.Player } unit
            && _playerSpec.TryGetValue(unit, out var spec))
        {
            _dragSpec = spec;
            _dragFrom = cell;
            _match.Remove(cell);
            _playerSpec.Remove(unit);
            Context.Sounds.Play("unit_pick");
        }
    }

    private void EndDrag(Point mouse)
    {
        var spec = _dragSpec!;
        var cell = CellUnderMouse();

        if (cell is { } c && IsPlayerZone(c) && _match.UnitAt(c) == null
            && !_battlefield[c].Terrain.BlocksMovement())
        {
            PlacePlayer(spec, c);                       // pose / repositionne
            Context.Sounds.Play("unit_place");
        }
        else if (!spec.Essential && IsOverPanel(mouse))
        {
            _pending.Add(spec);                         // retour à l'inventaire (jamais le commandant)
            Context.Sounds.Play("unit_pick");
        }
        else if (_dragFrom is { } from && _match.UnitAt(from) == null)
        {
            PlacePlayer(spec, from);                    // drop invalide : remet à l'origine
            Context.Sounds.Play("unit_place");
        }
        else
        {
            _pending.Add(spec);                         // venait de l'inventaire : y retourne
            Context.Sounds.Play("unit_pick");
        }

        _dragSpec = null;
        _dragFrom = null;
    }

    /// <summary>Repose proprement l'unité en cours de glisser (origine ou inventaire).</summary>
    private void CancelDrag()
    {
        if (_dragSpec == null)
            return;

        if (_dragFrom is { } from && _match.UnitAt(from) == null)
            PlacePlayer(_dragSpec, from);
        else
            _pending.Add(_dragSpec);

        _dragSpec = null;
        _dragFrom = null;
    }

    private void UpdateBattle(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Glissement d'entrée : on fige le combat (pas d'interaction) le temps que le panneau sorte
        // et que le plateau finisse de se recentrer — le layout est rafraîchi à chaque frame.
        if (_battleIntroTimer > 0)
        {
            _battleIntroTimer -= dt;
            MarkLayoutDirty();
            return;
        }

        _sparks.Update(dt);     // les particules vivent leur vie même pendant le gel de l'animation

        // Animation d'attaque en cours : on gèle entrées, IA et fin de combat le temps des FX
        // (le domaine est déjà résolu ; la fin de partie ne s'affiche qu'après la dissolution).
        if (_fx.Active)
        {
            _fx.Update(dt);
            if (_fx.HasImpacted && !_sparksEmitted)
                EmitImpactSparks();
            return;
        }

        if (_match.CurrentTurn == Faction.Enemy)
            UpdateAiTurn(gameTime);
        else
            UpdatePlayerTurn();

        if (!_fx.Active)        // une attaque vient peut-être de lancer une animation : on attend
            CheckBattleEnd();
    }

    private void CheckBattleEnd()
    {
        if (!_match.IsOver)
            return;

        if (_match.Winner == Faction.Player)
        {
            var casualties = _playerSpec
                .Where(kv => !kv.Key.IsAlive)
                .Select(kv => kv.Value)
                .ToList();
            _run.CompleteCombat(casualties, _enemyKillOrder);
        }
        else
        {
            _defeatReason = CommanderAlive() ? "ARMEE DETRUITE" : "COMMANDANT TOMBE";
            _run.Defeat();
        }

        ClearSelection();

        // Repère sonore de fin : campagne gagnée/perdue, ou combat remporté (→ recrutement).
        if (_run.Phase == RunPhase.Victory) Context.Sounds.Play("victory");
        else if (_run.Phase == RunPhase.Defeat) Context.Sounds.Play("defeat");
        else if (_match.Winner == Faction.Player) Context.Sounds.Play("combat_won");

        // Fin de run (boss vaincu ou commandant tombé) : la sauvegarde n'a plus lieu d'être.
        if (_run.Phase is RunPhase.Victory or RunPhase.Defeat)
            Context.Saves.DeleteSlot(_saveSlot);
    }

    private bool CommanderAlive() =>
        _playerSpec.Any(kv => kv.Value.Essential && kv.Key.IsAlive);

    private void UpdatePlayerTurn()
    {
        // Clic droit : repose le pion porté et annule la sélection (l'unité reste en place).
        if (Context.Input.WasRightClicked && (_selected is not null || _combatDragFrom is not null))
        {
            _combatDragFrom = null;
            ClearSelection();
            Context.Sounds.Play("unit_deselect");
            return;
        }

        if (Context.Input.WasLeftClicked)
            BeginCombatInteraction();
        else if (Context.Input.WasLeftReleased && _combatDragFrom is not null)
            DropCarriedUnit();
    }

    /// <summary>
    /// Appui gauche en combat : agit sur une cible déjà mise en évidence (clic-pour-déplacer
    /// conservé), sinon SAISIT une unité du joueur — qui devient « portée » à la souris.
    /// </summary>
    private void BeginCombatInteraction()
    {
        var hit = CellUnderMouse();
        if (hit is null)
        {
            if (_selected is not null) Context.Sounds.Play("unit_deselect");
            ClearSelection();
            return;
        }
        var cell = hit.Value;

        if (_selected is not null && _attackTargets.Contains(cell))
        {
            ResolveAttack(_selected.Value, cell);
            EndPlayerAction();
            return;
        }

        if (_selected is not null && _legalMoves.Contains(cell))
        {
            var from = _selected.Value;
            _match.TryMove(from, cell);
            if (_match.UnitAt(cell) is { } moved) FaceToward(moved, from, cell);
            TriggerLanding(cell);
            Context.Sounds.Play("unit_move");
            EndPlayerAction();
            return;
        }

        var unit = _match.UnitAt(cell);
        if (unit is { Faction: Faction.Player })
        {
            _selected = cell;
            _match.LegalMoves(cell, _legalMoves);       // remplit les buffers (pas d'allocation)
            _match.AttackTargets(cell, _attackTargets);
            _match.ThreatenedCells(cell, _attackReach); // toute la portée de tir (affichée avec le déplacement)
            _combatDragFrom = cell;                 // on soulève le pion (suit la souris jusqu'au relâché)
            Context.Sounds.Play("unit_select");
        }
        else
        {
            if (_selected is not null) Context.Sounds.Play("unit_deselect");
            ClearSelection();
        }
    }

    /// <summary>
    /// Relâché du glisser de combat : dépose le pion sur la case visée si c'est une attaque ou un
    /// déplacement légal ; sinon il « retombe » sur sa case d'origine et reste sélectionné.
    /// </summary>
    private void DropCarriedUnit()
    {
        var from = _combatDragFrom!.Value;
        _combatDragFrom = null;

        if (CellUnderMouse() is not { } cell || cell == from)
        {
            TriggerLanding(from);                   // reposé en place : reste sélectionné
            return;
        }

        if (_attackTargets.Contains(cell))
        {
            ResolveAttack(from, cell);
            EndPlayerAction();
        }
        else if (_legalMoves.Contains(cell))
        {
            _match.TryMove(from, cell);
            if (_match.UnitAt(cell) is { } moved) FaceToward(moved, from, cell);
            TriggerLanding(cell);
            Context.Sounds.Play("unit_move");
            EndPlayerAction();
        }
        else
        {
            TriggerLanding(from);                   // case invalide : retombe sur place, reste sélectionné
        }
    }

    private void EndPlayerAction()
    {
        ClearSelection();
        if (_match.CurrentTurn == Faction.Enemy && !_match.IsOver)
            _aiTimer = AiDelaySeconds;
    }

    private void UpdateAiTurn(GameTime gameTime)
    {
        _aiTimer -= gameTime.ElapsedGameTime.TotalSeconds;
        if (_aiTimer > 0)
            return;

        var action = EnemyAi.ChooseAction(_match);
        if (action is not { } a)
            return;

        if (a.IsAttack)
        {
            ResolveAttack(a.From, a.To);
        }
        else
        {
            _match.TryMove(a.From, a.To);
            if (_match.UnitAt(a.To) is { } moved) FaceToward(moved, a.From, a.To);
            TriggerLanding(a.To);
            Context.Sounds.Play("unit_move");
        }
    }

    private void UpdateRecruitment()
    {
        if (!Context.Input.WasLeftClicked)
            return;

        var viewport = VirtualViewport;
        var mouse = Context.Input.MousePosition;
        for (var i = 0; i < _run.Draft.Count; i++)
        {
            if (DraftCardRect(i, _run.Draft.Count, viewport.Width, viewport.Height).Contains(mouse))
            {
                _run.Recruit(_run.Draft[i]);
                Context.Sounds.Play("recruit");
                BeginPlacement();
                return;
            }
        }
    }

    private void ClearSelection()
    {
        _selected = null;
        _legalMoves.Clear();
        _attackTargets.Clear();
        _attackReach.Clear();
        _combatDragFrom = null;
    }

    /// <summary>Lance le rebond de « pose » sur la case où un pion vient d'atterrir.</summary>
    private void TriggerLanding(Cell cell)
    {
        _landingCell = cell;
        _landingTimer = LandingDuration;
    }

    /// <summary>
    /// Résout une attaque dans le domaine (instantané) PUIS lance l'animation de combat qui gèle le
    /// tour le temps des FX. L'avancée éventuelle de l'attaquant est DÉDUITE de l'état du plateau :
    /// après un kill en mêlée le domaine l'a déjà déplacé sur la case ; en tir il est resté en place.
    /// </summary>
    private MoveKind ResolveAttack(Cell from, Cell target)
    {
        var attacker = _match.UnitAt(from);
        var victim = _match.UnitAt(target);
        if (attacker != null)
            FaceToward(attacker, from, target);     // tourne l'attaquant vers sa cible (avant la capture du sprite)
        var attackerSprite = attacker != null ? UnitSprite(attacker) : null;
        var victimSprite = victim != null ? UnitSprite(victim) : null;

        var kind = _match.TryAttack(from, target);
        if (kind == MoveKind.Invalid)
            return kind;

        RecordIfEnemyKilled(victim);
        Context.Sounds.Play("unit_attack");

        var killed = kind == MoveKind.Killed;
        var advanced = killed && ReferenceEquals(_match.UnitAt(target), attacker);
        var attackerCell = advanced ? target : from;
        _fx.Begin(from, target, attackerCell, attackerSprite, victimSprite, killed, advanced);
        _sparksEmitted = false;     // la gerbe d'étincelles sera émise au contact (cf. UpdateBattle)

        return kind;
    }

    /// <summary>
    /// Si <paramref name="victim"/> est un ennemi NON essentiel qui vient de mourir, enregistre son
    /// gabarit dans l'ordre des morts (le boss est exclu : le recrutement ne le proposera jamais).
    /// </summary>
    private void RecordIfEnemyKilled(Unit? victim)
    {
        if (victim is { IsAlive: false, Faction: Faction.Enemy, IsEssential: false }
            && _enemySpec.TryGetValue(victim, out var spec))
            _enemyKillOrder.Add(spec);
    }

    private Cell? CellUnderMouse()
    {
        var hit = BuildLayout().ScreenToCell(Context.Input.MousePosition, Columns, Rows);
        return hit is null ? null : new Cell(hit.Value.Column, hit.Value.Row);
    }

    // ── Caméra (zoom molette + pan clavier) ───────────────────────────────────────

    /// <summary>Remet la caméra à l'état par défaut (zoom de cadrage, plateau centré).</summary>
    private void ResetCamera()
    {
        _zoomedIn = false;
        _camera = Vector2.Zero;
        _layoutDirty = true;
    }

    /// <summary>Marque le layout à recalculer (zoom ou pan modifié).</summary>
    private void MarkLayoutDirty() => _layoutDirty = true;

    private void UpdateCamera(GameTime gameTime)
    {
        // Molette : un seul cran de zoom (haut = rapproché, bas = retour au cadrage).
        var scroll = Context.Input.ScrollDelta;
        if (scroll > 0) SetZoom(true);
        else if (scroll < 0) SetZoom(false);

        // Pan clavier : flèches + ZQSD (AZERTY). Aller « voir à droite » fait reculer l'origine.
        var input = Context.Input;
        var dir = Vector2.Zero;
        if (input.IsKeyDown(Keys.Left) || input.IsKeyDown(Keys.Q)) dir.X += 1;
        if (input.IsKeyDown(Keys.Right) || input.IsKeyDown(Keys.D)) dir.X -= 1;
        if (input.IsKeyDown(Keys.Up) || input.IsKeyDown(Keys.Z)) dir.Y += 1;
        if (input.IsKeyDown(Keys.Down) || input.IsKeyDown(Keys.S)) dir.Y -= 1;

        if (dir != Vector2.Zero)
        {
            var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            _camera += dir * CameraPanSpeed * dt;
            MarkLayoutDirty();
        }
    }

    /// <summary>
    /// Bascule le zoom en gardant fixe le point du plateau sous le curseur (zoom-vers-curseur).
    /// Le débordement éventuel sera ensuite borné par le pan (cf. <see cref="BuildLayoutCore"/>).
    /// </summary>
    private void SetZoom(bool zoomIn)
    {
        if (zoomIn == _zoomedIn)
            return;

        var before = BuildLayout();             // origine + taille de case AVANT bascule
        var origin0 = before.Origin;
        int tile0 = before.TileSize;

        _zoomedIn = zoomIn;
        MarkLayoutDirty();

        int tile1 = GridLayout.DefaultTileSize * CurrentZoom();
        float ratio = tile1 / (float)tile0;

        // Origine visée pour garder le point monde sous le curseur immobile, puis on en déduit le pan.
        var m = Context.Input.MousePosition.ToVector2();
        var origin1 = m - (m - origin0) * ratio;

        var viewport = VirtualViewport;
        var availWidth = AvailableWidth();
        int pxW = Columns * tile1;
        int pxH = (Rows - 1) * tile1 + GridLayout.DefaultSpriteHeight * CurrentZoom();
        var center = new Vector2((availWidth - pxW) / 2f, (viewport.Height - pxH) / 2f);
        _camera = origin1 - center;
    }

    // ── Rendu ───────────────────────────────────────────────────────────────────
    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var layout = BuildLayout();
        var viewport = VirtualViewport;

        // Fond : eau animée pixel-art derrière le plateau (passes shader dédiées, hors du
        // batch principal car elles changent d'état SpriteBatch et de render target).
        DrawWaterBackground(sb, layout, viewport);

        // Le plateau (terrain + ombres + unités + FX) est secoué d'un cran à l'impact d'une attaque ;
        // le panneau latéral et l'eau restent stables. Le layout secoué ne sert qu'au dessin (le
        // hit-test souris reste sur le layout d'origine via BuildLayout).
        var board = ShakeBoard(layout);

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawTerrain(sb, board);
        sb.End();

        // Passe d'ombres projetées (sur le terrain, sous les unités) — batchs cisaillés dédiés.
        if (_run.Phase is RunPhase.Placement or RunPhase.Battle)
            DrawCastShadows(sb, board);

        switch (_run.Phase)
        {
            case RunPhase.Placement:
                sb.Begin(samplerState: SamplerState.PointClamp);
                DrawDeploymentZone(sb, board);
                DrawEnemyThreat(sb, board);
                DrawUnits(sb, board);
                DrawPanelBackground(sb);
                DrawPlacementPanel(sb);
                DrawDragGhost(sb);
                sb.End();
                break;
            case RunPhase.Battle:
                sb.Begin(samplerState: SamplerState.PointClamp);
                DrawHighlights(sb, board);
                DrawEnemyThreat(sb, board);
                DrawUnits(sb, board);
                DrawCarriedUnit(sb, board);
                sb.End();

                if (_fx.Active)             // dissolution / attaquant animé / flash : passes dédiées
                    DrawCombatFx(sb, board);

                _sparks.Draw(sb, Context.Pixel);   // étincelles d'impact, au-dessus de tout le plateau

                if (_battleIntroTimer > 0)
                    DrawSlidingPanel(sb);          // panneau de placement qui sort par la droite
                else
                {
                    sb.Begin(samplerState: SamplerState.PointClamp);
                    DrawCombatCards(sb, layout);
                    sb.End();
                }
                break;
            case RunPhase.Recruitment:
                sb.Begin(samplerState: SamplerState.PointClamp);
                DrawUnits(sb, board);
                DrawDim(sb, viewport);
                DrawRecruitment(sb, viewport);
                sb.End();
                break;
            case RunPhase.Victory:
            case RunPhase.Defeat:
                sb.Begin(samplerState: SamplerState.PointClamp);
                DrawUnits(sb, board);
                DrawDim(sb, viewport);
                DrawEndHud(sb, viewport);
                sb.End();
                break;
        }

        if (_showTrees)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            _treeRenderer.Draw(sb, viewport.Width, viewport.Height);
            sb.End();
        }
        else if (_pauseMenu.IsOpen)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            _pauseRenderer.Draw(sb, _pauseMenu, viewport.Width, viewport.Height,
                Context.Input.MousePosition.ToVector2(), Context.Input.IsLeftDown);
            sb.End();
        }
    }

    /// <summary>
    /// Dessine le fond d'eau animé derrière le plateau : masque du plateau → eau plein écran →
    /// frange d'ombre autour du plateau. Repli sur un aplat uni si le shader est indisponible.
    /// </summary>
    private void DrawWaterBackground(SpriteBatch sb, GridLayout layout, Viewport viewport)
    {
        var w = viewport.Width;
        var h = viewport.Height;

        if (!_water.Enabled)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            DrawRect(sb, new Rectangle(0, 0, w, h), WaterRenderer.FallbackColor);
            sb.End();
            return;
        }

        _water.DrawWater(sb, _time, w, h);

        // Frange d'ombre : UNIQUEMENT quand le plateau est un « îlot » entièrement dans le canvas.
        // Zoomé / pané, le plateau déborde l'écran : il n'y a plus d'eau autour à ombrer, et le
        // dégradé envahirait toute la vue (fond qui vire au noir). On la saute alors — ça évite aussi
        // de recalculer le flou 17-taps à chaque frame de pan (le rectangle du plateau changeant).
        var board = BoardRect(layout);
        if (board.X >= 0 && board.Y >= 0 && board.Right <= w && board.Bottom <= h)
            _water.DrawShadow(sb, board, w, h);   // ombre statique mise en cache (cf. WaterRenderer)
    }

    /// <summary>Rectangle (en coordonnées canvas) couvert par le plateau, épaisseur des sprites comprise.</summary>
    private static Rectangle BoardRect(GridLayout layout)
    {
        var pxW = Columns * layout.TileSize;
        var pxH = (Rows - 1) * layout.RowPitch + layout.SpriteHeight;
        return new Rectangle((int)layout.Origin.X, (int)layout.Origin.Y, pxW, pxH);
    }

    /// <summary>
    /// Étend l'eau dans les bandes noires du letterbox : on peint le champ d'eau sur tout le
    /// backbuffer réel, avec un repère raccordé à celui du canvas (mêmes coordonnées « monde »)
    /// → le courant est continu jusqu'au bord du canvas, qui sera ensuite blitté par-dessus.
    /// </summary>
    public override void DrawLetterboxBackground(Point realScreen, Point canvasOffset, int canvasScale)
    {
        if (canvasScale <= 0)
            return;

        var sb = Context.SpriteBatch;
        var fullScreen = new Rectangle(0, 0, realScreen.X, realScreen.Y);

        if (_water.Enabled)
        {
            // Écran réel → coordonnées canvas : le pixel écran s = canvasOffset + monde * canvasScale.
            var worldMin = new Vector2(-canvasOffset.X / (float)canvasScale, -canvasOffset.Y / (float)canvasScale);
            var worldSize = new Vector2(realScreen.X / (float)canvasScale, realScreen.Y / (float)canvasScale);
            _water.DrawWaterRect(sb, _time, fullScreen, worldMin, worldSize);
        }

        // Le voile d'assombrissement (pause / recrutement / fin) est dessiné DANS le canvas et ne
        // couvre donc que la zone 16:9 ; on l'étend ici aux bandes pour que tout l'écran soit sombre.
        if (FullScreenDim() is { } dim)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            sb.Draw(Context.Pixel, fullScreen, dim);
            sb.End();
        }
    }

    /// <summary>
    /// Voile plein écran actif, le cas échéant : doit reproduire EXACTEMENT le voile dessiné dans
    /// le canvas (pause → <see cref="PauseMenuRenderer"/>, recrutement/fin → <see cref="DrawDim"/>)
    /// afin que les bandes du letterbox s'assombrissent à l'identique. Null si rien à assombrir.
    /// </summary>
    private Color? FullScreenDim()
    {
        if (_pauseMenu.IsOpen)
            return Palette.Navy2 * 0.85f; // = PauseMenuRenderer.Overlay
        return _run.Phase is RunPhase.Recruitment or RunPhase.Victory or RunPhase.Defeat
            ? Palette.Black1 * 0.62f       // = DrawDim
            : null;
    }

    private void DrawTerrain(SpriteBatch sb, GridLayout layout)
    {
        // Arrière → avant (Cells() parcourt rangée 0 → N) pour que l'épaisseur se recouvre bien.
        foreach (var cell in _battlefield.Cells())
            sb.Draw(_tiles[_battlefield[cell].Terrain], layout.CellToSpriteRect(cell.Column, cell.Row), Color.White);
    }

    private void DrawDeploymentZone(SpriteBatch sb, GridLayout layout)
    {
        foreach (var cell in PlayerDeployCells())
            DrawZone(sb, layout, cell, Palette.Green1 * 0.22f);
    }

    private void DrawHighlights(SpriteBatch sb, GridLayout layout)
    {
        // Unité sélectionnée : on garde son aperçu (buffers remplis à la sélection).
        if (_selected is { } sel)
        {
            DrawMoveAttackZones(sb, layout, sel, _attackReach, _legalMoves, _attackTargets);
            return;
        }

        // Sinon, aperçu au SURVOL d'un pion joueur (uniquement pendant son tour : sinon pas de coups).
        if (_match.CurrentTurn == Faction.Player
            && CellUnderMouse() is { } cell && _match.UnitAt(cell) is { Faction: Faction.Player })
        {
            _match.ThreatenedCells(cell, _hoverReach);
            _match.LegalMoves(cell, _hoverMoves);
            _match.AttackTargets(cell, _hoverAttackTargets);
            DrawMoveAttackZones(sb, layout, cell, _hoverReach, _hoverMoves, _hoverAttackTargets);
        }
    }

    /// <summary>Surbrillances déplacement/attaque d'une unité : cerclage + portée de tir + cases de
    /// déplacement + cibles réellement à portée. Partagé par la sélection et l'aperçu au survol.</summary>
    private void DrawMoveAttackZones(SpriteBatch sb, GridLayout layout, Cell origin,
        List<Cell> reach, List<Cell> moves, List<Cell> targets)
    {
        DrawZoneBorder(sb, layout, origin, Palette.Yellow2, 3);

        foreach (var cell in reach)     // PORTÉE de tir (cases atteintes) = rouge pâle
            DrawZone(sb, layout, cell, Palette.Purple5 * 0.18f);

        foreach (var cell in moves)     // déplacement = jaune
            DrawZone(sb, layout, cell, Palette.Yellow2 * 0.30f);

        foreach (var cell in targets)   // ennemi réellement ciblable = rouge fort
            DrawZone(sb, layout, cell, Palette.Purple5 * 0.50f);
    }

    /// <summary>
    /// Au survol d'une unité ENNEMIE, prévisualise sa portée d'attaque : les cases qu'elle menace
    /// sont teintées en rouge, l'ennemi survolé est cerclé. Au MAINTIEN d'Espace, on affiche d'un
    /// coup les cases menacées par TOUS les ennemis (zones de danger globales). Aide à anticiper.
    /// </summary>
    private void DrawEnemyThreat(SpriteBatch sb, GridLayout layout)
    {
        if (Context.Input.IsKeyDown(Keys.Space))
        {
            foreach (var (cell, unit) in _match.Units())
            {
                if (unit.Faction != Faction.Enemy)
                    continue;
                _match.ThreatenedCells(cell, _threatCells);
                foreach (var threat in _threatCells)
                    DrawZone(sb, layout, threat, Palette.Purple5 * 0.30f);
            }
            return;
        }

        if (CellUnderMouse() is not { } hovered || _match.UnitAt(hovered) is not { Faction: Faction.Enemy })
            return;

        _match.ThreatenedCells(hovered, _threatCells);  // buffer réutilisé (pas d'allocation par frame)
        foreach (var threat in _threatCells)
            DrawZone(sb, layout, threat, Palette.Purple5 * 0.30f);
        DrawZoneBorder(sb, layout, hovered, Palette.Purple5, 2);
    }

    private void DrawUnits(SpriteBatch sb, GridLayout layout)
    {
        foreach (var (cell, unit) in _match.Units())
            DrawUnit(sb, layout, cell, unit);
    }

    private void DrawUnit(SpriteBatch sb, GridLayout layout, Cell cell, Unit unit)
    {
        // Pion porté à la souris : dessiné en flottant par DrawCarriedUnit, pas sur sa case.
        if (_combatDragFrom == cell)
            return;

        // Attaquant en cours d'animation : dessiné (fente / avance) par la passe FX, pas ici.
        if (_fx.Active && _fx.Attacker == cell)
            return;

        var top = layout.CellToScreen(cell.Column, cell.Row);
        var size = layout.TileSize;
        var zx = (int)top.X;
        var zy = (int)top.Y;
        var zone = new Rectangle(zx, zy, size, size);

        // Liseré doré pour les unités pivots (commandant / boss).
        if (unit.IsEssential)
            DrawRectBorder(sb, zone, Palette.Yellow1, 3);

        // L'ombre projetée est dessinée dans une passe dédiée (DrawCastShadows), sous toutes les unités.
        var animLift = UnitLift(cell, size);
        var spriteLift = (int)(size * SpriteLiftFraction);

        // Recul de la victime survivante : décalage en pixels à l'opposé de l'attaquant, au contact.
        var kb = IsFxVictim(cell) ? VictimKnockback(size) : Point.Zero;
        zx += kb.X;
        zy += kb.Y;

        var sprite = UnitSprite(unit);
        if (sprite != null)
        {
            // Le socle est en bas du sprite : on remonte pour le centrer (haut qui déborde, voulu).
            sb.Draw(sprite, new Rectangle(zx, zy - spriteLift - animLift, size, size), Color.White);
        }
        else
        {
            // Pas d'asset : placeholder jeton coloré + initiale de la classe.
            var token = new Rectangle(zx + 9, zy + 8 - animLift, size - 18, size - 26);
            DrawChip(sb, unit.Class, unit.Faction, token);
        }

        // Barre de vie retirée temporairement (cachait l'ombre) — à restaurer après les tests.
        // var barBg = new Rectangle(zx + 9, zy + size - 14, size - 18, 5);
        // DrawRect(sb, barBg, Palette.Black1);
        // var ratio = unit.MaxHp == 0 ? 0f : (float)unit.Hp / unit.MaxHp;
        // DrawRect(sb, new Rectangle(barBg.X, barBg.Y, (int)(barBg.Width * ratio), barBg.Height), Palette.Green1);
    }

    /// <summary>
    /// Soulèvement vertical (px entiers) du pion sur une case : constant tant qu'il est
    /// sélectionné (« tenu en main »), plus un rebond amorti juste après s'être posé.
    /// </summary>
    private int UnitLift(Cell cell, int size)
    {
        var lift = 0f;

        if (_selected == cell)
            lift += size * HeldLiftFraction;

        if (_landingCell == cell && _landingTimer > 0)
        {
            var t = (float)(1 - _landingTimer / LandingDuration);     // 0 → 1
            var bounce = MathF.Abs(MathF.Cos(t * MathF.PI * 1.5f)) * (1 - t); // 2 rebonds amortis
            lift += size * LandingLiftFraction * bounce;
        }

        return (int)lift;
    }

    /// <summary>
    /// Passe d'ombres PROJETÉES (à appeler entre le terrain et les unités) : chaque pion est redessiné
    /// en silhouette sombre, cisaillée et rabattue au sol, ancrée à la base de son socle. L'ombre
    /// reste au sol même quand le pion se soulève (elle utilise la position « au repos » du sprite),
    /// ce qui rend lisible le décollage. Un batch dédié par pion (matrice de cisaillement propre).
    /// </summary>
    private void DrawCastShadows(SpriteBatch sb, GridLayout layout)
    {
        var size = layout.TileSize;
        var spriteLift = (int)(size * SpriteLiftFraction);

        foreach (var (cell, unit) in _match.Units())
        {
            if (_combatDragFrom == cell)            // pion porté : ombre dessinée sous le curseur
                continue;
            if (_fx.Active && _fx.Attacker == cell) // attaquant animé : ombre dessinée par la passe FX
                continue;
            if (UnitSprite(unit) is not { } sprite) // placeholder sans sprite : pas de silhouette
                continue;

            var top = layout.CellToScreen(cell.Column, cell.Row);
            DrawPieceCastShadow(sb, sprite, (int)top.X, (int)top.Y - spriteLift, size, UnitLift(cell, size));
        }

        // Pion porté à la souris : son ombre au sol, à l'aplomb du curseur (position « au repos »).
        if (_combatDragFrom is { } from && _match.UnitAt(from) is { } carried && UnitSprite(carried) is { } cs)
        {
            var m = Context.Input.MousePosition;
            DrawPieceCastShadow(sb, cs, m.X - size / 2, m.Y - size / 2, size, (int)(size * CarriedLiftFraction));
        }
    }

    /// <summary>
    /// Ombre projetée d'un pion dont la silhouette « au repos » occupe (<paramref name="destX"/>,
    /// <paramref name="destY"/>). Quand le pion est en l'air (<paramref name="lift"/> &gt; 0), l'ombre
    /// GLISSE dans la direction de la lumière et S'ÉCLAIRCIT → lecture nette du décollage.
    /// </summary>
    private void DrawPieceCastShadow(SpriteBatch sb, Texture2D sprite, int destX, int destY, int size, int lift)
    {
        var k = MathHelper.Clamp(lift / (size * CarriedLiftFraction), 0f, 1f);
        var slideX = (int)(lift * ShadowLiftSlide);          // glisse vers la lumière (droite, comme le cisaillement)
        var slideY = (int)(lift * ShadowLiftSlide * 0.35f);  // et un peu vers le bas/avant
        var alpha = ShadowAlpha * (1f - ShadowLiftFade * k);

        var dest = new Rectangle(destX + slideX, destY + slideY, size, size);
        var anchor = new Vector2(dest.X + size / 2f, dest.Y + size * ShadowAnchorFraction);
        DrawSilhouetteShadow(sb, sprite, dest, anchor, alpha);
    }

    /// <summary>
    /// Dessine une silhouette de sprite en ombre : matrice qui, AUTOUR de <paramref name="anchor"/>
    /// (la base du socle), cisaille latéralement et rabat/aplatit la silhouette vers le bas
    /// (<see cref="ShadowFlatten"/> &lt; 0 → l'ombre tombe vers l'avant). Teinte sombre semi-transparente.
    /// </summary>
    private void DrawSilhouetteShadow(SpriteBatch sb, Texture2D sprite, Rectangle dest, Vector2 anchor, float alpha)
    {
        var transform =
            Matrix.CreateTranslation(-anchor.X, -anchor.Y, 0f)
            * new Matrix(1f, 0f, 0f, 0f,
                         -ShadowShear, ShadowFlatten, 0f, 0f,
                         0f, 0f, 1f, 0f,
                         0f, 0f, 0f, 1f)
            * Matrix.CreateTranslation(anchor.X, anchor.Y, 0f);

        // CullNone : ShadowFlatten < 0 retourne la silhouette (inverse le sens des triangles) →
        // sans ça le SpriteBatch l'éliminerait par culling et l'ombre serait invisible.
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: transform,
            rasterizerState: RasterizerState.CullNone);
        sb.Draw(sprite, dest, Palette.Black1 * alpha);
        sb.End();
    }

    /// <summary>
    /// Pion soulevé à la souris pendant un glisser de combat : le sprite suit le curseur, nettement
    /// soulevé, et projette une ombre sur la case visée quand c'est une cible légale (aperçu de pose).
    /// </summary>
    private void DrawCarriedUnit(SpriteBatch sb, GridLayout layout)
    {
        if (_combatDragFrom is not { } from || _match.UnitAt(from) is not { } unit)
            return;

        var size = layout.TileSize;
        var lift = (int)(size * CarriedLiftFraction);
        var m = Context.Input.MousePosition;

        // Repère de pose : liseré clair sur la case visée si le dépôt est valide (distinct de l'ombre).
        if (CellUnderMouse() is { } target && (_legalMoves.Contains(target) || _attackTargets.Contains(target)))
            DrawZoneBorder(sb, layout, target, Palette.White, 2);

        // (L'ombre projetée du pion porté est dessinée dans la passe d'ombres, au sol sous le curseur.)
        // Pion porté, centré sur la souris (même rendu que sur le plateau : sprite ou jeton placeholder).
        var rect = new Rectangle(m.X - size / 2, m.Y - size / 2 - lift, size, size);
        var sprite = UnitSprite(unit);
        if (sprite != null)
            sb.Draw(sprite, rect, Color.White);
        else
            DrawChip(sb, unit.Class, unit.Faction, new Rectangle(rect.X + 9, rect.Y + 8, size - 18, size - 26));
    }

    // ── Effets de combat (estafilade / dissolution / flash) ───────────────────────

    /// <summary>Renvoie le layout décalé de la secousse d'écran si une attaque s'anime, sinon tel quel.</summary>
    private GridLayout ShakeBoard(GridLayout layout)
    {
        if (!_fx.Active)
            return layout;
        var s = _fx.ShakeOffset(_fx.Killed ? 4f : 2f);     // secousse plus marquée sur un kill
        if (s == Point.Zero)
            return layout;
        return new GridLayout(layout.Origin + new Vector2(s.X, s.Y),
            layout.TileSize, layout.SpriteWidth, layout.SpriteHeight, layout.RowPitch);
    }

    /// <summary>
    /// Passe d'effets de l'attaque en cours (entre les unités et le panneau) : dissolution de la
    /// victime (avec son recul), attaquant en fente/avance avec son ombre, flash « touché » du
    /// survivant. Les étincelles d'impact, elles, sont dessinées à part (cf. <see cref="_sparks"/>).
    /// </summary>
    private void DrawCombatFx(SpriteBatch sb, GridLayout layout)
    {
        var size = layout.TileSize;
        var spriteLift = (int)(size * SpriteLiftFraction);
        // Taille d'un bloc des FX shader, alignée à la grille écran → pixel-art cohérent à tout zoom.
        var fxPixel = MathF.Max(2f, size / 32f);

        // Ancrages écran (coin haut-gauche du sprite, lift de socle compris) des cases en jeu.
        var fromTop = layout.CellToScreen(_fx.From.Column, _fx.From.Row) - new Vector2(0, spriteLift);
        var toTop = layout.CellToScreen(_fx.To.Column, _fx.To.Row) - new Vector2(0, spriteLift);
        var kb = VictimKnockback(size);
        var victimRect = new Rectangle((int)toTop.X + kb.X, (int)toTop.Y + kb.Y, size, size);

        // 1. Victime qui meurt : dissolution sur sa case (reculée), sous l'attaquant qui prendra la place.
        if (_fx.Killed && _fx.VictimSprite is { } deadSprite)
            _combatFx.DrawDissolve(sb, deadSprite, victimRect, _fx.DissolveProgress, Palette.Purple5, _fx.Seed);

        // 2. Attaquant animé (fente puis avance / recul) + ombre projetée à l'aplomb.
        if (_fx.AttackerSprite is { } attackerSprite)
        {
            var top = _fx.AttackerTopLeft(fromTop, toTop, size);
            var rect = new Rectangle((int)top.X, (int)top.Y, size, size);
            DrawPieceCastShadow(sb, attackerSprite, rect.X, rect.Y, size, 0);
            sb.Begin(samplerState: SamplerState.PointClamp);
            sb.Draw(attackerSprite, rect, Color.White);
            sb.End();
        }

        // 3. Réaction « touché » du survivant : flash additif par-dessus son sprite (reculé comme lui).
        if (!_fx.Killed && _fx.VictimSprite is { } hitSprite)
            _combatFx.DrawFlash(sb, hitSprite, victimRect, _fx.FlashIntensity, Palette.White, fxPixel);
    }

    /// <summary>Vrai pour la case d'une victime SURVIVANTE en cours d'animation (à reculer dans DrawUnit).</summary>
    private bool IsFxVictim(Cell cell) => _fx.Active && !_fx.Killed && cell == _fx.To;

    /// <summary>Décalage de recul (px entiers) de la victime au contact : à l'opposé de l'attaquant.</summary>
    private Point VictimKnockback(int size)
    {
        if (!_fx.Active)
            return Point.Zero;
        var amt = _fx.KnockbackAmount;
        if (amt <= 0f)
            return Point.Zero;

        var dir = new Vector2(_fx.To.Column - _fx.From.Column, _fx.To.Row - _fx.From.Row);
        if (dir.LengthSquared() > 0f)
            dir.Normalize();
        var mag = size * 0.16f * amt;
        return new Point((int)MathF.Round(dir.X * mag), (int)MathF.Round(dir.Y * mag));
    }

    /// <summary>Émet la gerbe d'étincelles au point de contact, dans le sens du coup (une fois par attaque).</summary>
    private void EmitImpactSparks()
    {
        _sparksEmitted = true;
        var layout = BuildLayout();
        var size = layout.TileSize;
        var center = layout.CellToScreen(_fx.To.Column, _fx.To.Row) + new Vector2(size / 2f, size / 2f);

        var dir = new Vector2(_fx.To.Column - _fx.From.Column, _fx.To.Row - _fx.From.Row); // sens du coup
        var unit = dir.LengthSquared() > 0f ? Vector2.Normalize(dir) : Vector2.Zero;
        var origin = center - unit * (size * 0.22f);   // bord de contact, côté attaquant
        var pixel = MathF.Max(2f, size / 32f);
        // EXCEPTION palette autorisée par le dev POUR CE FX UNIQUEMENT : rouge vif « flashy » +
        // cœurs orange chauds, pour que l'impact pète à l'écran (hors des 20 tons de la palette).
        _sparks.Emit(origin, dir, count: 16, new Color(255, 40, 40), new Color(255, 190, 80), pixel);
    }

    // ── Panneau latéral ───────────────────────────────────────────────────────────

    private Rectangle PanelRect()
    {
        var vp = VirtualViewport;
        return new Rectangle(vp.Width - RightPanelWidth, 0, RightPanelWidth, vp.Height);
    }

    private bool IsOverPanel(Point p) =>
        p.X >= Context.VirtualResolution.X - RightPanelWidth;

    /// <summary>Case 64×64 (cliquable) du portrait d'inventaire numéro <paramref name="index"/>, en grille.</summary>
    private Rectangle PanelCardRect(int index)
    {
        var panel = PanelRect();
        var col = index % InvCols;
        var row = index / InvCols;
        var x = panel.X + PanelPad + col * (InvIconSize + InvGapX);
        var y = PanelListTop + row * (InvCellH + InvGapY);
        return new Rectangle(x, y, InvIconSize, InvIconSize);
    }

    private int? PanelCardAt(Point p)
    {
        for (var i = 0; i < _pending.Count; i++)
            if (PanelCardRect(i).Contains(p))
                return i;
        return null;
    }

    private void DrawPanelBackground(SpriteBatch sb)
    {
        var panel = PanelRect();
        Context.Style.FillDither(sb, panel);   // fond tramé pixel-art, comme les cartes / boutons
        DrawRect(sb, new Rectangle(panel.X, 0, 2, panel.Height), Palette.Navy1);

        // Bord DROIT = bord du canvas : sur écran ultra-large, l'eau du letterbox affleure le panneau
        // et ses tons (proches du fond) le rendent peu lisible. Cette bande au ton le plus sombre de la
        // palette détache nettement le panneau de l'eau.
        const int rightEdge = 6;
        DrawRect(sb, new Rectangle(panel.Right - rightEdge, 0, rightEdge, panel.Height), Palette.Black1);
    }

    private void DrawPlacementPanel(SpriteBatch sb)
    {
        var panel = PanelRect();
        var x = panel.X + PanelPad;

        Context.Font.Draw(sb, CombatTitle(), new Vector2(x, 16), 1, Palette.Yellow1);
        Context.Font.Draw(sb, "PLACEMENT", new Vector2(x, 34), 2, Palette.Yellow2);
        Context.Font.Draw(sb, "INVENTAIRE", new Vector2(x, PanelListTop - 22), 1, Palette.Blue1);

        if (_pending.Count == 0 && _dragSpec == null)
            Context.Font.Draw(sb, "TOUT DEPLOYE", new Vector2(x, PanelListTop + 4), 1, Palette.Cyan2);

        for (var i = 0; i < _pending.Count; i++)
            DrawInventoryCard(sb, _pending[i], PanelCardRect(i));

        var bottom = panel.Height - 44;
        Context.Font.Draw(sb, "GLISSER POUR PLACER", new Vector2(x, bottom), 1, Palette.Blue1);
        Context.Font.Draw(sb, "ENTREE : COMBATTRE", new Vector2(x, bottom + 16), 1, Palette.Cyan1);
    }

    private void DrawInventoryCard(SpriteBatch sb, UnitSpec spec, Rectangle icon)
    {
        // Portrait 64×64 à taille native (jamais redimensionné), de FACE (présentation), nom dessous.
        DrawChip(sb, spec.UnitClass, Faction.Player, icon, front: true);
        Context.Font.DrawCentered(sb, spec.Name.ToUpperInvariant(),
            new Rectangle(icon.X - InvGapX / 2, icon.Bottom + 2, icon.Width + InvGapX, 10), 1, Palette.White);
    }

    // ── Cartes de combat (remplacent l'ancien panneau de droite) ──────────────────
    // Réutilisent le gabarit des cartes de recrutement ; le contenu sera retravaillé plus tard.
    private const int CombatCardW = 180;
    private const int CombatCardH = 200;
    private const int CombatCardGap = 24;

    /// <summary>
    /// Cartes flottantes du combat : l'unité SÉLECTIONNÉE s'affiche à droite du plateau, l'ennemi
    /// SURVOLÉ à gauche. Les deux peuvent coexister (sélection + survol d'un ennemi).
    /// </summary>
    private void DrawCombatCards(SpriteBatch sb, GridLayout layout)
    {
        var board = BoardRect(layout);
        var hovered = CellUnderMouse();

        // Carte de NOTRE pion (à droite) : l'unité sélectionnée tant qu'elle l'est ; sinon le pion
        // joueur survolé.
        var ownCell = _selected;
        if (ownCell is null && hovered is { } h && _match.UnitAt(h) is { Faction: Faction.Player })
            ownCell = h;
        if (ownCell is { } oc && _match.UnitAt(oc) is { } own)
            DrawUnitCard(sb, own, RightCardRect(board));

        // Carte de l'ennemi survolé (à gauche).
        if (hovered is { } he && _match.UnitAt(he) is { Faction: Faction.Enemy } enemy)
            DrawUnitCard(sb, enemy, LeftCardRect(board));
    }

    /// <summary>Emplacement de la carte à DROITE du plateau (unité sélectionnée), borné à l'écran.</summary>
    private Rectangle RightCardRect(Rectangle board)
    {
        var vp = VirtualViewport;
        var x = Math.Min(board.Right + CombatCardGap, vp.Width - CombatCardGap - CombatCardW);
        return new Rectangle(x, (vp.Height - CombatCardH) / 2, CombatCardW, CombatCardH);
    }

    /// <summary>Emplacement de la carte à GAUCHE du plateau (ennemi survolé), borné à l'écran.</summary>
    private Rectangle LeftCardRect(Rectangle board)
    {
        var vp = VirtualViewport;
        var x = Math.Max(board.X - CombatCardGap - CombatCardW, CombatCardGap);
        return new Rectangle(x, (vp.Height - CombatCardH) / 2, CombatCardW, CombatCardH);
    }

    /// <summary>
    /// Carte d'une unité du plateau, dans son ÉTAT COURANT (PV actuels). Calquée sur la carte de
    /// recrutement (<see cref="DrawDraftCard"/>) — la mise en forme sera retravaillée plus tard.
    /// </summary>
    private void DrawUnitCard(SpriteBatch sb, Unit unit, Rectangle rect)
    {
        Context.Style.DrawPanel(sb, rect);
        var c = unit.Class;

        var icon = new Rectangle(rect.X + (rect.Width - 64) / 2, rect.Y + 12, 64, 64);
        DrawChip(sb, c, unit.Faction, icon, front: true);

        Context.Font.DrawCentered(sb, c.Name.ToUpperInvariant(),
            new Rectangle(rect.X, icon.Bottom + 6, rect.Width, 14), 2, Palette.White);
        Context.Font.DrawCentered(sb, $"DOM {unit.Domaine}".ToUpperInvariant(),
            new Rectangle(rect.X, icon.Bottom + 26, rect.Width, 10), 1, Palette.Cyan1);
        Context.Font.DrawCentered(sb, $"PV {unit.Hp}/{unit.MaxHp}   DEG {unit.Damage}",
            new Rectangle(rect.X, icon.Bottom + 42, rect.Width, 10), 1, Palette.Yellow2);
        Context.Font.DrawCentered(sb, $"DEP {unit.MoveRange}   TIR {unit.AttackRange}",
            new Rectangle(rect.X, icon.Bottom + 58, rect.Width, 10), 1, Palette.Cyan2);
    }

    /// <summary>
    /// Pendant l'entrée en combat : redessine le panneau de placement décalé vers la droite (il sort
    /// de l'écran), via une translation du batch. Synchronisé avec le recentrage du plateau.
    /// </summary>
    private void DrawSlidingPanel(SpriteBatch sb)
    {
        var dx = BattleIntroProgress() * RightPanelWidth;
        sb.Begin(samplerState: SamplerState.PointClamp,
            transformMatrix: Matrix.CreateTranslation(dx, 0f, 0f));
        DrawPanelBackground(sb);
        DrawPlacementPanel(sb);
        sb.End();
    }

    private string CombatTitle() =>
        _run.IsBossCombat ? "COMBAT DE BOSS" : $"COMBAT {_run.CombatNumber} / {Run.TotalCombats}";

    /// <summary>
    /// Dessine un sprite à sa TAILLE NATIVE (jamais agrandi ni rétréci), centré dans
    /// <paramref name="area"/>. Garde-fou : si la zone est plus petite que le sprite, on réduit
    /// uniquement par un facteur ENTIER (1/2, 1/3…) — jamais fractionnaire — pour ne pas déborder
    /// ni déformer. Avec des boîtes de 64, le sprite 64×64 reste donc strictement intact.
    /// </summary>
    private static void DrawSpriteFit(SpriteBatch sb, Texture2D sprite, Rectangle area)
    {
        var src = sprite.Width;                       // sprites d'unité carrés (64×64)
        var box = Math.Min(area.Width, area.Height);
        var size = box >= src ? src : src / ((src + box - 1) / box);
        var x = area.X + (area.Width - size) / 2;
        var y = area.Y + (area.Height - size) / 2;
        sb.Draw(sprite, new Rectangle(x, y, size, size), Color.White);
    }

    /// <summary>
    /// Jeton/sprite d'une unité dessiné dans une zone (placeholder si pas d'asset).
    /// <paramref name="front"/> = montrer la face du joueur (présentation), sinon le dos.
    /// </summary>
    private void DrawChip(SpriteBatch sb, UnitClass cls, Faction faction, Rectangle area, bool front = false)
    {
        var sprite = SpriteFor(cls, faction, front);
        if (sprite != null)
        {
            DrawSpriteFit(sb, sprite, area);
            return;
        }

        var color = faction == Faction.Player ? Palette.Cyan1 : Palette.Purple5;
        DrawRect(sb, Inflate(area, 2), Palette.Black1);
        DrawRect(sb, area, color);
        Context.Font.DrawCentered(sb, cls.Name[..1], area, 2, Palette.White);
    }

    private void DrawDragGhost(SpriteBatch sb)
    {
        if (_dragSpec == null)
            return;

        var m = Context.Input.MousePosition;
        const int s = 64; // taille native du sprite → fantôme net, identique aux unités posées
        DrawChip(sb, _dragSpec.UnitClass, Faction.Player, new Rectangle(m.X - s / 2, m.Y - s / 2, s, s));
    }

    private void DrawRecruitment(SpriteBatch sb, Viewport viewport)
    {
        Context.Font.DrawCentered(sb, "RECRUTEMENT", new Rectangle(0, 60, viewport.Width, 24), 3, Palette.Yellow2);
        Context.Font.DrawCentered(sb, "CHOISIS UNE UNITE A REJOINDRE",
            new Rectangle(0, 100, viewport.Width, 12), 1, Palette.Blue1);

        for (var i = 0; i < _run.Draft.Count; i++)
            DrawDraftCard(sb, _run.Draft[i], DraftCardRect(i, _run.Draft.Count, viewport.Width, viewport.Height));
    }

    private void DrawDraftCard(SpriteBatch sb, UnitSpec spec, Rectangle rect)
    {
        Context.Style.DrawPanel(sb, rect);
        var c = spec.UnitClass;

        var icon = new Rectangle(rect.X + (rect.Width - 64) / 2, rect.Y + 12, 64, 64);
        DrawChip(sb, c, Faction.Player, icon, front: true);   // recrutement : portrait de FACE

        Context.Font.DrawCentered(sb, c.Name.ToUpperInvariant(),
            new Rectangle(rect.X, icon.Bottom + 6, rect.Width, 14), 2, Palette.White);
        Context.Font.DrawCentered(sb, $"DOM {spec.Domaine}".ToUpperInvariant(),
            new Rectangle(rect.X, icon.Bottom + 26, rect.Width, 10), 1, Palette.Cyan1);
        Context.Font.DrawCentered(sb, $"PV {c.MaxHp}   DEG {c.Damage}",
            new Rectangle(rect.X, icon.Bottom + 42, rect.Width, 10), 1, Palette.Yellow2);
        Context.Font.DrawCentered(sb, $"DEP {c.MoveRange}   TIR {c.AttackRange}",
            new Rectangle(rect.X, icon.Bottom + 56, rect.Width, 10), 1, Palette.Cyan2);
    }

    private static Rectangle DraftCardRect(int index, int count, int vpW, int vpH)
    {
        const int cardW = 180, cardH = 190, gap = 28;
        var total = count * cardW + (count - 1) * gap;     // centré sur le NOMBRE réel de cartes (peut être < 3)
        var x0 = (vpW - total) / 2;
        var y = (vpH - cardH) / 2 + 20;
        return new Rectangle(x0 + index * (cardW + gap), y, cardW, cardH);
    }

    private void DrawEndHud(SpriteBatch sb, Viewport viewport)
    {
        var victory = _run.Phase == RunPhase.Victory;
        var title = victory ? "VICTOIRE" : "DEFAITE";
        var sub = victory ? "BOSS VAINCU" : _defeatReason;

        Context.Font.DrawCentered(sb, title,
            new Rectangle(0, viewport.Height / 2 - 40, viewport.Width, 28), 4,
            victory ? Palette.Yellow2 : Palette.Purple5);
        Context.Font.DrawCentered(sb, sub,
            new Rectangle(0, viewport.Height / 2 + 4, viewport.Width, 12), 2, Palette.White);
        Context.Font.DrawCentered(sb, "CLIC POUR REJOUER",
            new Rectangle(0, viewport.Height / 2 + 36, viewport.Width, 12), 1, Palette.Blue1);
    }

    // ── Helpers de dessin ───────────────────────────────────────────────────────
    private void DrawRect(SpriteBatch sb, Rectangle r, Color c) => sb.Draw(Context.Pixel, r, c);

    private void DrawDim(SpriteBatch sb, Viewport viewport) =>
        DrawRect(sb, new Rectangle(0, 0, viewport.Width, viewport.Height), Palette.Black1 * 0.62f);

    private void DrawZone(SpriteBatch sb, GridLayout layout, Cell cell, Color c)
    {
        var top = layout.CellToScreen(cell.Column, cell.Row);
        DrawRect(sb, new Rectangle((int)top.X, (int)top.Y, layout.TileSize, layout.TileSize), c);
    }

    private void DrawZoneBorder(SpriteBatch sb, GridLayout layout, Cell cell, Color c, int thickness)
    {
        var top = layout.CellToScreen(cell.Column, cell.Row);
        DrawRectBorder(sb, new Rectangle((int)top.X, (int)top.Y, layout.TileSize, layout.TileSize), c, thickness);
    }

    private void DrawRectBorder(SpriteBatch sb, Rectangle r, Color c, int thickness)
    {
        DrawRect(sb, new Rectangle(r.X, r.Y, r.Width, thickness), c);
        DrawRect(sb, new Rectangle(r.X, r.Bottom - thickness, r.Width, thickness), c);
        DrawRect(sb, new Rectangle(r.X, r.Y, thickness, r.Height), c);
        DrawRect(sb, new Rectangle(r.Right - thickness, r.Y, thickness, r.Height), c);
    }

    private static Rectangle Inflate(Rectangle r, int by) =>
        new(r.X - by, r.Y - by, r.Width + 2 * by, r.Height + 2 * by);

    // Marge autour du terrain (px à l'écran).
    private const int BoardMargin = 24;

    /// <summary>
    /// Layout du plateau, MIS EN CACHE : il ne dépend que de la résolution virtuelle, donc on ne
    /// le recalcule qu'au changement de taille (sinon plusieurs allocations de GridLayout par frame,
    /// BuildLayout étant appelé par Draw et par chaque CellUnderMouse).
    /// </summary>
    private GridLayout BuildLayout()
    {
        var res = Context.VirtualResolution;
        if (_layoutCache == null || _layoutDirty || _layoutCacheFor != res)
        {
            _layoutCache = BuildLayoutCore();
            _layoutCacheFor = res;
            _layoutDirty = false;
        }
        return _layoutCache;
    }

    /// <summary>
    /// Largeur (px canvas) dans laquelle le plateau se cadre. Au PLACEMENT, le panneau de droite
    /// (inventaire) réserve sa bande ; dans les autres phases (combat, recrutement, fin) il n'y a
    /// plus de panneau → le plateau se recentre sur toute la largeur, libérant une marge de chaque
    /// côté pour les cartes d'unité (sélection à droite, ennemi survolé à gauche).
    /// </summary>
    private int AvailableWidth() =>
        _run.Phase == RunPhase.Placement ? VirtualViewport.Width - RightPanelWidth : VirtualViewport.Width;

    /// <summary>Progression 0→1 (lissée) du glissement d'entrée en combat ; 1 quand il est terminé.</summary>
    private float BattleIntroProgress() =>
        _battleIntroTimer <= 0 ? 1f : Smoothstep((float)(1 - _battleIntroTimer / BattleIntroDuration));

    private static float Smoothstep(float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Largeur servant à CENTRER le plateau (distincte de <see cref="AvailableWidth"/> qui fixe le
    /// zoom, stable). Pendant le glissement d'entrée en combat, elle interpole de la largeur du
    /// placement (panneau présent) vers le plein écran → le plateau glisse au lieu de sauter.
    /// </summary>
    private float CenteringWidth()
    {
        var full = AvailableWidth();
        if (_run.Phase == RunPhase.Battle && _battleIntroTimer > 0)
            return MathHelper.Lerp(VirtualViewport.Width - RightPanelWidth, full, BattleIntroProgress());
        return full;
    }

    /// <summary>
    /// Plus grand zoom ENTIER qui fait tenir le plateau (sprites jamais étirés : pixel-perfect)
    /// dans la zone disponible, marges comprises. Jamais sous 1. C'est le zoom « cadrage ».
    /// </summary>
    private int FitZoom()
    {
        var viewport = VirtualViewport;
        var availWidth = AvailableWidth();
        float boardW = Columns * GridLayout.DefaultTileSize;
        float boardH = (Rows - 1) * GridLayout.DefaultTileSize + GridLayout.DefaultSpriteHeight;
        int zoom = (int)Math.Min(
            (availWidth - 2f * BoardMargin) / boardW,
            (viewport.Height - 2f * BoardMargin) / boardH);
        return Math.Max(zoom, 1);
    }

    /// <summary>Zoom courant = cadrage, plus un cran (+1) quand le zoom rapproché est actif.</summary>
    private int CurrentZoom() => FitZoom() + (_zoomedIn ? 1 : 0);

    /// <summary>
    /// Origine du plateau : centré dans la zone de jeu, décalé par le pan caméra puis BORNÉ par axe
    /// pour que le plateau couvre toujours la zone (aucune bande noire). Si le plateau rentre sur un
    /// axe, il y reste verrouillé au centre (pan sans effet) ; sinon le pan glisse entre les bords.
    /// </summary>
    private GridLayout BuildLayoutCore()
    {
        var viewport = VirtualViewport;
        // Largeur de centrage (animée pendant l'entrée en combat) ; le zoom, lui, suit AvailableWidth
        // via CurrentZoom/FitZoom et reste stable → seul le glissement bouge, pas la taille des cases.
        var centerWidth = CenteringWidth();

        int zoom = CurrentZoom();
        var tile = GridLayout.DefaultTileSize * zoom;
        var spriteHeight = GridLayout.DefaultSpriteHeight * zoom;

        var pxW = Columns * tile;
        var pxH = (Rows - 1) * tile + spriteHeight;

        // Léger débordement (overscroll) aux bords pour révéler entièrement les sprites des rangées
        // extrêmes, dessinés AU-DESSUS de leur case (soulèvement) → la rangée du haut reste visible.
        var margin = tile * 0.5f;
        // Jeu de pan « libre » quand le plateau tient dans la zone : on autorise un léger débordement
        // (~1,5 case) dans les 4 directions au lieu de verrouiller au centre, pour pouvoir regarder un
        // peu autour. Quand le plateau déborde, c'est `margin` (overscroll des bords) qui s'applique.
        var slack = tile * 1.5f;
        float centerX = (centerWidth - pxW) / 2f;
        float centerY = (viewport.Height - pxH) / 2f;
        float ox = ClampAxis(centerX, _camera.X, pxW, centerWidth, margin, slack, out float cx);
        float oy = ClampAxis(centerY, _camera.Y, pxH, viewport.Height, margin, slack, out float cy);
        _camera = new Vector2(cx, cy);          // ré-écrit le pan borné (pas de dérive hors limites)

        // Origine arrondie au pixel entier → pas de scintillement pendant le pan (pixel-perfect).
        var origin = new Vector2(MathF.Round(ox), MathF.Round(oy));
        return new GridLayout(origin, tileSize: tile, spriteWidth: tile,
            spriteHeight: spriteHeight, rowPitch: tile);
    }

    /// <summary>
    /// Borne l'origine d'un axe : verrouillée au centre si le plateau (<paramref name="board"/>) rentre
    /// dans la zone (<paramref name="area"/>), sinon contrainte pour couvrir la zone bord à bord. Renvoie
    /// l'origine et, via <paramref name="clampedPan"/>, le pan effectivement appliqué.
    /// </summary>
    private static float ClampAxis(float center, float pan, float board, float area, float margin, float slack, out float clampedPan)
    {
        float lo, hi;
        if (board <= area) { lo = center - slack; hi = center + slack; }   // tient : petit jeu autour du centre
        else { lo = area - board - margin; hi = margin; }   // déborde : overscroll des deux bords
        float origin = MathHelper.Clamp(center + pan, lo, hi);
        clampedPan = origin - center;
        return origin;
    }

    /// <summary>
    /// Sprite à afficher pour une unité sur le plateau, selon son ORIENTATION : une unité qui
    /// regarde vers le bas (vers la caméra) montre sa face (&lt;asset&gt;_front / _ia_front), sinon
    /// son dos (&lt;asset&gt;_back / _ia_back). L'orientation suit la dernière action (déplacement/
    /// attaque) verticale — voir <see cref="FaceToward"/>. Repli sur le PNG simple, puis placeholder.
    /// </summary>
    private Texture2D? UnitSprite(Unit unit) => SpriteFor(unit.Class, unit.Faction, front: FacesDown(unit));

    /// <summary><paramref name="front"/> = l'unité regarde vers le bas (face caméra).</summary>
    private Texture2D? SpriteFor(UnitClass cls, Faction faction, bool front = false)
    {
        var variant = faction == Faction.Player
            ? $"{cls.Asset}_{(front ? "front" : "back")}"
            : $"{cls.Asset}_ia_{(front ? "front" : "back")}";
        return SpriteFor(variant) ?? SpriteFor(cls.Asset);
    }

    /// <summary>Orientation par défaut : le joueur regarde vers le haut (l'ennemi), l'ennemi vers le bas.</summary>
    private static bool DefaultFacesDown(Faction faction) => faction == Faction.Enemy;

    /// <summary>Vrai si l'unité regarde vers le bas (face caméra) — état suivi, ou défaut selon le camp.</summary>
    private bool FacesDown(Unit unit) =>
        _facesDown.TryGetValue(unit, out var f) ? f : DefaultFacesDown(unit.Faction);

    /// <summary>
    /// Oriente l'unité d'après une action <paramref name="from"/> → <paramref name="to"/>.
    /// JOUEUR : regarde vers le bas (front/face) UNIQUEMENT en descendant (diagonale descendante
    /// comprise) ; tout le reste (montée, horizontal) → dos (back). ENNEMI : exactement l'inverse —
    /// front par défaut (et en horizontal), dos (ia_back) seulement en montant.
    /// </summary>
    private void FaceToward(Unit unit, Cell from, Cell to)
    {
        _facesDown[unit] = unit.Faction == Faction.Player
            ? to.Row > from.Row     // joueur : face seulement vers le bas
            : to.Row >= from.Row;   // ennemi : face sauf en montant
    }

    /// <summary>Charge un PNG d'unité par nom de fichier (mis en cache), ou null s'il est absent.</summary>
    private Texture2D? SpriteFor(string fileName)
    {
        if (!_unitSprites.TryGetValue(fileName, out var sprite))
        {
            sprite = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath($"{UnitAssetFolder}/{fileName}.png"));
            _unitSprites[fileName] = sprite;
        }
        return sprite;
    }

    private void UpdatePauseMenu()
    {
        if (!Context.Input.WasLeftClicked)
            return;

        Context.Sounds.Play("menu_click");

        var viewport = VirtualViewport;
        var action = _pauseMenu.HandleClick(Context.Input.MousePosition, viewport.Width, viewport.Height);
        switch (action)
        {
            case MenuAction.MainMenu:
                // La progression est déjà sauvegardée (phase de placement) : on peut quitter vers
                // le menu, le slot proposera « Continuer ».
                Context.Scenes.Change(new MainMenuScene(Context));
                break;
            case MenuAction.Quit:
                Context.Quit();
                break;
            case MenuAction.GraphicsChanged:
                Context.Display.Apply(Context.Settings.Display);
                Context.Saves.SaveSettings(Context.Settings);
                break;
            case MenuAction.VolumeChanged:
                Context.Audio.Apply();
                Context.Saves.SaveSettings(Context.Settings);
                break;
        }
    }
}
