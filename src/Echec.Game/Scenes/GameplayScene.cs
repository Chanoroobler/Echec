using System.Collections.Generic;
using System.Linq;
using Echec.Core.Battle;
using Echec.Core.Campaign;
using Echec.Core.Map;
using Echec.Engine;
using Echec.Engine.Audio;
using Echec.Engine.Input;
using Echec.Engine.Localization;
using Echec.Engine.Rendering;
using Echec.Engine.Scenes;
using Echec.Engine.UI;
using Echec.Game.Dev;
using Echec.Game.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Echec.Game.Scenes;

/// <summary>
/// Scène de campagne (première boucle de gameplay) : terrain 8×8, boucle
/// Placement → Combat → Recrutement → … sur 6 combats, le dernier étant le boss.
/// Le commandant (mort = game over) est posé d'office ; le joueur déploie le reste de
/// son inventaire par glisser-déposer depuis le panneau de droite, puis combat l'IA.
/// Échap = menu pause, F1 = bascule du quadrillage, F10 = visualiseur d'arbres (dev).
/// </summary>
public sealed class GameplayScene : Scene
{
    // Dimensions du plateau du combat COURANT — variables : une map dessinée (escarmouche 6×6) les
    // fixe à sa taille, sinon 8×8 (terrain aléatoire). Réglées par combat dans BeginPlacement/BeginTutorial.
    private int Columns = 8;
    private int Rows = 8;
    // Plafond d'unités joueur déployées sur le plateau, COMMANDANT COMPRIS (commandant + 4 recrues max).
    private const int MaxDeployed = 5;
    private const double AiDelaySeconds = 0.45;
    private const double TutorialEnemyDelay = 2.6;   // tuto : laisse lire la pop avant que l'IA bouge/contre-attaque

    // Remontée du sprite (fraction de la case) pour centrer le socle sur la case. 0 = dans la case.
    private const float SpriteLiftFraction = 0.25f;

    // Panneau latéral droit (inventaire au placement, infos en combat).
    private const int RightPanelWidth = 240;   // élargi pour 3 colonnes de portraits 64×64
    private const int PanelPad = 12;
    // Inventaire en grille : portraits 64×64 NATIFS (jamais redimensionnés), 3 colonnes.
    private const int InvIconSize = 64;
    private const int InvCols = 3;
    private const int InvGapX = 8;
    private const int InvCellH = InvIconSize + 14; // portrait + libellé dessous
    private const int InvGapY = 6;
    private const int PanelListTop = 110;

    // (Ordre de déploiement centre→bords : ColumnsCenterOut(), calculé selon la largeur du plateau.)

    // Chemins d'assets résolus depuis le dossier de l'exe (indépendant du répertoire de travail).
    private static string AssetPath(string relative) =>
        System.IO.Path.Combine(System.AppContext.BaseDirectory, relative);

    // Terrain du combat courant. Combats 1-2 = escarmouche 6×6 dessinée (_map) ; sinon 8×8 aléatoire.
    private Battlefield _battlefield = Battlefield.CreateFlat(8, 8);

    // Catalogue de tuiles (tiles.json) + map des combats 1-2 (escarmouche_01), chargés une fois au Load.
    private TileCatalog? _catalog;
    private MapData? _escarmouche;
    // Map du combat courant : non-null = combat sur map dessinée (spawns peints) ; null = terrain aléatoire.
    private MapData? _map;

    // Tilesets : une tuile peut être rendue depuis une feuille (rectangle source) plutôt qu'un PNG
    // individuel. _sheets : nom de feuille → texture ; _tileSheet : id → feuille ; _tileSrc : id → cellule.
    private readonly Dictionary<string, Texture2D> _sheets = new();
    private readonly Dictionary<string, string> _tileSheet = new();
    private readonly Dictionary<string, Rectangle> _tileSrc = new();

    // Animation d'assemblage du plateau au début du placement : les tuiles montent du bas, en cascade.
    private float _boardIntro;          // temps écoulé depuis le début de l'assemblage (s)
    private float _boardIntroTotal;     // durée totale (0 = pas d'animation, ex. tutoriel)
    private const float BoardIntroStagger = 0.05f;   // délai entre 2 tuiles successives
    private const float BoardIntroRise = 0.32f;      // durée de montée d'une tuile
    private const float BoardIntroDrop = 0.6f;       // petite remontée (en hauteurs de sprite) : émergence, pas chute

    // Texture de tuile par type de terrain (PNG Assets/Tiles, repli sur un aplat coloré 64×80).
    private readonly Dictionary<string, Texture2D> _tiles = new();
    private WaterRenderer _water = null!;
    private Texture2D _waterNoise = null!;
    private float _time;
    private PauseMenu _pauseMenu = null!;
    private PauseMenuRenderer _pauseRenderer = null!;
    private DomaineTreeRenderer _treeRenderer = null!;

    // Effets de combat shader (dissolution / flash) + animation d'attaque en cours.
    private CombatFxRenderer _combatFx = null!;
    private readonly MeleeStrikeFx _fx = new();
    // Particules poolées : ne servent plus que pour le feu d'artifice d'extinction des chiffres de dégâts.
    private readonly SparkBurst _sparks = new();
    // Garde-fou « impact traité une seule fois par coup » (spawn du chiffre de dégâts au contact).
    private bool _impactHandled;
    // Chiffres de dégâts flottants (jaillis à l'impact, puis éclatent) + dégâts du coup en attente.
    private readonly DamagePopups _damagePopups = new();
    private int _pendingDamage;
    // Tutoriel « combat zéro » : non-null pendant le combat scénarisé de début de campagne.
    private TutorialGuide? _tutorial;
    private readonly List<Cell> _tutorialMoves = new();   // buffer des coups de l'ennemi scripté du tuto
    private int _tutorialCardIndex;                        // donnée de carte en cours de revue (0..3)
    private float _tutorialCardTimer;                      // temps restant avant passage auto à la donnée suivante
    private const int TutorialCardStats = 5;              // Déplacement (domaine), PV, Puissance, Mouvement, Portée
    private const float TutorialCardSeconds = 6f;         // durée d'affichage auto par donnée

    // Recrutement : le panneau d'inventaire est VISIBLE pendant le choix (on voit son armée, hors
    // commandant). À la sélection, le pion de la carte choisie VOLE vers son emplacement d'inventaire,
    // puis on recrute et on passe au placement. _recruitChoice = unité en vol, _recruitFrom = départ.
    private const float RecruitFlightDuration = 0.5f;
    private UnitSpec? _recruitChoice;
    private float _recruitHold;
    private Vector2 _recruitFrom;
    private int _recruitFocus;   // carte du draft sous le focus (navigation manette/surbrillance)

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

    // Fusion (placement) : pile d'unités identiques en cours d'assemblage par empilement. _fusionGroup
    // = toutes les pièces de la pile (vide=rien, 1..N-1=empilement, ==FusionSize=popup de choix).
    // _fusionCell = case du plateau où la pile est ancrée (null = pile dans le panneau de RÉSERVE).
    // _fusionFocus = carte d'évolution sous le focus dans la popup.
    private readonly List<UnitSpec> _fusionGroup = new();
    private Cell? _fusionCell;
    private int _fusionFocus;
    private bool FusionOpen => _fusionGroup.Count == Run.FusionSize;

    // Portage de la pile ENTIÈRE (on attrape les 2 pièces d'un coup, pour la déplacer). _carryPileFrom
    // = ancre d'origine (null = réserve) pour restaurer sur un lâcher invalide.
    private bool _carryPile;
    private Cell? _carryPileFrom;
    // Slot VISUEL où la pile de réserve s'affiche (là où elle a été formée), pour ne pas la renvoyer en fin de grille.
    private int _fusionReserveSlot;

    // Petit « punch scale » à chaque empilement (la pile gonfle brièvement puis revient).
    private double _fusionPunchTimer;
    private const double FusionPunchDuration = 0.16;

    // Animation d'ÉVOLUTION (gèle le placement). Machine à PHASES : Reveal (timée : zoom + clignotement
    // + révélation) → Hold (attend le CLIC du joueur, qui range la pièce) → Return (la pièce revient à
    // sa place). Version longue/dramatique UNIQUEMENT la 1re fois (sinon Reveal court auto). _evoSource =
    // case/slot de la pièce (la « caméra » zoome depuis là vers le centre puis y revient).
    private enum EvoPhase { None, Reveal, Hold, Return }
    private EvoPhase _evoPhase = EvoPhase.None;
    private double _evoPhaseTimer;
    private const double EvoRevealDuration = 8.4;   // zoom + clignotement + révélation (1re découverte)
    private const double EvoReturnDuration = 0.6;   // retour de la pièce à sa place après le clic
    private const double EvoShortDuration = 0.4;    // version rapide (déjà obtenue)
    private bool _evoLong;
    private Rectangle _evoSource;
    private UnitClass? _evoBase;
    private UnitClass? _evoResult;
    private bool _evoSparked;             // gerbe au flash (une seule fois)
    private bool EvoPlaying => _evoPhase != EvoPhase.None;

    // Curseur de plateau (manette) : case visée. En placement, _gpInventory bascule le focus dans
    // l'inventaire (sélection d'une unité à déployer) et _invFocus en est l'index.
    private Cell _cursor = new(4, 7);   // valeur par défaut (réinitialisée par combat dans BeginPlacement)
    private bool _gpInventory;
    private int _invFocus;

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
    private bool _showGrid = true;   // quadrillage permanent du plateau (bascule F1 / Select), activé par défaut

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
        LoadMaps();
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
        foreach (var sheet in _sheets.Values)
            sheet.Dispose();
        _sheets.Clear();
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
    /// Précharge les 3 tuiles « historiques » (campagne à terrain aléatoire). Les tuiles des maps
    /// dessinées sont chargées à la demande par <see cref="TileTexture"/>.
    /// </summary>
    private void LoadTiles()
    {
        TileTexture("grass");
        TileTexture("water");
        TileTexture("mountain");
    }

    /// <summary>
    /// Charge le catalogue de tuiles (<c>tiles.json</c>) et la map d'escarmouche 6×6 (combats 1-2). En
    /// cas de fichier absent/illisible, on retombe silencieusement sur le terrain aléatoire (jeu jouable).
    /// </summary>
    private void LoadMaps()
    {
        try
        {
            var tilesJson = System.IO.File.ReadAllText(AssetPath("Assets/Tiles/tiles.json"));
            _catalog = TileCatalog.FromJson(tilesJson);
            LoadTilesets(tilesJson);
            _escarmouche = MapLoader.Parse(
                System.IO.File.ReadAllText(AssetPath("Assets/Maps/escarmouche_01.json")), _catalog);
        }
        catch
        {
            _catalog = null;
            _escarmouche = null;   // repli : tous les combats en terrain aléatoire
        }
    }

    /// <summary>
    /// Texture d'une tuile par id (cache ; disposition gérée par <see cref="Unload"/>). Charge
    /// <c>Assets/Tiles/&lt;id&gt;.png</c>, repli sur un placeholder. Cas spéciaux « historiques » :
    /// l'eau est translucide (on voit le shader animé dessous), la montagne retombe sur un aplat de
    /// palette si le PNG manque.
    /// </summary>
    private Texture2D TileTexture(string id)
    {
        if (_tiles.TryGetValue(id, out var tex))
            return tex;

        var path = AssetPath($"Assets/Tiles/{id}.png");
        tex = id switch
        {
            "water" => Textures.LoadPngOrNull(Context.GraphicsDevice, path)
                ?? Textures.CreateTransparentTile(Context.GraphicsDevice,
                    WithAlpha(Palette.WaterShallow, 48), WithAlpha(Palette.WaterShallow, 140)),
            "mountain" => Textures.LoadPngOrNull(Context.GraphicsDevice, path)
                ?? Textures.CreateColorTile(Context.GraphicsDevice, Palette.Blue1, Palette.Black4),
            _ => Textures.LoadTileOrPlaceholder(Context.GraphicsDevice, path),
        };
        _tiles[id] = tex;
        return tex;
    }

    /// <summary>
    /// Lit la section <c>tilesets</c> + les <c>sheet</c>/<c>col</c>/<c>row</c> de tiles.json, charge les
    /// feuilles (<c>Assets/Tilesets/&lt;file&gt;</c>) et mémorise le rectangle source de chaque tuile.
    /// </summary>
    private void LoadTilesets(string tilesJson)
    {
        var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var doc = System.Text.Json.JsonSerializer.Deserialize<TilesDto>(tilesJson, opts);
        if (doc?.Tilesets is null || doc.Tiles is null)
            return;

        foreach (var (name, sheet) in doc.Tilesets)
        {
            if (sheet.File is null) continue;
            var tex = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath($"Assets/Tilesets/{sheet.File}"));
            if (tex != null)
                _sheets[name] = tex;
        }

