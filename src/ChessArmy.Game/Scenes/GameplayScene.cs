using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Battle;
using ChessArmy.Core.Campaign;
using ChessArmy.Core.Command;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Map;
using ChessArmy.Engine;
using ChessArmy.Engine.Audio;
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
/// Scène de campagne (première boucle de gameplay) : terrain 8×8, boucle
/// Placement → Combat → Recrutement → … sur 6 combats, le dernier étant le boss.
/// Le commandant (mort = game over) est posé d'office ; le joueur déploie le reste de
/// son inventaire par glisser-déposer depuis le panneau de droite, puis combat l'IA.
/// Échap = menu pause, F1 = bascule du quadrillage.
/// </summary>
public sealed class GameplayScene : Scene
{
    // Dimensions du plateau du combat COURANT — variables : une map dessinée (escarmouche 6×6) les
    // fixe à sa taille, sinon 8×8 (terrain aléatoire). Réglées par combat dans BeginPlacement/BeginTutorial.
    private int Columns = 8;
    private int Rows = 8;
    // Plafond d'unités joueur déployées sur le plateau, COMMANDANT COMPRIS. Propre au COMMANDANT (base 5 =
    // commandant + 4 recrues), agrandi par les nœuds « déploiement » de son arbre de commandement.
    private int MaxDeployed => _run.DeployLimit;
    private const double AiDelaySeconds = 0.45;
    private const double TutorialEnemyDelay = 2.6;   // tuto : laisse lire la pop avant que l'IA bouge/contre-attaque

    // Remontée du sprite (fraction de la case) pour centrer le socle sur la case. 0 = dans la case.
    private const float SpriteLiftFraction = 0.25f;

    // Icône de TIER (1/2/3), 23 (largeur) × 9 (hauteur). Posée au socle en jeu et au-dessus du nom sur la carte.
    private const int TierIconW = 23;
    private const int TierIconH = 9;
    // Position verticale du BAS de l'icône dans le sprite 64×64 (0 = haut … 1 = bas) : sur la face du socle.
    private const float SocleTierAnchor = 0.93f;

    // Panneau latéral droit (inventaire au placement, infos en combat).
    private const int RightPanelWidth = 240;   // élargi pour 3 colonnes de portraits 64×64
    private const int PanelPad = 12;
    // Inventaire en grille : portraits 64×64 NATIFS (jamais redimensionnés), 3 colonnes.
    private const int InvIconSize = 64;
    private const int InvCols = 3;
    private const int InvGapX = 8;
    private const int InvCellH = InvIconSize + 14; // portrait + libellé dessous
    private const int InvGapY = 6;
    private const int InvRowPitch = InvCellH + InvGapY;   // pas vertical d'une rangée à l'autre
    private const int InvHintReserve = 52;                // place gardée sous la grille pour les lignes d'aide
    private const int PanelListTop = 110;

    // (Ordre de déploiement centre→bords : ColumnsCenterOut(), calculé selon la largeur du plateau.)

    // Chemins d'assets résolus depuis le dossier de l'exe (indépendant du répertoire de travail).
    private static string AssetPath(string relative) =>
        System.IO.Path.Combine(System.AppContext.BaseDirectory, relative);

    // Terrain du combat courant. Combats 1-2 = escarmouche 6×6 dessinée (_map) ; sinon 8×8 aléatoire.
    private Battlefield _battlefield = Battlefield.CreateFlat(8, 8);

    // Catalogue de tuiles (tiles.json) + map des combats 1-2 (escarmouche_01), chargés une fois au Load.
    private TileCatalog? _catalog;
    private readonly List<MapData> _maps = new();   // toutes les maps dessinées (Assets/Maps), associées au combat par taille
    // Map du combat courant : non-null = combat sur map dessinée (spawns peints) ; null = terrain aléatoire.
    private MapData? _map;

    // Tilesets : une tuile peut être rendue depuis une feuille (rectangle source) plutôt qu'un PNG
    // individuel. _sheets : nom de feuille → texture ; _tileSheet : id → feuille ; _tileVariants : id →
    // une ou plusieurs cellules possibles (le rendu en choisit une, stable par case — voir TileSprite).
    private readonly Dictionary<string, Texture2D> _sheets = new();
    private readonly Dictionary<string, string> _tileSheet = new();
    private readonly Dictionary<string, List<Rectangle>> _tileVariants = new();

    // Animation d'assemblage du plateau au début du placement : les tuiles montent du bas, en cascade.
    private float _boardIntro;          // temps écoulé depuis le début de l'assemblage (s)
    private float _boardIntroTotal;     // durée totale (0 = pas d'animation, ex. tutoriel)
    private const float BoardIntroStagger = 0.05f;   // délai entre 2 tuiles successives
    private const float BoardIntroRise = 0.32f;      // durée de montée d'une tuile
    private const float BoardIntroDrop = 0.6f;       // petite remontée (en hauteurs de sprite) : émergence, pas chute

    // Objets « recrue » (calque "objects" de la map, comme les coffres) : pion « ? » immobile ; un allié
    // qui ENTRE dessus en combat gagne un pion (tier 1) en réserve, puis l'objet est consommé (usage
    // unique). Détection par transition (absent→présent). Rendu : PNG, repli sur un pion « ? » placeholder.
    private readonly List<Cell> _recrueCells = new();        // cases recrue du combat courant
    private readonly HashSet<Cell> _recrueConsumed = new();  // déjà déclenchées (libérées OU capturées)
    private readonly HashSet<Cell> _recrueCaptured = new();  // sous-ensemble CAPTURÉ par l'IA (mission « sauver » : distingue libéré du perdu)
    private readonly HashSet<Cell> _recruePrev = new();      // cases recrue occupées par un allié à la frame précédente
    private UnitSpec? _recrueReveal;                         // unité gagnée en attente de révélation (carte modale, fige le combat)
    private bool _recrueAdded;                               // recrue posée dans l'inventaire (phase « pause » : on laisse voir le slot)
    private float _recrueSettle;                             // temps restant d'affichage du panneau après l'atterrissage
    private const float RecrueSettleDuration = 0.7f;         // le panneau reste ce temps après l'atterrissage avant de fermer
    // Looks du pion recrue : chaque paire <Nom>_front.png (+ <Nom>_back.png optionnel) de Assets/Objects/ =
    // une variante ; une seule est tirée STABLE par case (cf. RecrueSpriteFor). L'orientation suit la MOITIÉ
    // du plateau, comme les pions (moitié haute → _front, moitié basse → _back), cf. DefaultFacesDown.
    // Liste vide → placeholder « ? » dessiné.
    private readonly List<(Texture2D Front, Texture2D? Back)> _recrueLooks = new();

    // Missions SPÉCIALES à paysans (tuiles recrue _recrueCells), selon le sous-objectif de la map Speciale :
    //   • LibererPaysans : le JOUEUR marche dessus pour les libérer (rejoignent l'armée) — IA gardes défensifs.
    //   • ProtegerPaysans : les ENNEMIS (IA offensive) tentent de les capturer ; le joueur les protège.
    // Objectif = résoudre le MAXIMUM avant la limite de tours ; aucune défaite hors chute du commandant
    // (la limite atteinte clôt juste la mission). _recrueConsumed = paysans RÉSOLUS (libérés OU capturés).
    private const int SpecialTurnLimit = 15;   // limite de rounds PAR DÉFAUT (surchargée par la map via `turnLimit`)
    private bool _specialMission;              // vrai si le combat courant est une vraie mission spéciale
    private SpecialObjective _specialObjective = SpecialObjective.Aucun;   // sous-objectif de la map courante
    private int _specialRoundsLeft;            // rounds restants (décrémenté à chaque action ennemie résolue)
    private bool _specialBriefOpen;            // modale de briefing ouverte : gèle le placement jusqu'au clic / A

    /// <summary>
    /// Chiffres d'une mission spéciale FIGÉS à sa clôture (avant que la complétion ne retire les pertes du
    /// roster), affichés par la modale de bilan avant l'écran de récupération des pions.
    /// </summary>
    private readonly record struct SpecialRecap(
        SpecialObjective Objective, int Paysans, int PaysansTotal, int Turns, int TurnBudget, int Losses, int Required);

    private SpecialRecap? _specialRecap;       // bilan à valider ; non-null = modale ouverte (gèle le recrutement)

    // Écran de récompense « protéger » (post-combat) : les pions gagnés (1 par paysan sauvé) sont affichés
    // en cartes ; un clic les envoie TOUS en réserve (pas de draft « choisir 1 parmi 3 »). Non-null = actif.
    private List<UnitSpec>? _protectReward;
    private float _protectRewardFlight;   // > 0 = vol en cours (les pions filent vers la réserve) ; 0 = attente du clic
    private readonly List<bool> _rewardKeep = new();   // par pion gagné : coché = à récupérer (limité par la place)
    private int _rewardFocus;             // MANETTE : carte de récompense focalisée
    private float _reserveFullFlash;      // > 0 = feedback « plus de place » (récup/recrutement bloqué) en cours
    private UnitSpec? _reserveDrag;        // pion de réserve en cours de DRAG (fusion souris) ; null = aucun

    // Édition de la réserve sur les écrans draft/récompense (plafond de réserve) : un pion sélectionné
    // peut être SUPPRIMÉ ou FUSIONNÉ (3 identiques → évolution choisie) pour faire de la place.
    private UnitSpec? _reserveSel;      // pion de la réserve sélectionné (null = aucun)
    private bool _reserveFuseChoice;    // vrai = on affiche le choix d'évolution pour fusionner le pion sélectionné
    private bool _reserveZone;          // MANETTE : focus dans le panneau réserve (vs sur les cartes)
    private int _reserveFocus;          // MANETTE : indice du pion de réserve focalisé (avant sélection)
    private int _reserveActionFocus;    // MANETTE : indice du bouton d'action / de l'évolution focalisé

    /// <summary>Vrai si la mission courante est « protéger les paysans » (ennemis offensifs les capturent).</summary>
    private bool IsProtectMission => _specialObjective == SpecialObjective.ProtegerPaysans;

    /// <summary>
    /// Vrai si la mission courante est « sauver les paysans » : COURSE — le joueur les récupère (comme Liberer)
    /// pendant que l'IA offensive tente de les capturer (comme Proteger). Sans limite de tours.
    /// </summary>
    private bool IsSauverMission => _specialObjective == SpecialObjective.SauverPaysans;

    /// <summary>Vrai si l'IA offensive peut CAPTURER des paysans dans la mission courante (« protéger » ou « sauver »).</summary>
    private bool AiCapturesPaysans => IsProtectMission || IsSauverMission;

    /// <summary>Vrai si la mission courante impose une limite de tours (toutes sauf « sauver », qui est une course).</summary>
    private bool HasSpecialTurnLimit => _specialMission && !IsSauverMission;

    /// <summary>Paysans RÉSOLUS (tuiles recrue consommées) : libérés (Liberer/Sauver) ou capturés (Proteger/Sauver).</summary>
    private int PaysansResolved => _recrueConsumed.Count;

    /// <summary>Paysans CAPTURÉS par l'IA (sous-ensemble des résolus). Nul hors mission où l'IA capture.</summary>
    private int PaysansCaptured => _recrueCaptured.Count;

    /// <summary>Paysans RÉCUPÉRÉS par le joueur (résolus moins capturés). En Liberer, tous les résolus sont libérés.</summary>
    private int PaysansFreed => PaysansResolved - PaysansCaptured;

    /// <summary>Paysans encore protégés (mission Proteger) : total moins ceux capturés.</summary>
    private int PaysansProtected => PaysansTotal - PaysansResolved;

    /// <summary>Nombre total de paysans sur la map (tuiles recrue).</summary>
    private int PaysansTotal => _recrueCells.Count;

    /// <summary>
    /// Paysans portés à l'actif du joueur : ceux qu'il a EMPÊCHÉ de capturer en « protéger », ceux qu'il a
    /// RÉCUPÉRÉS en « libérer »/« sauver ». C'est ce chiffre que le quota de difficulté compare.
    /// </summary>
    private int PaysansSaved => IsProtectMission ? PaysansProtected : PaysansFreed;

    /// <summary>
    /// Quota de paysans imposé par la difficulté (0 = aucun). Barème DISTINCT par type de mission :
    /// « protéger » et « sauver » sont plus exigeants que « libérer ». PLAFONNÉ au nombre de paysans réellement
    /// présents sur la map : une exigence impossible à tenir serait une défaite garantie.
    /// </summary>
    private int PaysansRequired
    {
        get
        {
            if (_run == null)
                return 0;
            var s = DifficultySettings.For(_run.Difficulty);
            var quota = IsProtectMission ? s.PaysansRequiredProtect
                      : IsSauverMission ? s.PaysansRequiredSave
                      : s.PaysansRequired;
            return System.Math.Min(quota, PaysansTotal);
        }
    }

    /// <summary>Limite de tours effective de la mission spéciale : celle de la map si fixée, sinon le défaut.</summary>
    private int SpecialTurnBudget() => _map is { TurnLimit: > 0 } m ? m.TurnLimit : SpecialTurnLimit;

    // Objets « buisson » (calque "objects") : couvert PERMANENT (non consommé) — un pion DESSUS reçoit
    // -4 dégâts (appliqué dans Match via les cases de couvert). Rendu sous les unités.
    private readonly List<Cell> _bushCells = new();
    private Texture2D? _bushSprite;                          // PNG du buisson (placeholder dessiné si absent)

    // Coffres (objet de map, calque "objects") : un allié qui ENTRE dessus en combat l'ouvre → équipement
    // commun tiré en inventaire de run, puis le coffre est consommé (usage unique). Même détection par
    // transition (absent→présent) que les tuiles recrue. Rendu : simple PNG pour l'instant (anim plus tard).
    private readonly List<Cell> _chestCells = new();
    private readonly HashSet<Cell> _chestConsumed = new();
    private readonly HashSet<Cell> _chestPrev = new();
    private Texture2D? _chestSprite;        // PNG du coffre fermé (placeholder coloré si absent)
    private Texture2D? _chestAnim;          // spritesheet d'ouverture (256×64 = 4 frames de 64×64)

    // Révélation MODALE à l'ouverture d'un coffre (fige le combat), calquée sur la recrue. Machine à phases :
    // Opening (anim du coffre) → Rolling (« machine à sous » : l'objet monte en défilant vite pendant ~3 s puis
    // se fige sur le gagné) → Item (l'objet + nom/rareté, attend le clic) → Fly (vole vers l'inventaire ; ajouté
    // à l'arrivée) → Settle (court répit) → fin. L'item n'entre dans l'inventaire qu'à la fin du vol.
    private enum ChestPhase { None, Opening, Rolling, Item, Fly, Settle }
    private ChestPhase _chestPhase = ChestPhase.None;
    private Equipment? _chestReveal;        // objet GAGNÉ (révélé à la fin, pas encore en inventaire)
    private Equipment? _chestRollItem;      // objet AFFICHÉ pendant le défilement « machine à sous » (change vite)
    private double _chestRollSwapTimer;     // temps depuis le dernier changement d'objet affiché
    private readonly System.Random _chestRollRng = new();
    private double _chestPhaseTimer;        // temps écoulé dans la phase courante
    private Vector2 _chestFlyFrom;          // position de départ du vol (centre de l'objet révélé)
    private bool ChestRevealActive => _chestPhase != ChestPhase.None;
    private const int ChestFrames = 4;
    private const double ChestOpenDuration = 0.6;
    private const double ChestRollDuration = 3.0;    // défilement rapide d'objets avant de se figer (« machine à sous »)
    private const double ChestRollSwapMin = 0.04;    // intervalle entre deux objets AU DÉBUT (rapide)
    private const double ChestRollSwapMax = 0.30;    // … à la FIN (ralenti progressivement)
    private const double ChestRollLockTime = 0.25;   // dernier laps figé sur l'objet gagné avant de le révéler
    private const double ChestFlyDuration = 0.5;
    private const double ChestSettleDuration = 0.6;

    // Feu d'artifice de récompense à la RÉVÉLATION du butin (cf. QueueLootFireworks) : RIEN pour un commun,
    // une petite gerbe pour un RARE, un bouquet de plusieurs GROSSES salves pour un LÉGENDAIRE. Réutilise le
    // système d'étincelles (_sparks) — gerbes radiales soumises à la gravité, dessinées PAR-DESSUS la modale.
    private int _lootBurstsLeft;            // salves restant à tirer (0 = pas de feu d'artifice en cours)
    private double _lootBurstTimer;         // compte à rebours avant la prochaine salve
    private bool _lootBurstBig;             // grosses salves dispersées (légendaire) vs petite gerbe centrée (rare)
    private readonly System.Random _lootFireworkRng = new();
    private const int RareFireworkBursts = 2;              // nombre de petites salves pour un rare
    private const int LegendaryFireworkBursts = 5;         // nombre de salves du bouquet légendaire
    private const double LegendaryFireworkInterval = 0.28; // délai entre deux salves du bouquet

    // Dissolution de l'ÉQUIPEMENT perdu quand une unité équipée meurt en combat (feedback de la perte).
    // Détectée par DISPARITION du plateau (toutes causes de mort confondues), jouée APRÈS la dissolution
    // du pion. Réutilise le shader de dissolution des unités (CombatFxRenderer).
    private sealed class EquipDissolveFx
    {
        public Equipment Equip = null!;
        public Cell Cell;
        public Vector2 Seed;
        public float Delay;   // attend la fin de la dissolution du pion
        public float Time;    // temps après le délai (hold visible puis dissolution)
    }
    private readonly List<EquipDissolveFx> _equipDissolves = new();
    private Dictionary<Unit, Cell> _equippedCells = new();   // snapshot des pions équipés posés (diff → morts)
    private const float EquipDissolveDelay = 0.4f;   // ~ durée de dissolution du pion mort
    private const float EquipDissolveHold = 0.22f;   // l'équipement reste visible un court instant
    private const float EquipDissolveDur = 0.5f;     // puis se dissout

    // Texture de tuile par type de terrain (PNG Assets/Tiles, repli sur un aplat coloré 64×80).
    private readonly Dictionary<string, Texture2D> _tiles = new();
    private WaterRenderer _water = null!;
    private Texture2D _waterNoise = null!;
    private float _time;
    private PauseMenu _pauseMenu = null!;
    private PauseMenuRenderer _pauseRenderer = null!;

    /// <summary>Écran modal de l'arbre de commandement, ouvert depuis le panneau de placement.</summary>
    private CommandTreeView _commandTree = null!;

    /// <summary>Codex (bestiaire des pions + équipements), ouvert par-dessus le menu pause.</summary>
    private CodexView _codex = null!;

    /// <summary>Vrai quand l'arbre de commandement est ouvert : le placement est gelé derrière lui.</summary>
    private bool CommandTreeOpen => _commandTree.IsOpen;

    /// <summary>
    /// Zone LIBRE où centrer la modale de l'arbre : sous la frise des missions, à gauche du panneau
    /// d'inventaire — c'est-à-dire exactement au-dessus du plateau, et non du canvas entier.
    /// </summary>
    private Rectangle CommandTreeArea()
    {
        var top = TimelineTopY + TimelineNodeSize + 16;   // sous les nœuds de la frise
        return new Rectangle(0, top, AvailableWidth(), VirtualViewport.Height - top);
    }

    // Effets de combat shader (dissolution / flash) + animation d'attaque en cours.
    private CombatFxRenderer _combatFx = null!;
    private readonly MeleeStrikeFx _fx = new();
    // Particules poolées : ne servent plus que pour le feu d'artifice d'extinction des chiffres de dégâts.
    private readonly SparkBurst _sparks = new();

    // Tremblement vertical LOCAL des tuiles de l'AoE (« Séisme » à la fin du tour ennemi, « Impact » à
    // l'action d'un porteur) : les cases frappées tressautent de haut en bas (cf. TileTremor / DrawTerrain).
    private readonly TileTremor _tremor = new();
    // Garde-fou « impact traité une seule fois par coup » (spawn du chiffre de dégâts au contact).
    private bool _impactHandled;
    // Chiffres de dégâts flottants (jaillis à l'impact, puis éclatent) + dégâts du coup en attente.
    private readonly DamagePopups _damagePopups = new();
    private int _pendingDamage;
    private int _pendingGiantBonus;   // part « Tueur de géants » du coup en attente : « +N » distinct affiché à l'impact
    private bool _pendingDodge;   // l'attaque en cours a été ESQUIVÉE : feedback dédié à l'impact (popup + son)
    private bool _pendingPhenix;  // la cible du coup a été RESSUSCITÉE (Queue de phénix) : callout dédié à l'impact
    // Orage / Tempête : éclairs sur tous les pions à l'attaque d'un porteur. Cases et chiffres figés
    // AVANT l'attaque (le domaine applique la foudre instantanément), déclenchés à l'impact.
    private readonly StormFx _storm = new();
    private List<Cell>? _pendingStormBolts;                    // pions à foudroyer (visuel)
    private List<(Cell Cell, int Damage)>? _pendingStormHits;  // ennemis touchés + dégâts (chiffres)
    // Impact / Recule (traits d'action) : chiffres de dégâts figés APRÈS l'attaque, déclenchés à l'impact
    // (comme l'orage). Sur un déplacement l'effet est instantané (cf. TryMoveWithFx), pas de report.
    private List<(Cell Cell, int Damage)>? _pendingImpactHits;  // ennemis frappés par l'« Impact » à l'attaque
    private List<Cell>? _pendingImpactZone;                     // zone AoE de l'« Impact » à l'attaque (tremblement des tuiles), reportée à l'impact
    private (Cell Cell, int Damage)? _pendingReculeSlam;        // cible plaquée par le « Recule » (dégât bonus)
    // « Recule » qui a GLISSÉ (pas plaqué) : la victime a changé de case dans le moteur ; on l'anime en la
    // faisant glisser de sa case d'origine (From) vers sa case d'arrivée (To) pendant l'anim d'attaque.
    private (Cell From, Cell To)? _reculeSlide;
    // « Riposte » : contre-attaque DÉJÀ résolue dans le moteur, rejouée en animation APRÈS l'attaque principale
    // (le pion riposteur fente vers l'assaillant + mot « RIPOSTE »). Réutilise _fx une fois l'anim d'attaque finie.
    // Le sprite de l'ASSAILLANT est figé ici (il peut mourir de la riposte) ; celui du riposteur est repris à vif.
    private (Cell From, Cell To, Texture2D? AttackerSprite, bool Killed, AttackStyle Style, int Damage)? _pendingRiposte;
    // « Transpercement » : le pion DERRIÈRE la cible encaisse aussi (déjà résolu par le moteur). Recul + chiffre +
    // mot-clé sont reportés à l'impact de l'attaque, comme un coup encaissé normal. Recul directionnel indépendant
    // de la victime directe (cf. HitRecoil), figé à l'impact et résorbé dans la fenêtre des FX.
    private readonly HitRecoil _pierceRecoil = new();
    private (Cell Cell, int Damage, int Dc, int Dr)? _pendingPierce;
    // « Revoir la dernière action de l'IA » (R clavier / RB manette, pendant le tour du joueur) : instantané de la
    // dernière action ennemie, capturé pour REJOUER son animation par-dessus le plateau courant sans re-toucher le
    // moteur (qui a déjà avancé). Null tant que l'IA n'a pas joué ce combat (ou après un tour PASSÉ).
    private AiReplaySnapshot? _lastAiAction;

    /// <summary>Tout ce qu'il faut pour rejouer l'animation de la dernière action de l'IA (cf. <see cref="_lastAiAction"/>).
    /// Une ATTAQUE rejoue l'anim complète + le feedback PRINCIPAL (dégâts / esquive / phénix) ; un DÉPLACEMENT rejoue le
    /// glissement du pion. Les FX de traits exotiques (orage, impact de zone, transpercement, riposte) ne sont PAS rejoués.</summary>
    private readonly record struct AiReplaySnapshot(
        bool IsAttack, Cell From, Cell To, Cell AttackerCell,
        Texture2D? AttackerSprite, Texture2D? VictimSprite,
        bool Killed, bool Advanced, AttackStyle Style, bool Dodged,
        int Damage, int GiantBonus, bool Phenix,
        (Cell From, Cell To)? ReculeSlide, string Sound);
    // Points de commandement « sur coup reçu » (commandant Lancier) : coups DÉJÀ signalés au joueur ce combat.
    // Sert à afficher un « +N » flottant à chaque coup qui RAPPORTE (sous le plafond OnHitCap), sans doublon —
    // le CRÉDIT réel reste groupé à la clôture (cf. GrantCommanderHitPoints).
    private int _commanderPtHitsShown;
    // Tutoriel « combat zéro » : non-null pendant le combat scénarisé de début de campagne.
    private TutorialGuide? _tutorial;
    private readonly List<Cell> _tutorialMoves = new();   // buffer des coups de l'ennemi scripté du tuto
    private int _tutorialCardIndex;                        // donnée de carte en cours de revue (0..3)
    private const int TutorialCardStats = 5;              // Déplacement (domaine), PV, Puissance, Mouvement, Portée
    private double _tutorialHold;                          // temps de maintien cumulé (leçon « zones de danger »)
    private const double TutorialDangerHoldSeconds = 0.45; // durée de maintien ESPACE/RT pour valider la leçon danger

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
    // action verticale (déplacement/attaque) ; à défaut, selon la MOITIÉ du plateau (cf. DefaultFacesDown).
    private readonly Dictionary<Unit, bool> _facesDown = new();

    // Orientation par DÉFAUT imposée à un ENNEMI par la case de spawn où il est apparu (calque `facing` de la
    // map, cf. MapData.ForcedFacing) : true = vers le bas, false = vers le haut. Capturée au spawn car l'ennemi
    // se déplace ensuite ; sert de défaut à DefaultFacesDown jusqu'à sa première action (comme _facesDown).
    // (Les pions JOUEUR lisent leur orientation forcée sur la case courante ; cf. DefaultFacesDown.)
    private readonly Dictionary<Unit, bool> _enemyForcedFacing = new();

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

    // Sous-phase ÉQUIPEMENT du placement : après placement+fusion, si le joueur a des équipements. La run
    // reste en RunPhase.Placement ; _equipPhase distingue le sous-état. On y pose/retire les équipements
    // sur les pions DÉPLOYÉS (slot au-dessus de la tête) par glisser-déposer depuis le bandeau du panneau.
    private bool _equipPhase;
    private Equipment? _dragEquip;     // équipement porté à la souris (null sinon)
    private UnitSpec? _dragEquipFrom;  // pion d'origine du portage (null = vient du bandeau d'inventaire)
    private int _equipFocus;           // index d'inventaire sous le focus (manette)

    // Fusion (placement) : pile d'unités identiques en cours d'assemblage par empilement. _fusionGroup
    // = toutes les pièces de la pile (vide=rien, 1..N-1=empilement, ==FusionSize=popup de choix).
    // _fusionCell = case du plateau où la pile est ancrée (null = pile dans le panneau de RÉSERVE).
    // _fusionFocus = carte d'évolution sous le focus dans la popup.
    private readonly List<UnitSpec> _fusionGroup = new();
    private Cell? _fusionCell;
    private int _fusionFocus;
    private bool FusionOpen => _fusionGroup.Count > 0 && _fusionGroup.Count == FusionGroupTarget;

    /// <summary>Nombre de pions requis pour fusionner la classe de <paramref name="spec"/> (domaine + tier, cf. Amalgame).</summary>
    private int FusionSizeOf(UnitSpec spec) => _run.FusionSizeFor(spec);

    /// <summary>Taille CIBLE de la pile de fusion en cours (selon la classe de sa pièce), ou 0 si aucune pile.</summary>
    private int FusionGroupTarget => _fusionGroup.Count > 0 ? _run.FusionSizeFor(_fusionGroup[0]) : 0;

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

    // Curseur de plateau (manette) : case visée. En placement, le focus manette traverse TROIS zones —
    // le plateau (curseur), l'inventaire (_gpInventory + _invFocus) et les boutons du panneau
    // (_gpButtons + _btnFocus : 0 = COMMANDEMENT, 1 = COMBATTRE/SUIVANT). RB fait le tour, Bas/Haut
    // enchaîne inventaire → boutons. Les deux drapeaux sont exclusifs (cf. UpdatePlacementGamepad).
    private Cell _cursor = new(4, 7);   // valeur par défaut (réinitialisée par combat dans BeginPlacement)
    private bool _gpInventory;
    private int _invFocus;
    private int _invScrollRow;   // défilement (en rangées) de la grille de réserve quand elle déborde du panneau
    private bool _gpButtons;
    private int _btnFocus;

    /// <summary>
    /// Le bouton COMMANDEMENT n'apparaît en tutoriel qu'à sa propre leçon : c'est PAR LUI que le joueur
    /// ouvre l'arbre, comme en vraie partie. Avant, il n'aurait rien à montrer (zéro point, arbre non
    /// présenté) ; après, la modale le recouvre de toute façon.
    /// </summary>
    private bool ShowCommandTreeButton =>
        _tutorial is null or { Step: TutorialStep.TreeOpen } or { Step: TutorialStep.TreeDo };

    /// <summary>
    /// Le bouton COMBATTRE est là dès les leçons de PLACEMENT du tuto — c'est par lui qu'on lance le combat,
    /// comme dans une vraie partie. Il disparaît pendant la préparation guidée, où le guide pilote la suite.
    /// </summary>
    private bool ShowFightButton => _tutorial is null or { InPlacement: true };

    /// <summary>Rang du bouton COMBATTRE dans la zone de focus manette (il suit COMMANDEMENT quand celui-ci est là).</summary>
    private int FightButtonIndex => ShowCommandTreeButton ? 1 : 0;

    /// <summary>Nombre de boutons focusables du panneau de placement (0 pendant la préparation guidée).</summary>
    private int PlacementButtonCount => (ShowCommandTreeButton ? 1 : 0) + (ShowFightButton ? 1 : 0);

    private Cell? _selected;
    // Buffers RÉUTILISÉS (remplis par les variantes sans-alloc du Match) : évitent une allocation
    // de liste à chaque sélection / chaque frame de survol.
    private readonly List<Cell> _legalMoves = new();
    private readonly List<Cell> _attackTargets = new();   // cases avec un ennemi réellement à portée
    private readonly List<Cell> _attackReach = new();     // toute la PORTÉE de tir (cases atteintes, même vides)
    private readonly List<Cell> _healTargets = new();     // trait « Soin » : alliés blessés à portée, ciblables pour soigner
    private readonly List<Cell> _threatCells = new();
    private readonly HashSet<Cell> _enemyThreatSet = new();   // cases menacées par ≥ 1 ennemi (icône « ! » sur les alliés)
    private readonly List<Cell> _auraCarriers = new();        // porteurs d'une même famille d'aura (barrière fusionnée)
    private readonly HashSet<Cell> _auraCells = new();        // union des cases couvertes par la famille (contour partagé)
    // Aperçu au SURVOL d'un pion joueur (rien de sélectionné) : buffers distincts de la sélection.
    private readonly List<Cell> _hoverMoves = new();
    private readonly List<Cell> _hoverAttackTargets = new();
    private readonly List<Cell> _hoverReach = new();
    private readonly List<Cell> _hoverHealTargets = new();
    // Cases dont le pion tremblote légèrement en aperçu (cf. DrawUnit / TargetTremble) : les ENNEMIS ciblables
    // par le pion sélectionné/survolé ET les ALLIÉS MENACÉS (à portée d'un ennemi). Rempli à chaque frame par
    // DrawHighlights.
    private readonly HashSet<Cell> _trembleTargets = new();
    private double _aiTimer;
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
    // DÉZOOM (un cran EN DESSOUS du cadrage) : symétrique du zoom avant, mais board-only comme lui — seule la
    // CASE du plateau rétrécit (÷2 : 64→32 px), l'UI ne bouge pas. Sous la taille native, l'art perd la moitié
    // de son détail. Exclusif avec _zoomedIn (3 niveaux : −1 dézoom / 0 cadrage / +1 zoom).
    // Le dézoom n'affecte QUE la taille de case du plateau (LAYOUT) ; l'UI, l'input et le hit-test restent en
    // coords NORMALES → tout fonctionne. Le RENDU du plateau, lui, passe par une couche NATIVE recomposée
    // nette (cf. DrawDezoomLayers) pour ne PAS être « chunky ».
    private bool _dezoomedOut;
    // Couches de dézoom (allouées à la volée) : le plateau natif (net) + l'UI, recomposés par la couche Game
    // par-dessus l'eau (cf. TryGetDezoomLayers / ChessArmyGame). Le rendu HORS dézoom ne les touche pas.
    private Microsoft.Xna.Framework.Graphics.RenderTarget2D? _boardTarget;
    private Microsoft.Xna.Framework.Graphics.RenderTarget2D? _uiTarget;
    private Microsoft.Xna.Framework.Graphics.RenderTarget2D? _ghostTarget;   // pion attrapé (couche curseur, PAR-DESSUS tout)
    private Rectangle _boardTargetDest;   // où (écran) recomposer le plateau natif ×1
    private Rectangle _ghostDest;         // où (écran, ×1) dessiner le pion attrapé — suit la souris
    private bool _ghostReady;             // vrai si un pion est attrapé cette frame
    private bool _dezoomLayersReady;      // vrai la frame où les couches viennent d'être remplies
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
    private const float ChestShadowShear = 0.28f;     // coffre : objet massif → cisaillement plus doux, ombre moins débordante à droite
    private const float ShadowFlatten = -0.45f;       // < 0 : rabat la silhouette au sol vers l'avant + aplatit
    private const float ShadowAlpha = 0.60f;          // opacité de l'ombre (au sol)
    private const float ShadowAnchorFraction = 0.94f; // hauteur de la base du socle dans le sprite (0 haut … 1 bas)
    // Réaction au soulèvement : quand le pion est en l'air, l'ombre GLISSE (direction lumière) et S'ÉCLAIRCIT.
    private const float ShadowLiftSlide = 0.85f;      // px de glissement de l'ombre par px de soulèvement
    private const float ShadowLiftFade = 0.5f;        // part d'opacité perdue à pleine hauteur (0 = aucune)
    // Cache des silhouettes TRAMÉES (pixel-art) par sprite : l'ombre est un motif Bayer de pixels pleins
    // plutôt qu'un aplat semi-transparent lissé (cf. Textures.CreateShadowStipple). Disposé à l'Unload.
    private readonly Dictionary<Texture2D, Texture2D> _shadowStipple = new();

    // Slot de sauvegarde piloté depuis le menu principal : la progression est auto-sauvegardée en
    // phase de placement et le slot est effacé à la fin de la run (victoire/défaite).
    private readonly int _saveSlot;
    private Run? _initialRun;

    // Choix faits sur l'écran de sélection du commandant (nouvelle partie uniquement) : ignorés à la reprise,
    // où le commandant et la difficulté viennent de la sauvegarde.
    private readonly CommandeDef? _chosenCommander;
    private readonly Difficulty _chosenDifficulty;

    /// <param name="saveSlot">Index du slot (0..2) où sauvegarder la progression.</param>
    /// <param name="run">Run à reprendre (depuis une sauvegarde), ou null pour une nouvelle partie.</param>
    /// <param name="commander">Commandant choisi pour une NOUVELLE partie (null → le commandant par défaut).</param>
    /// <param name="difficulty">Difficulté choisie pour une NOUVELLE partie.</param>
    public GameplayScene(GameContext context, int saveSlot, Run? run = null,
        CommandeDef? commander = null, Difficulty difficulty = Difficulty.Normal) : base(context)
    {
        _saveSlot = saveSlot;
        _initialRun = run;
        _chosenCommander = commander;
        _chosenDifficulty = difficulty;
    }

    /// <summary>Viewport logique (espace virtuel) dans lequel l'UI se met en page.</summary>
    private Viewport VirtualViewport =>
        new(0, 0, Context.VirtualResolution.X, Context.VirtualResolution.Y);

    public override void Load()
    {
        LoadTiles();
        LoadMaps();
        // Coffre : PNG fermé (plateau) + spritesheet d'ouverture (révélation). Placeholders si absents.
        _chestSprite = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath("Assets/Objects/coffre.png"));
        _chestAnim = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath("Assets/Objects/coffreAnimate.png"));
        // Objets recrue (pion « ? ») et buisson : PNG optionnels, repli sur un placeholder dessiné.
        LoadRecrueSprites();   // tous les looks recrue (Assets/Objects/*_front.png) : une variante par case
        _bushSprite = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath("Assets/Objects/buisson.png"));
        _equipSlotBg = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath("Assets/Equipment/background.png"));
        _rerollIcon = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath("Assets/UI/relance.png"));
        _recycleIcon = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath("Assets/UI/recycler.png"));
        _water = LoadWater();

        var native = Context.GraphicsDevice.Adapter.CurrentDisplayMode;
        // « Recommencer la mission » disparaît du menu pause si la difficulté l'interdit (Difficile : run sans filet).
        _pauseMenu = new PauseMenu(Context.Settings, new Point(native.Width, native.Height),
            allowRestart: DifficultySettings.For(_chosenDifficulty).AllowRestart);
        _pauseRenderer = new PauseMenuRenderer(Context.Pixel, Context.Font, Context.Style);
        _commandTree = new CommandTreeView(Context);
        _codex = new CodexView(Context);
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
        _boardTarget?.Dispose(); _uiTarget?.Dispose(); _ghostTarget?.Dispose();   // couches de dézoom
        _hoverCardTarget?.Dispose();   // couche de fondu des cartes-tooltips de survol
        _commandTree.Unload();
        _codex.Unload();
        foreach (var sprite in _unitSprites.Values)
            sprite?.Dispose();
        _unitSprites.Clear();
        _chestSprite?.Dispose();
        _chestSprite = null;
        _chestAnim?.Dispose();
        _chestAnim = null;
        foreach (var (front, back) in _recrueLooks)
        {
            front.Dispose();
            back?.Dispose();
        }
        _recrueLooks.Clear();
        _bushSprite?.Dispose();
        _bushSprite = null;
        _equipSlotBg?.Dispose();
        _equipSlotBg = null;
        foreach (var sprite in _equipSprites.Values)
            sprite?.Dispose();
        _equipSprites.Clear();
        foreach (var stipple in _shadowStipple.Values)
            stipple?.Dispose();
        _shadowStipple.Clear();
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
        _maps.Clear();
        try
        {
            var tilesJson = System.IO.File.ReadAllText(AssetPath("Assets/Tiles/tiles.json"));
            _catalog = TileCatalog.FromJson(tilesJson);
            LoadTilesets(tilesJson);
        }
        catch
        {
            _catalog = null;   // pas de catalogue → aucune map (terrain aléatoire partout)
            return;
        }

        var dir = AssetPath("Assets/Maps");
        if (!System.IO.Directory.Exists(dir))
            return;
        foreach (var file in System.IO.Directory.GetFiles(dir, "*.json"))
        {
            try { _maps.Add(MapLoader.Parse(System.IO.File.ReadAllText(file), _catalog)); }
            catch { /* map mal formée : ignorée, les autres restent chargées */ }
        }
    }

    /// <summary>
    /// Taille (côté du plateau) d'une mission (phase, mission), lue depuis le plan de campagne
    /// (<see cref="CampaignPlan"/> → <c>Assets/Config/campaign.json</c>, champ <c>mapSize</c>).
    /// Défauts : 6×6 en phase 1 (sauf missions 4-5 = 7×7), 7×7 en phase 2, 8×8 en phase 3.
    /// </summary>
    private static int MapSizeFor(int phaseIndex, int missionInPhase) =>
        ChessArmy.Core.Campaign.CampaignPlan.For(phaseIndex, missionInPhase).MapSize;

    /// <summary>
    /// Map à utiliser pour le combat courant. ESCARMOUCHE : cf. <see cref="EscarmoucheMapFor"/> (pool de la
    /// taille de la phase, élargi aux 9×9/10×10 dès la phase 3, en évitant les répétitions dans la run), sinon
    /// null = terrain aléatoire. BOSS : tirage par PHASE (cf. <see cref="BossMapFor"/>), sans tenir compte de la
    /// taille — c'est la map qui impose la taille du plateau. MISSION SPÉCIALE : tirage ALÉATOIRE parmi les maps
    /// <see cref="CombatType.Speciale"/> RÉSERVÉES À LA PHASE courante (<see cref="MapData.Phase"/> == phase) ou
    /// marquées « toutes phases » (Phase == 0), la TAILLE venant aussi de la map. Tirage DÉTERMINISTE (stable si
    /// on reprend le combat) mais qui varie d'une run à l'autre.
    /// </summary>
    private MapData? MapForCombat()
    {
        if (_run.CurrentMission == CombatType.Speciale)
            return SpecialMapFor(_run.PhaseIndex, _run.MissionInPhase);

        if (_run.CurrentMission == CombatType.Boss)
            return BossMapFor(_run.PhaseIndex, _run.MissionInPhase);

        return EscarmoucheMapFor(_run.CombatNumber);
    }

    /// <summary>
    /// Map d'un combat ESCARMOUCHE. Pool = maps carrées de type <see cref="CombatType.Escarmouche"/> à la
    /// TAILLE attendue par la mission (cf. <see cref="MapSizeFor"/>), ÉLARGI aux 9×9 et 10×10 dès la PHASE 3.
    /// On évite AU MAXIMUM de retomber sur une map déjà sortie dans la run : le tirage est REJOUÉ depuis le
    /// 1er combat en accumulant les maps utilisées et en les sautant ; pool éligible épuisé → on autorise la
    /// répétition. Pure fonction de (graine, <paramref name="combatNumber"/>) → stable si on reprend la partie.
    /// Null (aucune map éligible) = terrain aléatoire (cf. l'appelant).
    /// </summary>
    private MapData? EscarmoucheMapFor(int combatNumber)
    {
        var used = new HashSet<MapData>();
        MapData? chosen = null;
        for (var n = 1; n <= combatNumber; n++)
        {
            var phase = (n - 1) / Run.MissionsPerPhase + 1;
            var mission = (n - 1) % Run.MissionsPerPhase + 1;
            if (Run.MissionKindAt(phase, mission) != CombatType.Escarmouche)
                continue;
            var pool = ShuffledEscarmouchePool(phase, mission);
            chosen = pool.Count == 0
                ? null
                : pool.FirstOrDefault(m => !used.Contains(m)) ?? pool[(n - 1) % pool.Count];
            if (chosen != null)
                used.Add(chosen);
        }
        return chosen;
    }

    /// <summary>
    /// Pool d'escarmouches éligibles pour un combat, mélangé de façon DÉTERMINISTE (graine de run) : maps
    /// carrées de type <see cref="CombatType.Escarmouche"/> à la taille de la mission (cf.
    /// <see cref="MapSizeFor"/>), plus 9×9 et 10×10 dès la phase 3.
    /// </summary>
    private List<MapData> ShuffledEscarmouchePool(int phaseIndex, int missionInPhase)
    {
        var size = MapSizeFor(phaseIndex, missionInPhase);
        var pool = _maps.Where(m => m.Type == CombatType.Escarmouche && m.Width == m.Height
            && (m.Width == size || (phaseIndex >= 3 && (m.Width == 9 || m.Width == 10)))).ToList();
        var rng = new System.Random(unchecked(_run.Seed * 6151 + 4243));
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool;
    }

    /// <summary>
    /// Map d'un combat BOSS : tirage parmi les maps de type <see cref="CombatType.Boss"/> RÉSERVÉES à la
    /// phase courante (<see cref="MapData.Phase"/> == phase, ou « toutes phases » Phase == 0). Faute de map
    /// pour cette phase, repli sur N'IMPORTE QUELLE map Boss (au hasard, toutes phases confondues). Aucune
    /// map Boss du tout → null = terrain aléatoire. Contrairement aux escarmouches, la TAILLE n'entre PAS
    /// dans le tri : c'est la map choisie qui impose la taille du plateau (cf. appelant). Déterministe
    /// (graine de run + rang du combat) — sert au combat courant ET à la frise —, mais varie d'une run à l'autre.
    /// </summary>
    private MapData? BossMapFor(int phaseIndex, int missionInPhase)
    {
        var combatNumber = (phaseIndex - 1) * Run.MissionsPerPhase + missionInPhase;
        var all = MapsOfType(CombatType.Boss);
        var ofPhase = all.Where(m => m.Phase == phaseIndex || m.Phase == 0).ToList();
        return PickMap(ofPhase.Count > 0 ? ofPhase : all, phaseIndex, combatNumber);
    }

    /// <summary>
    /// Nombre d'escortes d'un boss sur map dessinée : une par case de spawn ennemie (B + E) SAUF celle
    /// occupée par le boss lui-même. Ainsi boss + escortes = exactement le nombre de cases dessinées.
    /// </summary>
    private static int BossEscortCount(MapData map) =>
        System.Math.Max(0, map.BossSpawns.Count + map.EnemySpawns.Count - 1);

    /// <summary>
    /// Map d'une mission SPÉCIALE au rang (<paramref name="phaseIndex"/>, <paramref name="missionInPhase"/>) :
    /// tirage ALÉATOIRE parmi les maps <see cref="CombatType.Speciale"/> réservées à la phase
    /// (<see cref="MapData.Phase"/> == phase) ou « toutes phases » (Phase == 0), repli sur une escarmouche
    /// si aucune spéciale éligible. Déterministe (graine de run + rang du combat). Sert au combat courant
    /// (via <see cref="MapForCombat"/>) ET à la frise (l'effectif = nb de spawns de la map tirée).
    /// </summary>
    private MapData? SpecialMapFor(int phaseIndex, int missionInPhase)
    {
        var combatNumber = (phaseIndex - 1) * Run.MissionsPerPhase + missionInPhase;
        var size = MapSizeFor(phaseIndex, missionInPhase);
        var specials = MapsOfType(CombatType.Speciale)
            .Where(m => m.Phase == phaseIndex || m.Phase == 0)   // réservées à cette phase, ou « toutes phases »
            .ToList();
        return PickMap(specials, size, combatNumber)
            ?? PickMap(MatchingMaps(CombatType.Escarmouche, size), size, combatNumber);
    }

    /// <summary>Maps chargées du type et de la taille (côté carré) demandés.</summary>
    private List<MapData> MatchingMaps(CombatType type, int size) =>
        _maps.Where(m => m.Type == type && m.Width == size && m.Height == size).ToList();

    /// <summary>Toutes les maps chargées d'un type (toutes tailles confondues).</summary>
    private List<MapData> MapsOfType(CombatType type) =>
        _maps.Where(m => m.Type == type).ToList();

    /// <summary>
    /// Choisit une map dans <paramref name="matches"/> par permutation DÉTERMINISTE (graine de run + taille,
    /// sel 2 — même logique que terrain/vague), puis index par numéro de combat. Null si la liste est vide.
    /// </summary>
    private MapData? PickMap(List<MapData> matches, int size, int? combatNumber = null)
    {
        if (matches.Count == 0)
            return null;
        var rng = new System.Random(unchecked(_run.Seed * 6151 + size * 1031 + 2));
        for (var i = matches.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (matches[i], matches[j]) = (matches[j], matches[i]);
        }
        return matches[((combatNumber ?? _run.CombatNumber) - 1) % matches.Count];
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

            // Une tuile peut lister plusieurs cellules dans "variants" : le rendu en tire une au hasard
            // (stable par case). Sans "variants", on retombe sur la cellule unique col/row.
            var cells = t.Variants is { Count: > 0 }
                ? t.Variants
                : new List<CellRefDto> { new() { Col = t.Col, Row = t.Row } };
            _tileVariants[t.Id] = cells
                .Select(c => new Rectangle(c.Col * sheet.CellW, c.Row * sheet.CellH, sheet.CellW, sheet.CellH))
                .ToList();
        }
    }

    /// <summary>
    /// Texture + rectangle source d'une tuile pour une case donnée : sa cellule dans la feuille si elle y
    /// est (une variante tirée au hasard mais STABLE par case si la tuile en déclare plusieurs), sinon un
    /// PNG individuel pris en entier (legacy grass/water/mountain, ou repli placeholder).
    /// </summary>
    private (Texture2D Texture, Rectangle Source) TileSprite(string id, Cell cell)
    {
        if (_tileSheet.TryGetValue(id, out var sheetName) && _sheets.TryGetValue(sheetName, out var sheet)
            && _tileVariants.TryGetValue(id, out var variants) && variants.Count > 0)
        {
            var src = variants.Count == 1 ? variants[0] : variants[VariantIndex(id, cell, variants.Count)];
            return (sheet, src);
        }

        var tex = TileTexture(id);
        return (tex, new Rectangle(0, 0, tex.Width, tex.Height));
    }

    /// <summary>
    /// Indice de variante STABLE par case (hash déterministe id + colonne + rangée) : varie le rendu d'une
    /// case à l'autre sans scintiller d'une frame à l'autre (même map = même motif à chaque partie).
    /// </summary>
    private static int VariantIndex(string id, Cell cell, int count)
    {
        unchecked
        {
            uint h = 2166136261u;                              // FNV-1a
            foreach (var c in id) { h ^= c; h *= 16777619u; }
            h ^= (uint)cell.Column; h *= 16777619u;
            h ^= (uint)cell.Row;    h *= 16777619u;
            return (int)(h % (uint)count);
        }
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
        public List<CellRefDto>? Variants { get; set; }   // plusieurs cellules possibles → tirage stable par case
    }

    private sealed class CellRefDto
    {
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
            _run = new Run(firstRun: firstRun, commander: _chosenCommander, difficulty: _chosenDifficulty);
        }
        _initialRun = null;                // ne sert qu'au tout premier chargement de la scène
        // Priorité de tirage du boss de dernière phase : le jeu privilégie un boss qui débloquerait un
        // commandant encore verrouillé (cf. Bosses.AssignForRun). À poser AVANT tout combat de boss.
        _run.SetUnlockedCommanders(Context.Saves.UnlockedCommanders());

        // Nouvelle campagne → tutoriel « combat zéro » (skippable). Reprise → direct au combat réel.
        if (resumed)
            BeginPlacement();
        else
            BeginTutorial();
    }

    /// <summary>Prépare la phase de placement : nouveau terrain, commandant posé d'office.</summary>
    private void BeginPlacement()
    {
        _protectReward = null;   // écran de récompense « protéger » du combat précédent : soldé
        _protectRewardFlight = 0f;
        _specialRecap = null;    // bilan de la mission précédente : soldé
        _reserveSel = null;
        _reserveFuseChoice = false;
        _reserveZone = false;
        _reserveFocus = 0;
        _reserveActionFocus = 0;
        _reserveDrag = null;
        _reserveFullFlash = 0f;
        _rewardKeep.Clear();
        _rewardFocus = 0;
        // Taille du plateau selon (phase, mission) — cf. MapSizeFor : phase 1 = 6×6, sauf missions 4-5 = 7×7 ;
        // phase 2 = 7×7 ; phase 3 = 8×8. Map dessinée de cette taille si dispo, sinon terrain aléatoire de même taille.
        var size = MapSizeFor(_run.PhaseIndex, _run.MissionInPhase);
        _map = MapForCombat();
        if (_map is { } map)
        {
            Columns = map.Width;
            Rows = map.Height;
            _battlefield = Battlefield.FromMap(map);
        }
        else
        {
            Columns = size;
            Rows = size;
            _battlefield = _run.BuildBattlefield(Columns, Rows);
        }
        // Objets de la map (calque "objects") : buissons (couvert permanent), recrues (pion « ? »),
        // coffres communs. Les buissons doivent être recensés AVANT le Match (ils modifient les dégâts).
        _bushCells.Clear();
        _recrueCells.Clear();
        _chestCells.Clear();
        if (_map is { } cm)
            foreach (var o in cm.Objects)
                switch (o.Kind)
                {
                    case MapObjectKind.Bush: _bushCells.Add(o.Cell); break;
                    case MapObjectKind.Recruit: _recrueCells.Add(o.Cell); break;
                    case MapObjectKind.ChestCommon: _chestCells.Add(o.Cell); break;
                }

        // Mission spéciale = map Speciale avec un sous-objectif (Liberer/Proteger paysans). En mode objectif,
        // éliminer tous les ennemis ne gagne PAS le combat : le joueur poursuit son objectif (seule la chute
        // du commandant reste une défaite — cf. CheckBattleEnd).
        _specialObjective = _map is { Type: CombatType.Speciale } sm ? sm.Objective : SpecialObjective.Aucun;
        _specialMission = _specialObjective != SpecialObjective.Aucun;
        // Mission « protéger » : le joueur ne peut pas se poser sur les cases paysan (il les défend, ne les
        // squatte pas) ; les ennemis, eux, y vont pour les capturer.
        var playerBlocked = IsProtectMission ? _recrueCells : null;
        // Bonus d'arbre qui règlent le MOTEUR : « Rempart renforcé » / « Esquive renforcée » (cf. Run + Match).
        _match = new Match(Columns, Rows, _battlefield, _bushCells,
            eliminationEndsGame: !_specialMission, playerBlockedCells: playerBlocked,
            rempartBonus: _run.RempartBonus, esquiveBonusPercent: _run.EsquiveBonusPercent,
            tueurGeantsBonus: _run.TueurDeGeantsBonus, formationBonus: _run.FormationBonus);
        // Mission spéciale : briefing détaillé en modale d'ouverture (l'encart sous la frise n'en garde
        // que le rappel une fois refermé — cf. DrawSpecialBriefingModal / DrawSpecialBriefing).
        _specialBriefOpen = _specialMission;

        // Effet d'émergence : les tuiles sortent de l'eau (fondu + remontée), en cascade (cf. BoardIntroAnim).
        _boardIntro = 0f;
        _boardIntroTotal = Columns * Rows * BoardIntroStagger + BoardIntroRise;

        // Réinitialise l'état déclencheur des objets recrue / coffre (détection « entre dessus »).
        _recrueConsumed.Clear();
        _recrueCaptured.Clear();
        _recruePrev.Clear();
        _recrueReveal = null;
        _recrueAdded = false;
        _recrueSettle = 0f;
        _chestConsumed.Clear();
        _chestPrev.Clear();
        _chestReveal = null;
        _chestRollItem = null;
        _chestRollSwapTimer = 0;
        _chestPhase = ChestPhase.None;
        _chestPhaseTimer = 0;
        _lootBurstsLeft = 0;   // pas de salve de récompense reportée du combat précédent
        _equipDissolves.Clear();
        _equippedCells = new Dictionary<Unit, Cell>();   // snapshot vidé : pas de fausse mort au 1er combat frame

        // Sous-phase Équipement : on (re)part toujours sur le placement normal.
        _equipPhase = false;
        _dragEquip = null;
        _dragEquipFrom = null;
        _equipFocus = 0;
        _facesDown.Clear();
        _enemyForcedFacing.Clear();
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
        _storm.Clear();
        _tremor.Clear();
        _pendingStormBolts = null;
        _pendingStormHits = null;
        _pendingImpactHits = null;
        _pendingImpactZone = null;
        _pendingReculeSlam = null;
        _reculeSlide = null;
        _pendingRiposte = null;
        _pendingPierce = null;
        _pierceRecoil.Clear();
        _commanderPtHitsShown = 0;
        _sparks.Clear();
        ClearSelection();
        ResetCamera();
        _aiTimer = 0;
        _recruitChoice = null;   // fin d'un éventuel vol de recrutement
        _recruitHold = 0;
        var commanderCell = CommanderStart();
        _cursor = commanderCell;       // curseur manette sur la case de départ du commandant
        _gpInventory = false;
        _gpButtons = false;
        _invScrollRow = 0;             // réserve remise en haut à chaque nouveau placement

        var commander = _run.Commander;
        PlacePlayer(commander, commanderCell);

        foreach (var spec in _run.Roster)
            if (spec != commander)
                _pending.Add(spec);

        // La vague ennemie est posée dès le placement : le joueur voit le déploiement
        // adverse avant de positionner ses pièces (rangées 0-1, hors zone joueur). MISSION SPÉCIALE :
        // l'effectif = EXACTEMENT le nombre de spawns dessinés sur la map (pas l'effectif fixe de la table).
        // La vague pioche ses T2/T3 UNIQUEMENT parmi les classes ÉLIGIBLES à l'IA (découvertes + nouveauté de
        // la run, cf. Run.PickEnemy / AiFreshFor). Rien n'est découvert AVANT le combat : la découverte se fait
        // à l'APPARITION, une fois la vague posée (plus bas).
        List<UnitSpec> wave;
        if (_specialMission && _map is { } spMap)
            // Tiers FIXÉS par la map (calque « tiers » de l'éditeur) s'ils existent, sinon gabarit campaign.json.
            wave = _run.BuildSpecialEnemyWave(spMap.EnemySpawns.Count, Context.Saves.IsUnitDiscovered, spMap.EnemyTiers);
        else if (_run.IsBossCombat && _map is { } bossMap)
            // Boss sur map dessinée : le boss + une escorte par case de spawn restante → toutes occupées.
            wave = _run.BuildBossEnemyWave(BossEscortCount(bossMap), Context.Saves.IsUnitDiscovered, bossMap.EnemyTiers);
        else
            wave = _run.BuildEnemyWave(Context.Saves.IsUnitDiscovered);
        PlaceEnemies(wave);

        // Découverte À L'APPARITION (méta-progression) : tout pion ennemi RÉELLEMENT placé — vague, escortes
        // ET boss — passe au codex. C'est la SEULE voie de découverte des T2/T3 côté IA : une classe rendue
        // éligible « nouveauté » mais jamais alignée reste inconnue. Idempotent (écrit sur disque à la 1re fois).
        foreach (var spec in wave)
            Context.Saves.DiscoverUnit(spec.UnitClass.Asset);

        // Les tier 1 débloqués (même absents de CETTE vague) restent « vus » pour que la tuile recrue puisse
        // les proposer à tout moment, y compris dans les runs suivantes (cf. RollSeenTier1). Idempotent.
        foreach (var asset in _run.UnlockedTier1Assets())
            Context.Saves.DiscoverUnit(asset);

        // Auto-sauvegarde : la progression n'est persistée qu'ici (phase de placement), jamais en
        // plein combat — on reprend toujours proprement au placement du combat courant. L'instantané
        // (RunSave.From) est pris ICI, sur le thread de jeu ; l'écriture disque part en arrière-plan pour
        // ne pas figer la frame où démarre l'émergence des tuiles (cf. SaveSlotAsync).
        // Le MÊME instantané sert à « Recommencer » (menu pause). Il est pris ici et nulle part ailleurs,
        // exactement comme la sauvegarde : c'est l'état d'ENTRÉE dans la mission, avant tout placement,
        // équipement, déplacement ou perte. On le garde en mémoire plutôt que de relire le disque — pas de
        // course avec l'écriture asynchrone, et ça marche même si le slot n'a pas encore été écrit.
        var snapshot = RunSave.From(_run);
        _missionStart = snapshot;
        Context.Saves.SaveSlotAsync(_saveSlot, snapshot);
    }

    /// <summary>
    /// État de la run à l'ENTRÉE de la mission courante, pour « Recommencer ». Null tant qu'aucune phase de
    /// placement n'a eu lieu (tutoriel) : l'option est alors sans effet.
    /// </summary>
    private RunSave? _missionStart;

    /// <summary>
    /// Rejoue la mission courante depuis son état d'entrée : on reconstruit la scène avec une run neuve
    /// issue de <see cref="_missionStart"/>. Terrain, vague ennemie et équipement ennemi sont regénérés à
    /// l'identique (tout dérive de la graine), et les pions tombés sont de retour puisque les pertes ne sont
    /// appliquées à la run qu'à la fin d'un combat gagné.
    /// </summary>
    private void RestartMission()
    {
        // Filet de sécurité : la difficulté peut interdire de recommencer (le bouton est alors absent du menu).
        if (!DifficultySettings.For(_chosenDifficulty).AllowRestart)
            return;
        if (_missionStart is not { } start)
            return;   // tutoriel : aucune phase de placement n'a eu lieu, rien à rejouer
        // Uniquement TANT QU'ON Y EST : en recrutement, la mission est déjà gagnée et « recommencer »
        // annulerait la victoire — ce serait ressenti comme un bug plutôt que comme un choix.
        if (_run.Phase is not (RunPhase.Placement or RunPhase.Battle))
            return;
        Context.Scenes.Change(new GameplayScene(Context, _saveSlot, start.ToRun()));
    }

    /// <summary>
    /// Prépare le TUTORIEL « combat zéro » : board plat, scénario fixe (commandant + 1 soldat joueur,
    /// 1 soldat ennemi à 2 cases), pas de phase de placement, ennemi passif, AUCUNE sauvegarde.
    /// On passe direct en phase Battle pour réutiliser toute la boucle/le rendu de combat.
    /// </summary>
    private void BeginTutorial()
    {
        // Map DESSINÉE du tuto (6×6 d'herbe, type Tutoriel → jamais tirée par la campagne). Absente ou
        // illisible : repli sur un board plat de même taille, le tuto reste jouable.
        _map = _maps.FirstOrDefault(m => m.Type == CombatType.Tutoriel);
        if (_map is { } tutoMap)
        {
            Columns = tutoMap.Width;
            Rows = tutoMap.Height;
            _battlefield = Battlefield.FromMap(tutoMap);
        }
        else
        {
            Columns = Rows = 6;
            _battlefield = Battlefield.CreateFlat(Columns, Rows);   // herbe partout, aucun obstacle
        }
        _boardIntro = _boardIntroTotal = 0f;                    // pas d'animation d'assemblage en tutoriel
        _bushCells.Clear();
        _recrueCells.Clear();
        _chestCells.Clear();
        _chestConsumed.Clear();
        _chestPrev.Clear();
        // Le seul objet de la map du tuto : le coffre de la leçon « équipement ».
        if (_map is { } withObjects)
            foreach (var o in withObjects.Objects.Where(o => o.Kind == MapObjectKind.ChestCommon))
                _chestCells.Add(o.Cell);
        _match = new Match(Columns, Rows, _battlefield);
        _facesDown.Clear();
        _enemyForcedFacing.Clear();
        _playerSpec.Clear();
        _enemySpec.Clear();
        _enemyKillOrder.Clear();
        _pending.Clear();
        _dragSpec = null;
        _dragFrom = null;
        _damagePopups.Clear();
        _storm.Clear();
        _tremor.Clear();
        _pendingStormBolts = null;
        _pendingStormHits = null;
        _pendingImpactHits = null;
        _pendingImpactZone = null;
        _pendingReculeSlam = null;
        _reculeSlide = null;
        _pendingRiposte = null;
        _pendingPierce = null;
        _pierceRecoil.Clear();
        _commanderPtHitsShown = 0;
        _sparks.Clear();
        ClearSelection();
        ResetCamera();
        _aiTimer = 0;
        _recruitChoice = null;
        _recruitHold = 0;
        _gpInventory = false;
        _gpButtons = false;
        _battleIntroTimer = 0;
        _tutorialHold = 0;

        // Commandant déjà posé (montre l'unité essentielle), 1 SOLDAT à déployer dans l'inventaire.
        var commanderCell = new Cell(Columns / 2, Rows - 1);
        PlacePlayer(_run.Commander, commanderCell);
        _pending.Add(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));

        // 1 Soldat ennemi NORMAL (12 PV, dégâts 10) : il survit à la 1re attaque et contre-attaque.
        var enemyCell = new Cell(Columns / 2, 1);
        _match.Place(enemyCell, new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass).Spawn(Faction.Enemy));

        _tutorial = new TutorialGuide
        {
            Commander = commanderCell,
            EnemySoldier = enemyCell,
            Chest = _chestCells.Count > 0 ? _chestCells[0] : null,
        };
        _cursor = commanderCell;        // curseur manette sur la zone joueur
        MarkLayoutDirty();
        // Reste en phase PLACEMENT (pas de StartBattle, pas de sauvegarde) : le tuto guide le placement.
    }

    /// <summary>
    /// Fin du tutoriel (victoire OU skip) : enchaîne sur le vrai combat 1. La préparation guidée a PRÊTÉ au
    /// joueur des soldats, un équipement et des points de commandement ; <see cref="Run.Reset"/> rend la
    /// run à son état de départ (commandant + ses pions de départ, rien d'autre) pour que le tuto ne fuite
    /// pas dans l'équilibrage. C'est aussi lui qui repasse la run en phase de placement.
    /// </summary>
    private void EndTutorial()
    {
        _tutorial = null;
        _commandTree.Close();
        _equipPhase = false;
        ClearSelection();
        _run.Reset();               // rend les prêts du tuto : roster, équipement, points de commandement
        BeginPlacement();           // 1re sauvegarde de la run a lieu ici (combat réel), jamais pendant le tuto
    }

    /// <summary>
    /// Bascule du combat guidé vers la PRÉPARATION guidée : on revient en phase de placement sur la même map,
    /// le cadavre ennemi est retiré, et on PRÊTE au joueur un 3ᵉ soldat (pour atteindre les
    /// <see cref="Run.FusionSize"/> exemplaires d'une fusion). L'équipement, lui, a été gagné pour de vrai au
    /// coffre (<see cref="TutorialStep.Chest"/>), et les points de commandement viendront de la fusion
    /// (cf. <see cref="Battle.CommandeDef.FusionPoints"/>). Tout est rendu par <see cref="EndTutorial"/>.
    /// </summary>
    private void BeginTutorialPreparation()
    {
        _run.ReturnToPlacement();
        _battleIntroTimer = 0;
        _equipPhase = false;
        ClearSelection();

        // Le plateau ne garde que le commandant : l'ennemi est mort, le soldat du combat a fait son office.
        foreach (var (cell, unit) in _match.Units().Where(u => !u.Unit.IsEssential).ToList())
        {
            _match.Remove(cell);
            _playerSpec.Remove(unit);
        }

        // Prêt du tuto : la leçon de fusion doit toujours être réalisable, quel que soit le commandant CHOISI
        // (LE FOUDROYEUR démarre avec 1 Lancier + 1 Soldat, LE BASTION avec 2 Lanciers...). On force donc la
        // réserve à EXACTEMENT FusionSize soldats identiques : on retire les pions de départ hérités du
        // commandant, puis on prête juste ce qu'il faut de soldats. Tout est rendu par EndTutorial (Run.Reset).
        foreach (var spec in ArmyMinusCommander())
            _run.DeleteUnit(spec);
        var fusionSize = _run.FusionSizeFor(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));
        for (var i = 0; i < fusionSize; i++)
            _run.AddUnit(new UnitSpec(Domaine.Dame, Domaines.Dame.BaseClass));

        _pending.Clear();
        _pending.AddRange(ArmyMinusCommander());   // = FusionSize soldats identiques, prêts à fusionner
        _dragSpec = null;
        _dragFrom = null;
        _gpInventory = false;
        _gpButtons = false;
        MarkLayoutDirty();
    }

    /// <summary>Équipement prêté par le tuto : un bonus de STAT, posable sur n'importe quelle classe.</summary>
    private const string TutorialEquipmentId = "epee";

    /// <summary>Fin du placement : lance le combat (la vague ennemie est déjà posée).</summary>
    private void BeginBattle()
    {
        CancelDrag();
        _invScrollRow = 0;   // le panneau de réserve du combat repart non défilé

        // Sous-phase Équipement close : un équipement encore porté retourne à l'inventaire (non perdu).
        if (_dragEquip is { } e)
            _run.AddEquipment(e);
        _dragEquip = null;
        _dragEquipFrom = null;
        _equipPhase = false;
        // Pile de fusion non terminée : sa base reprend sa case (et combat), le surplus va en réserve.
        DisbandFusionToOrigin();
        // Les pions posés au placement ont été instanciés AVANT la phase Équipement : on les ré-instancie
        // depuis leur gabarit pour que les équipements posés s'appliquent dès CE combat (stats + traits).
        RespawnPlayerUnitsFromSpecs();
        _run.StartBattle();
        ClearSelection();
        // Le panneau de droite glisse hors écran et le plateau se recentre : animation d'entrée.
        _battleIntroTimer = BattleIntroDuration;
        MarkLayoutDirty();
        _aiTimer = 0;
        _lastAiAction = null;   // aucune action IA à revoir au tout début du combat
        Context.Sounds.Play("battle_start");

        // Mission spéciale : le compte à rebours de tours démarre au combat (le joueur joue le round 1 en
        // premier). La limite vient de la map (`turnLimit`) si elle en fixe une, sinon la valeur par défaut.
        _specialRoundsLeft = SpecialTurnBudget();

        // Base de référence : un allié DÉJÀ posé sur une recrue (placement) ne déclenche pas — seule une
        // ENTRÉE pendant le combat compte.
        _recruePrev.Clear();
        foreach (var c in _recrueCells)
            if (_match.UnitAt(c) is { Faction: Faction.Player })
                _recruePrev.Add(c);

        // Idem pour les coffres : un allié posé DESSUS au placement ne l'ouvre pas (seule une entrée en combat compte).
        _chestPrev.Clear();
        foreach (var c in _chestCells)
            if (_match.UnitAt(c) is { Faction: Faction.Player })
                _chestPrev.Add(c);
    }

    /// <summary>
    /// Objets « recrue » : quand un allié ENTRE sur une de ces cases en combat (déplacement terminé),
    /// on gagne un pion aléatoire (tier 1) en réserve et l'objet est consommé (usage unique). La
    /// détection par transition (absent→présent) évite de se déclencher sur une unité simplement placée là.
    /// </summary>
    private void CheckRecrueObjects()
    {
        if (_recrueCells.Count == 0)
            return;

        foreach (var c in _recrueCells)
        {
            if (_recrueConsumed.Contains(c))
                continue;
            var allyOn = _match.UnitAt(c) is { Faction: Faction.Player };
            if (allyOn && !_recruePrev.Contains(c))
                TriggerRecrue(c);
            if (allyOn) _recruePrev.Add(c); else _recruePrev.Remove(c);
        }
    }

    /// <summary>
    /// Mission « protéger les paysans » : quand un ENNEMI (IA offensive) atteint une case paysan, celui-ci
    /// est CAPTURÉ — la tuile est consommée (le paysan disparaît, il n'est plus protégé). Pas de transition à
    /// suivre : l'ennemi n'y arrive qu'en s'y déplaçant, et la case consommée ne se redéclenche pas.
    /// </summary>
    private void CheckPaysanCapture()
    {
        if (_recrueCells.Count == 0)
            return;

        foreach (var c in _recrueCells)
        {
            if (_recrueConsumed.Contains(c))
                continue;
            if (_match.UnitAt(c) is { Faction: Faction.Enemy })
            {
                _recrueConsumed.Add(c);          // paysan résolu…
                _recrueCaptured.Add(c);          // …et PERDU (capturé par l'IA) — distingue le libéré du perdu en « sauver »
                _match.UnblockPlayerCell(c);     // le paysan n'est plus là → sa case redevient accessible au joueur
                Context.Sounds.Play("unit_place");   // repère sonore léger de capture
            }
        }
    }

    private void TriggerRecrue(Cell c)
    {
        _recrueConsumed.Add(c);
        // Le pion est TOUJOURS révélé (on VOIT la recrue). La révélation (UpdateBattle/DrawRecrueReveal) gère
        // le cas RÉSERVE PLEINE : le joueur fait de la place (supprimer/fusionner des pions NON déployés) pour
        // la récupérer, ou l'ABANDONNE. Il ne rejoint l'armée qu'à la fin du vol, seulement s'il y a la place.
        // Tirée mais PAS encore ajoutée : la carte est révélée, puis le pion vole vers l'inventaire (UpdateBattle),
        // et il rejoint l'armée seulement à la fin du vol. Tirage parmi les tier 1 déjà vus, avec une CHANCE
        // croissante (0 % mission 1, +5 %/mission dès la mission 2) d'un TIER 2 parmi les T2 déjà découverts
        // (cf. RollSeenRecruit) — sans effet tant qu'aucun T2 n'est découvert.
        _recrueReveal = _run.RollSeenRecruit(new System.Random(), Context.Saves.IsUnitDiscovered);
        Context.Sounds.Play("unit_place");   // TODO : son dédié « recrue »
    }

    /// <summary>
    /// Coffres : quand un allié ENTRE sur une case coffre en combat (déplacement terminé), le coffre s'ouvre
    /// (équipement commun en inventaire) et est consommé (usage unique). Détection par transition (absent→présent),
    /// comme les tuiles recrue, pour ne pas déclencher sur une unité simplement posée là au placement.
    /// </summary>
    private void CheckChests()
    {
        if (_chestCells.Count == 0)
            return;

        foreach (var c in _chestCells)
        {
            if (_chestConsumed.Contains(c))
                continue;
            var allyOn = _match.UnitAt(c) is { Faction: Faction.Player };
            if (allyOn && !_chestPrev.Contains(c))
                OpenChest(c);
            if (allyOn) _chestPrev.Add(c); else _chestPrev.Remove(c);
        }
    }

    /// <summary>
    /// Avance les dissolutions d'équipement en cours et détecte les NOUVELLES morts de pions équipés (par
    /// disparition du plateau, toutes causes) pour en lancer une. Appelé chaque frame de combat.
    /// </summary>
    private void UpdateEquipDissolves(float dt)
    {
        for (var i = _equipDissolves.Count - 1; i >= 0; i--)
        {
            var d = _equipDissolves[i];
            if (d.Delay > 0)
            {
                d.Delay -= dt;
                if (d.Delay <= 0)
                    Context.Sounds.Play("equip_lost");   // son négatif : l'équipement commence à se dissoudre
                continue;
            }
            d.Time += dt;
            if (d.Time >= EquipDissolveHold + EquipDissolveDur)
                _equipDissolves.RemoveAt(i);
        }

        // Pions joueur ÉQUIPÉS encore sur le plateau ; ceux du snapshot précédent qui ont disparu sont morts.
        var current = new Dictionary<Unit, Cell>();
        foreach (var (cell, unit) in _match.Units())
            if (unit.Faction == Faction.Player && unit.Equipment != null)
                current[unit] = cell;
        foreach (var (unit, cell) in _equippedCells)
            if (!current.ContainsKey(unit) && unit.Equipment is { } e)
                _equipDissolves.Add(new EquipDissolveFx
                {
                    Equip = e,
                    Cell = cell,
                    Seed = new Vector2((_equipDissolves.Count * 53) % 211, (_equipDissolves.Count * 97) % 199),
                    Delay = EquipDissolveDelay,
                });
        _equippedCells = current;
    }

    /// <summary>Ouvre un coffre : lance la révélation MODALE (l'objet n'entre en inventaire qu'à la fin du vol).</summary>
    private void OpenChest(Cell c)
    {
        _chestConsumed.Add(c);
        // Butin : rareté tirée selon la phase + la « pitié » (bonus cumulé tant qu'on ne drop pas), puis un
        // équipement de cette rareté. Met à jour l'état de pitié de la run (sauvegardé au prochain placement).
        // TUTORIEL : butin FIXE (une épée, bonus de stat posable sur n'importe quelle classe) — la leçon
        // suivante doit pouvoir l'équiper, et la « pitié » de la vraie run n'a pas à bouger.
        var item = _tutorial != null
            ? Equipments.ById(TutorialEquipmentId)
            : _run.RollChestEquipment(new System.Random());
        if (item == null)
            return;
        _chestReveal = item;
        _chestPhase = ChestPhase.Opening;
        _chestPhaseTimer = 0;
        Context.Sounds.Play("unit_place");   // TODO : son dédié « coffre qui s'ouvre »
    }

    /// <summary>Avance la révélation modale du coffre (fige le combat). Voir <see cref="ChestPhase"/>.</summary>
    private void UpdateChestReveal(float dt)
    {
        _chestPhaseTimer += dt;
        UpdateLootFireworks(dt);   // les salves de récompense éclatent en parallèle de la révélation
        switch (_chestPhase)
        {
            case ChestPhase.Opening:
                if (_chestPhaseTimer >= ChestOpenDuration)
                {
                    _chestPhase = ChestPhase.Rolling;      // le défilement « machine à sous » démarre
                    _chestPhaseTimer = 0;
                    _chestRollSwapTimer = 0;
                    _chestRollItem = RandomRollItem();
                }
                break;

            case ChestPhase.Rolling:   // l'objet monte en défilant vite (décélère) puis se fige sur le gagné
                UpdateChestRoll(dt);
                break;

            case ChestPhase.Item:   // l'objet flotte au-dessus du coffre + description ; on attend le clic
                if (Context.Input.WasLeftClicked || Context.Input.WasKeyPressed(Keys.Enter) || Context.Input.WasConfirmPressed)
                {
                    _chestFlyFrom = ChestItemRect(ChestRevealRect()).Center.ToVector2();
                    _chestPhase = ChestPhase.Fly;
                    _chestPhaseTimer = 0;
                    Context.Sounds.Play("unit_place");
                }
                break;

            case ChestPhase.Fly:    // l'objet vole vers l'inventaire ; il y entre à l'arrivée
                if (_chestPhaseTimer >= ChestFlyDuration)
                {
                    if (_chestReveal is { } item)
                    {
                        _run.AddEquipment(item);
                        _run.Stats.AddEquipmentFound();   // récap : équipement ramassé
                        if (Context.Saves.DiscoverEquipment(item.Id))   // méta-progression : désormais connu (codex)
                            _run.Stats.AddDiscoveredEquipment(item.Name);   // récap : équipement DÉCOUVERT cette run
                    }
                    _chestPhase = ChestPhase.Settle;
                    _chestPhaseTimer = 0;
                }
                break;

            default:                // Settle : court répit puis on reprend le combat
                if (_chestPhaseTimer >= ChestSettleDuration)
                {
                    _chestReveal = null;
                    _chestRollItem = null;
                    _chestPhase = ChestPhase.None;
                    _lootBurstsLeft = 0;   // fin de la modale : on annule d'éventuelles salves restantes
                }
                break;
        }
    }

    /// <summary>
    /// Défilement « machine à sous » : l'objet affiché change de plus en plus lentement (intervalle qui passe
    /// de <see cref="ChestRollSwapMin"/> à <see cref="ChestRollSwapMax"/>), puis se FIGE sur l'objet gagné
    /// (<see cref="_chestReveal"/>) durant le dernier <see cref="ChestRollLockTime"/> avant de le révéler.
    /// </summary>
    private void UpdateChestRoll(float dt)
    {
        var remaining = ChestRollDuration - _chestPhaseTimer;
        if (remaining <= ChestRollLockTime)
            _chestRollItem = _chestReveal;   // verrouillé sur le gagné : il « atterrit » avant la révélation

        if (_chestPhaseTimer >= ChestRollDuration)
        {
            _chestRollItem = _chestReveal;
            _chestPhase = ChestPhase.Item;
            _chestPhaseTimer = 0;
            Context.Sounds.Play("reward");   // jingle positif à l'instant où l'objet se fige
            if (_chestReveal!.Rarity == EquipmentRarity.Legendary)
                Context.Sounds.Play("reward_legendary");   // fanfare EN PLUS pour un légendaire (par-dessus le feu d'artifice)
            QueueLootFireworks(_chestReveal!.Rarity);   // rare = petit feu d'artifice, légendaire = grand bouquet
            return;
        }

        // Intervalle de changement croissant (décélération) selon l'avancement du défilement.
        if (_chestRollItem == _chestReveal && remaining <= ChestRollLockTime)
            return;   // déjà figé : on ne change plus
        var progress = _chestPhaseTimer / ChestRollDuration;
        var interval = ChestRollSwapMin + (ChestRollSwapMax - ChestRollSwapMin) * (progress * progress);
        _chestRollSwapTimer += dt;
        if (_chestRollSwapTimer >= interval)
        {
            _chestRollSwapTimer = 0;
            _chestRollItem = RandomRollItem();
        }
    }

    /// <summary>Objet aléatoire du catalogue pour le défilement (repli sur l'objet gagné si le catalogue est vide).</summary>
    private Equipment RandomRollItem()
    {
        var all = Equipments.All;
        return all.Count > 0 ? all[_chestRollRng.Next(all.Count)] : _chestReveal!;
    }

    /// <summary>
    /// Programme le feu d'artifice de récompense selon la rareté du butin révélé : RIEN pour un commun, UNE
    /// petite gerbe pour un RARE, un bouquet de <see cref="LegendaryFireworkBursts"/> GROSSES salves enchaînées
    /// pour un LÉGENDAIRE. Les salves éclatent au fil du temps (cf. <see cref="UpdateLootFireworks"/>).
    /// </summary>
    private void QueueLootFireworks(EquipmentRarity rarity)
    {
        if (rarity == EquipmentRarity.Common)
            return;
        _lootBurstBig = rarity == EquipmentRarity.Legendary;
        _lootBurstsLeft = _lootBurstBig ? LegendaryFireworkBursts : RareFireworkBursts;
        _lootBurstTimer = 0;   // première salve immédiatement
    }

    /// <summary>Fait éclater les salves programmées (une, puis attente, puis la suivante). Cf. <see cref="QueueLootFireworks"/>.</summary>
    private void UpdateLootFireworks(float dt)
    {
        if (_lootBurstsLeft <= 0)
            return;
        _lootBurstTimer -= dt;
        if (_lootBurstTimer > 0)
            return;
        _lootBurstsLeft--;
        _lootBurstTimer = LegendaryFireworkInterval;   // délai avant la prochaine (ignoré s'il n'en reste plus)
        EmitLootBurst();
    }

    /// <summary>
    /// Une salve : gerbe radiale d'étincelles dans le CIEL au-dessus du coffre (hors du tooltip du butin).
    /// Légendaire = grosse gerbe dispersée dans le tiers supérieur ; rare = petite gerbe centrée.
    /// </summary>
    private void EmitLootBurst()
    {
        var vp = VirtualViewport;
        var availW = vp.Width - RightPanelWidth;
        if (_lootBurstBig)
        {
            var x = availW * (0.25f + (float)_lootFireworkRng.NextDouble() * 0.5f);
            var y = vp.Height * (0.12f + (float)_lootFireworkRng.NextDouble() * 0.2f);
            _sparks.EmitFirework(new Vector2(x, y), 48, 3);   // beaucoup de grosses braises
        }
        else
        {
            // Deux petites gerbes proches du centre, légèrement dispersées pour ne pas se superposer.
            var x = availW * (0.38f + (float)_lootFireworkRng.NextDouble() * 0.24f);
            var y = vp.Height * (0.18f + (float)_lootFireworkRng.NextDouble() * 0.1f);
            _sparks.EmitFirework(new Vector2(x, y), 22, 1);
        }
        Context.Sounds.Play("firework", 0.8f);   // « pop » à chaque salve (crépitement pour le bouquet légendaire)
    }

    private void PlacePlayer(UnitSpec spec, Cell cell)
    {
        var unit = spec.Spawn(Faction.Player, _run.BuffsFor(spec));
        _match.Place(cell, unit);
        _playerSpec[unit] = spec;
        TriggerLanding(cell);
    }

    /// <summary>
    /// Ré-instancie chaque pion joueur posé depuis son gabarit (même case), pour appliquer l'équipement
    /// assigné en phase Équipement (un Unit fige son équipement à la création). PV pleins = correct en début
    /// de combat. Appelé au lancement du combat.
    /// </summary>
    private void RespawnPlayerUnitsFromSpecs()
    {
        // Un nœud acheté en placement peut régler le MOTEUR (Rempart renforcé / Esquive renforcée) : on
        // resynchronise les bonus d'arbre du Match (appliqués aux seules unités du joueur) en plus de
        // réinstancier les pions (stats d'arbre). _run peut être null (tutoriel) → aucun bonus d'arbre.
        _match.RempartBonus = _run?.RempartBonus ?? 0;
        _match.EsquiveBonusPercent = _run?.EsquiveBonusPercent ?? 0;
        _match.TueurDeGeantsBonus = _run?.TueurDeGeantsBonus ?? 0;
        _match.FormationBonus = _run?.FormationBonus ?? 0;
        foreach (var (cell, unit) in _match.Units().Where(u => u.Unit.Faction == Faction.Player).ToList())
            RespawnAt(cell, unit);
    }

    /// <summary>
    /// Ré-instancie LE pion posé du gabarit <paramref name="spec"/> (même case), pour resynchroniser son
    /// <c>Unit.Equipment</c> après un changement en phase Équipement → la carte tooltip se met à jour en direct.
    /// </summary>
    private void RefreshDeployedUnit(UnitSpec spec)
    {
        foreach (var (cell, unit) in _match.Units().Where(u => u.Unit.Faction == Faction.Player).ToList())
            if (_playerSpec.TryGetValue(unit, out var s) && s == spec)
            {
                RespawnAt(cell, unit);
                return;
            }
    }

    /// <summary>Remplace l'<see cref="Unit"/> d'une case par une instance neuve de son gabarit (même case, PV pleins).</summary>
    private void RespawnAt(Cell cell, Unit unit)
    {
        if (!_playerSpec.TryGetValue(unit, out var spec))
            return;
        _match.Remove(cell);
        _playerSpec.Remove(unit);
        var fresh = spec.Spawn(Faction.Player, _run.BuffsFor(spec));
        _match.Place(cell, fresh);
        _playerSpec[fresh] = spec;
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
        // Boss sur map dessinée : placement dédié (boss sur une case B, escortes sur toutes les autres).
        if (_run.IsBossCombat && _map is { BossSpawns.Count: > 0 } bossMap
            && wave.Count > 0 && wave[0].Essential)
        {
            PlaceBossWave(wave, bossMap);
            return;
        }

        var cells = EnemyDeployCells().ToList();
        if (_map is { } m)
        {
            _run.ShuffleForCombat(cells);   // map dessinée : ennemis sur des cases tirées au hasard parmi les E (déterministe pour ce combat)
            // Mission spéciale : les cases à IA spéciale (défensive D / offensive O) sont servies EN PREMIER
            // (tri stable), pour que les rôles clés soient tenus même si la vague a moins d'unités que de cases.
            if (m.DefensiveEnemySpawns.Count > 0 || m.OffensiveEnemySpawns.Count > 0)
                cells = cells.OrderByDescending(
                    c => m.DefensiveEnemySpawns.Contains(c) || m.OffensiveEnemySpawns.Contains(c)).ToList();
        }
        var i = 0;
        foreach (var spec in wave)
        {
            while (i < cells.Count
                && (_match.UnitAt(cells[i]) != null || _battlefield[cells[i]].BlocksMovement))
                i++;
            if (i >= cells.Count) break;
            // IA selon la case de spawn (mission spéciale) : « D » = garde défensif, « O » = assaillant
            // offensif ; sinon IA normale. L'assaut/capture de paysans (« O ») n'a de sens QUE lorsque l'IA peut
            // capturer (missions « protéger » ET « sauver ») : hors de ces modes, un marqueur « O » retombe sur
            // l'IA normale (fonce sur le joueur), pour que la capture par l'IA reste réservée à ces missions.
            var ai = AiKind.Normal;
            if (_map is { } dm)
            {
                if (dm.DefensiveEnemySpawns.Contains(cells[i]))
                    ai = AiKind.Defensif;
                else if (AiCapturesPaysans && dm.OffensiveEnemySpawns.Contains(cells[i]))
                    ai = AiKind.Offensif;
            }
            SpawnEnemyOn(spec, cells[i], ai);
            i++;
        }
    }

    /// <summary>
    /// Placement d'un combat de BOSS sur map dessinée : le boss (essentiel, en tête de vague) sur une case
    /// <c>B</c> ; les escortes sur TOUTES les autres cases ennemies (B en surplus + E), pour qu'AUCUNE case
    /// de spawn ne reste vide. Ordre des cases tiré au hasard (déterministe pour ce combat).
    /// </summary>
    private void PlaceBossWave(List<UnitSpec> wave, MapData map)
    {
        var bossCells = map.BossSpawns.ToList();
        _run.ShuffleForCombat(bossCells);
        // Case B non bloquante de préférence (filet si un B a été peint sur une tuile infranchissable).
        var bossCell = bossCells.FirstOrDefault(c => !_battlefield[c].BlocksMovement, bossCells[0]);
        SpawnEnemyOn(wave[0], bossCell, AiKind.Normal);   // le boss sur sa case dédiée

        // Toutes les cases ennemies restantes (B non utilisées + E), tirées au hasard, remplies par les escortes.
        var rest = map.BossSpawns.Concat(map.EnemySpawns).Where(c => c != bossCell).ToList();
        _run.ShuffleForCombat(rest);
        var i = 0;
        for (var k = 1; k < wave.Count; k++)
        {
            while (i < rest.Count
                && (_match.UnitAt(rest[i]) != null || _battlefield[rest[i]].BlocksMovement))
                i++;
            if (i >= rest.Count) break;
            SpawnEnemyOn(wave[k], rest[i], AiKind.Normal);
            i++;
        }
    }

    /// <summary>Instancie l'ennemi du gabarit sur la case, fixe son IA et l'enregistre (retrouvé à la mort).</summary>
    private void SpawnEnemyOn(UnitSpec spec, Cell cell, AiKind ai)
    {
        var unit = spec.Spawn(Faction.Enemy);
        unit.AiKind = ai;
        _match.Place(cell, unit);
        _enemySpec[unit] = spec;   // pour retrouver le gabarit à la mort (recrutement)
        if (_map is { } m && m.ForcedFacing.TryGetValue(cell, out var forced))
            _enemyForcedFacing[unit] = forced;   // orientation par défaut imposée par la case de spawn (calque `facing`)
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

        // Échap pendant la popup de fusion : annule la fusion plutôt que d'ouvrir le menu pause.
        if (FusionOpen && !_pauseMenu.IsOpen && Context.Input.WasKeyPressed(Keys.Escape))
        {
            CancelFusion();
            return;
        }

        // Échap pendant l'arbre de commandement : le referme plutôt que d'ouvrir le menu pause. En tuto,
        // l'arbre reste ouvert tant que le nœud n'est pas acheté (le guide le referme lui-même).
        if (CommandTreeOpen && _tutorial == null && !_pauseMenu.IsOpen && Context.Input.WasKeyPressed(Keys.Escape))
        {
            _commandTree.Close();
            RespawnPlayerUnitsFromSpecs();   // nœuds achetés : les pions posés reprennent les bons bonus
            return;
        }

        // Codex ouvert (depuis le menu pause) : il capte toutes les entrées jusqu'à sa fermeture ; le
        // menu pause reste ouvert derrière et reprend la main dès qu'on referme le codex.
        if (_codex.IsOpen)
        {
            _codex.Update(VirtualViewport, (float)gameTime.ElapsedGameTime.TotalSeconds);
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

        // Survol d'un pion : alimente le fondu d'entrée de sa carte-tooltip (cf. TooltipHoverAlpha).
        UpdateTooltipHover((float)gameTime.ElapsedGameTime.TotalSeconds);

        // Chronomètre de la run. Placé APRÈS les sorties anticipées ci-dessus, il ne compte donc QUE le
        // temps réellement joué : menu pause et codex sont exclus d'office, et l'écran de récap l'est par
        // le filtre de phase. Cumulé dans les stats, donc sauvegardé avec le slot et affiché en fin de run.
        if (_run.Phase is RunPhase.Placement or RunPhase.Battle or RunPhase.Recruitment)
            _run.Stats.AddPlayTime(gameTime.ElapsedGameTime.TotalSeconds);

        // Bascule du quadrillage permanent du plateau : F1 (clavier) ou Select (manette).
        if (Context.Input.WasKeyPressed(Keys.F1) || Context.Input.WasSelectPressed)
            _showGrid = !_showGrid;

        // Zoom (molette) + pan (flèches / ZQSD) uniquement sur les phases avec plateau, et pas pendant
        // le glissement d'entrée en combat (l'animation pilote seule le cadrage à ce moment-là).
        // Caméra gelée derrière un modal de placement (briefing / popup de fusion / animation d'évolution).
        if (_run.Phase is RunPhase.Placement or RunPhase.Battle && _battleIntroTimer <= 0
            && !FusionOpen && !EvoPlaying && !_specialBriefOpen)
            UpdateCamera(gameTime);

        // Le dézoom ne vaut que pendant le combat : hors phases plateau (recrutement, récap de fin), on
        // revient au cadrage plein pour que les pions figés derrière ces écrans gardent leur taille normale.
        if (_dezoomedOut && _run.Phase is not (RunPhase.Placement or RunPhase.Battle))
        {
            _dezoomedOut = false;
            MarkLayoutDirty();
        }

        switch (_run.Phase)
        {
            case RunPhase.Placement: UpdatePlacement(gameTime); break;
            case RunPhase.Battle: UpdateBattle(gameTime); break;
            case RunPhase.Recruitment: UpdateRecruitment(gameTime); break;
            case RunPhase.Victory:
            case RunPhase.Defeat:
                // Run terminée (slot déjà effacé) : le récap est affiché ; clic / A / Entrée ramène au menu.
                if (Context.Input.WasLeftClicked || Context.Input.WasConfirmPressed || Context.Input.WasKeyPressed(Keys.Enter))
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
            // Piste boss sur les 3 boss de phase. TODO : piste distincte sur _run.IsFinalBoss (boss final).
            RunPhase.Battle => _run.IsBossCombat ? MusicScene.Boss : MusicScene.Combat,
            _ => MusicScene.Combat,   // recrutement / victoire / défaite : « sinon », la playlist
        };
        Context.Music.Play(scene);
    }

    private void UpdatePlacement(GameTime gameTime)
    {
        // Briefing de mission spéciale : modale d'ouverture qui gèle toute la préparation jusqu'au clic / A.
        if (_specialBriefOpen)
        {
            if (Context.Input.WasLeftClicked || Context.Input.WasKeyPressed(Keys.Enter) || Context.Input.WasConfirmPressed)
            {
                _specialBriefOpen = false;
                Context.Sounds.Play("menu_close");
            }
            return;
        }

        // Tuto, PRÉPARATION guidée : ces étapes pilotent elles-mêmes les modales (sous-phase Équipement,
        // arbre de commandement) et doivent donc être évaluées AVANT les retours anticipés ci-dessous.
        if (_tutorial is { InPreparation: true })
        {
            if (TutorialSkipPressed()) { EndTutorial(); return; }
            if (UpdateTutorialPreparation())
                return;               // encart : placement gelé (le clic d'avancement est consommé)
        }

        // Arbre de commandement ouvert : modale par-dessus le placement, qui reste gelé derrière.
        if (CommandTreeOpen)
        {
            _commandTree.Update(_run, CommandTreeArea(), (float)gameTime.ElapsedGameTime.TotalSeconds,
                canClose: _tutorial == null);   // en tuto, on ne sort qu'après avoir acheté un nœud
            if (!CommandTreeOpen)
                RespawnPlayerUnitsFromSpecs();   // nœuds achetés : les pions posés reprennent les bons bonus
            return;
        }

        // Sous-phase Équipement (après placement+fusion) : on pose/retire les équipements sur les pions.
        if (_equipPhase)
        {
            UpdateEquipPhase(gameTime);
            return;
        }

        // Tuto : bouton « Passer » prioritaire ; sinon on suit l'avancement des étapes de placement.
        if (_tutorial != null)
        {
            if (TutorialSkipPressed()) { EndTutorial(); return; }
            var pre = _tutorial.Step;
            UpdateTutorialPlacement();
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

        // Bouton COMBATTRE (souris) : lance le combat comme Entrée. Testé avant le drag pour ne pas démarrer une
        // prise. En tuto, TryStartBattle ne mord qu'à l'étape prévue — le bouton est inerte avant.
        if (ShowFightButton && Context.Input.WasLeftClicked && FightButtonRect().Contains(mouse))
        {
            TryStartBattle();
            return;
        }

        // Bouton COMMANDEMENT (souris) : ouvre l'arbre. Même précaution que COMBATTRE vis-à-vis du drag.
        if (ShowCommandTreeButton && Context.Input.WasLeftClicked && CommandTreeButtonRect().Contains(mouse))
        {
            _commandTree.Open();
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

        // Phase de préparation en deux temps : si le joueur a des équipements, on passe d'abord par la
        // sous-phase Équipement ; sinon on lance directement le combat.
        if (!_equipPhase && _run.HasEquipment)
        {
            EnterEquipPhase();
            return;
        }
        BeginBattle();
    }

    /// <summary>Avancement des étapes de PLACEMENT du tuto (prise → pose → revue de carte → lancement).</summary>
    private void UpdateTutorialPlacement()
    {
        var t = _tutorial!;
        switch (t.Step)
        {
            case TutorialStep.Intro:
                if (Advanced())
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
                    PlaceTutorialChest(t.PlayerSoldier);        // coffre COLLÉ au soldat : un seul pas
                    if (t.Chest is { } chest)
                        PlaceTutorialEnemy(chest);              // ennemi à 4 cases du coffre (cf. le script)
                    else
                        RepositionTutorialEnemy(t.PlayerSoldier);
                    _tutorialCardIndex = 0;
                    t.Advance();                                // soldat posé → ReviewCard (revue DÈS la pose)
                }
                break;
            case TutorialStep.ReviewCard:
                UpdateTutorialCardReview();                     // une donnée par clic ; à la fin → StartCombat
                break;
        }
    }

    /// <summary>
    /// Avancement de la PRÉPARATION guidée (fusion → équipement → arbre). Renvoie vrai si le placement doit
    /// rester GELÉ cette frame (encart en attente de validation). Chaque étape « faire » se termine sur la
    /// preuve que le geste a réussi — une évolution au roster, l'équipement posé, un nœud acheté — et jamais
    /// sur un simple clic, pour que le joueur apprenne vraiment le geste.
    /// </summary>
    private bool UpdateTutorialPreparation()
    {
        var t = _tutorial!;

        // Encarts : un clic / A / Entrée passe à l'étape suivante, et gèle tout le reste en attendant.
        if (t.IsBriefing)
        {
            if (!Advanced())
                return true;
            if (t.Step == TutorialStep.Done)
            {
                EndTutorial();
                return true;
            }
            t.Advance();
            OnTutorialPreparationStep();
            return true;
        }

        switch (t.Step)
        {
            case TutorialStep.FusionDo:
                // Réussite = une unité NON basique au roster (l'évolution issue de la fusion), l'animation finie.
                if (!FusionOpen && !EvoPlaying
                    && _run.Roster.Any(u => !u.Essential && u.UnitClass != Domaines.Dame.BaseClass))
                    t.Advance();                     // → RerollLesson
                break;

            case TutorialStep.RerollLesson:
                // Encart informatif (relance + recyclage) : le placement reste gelé jusqu'à validation.
                if (Advanced())
                    t.Advance();                     // → DeployFused
                return true;

            case TutorialStep.DeployFused:
                // Réussite = la réserve est vide, donc l'unité évoluée est sur le plateau (prête à s'équiper).
                if (_pending.Count == 0 && _dragSpec == null)
                {
                    t.Advance();                 // → EquipIntro
                    return true;                 // l'encart s'affiche dès cette frame
                }
                break;

            case TutorialStep.EquipDo:
                // Réussite = l'épée est COLLÉE à un pion. (L'inventaire se vide aussi pendant le portage :
                // on regarde donc le roster, et on attend que le glisser soit relâché.)
                if (_dragEquip == null && _run.Roster.Any(u => u.Equipment != null))
                {
                    ExitEquipPhase();
                    t.Advance();                 // → TreeIntro
                    return true;
                }
                break;

            case TutorialStep.TreeOpen:
                // Réussite = le joueur a ouvert l'arbre LUI-MÊME, par le bouton COMMANDEMENT du panneau.
                if (CommandTreeOpen)
                    t.Advance();                     // → TreeDo
                break;

            case TutorialStep.TreeDo:
                // Réussite = un nœud acheté. L'arbre ne peut pas être refermé avant (cf. canClose).
                if (_run.UnlockedNodes.Count > 0)
                {
                    if (CommandTreeOpen)
                        _commandTree.Close();
                    RespawnPlayerUnitsFromSpecs();   // le nœud acheté change les stats du pion posé
                    t.Advance();                     // → Done
                    return true;
                }
                break;
        }
        return false;
    }

    /// <summary>Mise en place de l'étape de préparation qui vient de commencer (ouverture des modales prêtées).</summary>
    private void OnTutorialPreparationStep()
    {
        switch (_tutorial!.Step)
        {
            case TutorialStep.EquipDo:
                // L'épée vient du coffre. Filet de sécurité si la map du tuto n'en portait pas : sans objet
                // à poser, l'étape ne pourrait jamais se terminer.
                if (!_run.EquipmentInventory.Any() && Equipments.ById(TutorialEquipmentId) is { } spare)
                {
                    _run.AddEquipment(spare);
                    Context.Saves.DiscoverEquipment(spare.Id);   // méta-progression : désormais connu (codex)
                }
                EnterEquipPhase();   // le tuto ouvre la sous-phase à la place du bouton SUIVANT
                break;
            case TutorialStep.TreeOpen:
                // La fusion a normalement rapporté des points ; on complète si ce commandant n'en donne pas,
                // pour que le bouton en affiche et que le premier nœud soit achetable.
                _run.GrantCommandPoints(CommandTree.CostOf(1) - _run.CommandPoints);
                break;
        }
    }

    /// <summary>Vrai si le joueur valide l'encart courant (clic gauche, A manette, ou Entrée).</summary>
    private bool Advanced() =>
        Context.Input.WasLeftClicked || Context.Input.WasConfirmPressed || Context.Input.WasKeyPressed(Keys.Enter);

    /// <summary>Rapproche l'ennemi du tuto : 3 cases DEVANT le soldat posé (même colonne) → 1 pas chacun = corps à corps.</summary>
    /// <summary>
    /// Repose le coffre du tuto sur une case ADJACENTE au soldat qui vient d'être posé. La map en déclare
    /// un (pour l'éditeur et le chargement), mais sa case n'a pas de sens ici : le joueur choisit librement
    /// où déployer, et la leçon du coffre doit tenir en UN déplacement quoi qu'il choisisse. On préfère les
    /// cases du BAS et des côtés — jamais celle vers l'ennemi, qui s'y placerait dessus.
    /// </summary>
    private void PlaceTutorialChest(Cell soldier)
    {
        if (_chestCells.Count == 0)
            return;

        Cell[] candidates =
        {
            new(soldier.Column - 1, soldier.Row),       // gauche
            new(soldier.Column + 1, soldier.Row),       // droite
            new(soldier.Column - 1, soldier.Row + 1),   // bas-gauche
            new(soldier.Column + 1, soldier.Row + 1),   // bas-droite
            new(soldier.Column, soldier.Row + 1),       // bas
        };

        _chestCells.Clear();
        _chestConsumed.Clear();
        _chestPrev.Clear();
        foreach (var c in candidates)
        {
            if (!_match.InBounds(c) || _match.UnitAt(c) != null || _battlefield[c].BlocksMovement)
                continue;
            _chestCells.Add(c);
            _tutorial!.Chest = c;
            return;
        }
        _tutorial!.Chest = null;   // aucune case libre autour (impossible sur cette map) : leçon sautée
    }

    /// <summary>
    /// Place l'ennemi à EXACTEMENT 4 cases du COFFRE, dans sa colonne. Tout le script du combat en découle,
    /// et l'alternance stricte (un coup chacun) le rend déterministe :
    /// <list type="number">
    /// <item>le joueur va sur le coffre (seul pas offert) ; l'ennemi avance → distance 3 ;</item>
    /// <item>le joueur avance → 2 ; l'ennemi avance → 1, ADJACENT mais c'est au joueur de jouer ;</item>
    /// <item>le joueur FRAPPE LE PREMIER ; l'ennemi riposte (12 PV − 10 = 2 PV) ;</item>
    /// <item>le joueur achève. Il survit à 2 PV, et l'ennemi n'a jamais frappé deux fois.</item>
    /// </list>
    /// Une distance PAIRE inverserait l'ordre : l'ennemi deviendrait adjacent à SON tour et cognerait le
    /// premier, tuant le soldat au second échange.
    /// </summary>
    private void PlaceTutorialEnemy(Cell chest)
    {
        var t = _tutorial!;
        if (_match.UnitAt(t.EnemySoldier) is not { } enemy)
            return;
        var target = new Cell(chest.Column, System.Math.Max(0, chest.Row - 4));
        if (target == t.EnemySoldier || _match.UnitAt(target) != null)
            return;
        _match.Remove(t.EnemySoldier);
        _match.Place(target, enemy);
        t.EnemySoldier = target;
    }

    private void RepositionTutorialEnemy(Cell soldier)
    {
        var t = _tutorial!;
        if (_match.UnitAt(t.EnemySoldier) is not { } enemy)
            return;
        // EXACTEMENT 3 cases devant : le joueur avance (2), l'ennemi avance (1), puis le joueur FRAPPE LE
        // PREMIER. À 2 cases, l'ennemi deviendrait adjacent à son propre tour et cognerait avant lui.
        var target = new Cell(soldier.Column, System.Math.Max(0, soldier.Row - 3));
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
    /// Placement à la manette : curseur de case (croix), A saisir/poser, B annuler, RB inventaire puis
    /// boutons du panneau, Y lancer le combat. La saisie/dépose réutilise exactement la logique du
    /// glisser souris.
    /// </summary>
    private void UpdatePlacementGamepad()
    {
        if (Context.Input.WasQuaternaryPressed) { TryStartBattle(); return; }   // Y = COMBATTRE (raccourci global)

        if (_gpButtons) { UpdateButtonsFocus(); return; }
        if (_gpInventory) { UpdateInventoryFocus(); return; }

        // B sans rien porter (et hors portage) : annule une pile de fusion en cours (réserve ou plateau).
        if (FusionStacking && !_carryPile && _dragSpec == null && Context.Input.WasCancelPressed)
        {
            CancelFusion();
            return;
        }

        // DROITE au bord droit du plateau : le focus continue naturellement dans le panneau (réserve,
        // sinon boutons) au lieu de buter contre la dernière colonne. Pas quand on porte une pièce :
        // le curseur doit rester sur le plateau pour la poser.
        if (Context.Input.Nav(NavDir.Right) && _dragSpec == null && !_carryPile
            && _cursor.Column == Columns - 1 && EnterPanelFocus())
            return;

        MoveCursor();

        // X en PORTANT un pion : le RELANCE (échange contre un pion du même tier), équivalent manette du
        // glisser sur l'icône à la souris. Le remplaçant rejoint la réserve. Sans effet si plus de relance.
        if (_tutorial == null && Context.Input.WasTertiaryPressed && _dragSpec is { Essential: false }
            && TryRerollDraggedUnit(_dragSpec))
        {
            _dragSpec = null;
            _dragFrom = null;
            return;
        }

        // RB : terrain → panneau (même entrée que la sortie par la droite).
        if (Context.Input.WasRightShoulderPressed && _dragSpec == null && EnterPanelFocus())
            return;

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

        if (Context.Input.Nav(NavDir.Left))
        {
            // Gauche : colonne précédente, ou — depuis la PREMIÈRE colonne — retour sur le plateau.
            if (_invFocus % InvCols > 0) _invFocus--;
            else { _gpInventory = false; return; }
        }
        if (Context.Input.Nav(NavDir.Right) && _invFocus % InvCols < InvCols - 1 && _invFocus + 1 < n) _invFocus++;
        if (Context.Input.Nav(NavDir.Up) && _invFocus - InvCols >= 0) _invFocus -= InvCols;
        if (Context.Input.Nav(NavDir.Down))
        {
            // Bas : rangée suivante, ou — depuis la DERNIÈRE rangée — descente vers les boutons du panneau.
            if (_invFocus + InvCols < n) _invFocus += InvCols;
            else if (EnterButtonsFocus()) { _gpInventory = false; return; }
        }

        EnsureInvFocusVisible();   // la réserve défile pour garder le portrait focalisé à l'écran

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
        else if (Context.Input.WasRightShoulderPressed)
        {
            if (EnterButtonsFocus())
                _gpInventory = false;   // RB : inventaire → boutons du panneau
        }
        else if (Context.Input.WasCancelPressed || Context.Input.WasLeftShoulderPressed)
        {
            _gpInventory = false;   // LB (ou B) : inventaire → terrain
        }
    }

    /// <summary>
    /// Plateau → panneau de placement (manette) : la réserve si elle a des pions, sinon directement les
    /// boutons — sans quoi ils seraient inatteignables quand tout est déployé. Faux si le panneau n'a
    /// rien à focus (tutoriel avec réserve vide) : le curseur reste alors sur le plateau.
    /// </summary>
    private bool EnterPanelFocus()
    {
        if (_pending.Count > 0)
        {
            _gpInventory = true;
            _invFocus = System.Math.Clamp(_invFocus, 0, _pending.Count - 1);
            EnsureInvFocusVisible();
            return true;
        }
        return EnterButtonsFocus();
    }

    /// <summary>
    /// Entre dans la zone des boutons du panneau (COMMANDEMENT / COMBATTRE). Faux — et rien ne change — si
    /// aucun n'est dessiné (préparation guidée du tuto).
    /// </summary>
    private bool EnterButtonsFocus()
    {
        if (PlacementButtonCount == 0)
            return false;
        _gpButtons = true;
        _btnFocus = 0;
        return true;
    }

    /// <summary>
    /// Focus sur les boutons du panneau (manette) : Haut/Bas les parcourt, Gauche revient au plateau,
    /// A valide, B/LB/RB revient au terrain. Depuis le bouton du HAUT, Haut remonte dans l'inventaire.
    /// </summary>
    private void UpdateButtonsFocus()
    {
        if (Context.Input.Nav(NavDir.Left)) { _gpButtons = false; return; }   // sortie par la gauche : plateau

        if (Context.Input.Nav(NavDir.Down))
            _btnFocus = System.Math.Min(_btnFocus + 1, PlacementButtonCount - 1);
        if (Context.Input.Nav(NavDir.Up))
        {
            if (_btnFocus > 0)
            {
                _btnFocus--;
            }
            else if (_pending.Count > 0)
            {
                _gpButtons = false;   // remonte dans l'inventaire, sur son dernier portrait
                _gpInventory = true;
                _invFocus = _pending.Count - 1;
                EnsureInvFocusVisible();
                return;
            }
        }

        if (Context.Input.WasConfirmPressed)
        {
            _gpButtons = false;
            if (_btnFocus == FightButtonIndex) TryStartBattle();
            else _commandTree.Open();
            return;
        }

        if (Context.Input.WasCancelPressed || Context.Input.WasLeftShoulderPressed
            || Context.Input.WasRightShoulderPressed)
            _gpButtons = false;   // retour au plateau
    }

    /// <summary>Saisit l'unité joueur sous le curseur (retirée du plateau en attendant la pose).</summary>
    private void PickUpAt(Cell cell)
    {
        // Tuto : pas de reprise de pion pendant les leçons de placement (commandant figé, soldat une fois
        // posé). En PRÉPARATION en revanche il FAUT pouvoir reprendre un pion : un soldat lâché sur le
        // plateau au lieu d'être fusionné laisserait sinon la réserve à deux exemplaires, et la fusion —
        // qui en demande trois — deviendrait impossible.
        if (_tutorial is { InPreparation: false })
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
        //    Bloqué pendant les leçons de PLACEMENT du tuto (commandant figé, soldat une fois posé), mais
        //    autorisé en préparation : sans cela un pion lâché sur le plateau y resterait, et la fusion —
        //    qui demande trois exemplaires en réserve — deviendrait impossible.
        if (_tutorial is not { InPreparation: false }
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

        // Lâcher sur l'ICÔNE DE RELANCE (à gauche du panneau, souris) : échange le pion contre un autre du
        // même tier. Passe avant tout le reste. Le remplaçant rejoint la réserve (cf. TryRerollDraggedUnit).
        if (_tutorial == null && !Context.Input.UsingGamepad && !spec.Essential
            && RerollIconRect().Contains(Context.Input.MousePosition)
            && TryRerollDraggedUnit(spec))
        {
            _dragSpec = null;
            _dragFrom = null;
            return;
        }

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

    // ─── ÉQUIPEMENT (sous-phase de placement) ─────────────────────────────────────────────────────
    // Après placement+fusion, si le joueur a des équipements (HasEquipment), un bouton « Suivant » entre
    // dans cette sous-phase (la run reste en RunPhase.Placement). Chaque pion DÉPLOYÉ non-essentiel porte
    // un slot au-dessus de la tête ; on glisse un équipement depuis le bandeau du panneau vers un slot pour
    // l'équiper, ou d'un slot vers le bandeau pour le retirer. Un seul équipement par pion ; le commandant
    // n'en a pas. La règle métier (un par pion, swap, commandant exclu) vit dans Run.Equip/Unequip ; ici la
    // vue + le glisser. Le bouton « Combat » lance le combat (verrouille l'équipement, sauvé au prochain placement).

    private void EnterEquipPhase()
    {
        CancelDrag();
        DisbandFusionToOrigin();   // une pile de fusion non terminée est défaite avant d'équiper
        // Les pions restés EN RÉSERVE (non posés sur le plateau) ne portent pas de slot dans cette
        // sous-phase : leur équipement serait figé et inutilisable. On le rend donc à l'inventaire pour
        // qu'il puisse aller sur un pion déployé. _pending contient exactement les pions non placés.
        foreach (var spec in _pending)
            _run.Unequip(spec);
        _equipPhase = true;
        _dragEquip = null;
        _dragEquipFrom = null;
        _equipFocus = 0;
        _gpInventory = false;
        _gpButtons = false;
        ClearSelection();
        Context.Sounds.Play("unit_place");
    }

    /// <summary>Retour de la sous-phase Équipement vers le placement (un équipement porté retourne à l'inventaire).</summary>
    private void ExitEquipPhase()
    {
        if (_dragEquip is { } e)
        {
            _run.AddEquipment(e);
            _dragEquip = null;
            _dragEquipFrom = null;
        }
        _equipPhase = false;
        Context.Sounds.Play("unit_pick");
    }

    private void UpdateEquipPhase(GameTime gameTime)
    {
        if (Context.Input.UsingGamepad)
        {
            UpdateEquipPhaseGamepad();
            return;
        }

        var mouse = Context.Input.MousePosition;

        // Glisser en cours : le relâchement pose/retire l'équipement.
        if (_dragEquip != null)
        {
            if (Context.Input.WasLeftReleased)
                DropEquip(mouse);
            return;
        }

        // En tuto, la sortie de la sous-phase est pilotée par le guide (l'épée posée) : ni combat ni retour.
        if (_tutorial != null)
        {
            if (Context.Input.WasLeftClicked)
                BeginEquipDrag(mouse);
            return;
        }

        if (Context.Input.WasKeyPressed(Keys.Enter))   // Entrée = Combat
        {
            BeginBattle();
            return;
        }

        if (Context.Input.WasLeftClicked)
        {
            if (FightButtonRect().Contains(mouse)) { BeginBattle(); return; }
            if (EquipBackButtonRect().Contains(mouse)) { ExitEquipPhase(); return; }
            BeginEquipDrag(mouse);
        }
    }

    /// <summary>Sous-phase Équipement à la manette : curseur sur un pion, A équipe/déséquipe, LB/RB change l'item, Y combat, B retour.</summary>
    private void UpdateEquipPhaseGamepad()
    {
        // En tuto, la sortie est pilotée par le guide (l'épée posée) : ni combat ni retour.
        if (_tutorial == null)
        {
            if (Context.Input.WasQuaternaryPressed) { BeginBattle(); return; }   // Y = Combat
            if (Context.Input.WasCancelPressed) { ExitEquipPhase(); return; }    // B = retour au placement
        }

        MoveCursor();

        var inv = _run.EquipmentInventory;
        if (inv.Count > 0)
        {
            if (Context.Input.WasRightShoulderPressed) _equipFocus = (_equipFocus + 1) % inv.Count;
            if (Context.Input.WasLeftShoulderPressed) _equipFocus = (_equipFocus + inv.Count - 1) % inv.Count;
        }
        _equipFocus = inv.Count == 0 ? 0 : System.Math.Clamp(_equipFocus, 0, inv.Count - 1);

        // X : RECYCLER l'équipement focus de l'inventaire → +1 relance (l'objet est DÉTRUIT).
        if (_tutorial == null && Context.Input.WasTertiaryPressed && inv.Count > 0)
        {
            _run.RemoveEquipment(inv[_equipFocus]);
            _run.AddReroll();
            _equipFocus = inv.Count == 0 ? 0 : System.Math.Clamp(_equipFocus, 0, inv.Count - 1);
            Context.Sounds.Play("equip_lost");   // son de casse (objet détruit)
            return;
        }

        // A sur un pion déployé non-commandant : équipe l'item focus s'il est nu, sinon le déséquipe.
        if (Context.Input.WasConfirmPressed
            && _match.UnitAt(_cursor) is { Faction: Faction.Player } unit
            && _playerSpec.TryGetValue(unit, out var spec)
            && !spec.Essential)
        {
            if (spec.Equipment is null && inv.Count > 0 && _run.Equip(spec, inv[_equipFocus]))
            {
                _equipFocus = inv.Count == 0 ? 0 : System.Math.Clamp(_equipFocus, 0, inv.Count - 1);
                RefreshDeployedUnit(spec);
                Context.Sounds.Play("unit_place");
            }
            else if (spec.Equipment is not null)
            {
                _run.Unequip(spec);
                RefreshDeployedUnit(spec);
                Context.Sounds.Play("unit_pick");
            }
        }
    }

    /// <summary>Saisit un équipement à la souris : depuis le bandeau d'inventaire, ou depuis le slot d'un pion équipé.</summary>
    private void BeginEquipDrag(Point mouse)
    {
        if (EquipPanelCardAt(mouse) is { } i)
        {
            var item = _run.EquipmentInventory[i];
            _run.RemoveEquipment(item);     // retiré de l'inventaire le temps du portage
            _dragEquip = item;
            _dragEquipFrom = null;
            Context.Sounds.Play("unit_pick");
            return;
        }

        var layout = BuildLayout();
        foreach (var (cell, spec) in DeployedPlayerSpecs().ToList())
        {
            if (spec.Essential || spec.Equipment is null)
                continue;
            if (EquipBadgeRect(cell, layout).Contains(mouse))
            {
                _dragEquip = spec.Equipment;
                spec.Equipment = null;      // détaché (rendu à l'inventaire seulement si lâché hors d'un slot)
                _dragEquipFrom = spec;
                RefreshDeployedUnit(spec);  // la carte tooltip reflète le retrait en direct
                Context.Sounds.Play("unit_pick");
                return;
            }
        }
    }

    /// <summary>Dépose l'équipement porté : sur un slot de pion (équipe, l'ancien revient à l'inventaire) ou ailleurs (inventaire).</summary>
    private void DropEquip(Point mouse)
    {
        var carried = _dragEquip!;

        // Lâcher sur l'ICÔNE DE RELANCE : CASSE l'équipement (détruit) contre +1 relance.
        if (_tutorial == null && RerollIconRect().Contains(mouse))
        {
            _run.AddReroll();
            _dragEquip = null;
            _dragEquipFrom = null;
            Context.Sounds.Play("equip_lost");   // son de casse : l'objet est détruit
            return;
        }

        var layout = BuildLayout();

        foreach (var (cell, spec) in DeployedPlayerSpecs().ToList())
        {
            if (spec.Essential)
                continue;
            if (EquipBadgeRect(cell, layout).Contains(mouse))
            {
                if (!_run.CanEquip(spec, carried))
                {
                    // Pion incompatible (restriction de domaine : cf. Run.CanEquip) : refus, l'objet retourne à l'inventaire.
                    _run.AddEquipment(carried);
                    _dragEquip = null;
                    _dragEquipFrom = null;
                    Context.Sounds.Play("unit_deselect");
                    return;
                }
                if (spec.Equipment is { } occ)   // slot occupé : l'ancien équipement repart à l'inventaire
                    _run.AddEquipment(occ);
                spec.Equipment = carried;
                RefreshDeployedUnit(spec);       // la carte tooltip reflète le nouvel équipement en direct
                _dragEquip = null;
                _dragEquipFrom = null;
                Context.Sounds.Play("unit_place");
                return;
            }
        }

        // Lâcher hors d'un slot (bandeau / vide) : retour à l'inventaire (= déséquipé s'il venait d'un pion).
        _run.AddEquipment(carried);
        _dragEquip = null;
        _dragEquipFrom = null;
        Context.Sounds.Play("unit_pick");
    }

    /// <summary>Pions joueur DÉPLOYÉS (sur le plateau) et leur gabarit d'inventaire.</summary>
    private IEnumerable<(Cell Cell, UnitSpec Spec)> DeployedPlayerSpecs()
    {
        foreach (var (cell, unit) in _match.Units())
            if (unit.Faction == Faction.Player && _playerSpec.TryGetValue(unit, out var spec))
                yield return (cell, spec);
    }

    /// <summary>Rectangle de l'icône d'équipement AU-DESSUS de la tête du pion (badge placement + cible de dépose).</summary>
    private Rectangle EquipBadgeRect(Cell cell, GridLayout layout)
    {
        const int s = 34;                                        // cadre 34 ; l'icône 32 y est centrée
        var top = layout.CellToScreen(cell.Column, cell.Row);
        var size = layout.TileSize;
        var spriteLift = (int)(size * SpriteLiftFraction);
        var cx = (int)top.X + size / 2;
        var y = (int)top.Y - spriteLift - s - 2;                 // juste au-dessus du sommet du sprite
        return new Rectangle(cx - s / 2, y, s, s);
    }

    /// <summary>Indice d'inventaire d'équipement sous <paramref name="p"/> dans le bandeau du panneau (null si aucun).</summary>
    private int? EquipPanelCardAt(Point p)
    {
        var inv = _run.EquipmentInventory;
        for (var i = 0; i < inv.Count; i++)
            if (EquipRowRect(i).Contains(p))
                return i;
        return null;
    }

    // ─── RELANCE (icône à gauche du panneau) ────────────────────────────────────────────────────
    // Icône 32×32 flottant juste à GAUCHE du panneau de droite, visible au PLACEMENT et en sous-phase
    // ÉQUIPEMENT (hors tuto). On y LÂCHE un pion (souris) pour le RELANCER — échange contre un pion du
    // même tier, cf. Run.RerollUnit — ou un ÉQUIPEMENT pour le CASSER contre +1 relance (Run.AddReroll).
    // La règle métier vit dans Run ; ici la vue + la détection de dépose. Souris seulement pour l'instant.

    private const int RerollIconSize = 32;    // icône native
    private const int RerollFrame = 40;       // zone de dépose autour de l'icône

    /// <summary>
    /// Libellé sous l'icône : le COMPTEUR de relances au placement, « RECYCLER » en sous-phase Équipement
    /// (où lâcher un objet le casse contre +1 relance). Sert au dessin ET au calcul de l'écart au panneau.
    /// </summary>
    private string RerollLabel() =>
        _equipPhase ? Loc.T("relance.recycle") : Loc.T("relance.count", _run.Rerolls);

    /// <summary>
    /// Rectangle de l'icône de relance : à GAUCHE du panneau, à hauteur des BOUTONS du bas (bouton Combat).
    /// L'écart au panneau est JUSTE ce qu'il faut pour que le libellé centré sous l'icône tienne à gauche
    /// du panneau (pas plus) — évite le trou visuel d'un écart fixe trop large.
    /// </summary>
    private Rectangle RerollIconRect()
    {
        var fight = FightButtonRect();
        var labelW = (int)Context.Font.Measure(RerollLabel(), 1);
        var gap = System.Math.Max(12, (labelW - RerollFrame) / 2 + 12);
        var x = PanelRect().X - gap - RerollFrame;
        var y = fight.Y - 2;                  // aligné sur la ligne des boutons du bas
        return new Rectangle(x, y, RerollFrame, RerollFrame);
    }

    /// <summary>Relance le pion en cours de glisser (lâché sur l'icône) : remplaçant en réserve. Faux si impossible.</summary>
    private bool TryRerollDraggedUnit(UnitSpec spec)
    {
        var replacement = _run.RerollUnit(spec, new System.Random(), Context.Saves.IsUnitDiscovered);
        if (replacement == null)
            return false;
        _pending.Add(replacement);        // le remplaçant rejoint la réserve
        ClampInvScroll();
        Context.Sounds.Play("recruit");   // son positif : nouveau pion obtenu
        return true;
    }

    /// <summary>
    /// Dessine l'icône de relance + le compteur, à gauche du panneau (placement/équipement, hors tuto).
    /// « Active » = utilisable maintenant : au placement il faut une relance, en équipement casser en donne
    /// toujours une. Surbrillance quand un glisser compatible la survole.
    /// </summary>
    private void DrawRerollIcon(SpriteBatch sb)
    {
        // Masquée pendant le tuto, SAUF la leçon dédiée « relance » qui a justement besoin de la montrer.
        if (_tutorial is not (null or { Step: TutorialStep.RerollLesson }))
            return;

        var frame = RerollIconRect();
        var active = _equipPhase || _run.HasReroll;
        var tint = active ? Color.White : Color.White * 0.5f;   // grisé léger quand aucune relance dispo

        // Sous-phase Équipement : icône DIFFÉRENTE (recyclage) + libellé « RECYCLER ». Sinon : relance + compteur.
        var sprite = _equipPhase ? _recycleIcon : _rerollIcon;

        var icon = new Rectangle(frame.Center.X - RerollIconSize / 2, frame.Center.Y - RerollIconSize / 2,
            RerollIconSize, RerollIconSize);
        if (sprite != null)
        {
            sb.Draw(sprite, icon, tint);   // PNG tel quel, SANS fond (on n'écrase pas sa transparence)
        }
        else
        {
            // Repli OPAQUE tant que le PNG n'est pas fourni (pas de transparence sur le plateau).
            DrawRect(sb, frame, Palette.Navy1);
            DrawRectBorder(sb, frame, Palette.Blue1, 2);
            Context.Font.DrawCentered(sb, _equipPhase ? "C" : "R", icon, 2, tint);   // C = reCycler, R = Relance (repli ASCII)
        }

        // Libellé sous l'icône (centré, largement à gauche du panneau → libellé complet visible).
        // En JAUNE ; atténué quand aucune relance n'est disponible (mode placement).
        var label = RerollLabel();
        var below = new Rectangle(frame.Center.X - 80, frame.Bottom + 3, 160, 8);
        Context.Font.DrawCentered(sb, label, below, 1, active ? Palette.Yellow2 : Palette.Yellow2 * 0.5f);

        // Survol souris (AVEC OU SANS objet/pion en main) : surbrillance + tooltip descriptive au style
        // standard du jeu (titre jaune + description repliée), placée au-dessus de l'icône, bornée à l'écran
        // et à gauche du panneau. Titre/desc selon le mode (relancer un pion / recycler un équipement).
        if (!Context.Input.UsingGamepad && frame.Contains(Context.Input.MousePosition))
        {
            DrawRectBorder(sb, Inflate(frame, 2), Palette.Yellow1, 2);
            var title = _equipPhase ? Loc.T("relance.recycle") : Loc.T("relance.title");
            var desc = _equipPhase ? Loc.T("relance.tt_equip") : Loc.T("relance.tt_unit");
            var w = EnvTooltipWidth;
            var h = EnvTooltipHeight(desc);
            var x = System.Math.Clamp(frame.Center.X - w / 2, 8, PanelRect().X - w - 8);
            var y = frame.Y - h - 6;
            if (y < 8) y = frame.Bottom + 6;   // pas la place au-dessus : bascule dessous
            DrawEnvTooltipPanel(sb, title, desc, x, y, sentenceCase: true);
        }
        else if (Context.Input.UsingGamepad && _tutorial == null)
        {
            // Manette : pas de curseur souris — on met l'icône en surbrillance quand le X des aides agit
            // MAINTENANT (on porte un pion relançable, ou un objet est recyclable en sous-phase Équipement).
            var gpAvailable = _equipPhase
                ? _run.EquipmentInventory.Count > 0
                : (_dragSpec is { Essential: false } && _run.HasReroll);
            if (gpAvailable)
                DrawRectBorder(sb, Inflate(frame, 2), Palette.Yellow1, 2);
        }
    }

    /// <summary>Bouton « Retour » (vers le placement) juste au-dessus du bouton « Combat ».</summary>
    private Rectangle EquipBackButtonRect()
    {
        var f = FightButtonRect();
        return new Rectangle(f.X, f.Y - f.Height - 8, f.Width, f.Height);
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
        !spec.Essential && !spec.UnitClass.IsLeaf && PendingSameClassCount(spec) >= FusionSizeOf(spec);

    /// <summary>Une pile de fusion est en cours d'assemblage (entre 1 et FusionSize-1 pièces).</summary>
    private bool FusionStacking => _fusionGroup.Count > 0 && _fusionGroup.Count < FusionGroupTarget;

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

    /// <summary>Slot VISUEL (0-based, avant décalage de pile) du portrait de réserve d'indice <paramref name="i"/>.</summary>
    private int PendingVisualSlot(int i) => ReservePileSlot() is { } p && i >= p ? i + 1 : i;

    /// <summary>Case du portrait de réserve d'indice <paramref name="i"/>, en sautant le slot de la pile.</summary>
    private Rectangle PendingCardRect(int i) => SlotRect(PendingVisualSlot(i));

    /// <summary>Case de la carte « pile » de réserve : à son slot de formation (sinon en fin de grille).</summary>
    private Rectangle FusionStackCardRect() => SlotRect(ReservePileSlot() ?? _pending.Count);

    // ── Défilement de la grille de réserve ─────────────────────────────────────────────────────────
    // Quand le roster dépasse la place du panneau (réserve agrandie par l'arbre, canevas 540 en 1080p),
    // la grille défile en RANGÉES : PanelCardRect reste la grille brute (partagée avec les panneaux de
    // recrutement/combat, non défilés) ; SlotRect y applique le décalage propre au placement.

    /// <summary>Case d'un slot VISUEL, décalée du défilement courant de la réserve.</summary>
    private Rectangle SlotRect(int visualSlot)
    {
        var r = PanelCardRect(visualSlot);
        r.Y -= _invScrollRow * InvRowPitch;
        return r;
    }

    /// <summary>Nombre de slots occupés dans la grille (portraits + éventuelle pile de fusion).</summary>
    private int InvSlotCount() => _pending.Count + (ReservePileSlot() is null ? 0 : 1);

    /// <summary>Y sous lequel la grille ne doit pas déborder (au-dessus des lignes d'aide et des boutons).</summary>
    private int InvGridBottom()
    {
        var panel = PanelRect();
        var firstBtnTop = ShowCommandTreeButton ? CommandTreeButtonRect().Y
            : ShowFightButton ? FightButtonRect().Y
            : panel.Bottom - 24;
        return firstBtnTop - InvHintReserve;
    }

    /// <summary>Rangées affichables d'un coup ; au-delà, la réserve défile.</summary>
    private int InvVisibleRows() => System.Math.Max(1, (InvGridBottom() - PanelListTop) / InvRowPitch);

    private int InvTotalRows() => System.Math.Max(1, (InvSlotCount() + InvCols - 1) / InvCols);

    private int InvMaxScrollRow() => System.Math.Max(0, InvTotalRows() - InvVisibleRows());

    /// <summary>Vrai si le slot visuel tombe dans la fenêtre de rangées actuellement affichée.</summary>
    private bool InvSlotVisible(int visualSlot)
    {
        var row = visualSlot / InvCols;
        return row >= _invScrollRow && row < _invScrollRow + InvVisibleRows();
    }

    /// <summary>Borne le défilement à la plage valide (réserve rétrécie par un drag/fusion, etc.).</summary>
    private void ClampInvScroll() => _invScrollRow = System.Math.Clamp(_invScrollRow, 0, InvMaxScrollRow());

    /// <summary>Manette : fait défiler pour que le portrait focalisé reste visible.</summary>
    private void EnsureInvFocusVisible()
    {
        if (_pending.Count == 0)
            return;
        var row = PendingVisualSlot(System.Math.Clamp(_invFocus, 0, _pending.Count - 1)) / InvCols;
        if (row < _invScrollRow) _invScrollRow = row;
        else if (row >= _invScrollRow + InvVisibleRows()) _invScrollRow = row - InvVisibleRows() + 1;
        ClampInvScroll();
    }

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
        for (var i = _pending.Count - 1; i >= 0 && _fusionGroup.Count < FusionSizeOf(rep); i--)
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

        // Nœud « fusion » de l'arbre : chaque fusion offre EN PLUS des tier 1 déjà découverts. La fusion
        // consomme 3 pions pour 1, la réserve a donc toujours la place — on garde quand même le garde-fou.
        foreach (var bonus in GrantFusionRecruits())
            _pending.Add(bonus);

        // Version LONGUE (grand moment) uniquement la 1re fois qu'on obtient l'unité ; sinon version courte.
        var firstTime = !Context.Saves.IsUnitDiscovered(fused.UnitClass.Asset);
        Context.Saves.DiscoverUnit(fused.UnitClass.Asset);   // méta-progression : désormais connue
        _run.Stats.AddFusion();                                   // récap : fusion réalisée
        if (firstTime)
            _run.Stats.AddDiscoveredClass(fused.UnitClass.Name);  // récap : évolution DÉCOUVERTE cette run
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
        // La fusion a changé la composition du roster : les bonus « par paire de classes distinctes » de
        // l'arbre bougent, donc les pions déjà posés doivent reprendre leurs stats (le combat, lui, les
        // recalcule de toute façon au lancement).
        if (_run.Phase == RunPhase.Placement)
            RespawnPlayerUnitsFromSpecs();
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
        _tremor.Update(dt);        // les tuiles de l'AoE (Séisme/Impact) finissent de trembler (cf. DrawTerrain)
        _pierceRecoil.Update(dt);  // le recul du pion transpercé se résorbe (cf. DrawUnit)
        if (_damagePopups.HasActive) // chiffres de dégâts : éclatent en feu d'artifice à l'extinction
            _damagePopups.Update(dt, BuildLayout(), _sparks);
        SpawnCommanderPointFeedback();   // « +N » doré quand le commandant gagne un point de commandement sur un coup reçu
        UpdateEquipDissolves(dt);  // dissolution de l'équipement des unités équipées qui viennent de mourir

        // Paysans (tuiles recrue) :
        //   • « protéger » : seuls les ENNEMIS agissent (capture) — le joueur défend, il ne ramasse pas.
        //   • « sauver » : COURSE — les deux camps agissent (l'IA capture, l'allié récupère) sur les mêmes tuiles.
        //   • partout ailleurs (Liberer / escarmouche) : seul un ALLIÉ qui entre dessus recrute.
        // Chaque détection sort tôt sur _recrueConsumed → la première à résoudre une tuile gagne la course dessus.
        if (AiCapturesPaysans)
            CheckPaysanCapture();
        if (!IsProtectMission)
            CheckRecrueObjects();
        CheckChests();             // ouverture d'un coffre si un allié vient d'entrer dessus

        // Révélation modale du coffre : combat FIGÉ pendant toute la séquence (ouverture → objet → vol).
        if (ChestRevealActive)
        {
            UpdateChestReveal(dt);
            return;
        }

        // Révélation de recrue : carte au centre + inventaire ouvert ; au clic, le pion vole vers son slot
        // d'inventaire et ne rejoint l'armée qu'à la fin du vol. Combat FIGÉ pendant toute la séquence.
        if (_recrueReveal is { } gained)
        {
            // Fusion FAÇON PLACEMENT pendant la révélation (empiler → popup au centre → évolution). Pendant le
            // combat, _pending = la réserve NON déployée → fusionner dessus ne touche pas le plateau.
            if (EvoPlaying) { UpdateEvolutionAnimation(dt); return; }
            if (FusionOpen) { UpdateFusionPopup(); return; }

            if (_recruitChoice == null && !_recrueAdded)   // phase CARTE : on voit le pion, on décide
            {
                var vp = VirtualViewport;
                var availW = vp.Width - RightPanelWidth;
                var mouse = Context.Input.MousePosition;
                if (_run.IsReserveFull)
                {
                    // Réserve pleine : gérer la RÉSERVE (empiler/supprimer) pour faire de la place, ou
                    // ABANDONNER le pion (bouton / B ; le paysan reste compté pour l'objectif).
                    if (HandleReserveDrag())
                        return;
                    if (Context.Input.WasLeftClicked && RecruitAbandonBtnRect(availW, vp.Height).Contains(mouse)
                        || Context.Input.WasCancelPressed)
                    { _recrueReveal = null; _recrueAdded = false; }
                    return;
                }
                // Place dispo : un clic (carte / ailleurs) / Entrée / A → le pion vole vers la réserve.
                if (Context.Input.WasLeftClicked || Context.Input.WasKeyPressed(Keys.Enter) || Context.Input.WasConfirmPressed)
                {
                    var card = DraftCardRect(0, 1, availW, vp.Height);
                    _recruitFrom = new Vector2(card.X + card.Width / 2f, card.Y + card.Height / 2f);
                    _recruitChoice = gained;
                    _recruitHold = RecruitFlightDuration;
                    Context.Sounds.Play("unit_place");
                }
            }
            else if (_recruitChoice != null)               // phase VOL : le pion file vers l'inventaire
            {
                _recruitHold -= dt;
                if (_recruitHold <= 0f)
                {
                    _run.AddUnit(gained);   // la recrue rejoint l'armée (réserve), dans son slot
                    _pending.Add(gained);   // …ET la vue locale du panneau, sinon elle disparaît à la fin du vol
                    _recruitChoice = null;
                    _recrueAdded = true;
                    _recrueSettle = RecrueSettleDuration;
                }
            }
            else                                           // phase PAUSE : le pion est posé, on laisse voir le slot un instant
            {
                _recrueSettle -= dt;
                if (_recrueSettle <= 0f)
                {
                    _recrueReveal = null;
                    _recrueAdded = false;
                }
            }
            return;
        }

        // Animation d'attaque en cours : on gèle entrées, IA et fin de combat le temps des FX
        // (le domaine est déjà résolu ; la fin de partie ne s'affiche qu'après la dissolution).
        if (_fx.Active)
        {
            _fx.Update(dt);
            if (_fx.HasImpacted && !_impactHandled)
            {
                if (_fx.MoveOnly) OnReplayMoveLand();   // déplacement rejoué : rebond de pose, aucun impact
                else OnImpact();
            }
            _storm.Update(dt);    // les éclairs avancent en parallèle de la fin de l'anim d'attaque
            return;
        }

        // FX secondaire (orage) : peut se prolonger après l'anim d'attaque ; on gèle
        // jusqu'à son extinction (mise à jour d'un FX inactif = sans effet).
        if (_storm.Active)
        {
            _storm.Update(dt);
            return;
        }

        // « Riposte » : l'anim d'attaque (et l'orage) terminée, on rejoue la contre-attaque comme SECONDE
        // animation — AVANT que le combat ne se résolve ou que le camp adverse ne rejoue.
        if (_pendingRiposte is { } rip)
        {
            StartRiposteFx(rip);
            return;
        }

        // Mission spéciale : dès que TOUS les paysans sont libérés (dernière révélation close), on clôt la
        // mission AVANT que l'ennemi ne rejoue — sinon sa dernière action pourrait tuer le commandant et
        // transformer une réussite en défaite.
        if (_specialMission && PaysansTotal > 0 && PaysansResolved >= PaysansTotal)
        {
            CheckBattleEnd();
            if (_run.Phase != RunPhase.Battle)
                return;
        }

        // Bascule tooltip CONDENSÉ ↔ DÉTAILLÉ, valable sur les deux tours (avant l'aiguillage). X manette à
        // tout moment ; clic droit SEULEMENT s'il n'annule pas une sélection/un glisser — sinon l'annulation
        // prime (cf. UpdatePlayerTurn), le clic droit n'est donc pas volé.
        if (Context.Input.WasTertiaryPressed
            || (Context.Input.WasRightClicked && _selected is null && _combatDragFrom is null))
        {
            _detailedTooltip = !_detailedTooltip;
            Context.Sounds.Play("menu_click");
        }

        // Tremblement de zone (« Séisme » / « Impact ») : on GÈLE le jeu (IA comme joueur) tant qu'il joue,
        // pour laisser le temps de LIRE les dégâts avant que le tour suivant n'enchaîne. Le tremor a déjà été
        // mis à jour en tête de frame (cf. _tremor.Update) : il s'éteint seul, pas de risque de blocage.
        if (_tremor.Active)
            return;

        // « Revoir la dernière action de l'IA » : R (clavier) ou RB (manette), pendant le tour du JOUEUR et hors
        // manipulation d'un pion. Rejoue l'animation par-dessus le plateau courant (le moteur a déjà avancé) et gèle
        // le tour le temps des FX (cf. _fx.Active plus haut). Le tuto n'utilise pas UpdateAiTurn : rien à revoir.
        if (_match.CurrentTurn == Faction.Player && _tutorial == null && _combatDragFrom is null
            && _lastAiAction is { } replay
            && (Context.Input.WasKeyPressed(Keys.R) || Context.Input.WasRightShoulderPressed))
        {
            StartAiReplay(replay);
            return;
        }

        // Tuto : leçons de commandes INTERACTIVES intercalées dans le combat (caméra, zones de danger, revoir
        // action). Chacune GÈLE le combat (aucun tour ne se joue) tant que le geste n'est pas fait ; le bouton
        // PASSER reste dispo. Les autres étapes ne matchent aucun cas et laissent l'aiguillage des tours suivre.
        if (_tutorial is { } tut)
        {
            switch (tut.Step)
            {
                case TutorialStep.CameraLesson:
                    // Un pan (clavier / stick droit / molette centrale) OU un simple clic valide : le plateau du
                    // tuto tient à l'écran, panner peut ne rien révéler, on n'exige donc pas un déplacement réel.
                    if (TutorialCameraPanned() || Advanced())
                        tut.Advance();                              // → DangerLesson
                    return;
                case TutorialStep.DangerLesson:
                    if (Context.Input.IsKeyDown(Keys.Space) || Context.Input.IsRightTriggerDown)
                        _tutorialHold += dt;                        // maintien cumulé (les cases menacées s'allument)
                    if (_tutorialHold >= TutorialDangerHoldSeconds)
                        tut.Advance();                              // → Chest
                    return;
                case TutorialStep.ReplayLesson:
                    // _lastAiAction est garanti non nul ici : l'ennemi a bougé pendant la phase Move (cf. script).
                    if (_lastAiAction is { } rep
                        && (Context.Input.WasKeyPressed(Keys.R) || Context.Input.WasRightShoulderPressed))
                    {
                        StartAiReplay(rep);
                        tut.Advance();                              // → Attack (l'anim se joue par-dessus via _fx.Active)
                    }
                    return;
            }
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

        // Tuto : le coffre est ouvert et sa révélation terminée → on reprend le combat. Aucun replacement de
        // l'ennemi : il a avancé pendant la leçon comme n'importe quel tour, et la distance retombe juste
        // (cf. PlaceTutorialEnemy).
        if (_tutorial is { Step: TutorialStep.Chest } chestStep
            && (_chestConsumed.Count > 0 || chestStep.Chest is null) && !ChestRevealActive)
            chestStep.Advance();                            // Chest → Move

        // Filet ANTI-BLOCAGE : si plus aucun ennemi n'est sur le plateau alors que le script en attend un
        // (mort hors script, cas qui ne devrait plus arriver), on saute droit à l'encart du commandant
        // plutôt que d'attendre une condition qui ne viendra jamais.
        if (_tutorial is { } guide && guide.Step is TutorialStep.Chest or TutorialStep.Move
                or TutorialStep.ReplayLesson or TutorialStep.Attack
            && !_fx.Active && !ChestRevealActive
            && !_match.Units().Any(u => u.Unit.Faction == Faction.Enemy))
            while (guide.Step != TutorialStep.Commander)
                guide.Advance();

        // Tuto : dès que le soldat peut frapper l'ennemi, on passe à la leçon « revoir action » (l'ennemi a
        // forcément déjà bougé), puis à l'étape « attaque ».
        if (_tutorial is { Step: TutorialStep.Move }
            && _match.AttackTargets(_tutorial.PlayerSoldier).Contains(_tutorial.EnemySoldier))
            _tutorial.Advance();                            // Move → ReplayLesson

        if (!_fx.Active)        // une attaque vient peut-être de lancer une animation : on attend
            CheckBattleEnd();
    }

    /// <summary>
    /// Tour de l'ennemi en TUTORIEL. Il JOUE À CHAQUE TOUR — l'alternance doit rester visible : il avance
    /// d'une case vers le soldat (le coup légal qui réduit le plus la distance), et ne frappe QUE s'il a
    /// déjà été touché (la riposte scénarisée). Le script garantit qu'il n'est jamais adjacent et intact au
    /// moment de son tour (cf. <see cref="PlaceTutorialEnemy"/>), donc il ne passe jamais son tour.
    /// Respecte `_aiTimer` pour que le déplacement soit visible.
    /// </summary>
    private void TutorialEnemyTurn(GameTime gameTime)
    {
        _aiTimer -= gameTime.ElapsedGameTime.TotalSeconds;
        if (_aiTimer > 0)
            return;

        var from = _tutorial!.EnemySoldier;
        var target = _tutorial.PlayerSoldier;

        // Adjacent au soldat → l'ennemi CONTRE-ATTAQUE (anim via ResolveAttack) au lieu d'avancer. Mais
        // SEULEMENT s'il a DÉJÀ été frappé : le joueur porte toujours le premier coup, quelle que soit la
        // route qu'il a prise. Sinon le soldat (12 PV) encaisserait deux coups de 10 et mourrait, et le
        // tuto attendrait pour toujours une étape « attaque » que plus personne ne peut jouer.
        if (_match.AttackTargets(from).Contains(target))
        {
            if (_match.UnitAt(from) is { } enemy && enemy.Hp < enemy.MaxHp)
            {
                if (ResolveAttack(from, target) != MoveKind.Invalid)
                    RecordAiAttackReplay();   // la contre-attaque est « revoyable » (leçon replay)
            }
            else
                _match.PassTurn();   // intact : il attend le premier coup du joueur
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
            TryMoveWithFx(from, best);
            if (_match.UnitAt(best) is { } moved) FaceToward(moved, from, best);
            TriggerLanding(best);
            Context.Sounds.Play("unit_move");
            _tutorial.EnemySoldier = best;     // l'ennemi suit sa nouvelle case
            RecordAiMoveReplay(from, best);    // ce déplacement est « revoyable » (leçon replay)
        }
        else
        {
            _match.PassTurn();                 // adjacent ou bloqué : on rend la main au joueur
        }
    }

    /// <summary>Vrai si le joueur actionne une commande de déplacement de caméra cette frame (mêmes entrées que
    /// <see cref="UpdateCamera"/>) : flèches / ZQSD / WASD, stick droit, ou glisser à la molette. Sert à valider
    /// la leçon « caméra » du tuto sans dépendre d'un déplacement effectif (le plateau du tuto tient à l'écran).</summary>
    private bool TutorialCameraPanned()
    {
        var i = Context.Input;
        return i.IsKeyDown(Keys.Left) || i.IsKeyDown(Keys.Right) || i.IsKeyDown(Keys.Up) || i.IsKeyDown(Keys.Down)
            || i.IsKeyDown(Keys.Q) || i.IsKeyDown(Keys.D) || i.IsKeyDown(Keys.Z) || i.IsKeyDown(Keys.S)
            || i.IsKeyDown(Keys.A) || i.IsKeyDown(Keys.W)
            || i.RightStick != Vector2.Zero || i.IsMiddleDown;
    }

    private static int Chebyshev(Cell a, Cell b) =>
        System.Math.Max(System.Math.Abs(a.Column - b.Column), System.Math.Abs(a.Row - b.Row));

    /// <summary>
    /// Revue de la carte : passe en revue chaque donnée (PV → Puissance → Mouvement → Portée), une par une.
    /// Le joueur avance À SON RYTHME (clic / ESPACE / ENTREE / A) : aucune bulle ne défile toute seule.
    /// Après la dernière → étape Move.
    /// </summary>
    private void UpdateTutorialCardReview()
    {
        if (!Advanced() && !Context.Input.WasKeyPressed(Keys.Space))
            return;

        _tutorialCardIndex++;
        if (_tutorialCardIndex >= TutorialCardStats)
            _tutorial!.Advance();                   // ReviewCard → StartCombat (on peut lancer le combat)
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

        // Encart commandant : dernière étape du COMBAT. On bascule ensuite en préparation guidée (fusion,
        // équipement, arbre), qui se joue en phase de placement sur la même map.
        if (t.Step == TutorialStep.Commander && !_fx.Active && Advanced())
        {
            t.Advance();                  // → FusionIntro
            BeginTutorialPreparation();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Vrai si le joueur demande à passer le tuto : clic sur le bouton, ou BACK à la manette. (Pas X : la
    /// préparation guidée enseigne justement « X = fusionner » dans la réserve.)
    /// </summary>
    private bool TutorialSkipPressed() =>
        (TutorialSkipRect().Contains(Context.Input.MousePosition) && Context.Input.WasLeftClicked)
        || Context.Input.WasSelectPressed;

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
        else if (t.Step == TutorialStep.FusionDo && !FusionOpen && !EvoPlaying)
        {
            // Les trois soldats à empiler : chaque portrait de réserve est cerclé.
            for (var i = 0; i < _pending.Count; i++)
            {
                var card = PanelCardRect(i);
                DrawRectBorder(sb, Inflate(new Rectangle(card.X - InvGapX / 2, card.Y, card.Width + InvGapX, card.Height + 12), 3), pcol, 3);
            }
        }
        else if (t.Step == TutorialStep.EquipDo && _dragEquip == null)
        {
            DrawRectBorder(sb, Inflate(EquipRowRect(0), 3), pcol, 3);
        }
        else if (t.Step == TutorialStep.StartCombat)
        {
            DrawRectBorder(sb, Inflate(FightButtonRect(), 3), pcol, 3);   // c'est LUI qui lance le combat
        }
        else if (t.Step == TutorialStep.TreeOpen)
        {
            DrawRectBorder(sb, Inflate(CommandTreeButtonRect(), 3), pcol, 3);   // c'est LUI qui ouvre l'arbre
        }
        else if (t.Step == TutorialStep.RerollLesson)
        {
            DrawRectBorder(sb, Inflate(RerollIconRect(), 3), pcol, 3);   // l'icône de relance, expliquée
        }
        else if (t.Step == TutorialStep.Chest && t.Chest is { } chestCell)
        {
            DrawZoneBorder(sb, board, chestCell, pcol, 3);
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
            case TutorialStep.CameraLesson:
                DrawPawnPopup(sb, board, t.Commander,
                    Loc.T(Context.Input.UsingGamepad ? "tuto.camera_gp" : "tuto.camera"), Loc.T("tuto.next"));
                break;
            case TutorialStep.DangerLesson:
                DrawPawnPopup(sb, board, t.EnemySoldier,
                    Loc.T(Context.Input.UsingGamepad ? "tuto.danger_gp" : "tuto.danger"), null);
                break;
            case TutorialStep.Chest:
                if (t.Chest is { } chestCell)
                    DrawPawnPopup(sb, board, chestCell, Loc.T("tuto.chest"), null);
                break;
            case TutorialStep.Move:
                if (_match.CurrentTurn == Faction.Enemy)
                    DrawPawnPopup(sb, board, t.EnemySoldier, Loc.T("tuto.enemy_plays"), null);
                else
                    DrawPawnPopup(sb, board, t.PlayerSoldier, Loc.T("tuto.move"), null);
                break;
            case TutorialStep.ReplayLesson:
                if (!_fx.Active)   // pendant le replay lui-même, on n'affiche pas de pop par-dessus l'anim
                    DrawPawnPopup(sb, board, t.EnemySoldier,
                        Loc.T(Context.Input.UsingGamepad ? "tuto.replay_gp" : "tuto.replay"), null);
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

            // ── Préparation guidée ──────────────────────────────────────────────────────────────
            case TutorialStep.FusionIntro:
                DrawTutorialBigPanel(sb, viewport, Loc.T("tuto.fusion_title"), Loc.T("tuto.fusion_body"), Loc.T("tuto.intro_continue"));
                break;
            case TutorialStep.FusionDo:
                if (!FusionOpen && !EvoPlaying && _pending.Count > 0)
                    DrawAnchoredPopup(sb, PanelCardRect(0), Loc.T("tuto.fusion_do"), null);
                break;
            case TutorialStep.RerollLesson:
                DrawAnchoredPopup(sb, RerollIconRect(), Loc.T("tuto.reroll"), Loc.T("tuto.next"));
                break;
            case TutorialStep.DeployFused:
                DrawPawnPopup(sb, board, new Cell(Columns / 2, Rows - 2), Loc.T("tuto.deploy_fused"), null);
                break;
            case TutorialStep.EquipIntro:
                DrawTutorialBigPanel(sb, viewport, Loc.T("tuto.equip_title"), Loc.T("tuto.equip_body"), Loc.T("tuto.intro_continue"));
                break;
            case TutorialStep.EquipDo:
                DrawAnchoredPopup(sb, EquipRowRect(0), Loc.T("tuto.equip_do"), null);
                break;
            case TutorialStep.TreeIntro:
                DrawTutorialBigPanel(sb, viewport, Loc.T("tuto.tree_title"), Loc.T("tuto.tree_body"), Loc.T("tuto.intro_continue"));
                break;
            case TutorialStep.TreeOpen:
                DrawAnchoredPopup(sb, CommandTreeButtonRect(),
                    Loc.T(Context.Input.UsingGamepad ? "tuto.tree_open_gp" : "tuto.tree_open"), null);
                break;
            case TutorialStep.TreeDo:
                break;   // l'arbre est ouvert par-dessus : le rappel est dessiné après la modale
            case TutorialStep.Done:
                if (!_fx.Active)
                    DrawTutorialBigPanel(sb, viewport, Loc.T("tuto.victory_title"), Loc.T("tuto.recap_body"), Loc.T("tuto.continue"));
                break;
        }

        // 3) Bouton « Passer le tuto » (toujours visible) — le rappel « (X) » seulement à la manette.
        var skip = TutorialSkipRect();
        var hover = skip.Contains(Context.Input.MousePosition);
        var off = Context.Style.DrawButton(sb, skip, UiStyle.StateOf(hover, hover && Context.Input.IsLeftDown));
        var label = Loc.T("tuto.skip") + (Context.Input.UsingGamepad ? " (BACK)" : "");
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
        var soldier = Domaines.Dame.BaseClass;
        // La carte du pion qu'on vient de poser, à sa place d'aperçu habituelle (à gauche du panneau).
        var cardRect = new Rectangle(PanelRect().X - CombatCardGap - CombatCardW,
            (viewport.Height - CombatCardH) / 2, CombatCardW, CombatCardH);
        DrawCardLayout(sb, cardRect, soldier, Faction.Player, Domaine.Dame, soldier.MaxHp, soldier.MaxHp);

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
            Context.Font.Draw(sb, line, new Vector2(bubble.X + pad, ty), 1, Palette.White, preserveCase: true);
            ty += 12;
        }
        var contKey = Context.Input.UsingGamepad ? "tuto.card_continue_gp" : "tuto.card_continue";
        Context.Font.DrawCentered(sb, Loc.T(contKey),
            new Rectangle(bubble.X, bubble.Bottom - 16, bubble.Width, 10), 1, Palette.Cyan1, preserveCase: true);
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
        // Au-dessus de la case : sur le plateau, une bulle posée sur le côté masque les pions voisins.
        DrawAnchoredPopup(sb, new Rectangle((int)top.X, (int)top.Y, size, size), text, footer, preferAbove: true);
    }

    /// <summary>
    /// Bulle d'aide ANCRÉE à un élément (rectangle écran), clampée à l'écran. Par défaut à DROITE de
    /// l'élément (bascule à gauche si ça déborde) : c'est ce qu'il faut pour un élément du PANNEAU, où le
    /// côté donne sur le plateau libre. <paramref name="preferAbove"/> (bulles ancrées à une CASE, cf.
    /// <see cref="DrawPawnPopup"/>) la pose plutôt AU-DESSUS, centrée, puis dessous : sur le plateau, une
    /// bulle latérale recouvre les pions voisins.
    /// </summary>
    private void DrawAnchoredPopup(SpriteBatch sb, Rectangle anchor, string text, string? footer,
        bool preferAbove = false)
    {
        var vp = VirtualViewport;
        const int pad = 12;
        const int bw = 340;
        const int gap = 18;      // dégage le sprite du pion, dessiné au-dessus de sa case
        const int margin = 20;   // marge minimale au bord de l'écran
        var lines = WrapText(text, bw - 2 * pad, 1);
        var bh = pad + lines.Count * 12 + (footer != null ? 14 : 0) + pad;

        int bx, by;
        if (preferAbove && anchor.Y - gap - bh >= margin)                            // au-dessus, centrée
        {
            bx = anchor.Center.X - bw / 2;
            by = anchor.Y - gap - bh;
        }
        else if (preferAbove && anchor.Bottom + gap + bh <= vp.Height - margin)      // sinon dessous
        {
            bx = anchor.Center.X - bw / 2;
            by = anchor.Bottom + gap;
        }
        else                                                                         // sinon sur le côté
        {
            bx = anchor.Right + 14;
            if (bx + bw > vp.Width - margin)
                bx = anchor.X - 14 - bw;
            by = anchor.Y + anchor.Height / 2 - bh / 2;
        }
        bx = System.Math.Clamp(bx, margin, vp.Width - bw - margin);
        by = System.Math.Clamp(by, margin, vp.Height - bh - margin);

        var bubble = new Rectangle(bx, by, bw, bh);
        Context.Style.DrawPanel(sb, bubble);
        var ty = bubble.Y + pad;
        foreach (var line in lines)
        {
            // Consignes du tuto : phrases en casse normale (preserveCase), pas en capitales comme l'UI.
            Context.Font.Draw(sb, line, new Vector2(bubble.X + pad, ty), 1, Palette.Yellow2, preserveCase: true);
            ty += 12;
        }
        if (footer != null)
            Context.Font.DrawCentered(sb, footer,
                new Rectangle(bubble.X, bubble.Bottom - 14, bubble.Width, 10), 1, Palette.Cyan1, preserveCase: true);
    }

    /// <summary>Grand encart central : TITRE (échelle 3) + corps replié (échelle 2) + invite (bas).</summary>
    private void DrawTutorialBigPanel(SpriteBatch sb, Viewport viewport, string title, string body, string footer)
    {
        var pw = System.Math.Min(viewport.Width - 120, 700);
        var lines = WrapText(body, pw - 48, 2);
        var ph = 20 + 28 + 14 + lines.Count * 18 + 24 + 16;
        var box = new Rectangle((viewport.Width - pw) / 2, (viewport.Height - ph) / 2, pw, ph);
        Context.Style.DrawPanel(sb, box);

        // Le TITRE reste en capitales (c'est un libellé d'UI) ; le corps et l'invite sont des phrases.
        Context.Font.DrawCentered(sb, title, new Rectangle(box.X, box.Y + 20, box.Width, 24), 3, Palette.Yellow2);
        var y = box.Y + 20 + 28 + 14;
        foreach (var line in lines)
        {
            Context.Font.DrawCentered(sb, line, new Rectangle(box.X, y, box.Width, 16), 2, Palette.White, preserveCase: true);
            y += 18;
        }
        Context.Font.DrawCentered(sb, footer, new Rectangle(box.X, box.Bottom - 22, box.Width, 12), 1, Palette.Cyan1,
            preserveCase: true);
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
            Context.Font.DrawCentered(sb, line, new Rectangle(box.X, y, box.Width, 16), 2, Palette.Yellow2, preserveCase: true);
            y += 18;
        }
        if (footer != null)
            Context.Font.DrawCentered(sb, footer, new Rectangle(box.X, box.Bottom - 22, box.Width, 12), 1, Palette.Cyan1,
                preserveCase: true);
    }

    /// <summary>
    /// Consigne du tuto pendant l'étape « acheter un nœud » : dessinée APRÈS la modale de l'arbre (sinon
    /// le voile de celle-ci la recouvrirait), en bandeau bas, hors du panneau.
    /// </summary>
    private void DrawTutorialTreeHint(SpriteBatch sb, Viewport viewport)
    {
        // L'indice suit le périphérique courant (clic à la souris, A à la manette).
        var text = Loc.T(Context.Input.UsingGamepad ? "tuto.tree_do_gp" : "tuto.tree_do");
        var w = Context.Font.Measure(text, 1) + 40;
        var box = new Rectangle((viewport.Width - w) / 2, viewport.Height - 52, w, 30);

        sb.Begin(samplerState: SamplerState.PointClamp);
        Context.Style.DrawPanel(sb, box);
        Context.Font.DrawCentered(sb, text, box, 1, Palette.Yellow2, preserveCase: true);
        sb.End();
    }

    private void CheckBattleEnd()
    {
        if (_tutorial != null)   // en tuto : pas de recrutement/défaite/sauvegarde — géré par le guide
            return;

        // Défaite (commandant tombé / armée anéantie) : décisive dans TOUS les modes, y compris spéciale.
        if (_match.IsOver && _match.Winner == Faction.Enemy)
        {
            AccumulateCombatStats();   // récap : contribution du combat perdu (avant permadeath)
            _defeatReason = CommanderAlive() ? Loc.T("defeat.army_destroyed") : Loc.T("defeat.commander_fallen");
            _run.Defeat();
            FinishBattleEnd();
            return;
        }

        // Mission spéciale (mode objectif) : clôture quand TOUS les paysans sont résolus (libérés/capturés),
        // OU quand la limite de tours est atteinte (« trop tard » — jamais une défaite hors commandant).
        if (_specialMission)
        {
            bool done;
            if (IsSauverMission)
            {
                // « Sauver » : COURSE sans limite de tours. La mission CONTINUE tant qu'il reste des ennemis ;
                // résoudre toutes les tuiles paysan (récupérées OU capturées) ne la clôt PLUS à soi seul. Elle
                // se termine seulement quand :
                //  • le quota devient IMPOSSIBLE (trop de captures : max récupérable = total − capturés < requis)
                //    → défaite prononcée par le quota gate juste en dessous ;
                //  • OU tous les ennemis sont vaincus → plus aucune menace : on RÉCUPÈRE d'abord automatiquement
                //    les paysans restants, un par un (même révélation/réserve que si on marchait dessus), puis on
                //    clôt (victoire, le quota étant alors forcément tenu).
                var noEnemiesLeft = !_match.Units().Any(u => u.Unit.Faction == Faction.Enemy);
                if (PaysansTotal - PaysansCaptured < PaysansRequired)
                {
                    done = true;   // quota hors d'atteinte : clôture (la défaite est prononcée au quota gate)
                }
                else if (noEnemiesLeft && PaysanCells() is { Count: > 0 } remaining)
                {
                    // Menace éliminée : on déclenche la récupération d'UN paysan restant puis on laisse la
                    // révélation se jouer (le combat est figé pendant ce temps). Rappelée chaque frame tant qu'il
                    // en reste, jusqu'à ce que tous soient résolus → clôture ci-dessous.
                    TriggerRecrue(remaining[0]);
                    return;
                }
                else
                {
                    // Ne se clôt qu'à l'élimination de TOUS les ennemis (les paysans restants ayant alors été
                    // récupérés juste au-dessus). Sinon la mission continue, même toutes les tuiles résolues.
                    done = noEnemiesLeft;
                }
            }
            else
            {
                // « Protéger » : dès qu'il n'y a PLUS d'adversaire, les paysans restants sont sauvés → victoire
                // IMMÉDIATE (inutile d'attendre la fin des tours ; l'élimination ne clôt pas seule une mission).
                var noEnemiesLeft = IsProtectMission && !_match.Units().Any(u => u.Unit.Faction == Faction.Enemy);
                done = (PaysansTotal > 0 && PaysansResolved >= PaysansTotal) || _specialRoundsLeft <= 0 || noEnemiesLeft;
            }
            if (!done)
                return;

            // QUOTA DE DIFFICULTÉ : la mission est close, mais si le joueur n'a pas sauvé assez de paysans
            // la run est perdue — au même titre que la chute du commandant. Testé AVANT toute complétion :
            // ni points de commandement, ni recrutement, ni récompense ne doivent être accordés.
            if (PaysansSaved < PaysansRequired)
            {
                AccumulateCombatStats();   // récap : contribution du combat (quota de paysans manqué → défaite)
                _defeatReason = Loc.T("defeat.paysans", PaysansSaved, PaysansRequired);
                _run.Defeat();
                FinishBattleEnd();
                return;
            }

            AccumulateCombatStats();   // récap : contribution du combat (AVANT sync/permadeath)
            _run.Stats.AddPaysansSaved(PaysansSaved);   // paysans sauvés/libérés de cette mission
            SyncKillsToSpecs();   // fige les kills du combat sur les gabarits survivants AVANT permadeath

            // Bilan FIGÉ ici : la complétion va retirer les pertes du roster (permadeath) et remettre le
            // compteur de tours à zéro au combat suivant. Modale à valider avant la récupération des pions.
            var casualties = PlayerCasualties();
            _specialRecap = new SpecialRecap(_specialObjective, PaysansSaved, PaysansTotal,
                SpecialTurnBudget() - System.Math.Max(0, _specialRoundsLeft), SpecialTurnBudget(),
                casualties.Count, PaysansRequired);

            if (IsProtectMission)
            {
                // « Protéger » : PAS de draft. On tire 1 recrue par paysan sauvé et on affiche l'écran de
                // récompense (_protectReward) ; le clic les enverra TOUS en réserve (cf. UpdateRecruitment).
                var rewards = RollProtectedPaysanRecruits();
                _run.CompleteSpecialNoDraft(casualties);   // retire les pertes, va à l'écran post-combat
                GrantEliteReplacements(casualties);        // nœud « relève » : un T1 par unité tier 2+ tombée
                GrantCommanderHitPoints();                 // source « sur coup reçu » (commandant Lancier)
                _protectReward = rewards.Count > 0 ? rewards : null;   // 0 sauvé → rien à montrer (auto-skip)
                _rewardKeep.Clear();
                for (var k = 0; _protectReward != null && k < _protectReward.Count; k++)
                    _rewardKeep.Add(true);   // tous cochés par défaut (le joueur décoche si pas la place)
                _rewardFocus = 0;
            }
            else
            {
                _run.CompleteCombat(casualties, _enemyKillOrder);   // « libérer » : draft normal
                GrantEliteReplacements(casualties);
                GrantCommanderHitPoints();
            }
            FinishBattleEnd();
            return;
        }

        // Escarmouche / boss : issue classique (dernier camp debout / essentiel tué).
        if (!_match.IsOver)
            return;
        if (_match.Winner == Faction.Player)
        {
            AccumulateCombatStats();   // récap : contribution du combat (AVANT sync/permadeath)
            SyncKillsToSpecs();   // fige les kills du combat sur les gabarits survivants AVANT permadeath
            var casualties = PlayerCasualties();
            UnlockBossCommanderIfFinal();   // battre le boss de dernière phase débloque son commandant lié
            _run.CompleteCombat(casualties, _enemyKillOrder);
            GrantEliteReplacements(casualties);
            GrantCommanderHitPoints();
        }
        FinishBattleEnd();
    }

    /// <summary>
    /// Battre le boss de la DERNIÈRE phase (cf. <see cref="Run.IsFinalBoss"/>) débloque le commandant que ce
    /// boss porte (<see cref="BossDef.UnlocksCommander"/>), mémorisé dans le profil (méta-progression).
    /// Sans effet si la mission n'est pas ce boss ou si le boss ne débloque personne (ex. la Brute). Idempotent.
    /// </summary>
    private void UnlockBossCommanderIfFinal()
    {
        if (!_run.IsFinalBoss)
            return;
        if (_run.BossOfPhase(_run.PhaseIndex).UnlocksCommander is { } id && Context.Saves.UnlockCommander(id))
            _run.Stats.AddUnlockedCommander(Loc.TOr("commander." + id, id));   // NOUVEAU déblocage → récap
    }

    /// <summary>Gabarits du roster morts pendant le combat (permadeath : retirés à la complétion).</summary>
    private List<UnitSpec> PlayerCasualties() =>
        _playerSpec.Where(kv => !kv.Key.IsAlive).Select(kv => kv.Value).ToList();

    /// <summary>
    /// Verse dans <see cref="Run.Stats"/> la contribution du combat qui se termine : dégâts par CLASSE (compteur
    /// par combat de chaque pion), ennemis tués (delta de kills de ce combat) et pions perdus (hors commandant).
    /// À appeler UNE fois par fin de combat et AVANT <see cref="SyncKillsToSpecs"/> / la complétion : le delta
    /// de kills se lit tant que <c>spec.Kills</c> porte encore la valeur d'AVANT ce combat.
    /// </summary>
    private void AccumulateCombatStats()
    {
        var kills = 0;
        var lost = 0;
        foreach (var (unit, spec) in _playerSpec)
        {
            _run.Stats.AddDamage(unit.Class.Name, unit.DamageDealt);
            kills += System.Math.Max(0, unit.Kills - spec.Kills);
            if (!unit.IsAlive && !spec.Essential)
                lost++;
        }
        _run.Stats.AddKills(kills);
        _run.Stats.AddUnitsLost(lost);
    }

    /// <summary>Nœud « relève » (arbre TROUPES) : un pion T1 déjà vu arrive en réserve par unité tier 2+ tombée.
    /// Appelé APRÈS la complétion (les pertes retirées ont libéré la place). Voir <see cref="Run.GrantEliteDeathReplacements"/>.</summary>
    private void GrantEliteReplacements(IReadOnlyList<UnitSpec> casualties) =>
        _run.GrantEliteDeathReplacements(casualties, new System.Random(), Context.Saves.IsUnitDiscovered);

    /// <summary>
    /// Source de points « sur coup reçu » (commandant Lancier) : crédite la <see cref="Run"/> selon le nombre
    /// de fois où le COMMANDANT a été touché ce combat (<see cref="ChessArmy.Core.Battle.Unit.TimesHit"/>), plafonné
    /// par le commandant. Sans effet pour un commandant dont ce n'est pas la source. À appeler sur combat gagné
    /// (le commandant est alors vivant, donc encore dans <c>_playerSpec</c>).
    /// </summary>
    private void GrantCommanderHitPoints()
    {
        var commander = _playerSpec.Keys.FirstOrDefault(u => u.IsEssential);
        if (commander != null)
            _run.GrantCommanderHitPoints(commander.TimesHit);
    }

    /// <summary>
    /// Feedback « pendant le combat » de la source de points « sur coup reçu » (commandant Lancier) : à chaque
    /// coup encaissé par le commandant qui RAPPORTE vraiment un point (sous le plafond <c>OnHitCap</c>), fait
    /// jaillir un « +N » doré sur sa case + un son. Idempotent (ne resignale jamais un coup déjà montré) : le
    /// CRÉDIT effectif reste groupé à la clôture (cf. <see cref="GrantCommanderHitPoints"/>) — ce n'est QUE de
    /// l'affichage. Sans effet pour un commandant dont ce n'est pas la source (<c>OnHitPoints = 0</c>). Appelé
    /// chaque frame de combat : détecte l'augmentation de <see cref="ChessArmy.Core.Battle.Unit.TimesHit"/>.
    /// </summary>
    private void SpawnCommanderPointFeedback()
    {
        if (_run is null || _run.CommanderDef.OnHitPoints <= 0)
            return;
        var commander = _playerSpec.Keys.FirstOrDefault(u => u.IsEssential && u.IsAlive);
        if (commander is null || _match.CellOf(commander) is not { } cell)
            return;

        var earned = System.Math.Min(commander.TimesHit, _run.CommanderDef.OnHitCap);   // coups qui rapportent (plafonnés)
        while (_commanderPtHitsShown < earned)
        {
            _commanderPtHitsShown++;
            _damagePopups.SpawnText(cell, Loc.T("fx.command_point", _run.CommanderDef.OnHitPoints), Palette.Yellow1);
            Context.Sounds.Play("command_point");
        }
    }

    /// <summary>
    /// Recopie le total de kills des pions JOUEUR encore vivants sur leur gabarit persistant (cumul à vie).
    /// Les morts sont ignorés : ils quittent le roster (permadeath) et emportent leur compteur. À appeler
    /// à la clôture d'un combat non perdu, AVANT <see cref="Run.CompleteCombat"/> qui retire les pertes.
    /// </summary>
    private void SyncKillsToSpecs()
    {
        foreach (var (unit, spec) in _playerSpec)
            if (unit.IsAlive)
            {
                spec.Kills = unit.Kills;
                // « Queue de phénix » BRISÉE en combat (renaissance) : l'équipement disparaît AUSSI du gabarit
                // persistant — sinon le pion le récupérerait au combat suivant. cf. Unit.ReviveConsumingEquipment.
                if (unit.Equipment == null && spec.Equipment != null)
                    spec.Equipment = null;
            }
    }

    /// <summary>
    /// Récompense de la mission « protéger » : 1 recrue (pion tier-1 déjà vu, comme une tuile recrue) par
    /// paysan encore VIVANT à la fin. Tirée mais PAS encore ajoutée au roster — l'écran de récompense
    /// (_protectReward) les montre, puis le clic les verse en réserve (cf. UpdateRecruitment).
    /// </summary>
    private List<UnitSpec> RollProtectedPaysanRecruits()
    {
        var rng = new System.Random();
        var list = new List<UnitSpec>();
        // Borné au plafond de réserve : on ne peut de toute façon pas en garder plus (évite un écran de
        // récompense incollectable si la map a beaucoup de paysans).
        var n = System.Math.Min(PaysansProtected, _run.ReserveLimit);
        for (var k = 0; k < n; k++)
            list.Add(_run.RollSeenTier1(rng, Context.Saves.IsUnitDiscovered));
        return list;
    }

    /// <summary>Clôture commune d'un combat (recrutement / victoire / défaite) : sélection, focus, sons, sauvegarde.</summary>
    private void FinishBattleEnd()
    {
        ClearSelection();
        _recruitFocus = 0;   // focus manette sur la première carte du draft

        // Repère sonore de fin : campagne gagnée/perdue, ou combat remporté (→ recrutement).
        if (_run.Phase == RunPhase.Victory) Context.Sounds.Play("victory");
        else if (_run.Phase == RunPhase.Defeat) Context.Sounds.Play("defeat");
        else Context.Sounds.Play("combat_won");   // recrutement : escarmouche, boss non final, mission spéciale

        if (_run.Phase == RunPhase.Recruitment)
            SetupReserveScreen();   // réserve = armée dans _pending → fusion façon placement (empiler → popup)

        // Fin de run (boss vaincu ou commandant tombé) : la sauvegarde n'a plus lieu d'être.
        if (_run.Phase is RunPhase.Victory or RunPhase.Defeat)
            Context.Saves.DeleteSlot(_saveSlot);
    }

    /// <summary>
    /// Prépare l'écran post-combat (recrutement / récompense) : la RÉSERVE = l'armée (hors commandant) mise
    /// dans <see cref="_pending"/>, exactement comme l'inventaire du placement — ainsi la FUSION réutilise le
    /// MÊME système (empiler 3 identiques → popup de choix au centre → évolution). Reset des états fusion/drag.
    /// </summary>
    private void SetupReserveScreen()
    {
        _fusionGroup.Clear();
        _fusionCell = null;
        _carryPile = false;
        _fusionReserveSlot = 0;
        _evoPhase = EvoPhase.None;
        _dragSpec = null;
        _dragFrom = null;
        _pending.Clear();
        _pending.AddRange(ArmyMinusCommander());
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
        if (_selected is { } selH && _healTargets.Contains(cell))   // trait « Soin » : cible un allié blessé
        {
            ResolveHeal(selH, cell);
            EndPlayerAction();
            return;
        }
        if (_selected is { } sel2 && _legalMoves.Contains(cell))
        {
            TryMoveWithFx(sel2, cell);
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
            _match.HealTargets(cell, _healTargets);
            FilterTutorialActions();
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
    /// <summary>
    /// TUTORIEL : réduit les coups offerts (<see cref="_legalMoves"/> / <see cref="_attackTargets"/>) à UN
    /// SEUL — celui que la leçon attend. Le joueur ne peut donc rien faire d'autre, ni se mettre dans un état
    /// dont le guide ne sait pas sortir, et l'affichage ne lui montre jamais un coup qu'on refuserait ensuite.
    /// Un filtre qui ne laisserait AUCUN coup est abandonné : mieux vaut un tuto bavard qu'un tuto figé.
    /// </summary>
    private void FilterTutorialActions()
    {
        if (_tutorial is not { } t)
            return;

        _healTargets.Clear();   // le tuto n'a pas de soigneur : aucun soin proposé pendant les leçons

        switch (t.Step)
        {
            case TutorialStep.Chest:
                _attackTargets.Clear();
                KeepOnlyMove(t.Chest);                       // le coffre, et rien d'autre
                break;

            case TutorialStep.Move:
                _attackTargets.Clear();
                KeepOnlyMove(BestStepToward(t.PlayerSoldier, t.EnemySoldier));
                break;

            case TutorialStep.Attack:
                _legalMoves.Clear();                         // plus de déplacement : on frappe
                _attackTargets.RemoveAll(c => c != t.EnemySoldier);
                break;

            default:
                _attackTargets.Clear();   // hors des étapes de combat scénarisées, aucune attaque
                break;
        }
    }

    /// <summary>Ne garde que <paramref name="cell"/> parmi les déplacements légaux — sauf si elle n'y figure pas.</summary>
    private void KeepOnlyMove(Cell? cell)
    {
        if (cell is { } c && _legalMoves.Contains(c))
            _legalMoves.RemoveAll(m => m != c);
    }

    /// <summary>
    /// LE pas à jouer vers l'ennemi : parmi les coups légaux, celui qui réduit le plus la distance, départagé
    /// par l'alignement de colonne (marche droit devant) puis par la rangée — donc toujours le même, sans
    /// dépendre de l'ordre du buffer. Null si aucun coup ne rapproche : le filtre laisse alors tout passer.
    /// </summary>
    private Cell? BestStepToward(Cell soldier, Cell enemy)
    {
        Cell? best = null;
        var bestDist = Chebyshev(soldier, enemy);
        var bestOffset = int.MaxValue;

        foreach (var c in _legalMoves)
        {
            var dist = Chebyshev(c, enemy);
            var offset = System.Math.Abs(c.Column - enemy.Column);
            if (dist < bestDist || (best != null && dist == bestDist && offset < bestOffset))
            {
                best = c;
                bestDist = dist;
                bestOffset = offset;
            }
        }
        return best;
    }

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

        if (_selected is not null && _healTargets.Contains(cell))   // trait « Soin » : cible un allié blessé
        {
            ResolveHeal(_selected.Value, cell);
            EndPlayerAction();
            return;
        }

        if (_selected is not null && _legalMoves.Contains(cell))
        {
            var from = _selected.Value;
            TryMoveWithFx(from, cell);
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
            _match.HealTargets(cell, _healTargets);     // trait « Soin » : alliés blessés ciblables
            FilterTutorialActions();
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
        else if (_healTargets.Contains(cell))       // glissé sur un allié blessé : soin (trait « Soin »)
        {
            ResolveHeal(from, cell);
            EndPlayerAction();
        }
        else if (_legalMoves.Contains(cell))
        {
            TryMoveWithFx(from, cell);
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

        // La difficulté s'applique ICI : l'IA ne joue son meilleur coup qu'avec la précision du niveau
        // choisi POUR CETTE RUN, sinon elle descend d'un cran de priorité (cf. DifficultySettings).
        var accuracy = DifficultySettings.For(_run?.Difficulty ?? Difficulty.Normal).AiAccuracy;
        var action = EnemyAi.ChooseAction(_match, PaysanCells(), accuracy);
        if (action is not { } a)
        {
            // Aucun coup productif (ex. gardes défensifs déjà en place, joueur hors de portée) : l'ennemi
            // PASSE, sinon le tour resterait bloqué côté ennemi. Le round est tout de même consommé.
            _match.PassTurn();
            _lastAiAction = null;   // rien à revoir : l'IA a passé son tour
            OnEnemyTurnResolved();
            return;
        }

        if (a.IsAttack)
        {
            if (ResolveAttack(a.From, a.To) != MoveKind.Invalid)
                RecordAiAttackReplay();     // fige l'attaque pour pouvoir la REVOIR (touche R / RB)
        }
        else
        {
            TryMoveWithFx(a.From, a.To);
            if (_match.UnitAt(a.To) is { } moved) FaceToward(moved, a.From, a.To);
            TriggerLanding(a.To);
            Context.Sounds.Play("unit_move");
            RecordAiMoveReplay(a.From, a.To);
        }
        OnEnemyTurnResolved();
    }

    /// <summary>Fige la dernière ATTAQUE de l'IA (état de <see cref="_fx"/> + feedback principal en attente) pour pouvoir
    /// la REVOIR. Appelé juste après <see cref="ResolveAttack"/>, donc AVANT que l'impact ne consomme les _pending*.</summary>
    private void RecordAiAttackReplay() =>
        _lastAiAction = new AiReplaySnapshot(
            IsAttack: true, From: _fx.From, To: _fx.To, AttackerCell: _fx.Attacker,
            AttackerSprite: _fx.AttackerSprite, VictimSprite: _fx.VictimSprite,
            Killed: _fx.Killed, Advanced: _fx.Advanced, Style: _fx.Style, Dodged: _fx.Dodged,
            Damage: _pendingDamage, GiantBonus: _pendingGiantBonus, Phenix: _pendingPhenix,
            ReculeSlide: _reculeSlide, Sound: SoundForStyle(_fx.Style));

    /// <summary>Fige le dernier DÉPLACEMENT de l'IA (cases départ/arrivée + sprite du pion) pour pouvoir le REVOIR.</summary>
    private void RecordAiMoveReplay(Cell from, Cell to) =>
        _lastAiAction = new AiReplaySnapshot(
            IsAttack: false, From: from, To: to, AttackerCell: to,
            AttackerSprite: _match.UnitAt(to) is { } m ? UnitSprite(m) : null, VictimSprite: null,
            Killed: false, Advanced: false, Style: AttackStyle.Lunge, Dodged: false,
            Damage: 0, GiantBonus: 0, Phenix: false, ReculeSlide: null, Sound: "unit_move");

    /// <summary>
    /// Cases des paysans encore EN JEU (tuiles recrue non résolues), passées à l'IA : GARDÉES par les
    /// défensifs (Liberer), ASSAILLIES par les offensifs (Proteger). Vide hors mission spéciale.
    /// </summary>
    private List<Cell> PaysanCells()
    {
        var cells = new List<Cell>();
        if (!_specialMission)
            return cells;
        foreach (var c in _recrueCells)
            if (!_recrueConsumed.Contains(c))
                cells.Add(c);
        return cells;
    }

    /// <summary>Fin d'un tour ennemi (action jouée ou passée) = un round écoulé : décompte la limite spéciale
    /// puis déclenche les « Séismes » des pions joueur (trait <see cref="Trait.Seisme"/>).</summary>
    private void OnEnemyTurnResolved()
    {
        if (HasSpecialTurnLimit && _specialRoundsLeft > 0)   // « sauver » est une course : pas de décompte
            _specialRoundsLeft--;
        TriggerSeismes();
    }

    /// <summary>
    /// « Séisme » : à la FIN du tour ennemi, chaque pion JOUEUR portant le trait frappe les ennemis adjacents
    /// pour sa puissance (cf. <see cref="Match.ApplySeismes"/>). Feedback : les tuiles de l'AoE tremblent de
    /// haut en bas (cf. TileTremor) + poussière + un chiffre de dégâts par cible + le son « seisme ». Les
    /// dégâts/kills sont déjà appliqués côté moteur.
    /// </summary>
    private void TriggerSeismes()
    {
        var hits = _match.ApplySeismes(Faction.Player);
        if (hits.Count == 0)
            return;
        _tremor.Shake(_match.LastSeismeZone);               // les tuiles de l'AoE tressautent de haut en bas
        var layout = BuildLayout();
        var tile = layout.TileSize;
        var pixel = MathF.Max(2f, tile / 32f);
        foreach (var (cell, dmg) in hits)
        {
            _damagePopups.Spawn(cell, dmg);
            _damagePopups.SpawnText(cell, Loc.T("fx.seisme"), Palette.Brown3, new Vector2(0f, -0.5f));   // mot-clé au-dessus du chiffre
            // Poussière/débris giclant du sol sous chaque ennemi frappé.
            var ground = layout.CellToScreen(cell.Column, cell.Row) + new Vector2(tile / 2f, tile * 0.7f);
            _sparks.EmitDust(ground, 10, pixel);
        }
        Context.Sounds.Play("seisme");
    }

    private void UpdateRecruitment(GameTime gameTime)
    {
        // Bilan de mission spéciale : modale à valider AVANT la récupération des pions (draft / récompense).
        if (_specialRecap != null)
        {
            if (Context.Input.WasLeftClicked || Context.Input.WasKeyPressed(Keys.Enter) || Context.Input.WasConfirmPressed)
            {
                _specialRecap = null;
                Context.Sounds.Play("menu_close");
            }
            return;
        }

        var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _sparks.Update(dt);
        if (_reserveFullFlash > 0f)   // feedback « plus de place » (draft ou récompense)
            _reserveFullFlash -= dt;

        // FUSION (façon placement) : popup de choix ouverte ou animation d'évolution → prioritaires, gèlent le reste.
        if (EvoPlaying) { UpdateEvolutionAnimation(dt); return; }
        if (FusionOpen) { UpdateFusionPopup(); return; }

        // Écran de récompense « protéger » : on COCHE les pions gagnés à garder (limité par la place), puis
        // « Récupérer » les fait voler vers la réserve. Les décochés sont abandonnés.
        if (_protectReward is { } rewards)
        {
            if (_protectRewardFlight > 0f)   // vol en cours : on ajoute les cochés à la fin
            {
                _protectRewardFlight -= dt;
                if (_protectRewardFlight <= 0f)
                {
                    for (var i = 0; i < rewards.Count; i++)
                        if (i < _rewardKeep.Count && _rewardKeep[i])
                            _run.AddUnit(rewards[i]);
                    _protectReward = null;
                    _run.SkipRecruitment();
                    BeginPlacement();
                }
                return;
            }

            if (HandleReserveDrag())   // empiler (fusion) / supprimer pour faire de la place
                return;
            UpdateRewardChecks(rewards);
            return;
        }

        // Un pion a été choisi : soit il VOLE vers la réserve (place dispo), soit il est TENU en attente
        // (réserve pleine) — on affiche le pion et on laisse le joueur faire de la place (fusion/suppression)
        // pour le garder, ou l'ABANDONNER (le perdre) pour enchaîner.
        if (_recruitChoice is { } choice)
        {
            if (_recruitHold > 0f)   // vol en cours
            {
                _recruitHold -= dt;
                if (_recruitHold <= 0f)
                {
                    _run.Recruit(choice);    // BeginPlacement remet _recruitChoice à null
                    BeginPlacement();
                }
                return;
            }

            if (!_run.IsReserveFull)   // place libérée (fusion/suppression) → le pion s'envole enfin
            {
                _recruitHold = RecruitFlightDuration;
                Context.Sounds.Play("recruit");
                return;
            }

            // Tenu, réserve pleine : gérer la réserve (empiler/supprimer), re-choisir une carte, ou abandonner.
            if (HandleReserveDrag())
                return;
            var vpH = VirtualViewport;
            var availWH = vpH.Width - RightPanelWidth;
            var mouseH = Context.Input.MousePosition;
            if (Context.Input.WasLeftClicked && RecruitAbandonBtnRect(availWH, vpH.Height).Contains(mouseH)
                || Context.Input.WasCancelPressed)
            {
                _recruitChoice = null;   // ABANDONNER : on perd le pion
                _run.SkipRecruitment();
                BeginPlacement();
                return;
            }
            if (Context.Input.WasLeftClicked)
                for (var i = 0; i < _run.Draft.Count; i++)
                    if (DraftCardRect(i, _run.Draft.Count, availWH, vpH.Height).Contains(mouseH))
                    {
                        SelectRecruit(i, availWH, vpH.Height);   // re-choisir une autre carte
                        return;
                    }
            if (Context.Input.Nav(NavDir.Left)) { _recruitFocus = (_recruitFocus - 1 + _run.Draft.Count) % _run.Draft.Count; SelectRecruit(_recruitFocus, availWH, vpH.Height); }
            if (Context.Input.Nav(NavDir.Right)) { _recruitFocus = (_recruitFocus + 1) % _run.Draft.Count; SelectRecruit(_recruitFocus, availWH, vpH.Height); }
            return;
        }

        var viewport = VirtualViewport;
        var availW = viewport.Width - RightPanelWidth;   // cartes centrées à GAUCHE du panneau
        var count = _run.Draft.Count;
        if (count == 0)
        {
            // Aucun pion à drafter (ex. mission spéciale réussie sans avoir tué de garde) : la récompense
            // était les paysans ralliés en combat. On saute le recrutement et on enchaîne, sans se bloquer.
            _run.SkipRecruitment();
            BeginPlacement();
            return;
        }
        // La réserve reste gérable (fusion/suppression) ; on peut CHOISIR un pion même si elle est pleine :
        // il sera « tenu » (affiché) jusqu'à ce qu'on fasse de la place ou qu'on l'abandonne.
        if (HandleReserveDrag())
            return;
        _recruitFocus = System.Math.Clamp(_recruitFocus, 0, count - 1);

        // Manette : navigation gauche/droite (cyclique) + validation sur la carte focus.
        if (Context.Input.Nav(NavDir.Left)) _recruitFocus = (_recruitFocus - 1 + count) % count;
        if (Context.Input.Nav(NavDir.Right)) _recruitFocus = (_recruitFocus + 1) % count;
        if (Context.Input.WasConfirmPressed) { SelectRecruit(_recruitFocus, availW, viewport.Height); return; }

        // Souris : le survol fixe le focus, le clic choisit le pion.
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

    /// <summary>
    /// Choisit la carte <paramref name="index"/>. Si la réserve a de la place, le pion S'ENVOLE vers
    /// l'inventaire ; si elle est PLEINE, il est TENU (affiché) le temps que le joueur fasse de la place ou
    /// l'abandonne (cf. bloc « pion choisi » de UpdateRecruitment).
    /// </summary>
    private void SelectRecruit(int index, int availW, int vpH)
    {
        var rect = DraftCardRect(index, _run.Draft.Count, availW, vpH);
        _recruitChoice = _run.Draft[index];
        // Départ du vol = centre du sprite de la carte (cf. disposition dans DrawCardLayout).
        _recruitFrom = new Vector2(rect.X + rect.Width / 2f, rect.Y + CardPad + 22 + 32);
        _recruitHold = _run.IsReserveFull ? 0f : RecruitFlightDuration;   // pleine → tenu (pas de vol tout de suite)
        Context.Sounds.Play("recruit");
    }

    // ─── RÉSERVE des écrans post-combat (recrutement/récompense/révélation) ─────────────────────────
    // La réserve = _pending (comme l'inventaire du placement) → la FUSION réutilise EXACTEMENT le système du
    // placement : glisser un portrait sur un identique empile (« N/3 »), la 3e ouvre la popup de choix au
    // centre puis l'animation d'évolution (cf. TryStackOnReserve / DrawFusionStack / DrawFusionPopup). En plus :
    // clic DROIT sur un portrait = suppression (faire de la place sans fusionner).

    /// <summary>
    /// Entrée souris de la réserve post-combat (drag de fusion façon placement + suppression au clic droit).
    /// Renvoie vrai si l'entrée a été CONSOMMÉE (le caller ne doit pas enchaîner sur un recrutement/collecte).
    /// </summary>
    private bool HandleReserveDrag()
    {
        var mouse = Context.Input.MousePosition;

        // Drag en cours : le relâchement empile sur un identique (fusion) ou remet le pion en réserve.
        if (_dragSpec is { } dragged)
        {
            if (Context.Input.WasLeftReleased)
            {
                if (!TryStackOnReserve(dragged, mouse))
                    _pending.Add(dragged);
                _dragSpec = null;
                _dragFrom = null;
            }
            return true;
        }

        if (Context.Input.WasLeftClicked)
        {
            if (FusionStacking && FusionInReserve && FusionStackCancelRect().Contains(mouse))
            {
                CancelFusion();
                return true;
            }
            if (PanelCardAt(mouse) is { } i)   // prise d'un portrait pour l'empiler
            {
                _dragSpec = _pending[i];
                _pending.RemoveAt(i);
                _dragFrom = null;
                Context.Sounds.Play("unit_pick");
                return true;
            }
        }

        // Clic DROIT sur un portrait : suppression (faire de la place).
        if (Context.Input.WasRightClicked && PanelCardAt(mouse) is { } d)
        {
            _run.DeleteUnit(_pending[d]);
            _pending.RemoveAt(d);
            Context.Sounds.Play("unit_deselect");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Dessine la réserve post-combat (panneau de droite) EXACTEMENT comme l'inventaire du placement :
    /// portraits <see cref="_pending"/> + pile de fusion « N/3 » + compteur RESERVE X/8. Batch OUVERT requis.
    /// </summary>
    private void DrawReservePanelFusion(SpriteBatch sb)
    {
        for (var i = 0; i < _pending.Count; i++)
            DrawInventoryCard(sb, _pending[i], PendingCardRect(i));
        DrawFusionStack(sb);   // pile « N/3 » + bouton X (comme au placement)

        var panel = PanelRect();
        var counter = Loc.T("reserve.count", _run.ReserveCount, _run.ReserveLimit);
        Context.Font.Draw(sb, counter,
            new Vector2(panel.Right - PanelPad - Context.Font.Measure(counter, 1), PanelListTop - 22),
            1, _run.IsReserveFull ? Palette.Purple5 : Palette.Cyan1);

        // Rappel (souris) : fusion par empilement + suppression au clic droit.
        if (!Context.Input.UsingGamepad)
        {
            var y = panel.Bottom - 32;
            Context.Font.Draw(sb, Loc.T("reserve.hint_fuse"), new Vector2(panel.X + PanelPad, y), 1, Palette.Yellow2);
            Context.Font.Draw(sb, Loc.T("reserve.hint_del"), new Vector2(panel.X + PanelPad, y + 14), 1, Palette.Blue1);
        }
    }

    /// <summary>Rectangle du bouton « Abandonner » (perdre le pion tenu), sous les cartes de draft.</summary>
    private Rectangle RecruitAbandonBtnRect(int availW, int vpH)
    {
        var cards = DraftCardRect(0, 1, availW, vpH);
        const int w = 220, h = 30;
        return new Rectangle((availW - w) / 2, cards.Bottom + 14, w, h);
    }

    private const float ReserveFlashDuration = 0.8f;   // durée du feedback « plus de place »

    /// <summary>
    /// Écran de récompense : coche/décoche les pions gagnés (souris = clic carte, manette = X sur la carte
    /// focalisée), puis « Récupérer » (bouton souris / A / Entrée). La collecte n'est possible que si le
    /// nombre de cochés tient dans la place restante ; sinon feedback « plus de place » (_reserveFullFlash).
    /// </summary>
    private void UpdateRewardChecks(List<UnitSpec> rewards)
    {
        var vp = VirtualViewport;
        var availW = vp.Width - RightPanelWidth;
        var mouse = Context.Input.MousePosition;
        var gp = Context.Input.UsingGamepad;

        if (!gp && Context.Input.WasLeftClicked)
            for (var i = 0; i < rewards.Count; i++)
                if (i < _rewardKeep.Count && DraftCardRect(i, rewards.Count, availW, vp.Height).Contains(mouse))
                {
                    _rewardKeep[i] = !_rewardKeep[i];
                    Context.Sounds.Play("unit_deselect");
                    return;
                }
        if (gp && !_reserveZone && rewards.Count > 0)
        {
            if (Context.Input.Nav(NavDir.Left)) _rewardFocus = (_rewardFocus - 1 + rewards.Count) % rewards.Count;
            if (Context.Input.Nav(NavDir.Right)) _rewardFocus = (_rewardFocus + 1) % rewards.Count;
            _rewardFocus = System.Math.Clamp(_rewardFocus, 0, rewards.Count - 1);
            if (Context.Input.WasTertiaryPressed && _rewardFocus < _rewardKeep.Count)
            {
                _rewardKeep[_rewardFocus] = !_rewardKeep[_rewardFocus];
                Context.Sounds.Play("unit_deselect");
                return;
            }
        }

        var collect = (Context.Input.WasLeftClicked && RewardCollectBtnRect(availW, vp.Height).Contains(mouse))
            || Context.Input.WasConfirmPressed || Context.Input.WasKeyPressed(Keys.Enter);
        if (!collect)
            return;
        if (RewardCheckedCount() <= _run.ReserveLimit - _run.ReserveCount)
        {
            _protectRewardFlight = RecruitFlightDuration;   // les cochés s'envolent vers la réserve
            Context.Sounds.Play("recruit");
        }
        else
        {
            _reserveFullFlash = ReserveFlashDuration;        // pas assez de place : feedback
            Context.Sounds.Play("unit_deselect");
        }
    }

    /// <summary>Nombre de pions de récompense COCHÉS (à récupérer).</summary>
    private int RewardCheckedCount()
    {
        var n = 0;
        for (var i = 0; i < _rewardKeep.Count; i++)
            if (_rewardKeep[i]) n++;
        return n;
    }

    /// <summary>
    /// Rectangle du bouton « Récupérer », SOUS la rangée de cartes — comme « Abandonner » du draft (cf.
    /// <see cref="RecruitAbandonBtnRect"/>). C'est possible depuis que le détail des traits n'est plus
    /// affiché en permanence sous chaque carte : seule la carte survolée le montre, et sa pile se pose À
    /// CÔTÉ d'elle (cf. <see cref="DrawHoveredCardKeywords"/>) — le dessous des cartes est donc libre.
    /// </summary>
    private static Rectangle RewardCollectBtnRect(int availW, int vpH)
    {
        var cards = DraftCardRect(0, 1, availW, vpH);   // y identique quel que soit le nombre de cartes
        const int w = 220;
        return new Rectangle((availW - w) / 2, cards.Bottom + 14, w, PostCombatBtnH);
    }

    // ── Édition de la réserve (écrans draft / récompense) ───────────────────────────────────────────
    // Le plafond de réserve (Run.ReserveLimit) borne la réserve (roster hors commandant). Quand elle est pleine, on ne
    // peut plus recruter/récupérer tant qu'on n'a pas fusionné (3 identiques → évolution) ou supprimé un pion.

    /// <summary>
    /// Vrai si <paramref name="spec"/> peut être fusionné DEPUIS <paramref name="pool"/> (non-feuille + ≥3
    /// exemplaires DANS le pool). Le pool borne les instances consommables : réserve non déployée en combat
    /// (pas de désync plateau), tout le roster hors combat.
    /// </summary>
    private bool CanFuseReserve(UnitSpec spec, List<UnitSpec> pool) =>
        !spec.UnitClass.IsLeaf
        && pool.Count(u => !u.Essential && Run.SameClass(u, spec)) >= FusionSizeOf(spec);

    /// <summary>Indice de la carte de RÉSERVE (armée hors commandant) sous <paramref name="p"/>, ou null.</summary>
    private int? ArmyPanelCardAt(Point p, int armyCount)
    {
        for (var i = 0; i < armyCount; i++)
            if (PanelCardRect(i).Contains(p))
                return i;
        return null;
    }

    /// <summary>Rectangle d'un bouton d'action de réserve (0 = haut, 1 = bas) au bas du panneau de droite.</summary>
    private Rectangle ReserveBtnRect(int slot)
    {
        var panel = PanelRect();
        return new Rectangle(panel.X + PanelPad, panel.Bottom - 74 + slot * 32, panel.Width - 2 * PanelPad, 26);
    }

    /// <summary>
    /// Édition de réserve (souris OU manette) : sélectionner un pion, puis SUPPRIMER / FUSIONNER (→ choix
    /// d'une des 2 évolutions) pour faire de la place. Renvoie vrai si l'entrée a été CONSOMMÉE (le caller
    /// n'enchaîne pas sur un recrutement/collecte).
    /// </summary>
    private bool UpdateReserveEditing(List<UnitSpec> army)
    {
        if (_reserveSel is { } cur && !army.Contains(cur))   // sélection devenue invalide (supprimée/fusionnée)
        {
            _reserveSel = null;
            _reserveFuseChoice = false;
        }
        return Context.Input.UsingGamepad
            ? UpdateReserveEditingGamepad(army)
            : UpdateReserveEditingMouse(army);
    }

    /// <summary>
    /// Édition de réserve à la SOURIS : clics sur les boutons (Supprimer/Fusionner/évolution), et DRAG-DROP
    /// d'un pion sur un pion identique pour FUSIONNER (≥3 exemplaires → choix d'évolution). Un clic simple sur
    /// un pion (drag relâché sur lui-même) le (dé)sélectionne pour les boutons.
    /// </summary>
    private bool UpdateReserveEditingMouse(List<UnitSpec> army)
    {
        var mouse = Context.Input.MousePosition;

        // Drag en cours : résolu au relâchement (fusion si drop sur un identique, sinon (dé)sélection).
        if (_reserveDrag is { } drag)
        {
            if (Context.Input.WasLeftReleased)
            {
                var over = ArmyPanelCardAt(mouse, army.Count);
                if (over is { } oi && !ReferenceEquals(army[oi], drag)
                    && Run.SameClass(army[oi], drag) && CanFuseReserve(drag, army))
                {
                    _reserveSel = drag;          // drop sur un pion identique → ouvre le choix d'évolution
                    _reserveFuseChoice = true;
                    _reserveActionFocus = 0;
                    Context.Sounds.Play("unit_place");
                }
                else if (over is { } oj && ReferenceEquals(army[oj], drag))
                {
                    _reserveSel = ReferenceEquals(_reserveSel, drag) ? null : drag;   // clic simple → (dé)sélection
                    _reserveFuseChoice = false;
                }
                _reserveDrag = null;
            }
            return true;   // pendant tout le drag, on capte l'entrée
        }

        if (!Context.Input.WasLeftClicked)
            return false;

        // Choix d'évolution en cours : cliquer une des 2 options fusionne 3 exemplaires en cette évolution.
        if (_reserveSel is { } fspec && _reserveFuseChoice)
        {
            var evos = fspec.UnitClass.Evolutions;
            for (var e = 0; e < evos.Count; e++)
                if (ReserveBtnRect(e).Contains(mouse))
                {
                    FuseReserve(fspec, evos[e], army);
                    return true;
                }
            _reserveFuseChoice = false;   // clic ailleurs : on annule le choix
            return true;
        }

        // Pion sélectionné : boutons Supprimer (slot 0) / Fusionner (slot 1, si ≥3 identiques).
        if (_reserveSel is { } sel)
        {
            if (ReserveBtnRect(0).Contains(mouse))
            {
                _run.DeleteUnit(sel);
                Context.Sounds.Play("unit_deselect");
                _reserveSel = null;
                return true;
            }
            if (CanFuseReserve(sel, army) && ReserveBtnRect(1).Contains(mouse))
            {
                _reserveFuseChoice = true;
                return true;
            }
        }

        // Clic sur un pion de la réserve : DÉMARRE un drag (résolu au relâchement).
        if (ArmyPanelCardAt(mouse, army.Count) is { } idx)
        {
            _reserveDrag = army[idx];
            return true;
        }

        // Clic hors panneau : on désélectionne mais on LAISSE le caller gérer (ex. clic sur une carte draft).
        _reserveSel = null;
        _reserveFuseChoice = false;
        return false;
    }

    /// <summary>
    /// Édition de réserve à la MANETTE : RB entre/sort du panneau ; dans le panneau, Nav choisit un pion, A le
    /// sélectionne, puis Nav ↑/↓ + A choisit Supprimer/Fusionner (→ une des 2 évolutions), B revient en arrière.
    /// Renvoie vrai dès qu'on est dans la réserve (l'entrée manette est alors captée, pas les cartes).
    /// </summary>
    private bool UpdateReserveEditingGamepad(List<UnitSpec> army)
    {
        // Pion sélectionné (manette OU souris) : choix de l'action / de l'évolution.
        if (_reserveSel is { } sel)
        {
            var count = _reserveFuseChoice ? sel.UnitClass.Evolutions.Count
                : CanFuseReserve(sel, army) ? 2 : 1;
            if (Context.Input.Nav(NavDir.Down)) _reserveActionFocus = (_reserveActionFocus + 1) % count;
            if (Context.Input.Nav(NavDir.Up)) _reserveActionFocus = (_reserveActionFocus - 1 + count) % count;
            _reserveActionFocus = System.Math.Clamp(_reserveActionFocus, 0, count - 1);

            if (Context.Input.WasConfirmPressed)
            {
                if (_reserveFuseChoice)
                    FuseReserve(sel, sel.UnitClass.Evolutions[_reserveActionFocus], army);
                else if (_reserveActionFocus == 0) { _run.DeleteUnit(sel); Context.Sounds.Play("unit_deselect"); _reserveSel = null; }
                else { _reserveFuseChoice = true; _reserveActionFocus = 0; }   // → choix d'évolution
                _reserveFocus = System.Math.Clamp(_reserveFocus, 0, System.Math.Max(0, army.Count - 1));
            }
            else if (Context.Input.WasCancelPressed)
            {
                if (_reserveFuseChoice) { _reserveFuseChoice = false; _reserveActionFocus = 0; }
                else _reserveSel = null;   // retour au choix de pion
            }
            return true;
        }

        // Dans le panneau réserve (pas encore de pion sélectionné) : navigation + sélection.
        if (_reserveZone)
        {
            if (army.Count == 0) { _reserveZone = false; return false; }
            MoveGridFocus(ref _reserveFocus, army.Count, InvCols);
            if (Context.Input.WasConfirmPressed)
            {
                _reserveSel = army[System.Math.Clamp(_reserveFocus, 0, army.Count - 1)];
                _reserveActionFocus = 0;
                _reserveFuseChoice = false;
            }
            else if (Context.Input.WasCancelPressed || Context.Input.WasRightShoulderPressed)
                _reserveZone = false;   // sortir de la réserve → retour aux cartes
            return true;
        }

        // Sur les cartes : RB entre dans la gestion de réserve.
        if (Context.Input.WasRightShoulderPressed && army.Count > 0)
        {
            _reserveZone = true;
            _reserveFocus = System.Math.Clamp(_reserveFocus, 0, army.Count - 1);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Fusionne 3 exemplaires de la classe de <paramref name="rep"/> (pris dans <paramref name="pool"/>) en
    /// l'évolution donnée, et solde la sélection. Le pool évite de consommer un pion déployé en combat.
    /// </summary>
    private void FuseReserve(UnitSpec rep, UnitClass evolution, List<UnitSpec> pool)
    {
        var group = pool.Where(u => !u.Essential && Run.SameClass(u, rep)).Take(FusionSizeOf(rep)).ToList();
        if (_run.Fuse(group, evolution) != null)
            GrantFusionRecruits();   // nœud « fusion » de l'arbre : recrues offertes en plus
        Context.Sounds.Play("recruit");
        _reserveSel = null;
        _reserveFuseChoice = false;
    }

    /// <summary>
    /// Applique le bonus des nœuds « fusion » de l'arbre de commandement : ajoute au roster les recrues
    /// (<see cref="Run.FusionRecruitSpecs"/>) — un domaine précis (ex. Lancier) ou un tier 1 déjà découvert
    /// (méta-progression). Renvoie les gabarits ajoutés pour que l'appelant les affiche s'il le faut.
    /// Le plafond de réserve reste respecté (une fusion la libère de deux places, donc il ne mord jamais).
    /// </summary>
    private List<UnitSpec> GrantFusionRecruits()
    {
        var added = new List<UnitSpec>();
        foreach (var bonus in _run.FusionRecruitSpecs(new System.Random(), Context.Saves.IsUnitDiscovered))
        {
            if (_run.IsReserveFull)
                break;
            _run.AddUnit(bonus);
            added.Add(bonus);
        }
        return added;
    }

    /// <summary>Déplace un focus en GRILLE (largeur <paramref name="cols"/>) selon la Nav manette, borné à [0,count).</summary>
    private void MoveGridFocus(ref int focus, int count, int cols)
    {
        if (count <= 0) { focus = 0; return; }
        if (Context.Input.Nav(NavDir.Right)) focus = (focus + 1) % count;
        if (Context.Input.Nav(NavDir.Left)) focus = (focus - 1 + count) % count;
        if (Context.Input.Nav(NavDir.Down)) focus = System.Math.Min(count - 1, focus + cols);
        if (Context.Input.Nav(NavDir.Up)) focus = System.Math.Max(0, focus - cols);
        focus = System.Math.Clamp(focus, 0, count - 1);
    }

    private void ClearSelection()
    {
        _selected = null;
        _legalMoves.Clear();
        _attackTargets.Clear();
        _attackReach.Clear();
        _healTargets.Clear();
        _combatDragFrom = null;
    }

    /// <summary>Lance le rebond de « pose » sur la case où un pion vient d'atterrir.</summary>
    private void TriggerLanding(Cell cell)
    {
        _landingCell = cell;
        _landingTimer = LandingDuration;
    }

    /// <summary>
    /// Déplace via le moteur PUIS déclenche le feedback d'« Impact » (dégâts fixes autour de la case
    /// d'arrivée) si le pion le porte. Centralisé pour que TOUS les points de déplacement — joueur, IA,
    /// tutoriel — montrent l'effet. Le déplacement est instantané : les chiffres jaillissent sur-le-champ.
    /// </summary>
    private MoveKind TryMoveWithFx(Cell from, Cell to)
    {
        var kind = _match.TryMove(from, to);
        foreach (var (cell, dmg) in _match.LastImpactHits)
            _damagePopups.Spawn(cell, dmg);
        if (_match.LastImpactHits.Count > 0)   // l'« Impact » a frappé : tuiles de l'AoE + son (instantané sur un déplacement)
            ShakeAoeZone(_match.LastImpactZone);
        return kind;
    }

    /// <summary>Feedback de l'« Impact » (déplacement comme attaque) : fait trembler les tuiles de l'AoE de
    /// haut en bas + joue le son lourd (le même que le « Séisme »). Sans effet si la zone est vide.</summary>
    private void ShakeAoeZone(IReadOnlyList<Cell> zone)
    {
        if (zone.Count == 0)
            return;
        _tremor.Shake(zone);
        Context.Sounds.Play("seisme");
    }

    /// <summary>
    /// Trait « Soin » : soigne l'allié ciblé (montant = moitié de la puissance du soigneur) et passe le tour. Feedback :
    /// son d'incantation + « +N » vert flottant sur le soigné. La cible est déjà bornée aux alliés blessés à
    /// portée par <see cref="Match.HealTargets"/>/<see cref="Match.TryHeal"/>.
    /// </summary>
    private void ResolveHeal(Cell from, Cell target)
    {
        if (_match.UnitAt(from) is { } healer)
            FaceToward(healer, from, target);
        var before = _match.UnitAt(target)?.Hp ?? 0;
        _match.TryHeal(from, target);
        var healed = (_match.UnitAt(target)?.Hp ?? 0) - before;
        Context.Sounds.Play("unit_cast");
        if (healed > 0)
            _damagePopups.SpawnText(target, "+" + healed, Palette.Green1);
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
        var victimEquipBefore = victim?.Equipment;   // pour détecter une renaissance « Queue de phénix » après le coup
        // Dégâts EFFECTIFS à afficher (traits inclus : Rempart, Rage…), bornés aux PV de la cible.
        _pendingDamage = attacker != null && victim != null ? _match.PreviewDamage(from, target) : 0;
        // Part « Tueur de géants » de ces dégâts (0 sinon) : affichée en « +N » distinct à l'impact.
        _pendingGiantBonus = attacker != null && victim != null ? _match.GiantSlayerBonusFor(from, target) : 0;
        var victimHpBefore = victim?.Hp ?? 0;       // pour détecter l'esquive (PV inchangés malgré des dégâts attendus)
        if (attacker != null)
            FaceToward(attacker, from, target);     // tourne l'attaquant vers sa cible (avant la capture du sprite)
        var attackerSprite = attacker != null ? UnitSprite(attacker) : null;
        var victimSprite = victim != null ? UnitSprite(victim) : null;

        // Orage / Tempête : on fige AVANT l'attaque les PV des ENNEMIS candidats (ni alliés, ni porteur, ni cible
        // directe), car TryAttack applique la foudre instantanément — sur 3 ennemis TIRÉS AU HASARD. Après coup,
        // éclair + chiffre UNIQUEMENT sur ceux qui ont réellement perdu des PV (donc exactement les 3 foudroyés).
        List<(Cell Cell, Unit Unit, int Hp)>? stormBefore = null;
        if (attacker != null && Match.StormDamageFor(attacker) > 0)
        {
            stormBefore = new List<(Cell, Unit, int)>();
            foreach (var (cell, u) in _match.Units())
                if (u.Faction != attacker.Faction && cell != target)
                    stormBefore.Add((cell, u, u.Hp));     // candidat : on saura après l'attaque s'il a été foudroyé
        }

        var kind = _match.TryAttack(from, target);
        if (kind == MoveKind.Invalid)
            return kind;

        // Bilan de la foudre : éclair + dégâts UNIQUEMENT sur les ennemis réellement frappés (esquive/bouclier inclus).
        _pendingStormBolts = null;
        _pendingStormHits = null;
        if (stormBefore != null)
        {
            _pendingStormBolts = new List<Cell>();
            _pendingStormHits = new List<(Cell, int)>();
            foreach (var (cell, u, hp) in stormBefore)
                if (hp - u.Hp is > 0 and var dmg)
                {
                    _pendingStormBolts.Add(cell);
                    _pendingStormHits.Add((cell, dmg));
                }
        }

        // Impact / Recule : chiffres reportés à l'impact (copie : la liste du moteur est réécrite à l'action suivante).
        _pendingImpactHits = _match.LastImpactHits.Count > 0
            ? new List<(Cell, int)>(_match.LastImpactHits)
            : null;
        // Zone AoE reportée : les tuiles ne tremblent qu'au CONTACT (avec les chiffres), seulement si l'Impact a frappé.
        _pendingImpactZone = _pendingImpactHits != null
            ? new List<Cell>(_match.LastImpactZone)
            : null;
        _pendingReculeSlam = _match.LastRecule is { SlamDamage: > 0 } r ? (r.To, r.SlamDamage) : null;
        // Glissement du recul : la victime a réellement changé de case (To != cible) → on l'anime en glissant.
        _reculeSlide = _match.LastRecule is { } rc && rc.To != target ? (target, rc.To) : null;
        // Transpercement : le pion derrière la cible a encaissé — recul + chiffre + mot-clé reportés à l'impact.
        _pendingPierce = _match.LastPierce;

        // Riposte : contre-attaque DÉJÀ résolue par le moteur → on la rejoue en animation APRÈS l'anim d'attaque.
        // On fige le sprite de l'ASSAILLANT (il peut mourir de la riposte) ; le riposteur, vivant, sera repris à vif.
        _pendingRiposte = _match.LastRiposte is { } rp && victim != null
            ? (rp.From, rp.To, attackerSprite, rp.Killed, AttackStyleFor(victim, rp.From, rp.To), rp.Damage)
            : null;

        RecordIfEnemyKilled(victim);

        // « Queue de phénix » : la cible a encaissé un coup létal mais renaît à 1 PV (son équipement s'est brisé).
        _pendingPhenix = victim is { IsAlive: true } && victimEquipBefore != null && victim.Equipment == null;

        var killed = kind == MoveKind.Killed;
        // Esquive : la victime a le trait, des dégâts étaient attendus mais ses PV n'ont pas bougé → coup esquivé.
        // (Le trait évite de confondre avec un autre « 0 dégât », par exemple une réduction qui absorbe tout.)
        _pendingDodge = !killed && victim is { IsAlive: true } && _pendingDamage > 0
            && victim.Hp == victimHpBefore && victim.HasTrait(Trait.Esquive);
        if (_tutorial is { Step: TutorialStep.Attack } && killed && victim is { Faction: Faction.Enemy })
            _tutorial.Advance();            // mort de l'ENNEMI → Attack → Commander (pas sur la contre-attaque)
        var advanced = killed && ReferenceEquals(_match.UnitAt(target), attacker);
        var attackerCell = advanced ? target : from;
        var style = attacker != null ? AttackStyleFor(attacker, from, target) : AttackStyle.Lunge;
        Context.Sounds.Play(SoundForStyle(style));   // incantation (mage) / charge (cavalier) / tir (archer) / coup d'arme
        _fx.Begin(from, target, attackerCell, attackerSprite, victimSprite, killed, advanced, style, dodged: _pendingDodge);
        _impactHandled = false;     // le chiffre de dégâts sera lancé au contact (cf. UpdateBattle)

        return kind;
    }

    /// <summary>
    /// Rejoue la RIPOSTE (déjà résolue dans le moteur) comme une SECONDE animation d'attaque : le riposteur
    /// fente vers son assaillant, avec le mot « RIPOSTE » (au-dessus de lui) et le chiffre de dégâts à l'impact.
    /// Réutilise <see cref="_fx"/> une fois l'anim d'attaque principale terminée — purement visuel, les PV ont
    /// déjà bougé. Le riposteur (vivant) est réorienté et re-capturé ; l'assaillant peut, lui, être mort.
    /// </summary>
    private void StartRiposteFx((Cell From, Cell To, Texture2D? AttackerSprite, bool Killed, AttackStyle Style, int Damage) rip)
    {
        _pendingRiposte = null;
        _reculeSlide = null;   // le glissement du recul est terminé : il ne doit pas se rejouer sur l'anim de riposte
        Texture2D? riposterSprite = null;
        if (_match.UnitAt(rip.From) is { } riposter)
        {
            FaceToward(riposter, rip.From, rip.To);   // le riposteur regarde l'assaillant
            riposterSprite = UnitSprite(riposter);
        }

        _damagePopups.SpawnText(rip.From, Loc.T("fx.riposte"), Palette.Yellow2);   // « RIPOSTE » au-dessus du riposteur
        Context.Sounds.Play(SoundForStyle(rip.Style));

        // À l'impact de cette 2e anim : le chiffre de la riposte (les autres reports ont été consommés à
        // l'impact principal, donc _pending* sont déjà nuls) ; ni esquive ni phénix ici.
        _pendingDamage = rip.Damage;
        _pendingDodge = false;
        _pendingPhenix = false;
        _pendingGiantBonus = 0;   // le « +N » du bonus n'est pas rejoué sur la riposte (report principal déjà consommé)
        _fx.Begin(rip.From, rip.To, rip.From, riposterSprite, rip.AttackerSprite, rip.Killed, advanced: false, rip.Style);
        _impactHandled = false;
    }

    /// <summary>
    /// (Re)lance l'animation de la dernière action de l'IA depuis son instantané, SANS re-toucher le moteur (qui a
    /// déjà avancé). Une ATTAQUE rejoue l'anim complète + le feedback PRINCIPAL (dégâts / esquive / phénix + éventuel
    /// glissement « Recule ») ; un DÉPLACEMENT rejoue le glissement du pion. Les reports de traits exotiques (orage /
    /// impact de zone / transpercement / riposte) sont remis à zéro pour ne pas refaire jaillir de chiffres parasites
    /// pendant le tour du joueur. L'anim gèle le tour comme d'habitude (cf. _fx.Active dans UpdateBattle).
    /// </summary>
    private void StartAiReplay(AiReplaySnapshot snap)
    {
        _pendingStormBolts = null;
        _pendingStormHits = null;
        _pendingImpactHits = null;
        _pendingImpactZone = null;
        _pendingReculeSlam = null;
        _pendingPierce = null;
        _pendingRiposte = null;

        if (snap.IsAttack)
        {
            _pendingDamage = snap.Damage;
            _pendingGiantBonus = snap.GiantBonus;
            _pendingPhenix = snap.Phenix;
            _pendingDodge = snap.Dodged;
            _reculeSlide = snap.ReculeSlide;
            Context.Sounds.Play(snap.Sound);
            _fx.Begin(snap.From, snap.To, snap.AttackerCell, snap.AttackerSprite, snap.VictimSprite,
                snap.Killed, snap.Advanced, snap.Style, dodged: snap.Dodged);
        }
        else
        {
            _pendingDamage = 0;
            _pendingGiantBonus = 0;
            _pendingPhenix = false;
            _pendingDodge = false;
            _reculeSlide = null;
            _fx.BeginMove(snap.From, snap.To, snap.AttackerSprite);   // son « unit_move » joué à l'atterrissage
        }
        _impactHandled = false;
    }

    /// <summary>Fin d'un DÉPLACEMENT rejoué (« revoir action ») : le pion se pose (rebond + son), sans aucun dégât.</summary>
    private void OnReplayMoveLand()
    {
        _impactHandled = true;
        TriggerLanding(_fx.To);
        Context.Sounds.Play("unit_move");
    }

    /// <summary>Style d'animation d'attaque selon l'unité ET la case ciblée : un cavalier (monté compris) qui
    /// frappe une case en L SAUTE (charge) ; sinon archer (« Zone morte ») = tir, mage = projectile, autres =
    /// fente. Ainsi l'archer monté SAUTE au corps-à-corps en L mais TIRE une flèche sur ses cibles en ligne.</summary>
    /// <summary>Son d'attaque selon le style : incantation (mage), charge (cavalier), tir (archer) ou coup d'arme.</summary>
    private static string SoundForStyle(AttackStyle style) => style switch
    {
        AttackStyle.Cast  => "unit_cast",
        AttackStyle.Leap  => "unit_charge",
        AttackStyle.Shoot => "unit_shoot",
        _                 => "unit_attack",
    };

    private static AttackStyle AttackStyleFor(Unit unit, Cell from, Cell target)
    {
        // Cavalier (y compris archer monté) frappant une case en L : c'est un SAUT (charge), pas un tir.
        if (unit.Domaine == Domaine.Cavalier && IsKnightOffset(from, target))
            return AttackStyle.Leap;
        return unit switch
        {
            _ when unit.HasTrait(Trait.ZoneMorte) => AttackStyle.Shoot,     // archers, montés compris : tir EN LIGNE
            { Domaine: Domaine.Cavalier }         => AttackStyle.Leap,      // cavalier de mêlée : charge sautée
            { Domaine: Domaine.Fou }              => AttackStyle.Cast,
            _                                     => AttackStyle.Lunge,
        };
    }

    /// <summary>Vrai si <paramref name="to"/> est à un saut de cavalier (en L) de <paramref name="from"/>.</summary>
    private static bool IsKnightOffset(Cell from, Cell to)
    {
        var dc = System.Math.Abs(to.Column - from.Column);
        var dr = System.Math.Abs(to.Row - from.Row);
        return (dc == 1 && dr == 2) || (dc == 2 && dr == 1);
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
        _dezoomedOut = false;
        _camera = Vector2.Zero;
        _layoutDirty = true;
    }

    /// <summary>Marque le layout à recalculer (zoom ou pan modifié).</summary>
    private void MarkLayoutDirty() => _layoutDirty = true;

    private void UpdateCamera(GameTime gameTime)
    {
        // Molette : fait défiler la RÉSERVE quand le curseur la survole et qu'elle déborde ; sinon,
        // un seul cran de zoom du plateau (haut = rapproché, bas = retour au cadrage).
        var scroll = Context.Input.ScrollDelta;
        if (scroll != 0 && _run.Phase == RunPhase.Placement && !_equipPhase && !CommandTreeOpen
            && IsOverPanel(Context.Input.MousePosition) && InvMaxScrollRow() > 0)
        {
            _invScrollRow += scroll < 0 ? 1 : -1;   // molette bas = descendre dans la liste
            ClampInvScroll();
        }
        else if (scroll > 0) ZoomStep(+1);
        else if (scroll < 0) ZoomStep(-1);

        // Pan clavier : flèches + ZQSD (AZERTY) ET WASD (QWERTY) — les deux dispositions partagent S/D et
        // ajoutent Q+A (gauche) / Z+W (haut) pour couvrir les touches physiques des deux claviers. Aller
        // « voir à droite » fait reculer l'origine.
        var input = Context.Input;
        var dir = Vector2.Zero;
        if (input.IsKeyDown(Keys.Left) || input.IsKeyDown(Keys.Q) || input.IsKeyDown(Keys.A)) dir.X += 1;
        if (input.IsKeyDown(Keys.Right) || input.IsKeyDown(Keys.D)) dir.X -= 1;
        if (input.IsKeyDown(Keys.Up) || input.IsKeyDown(Keys.Z) || input.IsKeyDown(Keys.W)) dir.Y += 1;
        if (input.IsKeyDown(Keys.Down) || input.IsKeyDown(Keys.S)) dir.Y -= 1;

        // Pan manette : stick DROIT, en analogique (le stick gauche pilote déjà le curseur de case).
        // Mêmes signes que le clavier : pousser à droite fait reculer l'origine, pousser en haut l'avance.
        var stick = input.RightStick;
        dir.X -= stick.X;
        dir.Y += stick.Y;

        if (dir != Vector2.Zero)
        {
            var dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
            _camera += dir * CameraPanSpeed * dt;
            MarkLayoutDirty();
        }

        // Pan à la SOURIS : clic molette maintenu = on attrape le plateau et il suit le curseur (le point
        // sous la souris reste sous la souris). Delta en px canvas → 1:1 avec l'origine, donc pas de gain.
        if (input.IsMiddleDown)
        {
            var d = input.MouseDelta;
            if (d != Point.Zero)
            {
                _camera += d.ToVector2();
                MarkLayoutDirty();
            }
        }
    }

    /// <summary>
    /// Un cran de molette entre trois niveaux, tous BOARD-ONLY (l'UI ne bouge jamais) : DÉZOOM (−1, case du
    /// plateau rétrécie, cf. <see cref="BoardTileSize"/>) ↔ CADRAGE (0) ↔ ZOOM AVANT (+1, case agrandie d'un
    /// cran entier). Le zoom avant garde le zoom-vers-curseur (<see cref="SetZoom"/>) ; le dézoom recentre.
    /// </summary>
    private void ZoomStep(int dir)
    {
        int level = _zoomedIn ? 1 : (_dezoomedOut ? -1 : 0);
        int target = System.Math.Clamp(level + dir, -1, 1);   // −1 dézoom / 0 cadrage / +1 zoom avant
        if (target == level)
            return;

        // Transition impliquant le zoom AVANT du plateau (0↔+1) : réutilise le zoom-vers-curseur.
        if (level == 1 || target == 1)
            SetZoom(target == 1);

        // Entrée/sortie de dézoom (0↔−1) : le plateau passe/quitte sa couche de rendu réduite (cf. RenderBoardLayer).
        if (level == -1 || target == -1)
        {
            _dezoomedOut = target == -1;
            _camera = Vector2.Zero;
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

    /// <summary>Vrai quand le plateau est dézoomé (un cran sous le cadrage). Le dézoom N'AGIT QUE sur le rendu du
    /// PLATEAU (dessiné NATIF sur sa propre couche puis recomposé plus petit ×1) — l'UI, l'input et le hit-test
    /// restent en coordonnées NORMALES (layout ÷2), donc tout fonctionne comme d'habitude.</summary>
    private bool Dezoomed => _dezoomedOut;

    /// <summary>
    /// Couches de dézoom pour la couche Game : le plateau NATIF (net) à recomposer ×1 en <paramref name="boardDest"/>,
    /// et l'UI à recomposer ×2. Renvoie false hors dézoom (rendu normal du canvas). Rempli par <see cref="DrawDezoomLayers"/>.
    /// </summary>
    public bool TryGetDezoomLayers(out Microsoft.Xna.Framework.Graphics.RenderTarget2D board,
        out Rectangle boardDest, out Microsoft.Xna.Framework.Graphics.RenderTarget2D ui)
    {
        board = _boardTarget!; boardDest = _boardTargetDest; ui = _uiTarget!;
        return _dezoomLayersReady && _boardTarget != null && _uiTarget != null;
    }

    /// <summary>Couche « curseur » (pion attrapé) à recomposer ×1 PAR-DESSUS l'UI, à <paramref name="dest"/> (suit
    /// la souris). false si rien n'est attrapé. Séparée pour être au premier plan quel que soit l'endroit survolé.</summary>
    public bool TryGetDezoomGhost(out Microsoft.Xna.Framework.Graphics.RenderTarget2D ghost, out Rectangle dest)
    {
        ghost = _ghostTarget!; dest = _ghostDest;
        return _ghostReady && _ghostTarget != null;
    }

    /// <summary>
    /// Layout NATIF du plateau (case 64 px, origine adaptée à un target dédié) + destination ÉCRAN où le
    /// recomposer ×1 pour qu'il tombe EXACTEMENT sur le plateau du layout de jeu (dézoomé, ÷2). On aligne le
    /// coin haut-gauche du plateau natif sur la position écran du layout ÷2, marge comprise (débords de sprites).
    /// </summary>
    private GridLayout NativeBoardLayout(GridLayout hit, out Rectangle target, out Rectangle screenDest)
    {
        const int tile = 64;
        int margin = tile;   // débord des sprites/barres/icônes au-dessus & autour
        int spriteH = tile * GridLayout.DefaultSpriteHeight / GridLayout.DefaultTileSize;   // 80
        int boardW = Columns * tile;
        int boardH = (Rows - 1) * tile + spriteH;
        target = new Rectangle(0, 0, boardW + 2 * margin, boardH + 2 * margin);
        // Origine du plateau natif dans le target (après la marge).
        var native = new GridLayout(new Vector2(margin, margin), tileSize: tile, spriteWidth: tile,
            spriteHeight: spriteH, rowPitch: tile);
        // Le coin (margin,margin) du target = coin du plateau du layout de jeu (÷2), en px ÉCRAN (canvas×2).
        int scale = System.Math.Max(1, _virtualScaleHint);
        int destX = (int)System.Math.Round(hit.Origin.X * scale) - margin;
        int destY = (int)System.Math.Round(hit.Origin.Y * scale) - margin;
        screenDest = new Rectangle(destX, destY, target.Width, target.Height);
        return native;
    }

    // Facteur d'agrandissement canvas→écran, communiqué par la couche Game (cf. SetVirtualScaleHint). Sert à
    // placer la couche plateau à l'ÉCRAN. 2 en 1080p/1440p typiques.
    private int _virtualScaleHint = 2;
    public void SetVirtualScaleHint(int scale) => _virtualScaleHint = System.Math.Max(1, scale);

    // ── Rendu ───────────────────────────────────────────────────────────────────
    public override void Draw(GameTime gameTime)
    {
        var sb = Context.SpriteBatch;
        var layout = BuildLayout();
        var viewport = VirtualViewport;
        _dezoomLayersReady = false;

        // Fond : eau animée pixel-art derrière le plateau (passes shader dédiées, hors du
        // batch principal car elles changent d'état SpriteBatch et de render target).
        DrawWaterBackground(sb, layout, viewport);

        // Le plateau (terrain + ombres + unités + FX) est secoué d'un cran à l'impact d'une attaque ;
        // le panneau latéral et l'eau restent stables. Le layout secoué ne sert qu'au dessin (le
        // hit-test souris reste sur le layout d'origine via BuildLayout).
        var board = ShakeBoard(layout);

        // DÉZOOM : le plateau part sur une couche NATIVE nette (recomposée plus petit ×1 par la couche Game),
        // l'UI sur sa couche à taille normale — l'eau reste dans ce canvas. Le rendu NORMAL ci-dessous est
        // strictement inchangé (ce branchement n'existe qu'en dézoom).
        if (Dezoomed && _run.Phase is RunPhase.Placement or RunPhase.Battle && _battleIntroTimer <= 0)
        {
            DrawDezoomLayers(sb, layout, viewport);
            return;
        }

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawTerrain(sb, board);
        if (_showGrid && BoardAssembled && _run.Phase is RunPhase.Placement or RunPhase.Battle)
            DrawBoardGrid(sb, board, Palette.Green4);   // quadrillage permanent VERT foncé (bascule F1/Select) — masqué pendant l'émergence
        sb.End();

        // Passe d'ombres projetées (sur le terrain, sous les unités) — batchs cisaillés dédiés.
        if (_run.Phase is RunPhase.Placement or RunPhase.Battle)
            DrawCastShadows(sb, board);

        _deferredCards.Clear();           // cartes flottantes + popups : remplis pendant la passe, dessinés APRÈS le HUD
        _deferredKeywordPopups.Clear();
        _deferredHoverCards.Clear();
        _deferredHoverKeywordPopups.Clear();

        switch (_run.Phase)
        {
            case RunPhase.Placement:
                sb.Begin(samplerState: SamplerState.PointClamp);
                if (BoardAssembled && !_equipPhase) DrawDeploymentZone(sb, board);
                if (BoardAssembled) DrawEnemyThreat(sb, board);
                if (BoardAssembled) DrawAuraHalos(sb, board);   // halos d'aura : c'est au placement qu'on s'y range
                DrawChests(sb, board);                   // coffres (sous les unités : un allié peut être dessus)
                DrawRecrueObjects(sb, board);            // pions « ? » de recrutement (sous les unités)
                DrawBushes(sb, board, occupied: false);  // buissons SANS pion dessus : DERRIÈRE les unités
                DrawUnits(sb, board);
                DrawBushes(sb, board, occupied: true);   // buisson AVEC un pion dessus : DEVANT (« caché dans le feuillage »)
                DrawUnitsBelowOccupiedBushes(sb, board);  // pion de la case du dessous : pas masqué (il n'est pas sur le buisson)
                DrawUnitHpBars(sb, board);               // barres de vie TOUJOURS au-dessus (même du buisson)
                DrawEnemyEquipBadges(sb, board);         // objets ennemis visibles DÈS le placement : ça se prépare
                if (_equipPhase)
                {
                    DrawEquipBadgesPlacement(sb, board); // icône au-dessus de la tête (UNIQUEMENT en phase Équipement)
                    DrawEquipDropSlots(sb, board);       // cibles de dépose (pions non équipés)
                    DrawCombatCards(sb, board);          // cartes tooltip (stats + trait + bonus) du pion survolé
                    if (BoardAssembled && Context.Input.UsingGamepad)
                        DrawGamepadPlacementCursor(sb, board);
                    DrawPanelBackground(sb);
                    DrawEquipPanel(sb);
                    DrawDraggedEquip(sb);                // équipement porté, suit la souris
                    DrawEquipBadgeTooltip(sb, board);    // tooltip au survol d'un badge tête équipé
                }
                else
                {
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
                }
                sb.End();

                DrawPhaseTimeline(sb, viewport);   // frise des missions de la phase (HUD haut)
                if (!_equipPhase)
                {
                    if (_specialMission)
                        DrawSpecialBriefing(sb, viewport);       // rappel de l'objectif sous la frise (placement)
                    else if (_run.IsBossCombat)
                        DrawBossBriefing(sb, viewport);          // rappel de la condition de victoire (vaincre le boss)
                }
                // Cartes flottantes + popups : PAR-DESSUS tout le chrome, mais SOUS les modales (tuto, arbre
                // de commandement, fusion, briefing modal) dessinées juste après.
                DrawDeferredCards(sb);
                if (_tutorial != null)
                    DrawTutorialOverlay(sb, board, viewport);
                if (CommandTreeOpen)
                {
                    _commandTree.Draw(sb, viewport, CommandTreeArea(), _run);   // modale par-dessus le placement
                    if (_tutorial is { Step: TutorialStep.TreeDo })
                        DrawTutorialTreeHint(sb, viewport);                     // consigne AU-DESSUS de la modale
                }
                if (FusionOpen)
                    DrawFusionPopup(sb, viewport);   // modale par-dessus le placement
                if (EvoPlaying)
                    DrawEvolutionAnimation(sb, viewport);   // morph base → évolution
                else if (_sparks.HasActive)
                    _sparks.Draw(sb, Context.Pixel);        // fin du feu d'artifice (pièce rangée)
                if (_specialBriefOpen)
                    DrawSpecialBriefingModal(sb, viewport);   // briefing d'ouverture : PAR-DESSUS tout le reste
                break;
            case RunPhase.Battle:
                sb.Begin(samplerState: SamplerState.PointClamp);
                DrawHighlights(sb, board);
                DrawEnemyThreat(sb, board);
                DrawAuraHalos(sb, board);                // halos d'aura, par-dessus les zones mais sous les pions
                DrawChests(sb, board);                   // coffres fermés (sous les unités)
                DrawRecrueObjects(sb, board);            // pions « ? » de recrutement (sous les unités)
                DrawBushes(sb, board, occupied: false);  // buissons SANS pion dessus : DERRIÈRE les unités
                DrawUnits(sb, board);
                DrawBushes(sb, board, occupied: true);   // buisson AVEC un pion dessus : DEVANT (« caché dans le feuillage »)
                DrawUnitsBelowOccupiedBushes(sb, board);  // pion de la case du dessous : pas masqué (il n'est pas sur le buisson)
                DrawUnitHpBars(sb, board);               // barres de vie TOUJOURS au-dessus (même du buisson)
                DrawEnemyEquipBadges(sb, board);         // icône de l'objet porté par un ennemi
                DrawAllyThreatIcons(sb, board);          // « ! » au-dessus des alliés à portée d'un ennemi
                DrawCarriedUnit(sb, board);
                DrawGamepadBattleCursor(sb, board);      // curseur (coins) AU-DESSUS, toujours visible
                sb.End();

                if (_fx.Active)             // dissolution / attaquant animé / flash : passes dédiées
                    DrawCombatFx(sb, board);

                DrawEquipDissolves(sb, board);   // dissolution de l'équipement perdu (après celle du pion)

                _sparks.Draw(sb, Context.Pixel);   // étincelles d'impact, au-dessus de tout le plateau
                if (_storm.Active)
                    DrawStormFx(sb, board);        // éclairs d'orage sur les ennemis foudroyés (sous les chiffres)
                _damagePopups.Draw(sb, Context.Font, board);   // chiffres de dégâts, par-dessus

                if (_battleIntroTimer > 0)
                    DrawSlidingPanel(sb);          // panneau de placement qui sort par la droite
                else
                {
                    sb.Begin(samplerState: SamplerState.PointClamp);
                    DrawCombatCards(sb, layout);
                    sb.End();
                }

                DrawPhaseTimeline(sb, viewport);   // frise des missions de la phase (HUD haut)
                if (_specialMission)
                    DrawSpecialObjective(sb, viewport);   // paysans X/N + tours restants (sous la frise)
                // Cartes flottantes + popups : PAR-DESSUS le chrome, mais SOUS les révélations/overlays
                // (tuto, recrue, coffre) dessinés juste après.
                DrawDeferredCards(sb);
                if (_tutorial != null)
                    DrawTutorialOverlay(sb, board, viewport);
                if (_recrueReveal != null)
                    DrawRecrueReveal(sb, viewport);
                if (ChestRevealActive)
                    DrawChestReveal(sb, viewport);       // révélation modale du coffre (centre + inventaire)
                break;
            case RunPhase.Recruitment:
                sb.Begin(samplerState: SamplerState.PointClamp);
                DrawUnits(sb, board);
                DrawDim(sb, viewport);
                sb.End();
                // Mission spéciale : le BILAN passe d'abord (plateau figé derrière), la récupération des
                // pions n'apparaît qu'une fois validé — les deux gèrent leur propre batch.
                if (_specialRecap is { } recap)
                    DrawSpecialRecap(sb, viewport, recap);
                else
                    DrawRecruitment(sb, viewport);     // gère son propre batch (panneau + cartes + vol)
                break;
            case RunPhase.Victory:
            case RunPhase.Defeat:
                sb.Begin(samplerState: SamplerState.PointClamp);
                DrawUnits(sb, board);
                DrawDim(sb, viewport);
                DrawRunRecap(sb, viewport);
                sb.End();
                break;
        }

        if (_pauseMenu.IsOpen)
        {
            // En manette : pointeur synthétique = centre de l'élément focus → réutilise la surbrillance
            // de survol existante. En souris : vraie position.
            var gp = Context.Input.UsingGamepad;
            var focusRect = _pauseMenu.FocusedRect(viewport.Width, viewport.Height);
            var pointer = gp ? focusRect.Center.ToVector2() : Context.Input.MousePosition.ToVector2();
            sb.Begin(samplerState: SamplerState.PointClamp);
            _pauseRenderer.Draw(sb, _pauseMenu, viewport.Width, viewport.Height,
                pointer, gp ? false : Context.Input.IsLeftDown, gp ? focusRect : null);
            // Rappel des raccourcis de jeu (grille / zones de danger) : affiché UNIQUEMENT dans le menu
            // pause, et seulement là où ces touches servent (placement / combat).
            if (_run.Phase is RunPhase.Placement or RunPhase.Battle)
                DrawControlsLegend(sb, viewport);
            sb.End();
        }

        // Codex par-dessus le menu pause (dessine son propre voile + panneau).
        if (_codex.IsOpen)
            _codex.Draw(sb, viewport);
    }

    private static void EnsureTarget(Microsoft.Xna.Framework.Graphics.GraphicsDevice device,
        ref Microsoft.Xna.Framework.Graphics.RenderTarget2D? rt, int w, int h)
    {
        if (rt != null && rt.Width == w && rt.Height == h) return;
        rt?.Dispose();
        // PreserveContents (et non le DiscardContents par défaut) : on RE-sélectionne parfois ces cibles en
        // cours de frame (ex. fondu des cartes-tooltips qui part sur _hoverCardTarget puis restaure _uiTarget) ;
        // avec DiscardContents, ce retour EFFACERAIT tout le contenu déjà dessiné → écran noir en dézoom. Elles
        // sont Clear() explicitement à chaque frame, donc préserver le contenu ne change rien au rendu.
        rt = new Microsoft.Xna.Framework.Graphics.RenderTarget2D(device, w, h, false,
            Microsoft.Xna.Framework.Graphics.SurfaceFormat.Color, Microsoft.Xna.Framework.Graphics.DepthFormat.None,
            0, Microsoft.Xna.Framework.Graphics.RenderTargetUsage.PreserveContents);
    }

    /// <summary>
    /// DÉZOOM : remplit deux couches — le PLATEAU rendu NATIF (net) dans <see cref="_boardTarget"/>, et TOUTE
    /// l'UI (à sa taille NORMALE, ancrée sur le layout ÷2 → tombe pile sur le petit plateau) dans
    /// <see cref="_uiTarget"/>. L'eau reste dans le canvas courant. La couche Game recompose les trois
    /// (eau ×facteur → plateau natif ×1 → UI ×facteur). Réutilise les MÊMES méthodes de dessin (seuls le
    /// render target et le layout diffèrent) — les FX transitoires (dissolution/étincelles) sont omis ici.
    /// </summary>
    private void DrawDezoomLayers(SpriteBatch sb, GridLayout hit, Viewport viewport)
    {
        var device = Context.GraphicsDevice;
        var mainRT = device.GetRenderTargets();   // = canvas (eau déjà peinte)

        var nb = NativeBoardLayout(hit, out var targetRect, out var screenDest);
        EnsureTarget(device, ref _boardTarget, targetRect.Width, targetRect.Height);
        EnsureTarget(device, ref _uiTarget, viewport.Width, viewport.Height);

        // ── Couche PLATEAU (native, nette) : même structure/ordre que le rendu normal (terrain → ombres →
        //    unités → FX), avec le layout natif `nb`. ──
        device.SetRenderTarget(_boardTarget);
        device.Clear(Microsoft.Xna.Framework.Color.Transparent);
        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawTerrain(sb, nb);
        if (_showGrid && BoardAssembled) DrawBoardGrid(sb, nb, Palette.Green4);
        sb.End();
        DrawCastShadows(sb, nb);   // ombres projetées (batchs cisaillés dédiés)

        sb.Begin(samplerState: SamplerState.PointClamp);
        if (_run.Phase == RunPhase.Placement)
        {
            if (BoardAssembled && !_equipPhase) DrawDeploymentZone(sb, nb);
            if (BoardAssembled) DrawEnemyThreat(sb, nb);
            if (BoardAssembled) DrawAuraHalos(sb, nb);
            DrawChests(sb, nb); DrawRecrueObjects(sb, nb);
            DrawBushes(sb, nb, occupied: false); DrawUnits(sb, nb); DrawBushes(sb, nb, occupied: true);
            DrawUnitsBelowOccupiedBushes(sb, nb); DrawUnitHpBars(sb, nb); DrawEnemyEquipBadges(sb, nb);
            if (_equipPhase) { DrawEquipBadgesPlacement(sb, nb); DrawEquipDropSlots(sb, nb); }
            else DrawFusionBoardStack(sb, nb);   // le pion attrapé passe par la couche curseur (par-dessus tout)
            sb.End();
        }
        else   // Battle
        {
            DrawHighlights(sb, nb); DrawEnemyThreat(sb, nb); DrawAuraHalos(sb, nb);
            DrawChests(sb, nb); DrawRecrueObjects(sb, nb);
            DrawBushes(sb, nb, occupied: false); DrawUnits(sb, nb); DrawBushes(sb, nb, occupied: true);
            DrawUnitsBelowOccupiedBushes(sb, nb); DrawUnitHpBars(sb, nb); DrawEnemyEquipBadges(sb, nb);
            DrawAllyThreatIcons(sb, nb);
            DrawCarriedUnitNative(sb, nb);   // liseré de case cible (le pion soulevé = couche curseur)
            sb.End();
            // FX de combat (chacun gère son propre batch) — sur la couche plateau pour rester à la bonne échelle.
            if (_fx.Active) DrawCombatFx(sb, nb);
            DrawEquipDissolves(sb, nb);
            _sparks.Draw(sb, Context.Pixel);
            if (_storm.Active) DrawStormFx(sb, nb);
            _damagePopups.Draw(sb, Context.Font, nb);
        }

        // ── Couche UI (taille normale, ancrée sur le layout ÷2) ──
        device.SetRenderTarget(_uiTarget);
        device.Clear(Microsoft.Xna.Framework.Color.Transparent);
        _deferredCards.Clear();
        _deferredKeywordPopups.Clear();
        _deferredHoverCards.Clear();
        _deferredHoverKeywordPopups.Clear();
        if (_run.Phase == RunPhase.Placement)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            DrawPanelBackground(sb);
            if (_equipPhase) { DrawEquipPanel(sb); DrawDraggedEquip(sb); }
            else { DrawPlacementPanel(sb); DrawInventoryFocusHighlight(sb); DrawPlacementPreview(sb); }   // pion attrapé = couche curseur (par-dessus tout), cf. RenderGhostLayer
            sb.End();
            if (_equipPhase) { sb.Begin(samplerState: SamplerState.PointClamp); DrawCombatCards(sb, hit); sb.End(); }
            DrawPhaseTimeline(sb, viewport);
            if (!_equipPhase)
            {
                if (_specialMission) DrawSpecialBriefing(sb, viewport);
                else if (_run.IsBossCombat) DrawBossBriefing(sb, viewport);
            }
            DrawDeferredCards(sb);
            if (_tutorial != null) DrawTutorialOverlay(sb, hit, viewport);
            if (CommandTreeOpen)
            {
                _commandTree.Draw(sb, viewport, CommandTreeArea(), _run);
                if (_tutorial is { Step: TutorialStep.TreeDo }) DrawTutorialTreeHint(sb, viewport);
            }
            if (FusionOpen) DrawFusionPopup(sb, viewport);
            if (EvoPlaying) DrawEvolutionAnimation(sb, viewport);
            else if (_sparks.HasActive) _sparks.Draw(sb, Context.Pixel);
            if (_specialBriefOpen) DrawSpecialBriefingModal(sb, viewport);
        }
        else   // Battle
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            DrawCombatCards(sb, hit);
            sb.End();
            DrawPhaseTimeline(sb, viewport);
            if (_specialMission) DrawSpecialObjective(sb, viewport);
            DrawDeferredCards(sb);
            if (_tutorial != null) DrawTutorialOverlay(sb, hit, viewport);
            if (_recrueReveal != null) DrawRecrueReveal(sb, viewport);
            if (ChestRevealActive) DrawChestReveal(sb, viewport);
        }
        if (_pauseMenu.IsOpen)
        {
            var gp = Context.Input.UsingGamepad;
            var focusRect = _pauseMenu.FocusedRect(viewport.Width, viewport.Height);
            var pointer = gp ? focusRect.Center.ToVector2() : Context.Input.MousePosition.ToVector2();
            sb.Begin(samplerState: SamplerState.PointClamp);
            _pauseRenderer.Draw(sb, _pauseMenu, viewport.Width, viewport.Height, pointer, gp ? false : Context.Input.IsLeftDown, gp ? focusRect : null);
            if (_run.Phase is RunPhase.Placement or RunPhase.Battle) DrawControlsLegend(sb, viewport);
            sb.End();
        }
        if (_codex.IsOpen) _codex.Draw(sb, viewport);

        // ── Couche CURSEUR : le pion attrapé, dessiné NATIF à part et recomposé ×1 PAR-DESSUS l'UI (donc net,
        //    à l'échelle du plateau, et visible PARTOUT — réserve, eau, plateau — sans être coupé). ──
        RenderGhostLayer(sb, device);

        device.SetRenderTargets(mainRT);   // retour au canvas
        _boardTargetDest = screenDest;
        _dezoomLayersReady = true;
    }

    /// <summary>
    /// Rend le pion ATTRAPÉ (drag de placement ou pion combat porté) dans un petit target NATIF (net, 64 px),
    /// recomposé ×1 par la couche Game au premier plan à la position souris. Ainsi il reste net, à l'échelle du
    /// plateau, et n'est PAS coupé par les bords d'une couche (il suit le curseur partout). <see cref="_ghostReady"/>.
    /// </summary>
    private void RenderGhostLayer(SpriteBatch sb, Microsoft.Xna.Framework.Graphics.GraphicsDevice device)
    {
        _ghostReady = false;
        if (Context.Input.UsingGamepad)
            return;
        bool hasDrag = _dragSpec != null;
        bool hasPile = _carryPile && _fusionGroup.Count > 0;   // pile de fusion portée (2+ pions d'un coup)
        bool hasCarry = _combatDragFrom is { } cf && _match.UnitAt(cf) != null;
        if (!hasDrag && !hasPile && !hasCarry)
            return;

        const int box = 128;   // marge autour du sprite 64 (lift compris)
        const int s = 64;
        EnsureTarget(device, ref _ghostTarget, box, box);
        device.SetRenderTarget(_ghostTarget);
        device.Clear(Microsoft.Xna.Framework.Color.Transparent);
        sb.Begin(samplerState: SamplerState.PointClamp);
        int cx = box / 2, cy = box / 2;
        if (hasPile)
        {
            var r = new Rectangle(cx - InvIconSize / 2, cy - InvIconSize / 2, InvIconSize, InvIconSize);
            DrawFusionPileChip(sb, _fusionGroup[0].UnitClass, r, front: true);
            Context.Font.DrawCentered(sb, $"{_fusionGroup.Count}/{FusionGroupTarget}",
                new Rectangle(r.X, r.Bottom - 13, r.Width, 10), 1, Palette.Yellow2);
        }
        else if (hasDrag)
        {
            DrawChip(sb, _dragSpec!.UnitClass, Faction.Player, new Rectangle(cx - s / 2, cy - s / 2, s, s));
        }
        else
        {
            var unit = _match.UnitAt(_combatDragFrom!.Value)!;
            int lift = (int)(s * CarriedLiftFraction);
            var rect = new Rectangle(cx - s / 2, cy - s / 2 - lift, s, s);
            var sprite = UnitSprite(unit);
            if (sprite != null) sb.Draw(sprite, rect, Color.White);
            else DrawChip(sb, unit.Class, unit.Faction, new Rectangle(rect.X + 9, rect.Y + 8, s - 18, s - 26));
        }
        sb.End();

        // Destination écran (×1) centrée sur la souris — la couche Game ajoute l'offset du letterbox.
        var m = Context.Input.MousePosition;
        _ghostDest = new Rectangle(m.X * _virtualScaleHint - cx, m.Y * _virtualScaleHint - cy, box, box);
        _ghostReady = true;
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
        if (FusionOpen || CommandTreeOpen || (EvoPlaying && _evoLong) || _recrueReveal != null || ChestRevealActive
            || _specialBriefOpen)
            return Palette.Black1 * 0.62f; // fusion / arbre / morph évo long / révélation recrue / coffre / briefing : = DrawDim
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
            var (tex, src) = TileSprite(_battlefield[cell].Id, cell);
            var rect = layout.CellToSpriteRect(cell.Column, cell.Row);
            var (oy, a) = BoardIntroAnim(cell, layout);
            rect.Y += oy + _tremor.OffsetY(cell);   // secousse locale de l'AoE (Séisme/Impact)
            sb.Draw(tex, rect, src, Color.White * a);
        }
    }

    private void DrawDeploymentZone(SpriteBatch sb, GridLayout layout)
    {
        // Zone de déploiement : simple fond bleu TRANSPARENT (pas de traits de bordure).
        foreach (var cell in PlayerDeployCells())
            DrawZone(sb, layout, cell, Palette.Cyan1 * 0.38f);
    }

    /// <summary>
    /// Légende des commandes en haut à gauche (petit panneau) : bascule grille + zones de danger.
    /// Affichée UNIQUEMENT par-dessus le menu pause (plus en permanence pendant le jeu), donc dessinée
    /// après l'overlay de pause. Les touches suivent le PÉRIPHÉRIQUE actif (clavier/souris vs manette).
    /// </summary>
    private void DrawControlsLegend(SpriteBatch sb, Viewport viewport)
    {
        var gp = Context.Input.UsingGamepad;
        var lines = new List<string>
        {
            $"{(gp ? "SELECT" : "F1")} : {Loc.T("hud.toggle_grid")}",
            $"{(gp ? "RT" : "ESPACE")} : {Loc.T("hud.danger_zones")}",
        };
        // « Revoir action IA » n'a de sens qu'en combat (l'IA n'a pas joué au placement) : ligne ajoutée alors.
        if (_run.Phase == RunPhase.Battle)
            lines.Add($"{(gp ? "RB" : "R")} : {Loc.T("hud.replay_ai")}");

        const int pad = 10, lineH = 11;
        var w = 0;
        foreach (var line in lines)
            w = System.Math.Max(w, Context.Font.Measure(line, 1));
        var box = new Rectangle(12, 12, w + 2 * pad, pad + lines.Count * lineH + pad - 2);
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
        // Aperçu tremblant (cf. DrawUnit) : ALLIÉS menacés (toujours) + ENNEMIS ciblables (sélection/survol).
        _trembleTargets.Clear();
        AddThreatenedAlliesToTremble();

        // Unité sélectionnée : on garde son aperçu (buffers remplis à la sélection).
        if (_selected is { } sel)
        {
            DrawMoveAttackZones(sb, layout, sel, _attackReach, _legalMoves, _attackTargets, _healTargets);
            foreach (var c in _attackTargets) _trembleTargets.Add(c);
            return;
        }

        // Sinon, aperçu au SURVOL d'un pion joueur (uniquement pendant son tour : sinon pas de coups).
        if (_match.CurrentTurn == Faction.Player
            && CellUnderMouse() is { } cell && _match.UnitAt(cell) is { Faction: Faction.Player })
        {
            _match.ThreatenedCells(cell, _hoverReach);
            _match.LegalMoves(cell, _hoverMoves);
            _match.AttackTargets(cell, _hoverAttackTargets);
            _match.HealTargets(cell, _hoverHealTargets);
            DrawMoveAttackZones(sb, layout, cell, _hoverReach, _hoverMoves, _hoverAttackTargets, _hoverHealTargets);
            foreach (var c in _hoverAttackTargets) _trembleTargets.Add(c);
        }
    }

    /// <summary>Ajoute à <see cref="_trembleTargets"/> tout pion ALLIÉ (joueur) posé sur une case menacée par au
    /// moins un ennemi (à portée d'attaque), pour qu'il tremblote en alerte comme l'icône « ! ». Indépendant de
    /// la sélection/du survol.</summary>
    private void AddThreatenedAlliesToTremble()
    {
        foreach (var (cell, unit) in _match.Units())
        {
            if (unit.Faction != Faction.Enemy)
                continue;
            _match.ThreatenedCells(cell, _threatCells);
            foreach (var t in _threatCells)
                if (_match.UnitAt(t) is { Faction: Faction.Player })
                    _trembleTargets.Add(t);
        }
    }

    /// <summary>Surbrillances déplacement/attaque d'une unité : cerclage + portée de tir + cases de
    /// déplacement + cibles réellement à portée. Partagé par la sélection et l'aperçu au survol.</summary>
    private void DrawMoveAttackZones(SpriteBatch sb, GridLayout layout, Cell origin,
        List<Cell> reach, List<Cell> moves, List<Cell> targets, List<Cell>? heals = null)
    {
        DrawZoneBorder(sb, layout, origin, Palette.Yellow2, 3);

        foreach (var cell in reach)     // PORTÉE de tir (cases atteintes) = rouge pâle
            DrawZone(sb, layout, cell, Palette.Purple5 * 0.18f);

        foreach (var cell in moves)     // déplacement = jaune
            DrawZone(sb, layout, cell, Palette.Yellow2 * 0.30f);

        foreach (var cell in targets)   // ennemi réellement ciblable = rouge fort
            DrawZone(sb, layout, cell, Palette.Purple5 * 0.50f);

        if (heals != null)
            foreach (var cell in heals) // trait « Soin » : allié blessé ciblable = vert
                DrawZone(sb, layout, cell, Palette.Green1 * 0.55f);

        // Quadrillage de la portée PAR-DESSUS les remplissages (contour par case) : déplacement/attaque.
        foreach (var cell in reach)
            DrawZoneBorder(sb, layout, cell, Palette.Purple5 * 0.45f, 1);
        foreach (var cell in moves)
            DrawZoneBorder(sb, layout, cell, Palette.Yellow2 * 0.7f, 1);
        foreach (var cell in targets)
            DrawZoneBorder(sb, layout, cell, Palette.Purple5 * 0.9f, 1);
        if (heals != null)
            foreach (var cell in heals)
                DrawZoneBorder(sb, layout, cell, Palette.Green1, 1);
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
                DrawBoardGrid(sb, layout, Palette.Green4);   // + grille pleine VERT foncé sur toute la map
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

    // ── Barrière d'aura ──────────────────────────────────────────────────────────
    // Les traits d'« aura » agissent sur les 8 cases ADJACENTES au porteur (cf. HasAdjacentAlly dans
    // Match) : sans marque au sol, se placer DANS une aura oblige à compter les cases à la main. On la
    // matérialise donc en permanence, joueur comme ennemi (savoir qu'une cible est couverte fait partie
    // de la lecture du plateau).
    //
    // Rendu : PAS un halo plein (trop voyant, il noyait le plateau), mais une petite ENCEINTE le long du
    // CONTOUR de la zone — un rempart d'énergie. On ne peint que les ARÊTES de cases qui bordent l'union :
    // un liseré vif POSÉ sur l'arête (il épouse exactement la grille), doublé d'une ombre de contact pour
    // le contraste, et — sur les bords HORIZONTAUX seulement — d'une courte lueur qui MONTE au-dessus
    // (fausse hauteur : le mur « se dresse » ; les bords verticaux sont vus de profil → simple ourlet).
    // L'intérieur reste vide, les pions restent lisibles.
    //
    // FUSION : on ne dresse un mur que là où la case VOISINE n'appartient pas à l'union des porteurs d'une
    // même famille. Les arêtes internes (entre deux cases couvertes, y compris de deux porteurs distincts)
    // sont donc ignorées : deux auras voisines ne donnent qu'un seul contour continu, sans couture ni
    // double épaisseur à l'intersection.
    //
    // Chaque palier a sa COULEUR, pas seulement son opacité : une teinte unique en alpha croissant est
    // illisible sur le terrain sauge. On monte une rampe sombre → vive qui se lit comme un mur : pied
    // assombri (contraste garanti quel que soit le sol) → corps teinté → crête éclairée.
    //
    // CAMP : une aura ne profite QU'AUX unités du camp de son porteur (cf. HasAdjacentAlly dans Match) — il
    // faut donc voir d'un coup d'œil à qui elle sert. C'est le camp qui donne la TEMPÉRATURE de la rampe,
    // suivant le langage couleur du jeu (Palette : Cyan1 = camp joueur, Purple5 = ennemi) : froid/bleu pour
    // le joueur, chaud/rouge pour l'ennemi. La famille d'effet ne fait plus varier que la nuance à
    // l'intérieur de cette température — le camp reste le signal dominant.
    private static readonly float[] AuraRampAlpha = { 0.42f, 0.66f, 0.84f, 0.94f };

    // Une FAMILLE = les traits qui partagent la même enceinte : leurs porteurs fusionnent — mais seulement
    // À CAMP ÉGAL (cf. DrawAuraHalos). Ally = rampe du joueur, Foe = rampe de l'ennemi.
    private static readonly (string[] Traits, Color[] Ally, Color[] Foe)[] AuraFamilies =
    {
        // -dégâts à distance sur les alliés adjacents
        (new[] { Trait.AuraDeRempart },
            new[] { Palette.Black5, Palette.WaterMid2, Palette.Cyan1, Palette.White },
            new[] { Palette.Purple1, Palette.Purple2, Palette.Purple3, Palette.Purple5 }),
        // +puissance (crête chaude = « buff », sur un corps à la température du camp)
        (new[] { Trait.AuraDePuissance },
            new[] { Palette.Black4, Palette.Black5, Palette.Cyan2, Palette.Brown4 },
            new[] { Palette.Purple1, Palette.Purple3, Palette.Purple5, Palette.Brown5 }),
    };

    /// <summary>Camps balayés par <see cref="DrawAuraHalos"/> : une enceinte SÉPARÉE par camp.</summary>
    private static readonly Faction[] AuraFactions = { Faction.Player, Faction.Enemy };

    private const int AuraBlock = 4;          // côté du « gros pixel » du tramage (px virtuels)
    private const int AuraLevels = 4;         // paliers de la rampe (quantification pixel-art)
    private const int AuraLinePx = 2;         // épaisseur du liseré vif posé SUR l'arête de contour
    private const int AuraGlowH = 6;          // hauteur (px) de la lueur qui monte au-dessus d'un mur horizontal
    private const int AuraSideGlow = 4;       // largeur (px) de l'ourlet extérieur d'un mur vertical

    /// <summary>Matrice de Bayer 4×4 normalisée : seuil de tramage ordonné entre deux paliers.</summary>
    private static readonly float[,] AuraBayer =
    {
        { 0.5f / 16, 8.5f / 16, 2.5f / 16, 10.5f / 16 },
        { 12.5f / 16, 4.5f / 16, 14.5f / 16, 6.5f / 16 },
        { 3.5f / 16, 11.5f / 16, 1.5f / 16, 9.5f / 16 },
        { 15.5f / 16, 7.5f / 16, 13.5f / 16, 5.5f / 16 },
    };

    /// <summary>
    /// Barrières d'aura au sol. À dessiner APRÈS les remplissages de zones (elles les couvriraient) et
    /// AVANT les unités : l'enceinte reste sous les pions. Une passe par famille pour fusionner les
    /// porteurs voisins en une seule enceinte continue.
    /// </summary>
    private void DrawAuraHalos(SpriteBatch sb, GridLayout layout)
    {
        // Une passe par FAMILLE **et par CAMP** : deux porteurs de camps opposés ne doivent jamais fusionner
        // (leurs auras ne couvrent pas les mêmes unités), et chaque camp a sa propre rampe.
        foreach (var (traits, ally, foe) in AuraFamilies)
            foreach (var faction in AuraFactions)
            {
                _auraCarriers.Clear();
                foreach (var (cell, unit) in _match.Units())
                {
                    if (unit.Faction != faction)
                        continue;
                    foreach (var trait in traits)
                        if (unit.HasTrait(trait))
                        {
                            _auraCarriers.Add(cell);
                            break;
                        }
                }

                if (_auraCarriers.Count > 0)
                    DrawAuraBarrier(sb, layout, faction == Faction.Player ? ally : foe);
            }
    }

    /// <summary>
    /// Enceinte le long du contour de l'UNION des blocs 3×3 des porteurs (<see cref="_auraCarriers"/>). On
    /// dresse un mur uniquement sur les ARÊTES de cases dont la voisine n'est PAS dans l'union : les arêtes
    /// internes disparaissent, donc les auras voisines fusionnent en un seul tracé (cf. en-tête).
    /// </summary>
    private void DrawAuraBarrier(SpriteBatch sb, GridLayout layout, Color[] ramp)
    {
        var tile = layout.TileSize;
        var breath = 0.85f + 0.15f * MathF.Sin(_time * 1.7f);   // pulsation lente de l'enceinte

        // Union des 3×3 (bornée au plateau) : une case hors bord n'y entre pas, donc l'arête du bord la ferme.
        _auraCells.Clear();
        foreach (var c in _auraCarriers)
            for (var dr = -1; dr <= 1; dr++)
                for (var dc = -1; dc <= 1; dc++)
                {
                    var cell = new Cell(c.Column + dc, c.Row + dr);
                    if (_match.InBounds(cell))
                        _auraCells.Add(cell);
                }

        foreach (var cell in _auraCells)
        {
            var tl = layout.CellToScreen(cell.Column, cell.Row);
            var x0 = (int)tl.X;
            var y0 = (int)tl.Y;

            if (!_auraCells.Contains(new Cell(cell.Column, cell.Row - 1)))
                DrawWallH(sb, ramp, x0, y0, tile, breath);           // bord HAUT
            if (!_auraCells.Contains(new Cell(cell.Column, cell.Row + 1)))
                DrawWallH(sb, ramp, x0, y0 + tile, tile, breath);    // bord BAS
            if (!_auraCells.Contains(new Cell(cell.Column - 1, cell.Row)))
                DrawWallV(sb, ramp, x0, y0, tile, breath, -1);       // bord GAUCHE (ourlet vers -X)
            if (!_auraCells.Contains(new Cell(cell.Column + 1, cell.Row)))
                DrawWallV(sb, ramp, x0 + tile, y0, tile, breath, +1); // bord DROIT (ourlet vers +X)
        }
    }

    /// <summary>
    /// Mur HORIZONTAL le long de la ligne d'écran <paramref name="lineY"/> : liseré vif posé sur l'arête +
    /// ombre de contact dessous + lueur qui monte vers le HAUT (fausse hauteur).
    /// </summary>
    private void DrawWallH(SpriteBatch sb, Color[] ramp, int x0, int lineY, int len, float breath)
    {
        var top = lineY - AuraLinePx / 2;
        for (var x = 0; x < len; x += AuraBlock)
        {
            var sx = x0 + x;
            var shim = 0.80f + 0.20f * MathF.Sin(sx * 0.20f - _time * 3.0f);

            // Lueur montante (grain Bayer, extinction vers le haut).
            for (var h = 0; h < AuraGlowH; h += AuraBlock)
            {
                var up = 1f - (float)h / AuraGlowH;
                var stp = QuantizeAura(0.42f * up * breath * shim, x, h);
                if (stp > 0)
                    DrawRect(sb, new Rectangle(sx, top - h - AuraBlock, AuraBlock, AuraBlock),
                        ramp[stp - 1] * AuraRampAlpha[stp - 1]);
            }

            // Liseré vif SUR l'arête + ombre de contact juste dessous (contraste sur n'importe quel sol).
            var la = System.Math.Clamp(0.9f * (0.78f + 0.22f * shim) * breath, 0f, 1f);
            DrawRect(sb, new Rectangle(sx, top, AuraBlock, AuraLinePx), ramp[3] * la);
            DrawRect(sb, new Rectangle(sx, top + AuraLinePx, AuraBlock, 1), ramp[0] * 0.55f);
        }
    }

    /// <summary>
    /// Mur VERTICAL le long de la colonne d'écran <paramref name="lineX"/> : vu de profil, donc pas de
    /// hauteur — juste le liseré vif sur l'arête, une ombre de contact côté intérieur et un léger ourlet
    /// vers l'extérieur (<paramref name="outDir"/> = +1 vers +X, -1 vers -X).
    /// </summary>
    private void DrawWallV(SpriteBatch sb, Color[] ramp, int lineX, int y0, int len, float breath, int outDir)
    {
        var leftEdge = lineX - AuraLinePx / 2;
        for (var y = 0; y < len; y += AuraBlock)
        {
            var sy = y0 + y;
            var shim = 0.80f + 0.20f * MathF.Sin(sy * 0.20f - _time * 3.0f);

            // Ourlet extérieur (extinction vers l'extérieur).
            for (var g = 0; g < AuraSideGlow; g += AuraBlock)
            {
                var up = 1f - (float)g / AuraSideGlow;
                var stp = QuantizeAura(0.34f * up * breath * shim, g, y);
                if (stp > 0)
                {
                    var gx = outDir > 0 ? leftEdge + AuraLinePx + g : leftEdge - g - AuraBlock;
                    DrawRect(sb, new Rectangle(gx, sy, AuraBlock, AuraBlock),
                        ramp[stp - 1] * AuraRampAlpha[stp - 1]);
                }
            }

            var la = System.Math.Clamp(0.9f * (0.78f + 0.22f * shim) * breath, 0f, 1f);
            DrawRect(sb, new Rectangle(leftEdge, sy, AuraLinePx, AuraBlock), ramp[3] * la);
            var ix = outDir > 0 ? leftEdge - 1 : leftEdge + AuraLinePx;
            DrawRect(sb, new Rectangle(ix, sy, 1, AuraBlock), ramp[0] * 0.55f);
        }
    }

    /// <summary>Quantifie une intensité [0,1] en palier de rampe (1..AuraLevels), 0 = rien, avec tramage Bayer.</summary>
    private static int QuantizeAura(float a, int bx, int by)
    {
        var scaled = System.Math.Clamp(a, 0f, 1f) * AuraLevels;
        var step = (int)scaled;
        if (step < AuraLevels && AuraBayer[(by / AuraBlock) & 3, (bx / AuraBlock) & 3] < scaled - step)
            step++;
        return step;
    }

    /// <summary>
    /// Petit « ! » d'alerte au-dessus de chaque pion ALLIÉ (joueur) qui se trouve à portée d'attaque d'au
    /// moins un ennemi (case dans la menace d'un ennemi, cf. <see cref="Match.ThreatenedCells(Cell)"/>).
    /// Recalcule l'ensemble menacé une fois par frame (petits plateaux : négligeable).
    /// </summary>
    private void DrawAllyThreatIcons(SpriteBatch sb, GridLayout layout)
    {
        _enemyThreatSet.Clear();
        foreach (var (cell, unit) in _match.Units())
        {
            if (unit.Faction != Faction.Enemy)
                continue;
            _match.ThreatenedCells(cell, _threatCells);
            foreach (var t in _threatCells)
                _enemyThreatSet.Add(t);
        }
        if (_enemyThreatSet.Count == 0)
            return;

        foreach (var (cell, unit) in _match.Units())
        {
            if (unit.Faction != Faction.Player || !_enemyThreatSet.Contains(cell))
                continue;
            if (_combatDragFrom == cell)                    // pion en cours de glisser : pas d'icône
                continue;
            if (_fx.Active && _fx.Attacker == cell)         // attaquant animé : géré par la passe FX
                continue;
            DrawThreatIcon(sb, layout, cell);
        }
    }

    // Décalage vertical de l'icône de menace (fraction de case) : POSITIF = plus haut, NÉGATIF = plus bas
    // (vers la tête). La TAILLE, elle, suit la résolution du PNG à échelle ENTIÈRE (comme les pions).
    private const float ThreatIconGap = -0.08f;

    /// <summary>
    /// Icône de menace flottant au-dessus de la tête du pion : le PNG personnalisé <c>Assets/UI/menace.png</c>
    /// (32×32, fond transparent) s'il existe, sinon un petit « ! » rouge dessiné (cerné de noir pour rester
    /// lisible sur tout terrain). Position remontée au-dessus du sommet du sprite.
    /// </summary>
    private void DrawThreatIcon(SpriteBatch sb, GridLayout layout, Cell cell)
    {
        var size = layout.TileSize;
        var animLift = UnitLift(cell, size);
        var spriteLift = (int)(size * SpriteLiftFraction);
        var top = layout.CellToScreen(cell.Column, cell.Row);
        var cx = (int)top.X + size / 2;                          // centré sur la case
        var headTop = (int)top.Y - spriteLift - animLift;       // sommet du sprite du pion
        var bottom = headTop - (int)(size * ThreatIconGap);     // bas de l'icône : au-dessus de la tête

        if (ThreatIcon() is { } icon)
        {
            // Échelle ENTIÈRE (facteur = zoom des pions) : le PNG est dessiné à taille × zoom, jamais
            // fractionné → pixel-art net. La taille à l'écran suit donc la résolution du PNG (32×32 → demi-case).
            var zoom = System.Math.Max(1, size / GridLayout.DefaultTileSize);
            int w = icon.Width * zoom, h = icon.Height * zoom;
            sb.Draw(icon, new Rectangle(cx - w / 2, bottom - h, w, h), Color.White);
            return;
        }

        // Repli tant que le PNG n'existe pas : petit « ! » dessiné.
        var px = System.Math.Max(2, size / 24);
        var dot = new Rectangle(cx - px, bottom - px * 2, px * 2, px * 2);              // point du bas
        var stem = new Rectangle(cx - px, bottom - px * 7, px * 2, px * 4);             // barre verticale
        void Badge(Rectangle r)
        {
            DrawRect(sb, new Rectangle(r.X - px / 2 - 1, r.Y - px / 2 - 1, r.Width + px + 2, r.Height + px + 2), Palette.Black1);
            DrawRect(sb, r, Palette.Purple5);
        }
        Badge(stem);
        Badge(dot);
    }

    private Texture2D? _threatIcon;
    private bool _threatIconLoaded;

    /// <summary>PNG d'icône de menace (chargé une seule fois ; null s'il n'existe pas → repli « ! » dessiné).</summary>
    private Texture2D? ThreatIcon()
    {
        if (!_threatIconLoaded)
        {
            _threatIcon = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath("Assets/UI/menace.png"));
            _threatIconLoaded = true;
        }
        return _threatIcon;
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

    /// <summary>
    /// Corrige le recouvrement des buissons « devant » : un pion sur la case JUSTE EN DESSOUS d'un
    /// buisson occupé a le haut de sa tête (qui déborde vers le haut) masqué par le feuillage. La règle
    /// étant « le buisson ne masque QUE le pion SUR sa case », on redessine ce pion par-dessus le buisson.
    /// </summary>
    private void DrawUnitsBelowOccupiedBushes(SpriteBatch sb, GridLayout layout)
    {
        foreach (var c in _bushCells)
        {
            if (_match.UnitAt(c) == null)              // buisson vide : dessiné DERRIÈRE → rien à corriger
                continue;
            var below = new Cell(c.Column, c.Row + 1);
            if (_match.UnitAt(below) is { } u)
                DrawUnit(sb, layout, below, u);        // pion du dessous redessiné AU-DESSUS du buisson
        }
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

        // Recul de la victime survivante (à l'opposé de l'attaquant, au contact), OU — si elle a été REPOUSSÉE
        // d'une case (« Recule ») — le GLISSEMENT vers sa case d'arrivée : décalage en pixels. Le pion TRANSPERCÉ
        // (derrière la cible) recule dans l'axe du coup en parallèle (cf. HitRecoil).
        var kb = IsFxVictim(cell) ? VictimKnockback(size) : ReculeSlideOffset(cell, layout);
        kb += _pierceRecoil.Offset(cell, size);
        zx += kb.X;
        zy += kb.Y;

        // Léger tremblement d'aperçu : un ennemi CIBLABLE par le pion sélectionné/survolé vibre doucement.
        if (_run.Phase == RunPhase.Battle && _trembleTargets.Contains(cell))
        {
            var tr = TargetTremble(cell, size);
            zx += tr.X;
            zy += tr.Y;
        }

        var sprite = UnitSprite(unit);
        // « Rage » ACTIVE (le pion a gagné de la puissance à la mort d'alliés) : halo rouge pulsant DERRIÈRE le pion.
        if (sprite != null && unit.RagePower > 0 && unit.HasTrait(Trait.Rage))
            DrawRageAura(sb, sprite, zx, zy - spriteLift - animLift, size, introA);
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

        // Icône de TIER posée sur le socle (bas du sprite), centrée horizontalement, suit le lift et le fondu.
        // Échelle ENTIÈRE (× zoom des pions, comme les autres icônes de plateau) pour garder la même taille
        // RELATIVE quel que soit le zoom — sinon, l'icône 23×9 fixe paraît rétrécir quand les pions grandissent.
        var spriteTop = zy - spriteLift - animLift;
        var tierZoom = System.Math.Max(1, size / GridLayout.DefaultTileSize);
        int tiw = TierIconW * tierZoom, tih = TierIconH * tierZoom;
        var socleIcon = new Rectangle(
            zx + (size - tiw) / 2,
            spriteTop + (int)(size * SocleTierAnchor) - tih,
            tiw, tih);
        DrawTierIcon(sb, unit.Class.Tier, socleIcon, introA);

        // La barre de vie est dessinée dans une passe SÉPARÉE (DrawUnitHpBars), APRÈS les buissons,
        // pour qu'elle reste toujours visible (même quand le feuillage passe devant le pion).
    }

    /// <summary>Les 8 directions du halo (anneau autour de la silhouette).</summary>
    private static readonly (int Dx, int Dy)[] AuraRing =
        { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };

    /// <summary>
    /// Halo rouge PULSANT d'un pion « sous Rage » : on redessine sa SILHOUETTE TRAMÉE (blanche, donc teintable
    /// en rouge PLEIN — pas assombrie par les couleurs du sprite, cf. <see cref="ShadowStipple"/>) décalée en
    /// anneau DERRIÈRE le vrai sprite — deux passes (large plus pâle + proche vive) pour un glow qui déborde des
    /// bords. L'intensité respire au rythme de la colère et suit le fondu d'apparition du pion.
    /// </summary>
    private void DrawRageAura(SpriteBatch sb, Texture2D sprite, int x, int y, int size, float fade)
    {
        var stipple = ShadowStipple(sprite);                   // silhouette tramée BLANCHE (réutilisée de l'ombre)
        var pulse = 0.55f + 0.35f * MathF.Sin(_time * 6f);     // respiration rapide (colère)
        var spread = System.Math.Max(2, size / 22);           // épaisseur (px) du halo proche
        var outer = Palette.Purple5 * (0.35f * pulse * fade);  // couronne large, pâle
        var inner = Palette.Purple5 * (0.75f * pulse * fade);  // liseré proche, rouge vif
        foreach (var (dx, dy) in AuraRing)
            sb.Draw(stipple, new Rectangle(x + dx * spread * 2, y + dy * spread * 2, size, size), outer);
        foreach (var (dx, dy) in AuraRing)
            sb.Draw(stipple, new Rectangle(x + dx * spread, y + dy * spread, size, size), inner);
    }

    /// <summary>
    /// Décalage (px entiers) d'un « FRISSON » de peur pour un pion en alerte (ennemi ciblable ou allié menacé) :
    /// jitter TRÈS discret (~1 px) et rapide, surtout horizontal, DÉPHASÉ par case (les pions ne frissonnent pas
    /// à l'unisson). Deux ondes légèrement désaccordées donnent un tremblement nerveux et IRRÉGULIER, pas une
    /// oscillation lisse. Suit <see cref="_time"/>.
    /// </summary>
    private Point TargetTremble(Cell cell, int size)
    {
        var amp = MathF.Max(1f, size / 64f);                             // ~1 px : à peine perceptible
        var phase = _time * 26f + cell.Column * 2.3f + cell.Row * 3.1f;  // frisson posé, déphasé par case
        var dx = (MathF.Sin(phase) * 0.6f + MathF.Sin(phase * 2.2f) * 0.4f) * amp;   // jitter horizontal irrégulier
        var dy = MathF.Sin(phase * 1.6f) * amp * 0.55f;                  // soupçon de vertical
        return new Point((int)MathF.Round(dx), (int)MathF.Round(dy));
    }

    /// <summary>
    /// Passe des barres de vie, dessinée AU-DESSUS de tout (unités + buissons) : la jauge d'un pion
    /// reste lisible même si un buisson le masque. Mêmes exclusions et même position que <see cref="DrawUnit"/>.
    /// </summary>
    private void DrawUnitHpBars(SpriteBatch sb, GridLayout layout)
    {
        var size = layout.TileSize;

        // Pendant qu'on PORTE un pion, on VISE la case sous le curseur : si c'est une cible d'attaque
        // valide, on prévisualise les dégâts sur SA barre de vie (tranche menacée) — et on force la barre
        // à s'afficher même à pleine vie. Remplace la carte-tooltip (masquée pendant le glisser).
        Cell? aimed = null;
        var aimedPreview = 0;
        if (_combatDragFrom is { } dragFrom)
        {
            var over = Context.Input.UsingGamepad ? _cursor : CellUnderMouse();
            if (over is { } target && _attackTargets.Contains(target))
            {
                aimed = target;
                aimedPreview = _match.PreviewDamage(dragFrom, target);
            }
        }

        foreach (var (cell, unit) in _match.Units())
        {
            if (_combatDragFrom == cell)            // pion porté : pas de barre sur sa case
                continue;
            if (_fx.Active && _fx.Attacker == cell) // attaquant animé : géré par la passe FX
                continue;
            var isAimed = aimed == cell;
            if (unit.Hp >= unit.MaxHp && !isAimed)   // pleine vie : pas de barre (sauf cible visée → aperçu)
                continue;
            var top = layout.CellToScreen(cell.Column, cell.Row);
            var (introY, _) = BoardIntroAnim(cell, layout);
            var animLift = UnitLift(cell, size);
            var kb = IsFxVictim(cell) ? VictimKnockback(size) : ReculeSlideOffset(cell, layout);
            kb += _pierceRecoil.Offset(cell, size);   // la barre suit le recul du pion transpercé
            DrawUnitHpBar(sb, (int)top.X + kb.X, (int)top.Y + introY + kb.Y - animLift, size, unit.Hp, unit.MaxHp,
                isAimed ? aimedPreview : 0);
        }
    }

    /// <summary>
    /// Barre de PV VERTICALE sur le bord droit d'un pion, affichée uniquement quand il est blessé.
    /// Jauge PLEINE (pas de segments → aucun trait à désaligner, nette à tous les zooms) : le rouge
    /// remplit le bas en proportion des PV restants, le vert occupe le reste (PV manquants).
    /// Dimensions proportionnelles à la case pour garder les mêmes proportions quel que soit le zoom.
    /// <paramref name="previewDamage"/> &gt; 0 (cible visée pendant un glisser) : la tranche de PV qui
    /// serait perdue clignote plein↔vide en haut de la jauge restante — aperçu des dégâts.
    /// </summary>
    private void DrawUnitHpBar(SpriteBatch sb, int zx, int zy, int size, int hp, int maxHp, int previewDamage = 0)
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

        // Aperçu des dégâts : la tranche menacée (du haut des PV restants) clignote plein↔vide. Les PV
        // qui survivraient restent solides en bas ; un coup létal fait clignoter toute la jauge.
        if (previewDamage > 0)
        {
            var doomed = System.Math.Min(hp, previewDamage);
            var survivH = (int)System.Math.Round((double)barH * (hp - doomed) / maxHp);
            var doomH = fillH - survivH;
            if (doomH > 0)
            {
                var blink = 0.5f + 0.5f * MathF.Sin(_time * 12f);
                var col = Color.Lerp(Palette.Green4, Palette.Purple5, blink);
                DrawRect(sb, new Rectangle(x, y + barH - fillH, barW, doomH), col);
            }
        }
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

        // Ombre projetée des objets (comme les pions), seulement si l'objet a un PNG. Le coffre et le
        // buisson sont ancrés au sol comme la recrue ; le buisson n'est jamais consommé (couvert permanent).
        DrawObjectCastShadows(sb, layout, _recrueCells, RecrueSpriteFor, _recrueConsumed);
        DrawObjectCastShadows(sb, layout, _chestCells, _chestSprite, _chestConsumed, ChestShadowShear);
        DrawObjectCastShadows(sb, layout, _bushCells, _bushSprite, consumed: null);
    }

    /// <summary>
    /// Ombre projetée des objets d'un type (coffre / recrue / buisson) : silhouette cisaillée du PNG,
    /// posée au sol comme celle des pions. Sans PNG (placeholder) ou objet consommé : aucune ombre.
    /// </summary>
    private void DrawObjectCastShadows(SpriteBatch sb, GridLayout layout, List<Cell> cells,
        Texture2D? sprite, HashSet<Cell>? consumed, float shear = ShadowShear)
        => DrawObjectCastShadows(sb, layout, cells, _ => sprite, consumed, shear);

    /// <summary>Variante par-case : la silhouette dépend de la case (objet à plusieurs PNG, cf. recrue).</summary>
    private void DrawObjectCastShadows(SpriteBatch sb, GridLayout layout, List<Cell> cells,
        System.Func<Cell, Texture2D?> spriteFor, HashSet<Cell>? consumed, float shear = ShadowShear)
    {
        if (cells.Count == 0)
            return;
        var size = layout.TileSize;
        var spriteLift = (int)(size * SpriteLiftFraction);
        foreach (var c in cells)
        {
            if (consumed != null && consumed.Contains(c))
                continue;
            if (spriteFor(c) is not { } sprite)
                continue;
            var top = layout.CellToScreen(c.Column, c.Row);
            var (introY, introA) = BoardIntroAnim(c, layout);
            // L'objet est dessiné à plat (top.Y), mais on ANCRE son ombre au niveau du socle d'un pion
            // (top.Y - spriteLift) : sinon la silhouette rabattue vers l'avant déborderait dans la case
            // du dessous. Ainsi l'ombre reste serrée à la base, dans la case. Objet immobile → lift 0.
            DrawPieceCastShadow(sb, sprite, (int)top.X, (int)top.Y - spriteLift + introY, size, lift: 0, introA, shear);
        }
    }

    /// <summary>
    /// Ombre projetée d'un pion dont la silhouette « au repos » occupe (<paramref name="destX"/>,
    /// <paramref name="destY"/>). Quand le pion est en l'air (<paramref name="lift"/> &gt; 0), l'ombre
    /// GLISSE dans la direction de la lumière et S'ÉCLAIRCIT → lecture nette du décollage.
    /// </summary>
    private void DrawPieceCastShadow(SpriteBatch sb, Texture2D sprite, int destX, int destY, int size, int lift,
        float fade = 1f, float shear = ShadowShear)
    {
        var k = MathHelper.Clamp(lift / (size * CarriedLiftFraction), 0f, 1f);
        var slideX = (int)(lift * ShadowLiftSlide);          // glisse vers la lumière (droite, comme le cisaillement)
        var slideY = (int)(lift * ShadowLiftSlide * 0.35f);  // et un peu vers le bas/avant
        var alpha = ShadowAlpha * (1f - ShadowLiftFade * k) * fade;   // fade = fondu d'émergence du plateau

        var dest = new Rectangle(destX + slideX, destY + slideY, size, size);
        var anchor = new Vector2(dest.X + size / 2f, dest.Y + size * ShadowAnchorFraction);
        DrawSilhouetteShadow(sb, sprite, dest, anchor, alpha, shear);
    }

    /// <summary>
    /// Dessine une silhouette de sprite en ombre : matrice qui, AUTOUR de <paramref name="anchor"/>
    /// (la base du socle), cisaille latéralement et rabat/aplatit la silhouette vers le bas
    /// (<see cref="ShadowFlatten"/> &lt; 0 → l'ombre tombe vers l'avant). Teinte sombre semi-transparente.
    /// </summary>
    private void DrawSilhouetteShadow(SpriteBatch sb, Texture2D sprite, Rectangle dest, Vector2 anchor,
        float alpha, float shear = ShadowShear)
    {
        var transform =
            Matrix.CreateTranslation(-anchor.X, -anchor.Y, 0f)
            * new Matrix(1f, 0f, 0f, 0f,
                         -shear, ShadowFlatten, 0f, 0f,
                         0f, 0f, 1f, 0f,
                         0f, 0f, 0f, 1f)
            * Matrix.CreateTranslation(anchor.X, anchor.Y, 0f);

        // CullNone : ShadowFlatten < 0 retourne la silhouette (inverse le sens des triangles) →
        // sans ça le SpriteBatch l'éliminerait par culling et l'ombre serait invisible.
        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: transform,
            rasterizerState: RasterizerState.CullNone);
        // Silhouette TRAMÉE (demi-teinte Bayer de pixels pleins) plutôt qu'un aplat lissé → pixel-art.
        sb.Draw(ShadowStipple(sprite), dest, Palette.Black1 * alpha);
        sb.End();
    }

    /// <summary>Silhouette tramée (pixel-art) d'un sprite, générée à la demande puis mise en cache.</summary>
    private Texture2D ShadowStipple(Texture2D sprite) =>
        _shadowStipple.TryGetValue(sprite, out var s)
            ? s
            : _shadowStipple[sprite] = Textures.CreateShadowStipple(Context.GraphicsDevice, sprite);

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

    /// <summary>
    /// Combat porté, version DÉZOOM : ne dessine que le LISERÉ de la case cible sur la couche plateau (le pion
    /// soulevé lui-même est dessiné par la couche curseur <see cref="RenderGhostLayer"/>, au premier plan).
    /// </summary>
    private void DrawCarriedUnitNative(SpriteBatch sb, GridLayout nb)
    {
        if (_combatDragFrom is not { } from || _match.UnitAt(from) is null)
            return;
        if (CellUnderMouse() is { } target && (_legalMoves.Contains(target) || _attackTargets.Contains(target)))
            DrawZoneBorder(sb, nb, target, Palette.White, 2);
    }

    // ── Effets de combat (estafilade / dissolution / flash) ───────────────────────

    /// <summary>Renvoie le layout décalé de la secousse d'écran d'une attaque qui s'anime, sinon tel quel.
    /// (Le « Séisme » ne secoue plus l'écran : il fait trembler LOCALEMENT les tuiles de l'AoE, cf. TileTremor.)</summary>
    private GridLayout ShakeBoard(GridLayout layout)
    {
        if (!_fx.Active)
            return layout;
        var s = _fx.ShakeOffset(_fx.Killed ? 4f : 2f);   // secousse plus marquée sur un kill
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
        // Victime : normalement sur sa case (léger recul kb) ; si elle a été REPOUSSÉE d'une case (« Recule »),
        // elle GLISSE de sa case d'origine vers sa case d'arrivée — le glissement remplace le petit recul.
        var kb = VictimKnockback(size);
        var victimTop = toTop;
        if (_reculeSlide is { } rs)
        {
            victimTop = Vector2.Lerp(
                layout.CellToScreen(rs.From.Column, rs.From.Row) - new Vector2(0, spriteLift),
                layout.CellToScreen(rs.To.Column, rs.To.Row) - new Vector2(0, spriteLift),
                _fx.VictimSlide);
            kb = Point.Zero;
        }
        var victimRect = new Rectangle((int)victimTop.X + kb.X, (int)victimTop.Y + kb.Y, size, size);

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

    /// <summary>Éclairs d'ORAGE/TEMPÊTE : un éclair pixel-art s'abat sur chaque pion foudroyé, légèrement
    /// désynchronisés, puis s'éteignent. Rendu au-dessus du plateau, sous les chiffres de dégâts.</summary>
    private void DrawStormFx(SpriteBatch sb, GridLayout layout)
    {
        var size = layout.TileSize;
        var t = _storm.Progress;
        sb.Begin(samplerState: SamplerState.PointClamp);
        foreach (var cell in _storm.Cells)
        {
            var top = layout.CellToScreen(cell.Column, cell.Row);
            DrawLightningBolt(sb, top, size, t, seed: cell.Column * 7 + cell.Row * 13);
        }
        sb.End();
    }

    /// <summary>
    /// Un éclair : zigzag vertical (halo jaune + cœur blanc) qui frappe le pion du haut de la case, plus un
    /// flash sur la case. Forme figée par <paramref name="seed"/> ; alpha piloté par l'avancement local
    /// (léger décalage par case → tous ne frappent pas exactement en même temps).
    /// </summary>
    private void DrawLightningBolt(SpriteBatch sb, Vector2 cellTop, int size, float t, int seed)
    {
        var local = MathHelper.Clamp((t - (seed % 5) * 0.03f) / 0.55f, 0f, 1f);
        var alpha = 1f - local;                         // vif à la frappe, s'éteint
        if (alpha <= 0.03f)
            return;

        var cx = cellTop.X + size / 2f;
        var top = cellTop.Y - size * 0.9f;              // part au-dessus de la case
        var bottom = cellTop.Y + size * 0.3f;           // jusqu'au buste du pion
        var block = System.Math.Max(2, size / 12);

        var rng = new System.Random(seed);
        const int segments = 5;
        var prev = new Vector2(cx, top);
        for (var s = 1; s <= segments; s++)
        {
            var y = MathHelper.Lerp(top, bottom, s / (float)segments);
            var jitter = (float)(rng.NextDouble() * 2 - 1) * size * 0.18f;
            var next = new Vector2(cx + (s == segments ? 0f : jitter), y);   // pointe recentrée sur le pion
            DrawBoltSegment(sb, prev, next, block, Palette.White * alpha, Palette.Yellow2 * alpha);
            prev = next;
        }

        // Flash de la case au moment de la frappe (halo doux qui s'éteint avec l'éclair).
        DrawRect(sb, new Rectangle((int)cellTop.X, (int)cellTop.Y, size, size), Palette.Yellow2 * (alpha * 0.45f));
    }

    /// <summary>Segment d'éclair : blocs pixel-art le long de [a,b] — un halo large derrière, un cœur clair devant.</summary>
    private void DrawBoltSegment(SpriteBatch sb, Vector2 a, Vector2 b, int block, Color core, Color halo)
    {
        var steps = System.Math.Max(1, (int)(Vector2.Distance(a, b) / block));
        for (var i = 0; i <= steps; i++)
        {
            var p = Vector2.Lerp(a, b, i / (float)steps);
            DrawRect(sb, new Rectangle((int)p.X - block, (int)p.Y - block / 2, block * 2, block), halo);
        }
        for (var i = 0; i <= steps; i++)
        {
            var p = Vector2.Lerp(a, b, i / (float)steps);
            DrawRect(sb, new Rectangle((int)p.X - block / 2, (int)p.Y - block / 2, block, block), core);
        }
    }

    /// <summary>Vrai pour la case d'une victime SURVIVANTE en cours d'animation (à reculer dans DrawUnit).</summary>
    private bool IsFxVictim(Cell cell) => _fx.Active && !_fx.Killed && cell == _fx.To;

    /// <summary>
    /// Décalage (px entiers) du GLISSEMENT « Recule » pour la case <paramref name="cell"/> : la victime, déjà
    /// posée par le moteur sur sa case d'ARRIVÉE, est dessinée partant de sa case d'ORIGINE puis rejoignant
    /// l'arrivée au fil de <see cref="MeleeStrikeFx.VictimSlide"/>. Point.Zero hors recul-glissé (ou FX inactif).
    /// Ancré sur la case RÉELLE (arrivée) : l'offset part de (origine − arrivée) et s'annule à la fin.
    /// </summary>
    private Point ReculeSlideOffset(Cell cell, GridLayout layout)
    {
        if (_reculeSlide is not { } rs || !_fx.Active || cell != rs.To)
            return Point.Zero;
        var from = layout.CellToScreen(rs.From.Column, rs.From.Row);
        var to = layout.CellToScreen(rs.To.Column, rs.To.Row);
        var off = (from - to) * (1f - _fx.VictimSlide);
        return new Point((int)MathF.Round(off.X), (int)MathF.Round(off.Y));
    }

    /// <summary>
    /// Décalage (px entiers) de la victime pendant l'anim : recul à l'opposé de l'attaquant au contact, OU —
    /// en cas d'ESQUIVE — un BOND DE CÔTÉ (perpendiculaire à l'attaque, aller-retour) au lieu d'être touchée.
    /// </summary>
    private Point VictimKnockback(int size)
    {
        if (!_fx.Active)
            return Point.Zero;

        var dir = new Vector2(_fx.To.Column - _fx.From.Column, _fx.To.Row - _fx.From.Row);
        if (dir.LengthSquared() > 0f)
            dir.Normalize();

        if (_fx.Dodged)
        {
            var d = _fx.DodgeAmount;
            if (d <= 0f)
                return Point.Zero;
            var perp = new Vector2(-dir.Y, dir.X);           // perpendiculaire : côté vers lequel on s'écarte
            var mag = size * 0.38f * d;                      // amplitude du bond (plus ample que le recul)
            return new Point((int)MathF.Round(perp.X * mag), (int)MathF.Round(perp.Y * mag));
        }

        var amt = _fx.KnockbackAmount;
        if (amt <= 0f)
            return Point.Zero;
        var kmag = size * 0.16f * amt;
        return new Point((int)MathF.Round(dir.X * kmag), (int)MathF.Round(dir.Y * kmag));
    }

    /// <summary>Au contact (une fois par attaque) : fait jaillir le chiffre de dégâts, qui éclatera
    /// ensuite en feu d'artifice. Plus d'étincelles d'impact (le dev les trouvait trop chargées avec
    /// l'explosion du chiffre).</summary>
    private void OnImpact()
    {
        _impactHandled = true;
        if (_pendingDodge)
        {
            // Esquive : pas de chiffre de dégâts — un « ESQUIVE ! » bleu jaillit et un « whoosh » accompagne le bond.
            _damagePopups.SpawnText(_fx.To, Loc.T("fx.dodge"), Palette.Cyan1);
            Context.Sounds.Play("dodge");
        }
        else
        {
            _damagePopups.Spawn(_fx.To, _pendingDamage);   // le chiffre de dégâts jaillit au contact (puis éclate)
            if (_pendingGiantBonus > 0)   // « Tueur de géants » : « +N » rouge au-dessus du chiffre (part du bonus)
                _damagePopups.SpawnBonus(_fx.To, _pendingGiantBonus, Palette.Purple5);
            if (_pendingPhenix)   // renaissance : callout « PHÉNIX ! » au-dessus du coup encaissé
                _damagePopups.SpawnText(_fx.To, Loc.T("fx.phenix"), Palette.Brown3);
        }
        _pendingGiantBonus = 0;   // consommé (évite un report sur une action ultérieure sans bonus)

        // Orage / Tempête : au contact, les éclairs s'abattent sur les ennemis foudroyés et leurs chiffres
        // de dégâts jaillissent.
        if (_pendingStormBolts != null)
        {
            _storm.Begin(_pendingStormBolts);
            Context.Sounds.Play("storm");   // décharge de foudre, UNE fois pour toute la salve d'éclairs
            foreach (var (cell, dmg) in _pendingStormHits!)
                _damagePopups.Spawn(cell, dmg);
            _pendingStormBolts = null;
            _pendingStormHits = null;
        }

        // Impact (trait) : chiffres sur les ennemis frappés + tremblement des tuiles de l'AoE + son, au contact de l'attaque.
        if (_pendingImpactHits != null)
        {
            foreach (var (cell, dmg) in _pendingImpactHits)
            {
                // La cible ATTAQUÉE encaisse l'attaque ET l'impact sur la même case : on décale l'impact en
                // « +N » (en haut à gauche) pour qu'il ne recouvre pas le chiffre de l'attaque. Les autres
                // ennemis de l'AoE (et un impact SANS attaque, ex. déplacement) gardent un chiffre normal centré.
                if (cell == _fx.To && _pendingDamage > 0)
                    _damagePopups.SpawnBonus(cell, dmg, Palette.Yellow2, new Vector2(-0.22f, -0.34f));
                else
                    _damagePopups.Spawn(cell, dmg);
            }
            if (_pendingImpactZone != null)
                ShakeAoeZone(_pendingImpactZone);
            _pendingImpactHits = null;
            _pendingImpactZone = null;
        }

        // Recule (trait) : chiffre du dégât BONUS de plaquage sur la cible restée collée à l'obstacle.
        if (_pendingReculeSlam is { } slam)
        {
            _damagePopups.Spawn(slam.Cell, slam.Damage);
            _pendingReculeSlam = null;
        }

        // Transpercement : le pion DERRIÈRE la cible encaisse comme un coup normal — recul directionnel, chiffre
        // de dégâts et mot-clé « TRANSPERCER » posé juste au-dessus. S'il en est mort le moteur l'a déjà retiré :
        // le recul n'a alors aucun pion à décaler, mais le chiffre et le mot-clé jaillissent quand même.
        if (_pendingPierce is { } pierce)
        {
            _pierceRecoil.Begin(pierce.Cell, pierce.Dc, pierce.Dr);
            _damagePopups.Spawn(pierce.Cell, pierce.Damage);
            _damagePopups.SpawnText(pierce.Cell, Loc.T("fx.transpercer"), Palette.Cyan2, new Vector2(0f, -0.5f));
            _pendingPierce = null;
        }
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

    /// <summary>Bouton d'ouverture de l'arbre de commandement, juste au-dessus de COMBATTRE (placement seulement).</summary>
    private Rectangle CommandTreeButtonRect()
    {
        var f = FightButtonRect();
        return new Rectangle(f.X, f.Y - f.Height - 8, f.Width, f.Height);
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
            if (!InvSlotVisible(s) || !SlotRect(s).Contains(p))   // ignore les slots défilés hors vue
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

        // Place dans la RÉSERVE (roster hors commandant / plafond), TOUJOURS visible en placement : le joueur
        // voit combien de pions il peut encore recruter. Rouge quand elle est pleine (aligné à droite du titre).
        var reserve = Loc.T("reserve.count", _run.ReserveCount, _run.ReserveLimit);
        Context.Font.Draw(sb, reserve,
            new Vector2(panel.Right - PanelPad - Context.Font.Measure(reserve, 1), 40),
            1, _run.IsReserveFull ? Palette.Purple5 : Palette.Cyan1);

        Context.Font.Draw(sb, Loc.T("placement.inventory"), new Vector2(x, PanelListTop - 22), 1, Palette.Blue1);

        // Compteur de déploiement (commandant compris), aligné à droite de l'en-tête d'inventaire.
        // Rouge quand le plafond est atteint : signale que les unités restantes ne pourront pas être posées.
        var full = _playerSpec.Count >= MaxDeployed;
        var counter = Loc.T("placement.deployed", _playerSpec.Count, MaxDeployed);
        Context.Font.Draw(sb, counter,
            new Vector2(panel.Right - PanelPad - Context.Font.Measure(counter, 1), PanelListTop - 22),
            1, full ? Palette.Purple5 : Palette.Cyan1);

        ClampInvScroll();   // au cas où la réserve a rétréci (drag / fusion) depuis la dernière image
        var anyFusable = false;
        for (var i = 0; i < _pending.Count; i++)
        {
            if (InvSlotVisible(PendingVisualSlot(i)))
                DrawInventoryCard(sb, _pending[i], PendingCardRect(i));   // saute le slot de la pile
            if (CanFuseFromReserve(_pending[i]))
                anyFusable = true;   // sert juste à afficher l'indice de fusion (aucun cadre coloré)
        }

        // Pile de fusion en cours (état « N/3 ») + son bouton d'annulation.
        DrawFusionStack(sb);

        // Barre de défilement à droite de la grille dès que la réserve déborde du panneau.
        DrawInventoryScrollbar(sb);

        // Aide JUSTE SOUS la grille VISIBLE (position stable, que la réserve défile ou non).
        var shownRows = System.Math.Min(InvTotalRows(), InvVisibleRows());
        var hintY = PanelListTop + shownRows * InvRowPitch + 12;
        if (Context.Input.UsingGamepad)
        {
            if (_dragSpec != null)
            {
                // On PORTE un pion : poser / annuler, et RELANCER (X) si une relance est dispo.
                Context.Font.Draw(sb, Loc.T("placement.hint_gp_hold"), new Vector2(x, hintY), 1, Palette.Blue1);
                if (!_dragSpec.Essential && _run.HasReroll)
                    Context.Font.Draw(sb, Loc.T("placement.hint_gp_reroll"), new Vector2(x, hintY + 16), 1, Palette.Yellow2);
            }
            else
            {
                // L'indice décrit la zone de focus COURANTE (plateau / inventaire / boutons du panneau).
                var line1 = _gpButtons ? Loc.T("placement.hint_gp_btn")
                    : _gpInventory ? Loc.T("placement.hint_gp_terrain")
                    : Loc.T("placement.hint_gp_inventory");
                Context.Font.Draw(sb, line1, new Vector2(x, hintY), 1, Palette.Blue1);
                Context.Font.Draw(sb, Loc.T("placement.hint_gp_fight"), new Vector2(x, hintY + 16), 1, Palette.Cyan1);
                if (anyFusable)
                    Context.Font.Draw(sb, Loc.T("placement.hint_gp_fuse"), new Vector2(x, hintY + 32), 1, Palette.Yellow2);
                if (FusionStacking)   // une pile en cours : B pour la défusionner
                    Context.Font.Draw(sb, Loc.T("placement.hint_gp_unfuse"),
                        new Vector2(x, hintY + (anyFusable ? 48 : 32)), 1, Palette.Yellow2);
            }
        }
        else
        {
            Context.Font.Draw(sb, Loc.T("placement.hint_drag"), new Vector2(x, hintY), 1, Palette.Blue1);
            Context.Font.Draw(sb, Loc.T("placement.hint_fight"), new Vector2(x, hintY + 16), 1, Palette.Cyan1);
            if (anyFusable)
                Context.Font.Draw(sb, Loc.T("placement.hint_fuse"), new Vector2(x, hintY + 32), 1, Palette.Yellow2);
        }

        // Boutons du bas (souris + focus manette).
        if (ShowCommandTreeButton)
        {
            var affordable = _run.CommandPoints >= CommandTree.CostOf(1);
            DrawPanelButton(sb, CommandTreeButtonRect(), Loc.T("tree.button", _run.CommandPoints),
                affordable ? Palette.Yellow2 : Palette.White, focusIndex: 0, pointIcon: true);
        }
        if (ShowFightButton)
        {
            // Si le joueur a des équipements, le bouton mène d'abord à la sous-phase Équipement (« Suivant »).
            var fightLabel = _run.HasEquipment ? Loc.T("equip.next") : Loc.T("placement.fight");
            // En tuto, le bouton est là dès le début mais ne mord qu'à l'étape « lancer le combat » : on le
            // grise avant, plutôt que de le cacher — le joueur voit d'emblée où se lance un combat.
            var armed = _tutorial is null or { Step: TutorialStep.StartCombat };
            DrawPanelButton(sb, FightButtonRect(), fightLabel, armed ? Palette.White : Palette.Grey,
                focusIndex: FightButtonIndex, enabled: armed);
        }

        DrawRerollIcon(sb);   // icône de relance à gauche du panneau
    }

    /// <summary>
    /// Un bouton du bas du panneau de placement. Survol à la souris (enfoncement), cadre or quand il porte
    /// le focus MANETTE (<see cref="_gpButtons"/> + <see cref="_btnFocus"/>) — jamais les deux à la fois,
    /// l'indice affiché suivant déjà le périphérique courant. <paramref name="pointIcon"/> accole le jeton
    /// de points de commandement au libellé (bouton COMMANDEMENT). <paramref name="enabled"/> faux : le
    /// bouton est dessiné au repos (ni survol ni focus), signe qu'il n'agira pas encore.
    /// </summary>
    private void DrawPanelButton(SpriteBatch sb, Rectangle btn, string label, Color color, int focusIndex,
        bool pointIcon = false, bool enabled = true)
    {
        var gamepad = Context.Input.UsingGamepad;
        var focused = enabled && gamepad && _gpButtons && _btnFocus == focusIndex;
        var hover = enabled && !gamepad && btn.Contains(Context.Input.MousePosition);
        var dy = Context.Style.DrawButton(sb, btn, UiStyle.StateOf(hover || focused, hover && Context.Input.IsLeftDown));

        var area = btn; area.Offset(0, dy);
        if (focused)
            DrawRectBorder(sb, Inflate(area, 2), Palette.Yellow2, 2);

        var textColor = focused ? Palette.Yellow2 : color;
        if (pointIcon)
            _commandTree.DrawPointTotal(sb, Context.Font, label, area, textColor);
        else
            Context.Font.DrawCentered(sb, label, area, 1, textColor);
    }

    /// <summary>
    /// Carte de la PILE de fusion de RÉSERVE (« N/3 ») juste après la réserve, avec son bouton « X ».
    /// Rien si aucune pile, si la pile est sur le plateau, ou si la popup est ouverte (pile complète).
    /// </summary>
    private void DrawFusionStack(SpriteBatch sb)
    {
        if (!FusionStacking || !FusionInReserve || _carryPile)   // pas dessinée quand portée
            return;
        // La grille ne défile qu'au placement ; le panneau de réserve du combat, lui, affiche tout (non
        // défilé), donc on n'y masque jamais la pile.
        if (_run.Phase == RunPhase.Placement && ReservePileSlot() is { } slot && !InvSlotVisible(slot))
            return;   // pile défilée hors de la fenêtre visible

        var card = FusionStackCardRect();
        DrawFusionPileChip(sb, _fusionGroup[0].UnitClass, card, front: true);

        // Compteur « N/3 » sous la pile.
        Context.Font.DrawCentered(sb, $"{_fusionGroup.Count}/{FusionGroupTarget}",
            new Rectangle(card.X - InvGapX / 2, card.Bottom + 2, card.Width + InvGapX, 10), 1, Palette.Yellow2);
        DrawFusionCancelButton(sb, FusionStackCancelRect());
    }

    /// <summary>
    /// Fine barre de défilement à droite de la grille de réserve, seulement quand elle déborde. Piste
    /// sombre + curseur cyan proportionnel aux rangées visibles, positionné selon <see cref="_invScrollRow"/>.
    /// </summary>
    private void DrawInventoryScrollbar(SpriteBatch sb)
    {
        var max = InvMaxScrollRow();
        if (max == 0)
            return;
        var panel = PanelRect();
        var top = PanelListTop;
        var trackH = InvVisibleRows() * InvRowPitch;
        var trackX = panel.Right - 14;   // entre le bord droit de la grille et la bande sombre du bord
        DrawRect(sb, new Rectangle(trackX, top, 2, trackH), Palette.Navy1);

        var thumbH = System.Math.Max(10, trackH * InvVisibleRows() / InvTotalRows());
        var thumbY = top + (trackH - thumbH) * _invScrollRow / max;
        DrawRect(sb, new Rectangle(trackX, thumbY, 2, thumbH), Palette.Cyan1);
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
        Context.Font.DrawCentered(sb, $"{_fusionGroup.Count}/{FusionGroupTarget}",
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
        Context.Font.DrawCentered(sb, $"{_fusionGroup.Count}/{FusionGroupTarget}",
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

        // Cartes d'évolution. Le sprite reste en SILHOUETTE tant que le joueur n'a jamais obtenu cette
        // évolution (méta-progression) — et son détail de traits reste masqué (entrée nulle dans la rangée).
        var kwRow = new List<UnitClass?>(count);
        for (var i = 0; i < count; i++)
        {
            var rect = FusionCardRect(i, count);
            var revealed = Context.Saves.IsUnitDiscovered(options[i].Asset);
            DrawCardLayout(sb, rect, options[i], Faction.Player, domaine, options[i].MaxHp, options[i].MaxHp, revealed);
            kwRow.Add(revealed ? options[i] : null);
        }
        // Détail des traits : sous les cartes si tout y tient (le cas en 1440p), sinon au survol seulement —
        // deux évolutions à 3-4 traits ne rentrent pas sous les cartes en 1080p.
        var vpF = VirtualViewport;
        DrawRowKeywords(sb, kwRow, _fusionFocus, vpF.Width, vpF.Height, vpF.Height - KwScreenMargin);

        // Surbrillance de la carte focus.
        var fi = System.Math.Clamp(_fusionFocus, 0, count - 1);
        DrawRectBorder(sb, Inflate(FusionCardRect(fi, count), 3), Palette.Yellow2, 3);
        sb.End();
    }

    /// <summary>
    /// Révélation d'une recrue gagnée via une tuile « recrue » : voile + carte centrée de l'unité +
    /// « une unité veut rejoindre votre armée ». Le combat est figé jusqu'au clic (cf. UpdateBattle).
    /// L'unité est déjà ajoutée à l'armée ; le clic ne fait que fermer la carte.
    /// </summary>
    private void DrawRecrueReveal(SpriteBatch sb, Viewport viewport)
    {
        if (_recrueReveal is not { } spec)
            return;

        var availW = viewport.Width - RightPanelWidth;    // zone des cartes, à gauche du panneau

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawDim(sb, viewport);

        // Réserve (= _pending, non déployés en combat) à droite : on voit où la recrue va atterrir + on peut
        // FUSIONNER façon placement (empiler → popup) pour faire de la place.
        DrawPanelBackground(sb);
        DrawReservePanelFusion(sb);

        if (_recruitChoice == null && !_recrueAdded)   // phase CARTE : carte centrée, puis décision
        {
            var card = DraftCardRect(0, 1, availW, viewport.Height);
            Context.Font.DrawCentered(sb, Loc.T("recrue.join"),
                new Rectangle(0, card.Y - 44, availW, 24), 2, Palette.Yellow2);
            DrawCardLayout(sb, card, spec.UnitClass, Faction.Player, spec.Domaine, spec.UnitClass.MaxHp, spec.UnitClass.MaxHp);

            if (_run.IsReserveFull)
            {
                var ab = RecruitAbandonBtnRect(availW, viewport.Height);
                Context.Style.FillDither(sb, ab);
                DrawRectBorder(sb, ab, Palette.Purple5, 2);
                Context.Font.DrawCentered(sb, Loc.T("recruit.abandon"), ab, 1, Palette.Purple5);
                Context.Font.DrawCentered(sb, Loc.T("recruit.hold_prompt"),
                    new Rectangle(0, ab.Bottom + 6, availW, 12), 1, Palette.Cyan1);
            }
            else
            {
                Context.Font.DrawCentered(sb, Loc.T("recrue.continue"),
                    new Rectangle(0, card.Bottom + 14, availW, 16), 1, Palette.Cyan1);
            }
        }
        DrawDragGhost(sb);   // pion tenu (drag de fusion réserve, souris) AU-DESSUS du panneau, dans le batch actif
        sb.End();

        if (FusionOpen) DrawFusionPopup(sb, viewport);
        if (EvoPlaying) DrawEvolutionAnimation(sb, viewport);

        // Phase VOL : la recrue file de la carte vers son slot de réserve (slot suivant = _pending.Count).
        if (_recruitChoice is { } flying && _recruitHold > 0f)
            DrawRecruitFlight(sb, flying, _pending.Count);
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
                DrawSpecPreviewCard(sb, _pending[System.Math.Clamp(_invFocus, 0, _pending.Count - 1)]);
            else if (!_gpInventory && !_gpButtons && _match.UnitAt(_cursor) is { } cu)
                DrawPreviewCard(sb, cu.Class, cu.Faction, cu.Domaine, cu.Hp, cu.MaxHp, cu.Equipment, cu.Buffs,
                    TreeNodesFor(cu), cu.Kills, subject: _cursor);
            return;
        }

        var mouse = Context.Input.MousePosition;

        // Priorité : portrait survolé dans l'inventaire (PV pleins, unité neuve).
        if (PanelCardAt(mouse) is { } i)
        {
            DrawSpecPreviewCard(sb, _pending[i]);
            return;
        }

        // Sinon : pièce posée sous le curseur souris (joueur ou ennemi déjà déployé).
        if (CellUnderMouse() is { } cell && _match.UnitAt(cell) is { } unit)
            DrawPreviewCard(sb, unit.Class, unit.Faction, unit.Domaine, unit.Hp, unit.MaxHp, unit.Equipment, unit.Buffs,
                TreeNodesFor(unit), unit.Kills, subject: cell);
    }

    /// <summary>
    /// Carte d'aperçu, équipement inclus. Sans case décrite (aperçu RÉSERVE), à DROITE du plateau — juste à
    /// gauche du panneau d'inventaire (<see cref="RightCardRect"/> se borne à son bord en placement).
    /// <paramref name="subject"/> = case du pion décrit (survol du PLATEAU) : la carte se cale alors JUXTE le
    /// pion (cf. <see cref="CardRectNearCell"/>).
    /// </summary>
    private void DrawPreviewCard(SpriteBatch sb, UnitClass c, Faction faction, Domaine domaine, int hp, int maxHp,
        Equipment? equip = null, CommandBuffs? buffs = null, IReadOnlyList<CommandNode>? treeNodes = null, int kills = 0,
        Cell? subject = null)
    {
        var layout = BuildLayout();
        var board = BoardRect(layout);
        // Décrit une case du plateau (survol) → carte JUXTE le pion ; sinon (aperçu réserve) → à droite du plateau.
        // Carte d'aperçu = toujours DÉTAILLÉE (révélations recrue/évolution), donc au gabarit de combat plein.
        var rect = subject is { } s ? CardRectNearCell(s, layout, CombatCardW, CombatCardH) : RightCardRect(board);
        // Carte + popups DIFFÉRÉS (cf. DrawDeferredCards) : la carte d'aperçu doit rester lisible PAR-DESSUS
        // la frise, le briefing et le panneau, dessinés après elle.
        _deferredCards.Add(() => DrawCardLayout(Context.SpriteBatch, rect, c, faction, domaine, hp, maxHp,
            equip: equip, buffs: buffs, treeNodes: treeNodes, kills: kills));
        _deferredKeywordPopups.Add((c, rect, equip, buffs, null, faction));
    }

    /// <summary>
    /// Carte d'aperçu d'un gabarit de la RÉSERVE (pion non posé) : PV pleins, équipement ET bonus de
    /// l'arbre de commandement inclus — la réserve doit annoncer les mêmes chiffres que le plateau.
    /// </summary>
    private void DrawSpecPreviewCard(SpriteBatch sb, UnitSpec spec)
    {
        var buffs = _run.BuffsFor(spec);
        var maxHp = spec.UnitClass.MaxHp
                    + (spec.Equipment?.BonusFor(EquipStat.Hp) ?? 0)
                    + buffs.BonusFor(EquipStat.Hp);
        DrawPreviewCard(sb, spec.UnitClass, Faction.Player, spec.Domaine, maxHp, maxHp, spec.Equipment, buffs,
            _run.ActiveNodesFor(spec.Essential), spec.Kills);
    }

    private void DrawInventoryCard(SpriteBatch sb, UnitSpec spec, Rectangle icon)
    {
        // Portrait 64×64 à taille native (jamais redimensionné), de FACE (présentation), nom dessous.
        DrawChip(sb, spec.UnitClass, Faction.Player, icon, front: true);
        Context.Font.DrawCentered(sb, UnitName(spec.UnitClass).ToUpperInvariant(),
            new Rectangle(icon.X - InvGapX / 2, icon.Bottom + 2, icon.Width + InvGapX, 10), 1, Palette.White);
    }

    // ── Coffres + sous-phase Équipement (rendu) ───────────────────────────────────

    /// <summary>Dessine les coffres FERMÉS (non encore ouverts) du combat — simple PNG/placeholder, sous les unités.</summary>
    private void DrawChests(SpriteBatch sb, GridLayout layout)
    {
        if (_chestCells.Count == 0)
            return;
        var size = layout.TileSize;
        foreach (var c in _chestCells)
        {
            if (_chestConsumed.Contains(c))
                continue;   // ouvert : retiré du plateau (l'ouverture se joue en modale, cf. DrawChestReveal)
            var (introY, introA) = BoardIntroAnim(c, layout);
            var top = layout.CellToScreen(c.Column, c.Row);
            var zx = (int)top.X;
            var zy = (int)top.Y + introY;
            if (_chestSprite != null)
            {
                // PNG 64×64 rendu sur la surface de la case, CARRÉ (jamais déformé), comme un pion.
                sb.Draw(_chestSprite, new Rectangle(zx, zy, size, size), Color.White * introA);
                continue;
            }
            // Placeholder dessiné dans un carré centré (proportions fixes) : coffre brun, couvercle, serrure.
            var box = size * 3 / 4;
            var rect = new Rectangle(zx + (size - box) / 2, zy + (size - box) / 2, box, box);
            DrawRect(sb, rect, Palette.Brown1 * introA);
            DrawRect(sb, new Rectangle(rect.X, rect.Y, rect.Width, rect.Height / 3), Palette.Brown3 * introA);
            DrawRectBorder(sb, rect, Palette.Black1 * introA, 2);
            var lockS = System.Math.Max(4, size / 12);
            DrawRect(sb, new Rectangle(rect.Center.X - lockS / 2, rect.Y + rect.Height / 3 - lockS / 2, lockS, lockS),
                Palette.Yellow1 * introA);
        }
    }

    /// <summary>
    /// Charge les looks possibles de l'objet recrue : chaque paire <c>&lt;Nom&gt;_front.png</c> (+
    /// <c>&lt;Nom&gt;_back.png</c> optionnel) de <c>Assets/Objects/</c> (p. ex. <c>Paysan_front.png</c>) =
    /// une variante. Ordre stable (tri par nom) pour que le tirage par case soit reproductible. Aucun PNG
    /// → placeholder « ? » dessiné.
    /// </summary>
    private void LoadRecrueSprites()
    {
        _recrueLooks.Clear();
        var dir = AssetPath("Assets/Objects");
        if (!System.IO.Directory.Exists(dir))
            return;
        foreach (var file in System.IO.Directory.GetFiles(dir, "*_front.png")
                     .OrderBy(f => f, System.StringComparer.OrdinalIgnoreCase))
        {
            if (Textures.LoadPngOrNull(Context.GraphicsDevice, file) is not { } front)
                continue;
            // Dos optionnel (même nom, suffixe _back) : utilisé en mission « Protéger les paysans ».
            var back = Textures.LoadPngOrNull(Context.GraphicsDevice, file[..^"_front.png".Length] + "_back.png");
            _recrueLooks.Add((front, back));
        }
    }

    /// <summary>
    /// Look de l'objet recrue pour une case (variante tirée STABLE par case), ou null si aucun PNG. Même règle
    /// d'orientation que les pions (cf. <see cref="DefaultFacesDown"/>) : moitié HAUTE du plateau → face caméra
    /// (<c>_front</c>), moitié BASSE → dos (<c>_back</c>), quelle que soit la mission. Repli sur la face si pas de dos.
    /// </summary>
    private Texture2D? RecrueSpriteFor(Cell cell)
    {
        if (_recrueLooks.Count == 0)
            return null;
        var look = _recrueLooks[VariantIndex("recrue", cell, _recrueLooks.Count)];
        return cell.Row < Rows / 2 ? look.Front : look.Back ?? look.Front;
    }

    /// <summary>
    /// Objets de RECRUTEMENT : pion recrue immobile (un look tiré par case, sinon placeholder). Caché une
    /// fois consommé — la récompense se joue en modale (cf. <see cref="DrawRecrueReveal"/>). Sous les unités.
    /// </summary>
    private void DrawRecrueObjects(SpriteBatch sb, GridLayout layout)
    {
        if (_recrueCells.Count == 0)
            return;
        var size = layout.TileSize;
        var spriteLift = (int)(size * SpriteLiftFraction);   // même remontée que les pions (socle en bas, haut qui déborde)
        foreach (var c in _recrueCells)
        {
            if (_recrueConsumed.Contains(c))
                continue;   // consommé : retiré du plateau
            var (introY, introA) = BoardIntroAnim(c, layout);
            var top = layout.CellToScreen(c.Column, c.Row);
            var zx = (int)top.X;
            var zy = (int)top.Y + introY;
            if (RecrueSpriteFor(c) is { } sprite)
            {
                // Positionné comme un pion classique (cf. DrawUnit) : remonté de spriteLift, centré sur la case.
                sb.Draw(sprite, new Rectangle(zx, zy - spriteLift, size, size), Color.White * introA);
                continue;
            }
            // Placeholder : jeton de pion (corps + dessus éclairé) avec un « ? » jaune centré.
            var box = size * 3 / 4;
            var rect = new Rectangle(zx + (size - box) / 2, zy + (size - box) / 2, box, box);
            DrawRect(sb, rect, Palette.Navy1 * introA);
            DrawRect(sb, new Rectangle(rect.X, rect.Y, rect.Width, rect.Height / 3), Palette.Blue1 * introA);
            DrawRectBorder(sb, rect, Palette.Black1 * introA, 2);
            Context.Font.DrawCentered(sb, "?", rect, System.Math.Max(2, size / 16), Palette.Yellow2 * introA);
        }
    }

    /// <summary>
    /// Buissons (COUVERT) : un pion dessus reçoit -4 dégâts (appliqué dans <see cref="Match"/>). PNG si
    /// présent, sinon une touffe verte. Permanents (jamais consommés). Dessinés en DEUX passes selon
    /// <paramref name="occupied"/> : un buisson SANS pion passe DERRIÈRE les unités, un buisson AVEC un
    /// pion dessus passe DEVANT (le feuillage masque le pion qui s'y cache). L'ombre, elle, reste au sol
    /// (cf. <see cref="DrawCastShadows"/>).
    /// </summary>
    private void DrawBushes(SpriteBatch sb, GridLayout layout, bool occupied)
    {
        if (_bushCells.Count == 0)
            return;
        var size = layout.TileSize;
        foreach (var c in _bushCells)
        {
            if ((_match.UnitAt(c) != null) != occupied)   // garde seulement les buissons de cette passe
                continue;
            var (introY, introA) = BoardIntroAnim(c, layout);
            var top = layout.CellToScreen(c.Column, c.Row);
            var zx = (int)top.X;
            var zy = (int)top.Y + introY;
            if (_bushSprite != null)
            {
                sb.Draw(_bushSprite, new Rectangle(zx, zy, size, size), Color.White * introA);
                continue;
            }
            // Placeholder : touffe verte (base sombre + dessus plus clair) bordée de noir.
            var box = size * 3 / 4;
            var rect = new Rectangle(zx + (size - box) / 2, zy + (size - box) / 2, box, box);
            DrawRect(sb, rect, Palette.Green2 * introA);
            DrawRect(sb, new Rectangle(rect.X, rect.Y, rect.Width, rect.Height * 2 / 5), Palette.Green1 * introA);
            DrawRectBorder(sb, rect, Palette.Black1 * introA, 2);
        }
    }

    /// <summary>
    /// Rectangle 128×128 du coffre animé, dans la zone de jeu (à gauche du panneau d'inventaire), décalé un
    /// peu SOUS le centre pour laisser la place à l'objet + son tooltip au-dessus.
    /// </summary>
    private Rectangle ChestRevealRect()
    {
        var vp = VirtualViewport;
        var availW = vp.Width - RightPanelWidth;
        const int s = 128;
        return new Rectangle(availW / 2 - s / 2, vp.Height / 2 - s / 2 + 40, s, s);
    }

    /// <summary>Rectangle 64×64 de l'objet révélé, juste AU-DESSUS du coffre.</summary>
    private static Rectangle ChestItemRect(Rectangle chestRect)
    {
        const int s = 64;
        return new Rectangle(chestRect.Center.X - s / 2, chestRect.Y - s - 8, s, s);
    }

    /// <summary>
    /// Rectangle de l'objet PENDANT le défilement : il monte du haut du coffre jusqu'à sa place finale
    /// (<see cref="ChestItemRect"/>) selon l'avancement, avec une décélération (ease-out) pour se poser en douceur.
    /// </summary>
    private Rectangle ChestRollItemRect(Rectangle chestRect)
    {
        var target = ChestItemRect(chestRect);
        var startY = chestRect.Y + 12;   // émerge du haut du coffre
        var p = (float)System.Math.Clamp(_chestPhaseTimer / ChestRollDuration, 0, 1);
        var eased = 1f - (1f - p) * (1f - p) * (1f - p);   // ease-out cubique : monte vite puis ralentit
        var y = (int)MathHelper.Lerp(startY, target.Y, eased);
        return new Rectangle(target.X, y, target.Width, target.Height);
    }

    /// <summary>
    /// Révélation MODALE d'un coffre : voile + inventaire d'équipement ouvert à droite + coffre ANIMÉ au
    /// centre. Phase Item : l'objet flotte au-dessus avec sa description, on clique pour qu'il vole vers
    /// l'inventaire. Combat figé pendant toute la séquence (cf. <see cref="UpdateChestReveal"/>).
    /// </summary>
    private void DrawChestReveal(SpriteBatch sb, Viewport viewport)
    {
        if (_chestReveal is not { } item)
            return;
        var availW = viewport.Width - RightPanelWidth;
        var chestRect = ChestRevealRect();

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawDim(sb, viewport);
        DrawPanelBackground(sb);
        DrawEquipmentRevealPanel(sb);   // inventaire d'équipement (on voit où l'objet va atterrir)
        DrawChestFrame(sb, chestRect);

        if (_chestPhase == ChestPhase.Rolling && _chestRollItem is { } rolling)
        {
            // L'objet MONTE (du coffre vers sa place) en défilant vite ; pas de nom/rareté tant qu'il n'est pas figé.
            DrawEquipSpriteAt(sb, rolling, ChestRollItemRect(chestRect));
        }
        else if (_chestPhase == ChestPhase.Item)
        {
            var itemRect = ChestItemRect(chestRect);
            DrawEquipSpriteAt(sb, item, itemRect);                   // l'objet flotte au-dessus du coffre
            DrawEquipTooltipAbove(sb, item, itemRect);              // cadre tooltip (nom + description) au-dessus du sprite
            Context.Font.DrawCentered(sb, RarityLabel(item.Rarity),  // rareté du butin (couleur dédiée) sous le coffre
                new Rectangle(0, chestRect.Bottom + 4, availW, 10), 1, RarityColor(item.Rarity));
            Context.Font.DrawCentered(sb, Loc.T("recrue.continue"),  // invite au clic
                new Rectangle(0, chestRect.Bottom + 18, availW, 12), 1, Palette.Cyan1);
        }
        sb.End();

        if (_chestPhase == ChestPhase.Fly)
            DrawChestFlight(sb, item);

        _sparks.Draw(sb, Context.Pixel);   // feu d'artifice de récompense PAR-DESSUS le voile (cf. QueueLootFireworks)
    }

    /// <summary>
    /// Dissolutions d'équipement en cours : l'icône se désintègre (même shader que les unités) à
    /// l'emplacement du pion mort, après que celui-ci a fini de se dissoudre.
    /// </summary>
    private void DrawEquipDissolves(SpriteBatch sb, GridLayout layout)
    {
        if (_equipDissolves.Count == 0)
            return;
        var size = layout.TileSize;
        var spriteLift = (int)(size * SpriteLiftFraction);
        const int s = 32;
        foreach (var d in _equipDissolves)
        {
            if (d.Delay > 0)
                continue;   // le pion se dissout encore : on attend
            var progress = MathHelper.Clamp((d.Time - EquipDissolveHold) / EquipDissolveDur, 0f, 1f);
            var top = layout.CellToScreen(d.Cell.Column, d.Cell.Row);
            var rect = new Rectangle((int)top.X + (size - s) / 2, (int)top.Y - spriteLift + (size - s) / 2, s, s);
            if (EquipSprite(d.Equip) is { } sprite)
            {
                _combatFx.DrawDissolve(sb, sprite, rect, progress, Palette.Yellow2, d.Seed);
            }
            else
            {
                sb.Begin(samplerState: SamplerState.PointClamp);
                var col = (d.Equip.GrantsAnyTrait ? Palette.Yellow1 : Palette.Cyan1) * (1f - progress);
                DrawRect(sb, rect, col);
                sb.End();
            }
        }
    }

    /// <summary>Coffre rendu depuis le spritesheet d'ouverture (frame selon la phase), repli sur le PNG fermé.</summary>
    private void DrawChestFrame(SpriteBatch sb, Rectangle dest)
    {
        if (_chestAnim != null)
        {
            var frame = _chestPhase == ChestPhase.Opening
                ? System.Math.Clamp((int)(_chestPhaseTimer / ChestOpenDuration * ChestFrames), 0, ChestFrames - 1)
                : ChestFrames - 1;   // ouvert
            sb.Draw(_chestAnim, dest, new Rectangle(frame * 64, 0, 64, 64), Color.White);
        }
        else if (_chestSprite != null)
        {
            sb.Draw(_chestSprite, dest, Color.White);
        }
        else
        {
            DrawRect(sb, dest, Palette.Brown1);
            DrawRectBorder(sb, dest, Palette.Black1, 2);
        }
    }

    /// <summary>Panneau d'inventaire d'ÉQUIPEMENT pendant la révélation (titre + lignes existantes).</summary>
    private void DrawEquipmentRevealPanel(SpriteBatch sb)
    {
        var x = PanelRect().X + PanelPad;
        Context.Font.Draw(sb, Loc.T("equip.title"), new Vector2(x, 34), 2, Palette.Yellow2);
        Context.Font.Draw(sb, Loc.T("equip.inventory"), new Vector2(x, PanelListTop - 22), 1, Palette.Blue1);
        var inv = _run.EquipmentInventory;
        for (var i = 0; i < inv.Count; i++)
            DrawEquipInventoryRow(sb, inv[i], EquipRowRect(i), false);
    }

    /// <summary>Vol de l'objet (32×32, échelle constante) du centre vers son slot d'inventaire, avec accélération.</summary>
    private void DrawChestFlight(SpriteBatch sb, Equipment item)
    {
        var t = MathHelper.Clamp((float)(_chestPhaseTimer / ChestFlyDuration), 0f, 1f);
        var ease = t * t;
        var slot = EquipRowRect(_run.EquipmentInventory.Count);   // slot d'atterrissage (item pas encore ajouté)
        var iconCenter = new Vector2(slot.X + 18, slot.Y + slot.Height / 2f);
        var pos = Vector2.Lerp(_chestFlyFrom, iconCenter, ease);
        const int s = 32;
        var dest = new Rectangle((int)(pos.X - s / 2f), (int)(pos.Y - s / 2f), s, s);
        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawEquipSpriteAt(sb, item, dest);
        sb.End();
    }

    /// <summary>Sprite d'équipement rendu à la taille de <paramref name="dest"/> (multiple entier de 32 = pixel-perfect), ou placeholder.</summary>
    private void DrawEquipSpriteAt(SpriteBatch sb, Equipment equip, Rectangle dest)
    {
        if (EquipSprite(equip) is { } sprite)
        {
            sb.Draw(sprite, dest, Color.White);
            return;
        }
        var col = equip.GrantsAnyTrait ? Palette.Yellow1 : Palette.Cyan1;
        DrawRect(sb, dest, col);
        DrawRectBorder(sb, dest, Palette.Black1, 1);
    }

    /// <summary>Phase Équipement : fond de slot (cible de dépose) au-dessus des pions NON équipés (non-commandant).</summary>
    private void DrawEquipDropSlots(SpriteBatch sb, GridLayout layout)
    {
        foreach (var (cell, spec) in DeployedPlayerSpecs())
        {
            if (spec.Essential || spec.Equipment != null)
                continue;   // les pions équipés montrent déjà leur badge (DrawEquipBadgesPlacement)
            DrawEquipSlotBackground(sb, EquipBadgeRect(cell, layout));
        }
    }

    /// <summary>Fond de slot d'équipement 32×32 (PNG <c>background.png</c>) centré dans <paramref name="rect"/>, ou repli dessiné.</summary>
    private void DrawEquipSlotBackground(SpriteBatch sb, Rectangle rect)
    {
        const int s = 32;
        var box = new Rectangle(rect.Center.X - s / 2, rect.Center.Y - s / 2, s, s);
        if (_equipSlotBg != null)
        {
            sb.Draw(_equipSlotBg, box, Color.White);
            return;
        }
        DrawRect(sb, box, Palette.Black1 * 0.45f);
        DrawRectBorder(sb, box, Palette.Blue1, 2);
    }

    /// <summary>Badge d'équipement (icône 32×32) au-dessus des pions joueur posés — placement/équipement (source = gabarit).</summary>
    private void DrawEquipBadgesPlacement(SpriteBatch sb, GridLayout layout)
    {
        foreach (var (cell, spec) in DeployedPlayerSpecs())
        {
            if (spec.Essential || spec.Equipment is not { } e)
                continue;
            if (_dragEquipFrom == spec)   // en cours de portage : l'icône suit la souris, pas le pion
                continue;
            DrawEquipBadge(sb, layout, cell, e);
        }
    }

    /// <summary>
    /// Icône d'équipement au-dessus des pions ENNEMIS qui en portent un (difficulté ≥ 1). Volontairement
    /// SANS le fond de slot du badge joueur : ce fond signale une cible de dépose, or rien ne se dépose sur
    /// un ennemi. Ici c'est purement informatif — savoir quel pion frappe plus fort avant de l'engager.
    /// </summary>
    private void DrawEnemyEquipBadges(SpriteBatch sb, GridLayout layout)
    {
        foreach (var (cell, unit) in _match.Units())
            if (unit.Faction == Faction.Enemy && unit.Equipment is { } e)
                DrawEquipIcon(sb, e, EquipBadgeRect(cell, layout));
    }

    private void DrawEquipBadge(SpriteBatch sb, GridLayout layout, Cell cell, Equipment equip)
    {
        var r = EquipBadgeRect(cell, layout);
        DrawEquipSlotBackground(sb, r);   // même fond de slot que les emplacements vides
        DrawEquipIcon(sb, equip, r);
    }

    /// <summary>
    /// Icône d'un équipement, TOUJOURS rendue à 32×32 natif (pixel-perfect, jamais redimensionnée), centrée
    /// dans <paramref name="rect"/>. PNG <c>Assets/Equipment/&lt;icon&gt;.png</c>, ou repli placeholder
    /// (aplat coloré par type + initiale).
    /// </summary>
    private void DrawEquipIcon(SpriteBatch sb, Equipment equip, Rectangle rect)
    {
        const int s = 32;
        var box = new Rectangle(rect.Center.X - s / 2, rect.Center.Y - s / 2, s, s);
        if (EquipSprite(equip) is { } sprite)
        {
            sb.Draw(sprite, box, Color.White);
            return;
        }
        var col = equip.GrantsAnyTrait ? Palette.Yellow1 : Palette.Cyan1;
        DrawRect(sb, box, col);
        DrawRectBorder(sb, box, Palette.Black1, 1);
        var initial = string.IsNullOrEmpty(equip.Name) ? "?" : equip.Name[..1].ToUpperInvariant();
        Context.Font.DrawCentered(sb, initial, box, 1, Palette.Black1);
    }

    /// <summary>PNG d'icône 32×32 d'un équipement (cache ; null si absent → placeholder). Disposé par <see cref="Unload"/>.</summary>
    private Texture2D? EquipSprite(Equipment equip)
    {
        if (_equipSprites.TryGetValue(equip.Icon, out var tex))
            return tex;
        tex = Textures.LoadPngOrNull(Context.GraphicsDevice, AssetPath($"Assets/Equipment/{equip.Icon}.png"));
        _equipSprites[equip.Icon] = tex;
        return tex;
    }

    /// <summary>Panneau de droite pendant la sous-phase Équipement : bandeau d'inventaire + boutons Retour/Combat.</summary>
    private void DrawEquipPanel(SpriteBatch sb)
    {
        var panel = PanelRect();
        var x = panel.X + PanelPad;

        Context.Font.Draw(sb, CombatTitle(), new Vector2(x, 16), 1, Palette.Yellow1);
        Context.Font.Draw(sb, Loc.T("equip.title"), new Vector2(x, 34), 2, Palette.Yellow2);
        Context.Font.Draw(sb, Loc.T("equip.inventory"), new Vector2(x, PanelListTop - 22), 1, Palette.Blue1);

        var inv = _run.EquipmentInventory;
        if (inv.Count == 0)
            Context.Font.Draw(sb, Loc.T("equip.empty"), new Vector2(x, PanelListTop), 1, Palette.Blue1);
        for (var i = 0; i < inv.Count; i++)
            DrawEquipInventoryRow(sb, inv[i], EquipRowRect(i), Context.Input.UsingGamepad && i == _equipFocus);

        var hintY = PanelListTop + System.Math.Max(1, inv.Count) * (EquipRowH + EquipRowGap) + 12;
        if (Context.Input.UsingGamepad)
        {
            Context.Font.Draw(sb, Loc.T("equip.hint_gp_equip"), new Vector2(x, hintY), 1, Palette.Blue1);
            Context.Font.Draw(sb, Loc.T("equip.hint_gp_cycle"), new Vector2(x, hintY + 16), 1, Palette.Cyan1);
            if (inv.Count > 0)   // recyclage possible seulement s'il reste un objet en inventaire
                Context.Font.Draw(sb, Loc.T("equip.hint_gp_recycle"), new Vector2(x, hintY + 32), 1, Palette.Yellow2);
        }
        else
        {
            Context.Font.Draw(sb, Loc.T("equip.hint_drag"), new Vector2(x, hintY), 1, Palette.Blue1);
        }

        // Bouton « Retour » (vers le placement) puis « Combat » en bas.
        var back = EquipBackButtonRect();
        var backHover = !Context.Input.UsingGamepad && back.Contains(Context.Input.MousePosition);
        var backDy = Context.Style.DrawButton(sb, back, UiStyle.StateOf(backHover, backHover && Context.Input.IsLeftDown));
        var backArea = back; backArea.Offset(0, backDy);
        Context.Font.DrawCentered(sb, Loc.T("equip.back"), backArea, 1, Palette.White);

        var btn = FightButtonRect();
        var hover = !Context.Input.UsingGamepad && btn.Contains(Context.Input.MousePosition);
        var dy = Context.Style.DrawButton(sb, btn, UiStyle.StateOf(hover, hover && Context.Input.IsLeftDown));
        var area = btn; area.Offset(0, dy);
        Context.Font.DrawCentered(sb, Loc.T("placement.fight"), area, 1, Palette.White);

        // Tooltip descriptif au survol d'un équipement du bandeau (souris, hors glisser).
        if (!Context.Input.UsingGamepad && _dragEquip == null
            && EquipPanelCardAt(Context.Input.MousePosition) is { } hi)
            DrawEquipTooltip(sb, inv[hi], EquipRowRect(hi));

        DrawRerollIcon(sb);   // icône de relance / casse d'équipement, à gauche du panneau
    }

    // Bandeau d'équipement : UNE LIGNE par item (icône à gauche + nom à droite) — les noms d'équipement
    // sont trop longs pour la grille 3 colonnes des portraits d'unité.
    private const int EquipRowH = 40;
    private const int EquipRowGap = 6;

    /// <summary>Ligne pleine largeur de l'item d'inventaire d'équipement numéro <paramref name="i"/>.</summary>
    private Rectangle EquipRowRect(int i)
    {
        var panel = PanelRect();
        return new Rectangle(panel.X + PanelPad, PanelListTop + i * (EquipRowH + EquipRowGap),
            panel.Width - 2 * PanelPad, EquipRowH);
    }

    private void DrawEquipInventoryRow(SpriteBatch sb, Equipment equip, Rectangle row, bool focus)
    {
        // Icône 32×32 native à gauche (sans cadre, comme les portraits d'unité), nom à droite, centrés.
        var iconBox = new Rectangle(row.X, row.Y + (row.Height - 32) / 2, 32, 32);
        DrawEquipIcon(sb, equip, iconBox);
        if (focus)
            DrawRectBorder(sb, row, Palette.Yellow2, 2);
        Context.Font.Draw(sb, EquipName(equip).ToUpperInvariant(),
            new Vector2(iconBox.Right + 10, row.Y + (row.Height - 7) / 2), 1, Palette.White);
    }

    /// <summary>Équipement porté à la souris pendant le glisser (suit le curseur).</summary>
    private void DrawDraggedEquip(SpriteBatch sb)
    {
        if (_dragEquip is not { } equip || Context.Input.UsingGamepad)
            return;
        var m = Context.Input.MousePosition;
        DrawEquipIcon(sb, equip, new Rectangle(m.X - 18, m.Y - 18, 36, 36));
    }

    /// <summary>Nom affiché d'un équipement (localisable par id : « equip.&lt;id&gt; », repli sur le nom du catalogue).</summary>
    /// <summary>
    /// Nom LOCALISÉ d'un équipement : clé exacte <c>equip.&lt;id&gt;</c> si présente, sinon clé de BASE
    /// (rareté ôtée, ex. <c>casqueRare</c> → <c>equip.casque</c>) pour que les variantes de rareté partagent
    /// la même traduction, sinon le nom brut du json (repli). Voir strings.csv (<c>equip.*</c>).
    /// </summary>
    private static string EquipName(Equipment equip) =>
        Loc.TOr("equip." + equip.Id, null!) ?? Loc.TOr("equip." + EquipBaseId(equip.Id), equip.Name);

    /// <summary>Id d'équipement sans son suffixe de rareté (« Rare » / « Legendaire ») : clé de nom partagée.</summary>
    private static string EquipBaseId(string id)
    {
        if (id.EndsWith("Legendaire", System.StringComparison.Ordinal)) return id[..^"Legendaire".Length];
        if (id.EndsWith("Rare", System.StringComparison.Ordinal)) return id[..^"Rare".Length];
        return id;
    }

    /// <summary>Couleur associée à une rareté (commun = blanc, rare = bleu, légendaire = or).</summary>
    private static Color RarityColor(EquipmentRarity rarity) => rarity switch
    {
        EquipmentRarity.Legendary => Palette.Yellow2,
        EquipmentRarity.Rare => Palette.Cyan1,
        _ => Palette.White,
    };

    /// <summary>Libellé localisé d'une rareté (« COMMUN » / « RARE » / « LÉGENDAIRE »).</summary>
    private static string RarityLabel(EquipmentRarity rarity) => Loc.T(rarity switch
    {
        EquipmentRarity.Legendary => "equip.rarity.legendary",
        EquipmentRarity.Rare => "equip.rarity.rare",
        _ => "equip.rarity.common",
    });

    /// <summary>Texte d'un effet de STAT : « +N &lt;stat&gt; ».</summary>
    private static string StatEffectText(EquipEffect effect)
    {
        var label = effect.Stat switch
        {
            EquipStat.Hp => Loc.T("stat.hp"),
            EquipStat.Damage => Loc.T("stat.power"),
            EquipStat.MoveRange => Loc.T("stat.movement"),
            EquipStat.AttackRange => Loc.T("stat.range"),
            _ => "",
        };
        return Loc.T("equip.stat_bonus", effect.Amount, label);
    }

    /// <summary>
    /// Met un texte (souvent ALL-CAPS venu de strings.csv) en « casse de phrase » : tout en minuscules, puis
    /// une MAJUSCULE en tête de texte et après chaque ponctuation forte (<c>.</c> <c>!</c> <c>?</c>). Rendu
    /// via <c>Draw(preserveCase: true)</c>. Chiffres/apostrophes/espaces ne coupent pas la phrase.
    /// </summary>
    private static string SentenceCase(string text)
    {
        var chars = text.ToLowerInvariant().ToCharArray();
        var startOfSentence = true;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (char.IsLetter(c))
            {
                if (startOfSentence) { chars[i] = char.ToUpperInvariant(c); startOfSentence = false; }
            }
            else if (c is '.' or '!' or '?')
                startOfSentence = true;   // la prochaine lettre ouvrira une nouvelle phrase
        }
        return new string(chars);
    }

    /// <summary>
    /// Lignes de la description d'un équipement (texte + couleur), dans l'ordre des effets. Effet de STAT =
    /// une ligne « +N stat » crème (repliée si besoin). Effet de TRAIT = son NOM en BLEU puis, À LA LIGNE, sa
    /// description crème (repliée). Sert à la fois au calcul de hauteur et au rendu (pour rester synchronisés).
    /// </summary>
    private List<(string Text, Color Color)> EquipEffectLines(Equipment equip, int innerWidth)
    {
        var lines = new List<(string, Color)>();
        foreach (var e in equip.Effects)
        {
            if (e.Trait is { } t)
            {
                var kw = UnitKeywords.For(t);
                var (desc, _, reinforced) = KeywordDisplay(kw);
                lines.Add((kw.Label, reinforced ? ReinforcedTraitColor : Palette.Cyan1));   // nom du trait (rouge si renforcé)
                foreach (var line in WrapText(SentenceCase(desc), innerWidth, 1))
                    lines.Add((line, Palette.White));               // description : valeur effective substituée (jamais « {0} »)
            }
            else
            {
                foreach (var line in WrapText(SentenceCase(StatEffectText(e)), innerWidth, 1))
                    lines.Add((line, Palette.White));               // bonus de stat en casse de phrase
            }
        }
        return lines;
    }

    /// <summary>
    /// Restriction d'emploi à afficher SOUS l'effet (ligne séparée, en rouge doux), ou null si aucune.
    /// Reflète <see cref="Run.CanEquip"/> : « Attaque libre » interdit au domaine Dame ; portée interdite aux
    /// cavaliers de mêlée (sauf archer monté), mouvement interdit à TOUS les cavaliers.
    /// </summary>
    private static string? EquipRestriction(Equipment equip)
    {
        if (equip.GrantsTrait(Trait.AttaqueLibre))
            return Loc.T("equip.no_dame");            // tir « comme une Dame » : sans objet sur une Dame
        if (equip.BonusFor(EquipStat.AttackRange) > 0)
            return Loc.T("equip.no_cavalier");        // … sauf l'archer monté
        if (equip.BonusFor(EquipStat.MoveRange) > 0)
            return Loc.T("equip.no_cavalier_all");    // tous les cavaliers, sans exception
        return null;
    }

    /// <summary>Tooltip au survol d'un badge d'équipement AU-DESSUS de la tête d'un pion (phase Équipement, souris, hors glisser).</summary>
    private void DrawEquipBadgeTooltip(SpriteBatch sb, GridLayout layout)
    {
        if (Context.Input.UsingGamepad || _dragEquip != null)
            return;
        var mouse = Context.Input.MousePosition;
        foreach (var (cell, spec) in DeployedPlayerSpecs())
        {
            if (spec.Essential || spec.Equipment is not { } e)
                continue;
            var r = EquipBadgeRect(cell, layout);
            if (r.Contains(mouse))
            {
                DrawEquipTooltip(sb, e, r);
                return;
            }
        }
    }

    private const int EquipTooltipWidth = 210;

    /// <summary>Tooltip d'un équipement (nom + description), ancré à GAUCHE de l'élément survolé (repli à droite).</summary>
    private void DrawEquipTooltip(SpriteBatch sb, Equipment equip, Rectangle row)
    {
        var x = row.X - EquipTooltipWidth - 8;           // à gauche du bandeau (vers le plateau)
        if (x < 8) x = System.Math.Min(row.Right + 8, VirtualViewport.Width - EquipTooltipWidth - 8);
        DrawEquipTooltipPanel(sb, equip, x, row.Y);
    }

    /// <summary>Tooltip d'un équipement centré AU-DESSUS de <paramref name="anchor"/> (repli en dessous si pas de place).</summary>
    private void DrawEquipTooltipAbove(SpriteBatch sb, Equipment equip, Rectangle anchor)
    {
        var x = System.Math.Clamp(anchor.Center.X - EquipTooltipWidth / 2, 8, VirtualViewport.Width - EquipTooltipWidth - 8);
        var y = anchor.Y - EquipTooltipHeight(equip) - 8;
        if (y < 8) y = anchor.Bottom + 8;
        DrawEquipTooltipPanel(sb, equip, x, y);
    }

    // Géométrie partagée entre le calcul de hauteur et le rendu (pour rester synchronisés).
    private const int EquipTooltipPad = 8;     // marge intérieure
    private const int EquipTooltipLineH = 9;   // interligne du texte (scale 1)
    private const int EquipTooltipTitleH = 11; // hauteur réservée au nom
    private const int EquipTooltipGap = 6;     // espace AVANT la ligne de restriction

    /// <summary>Hauteur du cadre tooltip : nom + effet replié, plus l'éventuelle restriction (espacée), largeur fixe.</summary>
    private int EquipTooltipHeight(Equipment equip)
    {
        int inner = EquipTooltipWidth - 2 * EquipTooltipPad;
        int h = EquipTooltipPad + EquipTooltipTitleH
              + EquipEffectLines(equip, inner).Count * EquipTooltipLineH;
        if (EquipRestriction(equip) is { } r)
            h += EquipTooltipGap + WrapText(r, inner, 1).Count * EquipTooltipLineH;
        return h + EquipTooltipPad;
    }

    /// <summary>Dessine le cadre tooltip (nom jaune, effet crème, puis restriction en rouge doux) à un coin haut-gauche donné.</summary>
    private void DrawEquipTooltipPanel(SpriteBatch sb, Equipment equip, int x, int y)
    {
        int pad = EquipTooltipPad, lineH = EquipTooltipLineH, inner = EquipTooltipWidth - 2 * pad;
        var box = new Rectangle(x, y, EquipTooltipWidth, EquipTooltipHeight(equip));
        Context.Style.DrawPanel(sb, box);
        Context.Font.Draw(sb, EquipName(equip).ToUpperInvariant(), new Vector2(box.X + pad, box.Y + pad), 1, Palette.Yellow2);

        var ly = box.Y + pad + EquipTooltipTitleH;
        foreach (var (text, color) in EquipEffectLines(equip, inner))
        {
            Context.Font.Draw(sb, text, new Vector2(box.X + pad, ly), 1, color, preserveCase: true);
            ly += lineH;
        }
        if (EquipRestriction(equip) is { } r)
        {
            ly += EquipTooltipGap;   // respire entre l'effet et la mise en garde
            foreach (var line in WrapText(r, inner, 1))
            {
                Context.Font.Draw(sb, line, new Vector2(box.X + pad, ly), 1, Palette.Purple5);
                ly += lineH;
            }
        }
    }

    // ── Cartes de combat (remplacent l'ancien panneau de droite) ──────────────────
    // Réutilisent le gabarit des cartes de recrutement ; le contenu sera retravaillé plus tard.
    // Largeur calée sur la colonne d'améliorations d'arbre : marge gauche = CardPad + 2 cadres de 34
    // + leur écart, puis 10 px avant le sprite 64 centré. L'icône d'équipement, à droite du sprite,
    // vient alors buter exactement sur la marge droite.
    private const int CombatCardW = 248;
    private const int CombatCardH = 330;
    private const int CombatCardGap = 24;

    // Carte CONDENSÉE de combat : icônes de stats (valeurs effectives, sans « +N »), PV en texte, traits
    // (noms seuls) + kills. Bien plus petite que la carte détaillée. cf. DrawCondensedCardLayout.
    // La HAUTEUR est DYNAMIQUE (cf. CondensedCardHeight) : bloc haut fixe + bloc bas variable selon le nombre
    // de lignes de traits, pour que « TUÉS » ne repasse jamais derrière les traits.
    private const int CondensedCardW = 152;
    private const int CondensedPad = 10;
    // Bloc HAUT depuis rect.Y : 15 (marge tier/domaine) + 18 (nom) + 20 (PV) + 48 (icône 32 + 2 + valeur 14).
    private const int CondensedTopBlockH = 101;
    private const int CondensedBottomGap = 6;   // respiration entre les stats et le bloc traits/kills du bas

    // Combat : les cartes-tooltips sont CONDENSÉES par défaut ; clic droit (souris) / X (manette) montre le
    // détaillé pour le pion COURANT. Remis à false dès qu'on arrive sur un autre pion (cf. UpdateTooltipHover) :
    // on revient donc toujours au condensé en survolant un nouveau pion. Sans effet hors combat.
    private bool _detailedTooltip;

    /// <summary>
    /// Cartes flottantes du combat : l'unité SÉLECTIONNÉE s'affiche à droite du plateau, l'ennemi
    /// SURVOLÉ à gauche. Les deux peuvent coexister (sélection + survol d'un ennemi).
    ///
    /// Les popups de mots-clés ne sont dessinés qu'au SURVOL, jamais pour le pion « en main » (même
    /// curseur posé sur sa case) : passé ~2 traits la pile bascule À CÔTÉ de la carte (cf.
    /// <see cref="DrawKeywordPopupsBelow"/>) et mangeait le plateau pendant toute la visée. Le pion
    /// tenu garde sa carte (stats + PV) et récupère ses popups dès qu'il est désélectionné.
    /// </summary>
    // Cartes flottantes + leurs popups de mots-clés, mis de côté par DrawUnitCard/DrawPreviewCard et dessinés
    // EN DERNIER (par-dessus frise, briefing, panneau) : la carte-tooltip qu'on lit doit rester au premier plan.
    private readonly List<Action> _deferredCards = new();
    private readonly List<(UnitClass Class, Rectangle Card, Equipment? Equip, CommandBuffs? Buffs, IReadOnlyList<string>? Granted, Faction Faction)>
        _deferredKeywordPopups = new();

    // Cartes de SURVOL (tooltip d'un pion pointé, non sélectionné) : mêmes différés, mais FONDUES en entrée
    // (cf. UpdateTooltipHover / TooltipHoverAlpha). Rendues à part dans _hoverCardTarget puis recomposées avec
    // un alpha, pour que carte + popups de mots-clés fondent D'UN SEUL BLOC.
    private readonly List<Action> _deferredHoverCards = new();
    private readonly List<(UnitClass Class, Rectangle Card, Equipment? Equip, CommandBuffs? Buffs, IReadOnlyList<string>? Granted, Faction Faction)>
        _deferredHoverKeywordPopups = new();
    private Microsoft.Xna.Framework.Graphics.RenderTarget2D? _hoverCardTarget;

    // Durée du fondu d'entrée d'une carte-tooltip de pion (secondes) — pas de délai : elle démarre au survol.
    private const float TooltipHoverFadeSec = 0.5f;
    private Cell? _tooltipHoverCell;
    private float _tooltipHoverSec;

    /// <summary>
    /// Dessine EN DERNIER (par-dessus tout le HUD de phase — frise, briefing, panneau) les cartes flottantes
    /// PUIS leurs popups de mots-clés, empilés pendant la passe par <see cref="DrawUnitCard"/> /
    /// <see cref="DrawPreviewCard"/>. Sans ce report, l'UI dessinée après les cartes les recouvrait. Vidé à chaque frame.
    /// </summary>
    private void DrawDeferredCards(SpriteBatch sb)
    {
        if (_deferredCards.Count > 0)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            foreach (var draw in _deferredCards)
                draw();
            sb.End();
            _deferredCards.Clear();
        }

        if (_deferredKeywordPopups.Count > 0)
        {
            sb.Begin(samplerState: SamplerState.PointClamp);
            foreach (var (c, card, equip, buffs, granted, faction) in _deferredKeywordPopups)
                DrawKeywordPopupsBelow(sb, c, card, equip, buffs, granted, faction);
            sb.End();
            _deferredKeywordPopups.Clear();
        }

        DrawDeferredHoverCards(sb);
    }

    /// <summary>
    /// Cartes-tooltips de SURVOL : fondent en entrée sur <see cref="TooltipHoverFadeSec"/> dès le début du survol
    /// (cf. <see cref="UpdateTooltipHover"/>). Carte
    /// et popups sont d'abord rendues à part dans <see cref="_hoverCardTarget"/> (taille du viewport, comme la
    /// couche où atterrissent les cartes), puis recomposées d'un seul bloc avec un alpha — ainsi le fondu
    /// s'applique à l'ENSEMBLE et non pion par pion. Le render target courant (canvas normal ou couche UI du
    /// dézoom) est sauvegardé puis restauré.
    /// </summary>
    private void DrawDeferredHoverCards(SpriteBatch sb)
    {
        var hasContent = _deferredHoverCards.Count > 0 || _deferredHoverKeywordPopups.Count > 0;
        var alpha = TooltipHoverAlpha();
        if (alpha >= 1f && hasContent)
        {
            // Pleinement visible (état stable) : dessin DIRECT, sans round-trip render target — donc rendu
            // strictement identique aux autres cartes une fois le fondu terminé.
            DrawHoverCardContent(sb);
        }
        else if (alpha > 0f && hasContent)
        {
            // En cours de fondu : carte + popups rendues à part dans _hoverCardTarget (taille du viewport,
            // comme la couche où atterrissent les cartes), puis recomposées d'un seul bloc avec l'alpha.
            var device = Context.GraphicsDevice;
            var prev = device.GetRenderTargets();
            var vp = VirtualViewport;
            EnsureTarget(device, ref _hoverCardTarget, vp.Width, vp.Height);
            device.SetRenderTarget(_hoverCardTarget);
            device.Clear(Microsoft.Xna.Framework.Color.Transparent);
            DrawHoverCardContent(sb);
            if (prev.Length > 0) device.SetRenderTargets(prev); else device.SetRenderTarget(null);
            sb.Begin(samplerState: SamplerState.PointClamp, blendState: BlendState.AlphaBlend);
            sb.Draw(_hoverCardTarget, Vector2.Zero, Microsoft.Xna.Framework.Color.White * alpha);
            sb.End();
        }
        _deferredHoverCards.Clear();
        _deferredHoverKeywordPopups.Clear();
    }

    /// <summary>Dessine les cartes-tooltips de survol différées PUIS leurs popups de mots-clés (deux batchs).</summary>
    private void DrawHoverCardContent(SpriteBatch sb)
    {
        sb.Begin(samplerState: SamplerState.PointClamp);
        foreach (var draw in _deferredHoverCards)
            draw();
        sb.End();
        sb.Begin(samplerState: SamplerState.PointClamp);
        foreach (var (c, card, equip, buffs, granted, faction) in _deferredHoverKeywordPopups)
            DrawKeywordPopupsBelow(sb, c, card, equip, buffs, granted, faction);
        sb.End();
    }

    /// <summary>Opacité de la carte-tooltip de survol : rampe 0→1 sur la durée du fondu, dès le début du survol.</summary>
    private float TooltipHoverAlpha() => System.Math.Min(1f, _tooltipHoverSec / TooltipHoverFadeSec);

    /// <summary>
    /// Suit la durée de survol CONTINU d'un pion : souris (ou curseur manette) posée sur la MÊME case portant
    /// une unité, hors glisser de combat (les cartes sont alors masquées). Changer de case ou quitter un pion
    /// remet le compteur à zéro (fondu d'entrée, cf. TooltipHoverAlpha) ET repasse le tooltip en CONDENSÉ :
    /// le détaillé (clic droit / X) ne vaut que pour le pion courant, on revient au condensé en arrivant sur
    /// un autre pion.
    /// </summary>
    private void UpdateTooltipHover(float dt)
    {
        var hovered = Context.Input.UsingGamepad ? (Cell?)_cursor : CellUnderMouse();
        var cell = hovered is { } h && _combatDragFrom is null && _match.UnitAt(h) is not null ? hovered : null;
        if (cell != _tooltipHoverCell)
        {
            _tooltipHoverCell = cell;
            _tooltipHoverSec = 0f;
            _detailedTooltip = false;   // nouveau pion survolé → retour au condensé par défaut
        }
        else if (cell is not null)
            _tooltipHoverSec += dt;
    }

    private void DrawCombatCards(SpriteBatch sb, GridLayout layout)
    {
        // Pendant qu'on PORTE un pion (glisser de combat), les cartes-tooltips — amies comme ennemies —
        // masqueraient le plateau juste au moment où on vise : on les efface pour garder la vision. La
        // lecture des dégâts passe alors par la barre de vie de la cible visée (aperçu, cf. DrawUnitHpBars).
        if (_combatDragFrom is not null)
            return;

        var board = BoardRect(layout);
        // En manette, la « case survolée » est celle du curseur ; sinon celle sous la souris.
        var hovered = Context.Input.UsingGamepad ? _cursor : CellUnderMouse();

        // Carte de NOTRE pion (à droite) : l'unité sélectionnée tant qu'elle l'est ; sinon le pion
        // joueur survolé.
        var ownCell = _selected;
        if (ownCell is null && hovered is { } h && _match.UnitAt(h) is { Faction: Faction.Player })
            ownCell = h;

        // Carte de l'ennemi SURVOLÉ (à gauche).
        Cell? enemyCell = hovered is { } he && _match.UnitAt(he) is { Faction: Faction.Enemy } ? he : null;

        // Combat : cartes CONDENSÉES par défaut (icônes de stats + PV + traits/kills) ; clic droit / X bascule
        // vers le détaillé (cf. _detailedTooltip). Hors combat (placement/équipement), toujours détaillé.
        var condensed = _run.Phase == RunPhase.Battle && !_detailedTooltip;

        // Chaque carte se cale JUXTE le pion qu'elle décrit (à sa droite, cf. CardRectNearCell). En condensé,
        // la HAUTEUR dépend de l'unité (nombre de traits, cf. CondensedCardHeight).
        // La carte SÉLECTIONNÉE apparaît d'un coup ; celle d'un pion seulement SURVOLÉ fond en entrée.
        Rectangle? ownCard = null;
        if (ownCell is { } oc && _match.UnitAt(oc) is { } own)
        {
            ownCard = CardRectNearCell(oc, layout,
                condensed ? CondensedCardW : CombatCardW,
                condensed ? CondensedCardHeight(own, oc) : CombatCardH);
            var ownHover = oc != _selected;
            DrawUnitCard(sb, own, ownCard.Value, showKeywords: ownHover, cell: oc, hover: ownHover, condensed: condensed);
        }

        // Si NOTRE pion sélectionné le vise (case à portée d'attaque), on prévisualise les dégâts :
        // les PV menacés clignotent sur sa barre. Carte ennemie = toujours du survol (fondue en entrée).
        if (enemyCell is { } ec && _match.UnitAt(ec) is { } enemy)
        {
            var preview = _selected is { } sel && _attackTargets.Contains(ec) ? _match.PreviewDamage(sel, ec) : 0;
            var enemyRect = CardRectNearCell(ec, layout,
                condensed ? CondensedCardW : CombatCardW,
                condensed ? CondensedCardHeight(enemy, ec) : CombatCardH);
            DrawUnitCard(sb, enemy, enemyRect, preview, cell: ec, hover: true, condensed: condensed);
        }

        // Tooltip d'environnement (buisson) de la case survolée.
        DrawEnvironmentTooltip(sb, layout, hovered, ownCell, ownCard);
    }

    // ── Tooltip d'environnement (objets de plateau : buisson) ─────────────────────
    private const int EnvTooltipWidth = 170;

    /// <summary>Infos d'environnement d'une case (nom + effet), ou null si rien de notable dessus. Buisson
    /// (permanent), coffre et recrue tant qu'ils ne sont pas consommés. En mission « Protéger », la recrue
    /// est un paysan à défendre (pas à recruter).</summary>
    private (string Name, string Desc)? CellEnvironment(Cell cell)
    {
        if (_bushCells.Contains(cell))
            return (Loc.T("env.bush.name"), Loc.T("env.bush.desc"));
        if (_chestCells.Contains(cell) && !_chestConsumed.Contains(cell))
            return (Loc.T("env.chest.name"), Loc.T("env.chest.desc"));
        if (_recrueCells.Contains(cell) && !_recrueConsumed.Contains(cell))
            return IsProtectMission
                ? (Loc.T("env.paysan.name"), Loc.T("env.paysan.desc"))
                : (Loc.T("env.recrue.name"), Loc.T("env.recrue.desc"));
        // Tuile infranchissable qui coupe AUSSI la ligne de tir (pierre, montagne, mur plein) : obstacle.
        // L'eau (passage bloqué mais tir possible) est volontairement exclue.
        if (_battlefield.Contains(cell) && _battlefield[cell] is { BlocksMovement: true, BlocksLineOfFire: true })
            return (Loc.T("env.obstacle.name"), Loc.T("env.obstacle.desc"));
        return null;
    }

    /// <summary>
    /// Tooltip d'environnement au survol d'une case « notable » (buisson). S'il y a un pion DESSUS dont
    /// la carte est affichée, le tooltip se place AU-DESSUS de cette carte (l'objet est caché par le
    /// pion) — de quelque côté qu'elle soit ; sinon il flotte juste au-dessus de la case.
    /// </summary>
    private void DrawEnvironmentTooltip(SpriteBatch sb, GridLayout layout, Cell? hovered, Cell? ownCardCell, Rectangle? ownCard)
    {
        if (hovered is not { } cell || CellEnvironment(cell) is not { } env)
            return;

        int h = EnvTooltipHeight(env.Desc);
        if (ownCard is { } card && ownCardCell == cell)
        {
            // Un pion se tient sur la case (sa carte est affichée) : tooltip au-dessus de la carte.
            int x = card.X + (CombatCardW - EnvTooltipWidth) / 2;
            int y = System.Math.Max(8, card.Y - h - 8);
            DrawEnvTooltipPanel(sb, env.Name, env.Desc, x, y);
        }
        else
        {
            // Case nue (objet visible) : tooltip flottant juste au-dessus de la case (repli en dessous).
            var vp = VirtualViewport;
            var top = layout.CellToScreen(cell.Column, cell.Row);
            int cx = (int)top.X + layout.TileSize / 2;
            int x = System.Math.Clamp(cx - EnvTooltipWidth / 2, 8, vp.Width - EnvTooltipWidth - 8);
            int y = (int)top.Y - h - 6;
            if (y < 8) y = (int)top.Y + layout.TileSize + 6;
            DrawEnvTooltipPanel(sb, env.Name, env.Desc, x, y);
        }
    }

    /// <summary>Hauteur du cadre tooltip d'environnement (nom + description repliée), largeur fixe.</summary>
    private int EnvTooltipHeight(string desc) =>
        EquipTooltipPad + EquipTooltipTitleH
        + WrapText(desc, EnvTooltipWidth - 2 * EquipTooltipPad, 1).Count * EquipTooltipLineH
        + EquipTooltipPad;

    /// <summary>Dessine le cadre tooltip d'environnement (nom jaune + description crème) à un coin haut-gauche.</summary>
    private void DrawEnvTooltipPanel(SpriteBatch sb, string name, string desc, int x, int y, bool sentenceCase = false)
    {
        int pad = EquipTooltipPad, lineH = EquipTooltipLineH, inner = EnvTooltipWidth - 2 * pad;
        var box = new Rectangle(x, y, EnvTooltipWidth, EnvTooltipHeight(desc));
        Context.Style.DrawPanel(sb, box);
        Context.Font.Draw(sb, name.ToUpperInvariant(), new Vector2(box.X + pad, box.Y + pad), 1, Palette.Yellow2);
        var ly = box.Y + pad + EquipTooltipTitleH;
        // sentenceCase : description en « casse de phrase » (1re lettre en capitale, reste en minuscules),
        // comme les tooltips de mots-clés/équipement. La casse ne change ni le nombre de caractères ni le
        // découpage, donc EnvTooltipHeight (calculé sur desc brut) reste exact.
        foreach (var line in WrapText(sentenceCase ? SentenceCase(desc) : desc, inner, 1))
        {
            Context.Font.Draw(sb, line, new Vector2(box.X + pad, ly), 1, Palette.White, preserveCase: sentenceCase);
            ly += lineH;
        }
    }

    /// <summary>
    /// Emplacement de la carte à DROITE du plateau (unité sélectionnée), borné à l'écran. Pendant le
    /// placement (phase Équipement), le panneau de droite est visible : la carte reste à sa gauche.
    /// </summary>
    private Rectangle RightCardRect(Rectangle board)
    {
        var vp = VirtualViewport;
        var rightLimit = _run.Phase == RunPhase.Placement ? vp.Width - RightPanelWidth : vp.Width;
        var x = Math.Min(board.Right + CombatCardGap, rightLimit - CombatCardGap - CombatCardW);
        return new Rectangle(x, (vp.Height - CombatCardH) / 2, CombatCardW, CombatCardH);
    }

    /// <summary>
    /// Emplacement de la carte JUXTE le pion décrit : à sa DROITE (à sa hauteur) par défaut, rabattue à sa
    /// GAUCHE si elle déborderait le bord droit — puis bornée à l'écran. Elle SUIT le pion au lieu de se
    /// coller au bord du plateau (lisible même dézoomé, petit plateau centré). En placement, la marge droite
    /// exclut le panneau réserve.
    /// </summary>
    private Rectangle CardRectNearCell(Cell cell, GridLayout layout, int cardW, int cardH)
    {
        var vp = VirtualViewport;
        int rightLimit = _run.Phase == RunPhase.Placement ? vp.Width - RightPanelWidth : vp.Width;
        var pos = layout.CellToScreen(cell.Column, cell.Row);
        int tile = layout.TileSize;

        int x = (int)pos.X + tile + CombatCardGap;                 // à droite du pion
        if (x + cardW > rightLimit - CombatCardGap)
            x = (int)pos.X - CombatCardGap - cardW;                // pas la place → à gauche du pion
        x = Math.Clamp(x, CombatCardGap, Math.Max(CombatCardGap, rightLimit - CombatCardGap - cardW));

        int y = (int)pos.Y + tile / 2 - cardH / 2;                 // centrée verticalement sur la case
        y = Math.Clamp(y, CombatCardGap, Math.Max(CombatCardGap, vp.Height - CombatCardGap - cardH));
        return new Rectangle(x, y, cardW, cardH);
    }

    /// <summary>
    /// Hauteur TOTALE de la carte condensée d'une unité : bloc haut fixe (<see cref="CondensedTopBlockH"/>) +
    /// éventuel bloc bas (lignes de traits repliées + « TUÉS »). Ainsi la carte grandit avec le nombre de
    /// traits et le « TUÉS » ne repasse jamais derrière eux.
    /// </summary>
    private int CondensedCardHeight(Unit unit, Cell? cell)
    {
        var b = unit.Buffs ?? CommandBuffs.None;
        var keywords = KeywordsFor(unit.Class, unit.Equipment, b, GrantedTraitsFor(unit, cell));
        var lines = KeywordLineCount(keywords, CondensedCardW);
        var bottom = lines * 9 + (unit.Kills > 0 ? 9 : 0);
        return CondensedTopBlockH + (bottom > 0 ? CondensedBottomGap + bottom : 0) + CondensedPad;
    }

    /// <summary>Nombre de lignes qu'occuperait la liste de mots-clés dans une carte de largeur donnée — MÊME
    /// repli que <see cref="DrawKeywordList"/> (au mot-clé près, séparateur « | », police à chasse fixe).</summary>
    private int KeywordLineCount(List<UnitKeywords.Keyword> keywords, int cardWidth)
    {
        if (keywords.Count == 0)
            return 0;
        var font = Context.Font;
        var maxW = cardWidth - 2 * CardPad;
        var sepW = font.Measure(" | ", 1);
        int lines = 1, onLine = 0, lineW = 0;
        foreach (var kw in keywords)
        {
            var w = font.Measure(kw.Label, 1);
            if (onLine > 0 && lineW + sepW + w > maxW) { lines++; onLine = 0; lineW = 0; }
            if (onLine > 0) lineW += sepW;
            lineW += w;
            onLine++;
        }
        return lines;
    }

    /// <summary>
    /// Carte d'une unité du plateau, dans son ÉTAT COURANT (PV actuels). Les popups de mots-clés
    /// descendent SOUS la carte (à droite de l'écran ils seraient coupés par le bord) et ne sont
    /// dessinés que si <paramref name="showKeywords"/> (cf. <see cref="DrawCombatCards"/>).
    /// </summary>
    private void DrawUnitCard(SpriteBatch sb, Unit unit, Rectangle rect, int hpPreviewDamage = 0,
        bool showKeywords = true, Cell? cell = null, bool hover = false, bool condensed = false)
    {
        var c = unit.Class;
        var granted = GrantedTraitsFor(unit, cell);
        // Auras de puissance ET Formation agissent sur la PUISSANCE elle-même : afficher le mot-clé sans
        // faire bouger le chiffre laisserait la carte en contradiction avec les dégâts réellement infligés.
        var contextualDmg = cell is { } pc ? _match.ContextualPowerBonus(pc) : 0;
        // « Rage » ACTIVE : bonus transitoire (gagné à la mort d'un allié) — même logique que Match.EffectivePower,
        // sinon la carte sous-estime la puissance réelle (bonus invisible malgré le halo rouge).
        if (unit.HasTrait(Trait.Rage))
            contextualDmg += unit.RagePower;
        // Carte + popups DIFFÉRÉS : dessinés en dernier (cf. DrawDeferredCards) pour passer PAR-DESSUS tout le
        // HUD (frise, briefing, panneau), sinon l'UI dessinée après recouvrait la carte-tooltip qu'on lit.
        // hover : carte d'un pion SURVOLÉ (non sélectionné) → file fondue en entrée, à part de la sélection.
        var faction = unit.Faction; var domaine = unit.Domaine; var hp = unit.Hp; var maxHp = unit.MaxHp;
        var equip = unit.Equipment; var buffs = unit.Buffs; var treeNodes = TreeNodesFor(unit); var kills = unit.Kills;
        if (condensed)
        {
            // Version condensée (combat) : traits en NOMS inline, donc AUCUN popup de mots-clés à différer.
            (hover ? _deferredHoverCards : _deferredCards).Add(() => DrawCondensedCardLayout(Context.SpriteBatch, rect, c,
                domaine, hp, maxHp, equip, buffs, kills, granted, contextualDmg, hpPreviewDamage));
            return;
        }
        (hover ? _deferredHoverCards : _deferredCards).Add(() => DrawCardLayout(Context.SpriteBatch, rect, c, faction, domaine, hp, maxHp, equip: equip,
            hpPreviewDamage: hpPreviewDamage, buffs: buffs, treeNodes: treeNodes, kills: kills,
            granted: granted, contextualDmgBonus: contextualDmg));
        if (showKeywords)
            (hover ? _deferredHoverKeywordPopups : _deferredKeywordPopups).Add((c, rect, equip, buffs, granted, faction));
    }

    /// <summary>Auras dont l'effet se lit sur le BÉNÉFICIAIRE : le pion adjacent en profite sans porter le trait.</summary>
    private static readonly (string Aura, string Shown)[] GrantedAuras =
    {
        (Trait.AuraDeRempart, Trait.Rempart),                  // l'aura confère l'effet « Rempart »
        (Trait.AuraDePuissance, Trait.AuraDePuissance),        // pas de trait dédié : on montre l'aura elle-même
    };

    /// <summary>
    /// Traits que le pion tient de son PLACEMENT et non de sa fiche : ceux offerts par une aura alliée adjacente.
    /// Comme ils ne figurent ni sur la classe, ni sur l'équipement, ni sur l'arbre, la carte ne les montrerait
    /// jamais — d'où ce recalcul à chaque frame depuis le plateau. Rien n'est ajouté si le pion porte déjà le
    /// trait par lui-même (il ne s'agirait plus d'un apport de l'aura).
    /// </summary>
    private IReadOnlyList<string>? GrantedTraitsFor(Unit unit, Cell? cell)
    {
        if (cell is not { } c)
            return null;
        List<string>? granted = null;
        foreach (var (aura, shown) in GrantedAuras)
            if (!unit.HasTrait(shown) && _match.BenefitsFromAura(c, aura))
                (granted ??= new List<string>()).Add(shown);
        return granted;
    }

    // ── Mise en forme commune des cartes (combat + recrutement) ──────────────────
    private const int CardPad = 12;

    /// <summary>
    /// Corps d'une carte d'unité : sprite, icône de domaine (39×39), barre de PV (1 carré = 1 PV)
    /// + « pv/max », puis les caractéristiques (icône 32×32 + libellé + valeur). Les mots-clés sont
    /// dessinés à part (popups) par l'appelant. <paramref name="hp"/> = PV courants à afficher.
    /// </summary>
    private void DrawCardLayout(SpriteBatch sb, Rectangle rect, UnitClass c, Faction faction,
        Domaine domaine, int hp, int maxHp, bool revealed = true, Equipment? equip = null, int hpPreviewDamage = 0,
        CommandBuffs? buffs = null, IReadOnlyList<CommandNode>? treeNodes = null, int kills = 0,
        IReadOnlyList<string>? granted = null, int contextualDmgBonus = 0)
    {
        // Bonus affichés en « +N » à côté de la stat : ceux de l'ÉQUIPEMENT et ceux de l'ARBRE de
        // commandement, cumulés (la carte doit montrer ce que le pion vaut réellement au combat).
        var b = buffs ?? CommandBuffs.None;
        var hpBonus = (equip?.BonusFor(EquipStat.Hp) ?? 0) + b.BonusFor(EquipStat.Hp);
        var dmgBonus = (equip?.BonusFor(EquipStat.Damage) ?? 0) + b.BonusFor(EquipStat.Damage);
        // Berserk : +1 puissance par ennemi tué (bonus intrinsèque TOUJOURS actif, cf. Match.EffectivePower) —
        // on l'intègre au « +N » de la puissance pour que la carte montre la vraie valeur de combat.
        if (kills > 0 && CardHasTrait(c, equip, b, Trait.Berserk))
            dmgBonus += kills;
        dmgBonus += contextualDmgBonus;   // bonus de combat contextuels : auras de puissance + Formation + Rage (cf. DrawUnitCard)
        var moveBonus = (equip?.BonusFor(EquipStat.MoveRange) ?? 0) + b.BonusFor(EquipStat.MoveRange);
        var rangeBonus = (equip?.BonusFor(EquipStat.AttackRange) ?? 0) + b.BonusFor(EquipStat.AttackRange);
        const string unknown = "???";   // masque nom / PV / stats / traits d'une unité non découverte (méta)

        Context.Style.DrawPanel(sb, rect);
        var y = rect.Y + CardPad;

        // Icône de TIER (23×9) + NOM DU DOMAINE, groupés et centrés AU-DESSUS du nom, dans la marge haute :
        // ne décale rien dessous. Masqués tant que l'unité n'est pas découverte (on ne révèle pas son tier).
        if (revealed)
            DrawCardTierAndDomaine(sb, c.Tier, domaine, rect);

        // Titre : nom de l'unité (localisé), MASQUÉ « ??? » tant qu'elle n'est pas découverte. Échelle 2 par
        // défaut, repliée en 1 si le nom déborde de la carte (« ARBALETRIER MONTE » mesure 202 px pour une
        // carte de 200, et les cartes rétrécissent encore en 1080p). Pas d'échelle intermédiaire : le rendu
        // est pixel-perfect à l'échelle ENTIÈRE. La hauteur réservée ne bouge pas → rien ne se décale dessous.
        var name = revealed ? UnitName(c).ToUpperInvariant() : unknown;
        var nameScale = Context.Font.Measure(name, 2) <= rect.Width - 2 * CardPad ? 2 : 1;
        Context.Font.DrawCentered(sb, name, new Rectangle(rect.X, y, rect.Width, 14), nameScale, Palette.White);
        y += 22;

        // Sprite du pion (comme en jeu, de face). En SILHOUETTE si l'unité n'est pas encore découverte.
        var sprite = new Rectangle(rect.X + (rect.Width - 64) / 2, y, 64, 64);
        if (revealed)
            DrawChip(sb, c, faction, sprite, front: true);
        else
            DrawHiddenChip(sb, c, faction, sprite);

        // Les deux MARGES latérales du sprite, hautes de la bande « sprite + icône de domaine ». Tout ce
        // qui s'y pose est centré sur les deux axes : la mosaïque d'améliorations à gauche, l'icône
        // d'équipement à droite. Les deux blocs se répondent donc, quel que soit le nombre d'icônes.
        var bandH = 64 + 6 + DomaineIconSize;
        var left = new Rectangle(rect.X + CardPad, sprite.Y, sprite.X - SpriteMargin - (rect.X + CardPad), bandH);
        var right = new Rectangle(sprite.Right + SpriteMargin, sprite.Y,
            rect.Right - CardPad - (sprite.Right + SpriteMargin), bandH);

        // Icône de l'équipement porté : même cadre que les améliorations d'arbre, mais CERCLÉ D'OR.
        if (equip != null)
        {
            var eRect = new Rectangle(
                right.X + (right.Width - TreeIconBox) / 2,
                right.Y + (right.Height - TreeIconBox) / 2, TreeIconBox, TreeIconBox);
            DrawRect(sb, eRect, Palette.Black1);
            DrawRectBorder(sb, eRect, Palette.Yellow1, 1);
            DrawEquipIcon(sb, equip, eRect);
        }

        // Améliorations d'arbre ACTIVES sur ce pion, en mosaïque à GAUCHE du sprite (pendant de l'équipement).
        if (revealed && treeNodes is { Count: > 0 })
            DrawActiveTreeNodes(sb, treeNodes, left);
        y = sprite.Bottom + 6;

        // Icône de domaine (39×39), centrée sous le pion.
        var dom = new Rectangle(rect.X + (rect.Width - DomaineIconSize) / 2, y, DomaineIconSize, DomaineIconSize);
        DrawDomaineIcon(sb, domaine, dom);
        y = dom.Bottom + 10;

        // Barre de PV (une rangée, carrés ajustés à la largeur) + texte « pv/max » (+ bonus d'équipement).
        // Évolution NON DÉCOUVERTE (méta) : on masque tout l'effectif — barre neutre + « ??? » au lieu des PV,
        // et « ??? » à la place de chaque caractéristique (en plus de la silhouette du sprite et du nom).
        var barRect = new Rectangle(rect.X + CardPad, y, rect.Width - 2 * CardPad, 14);
        if (revealed)
        {
            DrawHpBar(sb, barRect, hp, maxHp, hpPreviewDamage);
            y = barRect.Bottom + 2;
            DrawHpText(sb, rect.X, y, rect.Width, hp, maxHp, hpBonus);
        }
        else
        {
            DrawRect(sb, barRect, Palette.Purple2);   // barre « inconnue » : ne révèle pas le nombre de PV
            y = barRect.Bottom + 2;
            Context.Font.DrawCentered(sb, unknown, new Rectangle(rect.X, y, rect.Width, 8), 1, Palette.White);
        }
        y += 14;

        // Caractéristiques : icône 32×32 + libellé + valeur (effective, équipement inclus) + « +N ».
        // Portée = MAX seulement (le « min » / zone morte est expliqué par le mot-clé ZONE MORTE).
        y = DrawStatRow(sb, rect, y, "deg", Loc.T("stat.power"), revealed ? $"{c.Damage + dmgBonus}" : unknown, Palette.Brown3, revealed ? dmgBonus : 0);
        y = DrawStatRow(sb, rect, y, "dep", Loc.T("stat.movement"), revealed ? $"{c.MoveRange + moveBonus}" : unknown, Palette.Cyan2, revealed ? moveBonus : 0);
        DrawStatRow(sb, rect, y, "tir", Loc.T("stat.range"), revealed ? $"{c.AttackRange + rangeBonus}" : unknown, Palette.Yellow2, revealed ? rangeBonus : 0);

        // Liste des mots-clés (traits) en bas de carte (séparés par « | »), détaillés dans les popups.
        // MASQUÉS « ??? » tant que l'unité n'est pas découverte : on ne révèle ni le nombre ni la nature des traits.
        if (!revealed)
        {
            Context.Font.DrawCentered(sb, unknown,
                new Rectangle(rect.X, rect.Bottom - CardPad - 9, rect.Width, 8), 1, Palette.Yellow2);
            return;
        }

        // Bas de carte ancré EN BAS et empilé vers le HAUT (jamais de chevauchement avec les stats ni entre
        // eux, quel que soit le nombre de traits) : la liste des mots-clés (traits séparés par « | », détaillés
        // en popups) tout en bas, puis « TUÉS : N » juste au-dessus (palmarès à vie, seulement si > 0).
        var keywords = KeywordsFor(c, equip, b, granted);
        var bottomY = DrawKeywordList(sb, keywords, AddedKeywordLabels(c, equip, b, granted), rect, rect.Bottom - CardPad);
        if (kills > 0)
            Context.Font.DrawCentered(sb, Loc.T("stat.kills", kills),
                new Rectangle(rect.X, bottomY - 9, rect.Width, 8), 1, Palette.Purple5);
    }

    /// <summary>
    /// Version CONDENSÉE de la carte (combat, par défaut) : nom, PV en texte « pv/max », les trois
    /// caractéristiques réduites à ICÔNE + valeur EFFECTIVE (équipement/arbre/berserk/contexte inclus, mais SANS
    /// le détail « +N »), puis en bas les traits en NOMS seuls (aucun popup) et « TUÉS : N ». Toujours révélée
    /// (les cartes de combat le sont). Le clic droit / X repasse au détaillé (cf. <see cref="DrawCardLayout"/>).
    /// </summary>
    private void DrawCondensedCardLayout(SpriteBatch sb, Rectangle rect, UnitClass c, Domaine domaine, int hp, int maxHp,
        Equipment? equip, CommandBuffs? buffs, int kills, IReadOnlyList<string>? granted, int contextualDmgBonus,
        int hpPreviewDamage)
    {
        // Mêmes bonus effectifs que la carte détaillée (cf. DrawCardLayout), mais on n'affiche QUE la valeur.
        var b = buffs ?? CommandBuffs.None;
        var dmgBonus = (equip?.BonusFor(EquipStat.Damage) ?? 0) + b.BonusFor(EquipStat.Damage);
        if (kills > 0 && CardHasTrait(c, equip, b, Trait.Berserk))
            dmgBonus += kills;
        dmgBonus += contextualDmgBonus;
        var moveBonus = (equip?.BonusFor(EquipStat.MoveRange) ?? 0) + b.BonusFor(EquipStat.MoveRange);
        var rangeBonus = (equip?.BonusFor(EquipStat.AttackRange) ?? 0) + b.BonusFor(EquipStat.AttackRange);

        Context.Style.DrawPanel(sb, rect);

        // Icône de TIER (taille native 23×9) + NOM DU DOMAINE, dans la marge haute (même rendu que le détaillé).
        DrawCardTierAndDomaine(sb, c.Tier, domaine, rect);
        var y = rect.Y + 15;   // sous l'en-tête tier/domaine

        // Nom centré, échelle 2 par défaut, repliée en 1 s'il déborde (même règle que la carte détaillée).
        var name = UnitName(c).ToUpperInvariant();
        var nameScale = Context.Font.Measure(name, 2) <= rect.Width - 2 * CondensedPad ? 2 : 1;
        Context.Font.DrawCentered(sb, name, new Rectangle(rect.X, y, rect.Width, 14), nameScale, Palette.White);
        y += 18;

        // PV « pv/max » EN ROUGE (échelle 2, bien lisible) centré ; aperçu de dégâts éventuel accolé.
        DrawCondensedHp(sb, rect, y, hp, maxHp, hpPreviewDamage);
        y += 20;

        // Trois stats réparties horizontalement : icône (taille NATIVE) + valeur effective, sans libellé ni « +N ».
        DrawCondensedStats(sb, rect, y, c.Damage + dmgBonus, c.MoveRange + moveBonus, c.AttackRange + rangeBonus);

        // Bas de carte (ancré en bas comme la carte détaillée) : traits en noms seuls, « TUÉS : N » au-dessus.
        var keywords = KeywordsFor(c, equip, b, granted);
        var bottomY = DrawKeywordList(sb, keywords, AddedKeywordLabels(c, equip, b, granted), rect, rect.Bottom - CondensedPad);
        if (kills > 0)
            Context.Font.DrawCentered(sb, Loc.T("stat.kills", kills),
                new Rectangle(rect.X, bottomY - 9, rect.Width, 8), 1, Palette.Purple5);
    }

    /// <summary>PV condensés « pv/max » EN ROUGE (échelle 2) centrés ; si une attaque est visée, « -N » jaune accolé.</summary>
    private void DrawCondensedHp(SpriteBatch sb, Rectangle rect, int y, int hp, int maxHp, int previewDamage)
    {
        var text = $"{hp}/{maxHp}";
        if (previewDamage <= 0)
        {
            Context.Font.DrawCentered(sb, text, new Rectangle(rect.X, y, rect.Width, 14), 2, Palette.Purple5);
            return;
        }
        // Aperçu de dégâts (visée) : « -N » en jaune vif pour ressortir des PV rouges, même taille.
        var tag = $" -{previewDamage}";
        var wMain = Context.Font.Measure(text, 2);
        var wTag = Context.Font.Measure(tag, 2);
        var startX = rect.X + (rect.Width - (wMain + wTag)) / 2;
        Context.Font.Draw(sb, text, new Vector2(startX, y), 2, Palette.Purple5);
        Context.Font.Draw(sb, tag, new Vector2(startX + wMain, y), 2, Palette.Yellow2);
    }

    /// <summary>Les trois caractéristiques condensées (puissance / mouvement / portée) en trois colonnes égales.</summary>
    private void DrawCondensedStats(SpriteBatch sb, Rectangle rect, int y, int power, int move, int range)
    {
        const int iconSize = 32;   // taille NATIVE de l'icône : dessinée 1:1, aucune mise à l'échelle (pixel-perfect)
        var inner = new Rectangle(rect.X + CondensedPad, y, rect.Width - 2 * CondensedPad, iconSize);
        var colW = inner.Width / 3;
        DrawCondensedStat(sb, new Rectangle(inner.X, y, colW, iconSize), "deg", $"{power}", Palette.Brown3);
        DrawCondensedStat(sb, new Rectangle(inner.X + colW, y, colW, iconSize), "dep", $"{move}", Palette.Cyan2);
        DrawCondensedStat(sb, new Rectangle(inner.X + 2 * colW, y, colW, iconSize), "tir", $"{range}", Palette.Yellow2);
    }

    /// <summary>Une stat condensée : icône NATIVE 32×32 centrée dans sa colonne, valeur (échelle 2) centrée dessous.</summary>
    private void DrawCondensedStat(SpriteBatch sb, Rectangle col, string iconKey, string value, Color color)
    {
        const int iconSize = 32;
        var icon = new Rectangle(col.X + (col.Width - iconSize) / 2, col.Y, iconSize, iconSize);
        DrawStatIcon(sb, iconKey, icon, color);
        Context.Font.DrawCentered(sb, value, new Rectangle(col.X, icon.Bottom + 2, col.Width, 14), 2, color);
    }

    // Grille d'améliorations d'arbre, à gauche du sprite : cadres 34 (icône 32×32 native centrée),
    // 2 colonnes × 3 rangées. Six emplacements = le maximum de nœuds pouvant agir sur un même pion
    // (branche complète du commandant). La grille tient exactement entre la marge gauche et le sprite,
    // et sa hauteur s'arrête au-dessus de la barre de PV.
    private const int TreeIconBox = 34;
    private const int TreeIconGap = 4;
    private const int TreeIconPitch = TreeIconBox + TreeIconGap - 2;   // 36 : rangées presque jointives
    private const int TreeIconCols = 2;
    private const int TreeIconRows = 3;
    private const int TreeIconSlots = TreeIconCols * TreeIconRows;

    /// <summary>Côté de l'icône de domaine, sous le sprite (elle ferme la bande de référence des cartes).</summary>
    private const int DomaineIconSize = 39;

    /// <summary>Respiration entre le sprite du pion et les blocs d'icônes qui l'encadrent.</summary>
    private const int SpriteMargin = 8;

    /// <summary>
    /// Améliorations de l'arbre ACTIVES sur ce pion, en mosaïque dans <paramref name="area"/> (la marge à
    /// gauche du sprite, pendant de l'icône d'équipement à droite). La mosaïque s'ADAPTE au nombre d'icônes :
    /// elle est centrée dans l'aire sur les deux axes, et chaque rangée est centrée à son tour — une icône
    /// seule tombe donc au milieu, deux se posent côte à côte, une rangée incomplète reste centrée.
    /// Au-delà de <see cref="TreeIconSlots"/> (arbre futur plus large), le dernier cadre devient un « +N ».
    /// </summary>
    private void DrawActiveTreeNodes(SpriteBatch sb, IReadOnlyList<CommandNode> nodes, Rectangle area)
    {
        var overflow = nodes.Count > TreeIconSlots;
        var shown = overflow ? TreeIconSlots - 1 : nodes.Count;
        var slots = overflow ? TreeIconSlots : nodes.Count;

        var rows = (slots + TreeIconCols - 1) / TreeIconCols;
        var top = area.Y + (area.Height - ((rows - 1) * TreeIconPitch + TreeIconBox)) / 2;

        for (var i = 0; i < shown; i++)
            _commandTree.DrawNodeIcon(sb, nodes[i], TreeIconSlot(sb, area, top, i, slots), Color.White);

        if (overflow)
            Context.Font.DrawCentered(sb, $"+{nodes.Count - shown}",
                TreeIconSlot(sb, area, top, shown, slots), 1, Palette.Cyan1);
    }

    /// <summary>
    /// Cadre du <paramref name="index"/>-ième emplacement d'une mosaïque de <paramref name="slots"/> cadres
    /// (fond sombre + liseré cyan), dessiné puis renvoyé. La rangée est centrée horizontalement dans
    /// <paramref name="area"/> selon le nombre de cadres qu'elle porte réellement.
    /// </summary>
    private Rectangle TreeIconSlot(SpriteBatch sb, Rectangle area, int top, int index, int slots)
    {
        var row = index / TreeIconCols;
        var inRow = System.Math.Min(TreeIconCols, slots - row * TreeIconCols);   // dernière rangée : parfois 1 seul
        var rowW = inRow * TreeIconBox + (inRow - 1) * TreeIconGap;

        var r = new Rectangle(
            area.X + (area.Width - rowW) / 2 + index % TreeIconCols * (TreeIconBox + TreeIconGap),
            top + row * TreeIconPitch,
            TreeIconBox, TreeIconBox);
        DrawRect(sb, r, Palette.Black1);
        DrawRectBorder(sb, r, Palette.Cyan1, 1);   // cyan = arbre (l'équipement, lui, est cerclé d'or)
        return r;
    }

    /// <summary>
    /// Améliorations d'arbre à montrer sur la carte d'une <see cref="Unit"/> : celles qui agissent sur elle
    /// (commandant ou troupe). Null pour un ennemi — l'arbre ne le concerne jamais.
    /// </summary>
    private IReadOnlyList<CommandNode>? TreeNodesFor(Unit unit) =>
        unit.Faction == Faction.Player ? _run.ActiveNodesFor(unit.IsEssential) : null;

    /// <summary>
    /// Une ligne de caractéristique : icône 32×32 à gauche, libellé, valeur alignée à droite. Si
    /// <paramref name="bonus"/> &gt; 0 (équipement), un « +N » bleu clair est affiché à gauche de la valeur.
    /// </summary>
    private int DrawStatRow(SpriteBatch sb, Rectangle card, int y, string iconKey, string label,
        string value, Color valueColor, int bonus = 0)
    {
        const int iconSize = 32;
        var icon = new Rectangle(card.X + CardPad, y, iconSize, iconSize);
        DrawStatIcon(sb, iconKey, icon, valueColor);

        var rowH = new Rectangle(icon.Right + 8, y, card.Right - CardPad - (icon.Right + 8), iconSize);
        var midBig = rowH.Y + (iconSize - 7 * 2) / 2;   // ligne de base des textes scale 2 (14 px), centrée
        // Libellé en scale 1 (l'icône identifie déjà la stat) → laisse la place à un bonus BIEN visible.
        Context.Font.Draw(sb, label, new Vector2(rowH.X, rowH.Y + (iconSize - 7) / 2), 1, Palette.Blue1);
        var vw = Context.Font.Measure(value, 2);
        Context.Font.Draw(sb, value, new Vector2(rowH.Right - vw, midBig), 2, valueColor);
        if (bonus > 0)
        {
            // Bonus d'équipement BIEN visible : grand (scale 2) et jaune vif, entre (), à gauche de la valeur.
            var tag = $"(+{bonus})";
            var tw = Context.Font.Measure(tag, 2);
            Context.Font.Draw(sb, tag, new Vector2(rowH.Right - vw - 8 - tw, midBig), 2, Palette.Yellow2);
        }
        return y + iconSize + 4;
    }

    /// <summary>Texte « pv/max » centré, avec un « +N » jaune vif accolé (MÊME taille que les PV) si l'équipement augmente les PV.</summary>
    private void DrawHpText(SpriteBatch sb, int x, int y, int width, int hp, int maxHp, int hpBonus)
    {
        var hpText = $"{hp}/{maxHp}";
        if (hpBonus <= 0)
        {
            Context.Font.DrawCentered(sb, hpText, new Rectangle(x, y, width, 8), 1, Palette.White);
            return;
        }
        // « pv/max » blanc puis « (+N) » jaune vif, MÊME taille (scale 1), sur une seule ligne centrée.
        var bonusText = $"(+{hpBonus})";
        var wMain = Context.Font.Measure(hpText, 1);
        var wBonus = Context.Font.Measure(bonusText, 1);
        const int gap = 5;
        var startX = x + (width - (wMain + gap + wBonus)) / 2;
        Context.Font.Draw(sb, hpText, new Vector2(startX, y), 1, Palette.White);
        Context.Font.Draw(sb, bonusText, new Vector2(startX + wMain + gap, y), 1, Palette.Yellow2);
    }

    /// <summary>
    /// Barre de PV : la barre occupe TOUTE la zone (taille fixe, hauteur indépendante du nombre de PV)
    /// et se découpe en un segment par point de vie. PV restants = rouge, PV manquants = rouge foncé.
    /// </summary>
    /// <summary>
    /// Barre de PV en carrés (1 carré = 1 PV). <paramref name="previewDamage"/> &gt; 0 : les
    /// <paramref name="previewDamage"/> derniers PV pleins CLIGNOTENT (prévisualisation des dégâts d'une
    /// attaque visée) entre plein et vide.
    /// </summary>
    private void DrawHpBar(SpriteBatch sb, Rectangle area, int hp, int maxHp, int previewDamage = 0)
    {
        if (maxHp <= 0)
            return;

        var doomedFrom = System.Math.Max(0, hp - previewDamage);          // 1er PV menacé (borne basse)
        var blink = 0.5f + 0.5f * MathF.Sin(_time * 12f);                 // clignotement des PV menacés

        const int gap = 1;
        // Bornes PARTAGÉES entre segments voisins : on arrondit une seule fois chaque frontière, puis
        // on retire 1 px à droite pour l'espace. Gap toujours constant, largeurs à ±1 px près.
        for (var i = 0; i < maxHp; i++)
        {
            var left = area.X + (int)System.Math.Round((double)i * area.Width / maxHp);
            var right = area.X + (int)System.Math.Round((double)(i + 1) * area.Width / maxHp);
            var w = System.Math.Max(1, right - left - (i < maxHp - 1 ? gap : 0));

            Color col;
            if (i >= hp)
                col = Palette.Purple2;                                    // PV manquant
            else if (previewDamage > 0 && i >= doomedFrom)
                col = Color.Lerp(Palette.Purple2, Palette.Purple5, blink);// PV menacé : clignote plein↔vide
            else
                col = Palette.Purple5;                                    // PV plein
            DrawRect(sb, new Rectangle(left, area.Y, w, area.Height), col);
        }
    }

    // ── Icônes (placeholders dessinés ; brancher un PNG = déposer le fichier nommé ci-dessous) ───
    private readonly Dictionary<string, Texture2D?> _iconSprites = new();

    // Icônes d'équipement 32×32 (Assets/Equipment/<icon>.png), mises en cache (clé = nom d'icône).
    private readonly Dictionary<string, Texture2D?> _equipSprites = new();
    // Fond de slot d'équipement 32×32 (Assets/Equipment/background.png) : derrière l'icône + slot vide.
    private Texture2D? _equipSlotBg;
    // Icône de RELANCE 32×32 (Assets/UI/relance.png) : cible de dépose à gauche du panneau (placeholder si absente).
    private Texture2D? _rerollIcon;
    // Icône de RECYCLAGE 32×32 (Assets/UI/recycler.png) : même emplacement, EN SOUS-PHASE ÉQUIPEMENT (casser un objet → +1 relance).
    private Texture2D? _recycleIcon;

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

    /// <summary>
    /// Icône du TIER (1/2/3) du pion, dessinée à sa taille native (23×9, non déformée) dans <paramref name="area"/>.
    /// PNG <c>Assets/Icons/tier_&lt;tier&gt;.png</c> si présent, sinon un bandeau « T&lt;n&gt; » de repli tant que
    /// l'art n'est pas fourni. <paramref name="alpha"/> pour suivre le fondu d'apparition en jeu.
    /// </summary>
    private void DrawTierIcon(SpriteBatch sb, int tier, Rectangle area, float alpha = 1f)
    {
        if (IconOrNull($"tier_{tier}") is { } png)
        {
            sb.Draw(png, area, Color.White * alpha);   // 23×9 → rect 23×9 : 1:1, pas de déformation
            return;
        }
        DrawRect(sb, Inflate(area, 1), Palette.Black1 * alpha);
        DrawRect(sb, area, Palette.Navy2 * alpha);
        Context.Font.DrawCentered(sb, $"T{tier}", area, 1, Palette.White * alpha);
    }

    /// <summary>
    /// Marge haute de la carte : icône de TIER (chiffre romain) suivie du NOM DU DOMAINE, l'ensemble centré.
    /// Le nom rappelle en toutes lettres le domaine dont l'icône de déplacement figure plus bas sur la carte.
    /// </summary>
    private void DrawCardTierAndDomaine(SpriteBatch sb, int tier, Domaine domaine, Rectangle rect)
    {
        var name = Loc.TOr($"domaine.{domaine}".ToLowerInvariant(), domaine.ToString().ToUpperInvariant());
        const int gap = 5;
        var nameW = Context.Font.Measure(name, 1);
        var startX = rect.X + (rect.Width - (TierIconW + gap + nameW)) / 2;
        DrawTierIcon(sb, tier, new Rectangle(startX, rect.Y + 2, TierIconW, TierIconH));
        Context.Font.Draw(sb, name, new Vector2(startX + TierIconW + gap, rect.Y + 3), 1, Palette.Cyan1);
    }

    // ── Popups de mots-clés ──────────────────────────────────────────────────────

    /// <summary>
    /// Mots-clés d'une classe : ses traits + « Traverse allié » si elle perce ses alliés. Un éventuel
    /// <paramref name="equip"/> de TRAIT et les <paramref name="buffs"/> de l'arbre de commandement
    /// ajoutent leurs traits (comme des traits natifs), sauf doublon.
    /// </summary>
    /// <summary>
    /// Couleur des mots-clés qui ne sont PAS des traits de BASE de la classe (ajoutés par un équipement,
    /// l'arbre de commandement ou une aura voisine) : mis en jaune pour signaler un bonus, à distinguer des
    /// traits natifs (qui restent en Cyan1). Cf. <see cref="AddedKeywordLabels"/>.
    /// </summary>
    private static readonly Color GrantedKeywordColor = Palette.Yellow2;

    /// <summary>Rouge signalant qu'un trait est RENFORCÉ par l'arbre de commandement (nom + valeur montée en surbrillance).</summary>
    private static readonly Color ReinforcedTraitColor = Palette.Purple5;

    /// <summary>
    /// Résout la description d'un mot-clé pour l'affichage. Les traits « renforçables » par l'arbre (Rempart,
    /// Esquive) portent un marqueur <c>{0}</c> dans <c>strings.csv</c> : on y substitue leur valeur EFFECTIVE
    /// (base + bonus d'arbre de la run courante). Renvoie AUSSI le token à peindre en ROUGE (la valeur montée)
    /// et un drapeau « renforcé » (nom du trait en rouge) — nuls/faux si non renforcé (bonus 0) ou non
    /// renforçable. TOUT rendu de description passe par ici : garantit qu'aucun « {0} » ne fuite à l'écran.
    /// <paramref name="reinforcedApplies"/> = false (unité ENNEMIE) ignore le bonus d'arbre : le renforcement
    /// ne vaut que pour les pions du JOUEUR (cf. <see cref="Match.RempartReductionFor"/>), donc l'ennemi affiche
    /// la valeur de BASE, sans rouge.
    /// </summary>
    private (string Desc, string? Highlight, bool Reinforced) KeywordDisplay(UnitKeywords.Keyword kw, bool reinforcedApplies = true)
    {
        var rempartBonus = reinforcedApplies ? _run?.RempartBonus ?? 0 : 0;
        var esquiveBonus = reinforcedApplies ? _run?.EsquiveBonusPercent ?? 0 : 0;
        var geantsBonus = reinforcedApplies ? _run?.TueurDeGeantsBonus ?? 0 : 0;
        var formationBonus = reinforcedApplies ? _run?.FormationBonus ?? 0 : 0;
        if (kw.Label == UnitKeywords.For(Trait.Rempart).Label)
            return ResolveReinforced(kw.Description, Match.BaseRempartReduction, rempartBonus);
        if (kw.Label == UnitKeywords.For(Trait.Esquive).Label)
            return ResolveReinforced(kw.Description, (int)System.Math.Round(Match.BaseEsquiveChance * 100), esquiveBonus);
        if (kw.Label == UnitKeywords.For(Trait.TueurDeGeants).Label)
            return ResolveReinforced(kw.Description, Match.BaseGiantSlayerBonus, geantsBonus);
        if (kw.Label == UnitKeywords.For(Trait.Formation).Label)
            return ResolveReinforced(kw.Description, Match.BaseFormationBonus, formationBonus);
        return (kw.Description, null, false);
    }

    /// <summary>Substitue <c>base + bonus</c> au marqueur <c>{0}</c> ; le token surligné et le drapeau ne sont
    /// posés que si <paramref name="bonus"/> &gt; 0 (trait effectivement renforcé).</summary>
    private static (string, string?, bool) ResolveReinforced(string template, int baseValue, int bonus)
    {
        var value = baseValue + bonus;
        var desc = template.Contains("{0}") ? string.Format(template, value) : template;
        return bonus > 0 ? (desc, value.ToString(), true) : (desc, null, false);
    }

    /// <summary>Dessine une ligne de description MOT À MOT, en peignant en rouge la PREMIÈRE occurrence de
    /// <paramref name="highlight"/> (sautée si <paramref name="alreadyDrawn"/>). Renvoie l'état « surbrillance posée ».
    /// Police à chasse fixe → l'avance par mot reconstruit exactement la mise en page d'un rendu d'un seul tenant.</summary>
    private bool DrawHighlightedLine(SpriteBatch sb, string line, int x, int y, string highlight, bool alreadyDrawn)
    {
        var font = Context.Font;
        var adv = font.Measure("mm", 1) - font.Measure("m", 1);   // avance d'UN caractère (police à chasse fixe)
        var col = 0;                                              // colonne caractère depuis le début de la ligne (comme Draw)
        foreach (var word in line.Split(' '))
        {
            var hl = !alreadyDrawn && word == highlight;
            if (hl) alreadyDrawn = true;
            font.Draw(sb, word, new Vector2(x + col * adv, y), 1, hl ? ReinforcedTraitColor : Palette.White, preserveCase: true);
            col += word.Length + 1;                              // longueur du mot + l'espace séparateur
        }
        return alreadyDrawn;
    }

    /// <summary>
    /// Libellés des mots-clés qui ne sont PAS des traits de BASE de la classe : ajoutés par un équipement,
    /// par l'arbre de commandement ou par une aura de placement. On les peint en jaune sur la carte pour
    /// signaler un bonus (<see cref="GrantedKeywordColor"/>) ; les traits natifs restent en Cyan1. Un trait à
    /// la fois natif ET accordé compte comme natif (présent dans <see cref="UnitClass.Traits"/>). Renvoie
    /// <c>null</c> si rien n'est ajouté (évite une allocation et le test au moment de peindre).
    /// </summary>
    private static HashSet<string>? AddedKeywordLabels(UnitClass c, Equipment? equip,
        CommandBuffs? buffs, IReadOnlyList<string>? granted)
    {
        var innate = new HashSet<string>(c.Traits);
        var labels = new HashSet<string>();
        void Consider(string raw)
        {
            if (!innate.Contains(raw))
                labels.Add(UnitKeywords.For(raw).Label);
        }
        if (equip is { } eq)
            foreach (var et in eq.Traits) Consider(et);
        foreach (var bt in (buffs ?? CommandBuffs.None).Traits) Consider(bt);
        if (granted != null)
            foreach (var gt in granted) Consider(gt);
        return labels.Count == 0 ? null : labels;
    }

    /// <summary>
    /// Liste des mots-clés en bas de carte, empilée vers le HAUT depuis <paramref name="bottomY"/> et centrée
    /// ligne par ligne. Chaque libellé est peint SÉPARÉMENT pour que les traits NON natifs ressortent (cf.
    /// <see cref="GrantedKeywordColor"/> / <see cref="AddedKeywordLabels"/>) : rendu segment par segment plutôt
    /// qu'une chaîne jointe. Le repli se fait au mot-clé près (jamais au milieu d'un libellé), la police étant
    /// à chasse fixe. Renvoie l'ordonnée du haut de la liste (le contenu suivant s'empile au-dessus).
    /// </summary>
    private int DrawKeywordList(SpriteBatch sb, List<UnitKeywords.Keyword> keywords,
        HashSet<string>? addedLabels, Rectangle rect, int bottomY)
    {
        if (keywords.Count == 0)
            return bottomY;

        const string sep = " | ";
        var font = Context.Font;
        var maxW = rect.Width - 2 * CardPad;
        var sepW = font.Measure(sep, 1);

        var lines = new List<List<(string Text, Color Color)>>();
        var line = new List<(string Text, Color Color)>();
        var lineW = 0;
        foreach (var kw in keywords)
        {
            var w = font.Measure(kw.Label, 1);
            if (line.Count > 0 && lineW + sepW + w > maxW)
            {
                lines.Add(line);
                line = new List<(string Text, Color Color)>();
                lineW = 0;
            }
            if (line.Count > 0)
            {
                line.Add((sep, Palette.Cyan1));
                lineW += sepW;
            }
            line.Add((kw.Label,
                addedLabels != null && addedLabels.Contains(kw.Label) ? GrantedKeywordColor : Palette.Cyan1));
            lineW += w;
        }
        lines.Add(line);

        var ty = bottomY - lines.Count * 9;
        foreach (var l in lines)
        {
            var x = rect.X + (rect.Width - l.Sum(t => font.Measure(t.Text, 1))) / 2;
            foreach (var (text, color) in l)
            {
                font.Draw(sb, text, new Vector2(x, ty), 1, color);
                x += font.Measure(text, 1);
            }
            ty += 9;
        }
        return bottomY - lines.Count * 9;
    }

    /// <summary>Vrai si la carte porte ce trait, toutes sources confondues (classe, équipement, arbre) —
    /// pendant côté UI de <see cref="Unit.HasTrait"/>, sans instance de pion.</summary>
    private static bool CardHasTrait(UnitClass c, Equipment? equip, CommandBuffs b, string trait) =>
        (equip?.GrantsTrait(trait) ?? false) || b.GrantsTrait(trait) || c.Traits.Contains(trait);

    private static List<UnitKeywords.Keyword> KeywordsFor(UnitClass c, Equipment? equip = null,
        CommandBuffs? buffs = null, IReadOnlyList<string>? granted = null)
    {
        var list = new List<UnitKeywords.Keyword>();
        var seen = new HashSet<string>(c.Traits);
        foreach (var t in c.Traits)
            list.Add(UnitKeywords.For(t));
        // « Traverse allié » est redondant avec « Franchissement » (le cavalier franchit déjà tout) :
        // on ne le montre pas quand la classe a ce trait, Franchissement suffit à l'expliquer.
        if (c.PiercesAllies && !c.Traits.Contains("Franchissement"))
            list.Add(UnitKeywords.PiercesAllies);
        if (c.MinAttackRange > 1)
            list.Add(UnitKeywords.DeadZone);
        // Traits octroyés par un équipement puis par l'arbre : affichés comme des traits natifs.
        if (equip is { } eq)
            foreach (var et in eq.Traits)
                if (seen.Add(et))
                    list.Add(UnitKeywords.For(et));
        foreach (var bt in (buffs ?? CommandBuffs.None).Traits)
            if (seen.Add(bt))
                list.Add(UnitKeywords.For(bt));
        // Traits tenus du PLACEMENT (ex. « Rempart » offert par une aura adjacente) : ils n'existent ni sur la
        // classe ni sur l'équipement, la carte ne les montrerait donc jamais sans ça.
        if (granted != null)
            foreach (var gt in granted)
                if (seen.Add(gt))
                    list.Add(UnitKeywords.For(gt));
        return list;
    }

    /// <summary>
    /// Popups permanents empilés SOUS la carte, ou À CÔTÉ d'elle quand ils n'y tiennent pas.
    ///
    /// <see cref="DrawKeywordPopupStack"/> remonte une pile qui dépasserait le bas de l'écran pour ne
    /// pas la couper — mais passé ~2 traits cette remontée la fait passer PAR-DESSUS la carte qu'on
    /// est en train de lire (en canvas 540, la carte tient 105→435 : il ne reste que ~90 px dessous,
    /// contre ~200 pour 4 traits). On bascule donc à côté, comme les écrans de draft (cf.
    /// <see cref="DrawRowKeywords"/>). Aucune disposition verticale n'est possible dans ce cas :
    /// carte (330) + pile (200) = toute la hauteur du canvas.
    /// </summary>
    private void DrawKeywordPopupsBelow(SpriteBatch sb, UnitClass c, Rectangle card, Equipment? equip = null,
        CommandBuffs? buffs = null, IReadOnlyList<string>? granted = null, Faction faction = Faction.Player)
    {
        var h = KeywordStackHeight(c, card.Width, equip, buffs, granted, faction);
        if (h == 0)
            return;

        // Ordonnée qu'aurait la pile une fois remontée : si elle mord sur la carte, on passe à côté.
        if (VirtualViewport.Height - KwScreenMargin - h >= card.Bottom)
        {
            DrawKeywordPopupStack(sb, c, new Point(card.X, card.Bottom + 10), card.Width, equip, buffs, granted, faction);
            return;
        }

        // À côté, vers le CENTRE de l'écran (la carte est collée à un bord : il n'y a de place que
        // de l'autre côté), alignée sur son haut.
        var x = card.X - CombatCardGap - card.Width;
        if (x < KwScreenMargin)
            x = card.Right + CombatCardGap;
        DrawKeywordPopupStack(sb, c, new Point(x, card.Y), card.Width, equip, buffs, granted, faction);
    }

    /// <summary>
    /// Empile verticalement un popup par mot-clé depuis <paramref name="origin"/> : un panneau avec le
    /// libellé (jaune) et la description en lignes repliées. Rien si l'unité n'a aucun mot-clé.
    /// Inclut le trait d'un éventuel équipement (cf. <see cref="KeywordsFor"/>).
    /// </summary>
    private const int KwPad = 8, KwLineH = 9, KwGap = 8, KwScreenMargin = 8;

    /// <summary>Popups d'une classe pré-calculés (lignes repliées + hauteur) pour une largeur donnée.</summary>
    private List<(UnitKeywords.Keyword Kw, List<string> Lines, int H, bool Reinforced, string? Highlight)> KeywordBoxes(
        UnitClass c, int width, Equipment? equip, CommandBuffs? buffs, IReadOnlyList<string>? granted = null,
        Faction faction = Faction.Player)
    {
        var boxes = new List<(UnitKeywords.Keyword, List<string>, int, bool, string?)>();
        foreach (var kw in KeywordsFor(c, equip, buffs, granted))
        {
            // Le renforcement d'arbre ne s'affiche que pour les unités du JOUEUR (l'ennemi n'en profite pas).
            var (desc, highlight, reinforced) = KeywordDisplay(kw, faction == Faction.Player);
            var lines = WrapText(SentenceCase(desc), width - 2 * KwPad, 1);
            boxes.Add((kw, lines, KwPad + 10 + lines.Count * KwLineH + KwPad, reinforced, highlight));   // titre + lignes
        }
        return boxes;
    }

    /// <summary>Hauteur totale de la pile de popups d'une classe (0 si elle n'a aucun mot-clé).</summary>
    private int KeywordStackHeight(UnitClass c, int width, Equipment? equip = null, CommandBuffs? buffs = null,
        IReadOnlyList<string>? granted = null, Faction faction = Faction.Player)
    {
        var boxes = KeywordBoxes(c, width, equip, buffs, granted, faction);
        return boxes.Count == 0 ? 0 : boxes.Sum(b => b.H) + (boxes.Count - 1) * KwGap;
    }

    private void DrawKeywordPopupStack(SpriteBatch sb, UnitClass c, Point origin, int width, Equipment? equip = null,
        CommandBuffs? buffs = null, IReadOnlyList<string>? granted = null, Faction faction = Faction.Player)
    {
        var boxes = KeywordBoxes(c, width, equip, buffs, granted, faction);
        if (boxes.Count == 0)
            return;
        var total = boxes.Sum(b => b.H) + (boxes.Count - 1) * KwGap;

        // REMONTE la pile si elle dépasse le bas de l'écran (sinon les derniers popups sont coupés, ex. une
        // évolution à 3 traits en phase de fusion). Décalage MINIMAL, borné en haut de l'écran.
        var y = origin.Y;
        if (y + total > VirtualViewport.Height - KwScreenMargin)
            y = System.Math.Max(KwScreenMargin, VirtualViewport.Height - KwScreenMargin - total);

        var addedLabels = AddedKeywordLabels(c, equip, buffs, granted);
        foreach (var (kw, lines, h, reinforced, highlight) in boxes)
        {
            var box = new Rectangle(origin.X, y, width, h);
            Context.Style.DrawPanel(sb, box);

            // Nom du trait : ROUGE si renforcé par l'arbre, sinon JAUNE si non natif (équipement/arbre/aura), sinon bleu.
            var labelColor = reinforced ? ReinforcedTraitColor
                : addedLabels != null && addedLabels.Contains(kw.Label) ? GrantedKeywordColor : Palette.Cyan1;
            Context.Font.Draw(sb, kw.Label, new Vector2(box.X + KwPad, box.Y + KwPad), 1, labelColor);
            var ly = box.Y + KwPad + 11;
            var highlightDrawn = false;
            foreach (var line in lines)
            {
                // Description : la valeur MONTÉE (highlight) ressort en rouge quand le trait est renforcé.
                if (highlight == null)
                    Context.Font.Draw(sb, line, new Vector2(box.X + KwPad, ly), 1, Palette.White, preserveCase: true);
                else
                    highlightDrawn = DrawHighlightedLine(sb, line, box.X + KwPad, ly, highlight, highlightDrawn);
                ly += KwLineH;
            }
            y += h + KwGap;
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

    private string CombatTitle()
    {
        var mission = _run.CurrentMission switch
        {
            CombatType.Boss => Loc.T("mission.boss"),
            CombatType.Speciale => Loc.T("mission.speciale"),
            _ => Loc.T("mission.escarmouche"),
        };
        return Loc.T("combat.phase", _run.PhaseIndex, Run.EndAtPhase, mission);
    }

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
        if (!InvSlotVisible(PendingVisualSlot(i)))
            return;   // portrait focalisé défilé hors de la fenêtre : pas de cadre orphelin
        var icon = PendingCardRect(i);
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
        if (_protectReward is { } rewards)   // mission « protéger » : écran de récompense (tous les pions sauvés)
        {
            DrawProtectReward(sb, viewport, rewards);
            return;
        }

        var availW = viewport.Width - RightPanelWidth;   // zone des cartes, à GAUCHE du panneau

        sb.Begin(samplerState: SamplerState.PointClamp);
        // Cadre (style panneau/carte) autour du titre + sous-titre, dimensionné sur le plus large des deux.
        var titleW = Context.Font.Measure(Loc.T("recruit.title"), 3);
        var subW = Context.Font.Measure(Loc.T("recruit.subtitle"), 1);
        var boxW = System.Math.Max(titleW, subW) + 56;
        Context.Style.DrawPanel(sb, new Rectangle((availW - boxW) / 2, PostCombatTitleY, boxW, PostCombatTitleH));
        Context.Font.DrawCentered(sb, Loc.T("recruit.title"),
            new Rectangle(0, PostCombatTitleY + 12, availW, 24), 3, Palette.Yellow2);
        var held = _recruitChoice is not null && _recruitHold <= 0f;   // pion tenu (réserve pleine)
        Context.Font.DrawCentered(sb, Loc.T(held ? "recruit.hold_prompt" : "recruit.subtitle"),
            new Rectangle(0, PostCombatTitleY + 52, availW, 12), 1, held ? Palette.Cyan1 : Palette.Blue1);
        for (var i = 0; i < _run.Draft.Count; i++)
            DrawDraftCard(sb, _run.Draft[i], DraftCardRect(i, _run.Draft.Count, availW, viewport.Height));

        if (held)
        {
            // Pion TENU : carte choisie surlignée, autres grisées, + bouton « Abandonner » (perdre le pion).
            var ci = -1;
            for (var i = 0; i < _run.Draft.Count; i++)
                if (ReferenceEquals(_run.Draft[i], _recruitChoice))
                    ci = i;
            for (var i = 0; i < _run.Draft.Count; i++)
                if (i != ci)
                    DrawRect(sb, DraftCardRect(i, _run.Draft.Count, availW, viewport.Height), Palette.Black1 * 0.55f);
            if (ci >= 0)
                DrawRectBorder(sb, Inflate(DraftCardRect(ci, _run.Draft.Count, availW, viewport.Height), 3), Palette.Yellow2, 3);
            var ab = RecruitAbandonBtnRect(availW, viewport.Height);
            Context.Style.FillDither(sb, ab);
            DrawRectBorder(sb, ab, Palette.Purple5, 2);
            Context.Font.DrawCentered(sb, Loc.T("recruit.abandon"), ab, 1, Palette.Purple5);
        }
        // Surbrillance de la carte FOCUS (souris ou manette) — sauf si un pion est déjà choisi (vol/tenu).
        else if (_recruitChoice == null && _run.Draft.Count > 0)
        {
            var fi = System.Math.Clamp(_recruitFocus, 0, _run.Draft.Count - 1);
            var fr = DraftCardRect(fi, _run.Draft.Count, availW, viewport.Height);
            DrawRectBorder(sb, Inflate(fr, 3), Palette.Yellow2, 3);
            // Détail des traits, par-dessus la rangée. Rien sous les cartes ici → la place libre va jusqu'au
            // bas du canvas. Pas quand un pion est TENU : le bouton « Abandonner » y est dessiné.
            DrawRowKeywords(sb, KeywordRow(_run.Draft), _recruitFocus, availW, viewport.Height,
                viewport.Height - KwScreenMargin);
        }

        // Panneau RÉSERVE (à droite) = _pending, avec FUSION façon placement (empiler → pile « N/3 »).
        DrawPanelBackground(sb);
        DrawReservePanelFusion(sb);
        DrawReserveFullFlash(sb, availW, viewport.Height);   // feedback « plus de place »
        DrawDragGhost(sb);            // pion tenu (drag de fusion réserve) AU-DESSUS du panneau, dans le batch actif
        sb.End();

        if (FusionOpen) DrawFusionPopup(sb, viewport);        // choix d'évolution au CENTRE (comme au placement)
        if (EvoPlaying) DrawEvolutionAnimation(sb, viewport);

        // Pion de la carte choisie en VOL vers son emplacement de réserve (par-dessus le reste).
        if (_recruitChoice is { } choice && _recruitHold > 0f)
            DrawRecruitFlight(sb, choice, _pending.Count);   // nouveau slot = à la suite de la réserve
    }

    /// <summary>
    /// Écran de récompense « protéger » : les pions gagnés en cartes AVEC CASE À COCHER (coché = à garder,
    /// limité par la place restante), un bouton « Récupérer », et le panneau réserve éditable à droite. Les
    /// pions cochés volent en réserve ; les décochés sont abandonnés.
    /// </summary>
    private void DrawProtectReward(SpriteBatch sb, Viewport viewport, List<UnitSpec> rewards)
    {
        var availW = viewport.Width - RightPanelWidth;
        var flying = _protectRewardFlight > 0f;
        var canCollect = RewardCheckedCount() <= _run.ReserveLimit - _run.ReserveCount;

        sb.Begin(samplerState: SamplerState.PointClamp);
        var title = Loc.T("reward.title");
        var sub = Loc.T("reward.subtitle");
        var boxW = System.Math.Max(Context.Font.Measure(title, 3), Context.Font.Measure(sub, 1)) + 56;
        Context.Style.DrawPanel(sb, new Rectangle((availW - boxW) / 2, PostCombatTitleY, boxW, PostCombatTitleH));
        Context.Font.DrawCentered(sb, title, new Rectangle(0, PostCombatTitleY + 12, availW, 24), 3, Palette.Yellow2);
        if (!flying)
            Context.Font.DrawCentered(sb, sub, new Rectangle(0, PostCombatTitleY + 52, availW, 12), 1, Palette.Blue1);

        for (var i = 0; i < rewards.Count; i++)
        {
            var rect = DraftCardRect(i, rewards.Count, availW, viewport.Height);
            DrawDraftCard(sb, rewards[i], rect);
            var kept = i < _rewardKeep.Count && _rewardKeep[i];
            if (!kept)
                DrawRect(sb, rect, Palette.Black1 * 0.6f);   // décochée : grisée
            DrawCheckbox(sb, new Rectangle(rect.X + 6, rect.Y + 6, 18, 18), kept);
        }

        if (!flying)
        {
            // Manette : carte de récompense focalisée.
            if (Context.Input.UsingGamepad && !_reserveZone && rewards.Count > 0)
                DrawRectBorder(sb, Inflate(DraftCardRect(System.Math.Clamp(_rewardFocus, 0, rewards.Count - 1),
                    rewards.Count, availW, viewport.Height), 3), Palette.Cyan1, 3);

            // Détail des traits, par-dessus la rangée. La place libre s'arrête au bouton « Récupérer »
            // (posé sous les cartes) : en pratique on est donc toujours au survol sur cet écran.
            DrawRowKeywords(sb, KeywordRow(rewards), _rewardFocus, availW, viewport.Height,
                RewardCollectBtnRect(availW, viewport.Height).Y - KwScreenMargin);

            // Bouton « Récupérer (N) » : doré si collectable, rouge sinon.
            var btn = RewardCollectBtnRect(availW, viewport.Height);
            Context.Style.FillDither(sb, btn);
            DrawRectBorder(sb, btn, canCollect ? Palette.Yellow1 : Palette.Purple5, 2);
            Context.Font.DrawCentered(sb, Loc.T("reward.collect", RewardCheckedCount()), btn, 1,
                canCollect ? Palette.Yellow2 : Palette.Purple5);
        }

        DrawPanelBackground(sb);
        DrawReservePanelFusion(sb);   // réserve _pending + fusion façon placement
        DrawReserveFullFlash(sb, availW, viewport.Height);
        DrawDragGhost(sb);            // pion tenu (drag de fusion réserve) AU-DESSUS du panneau, dans le batch actif
        sb.End();

        if (FusionOpen) DrawFusionPopup(sb, viewport);        // choix d'évolution au CENTRE
        if (EvoPlaying) DrawEvolutionAnimation(sb, viewport);

        // Vol : seuls les pions COCHÉS filent vers la réserve (slots consécutifs après la réserve).
        if (flying)
        {
            var t = MathHelper.Clamp(1f - _protectRewardFlight / RecruitFlightDuration, 0f, 1f);
            var ease = t * t;
            sb.Begin(samplerState: SamplerState.PointClamp);
            var slotIdx = 0;
            for (var i = 0; i < rewards.Count; i++)
            {
                if (i >= _rewardKeep.Count || !_rewardKeep[i])
                    continue;
                var card = DraftCardRect(i, rewards.Count, availW, viewport.Height);
                var from = new Vector2(card.X + card.Width / 2f, card.Y + CardPad + 22 + 32);
                var slot = PanelCardRect(_pending.Count + slotIdx++);
                var target = new Vector2(slot.X + slot.Width / 2f, slot.Y + slot.Height / 2f);
                var pos = Vector2.Lerp(from, target, ease);
                var dest = new Rectangle((int)(pos.X - InvIconSize / 2f), (int)(pos.Y - InvIconSize / 2f),
                    InvIconSize, InvIconSize);
                if (SpriteFor(rewards[i].UnitClass, Faction.Player, front: true) is { } sprite)
                    sb.Draw(sprite, dest, Color.White);
                else
                    DrawChip(sb, rewards[i].UnitClass, Faction.Player, dest, front: true);
            }
            sb.End();
        }
    }

    /// <summary>Petite case à cocher (fond tramé + liseré ; carré plein doré à l'intérieur si cochée).</summary>
    private void DrawCheckbox(SpriteBatch sb, Rectangle r, bool on)
    {
        Context.Style.FillDither(sb, r);
        DrawRectBorder(sb, r, Palette.Yellow1, 2);
        if (on)
            DrawRect(sb, new Rectangle(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8), Palette.Yellow2);
    }

    /// <summary>Feedback « plus de place » (bandeau rouge pulsé), déclenché sur un recrutement/collecte bloqué.</summary>
    private void DrawReserveFullFlash(SpriteBatch sb, int availW, int vpH)
    {
        if (_reserveFullFlash <= 0f)
            return;
        var pulse = 0.45f + 0.55f * (float)System.Math.Abs(System.Math.Sin(_time * 18));
        var msg = Loc.T("reserve.no_room");
        var w = Context.Font.Measure(msg, 2) + 40;
        var box = new Rectangle((availW - w) / 2, 126, w, 30);
        Context.Style.FillDither(sb, box);
        DrawRectBorder(sb, box, Palette.Purple5, 2);
        Context.Font.DrawCentered(sb, msg, box, 2, Palette.Purple5 * (0.5f + 0.5f * pulse));
    }

    /// <summary>Pion de réserve en cours de DRAG de fusion (souris) : suit le curseur, au-dessus de tout.</summary>
    private void DrawReserveDrag(SpriteBatch sb)
    {
        if (_reserveDrag is not { } spec)
            return;
        var m = Context.Input.MousePosition;
        var dest = new Rectangle(m.X - InvIconSize / 2, m.Y - InvIconSize / 2, InvIconSize, InvIconSize);
        sb.Begin(samplerState: SamplerState.PointClamp);
        if (SpriteFor(spec.UnitClass, Faction.Player, front: true) is { } sprite)
            sb.Draw(sprite, dest, Color.White * 0.85f);
        else
            DrawChip(sb, spec.UnitClass, Faction.Player, dest, front: true);
        sb.End();
    }

    /// <summary>
    /// Overlay d'édition de réserve (panneau de droite) : compteur RESERVE X/8, surbrillance du pion
    /// sélectionné + boutons Supprimer/Fusionner (ou le choix des 2 évolutions). Suppose un batch OUVERT.
    /// </summary>
    private void DrawReserveEditing(SpriteBatch sb, List<UnitSpec> army)
    {
        var panel = PanelRect();
        var full = _run.IsReserveFull;
        var gp = Context.Input.UsingGamepad;

        var counter = Loc.T("reserve.count", _run.ReserveCount, _run.ReserveLimit);
        Context.Font.Draw(sb, counter,
            new Vector2(panel.Right - PanelPad - Context.Font.Measure(counter, 1), PanelListTop - 22),
            1, full ? Palette.Purple5 : Palette.Cyan1);

        var si = _reserveSel is { } sel ? army.IndexOf(sel) : -1;
        if (si >= 0)
        {
            DrawRectBorder(sb, Inflate(PanelCardRect(si), 2), Palette.Yellow2, 2);
            if (_reserveFuseChoice)
            {
                var evos = army[si].UnitClass.Evolutions;
                for (var e = 0; e < evos.Count; e++)
                    DrawReserveButton(sb, ReserveBtnRect(e), evos[e].Name, gp && e == _reserveActionFocus);
            }
            else
            {
                DrawReserveButton(sb, ReserveBtnRect(0), Loc.T("reserve.delete"), gp && _reserveActionFocus == 0);
                if (CanFuseReserve(army[si], army))
                    DrawReserveButton(sb, ReserveBtnRect(1), Loc.T("reserve.fuse"), gp && _reserveActionFocus == 1);
            }
        }
        else
        {
            // Manette : pion focalisé dans la réserve (avant sélection).
            if (gp && _reserveZone && army.Count > 0)
                DrawRectBorder(sb, Inflate(PanelCardRect(System.Math.Clamp(_reserveFocus, 0, army.Count - 1)), 2),
                    Palette.Cyan1, 2);
            if (full)
                Context.Font.DrawCentered(sb, Loc.T("reserve.full_hint"),
                    new Rectangle(panel.X + PanelPad, panel.Bottom - 68, panel.Width - 2 * PanelPad, 12),
                    1, Palette.Purple5);
        }

        // Aide manette : comment gérer la réserve (jamais à la souris — le clic est explicite).
        if (gp)
        {
            var hint = _reserveSel is not null ? Loc.T("reserve.hint_act")
                : _reserveZone ? Loc.T("reserve.hint_pick")
                : Loc.T("reserve.hint_enter");
            Context.Font.DrawCentered(sb, hint,
                new Rectangle(panel.X + PanelPad, panel.Bottom - 16, panel.Width - 2 * PanelPad, 10), 1, Palette.Blue1);
        }
    }

    /// <summary>Bouton texte du panneau de réserve (fond tramé + liseré ; focalisé = liseré cyan plus épais).</summary>
    private void DrawReserveButton(SpriteBatch sb, Rectangle r, string label, bool focused)
    {
        Context.Style.FillDither(sb, r);
        DrawRectBorder(sb, r, focused ? Palette.Cyan1 : Palette.Yellow1, focused ? 3 : 2);
        Context.Font.DrawCentered(sb, label, r, 1, Palette.Yellow2);
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

    /// <summary>
    /// Pions de la RÉSERVE NON déployés (roster hors commandant ET hors pions posés sur le plateau). Sert à
    /// la gestion de réserve PENDANT le combat (révélation de recrue) : supprimer/fusionner ceux-là ne
    /// touche pas le plateau (aucun désync). Il y en a toujours ≥1 quand la réserve est pleine (MaxDeployed &lt; ReserveLimit).
    /// </summary>
    private List<UnitSpec> ReserveUndeployed()
    {
        var deployed = new HashSet<UnitSpec>(_playerSpec.Values);
        var commander = _run.Commander;
        var reserve = new List<UnitSpec>();
        foreach (var spec in _run.Roster)
            if (spec != commander && !deployed.Contains(spec))
                reserve.Add(spec);
        return reserve;
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
        // Recrutement : portrait de FACE, PV pleins (l'unité est neuve). Le DÉTAIL des traits n'est pas
        // dessiné ici : il n'apparaît que sous la carte survolée (cf. DrawHoveredCardKeywords).
        DrawCardLayout(sb, rect, c, Faction.Player, spec.Domaine, c.MaxHp, c.MaxHp);
    }

    // ── Écrans post-combat (draft / récompense) : gabarit ───────────────────────────────────────────────
    // Tout est dérivé de la taille du CANVAS, jamais d'ordonnées fixes : il ne fait que 960×540 en
    // 1920×1080 contre 1280×720 en 1440p (agrandissement ENTIER — cf. ChessArmyGame.ConfigureVirtualScreen).
    // Le 1080p est donc le cas le plus serré, et c'est celui qu'on ne voit pas en dev. Deux bugs venaient
    // de là : les 4 cartes de récompense débordaient en largeur, et le bouton (calé sur le haut des cartes,
    // qui remontent quand le canvas rétrécit) venait se poser DANS le cadre de titre resté à un Y fixe.
    private const int PostCombatTitleY = 8;                                    // haut du cadre de titre
    private const int PostCombatTitleH = 72;                                   // titre (échelle 3) + sous-titre (échelle 1)
    private const int PostCombatGap = 8;
    private const int PostCombatBtnH = 30;
    private const int DraftCardH = 330;

    /// <summary>
    /// Haut de la rangée de cartes : centrée verticalement, mais JAMAIS sous le cadre de titre. En 1440p le
    /// centrage l'emporte (disposition inchangée) ; en 1080p c'est le titre qui borne.
    /// </summary>
    private static int DraftCardsY(int vpH) =>
        System.Math.Max(PostCombatTitleY + PostCombatTitleH + PostCombatGap, (vpH - DraftCardH) / 2 + 20);

    /// <summary>
    /// Rectangle de la carte <paramref name="index"/> parmi <paramref name="count"/>, en rangée centrée dans
    /// <paramref name="vpW"/>. La rangée RÉTRÉCIT si elle déborde (écarts d'abord, largeur de carte ensuite) :
    /// les 4 cartes de récompense d'une mission « protéger » ne tiennent pas à la largeur de référence en
    /// 1080p. Le draft (3 cartes) tient partout et garde donc sa taille. Le contenu suit : tout
    /// <see cref="DrawCardLayout"/> se cale sur le rectangle reçu.
    /// </summary>
    private static Rectangle DraftCardRect(int index, int count, int vpW, int vpH)
    {
        const int fullW = 200, fullGap = 28, minGap = 12, minW = 150, sideMargin = 16;
        var avail = vpW - 2 * sideMargin;
        int w = fullW, gap = fullGap;
        if (count > 1 && count * w + (count - 1) * gap > avail)
        {
            // Plancher minW : en dessous, le libellé d'une stat chevauche sa valeur (cf. DrawStatRow).
            // Il borne donc le nombre de cartes affichables — 4 en 1080p, la limite des maps actuelles.
            gap = minGap;
            w = System.Math.Clamp((avail - (count - 1) * gap) / count, minW, fullW);
        }
        var total = count * w + (count - 1) * gap;     // centré sur le NOMBRE réel de cartes (peut être < 3)
        var x0 = (vpW - total) / 2;
        return new Rectangle(x0 + index * (w + gap), DraftCardsY(vpH), w, DraftCardH);
    }

    /// <summary>
    /// Indice de la carte SURVOLÉE d'une rangée de <paramref name="count"/> cartes : celle sous la souris,
    /// ou celle qui a le focus <paramref name="gamepadFocus"/> à la manette. -1 si aucune.
    /// </summary>
    private int HoveredCardIndex(int count, int gamepadFocus, int rowW, int vpH)
    {
        if (count <= 0)
            return -1;
        if (Context.Input.UsingGamepad)
            return System.Math.Clamp(gamepadFocus, 0, count - 1);
        var mouse = Context.Input.MousePosition;
        for (var i = 0; i < count; i++)
            if (DraftCardRect(i, count, rowW, vpH).Contains(mouse))
                return i;
        return -1;
    }

    /// <summary>
    /// Détail des traits d'une RANGÉE de cartes (draft / récompense / fusion), à appeler APRÈS toute la
    /// rangée. Deux régimes :
    /// <list type="bullet">
    ///   <item>Toutes les piles tiennent dans la place libre sous les cartes → on les affiche TOUTES, chacune
    ///   sous la sienne : tout est comparable d'un coup d'œil (cas courant, et seul régime en 1440p).</item>
    ///   <item>Sinon → seule la carte survolée/focalisée montre sa pile, posée À CÔTÉ d'elle (à droite, ou à
    ///   gauche faute de place) : elle ne recouvre que les VOISINES, jamais la carte qu'on cherche à lire.</item>
    /// </list>
    /// Une classe peut porter 4 traits (≈240 de pile) alors qu'il ne reste que ~70 px sous les cartes en 1080p
    /// (canvas 540 dont 330 rien que pour la carte) : tout afficher y ferait remonter les piles PAR-DESSUS les
    /// cartes. Les libellés restent de toute façon lisibles en bas de chaque carte (cf. DrawCardLayout).
    /// Une entrée <c>null</c> = carte sans détail à montrer (évolution non découverte en fusion).
    /// </summary>
    /// <param name="rowW">Largeur de la zone de la rangée (borne le repli à droite/gauche).</param>
    /// <param name="bottomLimit">Ordonnée à ne pas dépasser : bas du canvas, ou haut d'un bouton posé sous les cartes.</param>
    private void DrawRowKeywords(SpriteBatch sb, IReadOnlyList<UnitClass?> cards, int gamepadFocus,
        int rowW, int vpH, int bottomLimit)
    {
        if (cards.Count == 0)
            return;
        var first = DraftCardRect(0, cards.Count, rowW, vpH);
        var below = first.Bottom + 10;

        var allFit = true;
        foreach (var c in cards)
            if (c != null && below + KeywordStackHeight(c, first.Width) > bottomLimit)
                allFit = false;

        if (allFit)
        {
            for (var i = 0; i < cards.Count; i++)
                if (cards[i] is { } c)
                    DrawKeywordPopupsBelow(sb, c, DraftCardRect(i, cards.Count, rowW, vpH));
            return;
        }

        var hi = HoveredCardIndex(cards.Count, gamepadFocus, rowW, vpH);
        if (hi < 0 || cards[hi] is not { } hovered)
            return;
        var card = DraftCardRect(hi, cards.Count, rowW, vpH);
        const int sideGap = 10;
        var x = card.Right + sideGap;
        if (x + card.Width > rowW)
            x = System.Math.Max(0, card.X - sideGap - card.Width);
        DrawKeywordPopupStack(sb, hovered, new Point(x, card.Y), card.Width);
    }

    /// <summary>Les classes d'une rangée de cartes, pour <see cref="DrawRowKeywords"/>.</summary>
    private static List<UnitClass?> KeywordRow(IReadOnlyList<UnitSpec> specs)
    {
        var row = new List<UnitClass?>(specs.Count);
        foreach (var s in specs)
            row.Add(s.UnitClass);
        return row;
    }

    /// <summary>
    /// Récap de FIN DE RUN (victoire ou défaite), dessiné dans le batch de l'appelant sur le plateau figé et
    /// voilé : en-tête (résultat, commandant, difficulté, combat atteint), BILAN (tués/perdus/dégâts/fusions/…),
    /// dégâts par CLASSE (barres, top 6), MVP survivant, et déblocages de la run le cas échéant. Un clic / A
    /// ramène au menu (le slot est déjà effacé). Données lues sur <see cref="_run"/> + <see cref="Run.Stats"/>.
    /// </summary>
    private void DrawRunRecap(SpriteBatch sb, Viewport viewport)
    {
        var victory = _run.Phase == RunPhase.Victory;
        var title = victory ? Loc.T("end.victory") : Loc.T("end.defeat");

        var cmdName = Loc.TOr("commander." + _run.CommanderDef.Id, _run.CommanderDef.Name);
        var diff = Loc.T("difficulty." + _run.Difficulty.ToString().ToLowerInvariant());
        var sub = $"{cmdName}  -  {diff}  -  {Loc.T("recap.run_combat", _run.CombatNumber, Run.TotalCombats)}";
        var reason = victory ? "" : _defeatReason;

        var bilan = new List<(string Label, string Value)>
        {
            (Loc.T("recap.run_kills"), _run.Stats.TotalKills.ToString()),
            (Loc.T("recap.run_lost"), _run.Stats.UnitsLost.ToString()),
            (Loc.T("recap.run_damage_total"), _run.Stats.TotalDamage.ToString()),
            (Loc.T("recap.run_fusions"), _run.Stats.Fusions.ToString()),
            (Loc.T("recap.run_time"), TimeText.Duration(_run.Stats.PlayTimeSeconds)),
        };
        if (_run.Stats.PaysansSaved > 0) bilan.Add((Loc.T("recap.run_paysans"), _run.Stats.PaysansSaved.ToString()));
        if (_run.Stats.EquipmentFound > 0) bilan.Add((Loc.T("recap.run_equipment"), _run.Stats.EquipmentFound.ToString()));

        var dmg = _run.Stats.TopDamage(6);
        var maxDmg = dmg.Count > 0 ? System.Math.Max(1, dmg[0].Damage) : 1;

        // MVP : pion SURVIVANT avec le plus de kills (à vie ; ignoré si personne n'a tué).
        string? mvp = null;
        var best = _run.Roster.Where(u => u.Kills > 0).OrderByDescending(u => u.Kills).FirstOrDefault();
        if (best != null)
            mvp = Loc.T("recap.run_mvp", best.UnitClass.Name, best.Kills);

        // DÉBLOCAGES : pour l'instant seuls les COMMANDANTS débloqués y figurent (les classes/équipements
        // découverts restent collectés dans RunStats mais ne s'affichent pas ici).
        var unlocks = _run.Stats.UnlockedCommanders.ToList();

        var prompt = Loc.T(Context.Input.UsingGamepad ? "recap.run_back_gp" : "recap.run_back");

        var hasUnlocks = unlocks.Count > 0;
        var bilanHead = Loc.T("recap.run_bilan");
        var dmgHead = Loc.T("recap.run_damage");
        var deblocHead = Loc.T("recap.run_unlocks");
        var deblocSub = Loc.T("recap.run_unlock_sub");

        // ── Mesures ──────────────────────────────────────────────────────────
        const int SecGap = 12, BarW = 130, ColGap = 24, ItemGap = 8, MidGap = 44, RowH = 11, HeadH = 14,
                  DashH = 2, ChipPadH = 16, ChipPadV = 9;
        int Meas(string t, int s) => Context.Font.Measure(t, s);

        // Colonne GAUCHE (BILAN : libellé + valeur).
        int biLabel = 0, biValue = 0;
        foreach (var (l, v) in bilan) { biLabel = System.Math.Max(biLabel, Meas(l, 1)); biValue = System.Math.Max(biValue, Meas(v, 1)); }
        var leftColW = System.Math.Max(Meas(bilanHead, 2), biLabel + ColGap + biValue);

        // Colonne DROITE (DÉGÂTS : nom + barre + valeur).
        int dLabel = 0, dValue = 0;
        foreach (var (c, d) in dmg) { dLabel = System.Math.Max(dLabel, Meas(c, 1)); dValue = System.Math.Max(dValue, Meas(d.ToString(), 1)); }
        var rightColW = System.Math.Max(Meas(dmgHead, 2), dmg.Count > 0 ? dLabel + ItemGap + BarW + ItemGap + dValue : 0);

        var columnsW = leftColW + MidGap + rightColW;
        var rowsH = System.Math.Max(bilan.Count, dmg.Count) * RowH;
        var columnsBlockH = HeadH + 6 + rowsH;

        var chipW = hasUnlocks ? System.Math.Max(Meas(unlocks[0], 1), Meas(deblocSub, 1)) + 2 * ChipPadH : 0;
        var chipH = 7 + 6 + 7 + 2 * ChipPadV;

        var contentW = new[]
        {
            columnsW, Meas(title, 3), Meas(sub, 1), Meas(reason, 1), Meas(prompt, 1),
            mvp != null ? Meas(mvp, 1) : 0, hasUnlocks ? Meas(deblocHead, 2) : 0, hasUnlocks ? chipW : 0,
        }.Max();
        var boxW = contentW + 2 * ModalPadH;

        // ── Hauteur (mêmes incréments que le tracé) ────────────────────────────
        var boxH = ModalPadV + 21 + SecGap + 7;                     // titre + sous-titre
        if (!victory) boxH += 4 + 7;                                // raison de défaite
        boxH += SecGap + DashH + SecGap;                            // filet haut
        boxH += columnsBlockH;                                      // colonnes BILAN | DÉGÂTS
        boxH += SecGap + DashH + SecGap;                            // filet bas colonnes
        if (hasUnlocks) boxH += HeadH + 6 + chipH + SecGap + DashH + SecGap;   // déblocages + filet
        if (mvp != null) boxH += 7 + SecGap;                        // MVP
        boxH += 7 + ModalPadV;                                      // prompt + marge basse

        var box = new Rectangle((viewport.Width - boxW) / 2, (viewport.Height - boxH) / 2, boxW, boxH);
        Context.Style.DrawPanel(sb, box);

        var innerX = box.X + ModalPadH;
        var innerW = box.Width - 2 * ModalPadH;

        // En-tête.
        var y = box.Y + ModalPadV;
        Context.Font.DrawCentered(sb, title, new Rectangle(box.X, y, box.Width, 21), 3, victory ? Palette.Yellow2 : Palette.Purple5);
        y += 21 + SecGap;
        Context.Font.DrawCentered(sb, sub, new Rectangle(box.X, y, box.Width, 7), 1, Palette.Blue1);
        y += 7;
        if (!victory)
        {
            y += 4;
            Context.Font.DrawCentered(sb, reason, new Rectangle(box.X, y, box.Width, 7), 1, Palette.Purple5);
            y += 7;
        }

        // Filet pointillé, puis les deux colonnes séparées par un filet vertical.
        y += SecGap;
        DrawDashedH(sb, innerX, y, innerW, DashColor);
        y += DashH + SecGap;

        var colsX = box.X + (box.Width - columnsW) / 2;
        var rightX = colsX + leftColW + MidGap;
        var colsTop = y;
        Context.Font.DrawCentered(sb, bilanHead, new Rectangle(colsX, y, leftColW, HeadH), 2, Palette.Yellow2);
        Context.Font.DrawCentered(sb, dmgHead, new Rectangle(rightX, y, rightColW, HeadH), 2, Palette.Yellow2);
        var rowY = y + HeadH + 6;

        var ly = rowY;
        foreach (var (l, v) in bilan)
        {
            Context.Font.Draw(sb, l, new Vector2(colsX, ly), 1, Palette.White);
            Context.Font.Draw(sb, v, new Vector2(colsX + leftColW - Meas(v, 1), ly), 1, Palette.Yellow1);
            ly += RowH;
        }
        var ry = rowY;
        foreach (var (c, d) in dmg)
        {
            Context.Font.Draw(sb, c, new Vector2(rightX, ry), 1, Palette.White);
            var barX = rightX + dLabel + ItemGap;
            DrawRect(sb, new Rectangle(barX, ry, BarW, 6), Palette.Blue1 * 0.30f);
            DrawRect(sb, new Rectangle(barX, ry, System.Math.Max(1, BarW * d / maxDmg), 6), Palette.Yellow1);
            Context.Font.Draw(sb, d.ToString(), new Vector2(barX + BarW + ItemGap, ry), 1, Palette.Yellow1);
            ry += RowH;
        }
        DrawDashedV(sb, colsX + leftColW + MidGap / 2, colsTop, columnsBlockH, DashColor);
        y = colsTop + columnsBlockH;

        y += SecGap;
        DrawDashedH(sb, innerX, y, innerW, DashColor);
        y += DashH + SecGap;

        // Déblocages : le(s) commandant(s) débloqué(s), en puce (nom + sous-titre).
        if (hasUnlocks)
        {
            Context.Font.DrawCentered(sb, deblocHead, new Rectangle(box.X, y, box.Width, HeadH), 2, Palette.Yellow2);
            y += HeadH + 6;
            var cx = box.X + (box.Width - chipW) / 2;
            var chip = new Rectangle(cx, y, chipW, chipH);
            DrawRect(sb, chip, Palette.Blue1 * 0.14f);
            DrawBorderRect(sb, chip, Palette.Blue1 * 0.55f);
            Context.Font.DrawCentered(sb, unlocks[0], new Rectangle(cx, y + ChipPadV, chipW, 7), 1, Palette.Yellow2);
            Context.Font.DrawCentered(sb, deblocSub, new Rectangle(cx, y + ChipPadV + 13, chipW, 7), 1, Palette.Blue1);
            y += chipH;
            y += SecGap;
            DrawDashedH(sb, innerX, y, innerW, DashColor);
            y += DashH + SecGap;
        }

        // MVP.
        if (mvp != null)
        {
            Context.Font.DrawCentered(sb, mvp, new Rectangle(box.X, y, box.Width, 7), 1, Palette.Cyan1);
            y += 7 + SecGap;
        }

        // Invite pulsée → retour menu.
        var a = 0.5f + 0.5f * MathF.Abs(MathF.Sin(_time * 3f));
        Context.Font.DrawCentered(sb, prompt, new Rectangle(box.X, y, box.Width, 7), 1, Palette.Cyan1 * a);
    }

    /// <summary>Couleur des filets pointillés du récap de fin de run.</summary>
    private static Color DashColor => Palette.Blue1 * 0.5f;

    /// <summary>Filet pointillé HORIZONTAL (tirets de 6 px, espacés de 5, épais de 2).</summary>
    private void DrawDashedH(SpriteBatch sb, int x, int y, int width, Color color)
    {
        for (var dx = 0; dx < width; dx += 11)
            DrawRect(sb, new Rectangle(x + dx, y, System.Math.Min(6, width - dx), 2), color);
    }

    /// <summary>Filet pointillé VERTICAL (tirets de 6 px, espacés de 5, épais de 2).</summary>
    private void DrawDashedV(SpriteBatch sb, int x, int y, int height, Color color)
    {
        for (var dy = 0; dy < height; dy += 11)
            DrawRect(sb, new Rectangle(x, y + dy, 2, System.Math.Min(6, height - dy)), color);
    }

    /// <summary>Cadre 1 px autour d'un rectangle (quatre côtés).</summary>
    private void DrawBorderRect(SpriteBatch sb, Rectangle r, Color color)
    {
        DrawRect(sb, new Rectangle(r.X, r.Y, r.Width, 1), color);
        DrawRect(sb, new Rectangle(r.X, r.Bottom - 1, r.Width, 1), color);
        DrawRect(sb, new Rectangle(r.X, r.Y, 1, r.Height), color);
        DrawRect(sb, new Rectangle(r.Right - 1, r.Y, 1, r.Height), color);
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

    /// <summary>
    /// HUD d'objectif de la mission spéciale, sous la frise : paysans libérés X/N et tours restants. Le
    /// compteur de tours passe en alerte dans les 3 derniers rounds. Centré dans la zone du plateau.
    /// </summary>
    private void DrawSpecialObjective(SpriteBatch sb, Viewport viewport)
    {
        var railW = (int)CenteringWidth();   // même centrage que le plateau/la frise (suit le départ du panneau)
        string line1, line2;
        Color line2Color;
        if (IsSauverMission)
        {
            // COURSE : récupérés (haut, positif) vs capturés par l'IA (bas, danger). Aucun compteur de tours.
            line1 = Loc.T("special.saved", PaysansFreed, PaysansTotal);
            line2 = Loc.T("special.captured", PaysansCaptured, PaysansTotal);
            line2Color = PaysansCaptured > 0 ? Palette.Purple5 : Palette.Yellow1;
        }
        else
        {
            // Proteger : paysans encore PROTÉGÉS X/N ; Liberer : paysans LIBÉRÉS X/N ; ligne 2 = tours restants.
            line1 = IsProtectMission
                ? Loc.T("special.protected", PaysansProtected, PaysansTotal)
                : Loc.T("special.paysans", PaysansResolved, PaysansTotal);
            line2 = Loc.T("special.rounds", System.Math.Max(0, _specialRoundsLeft));
            line2Color = _specialRoundsLeft <= 3 ? Palette.Purple5 : Palette.Yellow1;   // alerte fin de temps
        }
        var textW = System.Math.Max(Context.Font.Measure(line1, 1), Context.Font.Measure(line2, 1));
        var box = new Rectangle((railW - ((int)textW + 28)) / 2, 78, (int)textW + 28, 40);

        sb.Begin(samplerState: SamplerState.PointClamp);
        Context.Style.FillDither(sb, box);
        DrawRectBorder(sb, box, Palette.Navy1, 2);
        Context.Font.DrawCentered(sb, line1, new Rectangle(box.X, box.Y + 6, box.Width, 12), 1, Palette.Cyan1);
        Context.Font.DrawCentered(sb, line2, new Rectangle(box.X, box.Y + 22, box.Width, 12), 1, line2Color);
        sb.End();
    }

    /// <summary>
    /// Encart de BRIEFING de la mission spéciale, sous la frise, pendant le PLACEMENT : rappelle l'objectif
    /// (libérer le maximum de paysans dans la limite de tours). Centré dans la zone du plateau. Texte replié
    /// pour tenir dans le cadre.
    /// </summary>
    private void DrawSpecialBriefing(SpriteBatch sb, Viewport viewport)
    {
        const int innerW = 360;   // largeur cible du texte (le rail 1280-240 est bien plus large)
        var goalKey = IsProtectMission ? "special.brief_protect"
                    : IsSauverMission ? "special.brief_save"   // course, sans référence au nombre de tours
                    : "special.brief_goal";
        var body = WrapText(Loc.T(goalKey, SpecialTurnBudget()), innerW, 1);
        DrawBriefingBox(sb, Loc.T("mission.speciale"), body);
    }

    /// <summary>
    /// Encart de BRIEFING du combat de BOSS, sous la frise, pendant le PLACEMENT : rappelle la CONDITION
    /// DE VICTOIRE (vaincre le boss suffit à gagner). Même cadre que le briefing de mission spéciale.
    /// </summary>
    private void DrawBossBriefing(SpriteBatch sb, Viewport viewport)
    {
        const int innerW = 360;
        var body = WrapText(Loc.T("boss.brief_goal"), innerW, 1);
        DrawBriefingBox(sb, Loc.T("combat.boss"), body);
    }

    /// <summary>
    /// Cadre de briefing (titre doré + texte crème replié) centré sous la frise, à la largeur du plateau.
    /// Partagé par les briefings de mission spéciale (<see cref="DrawSpecialBriefing"/>) et de boss
    /// (<see cref="DrawBossBriefing"/>).
    /// </summary>
    private void DrawBriefingBox(SpriteBatch sb, string title, IReadOnlyList<string> body)
    {
        var textW = Context.Font.Measure(title, 1);
        foreach (var l in body)
            textW = System.Math.Max(textW, Context.Font.Measure(l, 1));

        const int padH = 12, padV = 7, titleH = 11, lineH = 10;
        var railW = (int)CenteringWidth();
        var boxW = textW + 2 * padH;
        var boxH = padV + titleH + body.Count * lineH + padV;
        var box = new Rectangle((railW - boxW) / 2, 78, boxW, boxH);

        sb.Begin(samplerState: SamplerState.PointClamp);
        Context.Style.FillDither(sb, box);
        DrawRectBorder(sb, box, Palette.Yellow1, 2);
        var y = box.Y + padV;
        Context.Font.DrawCentered(sb, title, new Rectangle(box.X, y, box.Width, 7), 1, Palette.Yellow2);
        y += titleH;
        foreach (var l in body)
        {
            Context.Font.DrawCentered(sb, l, new Rectangle(box.X, y, box.Width, 7), 1, Palette.White);
            y += lineH;
        }
        sb.End();
    }

    // ── Modales de mission spéciale (briefing d'ouverture / bilan de clôture) ───
    private const int ModalPadH = 28;    // marge gauche/droite du cadre
    private const int ModalPadV = 20;    // marge haut/bas du cadre
    private const int ModalLineH = 12;   // pas vertical d'une ligne de texte (échelle 1)
    private const int ModalGap = 14;     // respiration entre deux blocs

    /// <summary>
    /// Modale d'OUVERTURE d'une mission spéciale, au début de la préparation : ce qu'il faut FAIRE (les
    /// paysans et leurs cases « ? ») puis les règles qui la distinguent d'une escarmouche (limite de tours
    /// non fatale, seule la chute du commandant fait perdre). Gèle le placement jusqu'au clic / A
    /// (cf. <see cref="UpdatePlacement"/>) ; une fois fermée, <see cref="DrawSpecialBriefing"/> en garde le
    /// rappel d'une ligne sous la frise.
    /// </summary>
    private void DrawSpecialBriefingModal(SpriteBatch sb, Viewport viewport)
    {
        const int innerW = 420;   // largeur cible du texte replié
        var title = Loc.T(IsProtectMission ? "special.title_protect"
                        : IsSauverMission ? "special.title_save"
                        : "special.title_liberate");
        var body = WrapText(Loc.T(IsProtectMission ? "special.desc_protect"
                                : IsSauverMission ? "special.desc_save"
                                : "special.desc_liberate"), innerW, 1);
        // Le QUOTA change la nature de la mission : sans lui le temps écoulé ne fait que clore, avec lui il
        // peut faire perdre. Les règles finales sont donc formulées différemment selon qu'il existe — et en
        // ROUGE, la couleur du danger dans tout le jeu, parce que ce sont elles qui font perdre la run.
        var quota = PaysansRequired;
        // Deux blocs : le CONTEXTE (course / limite de tours) en haut, puis les CONDITIONS DE DÉFAITE isolées
        // sous un sous-titre. Chaque condition qui FAIT PERDRE est une ligne à part : rater le quota (formulé
        // « moins de N ») et, toujours, la mort du commandant.
        var context = new List<(string Text, Color Color)>();
        var defeat = new List<(string Text, Color Color)>();
        if (IsSauverMission)
        {
            // COURSE sans limite de tours : on annonce la course et l'absence de limite.
            context.Add(("- " + Loc.T("special.rule_race"), Palette.Cyan1));
            context.Add(("- " + Loc.T("special.rule_no_limit"), Palette.Cyan1));
        }
        else
        {
            context.Add(("- " + Loc.T("special.rule_turns", SpecialTurnBudget()), Palette.Cyan1));
            // Sans quota, la fin du chrono ne fait PAS perdre : on le précise pour lever l'ambiguïté.
            if (quota == 0)
                context.Add(("- " + Loc.T("special.rule_timeout"), Palette.Cyan1));
        }
        if (quota > 0)
        {
            var quotaKey = IsSauverMission ? "special.defeat_quota_save"
                         : IsProtectMission ? "special.defeat_quota_protect"
                         : "special.defeat_quota";
            defeat.Add(("- " + Loc.T(quotaKey, quota), Palette.Purple5));
        }
        defeat.Add(("- " + Loc.T("special.defeat_commander"), Palette.Purple5));
        var defeatHeading = Loc.T("special.defeat_heading");
        var prompt = Loc.T(Context.Input.UsingGamepad ? "special.brief_continue_gp" : "special.brief_continue");

        var textW = System.Math.Max(Context.Font.Measure(title, 2), Context.Font.Measure(prompt, 1));
        foreach (var l in body)
            textW = System.Math.Max(textW, Context.Font.Measure(l, 1));
        foreach (var (text, _) in context)
            textW = System.Math.Max(textW, Context.Font.Measure(text, 1));
        foreach (var (text, _) in defeat)
            textW = System.Math.Max(textW, Context.Font.Measure(text, 1));
        if (defeat.Count > 0)
            textW = System.Math.Max(textW, Context.Font.Measure(defeatHeading, 1));

        var boxW = textW + 2 * ModalPadH;
        // Lignes de règles = contexte + défaite + le sous-titre (si présent) ; gaps = body→contexte,
        // contexte→sous-titre (si défaite) et règles→invite.
        var ruleLines = context.Count + defeat.Count + (defeat.Count > 0 ? 1 : 0);
        var ruleGaps = defeat.Count > 0 ? 3 : 2;
        var boxH = ModalPadV + 7 + ModalGap + 14 + ModalGap + (body.Count + ruleLines) * ModalLineH
                   + ruleGaps * ModalGap + 7 + ModalPadV;
        var box = new Rectangle((viewport.Width - boxW) / 2, (viewport.Height - boxH) / 2, boxW, boxH);

        sb.Begin(samplerState: SamplerState.PointClamp);
        DrawDim(sb, viewport);   // voile du canvas ; les bandes du letterbox le sont via FullScreenDim
        Context.Style.DrawPanel(sb, box);

        var y = box.Y + ModalPadV;
        Context.Font.DrawCentered(sb, Loc.T("mission.speciale"), new Rectangle(box.X, y, box.Width, 7), 1, Palette.Blue1);
        y += 7 + ModalGap;
        Context.Font.DrawCentered(sb, title, new Rectangle(box.X, y, box.Width, 14), 2, Palette.Yellow2);
        y += 14 + ModalGap;
        // Corps et règles alignés à GAUCHE : un pavé centré se lit mal sur plusieurs lignes. Rendus en CASSE
        // DE PHRASE (preserveCase) : ce sont des phrases, pas des libellés — minuscules avec majuscule initiale.
        var x = box.X + ModalPadH;
        foreach (var l in body)
        {
            Context.Font.Draw(sb, l, new Vector2(x, y), 1, Palette.White, preserveCase: true);
            y += ModalLineH;
        }
        y += ModalGap;
        foreach (var (text, color) in context)
        {
            Context.Font.Draw(sb, text, new Vector2(x, y), 1, color, preserveCase: true);
            y += ModalLineH;
        }
        if (defeat.Count > 0)
        {
            y += ModalGap;   // respiration pour bien détacher le bloc « défaite »
            Context.Font.Draw(sb, defeatHeading, new Vector2(x, y), 1, Palette.Purple5);   // sous-titre en rouge (danger)
            y += ModalLineH;
            foreach (var (text, color) in defeat)
            {
                Context.Font.Draw(sb, text, new Vector2(x, y), 1, color, preserveCase: true);
                y += ModalLineH;
            }
        }
        y += ModalGap;
        var a = 0.5f + 0.5f * MathF.Abs(MathF.Sin(_time * 3f));   // invite pulsée, comme la fin d'évolution
        Context.Font.DrawCentered(sb, prompt, new Rectangle(box.X, y, box.Width, 7), 1, Palette.Cyan1 * a);
        sb.End();
    }

    /// <summary>
    /// Modale de BILAN à la clôture d'une mission spéciale : le résultat de l'objectif (paysans X/N,
    /// tours consommés, pertes) figé par <see cref="CheckBattleEnd"/>. Gèle la phase de récupération des
    /// pions jusqu'au clic / A (cf. <see cref="UpdateRecruitment"/>). Le plateau et son voile sont déjà
    /// dessinés par l'appelant : pas de second voile ici (sinon décalage avec les bandes du letterbox).
    /// </summary>
    private void DrawSpecialRecap(SpriteBatch sb, Viewport viewport, SpecialRecap recap)
    {
        var protect = recap.Objective == SpecialObjective.ProtegerPaysans;
        var sauver = recap.Objective == SpecialObjective.SauverPaysans;
        var title = Loc.T("recap.title");
        var sub = Loc.T(protect ? "special.title_protect"
                      : sauver ? "special.title_save"
                      : "special.title_liberate");
        var prompt = Loc.T(Context.Input.UsingGamepad ? "recap.continue_gp" : "recap.continue");
        var rows = new List<(string Label, string Value)>
        {
            (Loc.T(protect ? "recap.paysans_saved"
                 : sauver ? "recap.paysans_rescued"
                 : "recap.paysans_freed"), $"{recap.Paysans} / {recap.PaysansTotal}"),
        };
        // Quota de difficulté : rappelé seulement s'il y en a un (aucun en facile).
        if (recap.Required > 0)
            rows.Add((Loc.T("recap.required"), recap.Required.ToString()));
        if (!sauver)   // « sauver » = course sans limite de tours : pas de ligne « tours utilisés »
            rows.Add((Loc.T("recap.turns"), $"{recap.Turns} / {recap.TurnBudget}"));
        rows.Add((Loc.T("recap.losses"), recap.Losses.ToString()));

        const int colGap = 40;   // écart mini entre le libellé et sa valeur (colonnes label/valeur)
        int labelW = 0, valueW = 0;
        foreach (var (label, value) in rows)
        {
            labelW = System.Math.Max(labelW, Context.Font.Measure(label, 1));
            valueW = System.Math.Max(valueW, Context.Font.Measure(value, 1));
        }
        var tableW = labelW + colGap + valueW;
        var textW = System.Math.Max(System.Math.Max(Context.Font.Measure(title, 3), Context.Font.Measure(sub, 1)),
            System.Math.Max(tableW, Context.Font.Measure(prompt, 1)));

        var boxW = textW + 2 * ModalPadH;
        var boxH = ModalPadV + 21 + ModalGap + 7 + ModalGap + rows.Count * ModalLineH + ModalGap + 7 + ModalPadV;
        var box = new Rectangle((viewport.Width - boxW) / 2, (viewport.Height - boxH) / 2, boxW, boxH);

        sb.Begin(samplerState: SamplerState.PointClamp);
        Context.Style.DrawPanel(sb, box);

        var y = box.Y + ModalPadV;
        Context.Font.DrawCentered(sb, title, new Rectangle(box.X, y, box.Width, 21), 3, Palette.Yellow2);
        y += 21 + ModalGap;
        Context.Font.DrawCentered(sb, sub, new Rectangle(box.X, y, box.Width, 7), 1, Palette.Blue1);
        y += 7 + ModalGap;
        // Libellés à gauche, valeurs alignées à droite : les chiffres se comparent en colonne.
        var tx = box.X + (box.Width - tableW) / 2;
        foreach (var (label, value) in rows)
        {
            Context.Font.Draw(sb, label, new Vector2(tx, y), 1, Palette.White);
            Context.Font.Draw(sb, value, new Vector2(tx + tableW - Context.Font.Measure(value, 1), y), 1, Palette.Yellow1);
            y += ModalLineH;
        }
        y += ModalGap;
        var a = 0.5f + 0.5f * MathF.Abs(MathF.Sin(_time * 3f));
        Context.Font.DrawCentered(sb, prompt, new Rectangle(box.X, y, box.Width, 7), 1, Palette.Cyan1 * a);
        sb.End();
    }

    // ── Frise chronologique de la phase (HUD haut) ──────────────────────────────
    private const int TimelineIconSize = 32;   // icône de mission (PNG Assets/Icons/mission_*.png = 32×32)
    private const int TimelineNodeSize = 40;   // côté d'un nœud (icône 32 + marge)
    private const int TimelineGap = 30;         // espace entre deux nœuds
    private const int TimelineTopY = 24;        // haut des nœuds (le libellé est au-dessus)

    /// <summary>
    /// Frise en haut de l'écran : les <see cref="Run.MissionsPerPhase"/> missions de la PHASE courante,
    /// une icône par nature (escarmouche / spéciale / boss). Avancement lisible : missions passées =
    /// liseré vert + connecteur doré ; mission en cours = liseré doré pulsé ; à venir = sombre. Overlay
    /// dessiné au-dessus du plateau, centré dans la zone à gauche du panneau (jamais sous lui). Masquée
    /// en tutoriel (ce n'est pas une vraie phase de campagne).
    /// </summary>
    private void DrawPhaseTimeline(SpriteBatch sb, Viewport viewport)
    {
        if (_tutorial != null)
            return;

        const int count = Run.MissionsPerPhase;
        const int pitch = TimelineNodeSize + TimelineGap;
        var contentW = count * TimelineNodeSize + (count - 1) * TimelineGap;
        // Même largeur de centrage que le PLATEAU (cf. CenteringWidth) : à gauche du panneau en placement,
        // plein écran en combat (animée pendant le glissement d'entrée) → la frise suit le plateau au lieu
        // de rester décalée quand le panneau part.
        var railW = (int)CenteringWidth();
        var startX = (railW - contentW) / 2;
        var centerY = TimelineTopY + TimelineNodeSize / 2;
        var current = _run.MissionInPhase;                          // 1..6

        sb.Begin(samplerState: SamplerState.PointClamp);

        // Fond tramé pixel-art (style maison des panneaux) pour détacher la frise du plateau.
        var bg = new Rectangle(startX - 14, 6, contentW + 28, TimelineTopY + TimelineNodeSize + 2);
        Context.Style.FillDither(sb, bg);
        DrawRectBorder(sb, bg, Palette.Navy1, 2);

        // Libellé « PHASE n/N » centré au-dessus des nœuds (N = phase de fin, cf. Run.EndAtPhase).
        Context.Font.DrawCentered(sb, Loc.T("hud.phase", _run.PhaseIndex, Run.EndAtPhase),
            new Rectangle(startX, TimelineTopY - 16, contentW, 12), 1, Palette.Yellow1);

        // Connecteurs (derrière les nœuds) : segment i→i+1 doré s'il est franchi, sombre sinon.
        for (var i = 0; i < count - 1; i++)
        {
            var x0 = startX + i * pitch + TimelineNodeSize;
            var color = (i + 1) < current ? Palette.Yellow2 : Palette.Navy1;
            DrawRect(sb, new Rectangle(x0, centerY - 1, TimelineGap, 2), color);
        }

        // Nœuds.
        for (var i = 0; i < count; i++)
        {
            var mission = i + 1;
            var type = Run.MissionKindAt(_run.PhaseIndex, mission);
            var area = new Rectangle(startX + i * pitch, TimelineTopY, TimelineNodeSize, TimelineNodeSize);
            var past = mission < current;

            DrawRect(sb, Inflate(area, 1), Palette.Black1);   // contour sombre
            DrawRect(sb, area, Palette.Navy2);                // fond sombre du nœud
            DrawMissionIcon(sb, type, CenteredSquare(area, TimelineIconSize), dim: past);

            if (mission == current)
            {
                var pulse = 1 + (int)System.Math.Round((System.Math.Sin(_time * 6) + 1) * 0.5);   // 1..2 px
                DrawRectBorder(sb, Inflate(area, pulse), Palette.Yellow2, 2);   // en cours : liseré doré pulsé
            }
            else if (past)
                DrawRectBorder(sb, area, Palette.Green1, 2);   // fait
            else
                DrawRectBorder(sb, area, Palette.Navy1, 1);    // à venir
        }

        // Tooltip au survol souris : nature de la mission + effectif ennemi (escortes + boss éventuel).
        if (!Context.Input.UsingGamepad)
        {
            var mouse = Context.Input.MousePosition;
            for (var i = 0; i < count; i++)
            {
                var area = new Rectangle(startX + i * pitch, TimelineTopY, TimelineNodeSize, TimelineNodeSize);
                if (!area.Contains(mouse))
                    continue;
                DrawMissionTooltip(sb, area, Run.MissionKindAt(_run.PhaseIndex, i + 1), TimelineEnemyCount(_run.PhaseIndex, i + 1));
                break;
            }
        }

        sb.End();
    }

    /// <summary>Carré de côté <paramref name="size"/> centré dans <paramref name="area"/>.</summary>
    private static Rectangle CenteredSquare(Rectangle area, int size) =>
        new(area.X + (area.Width - size) / 2, area.Y + (area.Height - size) / 2, size, size);

    /// <summary>
    /// Effectif ennemi affiché dans la frise pour la mission (phase, rang). MISSION SPÉCIALE : nb de spawns
    /// de la map tirée (cf. <see cref="Run.BuildSpecialEnemyWave"/>). BOSS avec map dessinée : nb total de
    /// cases de spawn ennemies (boss + escortes, cf. <see cref="Run.BuildBossEnemyWave"/>). Sinon repli sur
    /// la table (<see cref="Run.EnemyCount"/>) — escarmouche, ou spéciale/boss retombant sur du terrain aléatoire.
    /// </summary>
    private int TimelineEnemyCount(int phaseIndex, int missionInPhase)
    {
        var kind = Run.MissionKindAt(phaseIndex, missionInPhase);
        if (kind == CombatType.Speciale
            && SpecialMapFor(phaseIndex, missionInPhase) is { Type: CombatType.Speciale } sp)
            return sp.EnemySpawns.Count;
        if (kind == CombatType.Boss
            && BossMapFor(phaseIndex, missionInPhase) is { } bm)
            return bm.BossSpawns.Count + bm.EnemySpawns.Count;
        return Run.EnemyCount(phaseIndex, missionInPhase);
    }

    /// <summary>
    /// Bulle d'info d'un nœud de frise, sous le nœud : nom de la mission + « N ENNEMIS ». Bornée à la
    /// zone du plateau (jamais sous le panneau). Style tramé + liseré doré, comme les autres infobulles.
    /// </summary>
    private void DrawMissionTooltip(SpriteBatch sb, Rectangle node, CombatType type, int enemies)
    {
        var title = type switch
        {
            CombatType.Boss => Loc.T("mission.boss"),
            CombatType.Speciale => Loc.T("mission.speciale"),
            _ => Loc.T("mission.escarmouche"),
        };
        var sub = Loc.T("hud.enemies", enemies);

        var w = System.Math.Max(Context.Font.Measure(title, 1), Context.Font.Measure(sub, 1)) + 16;
        const int h = 32;
        var railRight = VirtualViewport.Width - RightPanelWidth;
        var x = System.Math.Clamp(node.Center.X - w / 2, 4, railRight - w - 4);
        var box = new Rectangle(x, node.Bottom + 8, w, h);

        Context.Style.FillDither(sb, box);
        DrawRectBorder(sb, box, Palette.Yellow1, 2);
        Context.Font.DrawCentered(sb, title, new Rectangle(box.X, box.Y + 6, box.Width, 8), 1, Palette.Yellow2);
        Context.Font.DrawCentered(sb, sub, new Rectangle(box.X, box.Y + 18, box.Width, 8), 1, Palette.White);
    }

    /// <summary>
    /// Icône d'une nature de mission dans un nœud de frise. PNG d'art <c>Assets/Icons/mission_&lt;type&gt;.png</c>
    /// si présent, sinon placeholder procédural distinct par type (escarmouche = épées croisées bleues,
    /// spéciale = étincelle dorée, boss = gemme rouge). <paramref name="dim"/> atténue les missions passées.
    /// </summary>
    private void DrawMissionIcon(SpriteBatch sb, CombatType type, Rectangle area, bool dim)
    {
        var alpha = dim ? 0.45f : 1f;

        if (IconOrNull($"mission_{type}".ToLowerInvariant()) is { } png)
        {
            DrawSpriteFit(sb, png, area);
            if (dim)
                DrawRect(sb, area, Palette.Black1 * 0.5f);   // voile pour les missions passées (PNG non teinté)
            return;
        }

        var center = new Vector2(area.Center.X, area.Center.Y);
        switch (type)
        {
            case CombatType.Boss:   // gemme (losange plein)
                var d = area.Width * 0.64f;
                sb.Draw(Context.Pixel, center, null, Palette.Purple5 * alpha, MathHelper.PiOver4,
                    new Vector2(0.5f, 0.5f), new Vector2(d, d), SpriteEffects.None, 0f);
                break;
            case CombatType.Speciale:   // étincelle (croix + diagonales)
                var s = Palette.Yellow2 * alpha;
                var l = area.Width * 0.95f;
                DrawBar(sb, center, l, 3, 0f, s);
                DrawBar(sb, center, l, 3, MathHelper.PiOver2, s);
                DrawBar(sb, center, l * 0.7f, 2, MathHelper.PiOver4, s);
                DrawBar(sb, center, l * 0.7f, 2, -MathHelper.PiOver4, s);
                break;
            default:   // Escarmouche : épées croisées (X)
                var e = Palette.Cyan1 * alpha;
                var el = area.Width * 0.95f;
                DrawBar(sb, center, el, 3, MathHelper.PiOver4, e);
                DrawBar(sb, center, el, 3, -MathHelper.PiOver4, e);
                break;
        }
    }

    /// <summary>Barre pleine (texture 1×1 étirée) centrée sur <paramref name="center"/>, tournée de <paramref name="rotation"/> rad.</summary>
    private void DrawBar(SpriteBatch sb, Vector2 center, float length, float thickness, float rotation, Color c) =>
        sb.Draw(Context.Pixel, center, null, c, rotation, new Vector2(0.5f, 0.5f),
            new Vector2(length, thickness), SpriteEffects.None, 0f);

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

    /// <summary>Taille de case du plateau (px canvas) : cadrage×zoom, ou la MOITIÉ en dézoom (÷2). Gouverne TOUT
    /// (rendu ET hit-test/input/ancrage des cartes) dans un SEUL espace de coordonnées → tout fonctionne. En
    /// dézoom le plateau est donc dessiné à 32 px (net mais « chunky » : demi-détail, prix du dézoom).</summary>
    private int BoardTileSize() => _dezoomedOut
        ? System.Math.Max(GridLayout.DefaultTileSize / 2, (FitZoom() - 1) * GridLayout.DefaultTileSize)
        : GridLayout.DefaultTileSize * CurrentZoom();

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

        // Taille de case (board-only) : cadrage/zoom, ou ÷2 en dézoom. La hauteur de sprite garde la même
        // proportion 80/64 que la case (le recouvrement 64×80 reste cohérent à toutes les tailles).
        var tile = BoardTileSize();
        var spriteHeight = tile * GridLayout.DefaultSpriteHeight / GridLayout.DefaultTileSize;

        var pxW = Columns * tile;
        var pxH = (Rows - 1) * tile + spriteHeight;

        // Débordement (overscroll) aux bords pour révéler entièrement les sprites des rangées extrêmes
        // (dessinés AU-DESSUS de leur case) ET laisser une marge de caméra autour du terrain quand on est
        // zoomé (~1 case de vide visible au-delà de chaque bord).
        var margin = tile * 1f;
        // Jeu de pan « libre » quand le plateau tient dans la zone : on autorise un débordement (~2,5 cases)
        // dans les 4 directions au lieu de verrouiller au centre, pour regarder autour du terrain avec de la
        // marge. Quand le plateau déborde, c'est `margin` (overscroll des bords) qui s'applique.
        var slack = tile * 2.5f;
        // Débattement SUPPLÉMENTAIRE vers le haut : la caméra peut descendre le plateau d'une case de
        // plus, pour dégager la rangée du fond de la frise des missions et voir ce qu'il y a au-dessus.
        var topSlack = tile;
        float centerX = (centerWidth - pxW) / 2f;
        float centerY = (viewport.Height - pxH) / 2f;
        float ox = ClampAxis(centerX, _camera.X, pxW, centerWidth, margin, slack, out float cx);
        float oy = ClampAxis(centerY, _camera.Y, pxH, viewport.Height, margin, slack, out float cy, topSlack);
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
    /// <paramref name="extraHi"/> élargit la borne HAUTE seule (origine plus grande = plateau poussé vers
    /// le bas) : sur l'axe Y, c'est le débattement supplémentaire pour regarder AU-DESSUS du plateau.
    /// </summary>
    private static float ClampAxis(float center, float pan, float board, float area, float margin, float slack,
        out float clampedPan, float extraHi = 0f)
    {
        float lo, hi;
        if (board <= area) { lo = center - slack; hi = center + slack; }   // tient : petit jeu autour du centre
        else { lo = area - board - margin; hi = margin; }   // déborde : overscroll des deux bords
        float origin = MathHelper.Clamp(center + pan, lo, hi + extraHi);
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

    /// <summary>
    /// <paramref name="front"/> = l'unité regarde vers le bas (face caméra). Le PNG est choisi par
    /// l'<see cref="UnitClass.Asset"/> de la classe (un sprite par classe) : <c>&lt;asset&gt;_*.png</c>.
    /// </summary>
    private Texture2D? SpriteFor(UnitClass cls, Faction faction, bool front = false)
    {
        var variant = faction == Faction.Player
            ? $"{cls.Asset}_{(front ? "front" : "back")}"
            : $"{cls.Asset}_ia_{(front ? "front" : "back")}";
        return SpriteFor(variant) ?? SpriteFor(cls.Asset);
    }

    /// <summary>
    /// Orientation par DÉFAUT d'un pion pas encore orienté par une action : selon la MOITIÉ du plateau où il
    /// se trouve, QUEL QUE SOIT son camp — moitié HAUTE (rangées du haut) → regarde vers le bas (front/face
    /// caméra), moitié BASSE → regarde vers le haut (back/dos). Repli sur l'orientation par camp (ennemi =
    /// face) si la case est introuvable (unité hors plateau).
    /// </summary>
    private bool DefaultFacesDown(Unit unit)
    {
        // La map peut FIXER l'orientation par défaut, PAR CASE de spawn (calque `facing`), quel que soit l'endroit
        // du plateau. ENNEMI : capté au spawn (_enemyForcedFacing), conservé même s'il se déplace ensuite. JOUEUR :
        // lu sur la case COURANTE (les pions joueurs sont ré-instanciés en Placement/Équipement, donc un cache
        // par-Unit serait perdu). Dans les deux cas, la règle ne vaut qu'AVANT la 1re action (ensuite _facesDown).
        if (unit.Faction == Faction.Enemy && _enemyForcedFacing.TryGetValue(unit, out var forced))
            return forced;
        if (unit.Faction == Faction.Player && _map is { } fm && _match.CellOf(unit) is { } pc
            && fm.ForcedFacing.TryGetValue(pc, out var playerForced))
            return playerForced;
        return _match.CellOf(unit) is { } c ? c.Row < Rows / 2 : unit.Faction == Faction.Enemy;
    }

    /// <summary>Vrai si l'unité regarde vers le bas (face caméra) — état suivi (action), ou défaut positionnel.</summary>
    private bool FacesDown(Unit unit) =>
        _facesDown.TryGetValue(unit, out var f) ? f : DefaultFacesDown(unit);

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
            case MenuAction.Codex:
                // Ouvre le codex PAR-DESSUS le menu pause (qui reste ouvert derrière) ; sa fermeture y ramène.
                _codex.Open();
                break;
            case MenuAction.RestartMission:
                RestartMission();
                break;
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