        foreach (var t in doc.Tiles)
        {
            if (t.Id is null || t.Sheet is null || !doc.Tilesets.TryGetValue(t.Sheet, out var sheet))
                continue;
            _tileSheet[t.Id] = t.Sheet;
            _tileSrc[t.Id] = new Rectangle(t.Col * sheet.CellW, t.Row * sheet.CellH, sheet.CellW, sheet.CellH);
        }
    }

    /// <summary>
    /// Texture + rectangle source d'une tuile : sa cellule dans la feuille si elle y est, sinon un PNG
    /// individuel pris en entier (legacy grass/water/mountain, ou repli placeholder).
    /// </summary>
    private (Texture2D Texture, Rectangle Source) TileSprite(string id)
    {
        if (_tileSheet.TryGetValue(id, out var sheetName) && _sheets.TryGetValue(sheetName, out var sheet))
            return (sheet, _tileSrc[id]);

        var tex = TileTexture(id);
        return (tex, new Rectangle(0, 0, tex.Width, tex.Height));
    }

    private sealed class TilesDto
    {
        public Dictionary<string, SheetDto>? Tilesets { get; set; }
        public List<TileEntryDto>? Tiles { get; set; }
    }

    private sealed class SheetDto
    {
        public string? File { get; set; }
        public int CellW { get; set; }
        public int CellH { get; set; }
    }

    private sealed class TileEntryDto
    {
        public string? Id { get; set; }
        public string? Sheet { get; set; }
        public int Col { get; set; }
        public int Row { get; set; }
    }

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
        var resumed = _initialRun != null;
        if (_initialRun != null)
        {
            _run = _initialRun;            // reprise depuis une sauvegarde (garde son propre FirstRun)
        }
        else
        {
            // Nouvelle campagne : la TOUTE PREMIÈRE du joueur a un déblocage ennemi plus doux.
            var firstRun = !Context.Saves.HasPlayedBefore();
            if (firstRun)
                Context.Saves.MarkPlayed();
            _run = new Run(firstRun: firstRun);
        }
        _initialRun = null;                // ne sert qu'au tout premier chargement de la scène

        // Nouvelle campagne → tutoriel « combat zéro » (skippable). Reprise → direct au combat réel.
        if (resumed)
            BeginPlacement();
        else
            BeginTutorial();
    }

    /// <summary>Prépare la phase de placement : nouveau terrain, commandant posé d'office.</summary>
    private void BeginPlacement()
    {
        // Combats 1-2 : escarmouche 6×6 dessinée (taille + spawns peints). Au-delà : terrain aléatoire 8×8.
        _map = _run.CombatNumber <= 2 ? _escarmouche : null;
        if (_map is { } map)
        {
            Columns = map.Width;
            Rows = map.Height;
            _battlefield = Battlefield.FromMap(map);
        }
        else
        {
            Columns = 8;
            Rows = 8;
            _battlefield = _run.BuildBattlefield(Columns, Rows);
        }
        _match = new Match(Columns, Rows, _battlefield);

        // Effet d'émergence : les tuiles sortent de l'eau (fondu + remontée), en cascade (cf. BoardIntroAnim).
        _boardIntro = 0f;
        _boardIntroTotal = Columns * Rows * BoardIntroStagger + BoardIntroRise;
        _facesDown.Clear();
        _playerSpec.Clear();
        _enemySpec.Clear();
        _enemyKillOrder.Clear();
        _pending.Clear();
        _fusionGroup.Clear();
        _fusionCell = null;
        _carryPile = false;
        _fusionReserveSlot = 0;
        _fusionPunchTimer = 0;
        _evoPhase = EvoPhase.None;
        _evoBase = null;
        _evoResult = null;
        _dragSpec = null;
        _dragFrom = null;
        _damagePopups.Clear();   // pas de chiffre/explosion reporté du combat précédent
        _sparks.Clear();
        ClearSelection();
        ResetCamera();
        _aiTimer = 0;
        _recruitChoice = null;   // fin d'un éventuel vol de recrutement
        _recruitHold = 0;
        var commanderCell = CommanderStart();
        _cursor = commanderCell;       // curseur manette sur la case de départ du commandant
        _gpInventory = false;

        var commander = _run.Commander;
        PlacePlayer(commander, commanderCell);

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

    /// <summary>
    /// Prépare le TUTORIEL « combat zéro » : board plat, scénario fixe (commandant + 1 soldat joueur,
    /// 1 soldat ennemi à 2 cases), pas de phase de placement, ennemi passif, AUCUNE sauvegarde.
    /// On passe direct en phase Battle pour réutiliser toute la boucle/le rendu de combat.
    /// </summary>
    private void BeginTutorial()
    {
        Columns = 8;
        Rows = 8;
        _map = null;                                            // tutoriel : board plat 8×8, jamais une map dessinée
        _boardIntro = _boardIntroTotal = 0f;                    // pas d'animation d'assemblage en tutoriel
        _battlefield = Battlefield.CreateFlat(Columns, Rows);   // herbe partout, aucun obstacle
        _match = new Match(Columns, Rows, _battlefield);
        _facesDown.Clear();
        _playerSpec.Clear();
        _enemySpec.Clear();
        _enemyKillOrder.Clear();
        _pending.Clear();
        _dragSpec = null;
        _dragFrom = null;
        _damagePopups.Clear();
        _sparks.Clear();
        ClearSelection();
        ResetCamera();
        _aiTimer = 0;
        _recruitChoice = null;
        _recruitHold = 0;
        _gpInventory = false;
        _battleIntroTimer = 0;

        // Commandant déjà posé (montre l'unité essentielle), 1 SOLDAT à déployer dans l'inventaire.
        var commanderCell = new Cell(Columns / 2, Rows - 1);
        PlacePlayer(_run.Commander, commanderCell);
        _pending.Add(new UnitSpec(Domaine.Pion, Domaines.Pion.BaseClass));

        // 1 Soldat ennemi NORMAL (12 PV, dégâts 10) : il survit à la 1re attaque et contre-attaque.
        var enemyCell = new Cell(Columns / 2, 1);
        _match.Place(enemyCell, new UnitSpec(Domaine.Pion, Domaines.Pion.BaseClass).Spawn(Faction.Enemy));

        _tutorial = new TutorialGuide { Commander = commanderCell, EnemySoldier = enemyCell };
        _cursor = commanderCell;        // curseur manette sur la zone joueur
        MarkLayoutDirty();
        // Reste en phase PLACEMENT (pas de StartBattle, pas de sauvegarde) : le tuto guide le placement.
    }

    /// <summary>Fin du tutoriel (victoire OU skip) : enchaîne sur le vrai combat 1 (CombatNumber inchangé).</summary>
    private void EndTutorial()
    {
        _tutorial = null;
        ClearSelection();
        _run.ReturnToPlacement();   // le tuto avait basculé la run en phase Battle → on revient au placement
        BeginPlacement();           // 1re sauvegarde de la run a lieu ici (combat réel), jamais pendant le tuto
    }

    /// <summary>Fin du placement : lance le combat (la vague ennemie est déjà posée).</summary>
    private void BeginBattle()
    {
        CancelDrag();
        // Pile de fusion non terminée : sa base reprend sa case (et combat), le surplus va en réserve.
        DisbandFusionToOrigin();
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

    /// <summary>Case de départ du commandant : centre de la rangée du bas, ou une case de déploiement de la map si ce centre n'en est pas une.</summary>
    private Cell CommanderStart()
    {
        var center = new Cell(Columns / 2, Rows - 1);
        if (_map is { } m && !m.PlayerSpawns.Contains(center))
            return m.PlayerSpawns.Count > 0 ? m.PlayerSpawns[^1] : center;
        return center;
    }

    private void PlaceEnemies(List<UnitSpec> wave)
    {
        var cells = EnemyDeployCells().ToList();
        if (_map != null)
            _run.ShuffleForCombat(cells);   // map dessinée : ennemis sur des cases tirées au hasard parmi les E (déterministe pour ce combat)
        var i = 0;
        foreach (var spec in wave)
        {
            while (i < cells.Count
                && (_match.UnitAt(cells[i]) != null || _battlefield[cells[i]].BlocksMovement))
                i++;
            if (i >= cells.Count) break;
            var unit = spec.Spawn(Faction.Enemy);
            _match.Place(cells[i], unit);
            _enemySpec[unit] = spec;          // pour retrouver le gabarit à la mort (recrutement)
            i++;
        }
    }

    // Colonnes du centre vers les bords (déploiement groupé au milieu), pour la largeur courante.
    // Pour 8 colonnes : 3,4,2,5,1,6,0,7 (identique à l'ancien tableau figé).
    private IEnumerable<int> ColumnsCenterOut()
    {
        var mid = (Columns - 1) / 2;
        yield return mid;
        for (var d = 1; d < Columns; d++)
        {
            if (mid + d < Columns) yield return mid + d;
            if (mid - d >= 0) yield return mid - d;
        }
    }

    /// <summary>Vrai si la case est une case de déploiement joueur : cases peintes de la map, sinon les 2 rangées du bas.</summary>
    private bool IsPlayerZone(Cell cell) =>
        _map is { } m ? m.PlayerSpawns.Contains(cell) : cell.Row >= Rows - 2;

    /// <summary>Cases de déploiement joueur : cases P de la map dessinée, sinon les 2 rangées du bas (centre→bords).</summary>
    private IEnumerable<Cell> PlayerDeployCells() =>
        _map is { } m ? m.PlayerSpawns : DefaultDeployCells(Rows - 1, Rows - 2);

    /// <summary>Cases de spawn ennemi : cases E de la map dessinée, sinon les 2 rangées du haut (centre→bords).</summary>
    private IEnumerable<Cell> EnemyDeployCells() =>
        _map is { } m ? m.EnemySpawns : DefaultDeployCells(0, 1);

    /// <summary>Cases des deux rangées <paramref name="rowA"/>→<paramref name="rowB"/>, colonnes du centre vers les bords.</summary>
    private IEnumerable<Cell> DefaultDeployCells(int rowA, int rowB)
    {
        var step = rowA <= rowB ? 1 : -1;
        for (var row = rowA; row != rowB + step; row += step)
            foreach (var col in ColumnsCenterOut())
                yield return new Cell(col, row);
    }

    // ── Mise à jour ─────────────────────────────────────────────────────────────
    public override void Update(GameTime gameTime)
    {
        // Le courant d'eau avance en continu (même en pause / menus).
        _time += (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (_boardIntro < _boardIntroTotal)
            _boardIntro += (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Musique pilotée par la phase (appel idempotent : ne relance pas le contexte déjà en cours).
        UpdateMusic();

        if (_landingTimer > 0)
            _landingTimer -= gameTime.ElapsedGameTime.TotalSeconds;
        if (_fusionPunchTimer > 0)
            _fusionPunchTimer -= gameTime.ElapsedGameTime.TotalSeconds;

        // Outil dev (F10) : prioritaire, fige le reste. (F1 = bascule du quadrillage.)
        if (Context.Input.WasKeyPressed(Keys.F10) && !_pauseMenu.IsOpen)
            _showTrees = !_showTrees;
        if (_showTrees)
        {
            if (Context.Input.WasKeyPressed(Keys.Escape))
                _showTrees = false;
            return;
        }

        // Échap pendant la popup de fusion : annule la fusion plutôt que d'ouvrir le menu pause.
        if (FusionOpen && !_pauseMenu.IsOpen && Context.Input.WasKeyPressed(Keys.Escape))
        {
            CancelFusion();
            return;
        }

        // Ouverture/fermeture : Échap (clavier) ou Start (manette). En manette, B referme aussi.
        if (Context.Input.WasKeyPressed(Keys.Escape) || Context.Input.WasMenuPressed
            || (_pauseMenu.IsOpen && Context.Input.WasCancelPressed))
        {
            if (_pauseMenu.IsOpen) { _pauseMenu.Back(); Context.Sounds.Play("menu_close"); }
            else { _pauseMenu.Open(); Context.Sounds.Play("menu_open"); }
        }

        if (_pauseMenu.IsOpen) { UpdatePauseMenu(); return; }

        // Bascule du quadrillage permanent du plateau : F1 (clavier) ou Select (manette).
        if (Context.Input.WasKeyPressed(Keys.F1) || Context.Input.WasSelectPressed)
            _showGrid = !_showGrid;

        // Zoom (molette) + pan (flèches / ZQSD) uniquement sur les phases avec plateau, et pas pendant
        // le glissement d'entrée en combat (l'animation pilote seule le cadrage à ce moment-là).
        // Caméra gelée derrière un modal de placement (popup de fusion / animation d'évolution).
        if (_run.Phase is RunPhase.Placement or RunPhase.Battle && _battleIntroTimer <= 0
            && !FusionOpen && !EvoPlaying)
            UpdateCamera(gameTime);

        switch (_run.Phase)
        {
            case RunPhase.Placement: UpdatePlacement(gameTime); break;
            case RunPhase.Battle: UpdateBattle(gameTime); break;
            case RunPhase.Recruitment: UpdateRecruitment(gameTime); break;
            case RunPhase.Victory:
            case RunPhase.Defeat:
                // Run terminée (slot déjà effacé) : un clic ramène au menu principal.
                if (Context.Input.WasLeftClicked)
                    Context.Scenes.Change(new MainMenuScene(Context));
                break;
        }
    }

    /// <summary>
    /// Choisit la musique selon la phase courante : placement → « Relaxed » (calme, comme le menu) ;
    /// combat de boss → « Fight 2 » ; tout le reste (combat normal, recrutement, victoire, défaite) →
    /// la playlist qui tourne. Idempotent côté <see cref="MusicPlayer"/> : sans changement de contexte,
    /// rien n'est coupé ni relancé.
    /// </summary>
    private void UpdateMusic()
    {
        var scene = _run.Phase switch
        {
            RunPhase.Placement => MusicScene.Calm,
            RunPhase.Battle => _run.IsBossCombat ? MusicScene.Boss : MusicScene.Combat,
            _ => MusicScene.Combat,   // recrutement / victoire / défaite : « sinon », la playlist
        };
        Context.Music.Play(scene);
    }

    private void UpdatePlacement(GameTime gameTime)
    {
        // Tuto : bouton « Passer » prioritaire ; sinon on suit l'avancement des étapes de placement.
        if (_tutorial != null)
        {
            if (TutorialSkipPressed()) { EndTutorial(); return; }
            var pre = _tutorial.Step;
            UpdateTutorialPlacement((float)gameTime.ElapsedGameTime.TotalSeconds);
            if (pre is TutorialStep.Intro or TutorialStep.ReviewCard)
                return;   // intro / revue : placement gelé (et le clic d'avancement est consommé)
        }

        // Popup de fusion ouverte : on gèle le placement et on ne traite que son choix.
        if (FusionOpen)
        {
            UpdateFusionPopup();
            return;
        }

        // Animation d'évolution en cours : placement gelé le temps du morph.
        if (EvoPlaying)
        {
            UpdateEvolutionAnimation((float)gameTime.ElapsedGameTime.TotalSeconds);
            return;
        }

        // Fin du feu d'artifice de fusion : les particules continuent de vivre après l'animation.
        if (_sparks.HasActive)
            _sparks.Update((float)gameTime.ElapsedGameTime.TotalSeconds);

        if (Context.Input.UsingGamepad)
            UpdatePlacementGamepad();

        if (Context.Input.WasKeyPressed(Keys.Enter))
        {
            TryStartBattle();
            return;
        }

        var mouse = Context.Input.MousePosition;

        // Pile portée (souris) : le relâchement la repose ; les autres interactions sont gelées.
        if (_carryPile)
        {
            if (!Context.Input.UsingGamepad && Context.Input.WasLeftReleased)
                DropCarriedPile(CellUnderMouse(), IsOverPanel(mouse));
            return;
        }

        // Clic sur le bouton d'annulation de la pile de fusion (état 2/3, réserve ou plateau).
        if (Context.Input.WasLeftClicked && FusionStacking && _dragSpec == null
            && FusionCancelRectActive().Contains(mouse))
        {
            CancelFusion();
            return;
        }

        // Bouton COMBATTRE (souris) : lance le combat comme Entrée. Testé avant le drag pour ne pas démarrer une prise.
        if (_tutorial == null && Context.Input.WasLeftClicked && FightButtonRect().Contains(mouse))
        {
            TryStartBattle();
            return;
        }

        if (Context.Input.WasLeftClicked)
            BeginDrag(mouse);
        else if (Context.Input.WasLeftReleased && _dragSpec != null)
            EndDrag(mouse);
    }

    /// <summary>Lance le combat — en tuto, uniquement une fois le soldat posé (étape StartCombat).</summary>
    private void TryStartBattle()
    {
        if (_tutorial != null)
        {
            if (_tutorial.Step != TutorialStep.StartCombat)
                return;                 // il faut d'abord poser le soldat ET avoir vu la revue de carte
            BeginBattle();
            _battleIntroTimer = 0;      // pas d'animation de panneau en tuto
            _tutorial.Advance();        // StartCombat → Move
            return;
        }
        BeginBattle();
    }

    /// <summary>Avancement des étapes de PLACEMENT du tuto (prise → pose → revue de carte → lancement).</summary>
    private void UpdateTutorialPlacement(float dt)
    {
        var t = _tutorial!;
        switch (t.Step)
        {
            case TutorialStep.Intro:
                if (Context.Input.WasLeftClicked || Context.Input.WasKeyPressed(Keys.Enter) || Context.Input.WasConfirmPressed)
                    t.Advance();                            // intro lue → PickSoldier
                break;
            case TutorialStep.PickSoldier:
                if (_dragSpec != null)
                    t.Advance();                            // soldat pris en main → PlaceSoldier
                break;
            case TutorialStep.PlaceSoldier:
                if (_dragSpec == null && _pending.Count == 0)
                {
                    t.PlayerSoldier = FindTutorialSoldierCell();
                    RepositionTutorialEnemy(t.PlayerSoldier);   // rapproche l'ennemi (peu de déplacement)
                    _tutorialCardIndex = 0;
                    _tutorialCardTimer = TutorialCardSeconds;
                    t.Advance();                                // soldat posé → ReviewCard (revue DÈS la pose)
                }
                break;
            case TutorialStep.ReviewCard:
                UpdateTutorialCardReview(dt);                   // défile les données ; à la fin → StartCombat
                break;
        }
    }

    /// <summary>Rapproche l'ennemi du tuto : 3 cases DEVANT le soldat posé (même colonne) → 1 pas chacun = corps à corps.</summary>
    private void RepositionTutorialEnemy(Cell soldier)
    {
        var t = _tutorial!;
        if (_match.UnitAt(t.EnemySoldier) is not { } enemy)
            return;
        var target = new Cell(soldier.Column, System.Math.Max(1, soldier.Row - 3));
        if (target == t.EnemySoldier || _match.UnitAt(target) != null)
            return;
        _match.Remove(t.EnemySoldier);
        _match.Place(target, enemy);
        t.EnemySoldier = target;
    }

    /// <summary>Case du soldat du tuto (seul pion joueur non essentiel sur le plateau).</summary>
    private Cell FindTutorialSoldierCell()
    {
        foreach (var (cell, unit) in _match.Units())
            if (unit.Faction == Faction.Player && !unit.IsEssential)
                return cell;
        return _tutorial!.PlayerSoldier;
    }

    /// <summary>
    /// Placement à la manette : curseur de case (croix), A saisir/poser, B annuler, RB inventaire,
    /// Y lancer le combat. La saisie/dépose réutilise exactement la logique du glisser souris.
    /// </summary>
    private void UpdatePlacementGamepad()
    {
        if (Context.Input.WasQuaternaryPressed) { TryStartBattle(); return; }   // Y = COMBATTRE

        if (_gpInventory) { UpdateInventoryFocus(); return; }

        // B sans rien porter (et hors portage) : annule une pile de fusion en cours (réserve ou plateau).
        if (FusionStacking && !_carryPile && _dragSpec == null && Context.Input.WasCancelPressed)
        {
            CancelFusion();
            return;
        }

        MoveCursor();

        // RB : terrain → inventaire (choisir une unité à déployer, si on n'en porte pas).
        if (Context.Input.WasRightShoulderPressed && _dragSpec == null && _pending.Count > 0)
        {
            _gpInventory = true;
            _invFocus = System.Math.Clamp(_invFocus, 0, _pending.Count - 1);
            return;
        }

        if (Context.Input.WasConfirmPressed)
        {
            if (_carryPile) DropCarriedPile(_cursor, overPanel: false);          // reposer la pile portée
            else if (_dragSpec != null) EndDragAt(_cursor, overPanel: false);    // poser/échanger au curseur
            else if (FusionStacking && _fusionCell == _cursor) GrabPile();       // attraper la pile sous le curseur
            else PickUpAt(_cursor);                                              // saisir l'unité sous le curseur
        }
        else if (Context.Input.WasCancelPressed && _carryPile)
        {
            DropCarriedPile(_carryPileFrom, overPanel: _carryPileFrom is null);  // B : retour à l'ancre
        }
        else if (Context.Input.WasCancelPressed && _dragSpec != null)
        {
            CancelDrag();
        }
    }

    /// <summary>Focus inventaire (manette) : navigue la grille, A prend l'unité en main, B/RB sort.</summary>
    private void UpdateInventoryFocus()
    {
        var n = _pending.Count;
        if (n == 0) { _gpInventory = false; return; }
        _invFocus = System.Math.Clamp(_invFocus, 0, n - 1);

        if (Context.Input.Nav(NavDir.Left) && _invFocus % InvCols > 0) _invFocus--;
        if (Context.Input.Nav(NavDir.Right) && _invFocus % InvCols < InvCols - 1 && _invFocus + 1 < n) _invFocus++;
        if (Context.Input.Nav(NavDir.Up) && _invFocus - InvCols >= 0) _invFocus -= InvCols;
        if (Context.Input.Nav(NavDir.Down) && _invFocus + InvCols < n) _invFocus += InvCols;

        // X : fusionner le portrait focus s'il a FusionSize exemplaires en réserve (raccourci manette).
        if (Context.Input.WasTertiaryPressed && CanFuseFromReserve(_pending[_invFocus]))
        {
            OpenFusionFromReserve(_pending[_invFocus]);
            return;
        }

        if (Context.Input.WasConfirmPressed)
        {
            _dragSpec = _pending[_invFocus];   // prise en main (comme la prise depuis l'inventaire à la souris)
            _pending.RemoveAt(_invFocus);
            _dragFrom = null;
            _gpInventory = false;
            Context.Sounds.Play("unit_pick");
        }
        else if (Context.Input.WasCancelPressed || Context.Input.WasLeftShoulderPressed)
        {
            _gpInventory = false;   // LB (ou B) : inventaire → terrain
        }
    }

    /// <summary>Saisit l'unité joueur sous le curseur (retirée du plateau en attendant la pose).</summary>
    private void PickUpAt(Cell cell)
    {
        if (_tutorial != null)   // tuto : pas de reprise de pion sur le plateau (commandant figé, soldat une fois posé)
            return;
        if (_match.UnitAt(cell) is { Faction: Faction.Player } unit
            && _playerSpec.TryGetValue(unit, out var spec))
        {
            _dragSpec = spec;
            _dragFrom = cell;
            _match.Remove(cell);
            _playerSpec.Remove(unit);
            Context.Sounds.Play("unit_pick");
        }
    }

    /// <summary>Déplace le curseur de case à la croix directionnelle (borné au plateau).</summary>
    private void MoveCursor()
    {
        if (Context.Input.Nav(NavDir.Up)) _cursor = new Cell(_cursor.Column, System.Math.Max(0, _cursor.Row - 1));
        if (Context.Input.Nav(NavDir.Down)) _cursor = new Cell(_cursor.Column, System.Math.Min(Rows - 1, _cursor.Row + 1));
        if (Context.Input.Nav(NavDir.Left)) _cursor = new Cell(System.Math.Max(0, _cursor.Column - 1), _cursor.Row);
        if (Context.Input.Nav(NavDir.Right)) _cursor = new Cell(System.Math.Min(Columns - 1, _cursor.Column + 1), _cursor.Row);
    }

    private void BeginDrag(Point mouse)
    {
        // 0. Prise de la PILE de fusion ENTIÈRE (réserve ou plateau) : on attrape les 2 pièces d'un coup
        //    et on porte la pile (déplaçable). Le lâcher la réancre (cf. DropCarriedPile).
        if (FusionStacking && !_carryPile
            && ((FusionInReserve && FusionStackCardRect().Contains(mouse))
                || (!FusionInReserve && CellUnderMouse() == _fusionCell)))
        {
            GrabPile();
            return;
        }

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
        //    Bloqué en tuto : le commandant reste figé et le soldat ne se reprend pas une fois posé.
        if (_tutorial == null
            && CellUnderMouse() is { } cell
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

    private void EndDrag(Point mouse) => EndDragAt(CellUnderMouse(), IsOverPanel(mouse));

    /// <summary>Dépose le pion porté sur <paramref name="cell"/> (ou à l'inventaire si
    /// <paramref name="overPanel"/>). Logique partagée souris (glisser) et manette (A).</summary>
    private void EndDragAt(Cell? cell, bool overPanel)
    {
        var spec = _dragSpec!;

        // Lâcher sur la réserve : tenter d'empiler sur une pièce identique (fusion) avant tout le reste.
        if (overPanel && TryStackOnReserve(spec, Context.Input.MousePosition))
        {
            _dragSpec = null;
            _dragFrom = null;
            return;
        }

        // Lâcher sur le plateau : tenter d'empiler sur une case (pile ou unité identique).
        if (cell is { } sc && TryStackOnBoard(spec, sc))
        {
            _dragSpec = null;
            _dragFrom = null;
            return;
        }

        // La case d'une pile de plateau n'accepte rien d'autre (ni pose ni échange) : drop invalide.
        if (cell is { } pc && _fusionCell == pc)
        {
            if (_dragFrom is { } from && _match.UnitAt(from) == null)
                PlacePlayer(spec, from);
            else
                _pending.Add(spec);
            _dragSpec = null;
            _dragFrom = null;
            return;
        }

        if (cell is { } c && IsPlayerZone(c) && _match.UnitAt(c) == null
            && !_battlefield[c].BlocksMovement
            // Repositionner un pion déjà posé est toujours permis ; poser une NOUVELLE unité (venue de
            // l'inventaire) seulement si le plafond n'est pas atteint (sinon elle retourne à l'inventaire).
            && (_dragFrom != null || _playerSpec.Count < MaxDeployed))
        {
            PlacePlayer(spec, c);                       // pose / repositionne
            Context.Sounds.Play("unit_place");
        }
        else if (cell is { } c2 && IsPlayerZone(c2) && !_battlefield[c2].BlocksMovement
            && _match.UnitAt(c2) is { Faction: Faction.Player } occupant
            && (!occupant.IsEssential || _dragFrom is not null)   // commandant : échange OK depuis le plateau, jamais depuis l'inventaire
            && _playerSpec.TryGetValue(occupant, out var occSpec))
        {
            // Case occupée par une de nos unités : on intervertit les deux pièces.
            _match.Remove(c2);
            _playerSpec.Remove(occupant);
            PlacePlayer(spec, c2);                      // la pièce portée prend la place
            if (_dragFrom is { } src)
                PlacePlayer(occSpec, src);              // l'occupant rejoint la case d'origine
            else
                _pending.Add(occSpec);                  // pièce prise dans l'inventaire : l'occupant y retourne
            Context.Sounds.Play("unit_place");
        }
        else if (!spec.Essential && overPanel)
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

    // ─── FUSION (par empilement, réserve OU plateau) ──────────────────────────────────────────────
    // On GLISSE une pièce sur une autre identique : elles forment une PILE (sprite + compteur « N/3 » +
    // petit bouton « X »). Glisser une 3e sur la pile atteint FusionSize et ouvre la popup de choix
    // d'évolution. La pile vit dans la RÉSERVE (_fusionCell == null, rendue dans le panneau) ou sur une
    // CASE du plateau (_fusionCell == cette case, rendue sur le plateau). _fusionGroup = toutes les
    // pièces de la pile : vide = aucune ; 1..FusionSize-1 = empilement ; == FusionSize = popup. Annuler
    // remet la pièce de base à sa case (pile plateau) ou tout en réserve, et le surplus en réserve. La
    // règle métier et la mutation du roster persistant vivent dans Run.Fuse ; ici, vue + empilement.

    /// <summary>Nombre d'exemplaires de la classe de <paramref name="spec"/> présents EN RÉSERVE.</summary>
    private int PendingSameClassCount(UnitSpec spec) =>
        _pending.Count(u => Run.SameClass(u, spec));

    /// <summary>Vrai si ce portrait de réserve peut amorcer une fusion (classe non-feuille + 3 en réserve).</summary>
    private bool CanFuseFromReserve(UnitSpec spec) =>
        !spec.Essential && !spec.UnitClass.IsLeaf && PendingSameClassCount(spec) >= Run.FusionSize;

    /// <summary>Une pile de fusion est en cours d'assemblage (entre 1 et FusionSize-1 pièces).</summary>
    private bool FusionStacking => _fusionGroup.Count > 0 && _fusionGroup.Count < Run.FusionSize;

    /// <summary>Pile ancrée dans la RÉSERVE (par opposition à une pile sur le plateau).</summary>
    private bool FusionInReserve => _fusionCell is null;

    /// <summary>Slot VISUEL de la pile de RÉSERVE affichée (null si pas de pile de réserve visible).</summary>
    private int? ReservePileSlot()
    {
        if (!FusionStacking || !FusionInReserve || _carryPile)
            return null;
        var total = _pending.Count + 1;
        return System.Math.Clamp(_fusionReserveSlot, 0, total - 1);
    }

    /// <summary>Case du portrait de réserve d'indice <paramref name="i"/>, en sautant le slot de la pile.</summary>
    private Rectangle PendingCardRect(int i) =>
        ReservePileSlot() is { } p && i >= p ? PanelCardRect(i + 1) : PanelCardRect(i);

    /// <summary>Case de la carte « pile » de réserve : à son slot de formation (sinon en fin de grille).</summary>
    private Rectangle FusionStackCardRect() => PanelCardRect(ReservePileSlot() ?? _pending.Count);

    /// <summary>Petit bouton d'annulation, DANS le coin haut-droit de la pile de réserve.</summary>
    private Rectangle FusionStackCancelRect()
    {
        var c = FusionStackCardRect();
        const int s = 16;
        return new Rectangle(c.Right - s - 1, c.Y + 1, s, s);
    }

    /// <summary>Bouton d'annulation d'une pile de PLATEAU, au coin haut-droit de sa case.</summary>
    private Rectangle FusionBoardCancelRect(GridLayout layout)
    {
        var cell = _fusionCell!.Value;
        var top = layout.CellToScreen(cell.Column, cell.Row);
        const int s = 16;
        return new Rectangle((int)top.X + layout.TileSize - s - 1, (int)top.Y + 1, s, s);
    }

    /// <summary>Le bouton d'annulation de la pile courante (réserve ou plateau).</summary>
    private Rectangle FusionCancelRectActive() =>
        FusionInReserve ? FusionStackCancelRect() : FusionBoardCancelRect(BuildLayout());

    /// <summary>
    /// Tente d'empiler la pièce portée <paramref name="spec"/> sur la RÉSERVE (lâcher sur le panneau) :
    /// sur la pile de réserve en cours (même classe) ou sur un portrait de réserve identique (démarre
    /// une pile). Renvoie vrai si l'empilement a eu lieu.
    /// </summary>
    private bool TryStackOnReserve(UnitSpec spec, Point mouse)
    {
        if (spec.Essential || spec.UnitClass.IsLeaf)
            return false;

        // a) Lâcher sur la pile de RÉSERVE en cours.
        if (FusionStacking && FusionInReserve && FusionStackCardRect().Contains(mouse)
            && Run.SameClass(spec, _fusionGroup[0]))
        {
            AddToFusionStack(spec);
            return true;
        }

        // b) Lâcher sur un portrait de réserve identique → démarre une pile [cible, spec] À CE SLOT.
        if (_fusionGroup.Count == 0 && PanelCardAt(mouse) is { } j && Run.SameClass(_pending[j], spec))
        {
            var target = _pending[j];
            _fusionReserveSlot = j;       // la pile s'affichera là où on a déposé
            _pending.RemoveAt(j);
            _fusionGroup.Add(target);
            AddToFusionStack(spec);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tente d'empiler la pièce portée <paramref name="spec"/> sur le PLATEAU (lâcher sur une case) :
    /// sur la pile de plateau en cours (même classe) ou sur une unité déployée identique (démarre une
    /// pile, en retirant la pièce de base du plateau). Renvoie vrai si l'empilement a eu lieu.
    /// </summary>
    private bool TryStackOnBoard(UnitSpec spec, Cell cell)
    {
        if (spec.Essential || spec.UnitClass.IsLeaf)
            return false;

        // a) Lâcher sur la pile de PLATEAU en cours.
        if (FusionStacking && _fusionCell == cell && Run.SameClass(spec, _fusionGroup[0]))
        {
            AddToFusionStack(spec);
            return true;
        }

        // b) Lâcher sur une unité déployée identique → démarre une pile [base, spec] ancrée sur sa case.
        if (_fusionGroup.Count == 0
            && _match.UnitAt(cell) is { Faction: Faction.Player } occupant && !occupant.IsEssential
            && _playerSpec.TryGetValue(occupant, out var baseSpec) && Run.SameClass(baseSpec, spec))
        {
            _match.Remove(cell);
            _playerSpec.Remove(occupant);
            _fusionCell = cell;
            _fusionGroup.Add(baseSpec);
            AddToFusionStack(spec);
            return true;
        }

        return false;
    }

    /// <summary>Ajoute une pièce à la pile ; à FusionSize, bascule en popup de choix d'évolution.</summary>
    private void AddToFusionStack(UnitSpec spec)
    {
        _fusionGroup.Add(spec);
        _fusionPunchTimer = FusionPunchDuration;   // petit « punch scale » de la pile
        if (FusionOpen)
        {
            _fusionFocus = 0;
            Context.Sounds.Play("menu_open");   // pile complète → choix d'évolution
        }
        else
        {
            Context.Sounds.Play("unit_place");  // « clac » d'empilement (2/3)
        }
    }

    /// <summary>Attrape la pile ENTIÈRE en main (les 2 pièces) pour la déplacer (réserve ↔ plateau).</summary>
    private void GrabPile()
    {
        _carryPile = true;
        _carryPileFrom = _fusionCell;   // mémorise l'ancre (null = réserve) pour un lâcher invalide
        _fusionCell = null;
        Context.Sounds.Play("unit_pick");
    }

    /// <summary>
    /// Lâche la pile portée : sur la réserve → ancrée en réserve ; sur une case libre de la zone joueur
    /// → ancrée sur cette case (la pile se déplace) ; sinon → retour à son ancre d'origine.
    /// </summary>
    private void DropCarriedPile(Cell? cell, bool overPanel)
    {
        if (overPanel)
        {
            // Sur un portrait de réserve identique → l'absorber (peut compléter la fusion) ; sinon ancrer.
            if (PanelCardAt(Context.Input.MousePosition) is { } j && Run.SameClass(_pending[j], _fusionGroup[0]))
            {
                var target = _pending[j];
                _fusionReserveSlot = j;
                _pending.RemoveAt(j);
                _carryPile = false;
                _fusionCell = null;
                AddToFusionStack(target);
                return;
            }
            _carryPile = false;
            _fusionCell = null;                 // ancrée en réserve
            _fusionReserveSlot = _pending.Count;
            Context.Sounds.Play("unit_place");
            return;
        }

        // Sur une unité déployée identique → l'absorber (peut compléter la fusion), pile ancrée sur sa case.
        if (cell is { } cc && _match.UnitAt(cc) is { Faction: Faction.Player } occ && !occ.IsEssential
            && _playerSpec.TryGetValue(occ, out var occSpec) && Run.SameClass(occSpec, _fusionGroup[0]))
        {
            _match.Remove(cc);
            _playerSpec.Remove(occ);
            _carryPile = false;
            _fusionCell = cc;
            AddToFusionStack(occSpec);
            return;
        }

        // Sur une case libre de la zone joueur → ancrer (déplace la pile).
        if (cell is { } c && IsPlayerZone(c) && _match.UnitAt(c) == null
            && !_battlefield[c].BlocksMovement)
        {
            _carryPile = false;
            _fusionCell = c;                    // ancrée sur la case (pile déplacée)
            Context.Sounds.Play("unit_place");
            return;
        }

        _carryPile = false;
        _fusionCell = _carryPileFrom;           // lâcher invalide : retour à l'ancre d'origine
        Context.Sounds.Play("unit_pick");
    }

    /// <summary>Manette : réunit d'un coup FusionSize exemplaires de réserve et ouvre la popup.</summary>
    private void OpenFusionFromReserve(UnitSpec rep)
    {
        if (!CanFuseFromReserve(rep))
            return;
        _fusionGroup.Clear();
        _fusionCell = null;
        for (var i = _pending.Count - 1; i >= 0 && _fusionGroup.Count < Run.FusionSize; i--)
            if (Run.SameClass(_pending[i], rep))
            {
                _fusionGroup.Add(_pending[i]);
                _pending.RemoveAt(i);
            }
        if (FusionOpen)
        {
            _fusionFocus = 0;
            Context.Sounds.Play("menu_open");
        }
        else
        {
            _pending.AddRange(_fusionGroup);   // sécurité : pas assez d'exemplaires
            _fusionGroup.Clear();
        }
    }

    /// <summary>
    /// Disperse la pile en cours SANS fusionner : la pièce de base d'une pile de plateau retourne sur sa
    /// case, le surplus (et toute pile de réserve) rejoint la réserve. Vide la pile.
    /// </summary>
    private void DisbandFusionToOrigin()
    {
        if (_fusionGroup.Count > 0)
        {
            if (_fusionCell is { } cell)
            {
                PlacePlayer(_fusionGroup[0], cell);                 // la base reprend sa case
                for (var i = 1; i < _fusionGroup.Count; i++)
                    _pending.Add(_fusionGroup[i]);                  // le surplus va en réserve
            }
            else
            {
                _pending.AddRange(_fusionGroup);
            }
        }
        _fusionGroup.Clear();
        _fusionCell = null;
        _carryPile = false;
    }

    /// <summary>Annule la pile/popup : pièces rendues à leur origine (cf. <see cref="DisbandFusionToOrigin"/>).</summary>
    private void CancelFusion()
    {
        DisbandFusionToOrigin();
        Context.Sounds.Play("menu_close");
    }

    /// <summary>Valide l'évolution choisie : Run.Fuse mute le roster, l'unité évoluée prend la place de la pile.</summary>
    private void ConfirmFusion(int optionIndex)
    {
        var baseClass = _fusionGroup[0].UnitClass;
        var options = baseClass.Evolutions;
        if (optionIndex < 0 || optionIndex >= options.Count)
            return;

        var consumed = _fusionGroup.ToList();
        var cell = _fusionCell;
        var fused = _run.Fuse(consumed, options[optionIndex]);
        if (fused == null)
        {
            DisbandFusionToOrigin();            // échec inattendu : on ne perd rien
            return;
        }

        Rectangle source;   // emplacement de la pièce (point de zoom de la « caméra »)
        if (cell is { } c)
        {
            PlacePlayer(fused, c);              // pile de plateau : l'unité évoluée prend la case
            var lay = BuildLayout();
            var top = lay.CellToScreen(c.Column, c.Row);
            source = new Rectangle((int)top.X, (int)top.Y, lay.TileSize, lay.TileSize);
        }
        else
        {
            _pending.Add(fused);               // pile de réserve : va en réserve, prête à déployer
            source = PanelCardRect(_pending.Count - 1);
        }

        // Version LONGUE (grand moment) uniquement la 1re fois qu'on obtient l'unité ; sinon version courte.
        var firstTime = !Context.Saves.IsUnitDiscovered(fused.UnitClass.Asset);
        Context.Saves.DiscoverUnit(fused.UnitClass.Asset);   // méta-progression : désormais connue
        StartEvolutionAnimation(baseClass, fused.UnitClass, firstTime, source);
        _fusionGroup.Clear();
        _fusionCell = null;
    }

    /// <summary>Lance l'animation d'évolution (base → évolution), longue/dramatique ou courte.</summary>
    private void StartEvolutionAnimation(UnitClass baseClass, UnitClass evolution, bool longVersion, Rectangle source)
    {
        _evoBase = baseClass;
        _evoResult = evolution;
        _evoLong = longVersion;
        _evoSource = source;
        _evoPhase = EvoPhase.Reveal;
        _evoPhaseTimer = longVersion ? EvoRevealDuration : EvoShortDuration;
        _evoSparked = false;
    }

    /// <summary>
    /// Avance la machine à phases : Reveal (timée, gerbe au flash) → Hold (attend le CLIC du joueur)
    /// → Return (timée, la pièce revient se ranger). La version courte saute Hold/Return.
    /// </summary>
    private void UpdateEvolutionAnimation(float dt)
    {
        _sparks.Update(dt);
        switch (_evoPhase)
        {
            case EvoPhase.Reveal:
                _evoPhaseTimer -= dt;
                var dur = _evoLong ? EvoRevealDuration : EvoShortDuration;
                var pr = 1.0 - _evoPhaseTimer / dur;
                var sparkAt = _evoLong ? (double)EvoFlickerEnd : 0.2;
                if (!_evoSparked && pr >= sparkAt)
                {
                    _evoSparked = true;
                    var c = _evoLong
                        ? new Vector2(VirtualViewport.Width / 2f, VirtualViewport.Height / 2f)
                        : new Vector2(_evoSource.Center.X, _evoSource.Center.Y);
                    _sparks.EmitFirework(c, _evoLong ? 48 : 20, 1);
                    Context.Sounds.Play("recruit");
                }
                if (_evoPhaseTimer <= 0)
                {
                    if (_evoLong) _evoPhase = EvoPhase.Hold;   // attend le clic du joueur
                    else EndEvolutionAnimation();
                }
                break;

            case EvoPhase.Hold:
                // Le joueur CLIQUE (ou A / Entrée) pour ranger la pièce : feu d'artifice + retour.
                if (Context.Input.WasLeftClicked || Context.Input.WasConfirmPressed
                    || Context.Input.WasKeyPressed(Keys.Enter))
                {
                    _evoPhase = EvoPhase.Return;
                    _evoPhaseTimer = EvoReturnDuration;
                    _sparks.EmitFirework(new Vector2(VirtualViewport.Width / 2f, VirtualViewport.Height / 2f), 56, 1);
                    Context.Sounds.Play("recruit");
                }
                break;

            case EvoPhase.Return:
                _evoPhaseTimer -= dt;
                if (_evoPhaseTimer <= 0)
                    EndEvolutionAnimation();
                break;
        }
    }

    private void EndEvolutionAnimation()
    {
        _evoPhase = EvoPhase.None;
        _evoBase = null;
        _evoResult = null;
    }

    /// <summary>Choix d'évolution (souris/clavier/manette) ; B/Échap/clic droit ou bouton Annuler ferment.</summary>
    private void UpdateFusionPopup()
    {
        var count = _fusionGroup[0].UnitClass.Evolutions.Count;
        _fusionFocus = System.Math.Clamp(_fusionFocus, 0, count - 1);

        // Annulation : B (manette) ou clic droit. (Échap est géré en amont dans Update.)
        if (Context.Input.WasCancelPressed || Context.Input.WasRightClicked)
        {
            CancelFusion();
            return;
        }

        // Manette / clavier : navigation + validation sur la carte focus.
        if (Context.Input.Nav(NavDir.Left)) _fusionFocus = (_fusionFocus - 1 + count) % count;
        if (Context.Input.Nav(NavDir.Right)) _fusionFocus = (_fusionFocus + 1) % count;
        if (Context.Input.WasConfirmPressed || Context.Input.WasKeyPressed(Keys.Enter))
        {
            ConfirmFusion(_fusionFocus);
            return;
        }

        // Souris : survol = focus, clic = valide ; clic sur Annuler ferme.
        var mouse = Context.Input.MousePosition;
        for (var i = 0; i < count; i++)
        {
            if (FusionCardRect(i, count).Contains(mouse))
            {
                _fusionFocus = i;
                if (Context.Input.WasLeftClicked)
                    ConfirmFusion(i);
                return;
            }
        }
        if (Context.Input.WasLeftClicked && FusionCancelRect().Contains(mouse))
            CancelFusion();
    }

    /// <summary>Carte d'évolution n° <paramref name="index"/>, centrée sur le canvas (gabarit du draft).</summary>
    private Rectangle FusionCardRect(int index, int count)
    {
        var vp = VirtualViewport;
        return DraftCardRect(index, count, vp.Width, vp.Height);
    }

    /// <summary>Bouton « Annuler » AU-DESSUS des cartes (n'empiète pas sur les mots-clés sous les cartes).</summary>
    private Rectangle FusionCancelRect()
    {
        var vp = VirtualViewport;
        var card = DraftCardRect(0, 2, vp.Width, vp.Height);
        const int w = 180, h = 34;
        return new Rectangle((vp.Width - w) / 2, card.Y - 18 - h, w, h);
    }

    private void UpdateBattle(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;

        // Tutoriel : bouton « Passer » (toujours) / « continuer » à la victoire — peut terminer le tuto.
        if (_tutorial != null && HandleTutorialInput())
            return;

        // Glissement d'entrée : on fige le combat (pas d'interaction) le temps que le panneau sorte
        // et que le plateau finisse de se recentrer — le layout est rafraîchi à chaque frame.
        if (_battleIntroTimer > 0)
        {
            _battleIntroTimer -= dt;
            MarkLayoutDirty();
            return;
        }

        _sparks.Update(dt);        // les particules vivent leur vie même pendant le gel de l'animation
        if (_damagePopups.HasActive) // chiffres de dégâts : éclatent en feu d'artifice à l'extinction
            _damagePopups.Update(dt, BuildLayout(), _sparks);

        // Animation d'attaque en cours : on gèle entrées, IA et fin de combat le temps des FX
        // (le domaine est déjà résolu ; la fin de partie ne s'affiche qu'après la dissolution).
        if (_fx.Active)
        {
            _fx.Update(dt);
            if (_fx.HasImpacted && !_impactHandled)
                OnImpact();
            return;
        }

        if (_match.CurrentTurn == Faction.Enemy)
        {
            if (_tutorial != null)
                TutorialEnemyTurn(gameTime);   // ennemi qui AVANCE (alternance visible), jamais d'attaque
            else
                UpdateAiTurn(gameTime);
        }
        else
            UpdatePlayerTurn();

        // Tuto : dès que le soldat peut frapper l'ennemi, on passe à l'étape « attaque ».
        if (_tutorial is { Step: TutorialStep.Move }
            && _match.AttackTargets(_tutorial.PlayerSoldier).Contains(_tutorial.EnemySoldier))
            _tutorial.Advance();

        if (!_fx.Active)        // une attaque vient peut-être de lancer une animation : on attend
            CheckBattleEnd();
    }

    /// <summary>
    /// Tour de l'ennemi en TUTORIEL : il avance d'une case vers le soldat (le coup légal qui réduit le
    /// plus la distance), sans jamais attaquer. Déjà adjacent ou bloqué → il passe. Respecte `_aiTimer`
    /// pour que le déplacement soit visible (alternance des tours).
    /// </summary>
    private void TutorialEnemyTurn(GameTime gameTime)
    {
        _aiTimer -= gameTime.ElapsedGameTime.TotalSeconds;
        if (_aiTimer > 0)
            return;

        var from = _tutorial!.EnemySoldier;
        var target = _tutorial.PlayerSoldier;

        // Adjacent au soldat → l'ennemi CONTRE-ATTAQUE (anim via ResolveAttack) au lieu d'avancer.
        if (_match.AttackTargets(from).Contains(target))
        {
            ResolveAttack(from, target);
            return;
        }

        _match.LegalMoves(from, _tutorialMoves);

        var best = from;
        var bestDist = Chebyshev(from, target);
        foreach (var to in _tutorialMoves)
        {
            var d = Chebyshev(to, target);
            if (d < bestDist) { bestDist = d; best = to; }
        }

        if (best != from)
        {
            _match.TryMove(from, best);
            if (_match.UnitAt(best) is { } moved) FaceToward(moved, from, best);
            TriggerLanding(best);
            Context.Sounds.Play("unit_move");
            _tutorial.EnemySoldier = best;     // l'ennemi suit sa nouvelle case
        }
        else
        {
            _match.PassTurn();                 // adjacent ou bloqué : on rend la main au joueur
        }
    }

    private static int Chebyshev(Cell a, Cell b) =>
        System.Math.Max(System.Math.Abs(a.Column - b.Column), System.Math.Abs(a.Row - b.Row));

    /// <summary>
    /// Revue de la carte : passe en revue chaque donnée (PV → Puissance → Mouvement → Portée), une par
    /// une, ~12s chacune ; ESPACE / ENTREE / A accélère le passage. Après la dernière → étape Move.
    /// </summary>
    private void UpdateTutorialCardReview(float dt)
    {
        _tutorialCardTimer -= dt;
        var next = _tutorialCardTimer <= 0f
            || Context.Input.WasLeftClicked            // clic = passer (clavier/souris)
            || Context.Input.WasKeyPressed(Keys.Space)
            || Context.Input.WasKeyPressed(Keys.Enter)
            || Context.Input.WasConfirmPressed;        // A manette
        if (!next)
            return;

        _tutorialCardIndex++;
        if (_tutorialCardIndex >= TutorialCardStats)
            _tutorial!.Advance();                   // ReviewCard → StartCombat (on peut lancer le combat)
        else
            _tutorialCardTimer = TutorialCardSeconds;
    }

    /// <summary>
    /// Entrées propres au tutoriel : bouton « Passer » (clic ou Y, à toute étape) et « continuer »
    /// à l'écran de victoire (clic / A / Entrée, une fois l'animation finie). Renvoie vrai si le tuto
    /// vient d'être terminé (la frame doit alors s'arrêter là).
    /// </summary>
    private bool HandleTutorialInput()
    {
        var t = _tutorial!;

        // Bouton « Passer » (souris) ou X manette : termine le tuto à TOUTE étape.
        if (TutorialSkipPressed())
        {
            EndTutorial();
            return true;
        }

        // Encart commandant puis récap : avancer / terminer au clic / A / Entrée (animation finie).
        if ((t.Step == TutorialStep.Commander || t.Step == TutorialStep.Done) && !_fx.Active
            && (Context.Input.WasLeftClicked || Context.Input.WasConfirmPressed || Context.Input.WasKeyPressed(Keys.Enter)))
        {
            if (t.Step == TutorialStep.Commander) t.Advance();   // → Done
            else EndTutorial();
            return true;
        }
        return false;
    }

    /// <summary>Vrai si le joueur demande à passer le tuto : clic sur le bouton, ou X (tertiaire) manette.</summary>
    private bool TutorialSkipPressed() =>
        (TutorialSkipRect().Contains(Context.Input.MousePosition) && Context.Input.WasLeftClicked)
        || Context.Input.WasTertiaryPressed;

    /// <summary>Rectangle du bouton « Passer le tuto » (coin bas-GAUCHE, hors du panneau d'inventaire).</summary>
    private Rectangle TutorialSkipRect()
    {
        var vp = VirtualViewport;
        return new Rectangle(20, vp.Height - 60, 200, 40);
    }

    /// <summary>
    /// Overlay du tutoriel (appelé en placement ET en combat) : surbrillances pulsées selon l'étape,
    /// revue de carte (après la pose), encart commandant / récap final, et bouton « Passer ».
    /// </summary>
    private void DrawTutorialOverlay(SpriteBatch sb, GridLayout board, Viewport viewport)
    {
        var t = _tutorial!;
        var pulse = 0.5f + 0.5f * MathF.Sin(_time * 4f);
        var pcol = Palette.Yellow2 * (0.35f + 0.65f * pulse);

        // 1) Surbrillances pulsées de l'objectif courant.
        sb.Begin(samplerState: SamplerState.PointClamp);
        if (t.Step == TutorialStep.PickSoldier && _pending.Count > 0)
        {
            // Cadre englobant l'icône ET le libellé « SOLDAT » dessous (cf. DrawInventoryCard).
            var card = PanelCardRect(0);
            var box = new Rectangle(card.X - InvGapX / 2, card.Y, card.Width + InvGapX, card.Height + 12);
            DrawRectBorder(sb, Inflate(box, 3), pcol, 3);
        }
        else if (!_fx.Active && t.Step is TutorialStep.Move or TutorialStep.Attack or TutorialStep.Commander)
        {
            var cell = t.Step switch
            {
                TutorialStep.Attack    => t.EnemySoldier,
                TutorialStep.Commander => t.Commander,
                TutorialStep.Move when _match.CurrentTurn == Faction.Enemy => t.EnemySoldier,
                _                      => t.PlayerSoldier,
            };
            DrawZoneBorder(sb, board, cell, pcol, 3);
        }
        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp);

        // 2) Consigne selon l'étape : TOUJOURS une pop ancrée près de l'élément concerné (jamais de bandeau haut).
        //    L'animation d'attaque doit se TERMINER avant la pop suivante → étapes post-attaque gelées si _fx.Active.
        switch (t.Step)
        {
            case TutorialStep.Intro:
                DrawTutorialBigPanel(sb, viewport, Loc.T("tuto.intro_title"), Loc.T("tuto.intro_body"), Loc.T("tuto.intro_continue"));
                break;
            case TutorialStep.PickSoldier:
                // À côté de la carte du soldat dans l'inventaire.
                DrawAnchoredPopup(sb, PanelCardRect(0), Loc.T("tuto.pick_soldier"), null);
                break;
            case TutorialStep.PlaceSoldier:
                // Près de la zone de déploiement (bas du plateau).
                DrawPawnPopup(sb, board, new Cell(Columns / 2, Rows - 2), Loc.T("tuto.place_soldier"), null);
                break;
            case TutorialStep.ReviewCard:
                DrawTutorialCardReview(sb, viewport);
                break;
            case TutorialStep.StartCombat:
            {
                // Près du soldat posé ; touche de lancement selon le périphérique.
                var key = Context.Input.UsingGamepad ? "tuto.start_combat_gp" : "tuto.start_combat";
                DrawPawnPopup(sb, board, t.PlayerSoldier, Loc.T(key), null);
                break;
            }
            case TutorialStep.Move:
                if (_match.CurrentTurn == Faction.Enemy)
                    DrawPawnPopup(sb, board, t.EnemySoldier, Loc.T("tuto.enemy_plays"), null);
                else
                    DrawPawnPopup(sb, board, t.PlayerSoldier, Loc.T("tuto.move"), null);
                break;
            case TutorialStep.Attack:
                if (!_fx.Active)
                {
                    if (_match.CurrentTurn == Faction.Enemy)
                        DrawPawnPopup(sb, board, t.EnemySoldier, Loc.T("tuto.counter"), null);   // l'ennemi va contre-attaquer
                    else
                    {
                        // 1re attaque (ennemi intact) vs 2e attaque (ennemi blessé → prise de place).
                        var enemy = _match.UnitAt(t.EnemySoldier);
                        var damaged = enemy != null && enemy.Hp < enemy.MaxHp;
                        DrawPawnPopup(sb, board, t.EnemySoldier, Loc.T(damaged ? "tuto.attack2" : "tuto.attack"), null);
                    }
                }
                break;
            case TutorialStep.Commander:
                if (!_fx.Active)   // on laisse l'attaque se terminer avant d'afficher la pop commandant
                    DrawPawnPopup(sb, board, t.Commander, Loc.T("tuto.commander"), Loc.T("tuto.continue"));
                break;
            case TutorialStep.Done:
                if (!_fx.Active)
                    DrawTutorialPanel(sb, viewport, Loc.T("tuto.continue"), null);   // juste « clic pour commencer à jouer »
                break;
        }

        // 3) Bouton « Passer le tuto » (toujours visible) — le rappel « (X) » seulement à la manette.
        var skip = TutorialSkipRect();
        var hover = skip.Contains(Context.Input.MousePosition);
        var off = Context.Style.DrawButton(sb, skip, UiStyle.StateOf(hover, hover && Context.Input.IsLeftDown));
        var label = Loc.T("tuto.skip") + (Context.Input.UsingGamepad ? " (X)" : "");
        Context.Font.DrawCentered(sb, label,
            new Rectangle(skip.X, skip.Y + off, skip.Width, skip.Height), 1, Palette.White);

        sb.End();
    }

    /// <summary>
    /// Revue de carte (dès la pose) : la carte du Soldat, la donnée COURANTE encadrée (cadre pulsé), et
    /// une BULLE pop juste à côté qui l'explique (PV → Puissance → Mouvement → Portée), progression n/4.
    /// </summary>
    private void DrawTutorialCardReview(SpriteBatch sb, Viewport viewport)
    {
        var soldier = Domaines.Pion.BaseClass;
        // La carte du pion qu'on vient de poser, à sa place d'aperçu habituelle (à gauche du panneau).
        var cardRect = new Rectangle(PanelRect().X - CombatCardGap - CombatCardW,
            (viewport.Height - CombatCardH) / 2, CombatCardW, CombatCardH);
        DrawCardLayout(sb, cardRect, soldier, Faction.Player, Domaine.Pion, soldier.MaxHp, soldier.MaxHp);

        // Ordre haut→bas sur la carte : icône de DÉPLACEMENT (domaine), PV, Puissance, Mouvement, Portée.
        string[] keys = { "tuto.card_domaine", "tuto.card_hp", "tuto.card_power", "tuto.card_move", "tuto.card_range" };
        var idx = System.Math.Clamp(_tutorialCardIndex, 0, keys.Length - 1);

        // Cadre PULSÉ autour de la donnée en cours, sur la carte.
        var statRect = TutorialCardStatRect(cardRect, idx);
        var pulse = 0.5f + 0.5f * MathF.Sin(_time * 4f);
        DrawRectBorder(sb, Inflate(statRect, 2), Palette.Yellow2 * (0.5f + 0.5f * pulse), 3);

        // Bulle « pop » à GAUCHE de la carte (espace libre de ce côté), alignée sur la donnée encadrée.
        const int pad = 14;
        const int bw = 360;
        var lines = WrapText(Loc.T(keys[idx]), bw - 2 * pad, 1);
        var bh = pad + 14 + lines.Count * 12 + 16 + pad;
        var by = System.Math.Clamp(statRect.Y + statRect.Height / 2 - bh / 2, 20, viewport.Height - bh - 20);
        var bubble = new Rectangle(cardRect.X - 28 - bw, by, bw, bh);
        Context.Style.DrawPanel(sb, bubble);

        Context.Font.Draw(sb, $"{idx + 1}/{keys.Length}", new Vector2(bubble.X + pad, bubble.Y + pad), 1, Palette.Cyan1);
        var ty = bubble.Y + pad + 14;
        foreach (var line in lines)
        {
            Context.Font.Draw(sb, line, new Vector2(bubble.X + pad, ty), 1, Palette.White);
            ty += 12;
        }
        var contKey = Context.Input.UsingGamepad ? "tuto.card_continue_gp" : "tuto.card_continue";
        Context.Font.DrawCentered(sb, Loc.T(contKey),
            new Rectangle(bubble.X, bubble.Bottom - 16, bubble.Width, 10), 1, Palette.Cyan1);
    }

    /// <summary>
    /// Rectangle d'une donnée de la carte (0=icône domaine/déplacement, 1=PV, 2=Puissance, 3=Mouvement, 4=Portée),
    /// positions calquées sur <see cref="DrawCardLayout"/> (titre 22, sprite 64+6, domaine 39+10, barre PV 14+2, texte 14, 3 lignes de 36).
    /// </summary>
    private Rectangle TutorialCardStatRect(Rectangle card, int index)
    {
        var y0 = card.Y + CardPad;
        var inner = card.Width - 2 * CardPad;
        return index switch
        {
            0 => new Rectangle(card.X + (card.Width - 39) / 2, y0 + 92, 39, 39),  // icône domaine (déplacement)
            1 => new Rectangle(card.X + CardPad, y0 + 141, inner, 30),            // PV (barre + texte pv/max)
            2 => new Rectangle(card.X + CardPad, y0 + 171, inner, 32),            // Puissance
            3 => new Rectangle(card.X + CardPad, y0 + 207, inner, 32),            // Mouvement
            _ => new Rectangle(card.X + CardPad, y0 + 243, inner, 32),            // Portée
        };
    }

    /// <summary>Bulle d'aide ancrée à la CASE d'un pion (cf. <see cref="DrawAnchoredPopup"/>).</summary>
    private void DrawPawnPopup(SpriteBatch sb, GridLayout board, Cell cell, string text, string? footer)
    {
        var size = board.TileSize;
        var top = board.CellToScreen(cell.Column, cell.Row);
        DrawAnchoredPopup(sb, new Rectangle((int)top.X, (int)top.Y, size, size), text, footer);
    }

    /// <summary>
    /// Bulle d'aide ANCRÉE à un élément (rectangle écran) : à droite de l'élément, bascule à gauche si
    /// elle déborde, clampée à l'écran. Texte replié + bas de page facultatif (invite à continuer).
    /// </summary>
    private void DrawAnchoredPopup(SpriteBatch sb, Rectangle anchor, string text, string? footer)
    {
        var vp = VirtualViewport;
        const int pad = 12;
        const int bw = 340;
        var lines = WrapText(text, bw - 2 * pad, 1);
        var bh = pad + lines.Count * 12 + (footer != null ? 14 : 0) + pad;

        var bx = anchor.Right + 14;                  // à droite de l'élément
        if (bx + bw > vp.Width - 20)
            bx = anchor.X - 14 - bw;                 // déborde : bascule à gauche
        bx = System.Math.Clamp(bx, 20, vp.Width - bw - 20);
        var by = System.Math.Clamp(anchor.Y + anchor.Height / 2 - bh / 2, 20, vp.Height - bh - 20);

        var bubble = new Rectangle(bx, by, bw, bh);
        Context.Style.DrawPanel(sb, bubble);
        var ty = bubble.Y + pad;
        foreach (var line in lines)
        {
            Context.Font.Draw(sb, line, new Vector2(bubble.X + pad, ty), 1, Palette.Yellow2);
            ty += 12;
        }
        if (footer != null)
            Context.Font.DrawCentered(sb, footer,
                new Rectangle(bubble.X, bubble.Bottom - 14, bubble.Width, 10), 1, Palette.Cyan1);
    }

    /// <summary>Grand encart central : TITRE (échelle 3) + corps replié (échelle 2) + invite (bas).</summary>
    private void DrawTutorialBigPanel(SpriteBatch sb, Viewport viewport, string title, string body, string footer)
    {
        var pw = System.Math.Min(viewport.Width - 120, 700);
        var lines = WrapText(body, pw - 48, 2);
        var ph = 20 + 28 + 14 + lines.Count * 18 + 24 + 16;
        var box = new Rectangle((viewport.Width - pw) / 2, (viewport.Height - ph) / 2, pw, ph);
        Context.Style.DrawPanel(sb, box);

        Context.Font.DrawCentered(sb, title, new Rectangle(box.X, box.Y + 20, box.Width, 24), 3, Palette.Yellow2);
        var y = box.Y + 20 + 28 + 14;
        foreach (var line in lines)
        {
            Context.Font.DrawCentered(sb, line, new Rectangle(box.X, y, box.Width, 16), 2, Palette.White);
            y += 18;
        }
        Context.Font.DrawCentered(sb, footer, new Rectangle(box.X, box.Bottom - 22, box.Width, 12), 1, Palette.Cyan1);
    }

    /// <summary>Encart central : corps (texte replié, échelle 2) + bas de page facultatif (invite).</summary>
    private void DrawTutorialPanel(SpriteBatch sb, Viewport viewport, string body, string? footer)
    {
        var pw = System.Math.Min(viewport.Width - 120, 620);
        var lines = WrapText(body, pw - 48, 2);
        var ph = 24 + lines.Count * 18 + (footer != null ? 26 : 0) + 16;
        var box = new Rectangle((viewport.Width - pw) / 2, (viewport.Height - ph) / 2, pw, ph);
        Context.Style.DrawPanel(sb, box);

        var y = box.Y + 20;
        foreach (var line in lines)
        {
            Context.Font.DrawCentered(sb, line, new Rectangle(box.X, y, box.Width, 16), 2, Palette.Yellow2);
            y += 18;
        }
        if (footer != null)
            Context.Font.DrawCentered(sb, footer, new Rectangle(box.X, box.Bottom - 22, box.Width, 12), 1, Palette.Cyan1);
    }

    private void CheckBattleEnd()
    {
        if (_tutorial != null)   // en tuto : pas de recrutement/défaite/sauvegarde — géré par le guide
            return;
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
            _defeatReason = CommanderAlive() ? Loc.T("defeat.army_destroyed") : Loc.T("defeat.commander_fallen");
            _run.Defeat();
        }

        ClearSelection();
        _recruitFocus = 0;   // focus manette sur la première carte du draft

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
        // Manette : curseur de case, A agit (sélectionne / déplace / attaque), B désélectionne.
        if (Context.Input.UsingGamepad)
        {
            MoveCursor();
            if (Context.Input.WasConfirmPressed) { CombatActAt(_cursor); return; }
            if (Context.Input.WasCancelPressed && _selected is not null)
            {
                ClearSelection();
                Context.Sounds.Play("unit_deselect");
                return;
            }
        }

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
    /// Action de combat à la manette sur <paramref name="cell"/> (clic-pour-agir, sans glisser) :
    /// attaque une cible à portée, sinon déplacement légal, sinon (dé)sélection d'un pion joueur.
    /// </summary>
    private void CombatActAt(Cell cell)
    {
        if (_selected is { } sel && _attackTargets.Contains(cell))
        {
            ResolveAttack(sel, cell);
            EndPlayerAction();
            return;
        }
        if (_selected is { } sel2 && _legalMoves.Contains(cell))
        {
            _match.TryMove(sel2, cell);
            if (_match.UnitAt(cell) is { } moved) FaceToward(moved, sel2, cell);
            TriggerLanding(cell);
            Context.Sounds.Play("unit_move");
            TutorialOnPlayerMove(cell);
            EndPlayerAction();
            return;
        }

        if (_match.UnitAt(cell) is { Faction: Faction.Player } && (_tutorial is null || _tutorial.CanSelectInCombat(cell)))
        {
            _selected = cell;
            _match.LegalMoves(cell, _legalMoves);
            _match.AttackTargets(cell, _attackTargets);
            _match.ThreatenedCells(cell, _attackReach);
            Context.Sounds.Play("unit_select");
        }
        else
        {
            if (_selected is not null) Context.Sounds.Play("unit_deselect");
            ClearSelection();
        }
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
            TutorialOnPlayerMove(cell);
            EndPlayerAction();
            return;
        }

        var unit = _match.UnitAt(cell);
        if (unit is { Faction: Faction.Player } && (_tutorial is null || _tutorial.CanSelectInCombat(cell)))
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
            TutorialOnPlayerMove(cell);
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
            _aiTimer = _tutorial != null ? TutorialEnemyDelay : AiDelaySeconds;
    }

    /// <summary>En tuto : le soldat déplacé est suivi (l'avancement Move→Attack se fait via AttackTargets).</summary>
    private void TutorialOnPlayerMove(Cell to)
    {
        if (_tutorial != null)
            _tutorial.PlayerSoldier = to;
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

    private void UpdateRecruitment(GameTime gameTime)
    {
        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _sparks.Update(dt);

        // Vol en cours : le pion choisi rejoint le panneau d'inventaire, puis on recrute et on place.
        if (_recruitChoice is { } choice)
        {
            _recruitHold -= dt;
            if (_recruitHold <= 0f)
            {
                _run.Recruit(choice);    // BeginPlacement remet _recruitChoice à null
                BeginPlacement();
            }
            return;
        }

        var viewport = VirtualViewport;
        var availW = viewport.Width - RightPanelWidth;   // cartes centrées à GAUCHE du panneau
        var count = _run.Draft.Count;
        if (count == 0)
            return;
        _recruitFocus = System.Math.Clamp(_recruitFocus, 0, count - 1);

        // Manette : navigation gauche/droite (cyclique) + validation sur la carte focus.
        if (Context.Input.Nav(NavDir.Left)) _recruitFocus = (_recruitFocus - 1 + count) % count;
        if (Context.Input.Nav(NavDir.Right)) _recruitFocus = (_recruitFocus + 1) % count;
        if (Context.Input.WasConfirmPressed) { SelectRecruit(_recruitFocus, availW, viewport.Height); return; }

        // Souris : le survol fixe le focus, le clic sélectionne.
        var mouse = Context.Input.MousePosition;
        for (var i = 0; i < count; i++)
        {
            if (DraftCardRect(i, count, availW, viewport.Height).Contains(mouse))
            {
                _recruitFocus = i;
                if (Context.Input.WasLeftClicked) SelectRecruit(i, availW, viewport.Height);
                return;
            }
        }
    }

    /// <summary>Lance le vol de recrutement de la carte <paramref name="index"/> vers l'inventaire.</summary>
    private void SelectRecruit(int index, int availW, int vpH)
    {
        var rect = DraftCardRect(index, _run.Draft.Count, availW, vpH);
        _recruitChoice = _run.Draft[index];
        _recruitHold = RecruitFlightDuration;
        // Départ du vol = centre du sprite de la carte (cf. disposition dans DrawCardLayout).
        _recruitFrom = new Vector2(rect.X + rect.Width / 2f, rect.Y + CardPad + 22 + 32);
        Context.Sounds.Play("recruit");
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
        // Dégâts EFFECTIFS à afficher (traits inclus : Rempart, Rage…), bornés aux PV de la cible.
        _pendingDamage = attacker != null && victim != null ? _match.PreviewDamage(from, target) : 0;
        if (attacker != null)
            FaceToward(attacker, from, target);     // tourne l'attaquant vers sa cible (avant la capture du sprite)
        var attackerSprite = attacker != null ? UnitSprite(attacker) : null;
        var victimSprite = victim != null ? UnitSprite(victim) : null;

        var kind = _match.TryAttack(from, target);
        if (kind == MoveKind.Invalid)
            return kind;

        RecordIfEnemyKilled(victim);

        var killed = kind == MoveKind.Killed;
        if (_tutorial is { Step: TutorialStep.Attack } && killed && victim is { Faction: Faction.Enemy })
            _tutorial.Advance();            // mort de l'ENNEMI → Attack → Commander (pas sur la contre-attaque)
        var advanced = killed && ReferenceEquals(_match.UnitAt(target), attacker);
        var attackerCell = advanced ? target : from;
        var style = attacker != null ? AttackStyleFor(attacker) : AttackStyle.Lunge;
        // Son selon le style : incantation pour le mage, charge pour le cavalier, coup d'arme sinon.
        Context.Sounds.Play(style switch
        {
            AttackStyle.Cast  => "unit_cast",
            AttackStyle.Leap  => "unit_charge",
            AttackStyle.Shoot => "unit_shoot",
            _                 => "unit_attack",
        });
        _fx.Begin(from, target, attackerCell, attackerSprite, victimSprite, killed, advanced, style);
        _impactHandled = false;     // le chiffre de dégâts sera lancé au contact (cf. UpdateBattle)

        return kind;
    }

    /// <summary>Style d'animation d'attaque selon l'unité : cavalier = charge sautée, mage = projectile, autres = fente.</summary>
    private static AttackStyle AttackStyleFor(Unit unit) => unit.Domaine switch
    {
        Domaine.Cavalier => AttackStyle.Leap,
        Domaine.Fou      => AttackStyle.Cast,
        Domaine.Dame     => AttackStyle.Shoot,   // archer (Archer / Rôdeur / Maître archer)
        _                => AttackStyle.Lunge,
    };

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
        if (_showGrid && BoardAssembled && _run.Phase is RunPhase.Placement or RunPhase.Battle)
            DrawBoardGrid(sb, board, Palette.Black1);   // quadrillage permanent NOIR opaque (bascule F1/Select) — masqué pendant l'émergence
        sb.End();

        // Passe d'ombres projetées (sur le terrain, sous les unités) — batchs cisaillés dédiés.
        if (_run.Phase is RunPhase.Placement or RunPhase.Battle)
            DrawCastShadows(sb, board);

        switch (_run.Phase)
        {
            case RunPhase.Placement:
                sb.Begin(samplerState: SamplerState.PointClamp);
                if (BoardAssembled) DrawDeploymentZone(sb, board);
                if (BoardAssembled) DrawEnemyThreat(sb, board);
                DrawUnits(sb, board);
                DrawFusionBoardStack(sb, board);         // pile de fusion ancrée sur une case
                DrawCarriedAtCursor(sb, board);          // pion porté AU-DESSUS des pièces
                if (BoardAssembled)
                    DrawGamepadPlacementCursor(sb, board);   // curseur (coins) AU-DESSUS, toujours visible
                DrawPanelBackground(sb);
                DrawPlacementPanel(sb);
                DrawInventoryFocusHighlight(sb);
                DrawPlacementPreview(sb);
                DrawDragGhost(sb);
                DrawCarriedPile(sb, board);              // pile portée, suit la souris/curseur
                sb.End();

                if (_tutorial != null)
                    DrawTutorialOverlay(sb, board, viewport);
                if (FusionOpen)
                    DrawFusionPopup(sb, viewport);   // modale par-dessus le placement
                if (EvoPlaying)
                    DrawEvolutionAnimation(sb, viewport);   // morph base → évolution
                else if (_sparks.HasActive)
                    _sparks.Draw(sb, Context.Pixel);        // fin du feu d'artifice (pièce rangée)
                break;
            case RunPhase.Battle:
                sb.Begin(samplerState: SamplerState.PointClamp);
                DrawHighlights(sb, board);
                DrawEnemyThreat(sb, board);
                DrawUnits(sb, board);
                DrawCarriedUnit(sb, board);
                DrawGamepadBattleCursor(sb, board);      // curseur (coins) AU-DESSUS, toujours visible
                sb.End();

                if (_fx.Active)             // dissolution / attaquant animé / flash : passes dédiées
                    DrawCombatFx(sb, board);

                _sparks.Draw(sb, Context.Pixel);   // étincelles d'impact, au-dessus de tout le plateau
                _damagePopups.Draw(sb, Context.Font, board);   // chiffres de dégâts, par-dessus

                if (_battleIntroTimer > 0)
                    DrawSlidingPanel(sb);          // panneau de placement qui sort par la droite
                else
                {
                    sb.Begin(samplerState: SamplerState.PointClamp);
                    DrawCombatCards(sb, layout);
                    sb.End();
                }

                if (_tutorial != null)
                    DrawTutorialOverlay(sb, board, viewport);
                break;
            case RunPhase.Recruitment:
                sb.Begin(samplerState: SamplerState.PointClamp);
                DrawUnits(sb, board);
                DrawDim(sb, viewport);
                sb.End();
                DrawRecruitment(sb, viewport);     // gère son propre batch (panneau + cartes + vol)
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

        // Légende des commandes (haut-gauche) pendant placement / combat.
        if (_run.Phase is RunPhase.Placement or RunPhase.Battle && !_showTrees && !_pauseMenu.IsOpen)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            DrawControlsLegend(sb, viewport);
            sb.End();
        }

        if (_showTrees)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            _treeRenderer.Draw(sb, viewport.Width, viewport.Height);
            sb.End();
        }
        else if (_pauseMenu.IsOpen)
        {
            // En manette : pointeur synthétique = centre de l'élément focus → réutilise la surbrillance
            // de survol existante. En souris : vraie position.
            var gp = Context.Input.UsingGamepad;
            var focusRect = _pauseMenu.FocusedRect(viewport.Width, viewport.Height);
            var pointer = gp ? focusRect.Center.ToVector2() : Context.Input.MousePosition.ToVector2();
            sb.Begin(samplerState: SamplerState.PointClamp);
            _pauseRenderer.Draw(sb, _pauseMenu, viewport.Width, viewport.Height,
                pointer, gp ? false : Context.Input.IsLeftDown, gp ? focusRect : null);
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
        // ...mais pas pendant l'émergence des tuiles (sinon l'ombre du plateau « complet » est là
        // avant que les tuiles ne soient sorties de l'eau → bizarre).
        var board = BoardRect(layout);
        if (BoardAssembled && board.X >= 0 && board.Y >= 0 && board.Right <= w && board.Bottom <= h)
            _water.DrawShadow(sb, board, w, h);   // ombre statique mise en cache (cf. WaterRenderer)
    }

    /// <summary>Rectangle (en coordonnées canvas) couvert par le plateau, épaisseur des sprites comprise.</summary>
    private Rectangle BoardRect(GridLayout layout)
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
        if (FusionOpen || (EvoPlaying && _evoLong))
            return Palette.Black1 * 0.62f; // popup de fusion / morph d'évolution long (placement) : = DrawDim
        return _run.Phase is RunPhase.Recruitment or RunPhase.Victory or RunPhase.Defeat
            ? Palette.Black1 * 0.62f       // = DrawDim
            : null;
    }

    /// <summary>Vrai quand l'animation d'assemblage du plateau est finie (toutes les tuiles en place).</summary>
    private bool BoardAssembled => _boardIntro >= _boardIntroTotal;

    /// <summary>
    /// État d'émergence d'une case : décalage vertical (px) + opacité. La tuile démarre un peu plus
    /// bas et TRANSPARENTE, puis remonte en se révélant (fondu) → impression de sortir de l'eau (qui
    /// se voit derrière le plateau). Décalée par son indice (cascade). (0, 1) une fois posée.
    /// Appliqué aussi aux ombres et aux pions pour qu'ils émergent avec leur tuile.
    /// </summary>
    private (int OffsetY, float Alpha) BoardIntroAnim(Cell cell, GridLayout layout)
    {
        if (_boardIntro >= _boardIntroTotal)
            return (0, 1f);
        var index = cell.Row * Columns + cell.Column;
        var t = MathHelper.Clamp((_boardIntro - index * BoardIntroStagger) / BoardIntroRise, 0f, 1f);
        var eased = 1f - (1f - t) * (1f - t) * (1f - t);   // easeOutCubic
        return ((int)((1f - eased) * layout.SpriteHeight * BoardIntroDrop), eased);
    }

    private void DrawTerrain(SpriteBatch sb, GridLayout layout)
    {
        // Arrière → avant (Cells() parcourt rangée 0 → N) pour que l'épaisseur se recouvre bien.
        foreach (var cell in _battlefield.Cells())
        {
            var (tex, src) = TileSprite(_battlefield[cell].Id);
            var rect = layout.CellToSpriteRect(cell.Column, cell.Row);
            var (oy, a) = BoardIntroAnim(cell, layout);
            rect.Y += oy;
            sb.Draw(tex, rect, src, Color.White * a);
        }
    }

    private void DrawDeploymentZone(SpriteBatch sb, GridLayout layout)
    {
        // Zone de déploiement : fond bleu TRANSPARENT (comme avant) + traits en BLEU (camp joueur) à la
        // même épaisseur que la grille noire (1 pixel d'art).
        var thick = System.Math.Max(1, layout.TileSize / GridLayout.DefaultTileSize);
        foreach (var cell in PlayerDeployCells())
        {
            DrawZone(sb, layout, cell, Palette.Cyan1 * 0.38f);
            DrawZoneBorder(sb, layout, cell, Palette.Navy1, thick);   // traits bleu foncé
        }
    }

    /// <summary>
    /// Légende des commandes en haut à gauche (petit panneau) : bascule grille + zones de danger.
    /// Les touches affichées correspondent au PÉRIPHÉRIQUE actif (clavier/souris vs manette).
    /// </summary>
    private void DrawControlsLegend(SpriteBatch sb, Viewport viewport)
    {
        var gp = Context.Input.UsingGamepad;
        var lines = new[]
        {
            $"{(gp ? "SELECT" : "F1")} : {Loc.T("hud.toggle_grid")}",
            $"{(gp ? "RT" : "ESPACE")} : {Loc.T("hud.danger_zones")}",
        };

        const int pad = 10, lineH = 11;
        var w = 0;
        foreach (var line in lines)
            w = System.Math.Max(w, Context.Font.Measure(line, 1));
        var box = new Rectangle(12, 12, w + 2 * pad, pad + lines.Length * lineH + pad - 2);
        Context.Style.DrawPanel(sb, box);

        var y = box.Y + pad;
        foreach (var line in lines)
        {
            Context.Font.Draw(sb, line, new Vector2(box.X + pad, y), 1, Palette.White);
            y += lineH;
        }
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

        // Quadrillage de la portée PAR-DESSUS les remplissages (contour par case) : déplacement/attaque.
        foreach (var cell in reach)
            DrawZoneBorder(sb, layout, cell, Palette.Purple5 * 0.45f, 1);
        foreach (var cell in moves)
            DrawZoneBorder(sb, layout, cell, Palette.Yellow2 * 0.7f, 1);
        foreach (var cell in targets)
            DrawZoneBorder(sb, layout, cell, Palette.Purple5 * 0.9f, 1);
    }

    /// <summary>
    /// Au survol d'une unité ENNEMIE, prévisualise sa portée d'attaque : les cases qu'elle menace
    /// sont teintées en rouge, l'ennemi survolé est cerclé. Au MAINTIEN d'Espace, on affiche d'un
    /// coup les cases menacées par TOUS les ennemis (zones de danger globales). Aide à anticiper.
    /// </summary>
    private void DrawEnemyThreat(SpriteBatch sb, GridLayout layout)
    {
        // Espace (clavier) ou gâchette droite (manette) maintenu : toutes les zones de danger.
        if (Context.Input.IsKeyDown(Keys.Space) || Context.Input.IsRightTriggerDown)
        {
            foreach (var (cell, unit) in _match.Units())
            {
                if (unit.Faction != Faction.Enemy)
                    continue;
                _match.ThreatenedCells(cell, _threatCells);
                foreach (var threat in _threatCells)
                    DrawZone(sb, layout, threat, Palette.Purple5 * 0.30f);
            }
            if (!_showGrid)   // si le quadrillage permanent est déjà là, pas besoin de le redessiner
                DrawBoardGrid(sb, layout, Palette.Black1);   // + grille pleine NOIR opaque sur toute la map
            return;
        }

        // Case survolée : curseur en manette, souris sinon.
        var probe = Context.Input.UsingGamepad ? (Cell?)_cursor : CellUnderMouse();
        if (probe is not { } hovered || _match.UnitAt(hovered) is not { Faction: Faction.Enemy })
            return;

        _match.ThreatenedCells(hovered, _threatCells);  // buffer réutilisé (pas d'allocation par frame)
        foreach (var threat in _threatCells)
            DrawZone(sb, layout, threat, Palette.Purple5 * 0.30f);
        foreach (var threat in _threatCells)               // quadrillage de la portée de l'ennemi survolé
            DrawZoneBorder(sb, layout, threat, Palette.Purple5 * 0.6f, 1);
        DrawZoneBorder(sb, layout, hovered, Palette.Purple5, 2);
    }

    /// <summary>Quadrillage (lignes) sur TOUT le plateau : lignes verticales + horizontales aux frontières de cases.</summary>
    private void DrawBoardGrid(SpriteBatch sb, GridLayout layout, Color color)
    {
        var origin = layout.CellToScreen(0, 0);
        var size = layout.TileSize;
        // Épaisseur = 1 pixel d'art : la tuile fait DefaultTileSize px natifs, dessinée à TileSize.
        var thick = System.Math.Max(1, size / GridLayout.DefaultTileSize);
        int ox = (int)origin.X, oy = (int)origin.Y, w = Columns * size, h = Rows * size;
        for (var i = 0; i <= Columns; i++)
            DrawRect(sb, new Rectangle(ox + i * size, oy, thick, h), color);   // lignes verticales
        for (var j = 0; j <= Rows; j++)
            DrawRect(sb, new Rectangle(ox, oy + j * size, w, thick), color);   // lignes horizontales
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
        var (introY, introA) = BoardIntroAnim(cell, layout);   // émerge avec sa tuile
        var zx = (int)top.X;
        var zy = (int)top.Y + introY;
        var zone = new Rectangle(zx, zy, size, size);

        // Liseré doré pour les unités pivots (commandant / boss).
        if (unit.IsEssential)
            DrawRectBorder(sb, zone, Palette.Yellow1 * introA, 3);

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
            sb.Draw(sprite, new Rectangle(zx, zy - spriteLift - animLift, size, size), Color.White * introA);
        }
        else
        {
            // Pas d'asset : placeholder jeton coloré + initiale de la classe.
            var token = new Rectangle(zx + 9, zy + 8 - animLift, size - 18, size - 26);
            DrawChip(sb, unit.Class, unit.Faction, token);
        }

        // Barre de vie : visible seulement quand l'unité est blessée, verticale au bord droit du pion.
        if (unit.Hp < unit.MaxHp)
            DrawUnitHpBar(sb, zx, zy - animLift, size, unit.Hp, unit.MaxHp);
    }

    /// <summary>
    /// Barre de PV VERTICALE sur le bord droit d'un pion, affichée uniquement quand il est blessé.
    /// Jauge PLEINE (pas de segments → aucun trait à désaligner, nette à tous les zooms) : le rouge
    /// remplit le bas en proportion des PV restants, le vert occupe le reste (PV manquants).
    /// Dimensions proportionnelles à la case pour garder les mêmes proportions quel que soit le zoom.
    /// </summary>
    private void DrawUnitHpBar(SpriteBatch sb, int zx, int zy, int size, int hp, int maxHp)
    {
        if (maxHp <= 0)
            return;

        var barW = System.Math.Max(4, size / 11);
        var margin = System.Math.Max(3, size / 16);
        var barH = size - 2 * margin;
        var x = zx + size - barW - margin;  // collé au bord droit de la case
        var y = zy + (size - barH) / 2;     // centré verticalement sur le pion

        // Fond + cadre sombre (contraste sur tous les terrains).
        DrawRect(sb, new Rectangle(x - 1, y - 1, barW + 2, barH + 2), Palette.Black1);
        // Tout le fond = PV manquants (vert foncé) ; le rouge remplit le bas selon les PV restants.
        DrawRect(sb, new Rectangle(x, y, barW, barH), Palette.Green4);

        var fillH = (int)System.Math.Round((double)barH * hp / maxHp);
        DrawRect(sb, new Rectangle(x, y + barH - fillH, barW, fillH), Palette.Purple5);
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
            var (introY, introA) = BoardIntroAnim(cell, layout);
            DrawPieceCastShadow(sb, sprite, (int)top.X, (int)top.Y - spriteLift + introY, size, UnitLift(cell, size), introA);
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
    private void DrawPieceCastShadow(SpriteBatch sb, Texture2D sprite, int destX, int destY, int size, int lift, float fade = 1f)
    {
        var k = MathHelper.Clamp(lift / (size * CarriedLiftFraction), 0f, 1f);
        var slideX = (int)(lift * ShadowLiftSlide);          // glisse vers la lumière (droite, comme le cisaillement)
        var slideY = (int)(lift * ShadowLiftSlide * 0.35f);  // et un peu vers le bas/avant
        var alpha = ShadowAlpha * (1f - ShadowLiftFade * k) * fade;   // fade = fondu d'émergence du plateau

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

        // 2. Attaquant animé (fente/charge sautée puis avance ou recul) + ombre projetée à l'aplomb.
        if (_fx.AttackerSprite is { } attackerSprite)
        {
            var ground = _fx.AttackerTopLeft(fromTop, toTop, size);   // position au sol (sans le saut)
            var jump = (int)_fx.AttackerJumpLift(size);               // hauteur du bond (charge sautée)
            var rect = new Rectangle((int)ground.X, (int)ground.Y - jump, size, size);
            // L'ombre reste AU SOL et glisse/s'éclaircit avec le bond (cf. DrawPieceCastShadow).
            DrawPieceCastShadow(sb, attackerSprite, (int)ground.X, (int)ground.Y, size, jump);
            sb.Begin(samplerState: SamplerState.PointClamp);
            sb.Draw(attackerSprite, rect, Color.White);
            sb.End();
        }

        // 3. Réaction « touché » du survivant : flash additif par-dessus son sprite (reculé comme lui).
        if (!_fx.Killed && _fx.VictimSprite is { } hitSprite)
            _combatFx.DrawFlash(sb, hitSprite, victimRect, _fx.FlashIntensity, Palette.White, fxPixel);

        // 4. Projectile en vol vers la cible (mage : orbe ; archer : flèche) — disparaît à l'impact.
        if (_fx.ProjectileFlight is var flight && flight >= 0f)
        {
            var fromCenter = fromTop + new Vector2(size / 2f, size / 2f);
            var toCenter = toTop + new Vector2(size / 2f, size / 2f);
            if (_fx.Style == AttackStyle.Shoot)
                DrawArrow(sb, fromCenter, toCenter, flight, size);
            else
                DrawMagicBolt(sb, fromCenter, toCenter, flight, size);
        }
    }

    /// <summary>
    /// Flèche pixel-art : une traînée de blocs « bois » alignés sur la direction du tir, terminée par
    /// une pointe claire. Blocs calés sur leur grille (pixel-perfect, comme les étincelles).
    /// </summary>
    private void DrawArrow(SpriteBatch sb, Vector2 from, Vector2 to, float flight, int size)
    {
        var dir = to - from;
        if (dir.LengthSquared() > 0.0001f)
            dir.Normalize();
        var head = Vector2.Lerp(from, to, flight);
        var block = System.Math.Max(2, size / 14);

        sb.Begin(samplerState: SamplerState.PointClamp);
        for (var i = 4; i >= 1; i--)                            // fût : blocs en arrière de la pointe
            DrawBlockSnapped(sb, head - dir * (block * i), block, Palette.Brown1);
        DrawBlockSnapped(sb, head, block, Palette.Brown4);      // pointe (fer clair)
        sb.End();
    }

    /// <summary>Carré plein de côté <paramref name="s"/> centré sur <paramref name="c"/>, calé sur la grille de blocs.</summary>
    private void DrawBlockSnapped(SpriteBatch sb, Vector2 c, int s, Color col)
    {
        var x = (int)System.MathF.Round(c.X / s) * s;
        var y = (int)System.MathF.Round(c.Y / s) * s;
        DrawRect(sb, new Rectangle(x, y, s, s), col);
    }

    /// <summary>
    /// Projectile magique pixel-art : une orbe (halo cyan + cœur clair) qui file de <paramref name="from"/>
    /// à <paramref name="to"/>, traînée de 2 orbes plus pâles derrière. Tailles proportionnelles à la case.
    /// </summary>
    private void DrawMagicBolt(SpriteBatch sb, Vector2 from, Vector2 to, float flight, int size)
    {
        sb.Begin(samplerState: SamplerState.PointClamp);
        var r = System.Math.Max(2, size / 9);     // rayon de l'orbe (proportionnel au zoom)

        // Traînée : 2 orbes plus petites et plus pâles, en arrière sur la trajectoire.
        for (var i = 2; i >= 1; i--)
        {
            var tt = MathHelper.Clamp(flight - i * 0.07f, 0f, 1f);
            var p = Vector2.Lerp(from, to, tt);
            var rr = System.Math.Max(1, r - i);
            DrawOrb(sb, p, rr, Palette.Cyan1 * (0.5f - i * 0.12f), Palette.Cyan2 * (0.5f - i * 0.12f));
        }

        // Tête de l'orbe : halo cyan + corps + cœur clair.
        var head = Vector2.Lerp(from, to, flight);
        DrawOrb(sb, head, r, Palette.Cyan1 * 0.6f, Palette.White);
        sb.End();
    }

    /// <summary>Orbe carrée pixel-perfect : un halo (<paramref name="outer"/>) et un cœur (<paramref name="core"/>) centrés.</summary>
    private void DrawOrb(SpriteBatch sb, Vector2 center, int radius, Color outer, Color core)
    {
        var cx = (int)System.MathF.Round(center.X);
        var cy = (int)System.MathF.Round(center.Y);
        DrawRect(sb, new Rectangle(cx - radius, cy - radius, radius * 2, radius * 2), outer);
        var cr = System.Math.Max(1, radius / 2);
        DrawRect(sb, new Rectangle(cx - cr, cy - cr, cr * 2, cr * 2), core);
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

    /// <summary>Au contact (une fois par attaque) : fait jaillir le chiffre de dégâts, qui éclatera
    /// ensuite en feu d'artifice. Plus d'étincelles d'impact (le dev les trouvait trop chargées avec
    /// l'explosion du chiffre).</summary>
    private void OnImpact()
    {
        _impactHandled = true;
        _damagePopups.Spawn(_fx.To, _pendingDamage);   // le chiffre de dégâts jaillit au contact (puis éclate)
    }

    // ── Panneau latéral ───────────────────────────────────────────────────────────

    private Rectangle PanelRect()
    {
        var vp = VirtualViewport;
        return new Rectangle(vp.Width - RightPanelWidth, 0, RightPanelWidth, vp.Height);
    }

    private bool IsOverPanel(Point p) =>
        p.X >= Context.VirtualResolution.X - RightPanelWidth;

    /// <summary>Bouton « COMBATTRE » en bas du panneau de placement (souris) — équivaut à la touche Entrée.</summary>
    private Rectangle FightButtonRect()
    {
        var panel = PanelRect();
        const int h = 40, margin = 32;   // marge basse pour ne pas coller le bouton au bord de l'écran
        return new Rectangle(panel.X + PanelPad, panel.Bottom - margin - h, panel.Width - 2 * PanelPad, h);
    }

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

    /// <summary>
    /// Indice DANS <see cref="_pending"/> du portrait de réserve sous <paramref name="p"/> (null si
    /// aucun, ou si c'est la carte de la PILE). Le slot occupé par la pile de réserve est sauté, pour
    /// que la pile reste affichée là où elle a été formée et que les portraits gardent leur place.
    /// </summary>
    private int? PanelCardAt(Point p)
    {
        var pile = ReservePileSlot();
        var total = _pending.Count + (pile is null ? 0 : 1);
        for (var s = 0; s < total; s++)
        {
            if (!PanelCardRect(s).Contains(p))
                continue;
            if (pile == s)
                return null;                              // sur la pile, pas un portrait
            return pile is { } ps && s > ps ? s - 1 : s;  // slot → indice _pending (saute la pile)
        }
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
        Context.Font.Draw(sb, Loc.T("placement.title"), new Vector2(x, 34), 2, Palette.Yellow2);
        Context.Font.Draw(sb, Loc.T("placement.inventory"), new Vector2(x, PanelListTop - 22), 1, Palette.Blue1);

        // Compteur de déploiement (commandant compris), aligné à droite de l'en-tête d'inventaire.
        // Rouge quand le plafond est atteint : signale que les unités restantes ne pourront pas être posées.
        var full = _playerSpec.Count >= MaxDeployed;
        var counter = Loc.T("placement.deployed", _playerSpec.Count, MaxDeployed);
        Context.Font.Draw(sb, counter,
            new Vector2(panel.Right - PanelPad - Context.Font.Measure(counter, 1), PanelListTop - 22),
            1, full ? Palette.Purple5 : Palette.Cyan1);

        var anyFusable = false;
        for (var i = 0; i < _pending.Count; i++)
        {
            DrawInventoryCard(sb, _pending[i], PendingCardRect(i));   // saute le slot de la pile
            if (CanFuseFromReserve(_pending[i]))
                anyFusable = true;   // sert juste à afficher l'indice de fusion (aucun cadre coloré)
        }

        // Pile de fusion en cours (état « N/3 ») + son bouton d'annulation.
        DrawFusionStack(sb);

        // Aide JUSTE SOUS l'inventaire (et non collée au bas du panneau). La pile de réserve occupe un slot.
        var slots = _pending.Count + (ReservePileSlot() is null ? 0 : 1);
        var rows = System.Math.Max(1, (slots + InvCols - 1) / InvCols);
        var hintY = PanelListTop + rows * (InvCellH + InvGapY) + 12;
        if (Context.Input.UsingGamepad)
        {
            var line1 = _gpInventory ? Loc.T("placement.hint_gp_terrain") : Loc.T("placement.hint_gp_inventory");
            Context.Font.Draw(sb, line1, new Vector2(x, hintY), 1, Palette.Blue1);
            Context.Font.Draw(sb, Loc.T("placement.hint_gp_fight"), new Vector2(x, hintY + 16), 1, Palette.Cyan1);
            if (anyFusable)
                Context.Font.Draw(sb, Loc.T("placement.hint_gp_fuse"), new Vector2(x, hintY + 32), 1, Palette.Yellow2);
            if (FusionStacking)   // une pile en cours : B pour la défusionner
                Context.Font.Draw(sb, Loc.T("placement.hint_gp_unfuse"),
                    new Vector2(x, hintY + (anyFusable ? 48 : 32)), 1, Palette.Yellow2);
        }
        else
        {
            Context.Font.Draw(sb, Loc.T("placement.hint_drag"), new Vector2(x, hintY), 1, Palette.Blue1);
            Context.Font.Draw(sb, Loc.T("placement.hint_fight"), new Vector2(x, hintY + 16), 1, Palette.Cyan1);
            if (anyFusable)
                Context.Font.Draw(sb, Loc.T("placement.hint_fuse"), new Vector2(x, hintY + 32), 1, Palette.Yellow2);
        }

        // Bouton COMBATTRE en bas du panneau (souris), en plus de la touche Entrée. Pas en tuto (lancement scénarisé).
        if (_tutorial == null)
        {
            var btn = FightButtonRect();
            var hover = !Context.Input.UsingGamepad && btn.Contains(Context.Input.MousePosition);
            var down = hover && Context.Input.IsLeftDown;
            var dy = Context.Style.DrawButton(sb, btn, UiStyle.StateOf(hover, down));
            var area = btn; area.Offset(0, dy);
            Context.Font.DrawCentered(sb, Loc.T("placement.fight"), area, 1, Palette.White);
        }
    }

    /// <summary>
    /// Carte de la PILE de fusion de RÉSERVE (« N/3 ») juste après la réserve, avec son bouton « X ».
    /// Rien si aucune pile, si la pile est sur le plateau, ou si la popup est ouverte (pile complète).
    /// </summary>
    private void DrawFusionStack(SpriteBatch sb)
    {
        if (!FusionStacking || !FusionInReserve || _carryPile)   // pas dessinée quand portée
            return;

        var card = FusionStackCardRect();
        DrawFusionPileChip(sb, _fusionGroup[0].UnitClass, card, front: true);

        // Compteur « N/3 » sous la pile.
        Context.Font.DrawCentered(sb, $"{_fusionGroup.Count}/{Run.FusionSize}",
            new Rectangle(card.X - InvGapX / 2, card.Bottom + 2, card.Width + InvGapX, 10), 1, Palette.Yellow2);
        DrawFusionCancelButton(sb, FusionStackCancelRect());
    }

    /// <summary>
    /// Pile de fusion ancrée sur une CASE du plateau (« N/3 ») : sprite de la pièce + compteur + bouton
    /// « X ». Rien si aucune pile de plateau, ou si la popup est ouverte.
    /// </summary>
    private void DrawFusionBoardStack(SpriteBatch sb, GridLayout layout)
    {
        if (!FusionStacking || _fusionCell is not { } cell)
            return;

        var top = layout.CellToScreen(cell.Column, cell.Row);
        var rect = new Rectangle((int)top.X, (int)top.Y, layout.TileSize, layout.TileSize);
        DrawFusionPileChip(sb, _fusionGroup[0].UnitClass, rect, front: false);

        // Compteur « N/3 » en bas de la case.
        Context.Font.DrawCentered(sb, $"{_fusionGroup.Count}/{Run.FusionSize}",
            new Rectangle(rect.X, rect.Bottom - 13, rect.Width, 10), 1, Palette.Yellow2);
        DrawFusionCancelButton(sb, FusionBoardCancelRect(layout));
    }

    /// <summary>Pile PORTÉE : suit la souris (ou le curseur en manette), sprite + compteur « N/3 ».</summary>
    private void DrawCarriedPile(SpriteBatch sb, GridLayout board)
    {
        if (!_carryPile)
            return;

        Rectangle rect;
        if (Context.Input.UsingGamepad)
        {
            var top = board.CellToScreen(_cursor.Column, _cursor.Row);
            rect = new Rectangle((int)top.X, (int)top.Y, board.TileSize, board.TileSize);
        }
        else
        {
            var m = Context.Input.MousePosition;
            rect = new Rectangle(m.X - InvIconSize / 2, m.Y - InvIconSize / 2, InvIconSize, InvIconSize);
        }
        DrawFusionPileChip(sb, _fusionGroup[0].UnitClass, rect, front: true);
        Context.Font.DrawCentered(sb, $"{_fusionGroup.Count}/{Run.FusionSize}",
            new Rectangle(rect.X, rect.Bottom - 13, rect.Width, 10), 1, Palette.Yellow2);
    }

    /// <summary>Échelle du « punch » : gonfle à ~1,3× à l'empilement puis revient à 1.</summary>
    private float FusionPunchScale() =>
        _fusionPunchTimer <= 0 ? 1f : 1f + 0.30f * (float)(_fusionPunchTimer / FusionPunchDuration);

    /// <summary>Sprite de pile dessiné avec le « punch scale » (autour du centre de la zone).</summary>
    private void DrawFusionPileChip(SpriteBatch sb, UnitClass cls, Rectangle rect, bool front)
    {
        var scale = FusionPunchScale();
        var sprite = SpriteFor(cls, Faction.Player, front);
        if (scale <= 1.001f || sprite is null)
        {
            DrawChip(sb, cls, Faction.Player, rect, front);
            return;
        }
        var size = sprite.Width * scale;
        var cx = rect.X + rect.Width / 2f;
        var cy = rect.Y + rect.Height / 2f;
        var dest = new Rectangle((int)(cx - size / 2f), (int)(cy - size / 2f), (int)size, (int)size);
        sb.Draw(sprite, dest, Color.White);
    }

    /// <summary>Petit bouton « X » d'annulation de pile : fond TRAMÉ (dither) + relief, comme les boutons.</summary>
    private void DrawFusionCancelButton(SpriteBatch sb, Rectangle cancel)
    {
        var hover = !Context.Input.UsingGamepad && cancel.Contains(Context.Input.MousePosition);
        var dy = Context.Style.DrawButton(sb, cancel, UiStyle.StateOf(hover, Context.Input.IsLeftDown));
        Context.Font.DrawCentered(sb, "X",
            new Rectangle(cancel.X, cancel.Y + dy, cancel.Width, cancel.Height), 1, Palette.White);
    }

    /// <summary>
    /// Popup MODALE de fusion : assombrit l'écran et présente les 2 évolutions (cartes d'unité) à
    /// choisir, plus un bouton « Annuler ». Souris (survol+clic), clavier (Échap/Entrée) et manette
    /// (←/→, A, B) sont gérés dans <see cref="UpdateFusionPopup"/>.
    /// </summary>
    private void DrawFusionPopup(SpriteBatch sb, Viewport viewport)
    {
        var options = _fusionGroup[0].UnitClass.Evolutions;
        var count = options.Count;
        var domaine = _fusionGroup[0].Domaine;

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawDim(sb, viewport);   // voile du canvas ; les bandes du letterbox sont assombries via FullScreenDim

        var vpW = viewport.Width;
        var cancel = FusionCancelRect();

        // Cadre du TITRE (FUSION + sous-titre), centré au-dessus du bouton Annuler et des cartes.
        var titleW = Context.Font.Measure(Loc.T("fusion.title"), 3);
        var subW = Context.Font.Measure(Loc.T("fusion.subtitle"), 1);
        var boxW = System.Math.Max(titleW, subW) + 56;
        const int boxH = 64;
        var boxY = cancel.Y - 14 - boxH;
        Context.Style.DrawPanel(sb, new Rectangle((vpW - boxW) / 2, boxY, boxW, boxH));
        Context.Font.DrawCentered(sb, Loc.T("fusion.title"), new Rectangle(0, boxY + 12, vpW, 24), 3, Palette.Yellow2);
        Context.Font.DrawCentered(sb, Loc.T("fusion.subtitle"), new Rectangle(0, boxY + 42, vpW, 12), 1, Palette.Blue1);

        // Bouton Annuler ENTRE le cadre titre et les cartes, avec retour d'enfoncement (poussoir).
        var hovered = !Context.Input.UsingGamepad && cancel.Contains(Context.Input.MousePosition);
        var dyCancel = Context.Style.DrawButton(sb, cancel, UiStyle.StateOf(hovered, Context.Input.IsLeftDown));
        Context.Font.DrawCentered(sb, Loc.T("fusion.cancel"),
            new Rectangle(cancel.X, cancel.Y + dyCancel, cancel.Width, cancel.Height), 2,
            hovered ? Palette.Yellow2 : Palette.White);

        // Cartes d'évolution (mots-clés détaillés SOUS chaque carte, désormais dégagés). Le sprite reste
        // en SILHOUETTE tant que le joueur n'a jamais obtenu cette évolution (méta-progression).
        for (var i = 0; i < count; i++)
        {
            var rect = FusionCardRect(i, count);
            var revealed = Context.Saves.IsUnitDiscovered(options[i].Asset);
            DrawCardLayout(sb, rect, options[i], Faction.Player, domaine, options[i].MaxHp, options[i].MaxHp, revealed);
            DrawKeywordPopupsBelow(sb, options[i], rect);
        }

        // Surbrillance de la carte focus.
        var fi = System.Math.Clamp(_fusionFocus, 0, count - 1);
        DrawRectBorder(sb, Inflate(FusionCardRect(fi, count), 3), Palette.Yellow2, 3);
        sb.End();
    }

    // Bornes internes de la phase REVEAL (fractions de EvoRevealDuration).
    private const float EvoZoomIn = 0.10f;     // fin du zoom caméra (pièce → centre)
    private const float EvoFlickerEnd = 0.78f; // fin du clignotement (silhouettes) → flash + couleur

    /// <summary>
    /// Animation d'ÉVOLUTION. LONGUE (1re fois) : Reveal (zoom + clignotement Pokémon en ombre noire +
    /// flash + couleur) → Hold (attend le CLIC) → Return (la pièce revient se ranger). COURTE (déjà
    /// obtenue) : simple punch + flash sur la pièce.
    /// </summary>
    private void DrawEvolutionAnimation(SpriteBatch sb, Viewport viewport)
    {
        if (!_evoLong)
        {
            DrawEvolutionShort(sb, (float)(1.0 - _evoPhaseTimer / EvoShortDuration));
            return;
        }

        var centerBig = CenteredRect(viewport, 176);
        switch (_evoPhase)
        {
            case EvoPhase.Reveal: DrawEvolutionReveal(sb, viewport, centerBig); break;
            case EvoPhase.Hold: DrawEvolutionHold(sb, viewport, centerBig); break;
            default: DrawEvolutionReturn(sb, viewport, centerBig); break;   // Return
        }
        _sparks.Draw(sb, Context.Pixel);
    }

    /// <summary>Phase REVEAL : zoom caméra → clignotement ombre noire (accéléré) → flash → couleur.</summary>
    private void DrawEvolutionReveal(SpriteBatch sb, Viewport viewport, Rectangle centerBig)
    {
        var p = (float)(1.0 - _evoPhaseTimer / EvoRevealDuration);   // 0 → 1
        var zoom = p < EvoZoomIn ? Smooth01(p / EvoZoomIn) : 1f;     // zoom puis maintien au centre
        var rect = LerpRect(_evoSource, centerBig, zoom);

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawRect(sb, new Rectangle(0, 0, viewport.Width, viewport.Height), Palette.Black1 * (0.62f * zoom));

        if (p < EvoZoomIn)
        {
            DrawEvoSprite(sb, _evoBase, rect, Color.Black, 1f);                 // ombre du pion de base
        }
        else if (p < EvoFlickerEnd)
        {
            // CLIGNOTEMENT : alterne base/évolution en OMBRE NOIRE. Cadence = base (terme linéaire, du
            // switch dès le début) + accélération DOUCE et CONTINUE (terme quadratique) — pas de cubique
            // qui reste plat puis explose d'un coup.
            var phase = (p - EvoZoomIn) / (EvoFlickerEnd - EvoZoomIn);
            var toggle = (int)(phase * 6f + phase * phase * 18f);
            DrawEvoSprite(sb, toggle % 2 == 1 ? _evoResult : _evoBase, rect, Color.Black, 1f);
        }
        else
        {
            // RÉVÉLATION : l'évolution sort de l'ombre en couleur (reste au centre).
            var rp = (p - EvoFlickerEnd) / (1f - EvoFlickerEnd);
            var evoAlpha = Smooth01(rp / 0.5f);
            if (evoAlpha < 1f)
                DrawEvoSprite(sb, _evoResult, rect, Color.Black, 1f - evoAlpha);
            DrawEvoSprite(sb, _evoResult, rect, Color.White, evoAlpha);
            DrawEvoName(sb, viewport, rect, evoAlpha);
        }
        sb.End();

        var flashA = Bell(p, EvoFlickerEnd + 0.01f, 0.05f);
        if (flashA > 0.01f)
        {
            sb.Begin(blendState: BlendState.Additive, samplerState: SamplerState.PointClamp);
            DrawEvoSprite(sb, _evoResult, rect, Color.White, 0.95f * flashA);
            sb.End();
        }
    }

    /// <summary>Phase HOLD : l'évolution en couleur au centre + invite à CLIQUER pour ranger la pièce.</summary>
    private void DrawEvolutionHold(SpriteBatch sb, Viewport viewport, Rectangle centerBig)
    {
        var rect = ScaleRectCentered(centerBig, 1f + 0.03f * MathF.Sin(_time * 5f));   // léger souffle

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawDim(sb, viewport);
        DrawEvoSprite(sb, _evoResult, rect, Color.White, 1f);
        DrawEvoName(sb, viewport, centerBig, 1f);

        var prompt = Loc.T(Context.Input.UsingGamepad ? "fusion.continue_gp" : "fusion.continue");
        var a = 0.5f + 0.5f * MathF.Abs(MathF.Sin(_time * 3f));
        Context.Font.DrawCentered(sb, prompt,
            new Rectangle(0, centerBig.Bottom + 48, viewport.Width, 12), 1, Palette.Cyan1 * a);
        sb.End();
    }

    /// <summary>Phase RETURN : la pièce (caméra) revient du centre vers sa place après le clic.</summary>
    private void DrawEvolutionReturn(SpriteBatch sb, Viewport viewport, Rectangle centerBig)
    {
        var prt = (float)(1.0 - _evoPhaseTimer / EvoReturnDuration);
        var zoom = 1f - Smooth01(prt);   // centre → source
        var rect = LerpRect(_evoSource, centerBig, zoom);

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawRect(sb, new Rectangle(0, 0, viewport.Width, viewport.Height), Palette.Black1 * (0.62f * zoom));
        DrawEvoSprite(sb, _evoResult, rect, Color.White, 1f);
        sb.End();
    }

    /// <summary>Nom de l'évolution centré sous <paramref name="rect"/> (fondu via <paramref name="alpha"/>).</summary>
    private void DrawEvoName(SpriteBatch sb, Viewport viewport, Rectangle rect, float alpha)
    {
        if (_evoResult is { } r && alpha > 0.2f)
            Context.Font.DrawCentered(sb, UnitName(r).ToUpperInvariant(),
                new Rectangle(0, rect.Bottom + 14, viewport.Width, 18), 3, Palette.Yellow2 * alpha);
    }

    /// <summary>Nom d'affichage LOCALISÉ d'une classe (clé <c>unit.&lt;asset&gt;</c>, repli sur le nom brut).</summary>
    private static string UnitName(UnitClass c) => Loc.TOr("unit." + c.Asset, c.Name);

    private static Rectangle ScaleRectCentered(Rectangle r, float scale)
    {
        var w = (int)(r.Width * scale);
        var h = (int)(r.Height * scale);
        return new Rectangle(r.Center.X - w / 2, r.Center.Y - h / 2, w, h);
    }

    /// <summary>Version COURTE (unité déjà obtenue) : punch + flash sur la pièce, à son emplacement.</summary>
    private void DrawEvolutionShort(SpriteBatch sb, float p)
    {
        var punch = 1f + 0.5f * Bell(p, 0.25f, 0.32f);
        var s = (int)(_evoSource.Width * punch);
        var rect = new Rectangle(_evoSource.Center.X - s / 2, _evoSource.Center.Y - s / 2, s, s);

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawEvoSprite(sb, _evoResult, rect, Color.White, 1f);
        sb.End();

        var flashA = Bell(p, 0.2f, 0.14f);
        if (flashA > 0.01f)
        {
            sb.Begin(blendState: BlendState.Additive, samplerState: SamplerState.PointClamp);
            DrawEvoSprite(sb, _evoResult, rect, Color.White, 0.9f * flashA);
            sb.End();
        }

        _sparks.Draw(sb, Context.Pixel);
    }

    private static Rectangle CenteredRect(Viewport vp, int size) =>
        new(vp.Width / 2 - size / 2, vp.Height / 2 - size / 2, size, size);

    /// <summary>Interpolation linéaire entre deux rectangles (pour le « zoom caméra »).</summary>
    private static Rectangle LerpRect(Rectangle a, Rectangle b, float t) =>
        new((int)MathHelper.Lerp(a.X, b.X, t), (int)MathHelper.Lerp(a.Y, b.Y, t),
            (int)MathHelper.Lerp(a.Width, b.Width, t), (int)MathHelper.Lerp(a.Height, b.Height, t));

    /// <summary>Sprite d'une classe étiré dans <paramref name="rect"/>, teinte + alpha (overlay d'évolution).</summary>
    private void DrawEvoSprite(SpriteBatch sb, UnitClass? cls, Rectangle rect, Color tint, float alpha)
    {
        if (cls is null || alpha <= 0.001f)
            return;
        var sprite = SpriteFor(cls, Faction.Player, front: true);
        if (sprite != null)
            sb.Draw(sprite, rect, tint * alpha);
        else
            DrawRect(sb, rect, tint * alpha);
    }

    /// <summary>Lissage cubique 0→1 (smoothstep), borné.</summary>
    private static float Smooth01(float t)
    {
        t = MathHelper.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Bosse parabolique : 1 au centre, 0 à ±largeur (pour flash / punch).</summary>
    private static float Bell(float p, float center, float width)
    {
        var d = (p - center) / width;
        return MathF.Max(0f, 1f - d * d);
    }

    /// <summary>
    /// Aperçu au survol en placement (hors glisser) : affiche la carte complète À GAUCHE du panneau
    /// d'inventaire, pour un portrait d'inventaire OU une pièce déjà posée sur le plateau.
    /// </summary>
    private void DrawPlacementPreview(SpriteBatch sb)
    {
        if (_dragSpec != null)
            return;
        if (_tutorial is { Step: TutorialStep.ReviewCard })
            return;   // la revue de carte affiche déjà la carte du soldat (pas d'aperçu en double)

        // Cible de l'aperçu : en manette, slot d'inventaire focus ou case du curseur ; sinon souris.
        if (Context.Input.UsingGamepad)
        {
            if (_gpInventory && _pending.Count > 0)
            {
                var spec = _pending[System.Math.Clamp(_invFocus, 0, _pending.Count - 1)];
                DrawPreviewCard(sb, spec.UnitClass, Faction.Player, spec.Domaine,
                    spec.UnitClass.MaxHp, spec.UnitClass.MaxHp);
            }
            else if (!_gpInventory && _match.UnitAt(_cursor) is { } cu)
                DrawPreviewCard(sb, cu.Class, cu.Faction, cu.Domaine, cu.Hp, cu.MaxHp);
            return;
        }

        var mouse = Context.Input.MousePosition;

        // Priorité : portrait survolé dans l'inventaire (PV pleins, unité neuve).
        if (PanelCardAt(mouse) is { } i)
        {
            var spec = _pending[i];
            DrawPreviewCard(sb, spec.UnitClass, Faction.Player, spec.Domaine,
                spec.UnitClass.MaxHp, spec.UnitClass.MaxHp);
            return;
        }

        // Sinon : pièce posée sous le curseur souris (joueur ou ennemi déjà déployé).
        if (CellUnderMouse() is { } cell && _match.UnitAt(cell) is { } unit)
            DrawPreviewCard(sb, unit.Class, unit.Faction, unit.Domaine, unit.Hp, unit.MaxHp);
    }

    /// <summary>Carte d'aperçu placée juste à GAUCHE du panneau d'inventaire (espace libre).</summary>
    private void DrawPreviewCard(SpriteBatch sb, UnitClass c, Faction faction, Domaine domaine, int hp, int maxHp)
    {
        var vp = VirtualViewport;
        var x = PanelRect().X - CombatCardGap - CombatCardW;
        var rect = new Rectangle(x, (vp.Height - CombatCardH) / 2, CombatCardW, CombatCardH);
        DrawCardLayout(sb, rect, c, faction, domaine, hp, maxHp);
        DrawKeywordPopupsBelow(sb, c, rect);
    }

    private void DrawInventoryCard(SpriteBatch sb, UnitSpec spec, Rectangle icon)
    {
        // Portrait 64×64 à taille native (jamais redimensionné), de FACE (présentation), nom dessous.
        DrawChip(sb, spec.UnitClass, Faction.Player, icon, front: true);
        Context.Font.DrawCentered(sb, UnitName(spec.UnitClass).ToUpperInvariant(),
            new Rectangle(icon.X - InvGapX / 2, icon.Bottom + 2, icon.Width + InvGapX, 10), 1, Palette.White);
    }

    // ── Cartes de combat (remplacent l'ancien panneau de droite) ──────────────────
    // Réutilisent le gabarit des cartes de recrutement ; le contenu sera retravaillé plus tard.
    private const int CombatCardW = 200;
    private const int CombatCardH = 330;
    private const int CombatCardGap = 24;

    /// <summary>
    /// Cartes flottantes du combat : l'unité SÉLECTIONNÉE s'affiche à droite du plateau, l'ennemi
    /// SURVOLÉ à gauche. Les deux peuvent coexister (sélection + survol d'un ennemi).
    /// </summary>
    private void DrawCombatCards(SpriteBatch sb, GridLayout layout)
    {
        var board = BoardRect(layout);
        // En manette, la « case survolée » est celle du curseur ; sinon celle sous la souris.
        var hovered = Context.Input.UsingGamepad ? _cursor : CellUnderMouse();

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
    /// Carte d'une unité du plateau, dans son ÉTAT COURANT (PV actuels). Les popups de mots-clés
    /// descendent SOUS la carte (à droite de l'écran ils seraient coupés par le bord).
    /// </summary>
    private void DrawUnitCard(SpriteBatch sb, Unit unit, Rectangle rect)
    {
        var c = unit.Class;
        DrawCardLayout(sb, rect, c, unit.Faction, unit.Domaine, unit.Hp, unit.MaxHp);
        DrawKeywordPopupsBelow(sb, c, rect);
    }

    // ── Mise en forme commune des cartes (combat + recrutement) ──────────────────
    private const int CardPad = 12;

    /// <summary>
    /// Corps d'une carte d'unité : sprite, icône de domaine (39×39), barre de PV (1 carré = 1 PV)
    /// + « pv/max », puis les caractéristiques (icône 32×32 + libellé + valeur). Les mots-clés sont
    /// dessinés à part (popups) par l'appelant. <paramref name="hp"/> = PV courants à afficher.
    /// </summary>
    private void DrawCardLayout(SpriteBatch sb, Rectangle rect, UnitClass c, Faction faction,
        Domaine domaine, int hp, int maxHp, bool revealed = true)
    {
        Context.Style.DrawPanel(sb, rect);
        var y = rect.Y + CardPad;

        // Titre : nom de l'unité (localisé).
        Context.Font.DrawCentered(sb, UnitName(c).ToUpperInvariant(),
            new Rectangle(rect.X, y, rect.Width, 14), 2, Palette.White);
        y += 22;

        // Sprite du pion (comme en jeu, de face). En SILHOUETTE si l'unité n'est pas encore découverte.
        var sprite = new Rectangle(rect.X + (rect.Width - 64) / 2, y, 64, 64);
        if (revealed)
            DrawChip(sb, c, faction, sprite, front: true);
        else
            DrawHiddenChip(sb, c, faction, sprite);
        y = sprite.Bottom + 6;

        // Icône de domaine (39×39), centrée sous le pion.
        var dom = new Rectangle(rect.X + (rect.Width - 39) / 2, y, 39, 39);
        DrawDomaineIcon(sb, domaine, dom);
        y = dom.Bottom + 10;

        // Barre de PV (une rangée, carrés ajustés à la largeur) + texte « pv/max ».
        var barRect = new Rectangle(rect.X + CardPad, y, rect.Width - 2 * CardPad, 14);
        DrawHpBar(sb, barRect, hp, maxHp);
        y = barRect.Bottom + 2;
        Context.Font.DrawCentered(sb, $"{hp}/{maxHp}",
            new Rectangle(rect.X, y, rect.Width, 8), 1, Palette.White);
        y += 14;

        // Caractéristiques : icône 32×32 + libellé + valeur.
        // Portée = MAX seulement (le « min » / zone morte est expliqué par le mot-clé ZONE MORTE).
        y = DrawStatRow(sb, rect, y, "deg", Loc.T("stat.power"), $"{c.Damage}", Palette.Brown3);
        y = DrawStatRow(sb, rect, y, "dep", Loc.T("stat.movement"), $"{c.MoveRange}", Palette.Cyan2);
        DrawStatRow(sb, rect, y, "tir", Loc.T("stat.range"), $"{c.AttackRange}", Palette.Yellow2);

        // Liste des mots-clés en bas de carte (séparés par « | »), détaillés dans les popups.
        var keywords = KeywordsFor(c);
        if (keywords.Count > 0)
        {
            var joined = string.Join(" | ", keywords.Select(k => k.Label));
            var lines = WrapText(joined, rect.Width - 2 * CardPad, 1);
            var ty = rect.Bottom - CardPad - lines.Count * 9;
            foreach (var line in lines)
            {
                Context.Font.DrawCentered(sb, line, new Rectangle(rect.X, ty, rect.Width, 8), 1, Palette.Yellow2);
                ty += 9;
            }
        }
    }

    /// <summary>Une ligne de caractéristique : icône 32×32 à gauche, libellé, valeur alignée à droite.</summary>
    private int DrawStatRow(SpriteBatch sb, Rectangle card, int y, string iconKey, string label,
        string value, Color valueColor)
    {
        const int iconSize = 32;
        var icon = new Rectangle(card.X + CardPad, y, iconSize, iconSize);
        DrawStatIcon(sb, iconKey, icon, valueColor);

        // Libellé + valeur centrés verticalement sur la hauteur de l'icône.
        var rowH = new Rectangle(icon.Right + 8, y, card.Right - CardPad - (icon.Right + 8), iconSize);
        Context.Font.Draw(sb, label,
            new Vector2(rowH.X, rowH.Y + (iconSize - 7 * 2) / 2), 2, Palette.Blue1);
        var vw = Context.Font.Measure(value, 2);
        Context.Font.Draw(sb, value,
            new Vector2(rowH.Right - vw, rowH.Y + (iconSize - 7 * 2) / 2), 2, valueColor);
        return y + iconSize + 4;
    }

    /// <summary>
    /// Barre de PV : la barre occupe TOUTE la zone (taille fixe, hauteur indépendante du nombre de PV)
    /// et se découpe en un segment par point de vie. PV restants = rouge, PV manquants = rouge foncé.
    /// </summary>
    private void DrawHpBar(SpriteBatch sb, Rectangle area, int hp, int maxHp)
    {
        if (maxHp <= 0)
            return;

        const int gap = 1;
        // Bornes PARTAGÉES entre segments voisins : on arrondit une seule fois chaque frontière, puis
        // on retire 1 px à droite pour l'espace. Gap toujours constant, largeurs à ±1 px près.
        for (var i = 0; i < maxHp; i++)
        {
            var left = area.X + (int)System.Math.Round((double)i * area.Width / maxHp);
            var right = area.X + (int)System.Math.Round((double)(i + 1) * area.Width / maxHp);
            var w = System.Math.Max(1, right - left - (i < maxHp - 1 ? gap : 0));
            DrawRect(sb, new Rectangle(left, area.Y, w, area.Height), i < hp ? Palette.Purple5 : Palette.Purple2);
        }
    }

    // ── Icônes (placeholders dessinés ; brancher un PNG = déposer le fichier nommé ci-dessous) ───
    private readonly Dictionary<string, Texture2D?> _iconSprites = new();

    /// <summary>PNG d'icône dans Assets/Icons (mis en cache), ou null s'il est absent.</summary>
    private Texture2D? IconOrNull(string fileName)
    {
        if (!_iconSprites.TryGetValue(fileName, out var sprite))
        {
            sprite = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath($"Assets/Icons/{fileName}.png"));
            _iconSprites[fileName] = sprite;
        }
        return sprite;
    }

    /// <summary>Icône de domaine 39×39. PNG <c>Assets/Icons/domaine_&lt;domaine&gt;.png</c> si présent, sinon placeholder.</summary>
    private void DrawDomaineIcon(SpriteBatch sb, Domaine domaine, Rectangle area)
    {
        if (IconOrNull($"domaine_{domaine}".ToLowerInvariant()) is { } png)
        {
            DrawSpriteFit(sb, png, area);
            return;
        }
        // Placeholder : pastille colorée + initiale du domaine.
        var color = domaine switch
        {
            Domaine.Pion => Palette.Cyan1,
            Domaine.Fou => Palette.Brown2,
            Domaine.Cavalier => Palette.Green1,
            Domaine.Tour => Palette.Navy1,
            Domaine.Dame => Palette.Yellow1,
            _ => Palette.Grey,
        };
        DrawRect(sb, Inflate(area, 1), Palette.Black1);
        DrawRect(sb, area, color);
        Context.Font.DrawCentered(sb, domaine.ToString()[..1].ToUpperInvariant(), area, 2, Palette.Black1);
    }

    /// <summary>Icône de stat 32×32. PNG <c>Assets/Icons/stat_&lt;key&gt;.png</c> si présent, sinon placeholder.</summary>
    private void DrawStatIcon(SpriteBatch sb, string key, Rectangle area, Color tint)
    {
        if (IconOrNull($"stat_{key}") is { } png)
        {
            DrawSpriteFit(sb, png, area);
            return;
        }
        DrawRect(sb, Inflate(area, 1), Palette.Black1);
        DrawRect(sb, area, Palette.Navy2);
        Context.Font.DrawCentered(sb, key.ToUpperInvariant()[..1], area, 2, tint);
    }

    // ── Popups de mots-clés ──────────────────────────────────────────────────────

    /// <summary>Mots-clés d'une classe : ses traits + « Traverse allié » si elle perce ses alliés.</summary>
    private static List<UnitKeywords.Keyword> KeywordsFor(UnitClass c)
    {
        var list = new List<UnitKeywords.Keyword>();
        foreach (var t in c.Traits)
            list.Add(UnitKeywords.For(t));
        // « Traverse allié » est redondant avec « Franchissement » (le cavalier franchit déjà tout) :
        // on ne le montre pas quand la classe a ce trait, Franchissement suffit à l'expliquer.
        if (c.PiercesAllies && !c.Traits.Contains("Franchissement"))
            list.Add(UnitKeywords.PiercesAllies);
        if (c.MinAttackRange > 1)
            list.Add(UnitKeywords.DeadZone);
        return list;
    }

    /// <summary>Popups permanents empilés SOUS la carte (évite la coupe au bord droit de l'écran).</summary>
    private void DrawKeywordPopupsBelow(SpriteBatch sb, UnitClass c, Rectangle card)
        => DrawKeywordPopupStack(sb, c, new Point(card.X, card.Bottom + 10), card.Width);

    /// <summary>
    /// Empile verticalement un popup par mot-clé depuis <paramref name="origin"/> : un panneau avec le
    /// libellé (jaune) et la description en lignes repliées. Rien si l'unité n'a aucun mot-clé.
    /// </summary>
    private void DrawKeywordPopupStack(SpriteBatch sb, UnitClass c, Point origin, int width)
    {
        var keywords = KeywordsFor(c);
        if (keywords.Count == 0)
            return;

        const int pad = 8, lineH = 9, gap = 8;
        var y = origin.Y;
        foreach (var kw in keywords)
        {
            var lines = WrapText(kw.Description, width - 2 * pad, 1);
            var h = pad + 10 + lines.Count * lineH + pad;     // titre + lignes
            var box = new Rectangle(origin.X, y, width, h);
            Context.Style.DrawPanel(sb, box);

            Context.Font.Draw(sb, kw.Label, new Vector2(box.X + pad, box.Y + pad), 1, Palette.Yellow2);
            var ly = box.Y + pad + 11;
            foreach (var line in lines)
            {
                Context.Font.Draw(sb, line, new Vector2(box.X + pad, ly), 1, Palette.White);
                ly += lineH;
            }
            y += h + gap;
        }
    }

    /// <summary>Découpe un texte en lignes tenant dans <paramref name="maxWidth"/> (coupe aux espaces).</summary>
    private List<string> WrapText(string text, int maxWidth, int scale)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Context.Font.Measure(candidate, scale) > maxWidth && current.Length > 0)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        if (current.Length > 0)
            lines.Add(current);
        return lines;
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
        _run.IsBossCombat ? Loc.T("combat.boss") : Loc.T("combat.number", _run.CombatNumber, Run.TotalCombats);

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
        Context.Font.DrawCentered(sb, UnitName(cls)[..1].ToUpperInvariant(), area, 2, Palette.White);
    }

    /// <summary>
    /// Unité non encore découverte (méta-progression) : OMBRE NOIRE UNIFORME du sprite — on voit la
    /// SILHOUETTE (forme) entièrement noire, aucun détail. Teinte NOIR PUR (multiplie tout à 0).
    /// </summary>
    private void DrawHiddenChip(SpriteBatch sb, UnitClass cls, Faction faction, Rectangle area)
    {
        var sprite = SpriteFor(cls, faction, front: true);
        if (sprite != null)
            sb.Draw(sprite, area, Color.Black);   // ombre noire uniforme (silhouette)
        else
            DrawRect(sb, area, Palette.Black1);
    }

    private void DrawDragGhost(SpriteBatch sb)
    {
        // En manette, le pion porté est dessiné sur la case du curseur (cf. DrawGamepadPlacementCursor).
        if (_dragSpec == null || Context.Input.UsingGamepad)
            return;

        var m = Context.Input.MousePosition;
        const int s = 64; // taille native du sprite → fantôme net, identique aux unités posées
        DrawChip(sb, _dragSpec.UnitClass, Faction.Player, new Rectangle(m.X - s / 2, m.Y - s / 2, s, s));
    }

    /// <summary>Curseur de case (manette) au placement — coins AU-DESSUS des pièces (toujours visible).</summary>
    private void DrawGamepadPlacementCursor(SpriteBatch sb, GridLayout board)
    {
        if (!Context.Input.UsingGamepad || _gpInventory)
            return;
        DrawCursorCorners(sb, board, _cursor);
    }

    /// <summary>
    /// Curseur dessiné en CROCHETS aux 4 coins de la case (au-dessus des pièces) : reste lisible même
    /// sur un pion, sans le « barrer » comme un cadre plein. <see cref="Palette.Yellow2"/>.
    /// </summary>
    private void DrawCursorCorners(SpriteBatch sb, GridLayout board, Cell cell)
    {
        var top = board.CellToScreen(cell.Column, cell.Row);
        int x = (int)top.X, y = (int)top.Y, s = board.TileSize;
        int leg = System.Math.Max(6, s / 4), t = 2;   // longueur des branches, épaisseur
        var c = Palette.Yellow2;

        // Coin haut-gauche
        DrawRect(sb, new Rectangle(x, y, leg, t), c);
        DrawRect(sb, new Rectangle(x, y, t, leg), c);
        // Coin haut-droit
        DrawRect(sb, new Rectangle(x + s - leg, y, leg, t), c);
        DrawRect(sb, new Rectangle(x + s - t, y, t, leg), c);
        // Coin bas-gauche
        DrawRect(sb, new Rectangle(x, y + s - t, leg, t), c);
        DrawRect(sb, new Rectangle(x, y + s - leg, t, leg), c);
        // Coin bas-droit
        DrawRect(sb, new Rectangle(x + s - leg, y + s - t, leg, t), c);
        DrawRect(sb, new Rectangle(x + s - t, y + s - leg, t, leg), c);
    }

    /// <summary>Pion porté affiché sur la case du curseur — dessiné AU-DESSUS des pièces.</summary>
    private void DrawCarriedAtCursor(SpriteBatch sb, GridLayout board)
    {
        if (!Context.Input.UsingGamepad || _gpInventory || _dragSpec == null)
            return;
        var top = board.CellToScreen(_cursor.Column, _cursor.Row);
        DrawChip(sb, _dragSpec.UnitClass, Faction.Player,
            new Rectangle((int)top.X, (int)top.Y, board.TileSize, board.TileSize));
    }

    /// <summary>Surbrillance du slot d'inventaire sous le focus manette (sous-mode inventaire).</summary>
    private void DrawInventoryFocusHighlight(SpriteBatch sb)
    {
        if (!Context.Input.UsingGamepad || !_gpInventory || _pending.Count == 0)
            return;

        var i = System.Math.Clamp(_invFocus, 0, _pending.Count - 1);
        var icon = PanelCardRect(i);
        // Cadre englobant l'icône ET le nom dessous (cf. DrawInventoryCard : nom large + 12 px sous l'icône).
        var frame = new Rectangle(icon.X - InvGapX / 2, icon.Y, icon.Width + InvGapX, icon.Height + 14);
        DrawRectBorder(sb, Inflate(frame, 3), Palette.Yellow2, 3);
    }

    /// <summary>Curseur de case (manette) en combat, au tour du joueur — coins au-dessus des pièces.</summary>
    private void DrawGamepadBattleCursor(SpriteBatch sb, GridLayout board)
    {
        if (!Context.Input.UsingGamepad || _match.CurrentTurn != Faction.Player)
            return;

        DrawCursorCorners(sb, board, _cursor);
    }

    private void DrawRecruitment(SpriteBatch sb, Viewport viewport)
    {
        var army = ArmyMinusCommander();
        var availW = viewport.Width - RightPanelWidth;   // zone des cartes, à GAUCHE du panneau

        sb.Begin(samplerState: SamplerState.PointClamp);
        // Cadre (style panneau/carte) autour du titre + sous-titre, dimensionné sur le plus large des deux.
        var titleW = Context.Font.Measure(Loc.T("recruit.title"), 3);
        var subW = Context.Font.Measure(Loc.T("recruit.subtitle"), 1);
        var boxW = System.Math.Max(titleW, subW) + 56;
        Context.Style.DrawPanel(sb, new Rectangle((availW - boxW) / 2, 48, boxW, 72));
        Context.Font.DrawCentered(sb, Loc.T("recruit.title"), new Rectangle(0, 60, availW, 24), 3, Palette.Yellow2);
        Context.Font.DrawCentered(sb, Loc.T("recruit.subtitle"),
            new Rectangle(0, 100, availW, 12), 1, Palette.Blue1);
        for (var i = 0; i < _run.Draft.Count; i++)
            DrawDraftCard(sb, _run.Draft[i], DraftCardRect(i, _run.Draft.Count, availW, viewport.Height));

        // Surbrillance de la carte FOCUS (souris ou manette) — sauf pendant le vol de sélection.
        if (_recruitChoice == null && _run.Draft.Count > 0)
        {
            var fi = System.Math.Clamp(_recruitFocus, 0, _run.Draft.Count - 1);
            var fr = DraftCardRect(fi, _run.Draft.Count, availW, viewport.Height);
            DrawRectBorder(sb, Inflate(fr, 3), Palette.Yellow2, 3);
        }

        // Panneau d'inventaire (à droite) : ton armée actuelle, hors commandant.
        DrawPanelBackground(sb);
        DrawArmyInventory(sb, army);
        sb.End();

        // Pion de la carte choisie en vol vers son emplacement d'inventaire (par-dessus le reste).
        if (_recruitChoice is { } choice)
            DrawRecruitFlight(sb, choice, army.Count);   // nouveau slot = à la suite de l'armée
    }

    /// <summary>L'armée actuelle hors commandant — affichée dans le panneau d'inventaire au recrutement.</summary>
    private List<UnitSpec> ArmyMinusCommander()
    {
        var commander = _run.Commander;
        var army = new List<UnitSpec>();
        foreach (var spec in _run.Roster)
            if (spec != commander)
                army.Add(spec);
        return army;
    }

    /// <summary>Contenu du panneau d'inventaire au recrutement : titre + portraits de l'armée.</summary>
    private void DrawArmyInventory(SpriteBatch sb, List<UnitSpec> army)
    {
        var x = PanelRect().X + PanelPad;
        Context.Font.Draw(sb, Loc.T("recruit.army"), new Vector2(x, 34), 2, Palette.Yellow2);
        Context.Font.Draw(sb, Loc.T("placement.inventory"), new Vector2(x, PanelListTop - 22), 1, Palette.Blue1);
        for (var i = 0; i < army.Count; i++)
            DrawInventoryCard(sb, army[i], PanelCardRect(i));
    }

    /// <summary>
    /// Pion recruté qui vole de sa carte (<see cref="_recruitFrom"/>) vers son emplacement d'inventaire
    /// (slot <paramref name="slotIndex"/>) — translation pure 64×64 (pixel-perfect), avec accélération.
    /// </summary>
    private void DrawRecruitFlight(SpriteBatch sb, UnitSpec choice, int slotIndex)
    {
        var t = MathHelper.Clamp(1f - _recruitHold / RecruitFlightDuration, 0f, 1f);
        var ease = t * t;
        var slot = PanelCardRect(slotIndex);
        var target = new Vector2(slot.X + slot.Width / 2f, slot.Y + slot.Height / 2f);
        var pos = Vector2.Lerp(_recruitFrom, target, ease);
        var dest = new Rectangle((int)(pos.X - InvIconSize / 2f), (int)(pos.Y - InvIconSize / 2f),
            InvIconSize, InvIconSize);

        sb.Begin(samplerState: SamplerState.PointClamp);
        if (SpriteFor(choice.UnitClass, Faction.Player, front: true) is { } sprite)
            sb.Draw(sprite, dest, Color.White);
        else
            DrawChip(sb, choice.UnitClass, Faction.Player, dest, front: true);
        sb.End();
    }

    private void DrawDraftCard(SpriteBatch sb, UnitSpec spec, Rectangle rect)
    {
        var c = spec.UnitClass;
        // Recrutement : portrait de FACE, PV pleins (l'unité est neuve).
        DrawCardLayout(sb, rect, c, Faction.Player, spec.Domaine, c.MaxHp, c.MaxHp);
        // Cartes côte à côte : les popups descendent SOUS la carte (pas sur le côté).
        DrawKeywordPopupsBelow(sb, c, rect);
    }

    private static Rectangle DraftCardRect(int index, int count, int vpW, int vpH)
    {
        const int cardW = 200, cardH = 330, gap = 28;
        var total = count * cardW + (count - 1) * gap;     // centré sur le NOMBRE réel de cartes (peut être < 3)
        var x0 = (vpW - total) / 2;
        var y = (vpH - cardH) / 2 + 20;
        return new Rectangle(x0 + index * (cardW + gap), y, cardW, cardH);
    }

    private void DrawEndHud(SpriteBatch sb, Viewport viewport)
    {
        var victory = _run.Phase == RunPhase.Victory;
        var title = victory ? Loc.T("end.victory") : Loc.T("end.defeat");
        var sub = victory ? Loc.T("end.boss_defeated") : _defeatReason;

        Context.Font.DrawCentered(sb, title,
            new Rectangle(0, viewport.Height / 2 - 40, viewport.Width, 28), 4,
            victory ? Palette.Yellow2 : Palette.Purple5);
        Context.Font.DrawCentered(sb, sub,
            new Rectangle(0, viewport.Height / 2 + 4, viewport.Width, 12), 2, Palette.White);
        Context.Font.DrawCentered(sb, Loc.T("end.replay"),
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
        var viewport = VirtualViewport;
        var action = MenuAction.None;

        // Manette : navigation au focus (haut/bas), réglages (gauche/droite), A = valider.
        if (Context.Input.Nav(NavDir.Up)) { _pauseMenu.MoveFocus(-1); Context.Sounds.Play("menu_click"); }
        if (Context.Input.Nav(NavDir.Down)) { _pauseMenu.MoveFocus(+1); Context.Sounds.Play("menu_click"); }
        if (Context.Input.Nav(NavDir.Left)) action = _pauseMenu.AdjustFocused(-1);
        if (Context.Input.Nav(NavDir.Right)) action = _pauseMenu.AdjustFocused(+1);
        if (Context.Input.WasConfirmPressed) { Context.Sounds.Play("menu_click"); action = _pauseMenu.ActivateFocused(); }

        // Souris : clic direct.
        if (Context.Input.WasLeftClicked)
        {
            Context.Sounds.Play("menu_click");
            action = _pauseMenu.HandleClick(Context.Input.MousePosition, viewport.Width, viewport.Height);
        }

        ApplyMenuAction(action);
    }

    private void ApplyMenuAction(MenuAction action)
    {
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
            case MenuAction.LanguageChanged:
                Context.Saves.SaveSettings(Context.Settings);
                break;
        }
    }
}
