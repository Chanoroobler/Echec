using System.Collections.Generic;
using System.Linq;
using ChessArmy.Core.Command;
using ChessArmy.Core.Equip;
using ChessArmy.Core.Map;

namespace ChessArmy.Core.Battle;

/// <summary>
/// État et règles d'une partie : grille d'unités, tour courant, déplacement et combat,
/// condition de victoire. Domaine pur (aucun rendu).
///
/// Un tour = UNE action : se DÉPLACER vers une case vide (jusqu'à la portée de
/// déplacement) OU ATTAQUER une cible à portée de tir. Les deux suivent les directions
/// du domaine. À l'attaque : si la cible meurt, l'attaquant prend sa place dès lors qu'il
/// POURRAIT s'y déplacer (case libérée atteignable par son mouvement : mêlée, saut, ou ligne
/// dégagée du lancier dans sa portée) ; sinon (hors d'atteinte ou chemin bloqué) il reste.
/// </summary>
public sealed class Match
{
    private readonly Unit?[,] _units;

    // Terrain optionnel : si fourni, l'eau et la montagne bornent déplacements et/ou tirs
    // (null = plateau entièrement traversable, comme avant l'ajout du terrain).
    private readonly Battlefield? _terrain;

    // Cases de COUVERT (buissons, calque "objects" de la map) : une unité dessus encaisse moins de
    // dégâts (cf. BushReduction). Vide = aucun couvert.
    private readonly HashSet<Cell> _cover;

    // Unités essentielles posées sur le terrain (commandant joueur / boss ennemi).
    // On garde la référence même après leur mort pour évaluer la condition de victoire.
    private readonly List<Unit> _essential = new();

    // Buffer réutilisé par CanTakePlace (évite d'allouer une liste de coups à chaque kill).
    private readonly List<Cell> _placeBuffer = new();

    // Buffer réutilisé par LienPuissancePowerBonus (appelé à chaque calcul de dégâts : pas d'allocation).
    private readonly List<Cell> _lienBuffer = new();

    // Résultats de la DERNIÈRE action (déplacement/attaque) du porteur, pour le feedback de la scène.
    // Réinitialisés au début de chaque TryMove/TryAttack ; restent vides/null si le trait est absent.
    private readonly List<(Cell Cell, int Damage)> _impactHits = new();   // « Impact » : ennemis touchés autour du porteur
    private readonly List<Cell> _impactZone = new();                      // « Impact » : les 8 cases de l'AoE (touchées ou non) pour le tremblement des tuiles
    private readonly List<Cell> _seismeZone = new();                      // « Séisme » : union des voisinages 3×3 des porteurs, pour le tremblement des tuiles
    private (Cell To, int SlamDamage)? _lastRecule;                        // « Recule » : case d'arrivée de la cible + dégât de plaquage (0 si glissée)
    private readonly List<(Cell From, Cell To)> _dodges = new();           // « Esquive » : replis déclenchés par l'action (départ → arrivée), dans l'ordre
    private (Cell From, Cell To, int Damage, bool Killed)? _lastRiposte;   // « Riposte » : case du riposteur → assaillant + dégâts + assaillant abattu
    private readonly List<(Cell Cell, int Damage, bool Killed)> _thorns = new();  // « Épines » : renvois de l'action (case de l'assaillant + dégâts + assaillant abattu)
    private bool _reflecting;                                              // « Épines » : un renvoi est en cours (jamais relayé en chaîne)
    private (Cell Cell, int Damage, int Dc, int Dr)? _lastPierce;          // « Transpercement » : ennemi derrière la cible touché + dégâts + direction du coup
    private readonly List<(Cell From, Cell To, int Damage, bool Killed)> _interceptions = new();  // « Interception » : intercepteur → mobile + dégâts + mobile abattu (par ordre de frappe)
    private IReadOnlyList<Cell>? _lastSlide;                               // « Glace » : chemin de glissade (départ inclus → repos), null si aucune glissade

    // Source d'aléa du combat : tirage des cibles d'« Orage »/« Tempête » et choix de la case de repli
    // d'« Esquive » (à égalité de menace). Injectable pour des tests reproductibles.
    private readonly System.Random _rng;

    // Vrai (défaut) : éliminer tout un camp décide la partie (escarmouche/boss). Faux : l'élimination des
    // ENNEMIS ne gagne PAS le combat (mission spéciale à objectif : c'est la scène qui décide la réussite ;
    // seule la chute du commandant reste une défaite). Voir UpdateWinner.
    private readonly bool _eliminationEndsGame;

    // Cases interdites au DÉPLACEMENT du JOUEUR uniquement (les ennemis y vont) : les paysans de la mission
    // « protéger » (le joueur ne peut pas camper dessus ; il défend en interceptant). Le joueur peut GLISSER
    // au travers mais pas s'y arrêter ; les ennemis les traitent normalement. Vide = aucune restriction.
    private readonly HashSet<Cell> _playerBlocked;

    public Match(int width, int height, Battlefield? terrain = null,
        IEnumerable<Cell>? coverCells = null, System.Random? rng = null, bool eliminationEndsGame = true,
        IEnumerable<Cell>? playerBlockedCells = null, int rempartBonus = 0,
        int tueurGeantsBonus = 0, int formationBonus = 0)
    {
        Width = width;
        Height = height;
        _units = new Unit?[width, height];
        _terrain = terrain;
        _cover = coverCells is null ? new HashSet<Cell>() : new HashSet<Cell>(coverCells);
        _rng = rng ?? new System.Random();
        _eliminationEndsGame = eliminationEndsGame;
        _playerBlocked = playerBlockedCells is null ? new HashSet<Cell>() : new HashSet<Cell>(playerBlockedCells);
        RempartBonus = rempartBonus;
        TueurDeGeantsBonus = tueurGeantsBonus;
        FormationBonus = formationBonus;
    }

    /// <summary>Bonus d'arbre « Rempart renforcé » (points de réduction en PLUS de <see cref="BaseRempartReduction"/>).
    /// Appliqué UNIQUEMENT aux unités du JOUEUR — c'est SON arbre de commandement, l'ennemi n'en profite pas.
    /// Réglable par la scène si un nœud est acheté en cours de placement. Cf. <see cref="RempartReductionFor"/>.</summary>
    public int RempartBonus { get; set; }

    /// <summary>Bonus d'arbre « Tueur de géant renforcé » (dégâts en PLUS de <see cref="BaseGiantSlayerBonus"/>).
    /// Appliqué UNIQUEMENT aux unités du JOUEUR. Cf. <see cref="GiantSlayerDamageFor"/>.</summary>
    public int TueurDeGeantsBonus { get; set; }

    /// <summary>Bonus EFFECTIF du trait « Tueur de géants » pour <paramref name="attacker"/> : base, plus le bonus
    /// d'arbre SEULEMENT si c'est une unité du joueur (le renforcement ne touche pas l'ennemi).</summary>
    private int GiantSlayerDamageFor(Unit attacker) =>
        BaseGiantSlayerBonus + (attacker.Faction == Faction.Player ? TueurDeGeantsBonus : 0);

    /// <summary>Bonus d'arbre « Formation renforcée » (puissance par allié adjacent en PLUS de
    /// <see cref="BaseFormationBonus"/>). Appliqué UNIQUEMENT aux unités du JOUEUR. Cf. <see cref="PerAllyFormationBonus"/>.</summary>
    public int FormationBonus { get; set; }

    /// <summary>Bonus de puissance EFFECTIF par allié adjacent du trait « Formation » pour <paramref name="unit"/> :
    /// base, plus le bonus d'arbre SEULEMENT si c'est une unité du joueur (le renforcement ne touche pas l'ennemi).</summary>
    private int PerAllyFormationBonus(Unit unit) =>
        BaseFormationBonus + (unit.Faction == Faction.Player ? FormationBonus : 0);

    /// <summary>Bonus d'arbre « Impact renforcé » (dégâts en PLUS de <see cref="BaseImpactDamage"/>).
    /// Appliqué UNIQUEMENT aux unités du JOUEUR. Cf. <see cref="ImpactDamageFor"/>.</summary>
    public int ImpactBonus { get; set; }

    /// <summary>Dégât EFFECTIF du trait « Impact » pour <paramref name="unit"/> : base, plus le bonus d'arbre
    /// SEULEMENT si c'est une unité du joueur (le renforcement ne touche pas l'ennemi).</summary>
    private int ImpactDamageFor(Unit unit) =>
        BaseImpactDamage + (unit.Faction == Faction.Player ? ImpactBonus : 0);

    /// <summary>
    /// Nœud d'arbre « charge » : quand une unité du JOUEUR de CE domaine TUE par son attaque, le tour ne passe
    /// pas — le joueur rejoue aussitôt (UNE seule fois avant que l'ennemi joue, cf. <see cref="_extraTurnUsed"/>).
    /// null = aucun rejeu. Réglé par la scène depuis la <see cref="Campaign.Run"/>.
    /// </summary>
    public Domaine? ExtraTurnDomaine { get; set; }

    /// <summary>Vrai si le rejeu a DÉJÀ été consommé depuis le dernier tour ennemi (remis à zéro quand le tour revient au joueur).</summary>
    private bool _extraTurnUsed;

    /// <summary>Vrai si la DERNIÈRE action a offert un tour bonus (feedback : le tour n'est pas passé).</summary>
    public bool LastGrantedExtraTurn { get; private set; }

    /// <summary>Réduction EFFECTIVE du trait « Rempart » pour <paramref name="victim"/> : base, plus le bonus
    /// d'arbre SEULEMENT si c'est une unité du joueur (le renforcement ne touche pas l'ennemi).</summary>
    private int RempartReductionFor(Unit victim) =>
        BaseRempartReduction + (victim.Faction == Faction.Player ? RempartBonus : 0);

    /// <summary>Vrai si la case offre un COUVERT (buisson) : l'unité dessus reçoit moins de dégâts.</summary>
    private bool IsCover(Cell cell) => _cover.Contains(cell);

    /// <summary>Vrai si la tuile interdit le déplacement (mur, eau).</summary>
    private bool BlocksMovement(Cell cell) =>
        _terrain != null && _terrain[cell].BlocksMovement;

    /// <summary>Vrai si la tuile arrête une ligne de tir (mur).</summary>
    private bool BlocksLineOfFire(Cell cell) =>
        _terrain != null && _terrain[cell].BlocksLineOfFire;

    /// <summary>
    /// Portée d'attaque minimale à partir de laquelle une unité compte comme TIREUSE : c'est le seuil que la
    /// tuile « tour de guet » exige pour accorder son bonus. Un pion de contact ne gagne rien à monter.
    /// </summary>
    public const int RangedAttackRange = 2;

    /// <summary>
    /// Trait de combat PRÊTÉ par la tuile de <paramref name="cell"/> à qui s'y tient
    /// (cf. <see cref="Map.TileDef.Trait"/>), ou null. PUBLIQUE : l'UI l'affiche sur la carte du pion, pour ne
    /// pas laisser un trait agir en combat sans que rien ne le dise.
    /// </summary>
    public string? TileTraitAt(Cell cell) => _terrain?[cell].GrantedTrait;

    /// <summary>
    /// Vrai si <paramref name="unit"/>, postée en <paramref name="from"/>, a <paramref name="trait"/> — par
    /// elle-même (classe, équipement, arbre) OU prêté par la tuile sous ses pieds. À employer partout où un
    /// trait dépend de l'endroit d'où le pion agit ; <see cref="Unit.HasTrait"/> seul ignore le terrain.
    /// </summary>
    private bool HasTraitOn(Unit unit, Cell from, string trait) =>
        unit.HasTrait(trait) || TileTraitAt(from) == trait;

    /// <summary>
    /// Portée d'attaque GAGNÉE par l'unité postée en <paramref name="cell"/> grâce à sa tuile (tour de guet,
    /// cf. <see cref="Map.TileDef.RangeBonus"/>). 0 si la case n'en donne pas, si elle est vide, ou si son
    /// occupant frappe au contact (portée native &lt; <see cref="RangedAttackRange"/>) : une tour rallonge un
    /// tir, pas un bras. Le bonus se lit sur la portée NATIVE, donc il ne s'auto-entretient jamais.
    /// PUBLIQUE : l'UI l'affiche sur la carte du pion, pour ne pas annoncer une portée inférieure à la vraie.
    /// </summary>
    public int AttackRangeBonus(Cell cell) =>
        _terrain != null && UnitAt(cell) is { } u && u.AttackRange >= RangedAttackRange
            ? _terrain[cell].RangeBonus
            : 0;

    /// <summary>
    /// Portée d'attaque EFFECTIVE de <paramref name="unit"/> postée en <paramref name="from"/> : sa portée
    /// plus celle que lui donne sa tuile. Passage OBLIGÉ de tous les balayages de tir (cibles, menace, rayons
    /// de soutien) — les faire diverger annoncerait au joueur une portée que le moteur ne jouerait pas.
    /// </summary>
    private int EffectiveAttackRange(Unit unit, Cell from) => unit.AttackRange + AttackRangeBonus(from);

    /// <summary>Vrai si <paramref name="unit"/> (un JOUEUR) ne peut pas s'arrêter sur <paramref name="cell"/>
    /// — case paysan de la mission « protéger ». Les ennemis n'y sont jamais bloqués.</summary>
    private bool BlocksPlayerLanding(Cell cell, Unit unit) =>
        _playerBlocked.Count > 0 && unit.Faction == Faction.Player && _playerBlocked.Contains(cell);

    /// <summary>
    /// Lève l'interdiction du JOUEUR sur une case (mission « protéger » : un paysan CAPTURÉ n'est plus là,
    /// sa case redevient accessible). Sans effet si la case n'était pas bloquée.
    /// </summary>
    public void UnblockPlayerCell(Cell cell) => _playerBlocked.Remove(cell);

    public int Width { get; }
    public int Height { get; }
    public Faction CurrentTurn { get; private set; } = Faction.Player;
    public Faction? Winner { get; private set; }
    public bool IsOver => Winner != null;

    public bool InBounds(Cell cell) =>
        cell.Column >= 0 && cell.Column < Width && cell.Row >= 0 && cell.Row < Height;

    public Unit? UnitAt(Cell cell) => InBounds(cell) ? _units[cell.Column, cell.Row] : null;

    /// <summary>Case occupée par <paramref name="unit"/> sur le plateau, ou <c>null</c> s'il n'y est pas.</summary>
    public Cell? CellOf(Unit unit)
    {
        for (var row = 0; row < Height; row++)
            for (var column = 0; column < Width; column++)
                if (ReferenceEquals(_units[column, row], unit))
                    return new Cell(column, row);
        return null;
    }

    public void Place(Cell cell, Unit unit)
    {
        _units[cell.Column, cell.Row] = unit;
        if (unit.IsEssential)
            _essential.Add(unit);
    }

    /// <summary>Retire l'unité d'une case (utilisé en phase de placement).</summary>
    public void Remove(Cell cell)
    {
        var unit = UnitAt(cell);
        if (unit == null)
            return;
        _units[cell.Column, cell.Row] = null;
        _essential.Remove(unit);
    }

    public IEnumerable<(Cell Cell, Unit Unit)> Units()
    {
        for (var row = 0; row < Height; row++)
            for (var column = 0; column < Width; column++)
            {
                var unit = _units[column, row];
                if (unit != null)
                    yield return (new Cell(column, row), unit);
            }
    }

    /// <summary>Cases VIDES atteignables en déplacement (le long des directions, bloqué par toute unité).</summary>
    public List<Cell> LegalMoves(Cell from)
    {
        var result = new List<Cell>();
        LegalMoves(from, result);
        return result;
    }

    /// <summary>Variante SANS allocation : vide puis remplit <paramref name="result"/> (réutiliser un buffer).</summary>
    public void LegalMoves(Cell from, List<Cell> result)
    {
        result.Clear();
        if (ActiveUnitAt(from) is { } unit)
            AppendLegalMoves(from, unit, result);
    }

    /// <summary>Remplit <paramref name="result"/> (déjà vidé) avec les cases d'arrivée de
    /// <paramref name="unit"/> posée sur <paramref name="from"/>, INDÉPENDAMMENT du tour courant — le repli
    /// d'« Esquive » se déclenche pendant le tour ADVERSE (cf. <see cref="ApplyEsquive"/>).</summary>
    private void AppendLegalMoves(Cell from, Unit unit, List<Cell> result)
    {
        var vectors = Movement.Vectors(unit.Domaine);
        var flies = unit.HasTrait(Trait.Vol);   // Vol : les obstacles de terrain (eau/montagne) ne bloquent plus

        // « Repositionnement stratégique » : ajoute un pas d'UNE case à gauche ET à droite (orthogonal), quel
        // que soit le domaine, EN PLUS du motif natif. Déplacement SEULEMENT (jamais l'attaque) ; mêmes règles
        // d'arrivée que le reste (case libre, non-obstacle sauf Vol, hors case protégée, sans doublon).
        void AddRepositionnement()
        {
            if (!unit.HasTrait(Trait.RepositionnementStrategique))
                return;
            for (var dc = -1; dc <= 1; dc += 2)
            {
                var side = new Cell(from.Column + dc, from.Row);
                if (InBounds(side) && _units[side.Column, side.Row] == null
                    && (flies || !BlocksMovement(side)) && !BlocksPlayerLanding(side, unit)
                    && !result.Contains(side))
                    result.Add(side);
            }
        }

        if (Movement.Kind(unit.Domaine) == MovementKind.Jump)
        {
            foreach (var offset in vectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (!InBounds(to))
                    continue;
                // « Roque » (DUO) : la case de l'AUTRE meneur est une arrivée valide — les deux échangent.
                if (_units[to.Column, to.Row] is { } occupant)
                {
                    if (CanRoque(unit, occupant))
                        result.Add(to);
                    continue;
                }
                if ((flies || !BlocksMovement(to))
                    && !BlocksPlayerLanding(to, unit))   // le joueur ne se pose pas sur un paysan (mission « protéger »)
                    result.Add(to);
            }
            AddRepositionnement();
            return;
        }

        // Franchissement : traverse aussi bien les UNITÉS que les OBSTACLES de terrain (eau/montagne/mur)
        // qui jalonnent le chemin — sans jamais pouvoir s'y arrêter (il se pose sur une case libre franchissable).
        var phases = unit.HasTrait(Trait.Franchissement);
        // « Aura de célérité » : +1 portée si l'unité COMMENCE son tour (= sa case actuelle `from`) au CONTACT DIRECT
        // (orthogonal, hors diagonales — plus exigeant que les autres auras) d'un allié porteur. Contextuel au placement.
        var range = unit.MoveRange
                    + (HasAdjacentAlly(from, unit.Faction, Trait.AuraDeCelerite, orthogonalOnly: true) ? AuraCeleriteBonus : 0);
        foreach (var dir in vectors)
        {
            for (var step = 1; step <= range; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to))
                    break; // hors plateau
                if (BlocksMovement(to) && !flies)
                {
                    if (phases) continue;   // Franchissement : traverse l'obstacle (eau/montagne/mur) sans s'y poser
                    break;                  // obstacle infranchissable (sauf l'unité qui vole)
                }
                if (_units[to.Column, to.Row] is { } occupant)
                {
                    // « Roque » (DUO) : l'autre meneur n'est pas un obstacle — on va sur sa case et ils
                    // échangent. Le rayon s'arrête là pour autant (on ne le traverse pas).
                    if (CanRoque(unit, occupant))
                    {
                        result.Add(to);
                        break;
                    }
                    if (phases) continue;   // Franchissement : on enjambe l'unité (sans pouvoir s'y poser)
                    break;                  // sinon une unité borne le déplacement
                }
                if (BlocksPlayerLanding(to, unit))
                    continue;   // le joueur GLISSE au travers d'un paysan (protéger) mais ne s'y arrête pas
                result.Add(to);
            }
        }
        AddRepositionnement();
    }

    /// <summary>Cases ennemies à portée de TIR (première unité rencontrée dans chaque direction).</summary>
    public List<Cell> AttackTargets(Cell from)
    {
        var result = new List<Cell>();
        AttackTargets(from, result);
        return result;
    }

    /// <summary>Variante SANS allocation : vide puis remplit <paramref name="result"/> (réutiliser un buffer).</summary>
    public void AttackTargets(Cell from, List<Cell> result)
    {
        result.Clear();
        var unit = ActiveUnitAt(from);
        if (unit == null)
            return;

        // Pattern d'ATTAQUE de l'unité (peut différer du déplacement : cavalier monté = saut en L ;
        // « Attaque libre » = tir de Dame qui REMPLACE le pattern natif, cf. Unit.AttackDomaine).
        AppendAttackTargets(from, unit, unit.AttackDomaine, result);
    }

    /// <summary>
    /// Ajoute à <paramref name="result"/> les ENNEMIS atteignables selon le pattern <paramref name="attackDomaine"/> :
    /// SAUT = cases en L (cavalier) ; GLISSÉ = 1er ennemi rencontré par direction (zone morte, ligne de tir /
    /// montagne, traverse-allié respectés). Sans doublon — permet d'UNIR plusieurs patterns (cf. « Attaque libre »).
    /// </summary>
    private void AppendAttackTargets(Cell from, Unit unit, Domaine attackDomaine, List<Cell> result)
    {
        var vectors = Movement.Vectors(attackDomaine);
        // « Sacrifice » (BRUTE) : le porteur peut aussi VISER SES PROPRES ALLIÉS. Réservé au camp du joueur —
        // une IA ne doit jamais se mettre à frapper les siens, même si elle héritait du trait.
        var sacrifice = unit.Faction == Faction.Player && unit.HasTrait(Trait.Sacrifice);

        if (Movement.Kind(attackDomaine) == MovementKind.Jump)
        {
            foreach (var offset in vectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (UnitAt(to) is { } target && (target.Faction != unit.Faction || sacrifice)
                    && !result.Contains(to))
                    result.Add(to);
            }
            return;
        }

        var piercesAllies = unit.HasTrait(Trait.TraverseAllie);   // via HasTrait : classe (PiercesAllies) OU équipement
        // …ou prêté par sa tuile (mirador) : le tir indirect dépend de l'endroit d'où l'on tire.
        var balistique = HasTraitOn(unit, from, Trait.Balistique);   // tir indirect : la montagne ne coupe plus la ligne
        var reach = EffectiveAttackRange(unit, from);   // portée + bonus de tuile (tour de guet)
        foreach (var dir in vectors)
        {
            // Zone morte (portée min) UNIQUEMENT en ligne droite : en diagonale on peut tirer dès la
            // distance 1 (le contact « corps à corps » n'est interdit qu'en face/côté).
            var minStep = dir.Column != 0 && dir.Row != 0 ? 1 : unit.MinAttackRange;
            for (var step = 1; step <= reach; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to))
                    break; // hors plateau : la ligne de tir s'arrête
                var blocked = BlocksLineOfFire(to) && !balistique;   // montagne/mur (l'eau laisse toujours passer)

                var target = _units[to.Column, to.Row];
                if (target == null)
                {
                    if (blocked)
                        break;    // obstacle NU : coupe la ligne (sauf tir balistique)
                    continue;     // case vide (ou eau) : la ligne de tir continue
                }

                // Une unité PERCHÉE sur l'obstacle (elle n'a pu s'y poser qu'avec « Vol ») reste à découvert :
                // c'est elle qu'on vise, pas le mur, donc l'obstacle ne la protège pas. Le tueur ne prendra pas
                // sa place pour autant — il faut voler soi-même pour s'y poser (cf. LegalMoves / CanTakePlace).
                if (target.Faction != unit.Faction)
                {
                    // Premier ennemi en vue : cible SI au-delà de la zone morte de cette direction.
                    // Dans tous les cas son corps borne la ligne (pas de tir au travers).
                    if (step >= minStep && !result.Contains(to))
                        result.Add(to);
                    break;
                }

                // « Sacrifice » : l'allié rencontré est une cible comme une autre (et son corps borne la ligne).
                if (sacrifice)
                {
                    if (step >= minStep && !result.Contains(to))
                        result.Add(to);
                    break;
                }

                // Allié : le LANCIER le traverse sans le toucher (ne borne pas) ; sinon il bloque. Perché sur un
                // obstacle il borne QUAND MÊME : c'est le mur qui le porte qui coupe la ligne derrière lui.
                if (!piercesAllies || blocked)
                    break;
            }
        }
    }

    /// <summary>
    /// Cases MENACÉES par l'unité en <paramref name="from"/> : toutes les cases atteignables le
    /// long de ses directions de tir jusqu'à sa portée, en s'arrêtant à la première unité
    /// rencontrée (incluse, car elle subirait l'attaque). INDÉPENDANT du tour courant — sert à
    /// prévisualiser la menace d'un ennemi au survol. Liste vide si la case est inoccupée.
    /// </summary>
    public List<Cell> ThreatenedCells(Cell from)
    {
        var result = new List<Cell>();
        ThreatenedCells(from, result);
        return result;
    }

    /// <summary>Variante SANS allocation : vide puis remplit <paramref name="result"/> (réutiliser un buffer).</summary>
    public void ThreatenedCells(Cell from, List<Cell> result)
    {
        result.Clear();
        var unit = UnitAt(from);
        if (unit == null)
            return;

        // Pattern d'ATTAQUE de l'unité (cavalier monté = saut en L ; « Attaque libre » = lignes de Dame à la
        // PLACE du pattern natif, cf. Unit.AttackDomaine) : la menace affichée suit exactement le tir réel.
        AppendThreatenedCells(from, unit, unit.AttackDomaine, result);
    }

    /// <summary>Ajoute à <paramref name="result"/> les cases MENACÉES selon le pattern <paramref name="attackDomaine"/>
    /// (toutes les cases atteignables jusqu'à la 1re unité incluse). Sans doublon — union de patterns possible.</summary>
    private void AppendThreatenedCells(Cell from, Unit unit, Domaine attackDomaine, List<Cell> result)
    {
        var vectors = Movement.Vectors(attackDomaine);

        if (Movement.Kind(attackDomaine) == MovementKind.Jump)
        {
            foreach (var offset in vectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (InBounds(to) && !result.Contains(to))
                    result.Add(to);
            }
            return;
        }

        var piercesAllies = unit.HasTrait(Trait.TraverseAllie);   // via HasTrait : classe (PiercesAllies) OU équipement
        var balistique = HasTraitOn(unit, from, Trait.Balistique);   // tir indirect (trait ou tuile) : la montagne ne coupe plus la ligne
        var reach = EffectiveAttackRange(unit, from);   // portée + bonus de tuile (tour de guet)
        foreach (var dir in vectors)
        {
            var minStep = dir.Column != 0 && dir.Row != 0 ? 1 : unit.MinAttackRange;
            for (var step = 1; step <= reach; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to))
                    break; // hors plateau : la menace ne porte pas au-delà
                var blocked = BlocksLineOfFire(to) && !balistique;   // montagne/mur (l'eau laisse passer)

                var occupant = _units[to.Column, to.Row];
                if (occupant == null && blocked)
                    break; // obstacle NU : coupe la ligne (sauf tir balistique)
                // Occupant PERCHÉ dessus (« Vol ») : il est menacé comme n'importe quelle cible, l'obstacle ne
                // le couvre pas (même règle qu'AppendAttackTargets) — et il borne la ligne au-delà.
                if (occupant != null && occupant.Faction == unit.Faction && piercesAllies && !blocked)
                    continue; // lancier : traverse l'allié sans le menacer, la ligne continue

                if (step >= minStep && !result.Contains(to))
                    result.Add(to); // hors zone morte (diagonale = dès 1) : case réellement menacée
                if (occupant != null)
                    break; // un ennemi (ou un allié non traversé) borne la ligne de tir au-delà
            }
        }
    }

    /// <summary>Déplace l'unité vers une case vide légale. Passe le tour en cas de succès.</summary>
    public MoveKind TryMove(Cell from, Cell to)
    {
        var unit = ActiveUnitAt(from);
        if (unit == null || !LegalMoves(from).Contains(to))
            return MoveKind.Invalid;

        ResetActionFx();
        RecordObstacleJump(unit, from, to);   // saut du cavalier PAR-DESSUS quelque chose (compté AVANT de bouger)

        // « Roque » (DUO) : la case d'arrivée porte l'AUTRE meneur → les deux ÉCHANGENT leurs places. Aucun
        // autre effet de déplacement ne s'applique (ni glace, ni interception, ni impact) : c'est un échange.
        if (_units[to.Column, to.Row] is { } partner && CanRoque(unit, partner))
        {
            _units[to.Column, to.Row] = unit;
            _units[from.Column, from.Row] = partner;
            // « La puissance du rock » : l'ARTISAN (le meneur non compagnon) repart chargé, quel que soit celui
            // des deux qui a lancé l'échange — c'est le même mouvement vu des deux bouts, distinguer le
            // « moteur » du roque passerait pour un bug. Non cumulable (cf. Unit.ChargeRoquePower).
            if (RoquePower > 0)
                (unit.IsCompanion ? partner : unit).ChargeRoquePower(RoquePower);
            EndTurn();
            return MoveKind.Moved;
        }

        MoveUnit(from, to);
        // « Glace » : si la case d'arrivée est glissante, l'unité glisse au-delà (peut dépasser sa portée). Les
        // traits (interception/impact) se déclenchent depuis sa case de REPOS réelle.
        var landing = SlideOnIce(unit, from, to);
        TriggerInterceptions(landing, unit);   // ennemis avec « Interception » dont la portée couvre la case de repos
        // « Impact » : à son propre déplacement, le porteur frappe les ennemis autour de sa case de repos
        // (s'il a survécu à une éventuelle interception — et depuis la case où elle a pu le faire se replier).
        if (unit.HasTrait(Trait.Impact) && CellOf(unit) is { } rest)
            ApplyImpact(unit, rest);
        EndTurn();
        return MoveKind.Moved;
    }

    /// <summary>Attaque une cible ennemie à portée de tir. Passe le tour en cas de succès.</summary>
    public MoveKind TryAttack(Cell from, Cell target)
    {
        var unit = ActiveUnitAt(from);
        if (unit == null || !AttackTargets(from).Contains(target))
            return MoveKind.Invalid;

        ResetActionFx();
        var victim = _units[target.Column, target.Row]!;
        var victimHpBefore = victim.Hp;
        // « Sacrifice » (BRUTE) : frapper un des SIENS. Le coup part normalement, mais le porteur y gagne des
        // PV — dans la limite de son maximum : il se nourrit de ses pions, il ne devient pas un colosse.
        var sacrificed = victim.Faction == unit.Faction;
        ApplyDamage(victim, EffectiveDamage(unit, from, victim, target), unit);
        if (sacrificed)
            unit.Heal(SacrificeHeal);

        // « Épines » de la cible : l'assaillant a pu tomber sur le renvoi (il est alors DÉJÀ retiré du plateau).
        // La suite de son attaque (drain, transpercement, foudre, impact) est alors sans objet : un mort ne
        // poursuit pas son coup. Le coup déjà porté, lui, reste acquis.
        var attackerStanding = CellOf(unit) != null;

        // Drain de vie : l'attaquant récupère 50 % des dégâts RÉELLEMENT infligés (réductions incluses).
        // « Seringue » (objet à lancer) : drain INTÉGRAL — tout ce qui a été infligé revient en PV. Prioritaire
        // sur « Drain de vie » (50 %) : les deux ne se cumulent pas, le meilleur gagne.
        if (attackerStanding && unit.HasTrait(Trait.Seringue))
            unit.Heal(victimHpBefore - victim.Hp);
        else if (attackerStanding && unit.HasTrait(Trait.DrainDeVie))
            unit.Heal((victimHpBefore - victim.Hp) / 2);

        // Source de points « sur coup à distance » (commandant du Fou) : un coup DIRECT qui TOUCHE vraiment
        // (dégâts réellement encaissés, 0 exclu) une cible à portée >= CommanderRangedHitDistance
        // rapporte un point au porteur. Compté sur TOUTE unité (comme TimesHit) ; seul un commandant dont
        // c'est la source en tire des points, crédités à la clôture (cf. Run.GrantCommanderRangedHitPoints).
        if (victim.Hp < victimHpBefore && ChebyshevDistance(from, target) >= CommanderRangedHitDistance)
            unit.RecordRangedHit();

        // Transpercement : l'unité juste DERRIÈRE la cible (même direction) est aussi touchée.
        if (attackerStanding && unit.HasTrait(Trait.Transpercement))
            PierceBehind(from, target, unit);

        // Orage / Tempête : la foudre frappe des ennemis AU HASARD (hors cible directe) pour un dégât fixe —
        // 3 cibles pour l'Orage, 5 pour la Tempête (cf. StormTargetsFor).
        if (attackerStanding && StormDamageFor(unit) is > 0 and var storm)
            StormStrike(unit, target, storm);

        // DUO — « Tir en ligne » : toutes les autres cibles alignées à portée encaissent le même coup.
        if (attackerStanding && unit.HasTrait(Trait.TirEnLigne))
            ApplyTirEnLigne(unit, from, target);

        // DUO — OBJET À LANCER (sacoche) : effet propre de l'objet (zone, rebonds, retournement), puis il se
        // BRISE. Résolu AVANT que l'attaquant ne prenne la place d'une cible abattue : les portées se
        // comptent depuis la case d'où le coup est parti.
        if (attackerStanding && unit.Equipments.Count > 0)
        {
            ResolveThrownItem(unit, from, target);
            ConsumeThrownItem(unit);
        }

        MoveKind kind;
        if (!victim.IsAlive && TryReviveWithEquipment(victim))
        {
            // « Queue de phénix » : la cible tombée ressuscite à 1 PV (équipement brisé). Elle SURVIT donc :
            // pas de kill, l'attaquant ne prend pas sa place. Traité comme un coup encaissé (survivant).
            if (unit.HasTrait(Trait.Recule))
                ApplyRecule(from, target, unit);   // le ressuscité est repoussé (pas de riposte dans ce cas)
            kind = MoveKind.Moved;
        }
        else if (!victim.IsAlive)
        {
            if (!sacrificed)
                unit.RecordKill();                       // mise à mort créditée à l'attaquant (compteur à vie)
            _units[target.Column, target.Row] = null;   // case libérée AVANT de tester l'accès
            OnUnitDied(victim, target);                  // « Rage » : les alliés survivants du mort gagnent de la puissance
            // « Statique » : ne prend JAMAIS la place de sa cible — l'attaquant reste sur sa case (la case de
            // la victime reste libre). Sinon, comportement normal : il avance sur la case si l'accès le permet.
            if (!unit.HasTrait(Trait.Statique) && CanTakePlace(from, target))
            {
                MoveUnit(from, target);
                // « Glace » : l'attaquant qui avance sur la case de la victime glisse si elle est glissante
                // (l'« Impact » plus bas frappe depuis sa case de repos réelle via CellOf).
                SlideOnIce(unit, from, target);
            }
            kind = MoveKind.Killed;
        }
        else
        {
            // « Recule » d'ABORD : la cible est repoussée AVANT de pouvoir riposter (le plaquage peut même
            // l'achever). C'est ce qui permet à un pion poussé HORS DE PORTÉE de ne plus riposter.
            if (unit.HasTrait(Trait.Recule))
                ApplyRecule(from, target, unit);

            // Riposte : la victime SURVIVANTE (au plaquage éventuel) contre-attaque DEPUIS SA CASE ACTUELLE
            // (elle a pu être repoussée), à condition de POUVOIR réellement frapper son assaillant — mêmes
            // règles que son attaque (motif, portée, zone morte, ligne de tir, traverse-allié). Repoussée hors
            // de portée, en diagonale d'une « Tour » ou derrière un obstacle → aucune riposte.
            // Un allié sacrifié ne riposte jamais : il encaisse sans comprendre.
            if (victim.IsAlive && !sacrificed && victim.HasTrait(Trait.Riposte)
                && UnitAt(from) is { } attacker && ReferenceEquals(attacker, unit)
                && CellOf(victim) is { } vc && CanStrike(vc, victim, from))
            {
                var attackerHpBefore = attacker.Hp;
                ApplyDamage(attacker, EffectiveDamage(victim, vc, attacker, from), victim);
                // « La puissance du rock » : une riposte EST un coup porté — elle profite de la charge et la
                // dépense. Sans ça elle en profiterait à chaque contre sans jamais l'épuiser.
                victim.SpendRoquePower();
                _lastRiposte = (vc, from, attackerHpBefore - attacker.Hp, !attacker.IsAlive);   // report feedback
                RemoveDeadAt(from, victim);   // la riposte tue l'attaquant : kill crédité à la victime
            }
            kind = MoveKind.Attacked; // l'attaquant reste sur place
        }

        // DUO — « Réaction en chaîne » : la mise à mort enchaîne sur une unité au CONTACT DU CORPS, et ainsi
        // de suite tant qu'il tue. Elle part donc de la case VISÉE (là où la victime est tombée), pas de celle
        // de l'attaquant — qui, en tir, est resté à distance.
        if (kind == MoveKind.Killed && unit.HasTrait(Trait.ReactionEnChaine))
            ArmChainReaction(unit, target);

        // « Impact » : le porteur frappe les ennemis autour de sa position FINALE (from, ou la case prise sur
        // un kill) — jamais si un contre l'a abattu (CellOf renvoie null). Déclenché par sa seule attaque.
        if (unit.HasTrait(Trait.Impact) && CellOf(unit) is { } here)
            ApplyImpact(unit, here);

        // « La puissance du rock » : la charge mise en réserve par le ROQUE a servi ce coup — et tout ce qu'il
        // a entraîné (transpercement, tir en ligne, foudre, impact, chaîne), qui font partie de la MÊME
        // attaque. Dépensée ici, avant les deux sorties de la méthode.
        unit.SpendRoquePower();

        // Nœud « charge » de l'arbre : une unité du JOUEUR du domaine visé qui TUE PAR SON ATTAQUE (les morts
        // indirectes — impact, foudre, riposte, épines — ne comptent pas) rend la main au joueur au lieu de
        // passer le tour. UNE seule fois entre deux tours ennemis, et seulement si l'assaillant est encore
        // debout (un attaquant tombé sur une riposte / des épines ne rejoue pas).
        if (GrantsExtraTurn(unit, kind))
        {
            _extraTurnUsed = true;
            LastGrantedExtraTurn = true;
            UpdateWinner();   // le tour NE passe PAS : on vérifie seulement si le combat est déjà décidé
            return kind;
        }

        EndTurn();
        return kind;
    }

    // ─── « CHAIR À CANON » (arbre de la BRUTE) ────────────────────────────────────────────────────
    // Le porteur empoigne un allié AU CONTACT et le jette sur un ennemi à ChairACanonRange cases : la cible
    // encaisse la puissance du lanceur (défenses comprises, comme toute attaque) et le projectile — indemne —
    // atterrit sur la case libre la plus proche d'elle. Le lancer EST l'action du tour du lanceur.

    /// <summary>
    /// Alliés que le porteur de « Chair à canon » posté sur <paramref name="from"/> peut empoigner : ceux au
    /// CONTACT (les 8 cases voisines). Vide s'il n'a pas le trait ou si ce n'est pas son tour.
    /// </summary>
    public List<Cell> ThrowableAllies(Cell from)
    {
        var result = new List<Cell>();
        var unit = ActiveUnitAt(from);
        if (unit == null || !unit.HasTrait(Trait.ChairACanon))
            return result;

        foreach (var (cell, other) in Units())
            if (!ReferenceEquals(other, unit) && other.Faction == unit.Faction && other.IsAlive
                && ChebyshevDistance(from, cell) == 1)
                result.Add(cell);
        return result;
    }

    /// <summary>
    /// Cibles d'un lancer depuis <paramref name="from"/> : les ennemis à <see cref="ChairACanonRange"/> cases
    /// ou moins. C'est un JET, pas un tir : ni motif de domaine ni ligne de vue — seule la distance compte.
    /// Vide s'il n'y a personne à empoigner (le lancer n'aurait pas de projectile).
    /// </summary>
    public List<Cell> ThrowTargets(Cell from)
    {
        var result = new List<Cell>();
        var unit = ActiveUnitAt(from);
        if (unit == null || !unit.HasTrait(Trait.ChairACanon) || ThrowableAllies(from).Count == 0)
            return result;

        foreach (var (cell, other) in Units())
            if (other.Faction != unit.Faction && other.IsAlive && ChebyshevDistance(from, cell) <= ChairACanonRange)
                result.Add(cell);
        return result;
    }

    /// <summary>
    /// Case où retombe le projectile : la case LIBRE et praticable la plus proche de <paramref name="target"/>,
    /// en anneaux croissants autour d'elle, puis par ordre de grille à distance égale (déterminisme). La case
    /// de la cible elle-même est exclue tant qu'elle est occupée ; elle devient éligible si le coup l'a tuée.
    /// <c>null</c> si tout est bouché — le lancer est alors refusé.
    /// </summary>
    public Cell? ThrowLanding(Cell target, Unit thrown)
    {
        Cell? best = null;
        var bestDistance = int.MaxValue;
        for (var column = 0; column < Width; column++)
        for (var row = 0; row < Height; row++)
        {
            var cell = new Cell(column, row);
            // Case vide, praticable (ni mur ni eau — sauf pour qui vole) et non interdite au camp du projectile.
            if (UnitAt(cell) != null || BlocksPlayerLanding(cell, thrown)
                || (BlocksMovement(cell) && !thrown.HasTrait(Trait.Vol)))
                continue;
            var distance = ChebyshevDistance(target, cell);
            if (distance >= bestDistance)
                continue;
            best = cell;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// Lance l'allié de <paramref name="allyCell"/> sur l'ennemi de <paramref name="target"/> :
    /// <see cref="MoveKind.Killed"/> si la cible tombe, <see cref="MoveKind.Attacked"/> si elle tient,
    /// <see cref="MoveKind.Invalid"/> si le lancer est illégal (pas le trait, allié hors contact, cible hors
    /// portée, ou nulle part où faire retomber le projectile). Le lancer consomme le tour du lanceur.
    /// </summary>
    public MoveKind TryThrow(Cell from, Cell allyCell, Cell target)
    {
        var unit = ActiveUnitAt(from);
        if (unit == null || !ThrowableAllies(from).Contains(allyCell) || !ThrowTargets(from).Contains(target))
            return MoveKind.Invalid;
        if (UnitAt(allyCell) is not { } ally || UnitAt(target) is not { } victim)
            return MoveKind.Invalid;

        ResetActionFx();
        _lastThrow = null;

        // Le projectile quitte sa case AVANT le coup : il ne doit ni border une ligne ni encaisser quoi que
        // ce soit, et sa case libérée peut servir de point de chute.
        _units[allyCell.Column, allyCell.Row] = null;

        ApplyDamage(victim, EffectiveDamage(unit, from, victim, target), unit);
        var killed = !victim.IsAlive && !TryReviveWithEquipment(victim);
        if (killed)
        {
            unit.RecordKill();
            _units[target.Column, target.Row] = null;
            OnUnitDied(victim, target);
        }

        // Point de chute : au plus près de la cible. Tout bouché (cas de figure très rare) → le projectile
        // revient d'où il vient plutôt que de disparaître.
        var landing = ThrowLanding(target, ally) ?? allyCell;
        _units[landing.Column, landing.Row] = ally;
        _lastThrow = (allyCell, landing, target, killed);

        EndTurn();
        return killed ? MoveKind.Killed : MoveKind.Attacked;
    }

    /// <summary>
    /// Dernier lancer de « Chair à canon » : case de départ du projectile, case d'arrivée, cible visée, et si
    /// elle est tombée. <c>null</c> hors lancer. La scène y lit de quoi animer la trajectoire.
    /// </summary>
    public (Cell From, Cell To, Cell Target, bool Killed)? LastThrow => _lastThrow;

    private (Cell From, Cell To, Cell Target, bool Killed)? _lastThrow;

    /// <summary>Vrai si cette mise à mort ouvre un tour bonus (cf. <see cref="ExtraTurnDomaine"/>).</summary>
    private bool GrantsExtraTurn(Unit attacker, MoveKind kind) =>
        kind == MoveKind.Killed
        && !_extraTurnUsed
        && ExtraTurnDomaine is { } d && attacker.Domaine == d
        && attacker.Faction == Faction.Player
        && CellOf(attacker) != null;

    // ─── TRAITS : dégâts effectifs, formes d'attaque, réactions ───────────────────────────────────

    /// <summary>PV rendus au porteur de « Sacrifice » chaque fois qu'il frappe un des siens (plafonnés à ses PV max).</summary>
    public const int SacrificeHeal = 12;

    /// <summary>Portée du lancer de « Chair à canon », en cases (distance de Chebyshev depuis le lanceur).</summary>
    public const int ChairACanonRange = 3;

    private const int BushReduction = 4;         // -4 dégâts quand la cible est sur un buisson (couvert)
    /// <summary>Réduction de dégâts de BASE du trait « Rempart » (avant bonus d'arbre « Rempart renforcé »).</summary>
    public const int BaseRempartReduction = 4;   // -4 dégâts d'une attaque à distance (>= 2)
    private const int DuellisteReduction = 4;    // -4 dégâts d'une attaque au corps à corps
    private const int AuraPuissanceBonus = 3;    // +3 puissance offerte par un allié « Aura de puissance » adjacent
    private const int AuraCeleriteBonus = 1;     // +1 Déplacement offert par un allié « Aura de célérité » adjacent
    /// <summary>Bonus de puissance de BASE par allié adjacent du trait « Formation » (avant « Formation renforcée » de l'arbre).</summary>
    public const int BaseFormationBonus = 2;     // +2 puissance par allié adjacent (trait « Formation »)
    /// <summary>Bonus de dégâts de BASE du trait « Tueur de géants » (avant « Tueur de géant renforcé » de l'arbre).</summary>
    public const int BaseGiantSlayerBonus = 5;   // +5 dégâts contre une cible aux PV ACTUELS supérieurs
    /// <summary>Bonus de puissance du trait « Rage » : accordé UNE fois, à la première mort d'un allié du combat
    /// (non cumulable — les morts suivantes ne l'augmentent pas).</summary>
    public const int RagePowerBonus = 7;
    private const int OrageDamage = 3;           // dégât fixe de l'orage (trait « Orage »)
    private const int TempeteDamage = 3;         // dégât fixe de la tempête (trait « Tempête ») — elle frappe PLUS LARGE, pas plus fort
    /// <summary>Dégât de BASE autour du porteur d'« Impact » (avant le bonus d'arbre « Impact renforcé »).</summary>
    public const int BaseImpactDamage = 5;       // dégât fixe autour du porteur d'« Impact » (déplacement/attaque)
    private const int LoupSolitairePower = 3;    // +3 puissance quand le porteur de « Loup solitaire » n'a AUCUN allié adjacent
    private const int LoupSolitaireReduction = 2; // -2 dégâts subis dans les mêmes conditions
    private const int ReculeSlamDamage = 5;      // dégât bonus quand « Recule » plaque la cible contre un obstacle
    private const int LienPuissanceBonus = 2;    // +2 puissance par allié dans la PORTÉE DE DÉPLACEMENT (trait « Lien de puissance »)
    /// <summary>Distance de Chebyshev MINIMALE d'un coup direct pour compter comme « coup à distance » — la
    /// source de points de commandement du commandant du Fou (cf. <see cref="Unit.RecordRangedHit"/>).</summary>
    public const int CommanderRangedHitDistance = 2;

    /// <summary>Dégât fixe de foudre infligé par <paramref name="unit"/> à l'attaque (Tempête &gt; Orage &gt; 0 si aucun).</summary>
    public static int StormDamageFor(Unit unit) =>
        unit.HasTrait(Trait.Tempete) ? TempeteDamage
        : unit.HasTrait(Trait.Orage) ? OrageDamage
        : 0;

    /// <summary>Ennemis touchés par l'« Impact » de la DERNIÈRE action (case + dégâts réels) — vide sinon. Pour le feedback.</summary>
    public IReadOnlyList<(Cell Cell, int Damage)> LastImpactHits => _impactHits;

    /// <summary>Les 8 cases voisines frappées par l'« Impact » de la DERNIÈRE action (qu'un ennemi y fût ou non ;
    /// cases hors plateau incluses, ignorées au rendu). Vide sinon. Pour le tremblement des tuiles de l'AoE.</summary>
    public IReadOnlyList<Cell> LastImpactZone => _impactZone;

    /// <summary>Union des voisinages 3×3 des porteurs de « Séisme » lors du DERNIER <see cref="ApplySeismes"/>
    /// (cases hors plateau incluses, ignorées au rendu). Pour le tremblement des tuiles de l'AoE.</summary>
    public IReadOnlyList<Cell> LastSeismeZone => _seismeZone;

    /// <summary>Résultat du « Recule » de la DERNIÈRE attaque : case d'arrivée de la cible + dégât de plaquage
    /// (0 si elle a glissé librement) ; <c>null</c> si aucun recul n'a eu lieu. Pour le feedback.</summary>
    public (Cell To, int SlamDamage)? LastRecule => _lastRecule;

    /// <summary>Replis d'« Esquive » déclenchés par la DERNIÈRE action (case de départ → case d'arrivée), dans
    /// l'ordre où ils ont eu lieu. Vide si personne ne s'est replié. Pour le feedback.</summary>
    public IReadOnlyList<(Cell From, Cell To)> LastDodges => _dodges;

    /// <summary>Riposte de la DERNIÈRE attaque : case du riposteur (après un éventuel recul) → case de l'assaillant,
    /// dégâts réellement infligés, et si l'assaillant en est mort. <c>null</c> si aucune riposte. Pour le feedback.</summary>
    public (Cell From, Cell To, int Damage, bool Killed)? LastRiposte => _lastRiposte;

    /// <summary>« Épines » de la DERNIÈRE action : pour chaque renvoi, la case de l'assaillant piqué, les dégâts
    /// renvoyés et s'il en est mort. Vide si aucun renvoi. Pour le feedback (chiffre + mot-clé sur l'assaillant).</summary>
    public IReadOnlyList<(Cell Cell, int Damage, bool Killed)> LastThorns => _thorns;

    /// <summary>« Interception(s) » du DERNIER déplacement : pour chaque intercepteur qui a frappé le mobile, sa case
    /// → la case d'arrivée du mobile, les dégâts réels et si le mobile en est mort (ordre de frappe). Vide sinon.
    /// Pour rejouer l'animation d'attaque de chaque intercepteur (le moteur a déjà appliqué les dégâts).</summary>
    public IReadOnlyList<(Cell From, Cell To, int Damage, bool Killed)> LastInterceptions => _interceptions;

    /// <summary>« Transpercement » de la DERNIÈRE attaque : case de l'ennemi transpercé (derrière la cible), dégâts
    /// réellement infligés (&gt; 0) et direction (dc, dr) du coup ; <c>null</c> si aucun transpercement (ou esquivé).
    /// Pour le feedback : recul + chiffre + mot-clé sur le pion transpercé.</summary>
    public (Cell Cell, int Damage, int Dc, int Dr)? LastPierce => _lastPierce;

    /// <summary>« Glace » : chemin de glissade de la DERNIÈRE action (déplacement ou avance sur un kill) — cases
    /// traversées, case de DÉPART (là où l'unité s'est arrêtée) incluse en tête et case de REPOS en queue.
    /// <c>null</c> si aucune glissade. Pour le feedback (glissement + son).</summary>
    public IReadOnlyList<Cell>? LastSlide => _lastSlide;

    private static readonly (int Dc, int Dr)[] Neighbors8 =
        { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };

    private static int ChebyshevDistance(Cell a, Cell b) =>
        System.Math.Max(System.Math.Abs(a.Column - b.Column), System.Math.Abs(a.Row - b.Row));

    /// <summary>
    /// Contact DIRECT : cases orthogonalement adjacentes (haut/bas/gauche/droite). Les diagonales n'en
    /// font PAS partie — c'est ce qui laisse « Rempart » agir même sur un assaillant collé en diagonale.
    /// </summary>
    private static bool IsDirectContact(Cell a, Cell b) =>
        System.Math.Abs(a.Column - b.Column) + System.Math.Abs(a.Row - b.Row) == 1;

    /// <summary>Vrai si une case adjacente porte un allié de <paramref name="faction"/> avec ce trait. Par défaut
    /// le voisinage 8 (diagonales comprises, comme les auras de puissance/rempart) ; <paramref name="orthogonalOnly"/>
    /// le restreint au CONTACT DIRECT (haut/bas/gauche/droite) — c'est le cas de l'« Aura de célérité », volontairement
    /// plus exigeante que les autres auras pour ne pas être trop forte.</summary>
    private bool HasAdjacentAlly(Cell cell, Faction faction, string trait, bool orthogonalOnly = false)
    {
        foreach (var (dc, dr) in Neighbors8)
        {
            if (orthogonalOnly && dc != 0 && dr != 0)
                continue;   // hors diagonales
            if (UnitAt(new Cell(cell.Column + dc, cell.Row + dr)) is { } u
                && u.Faction == faction && u.HasTrait(trait))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Vrai si l'unité de <paramref name="cell"/> bénéficie de <paramref name="auraTrait"/> grâce à un allié
    /// ADJACENT qui le porte. Effet CONTEXTUEL : il tient au placement, pas à la fiche du pion (ni classe, ni
    /// équipement, ni arbre) — l'UI ne peut donc pas le déduire seule et doit le demander ici.
    /// </summary>
    public bool BenefitsFromAura(Cell cell, string auraTrait) =>
        UnitAt(cell) is { } u && HasAdjacentAlly(cell, u.Faction, auraTrait,
            orthogonalOnly: auraTrait == Trait.AuraDeCelerite);   // la célérité ne compte pas les diagonales

    /// <summary>
    /// Puissance que l'unité de <paramref name="cell"/> tient de l'AURA de puissance d'un allié adjacent,
    /// exactement comme dans <see cref="EffectiveDamage"/> — la carte doit afficher la même valeur que celle
    /// réellement infligée.
    /// </summary>
    public int AuraPowerBonus(Cell cell)
    {
        if (UnitAt(cell) is not { } u)
            return 0;
        return HasAdjacentAlly(cell, u.Faction, Trait.AuraDePuissance) ? AuraPuissanceBonus : 0;
    }

    /// <summary>Bonus de DÉPLACEMENT que l'unité de <paramref name="cell"/> tient de l'« Aura de célérité » d'un allié
    /// adjacent (+<see cref="AuraCeleriteBonus"/>), ou 0 sinon. Contextuel au placement, comme <see cref="AuraPowerBonus"/> :
    /// la carte doit afficher la même portée que celle réellement utilisée par <see cref="LegalMoves(Cell, List{Cell})"/>.</summary>
    public int MoveRangeBonus(Cell cell) =>
        UnitAt(cell) is { } u && HasAdjacentAlly(cell, u.Faction, Trait.AuraDeCelerite, orthogonalOnly: true) ? AuraCeleriteBonus : 0;

    /// <summary>
    /// Puissance qu'une unité « Formation » tient de ses alliés ADJACENTS (+<see cref="BaseFormationBonus"/> par
    /// allié), ou 0 si elle n'a pas le trait. Effet CONTEXTUEL au placement — comme les auras, l'UI ne peut
    /// pas le déduire de la fiche et doit le demander ici pour afficher la vraie puissance de combat.
    /// </summary>
    public int FormationPowerBonus(Cell cell) =>
        UnitAt(cell) is { } u && u.HasTrait(Trait.Formation)
            ? PerAllyFormationBonus(u) * AdjacentAllyCount(cell, u.Faction)
            : 0;

    /// <summary>
    /// Cases des ALLIÉS ADJACENTS qui alimentent la « Formation » du porteur posé sur <paramref name="cell"/>,
    /// dans le tampon fourni (vidé au passage). Vide s'il n'a pas le trait. Même parcours que
    /// <see cref="AdjacentAllyCount"/>, dont <see cref="FormationPowerBonus"/> tire le bonus — l'UI s'en sert
    /// pour montrer d'où il vient (halo + arcs), les deux DOIVENT donc rester d'accord. Pendant du
    /// <see cref="LienDePuissanceAllies(Cell, List{Cell})"/> pour l'autre trait de placement.
    /// </summary>
    public void FormationAllies(Cell cell, List<Cell> result)
    {
        result.Clear();
        if (UnitAt(cell) is not { } u || !u.HasTrait(Trait.Formation))
            return;
        foreach (var (dc, dr) in Neighbors8)
        {
            var n = new Cell(cell.Column + dc, cell.Row + dr);
            if (UnitAt(n) is { } ally && ally.Faction == u.Faction)
                result.Add(n);
        }
    }

    /// <summary>
    /// Bonus de puissance CONTEXTUEL total d'une unité : ce qu'elle tient de son PLACEMENT — auras alliées
    /// (<see cref="AuraPowerBonus"/>) et Formation (<see cref="FormationPowerBonus"/>) — et que sa fiche ne
    /// laisse pas deviner. La carte l'affiche en « +N » sur la puissance pour rester fidèle aux dégâts
    /// réellement infligés. Le Berserk (kills) et la Rage (alliés morts), eux, dépendent de l'état du pion
    /// (pas du placement) et se calculent à part.
    /// </summary>
    public int ContextualPowerBonus(Cell cell) =>
        AuraPowerBonus(cell) + FormationPowerBonus(cell) + LienPuissancePowerBonus(cell) + LoupSolitairePowerBonus(cell)
        + PositionStrategiquePowerBonus(cell)
        + (UnitAt(cell) is { } u ? CrossKillPowerBonus(u) : 0);

    /// <summary>
    /// « Loup solitaire » : vrai si <paramref name="unit"/>, postée en <paramref name="cell"/>, porte le trait
    /// ET n'a AUCUN allié sur les 8 cases voisines. C'est la condition unique du trait : elle vaut aussi bien
    /// pour son bonus de puissance que pour sa réduction de dégâts.
    /// </summary>
    private bool IsLoneWolf(Unit unit, Cell cell) =>
        unit.HasTrait(Trait.LoupSolitaire) && AdjacentAllyCount(cell, unit.Faction) == 0;

    /// <summary>Puissance qu'une unité « Loup solitaire » ISOLÉE tient de son trait (0 sinon). Effet CONTEXTUEL
    /// au placement, comme les auras : l'UI doit le demander ici pour afficher la vraie puissance.</summary>
    public int LoupSolitairePowerBonus(Cell cell) =>
        UnitAt(cell) is { } u && IsLoneWolf(u, cell) ? LoupSolitairePower : 0;

    /// <summary>Nombre d'unités alliées (même <paramref name="faction"/>) adjacentes à <paramref name="cell"/> (trait « Formation »).</summary>
    private int AdjacentAllyCount(Cell cell, Faction faction)
    {
        var count = 0;
        foreach (var (dc, dr) in Neighbors8)
            if (UnitAt(new Cell(cell.Column + dc, cell.Row + dr)) is { } u && u.Faction == faction)
                count++;
        return count;
    }

    /// <summary>
    /// Puissance qu'une unité « Lien de puissance » tient des alliés situés dans sa PORTÉE DE DÉPLACEMENT
    /// (+<see cref="LienPuissanceBonus"/> par allié), ou 0 si elle n'a pas le trait. « Portée de déplacement »
    /// = les cases qu'atteint son motif de déplacement jusqu'à sa portée, exactement comme
    /// <see cref="LegalMoves(Cell, System.Collections.Generic.List{Cell})"/> (obstacles et unités bornent la
    /// ligne) ; on y compte les ALLIÉS croisés. Effet CONTEXTUEL au placement — comme la Formation et les auras,
    /// l'UI ne peut pas le déduire de la fiche et doit le demander ici pour afficher la vraie puissance.
    /// </summary>
    public int LienPuissancePowerBonus(Cell cell)
    {
        if (UnitAt(cell) is not { } u || !u.HasTrait(Trait.LienDePuissance))
            return 0;
        _lienBuffer.Clear();
        AppendAlliesInMoveRange(cell, u, _lienBuffer);
        return LienPuissanceBonus * _lienBuffer.Count;
    }

    /// <summary>
    /// Cases des ALLIÉS qui alimentent le « Lien de puissance » du porteur posé sur <paramref name="cell"/>.
    /// Liste vide s'il n'a pas le trait. Même calcul que <see cref="LienPuissancePowerBonus"/> — l'UI s'en sert
    /// pour montrer d'où vient le bonus (halo sur les alliés liés), donc les deux DOIVENT rester d'accord.
    /// </summary>
    public List<Cell> LienDePuissanceAllies(Cell cell)
    {
        var result = new List<Cell>();
        LienDePuissanceAllies(cell, result);
        return result;
    }

    /// <summary>
    /// Même chose dans un tampon FOURNI (vidé au passage) : l'UI dessine les liens de TOUS les porteurs à
    /// chaque frame, elle ne peut pas allouer une liste par porteur et par frame.
    /// </summary>
    public void LienDePuissanceAllies(Cell cell, List<Cell> result)
    {
        result.Clear();
        if (UnitAt(cell) is { } u && u.HasTrait(Trait.LienDePuissance))
            AppendAlliesInMoveRange(cell, u, result);
    }

    /// <summary>
    /// Ajoute à <paramref name="result"/> les alliés (même faction que <paramref name="unit"/>) situés sur les
    /// rayons de son motif de DÉPLACEMENT depuis <paramref name="from"/>, jusqu'à sa portée (trait « Lien de
    /// puissance »). RIEN ne borne un rayon à part le bord du plateau : ni le terrain, ni un pion — un allié
    /// n'en masque donc pas un autre plus loin sur la même ligne, et le joueur peut lire ses liens d'un coup
    /// d'œil sans simuler les blocages. C'est ce qui distingue ce parcours de
    /// <see cref="LegalMoves(Cell, System.Collections.Generic.List{Cell})"/>, qui borne au premier obstacle.
    /// </summary>
    private void AppendAlliesInMoveRange(Cell from, Unit unit, List<Cell> result)
    {
        var vectors = Movement.Vectors(unit.Domaine);

        if (Movement.Kind(unit.Domaine) == MovementKind.Jump)
        {
            foreach (var offset in vectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (UnitAt(to) is { } j && j.Faction == unit.Faction)
                    result.Add(to);
            }
            return;
        }

        var range = unit.MoveRange
                    + (HasAdjacentAlly(from, unit.Faction, Trait.AuraDeCelerite, orthogonalOnly: true) ? AuraCeleriteBonus : 0);
        foreach (var dir in vectors)
            for (var step = 1; step <= range; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to))
                    break;   // seul le bord du plateau arrête le rayon
                if (_units[to.Column, to.Row] is { } occ && occ.Faction == unit.Faction)
                    result.Add(to);
            }
    }

    /// <summary>
    /// PUISSANCE EFFECTIVE de l'unité posée sur <paramref name="cell"/> : sa puissance de base plus TOUS ses
    /// bonus offensifs contextuels — Berserk (+1 par ennemi tué à vie), Rage (+7 à la première mort d'un allié),
    /// Aura de puissance d'un allié adjacent, Formation (+2 par allié adjacent), Lien de puissance (+2 par allié
    /// dans sa portée de déplacement). C'est la valeur affichée sur
    /// sa carte, et la SEULE source de
    /// « puissance » du moteur : tout ce qui se dit « une fraction de la puissance » (<see cref="HealAmount"/>)
    /// en dérive, pour que les bonus profitent partout de la même façon. Les réductions de la CIBLE ne sont
    /// pas ici : elles s'appliquent à l'arrivée, dans <see cref="EffectiveDamage"/>.
    /// </summary>
    private int EffectivePower(Unit unit, Cell cell)
    {
        var power = unit.Damage;
        if (unit.HasTrait(Trait.Berserk))
            power += unit.Kills;      // « Berserk » : +1 puissance par ennemi tué, cumulé sur la run (cf. Unit.Kills)
        if (unit.HasTrait(Trait.Rage))
            power += unit.RagePower;  // « Rage » : +7 dès la première mort d'un allié ce combat (non cumulable, cf. Unit.RagePower)
        if (unit.HasTrait(Trait.Vengeance))
            power += unit.VengeancePower;   // « Vengeance » : +7 à la mort d'un allié, pour UN tour seulement
        if (IsLoneWolf(unit, cell))
            power += LoupSolitairePower;   // « Loup solitaire » : isolé, il frappe plus fort
        power += AuraPowerBonus(cell);
        power += FormationPowerBonus(cell);
        power += LienPuissancePowerBonus(cell);   // « Lien de puissance » : +2 par allié dans la portée de déplacement
        power += PositionStrategiquePowerBonus(cell);   // DUO : +5 tant qu'un allié est à portée
        power += CrossKillPowerBonus(unit);             // DUO : +1 par tranche de 3 kills du COMPAGNON
        power += unit.RoquePower;                       // DUO : charge mise en réserve par un ROQUE (une attaque)
        power += unit.InheritedPower;                   // DUO : puissance léguée par un meneur tombé EN COMBAT
        return System.Math.Max(0, power);
    }

    /// <summary>
    /// Dégâts EFFECTIFS d'une attaque, traits inclus : la <see cref="EffectivePower"/> de l'attaquant, moins
    /// Rempart / Aura de rempart (partout SAUF au contact direct orthogonal), Duelliste (corps à corps) et
    /// le couvert d'un buisson. Borné à 0.
    /// </summary>
    /// <param name="halfPower">
    /// Le coup ne vaut que la MOITIÉ de la puissance (arrondie vers le bas comme « Épines ») : c'est le cas du
    /// « Séisme ». La réduction porte sur la puissance AVANT les défenses de la cible (Rempart Duelliste
    /// couvert…) qui s'appliquent ensuite normalement.
    /// </param>
    private int EffectiveDamage(Unit attacker, Cell attackerCell, Unit victim, Cell victimCell, bool halfPower = false)
    {
        var dmg = EffectivePower(attacker, attackerCell);
        if (halfPower)
            dmg /= 2;

        // « Tueur de géants » : +5 (ou +7 « renforcé ») quand la cible a PLUS de PV ACTUELS que l'attaquant
        // (autrement dit l'attaquant est le plus BLESSÉ des deux). Comparaison AVANT que ce coup ne porte.
        // Bonus offensif (soumis comme le reste aux réductions de la cible : Rempart, Duelliste, couvert).
        if (attacker.HasTrait(Trait.TueurDeGeants) && victim.Hp > attacker.Hp)
            dmg += GiantSlayerDamageFor(attacker);

        var distance = ChebyshevDistance(attackerCell, victimCell);
        var shielded = victim.HasTrait(Trait.Rempart)
            || HasAdjacentAlly(victimCell, victim.Faction, Trait.AuraDeRempart);
        // Rempart protège PARTOUT, sauf quand l'assaillant est collé EN LIGNE DROITE (contact direct).
        // Une attaque en diagonale, même à une case, reste réduite : seul le corps à corps orthogonal
        // passe la garde.
        if (shielded && !IsDirectContact(attackerCell, victimCell))
            dmg -= RempartReductionFor(victim);
        if (distance == 1 && victim.HasTrait(Trait.Duelliste))
            dmg -= DuellisteReduction;
        if (IsLoneWolf(victim, victimCell))    // « Loup solitaire » : isolé, il encaisse mieux
            dmg -= LoupSolitaireReduction;
        if (IsCover(victimCell))               // cible à couvert dans un buisson
            dmg -= BushReduction;

        return System.Math.Max(0, dmg);
    }

    /// <summary>
    /// Applique des dégâts. Le porteur d'« Épines » renvoie aussitôt la moitié du coup à son assaillant
    /// (cf. <see cref="ApplyEpines"/>), puis le porteur d'« Esquive » qui SURVIT se replie
    /// (cf. <see cref="ApplyEsquive"/>) : il encaisse d'abord, puis quitte la case.
    /// </summary>
    private void ApplyDamage(Unit unit, int amount, Unit? attacker)
    {
        if (amount <= 0)
            return;
        // « Bouclier humain » (BRUTE) : un allié à portée prend le coup ENTIER à la place du porteur.
        if (TryShield(unit, amount, attacker))
            return;
        // « Lien d'amitié » (DUO) : une partie du coup part sur les alliés liés à portée ; la victime
        // n'encaisse que ce qu'il en reste.
        amount = ShareFriendshipDamage(unit, amount, attacker);
        if (amount <= 0)
            return;
        unit.TakeDamage(amount);
        unit.RecordHit();               // coup RÉELLEMENT encaissé (0 exclu) — points d'un commandant
        attacker?.RecordDamage(amount);  // dégâts RÉELLEMENT infligés — récap de fin de run (dégâts par type)
        // Qui a entamé cette unité : lu à sa mort par les règles de campagne (mise à mort « à deux » du DUO).
        // Posé ICI, le seul passage obligé des dégâts : éclats, épines et rebonds comptent comme un coup direct.
        if (attacker != null)
            unit.RecordDamagedBy(attacker);
        if (attacker != null && unit.HasTrait(Trait.Epines))
            ApplyEpines(unit, attacker, amount);
        if (unit.IsAlive && unit.HasTrait(Trait.Esquive))
            ApplyEsquive(unit);
    }

    /// <summary>
    /// « Épines » : le porteur renvoie à son assaillant la MOITIÉ (arrondie vers le bas) des dégâts qu'il vient
    /// d'encaisser — quelle que soit la source (attaque, impact, foudre, séisme, riposte, plaquage). Le renvoi
    /// est un dégât BRUT : aucune réduction de l'assaillant ne s'y applique, et il peut le tuer (l'assaillant
    /// est alors retiré du plateau, la mise à mort créditée au porteur). JAMAIS relayé : les dégâts d'un renvoi
    /// n'en déclenchent pas un autre (<see cref="_reflecting"/>), y compris entre deux porteurs d'« Épines ».
    /// Noté dans <see cref="_thorns"/> pour le feedback.
    /// </summary>
    private void ApplyEpines(Unit victim, Unit attacker, int taken)
    {
        var back = taken / 2;
        if (_reflecting || back <= 0)
            return;
        if (CellOf(attacker) is not { } attackerCell)
            return;   // assaillant déjà hors du plateau (mort en chaîne) : rien à renvoyer

        _reflecting = true;
        try
        {
            ApplyDamage(attacker, back, victim);
        }
        finally
        {
            _reflecting = false;
        }
        // La case peut avoir changé (« Esquive » de l'assaillant) : on relit sa position avant de le retirer.
        var restCell = CellOf(attacker) ?? attackerCell;
        _thorns.Add((restCell, back, !attacker.IsAlive));
        RemoveDeadAt(restCell, victim);
    }

    /// <summary>
    /// Transferts de coup par « Bouclier humain » de l'action courante : case de l'allié qui a encaissé,
    /// dégâts pris, et s'il est tombé. Vidés à chaque action (cf. <see cref="ResetActionFx"/>) — la scène y
    /// lit de quoi animer le sacrifice.
    /// </summary>
    public IReadOnlyList<(Cell Cell, int Damage, bool Killed)> LastShieldHits => _shieldHits;

    private readonly List<(Cell Cell, int Damage, bool Killed)> _shieldHits = new();

    /// <summary>Garde anti-relais : un coup déjà détourné ne se détourne pas une seconde fois.</summary>
    private bool _shielding;

    /// <summary>
    /// « BOUCLIER HUMAIN » (BRUTE) : le porteur attaqué pousse un ALLIÉ devant lui — l'allié le plus proche
    /// dans sa PORTÉE D'ATTAQUE encaisse le coup ENTIER à sa place, avec tout ce que cela entraîne (épines,
    /// esquive, mort). Le porteur, lui, ne perd pas un PV : ce n'est pas un partage mais un remplacement.
    /// JAMAIS relayé — l'allié poussé ne peut pas à son tour se cacher derrière un autre.
    /// Renvoie vrai si le coup a bien été détourné (l'appelant n'a alors plus rien à appliquer).
    /// </summary>
    private bool TryShield(Unit unit, int amount, Unit? attacker)
    {
        if (_shielding || !unit.HasTrait(Trait.BouclierHumain))
            return false;
        if (ShieldTargetFor(unit) is not { } shieldCell || UnitAt(shieldCell) is not { } ally)
            return false;

        _shielding = true;
        try
        {
            ApplyDamage(ally, amount, attacker);
        }
        finally
        {
            _shielding = false;
        }

        // L'allié a pu se replier (« Esquive ») : on relit sa case avant de constater sa chute.
        var restCell = CellOf(ally) ?? shieldCell;
        _shieldHits.Add((restCell, amount, !ally.IsAlive));
        RemoveDeadAt(restCell, attacker);   // tombé pour le porteur : la mise à mort revient à l'assaillant
        return true;
    }

    /// <summary>
    /// Case de l'allié qui prendrait le coup à la place de <paramref name="unit"/> (« Bouclier humain ») :
    /// le plus proche dans sa portée d'attaque, départagé par l'ordre de la grille pour rester déterministe.
    /// <c>null</c> s'il n'y a personne — le porteur encaisse alors lui-même. PUBLIC : l'UI s'en sert pour
    /// montrer qui va trinquer.
    /// </summary>
    public Cell? ShieldTargetFor(Unit unit)
    {
        if (CellOf(unit) is not { } here)
            return null;

        var reach = EffectiveAttackRange(unit, here);
        Cell? best = null;
        var bestDistance = int.MaxValue;
        foreach (var (cell, other) in Units())
        {
            if (ReferenceEquals(other, unit) || other.Faction != unit.Faction || !other.IsAlive)
                continue;
            var distance = ChebyshevDistance(here, cell);
            if (distance > reach || distance >= bestDistance)
                continue;
            best = cell;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// « Esquive » : le porteur qui vient d'ENCAISSER un coup (et y a survécu) se replie IMMÉDIATEMENT sur une
    /// case de sa PORTÉE DE DÉPLACEMENT — mêmes règles qu'un déplacement normal (motif du domaine, obstacles,
    /// cases interdites), mais HORS DE SON TOUR et sans le consommer. Il vise en PRIORITÉ une case menacée par
    /// AUCUN ennemi ; s'il n'en existe pas, il file vers la MOINS menacée. À égalité, la case est tirée au sort
    /// (cf. <see cref="_rng"/>). Le repli est un BOND : il ne déclenche ni « Interception » ni « Impact » et ne
    /// dérape pas sur la glace. Sans aucune case d'arrivée, le porteur reste sur place.
    /// Les replis sont notés dans <see cref="_dodges"/> pour le feedback.
    /// </summary>
    private void ApplyEsquive(Unit unit)
    {
        if (CellOf(unit) is not { } from)
            return;

        var moves = new List<Cell>();
        AppendLegalMoves(from, unit, moves);
        if (moves.Count == 0)
            return;   // encerclé / bloqué : aucun repli possible

        // Carte de menace des ennemis du porteur, calculée UNE fois : combien de tirs couvrent chaque case.
        // (Le porteur est encore sur `from` : son corps borne les lignes, comme au moment où il encaisse.)
        var threat = new Dictionary<Cell, int>();
        var buffer = new List<Cell>();
        foreach (var (cell, other) in Units())
        {
            if (other.Faction == unit.Faction)
                continue;
            ThreatenedCells(cell, buffer);
            foreach (var cover in buffer)
                threat[cover] = threat.TryGetValue(cover, out var n) ? n + 1 : 1;
        }

        // Cases les MOINS menacées (0 si une case sûre existe) ; on tire au sort entre les ex aequo.
        var best = int.MaxValue;
        var pool = new List<Cell>();
        foreach (var move in moves)
        {
            var count = threat.TryGetValue(move, out var n) ? n : 0;
            if (count > best)
                continue;
            if (count < best)
            {
                best = count;
                pool.Clear();
            }
            pool.Add(move);
        }

        var to = pool[_rng.Next(pool.Count)];
        MoveUnit(from, to);
        _dodges.Add((from, to));
    }

    /// <summary>Dégâts EFFECTIFS qu'infligerait l'attaque de <paramref name="from"/> sur
    /// <paramref name="target"/> (traits inclus), bornés aux PV de la cible — pour l'affichage.</summary>
    public int PreviewDamage(Cell from, Cell target)
    {
        var attacker = UnitAt(from);
        var victim = UnitAt(target);
        if (attacker == null || victim == null)
            return 0;
        // « Lien d'amitié » : la cible ne prend que SA part. On l'annonce ici, sinon l'aperçu (et le chiffre
        // qui jaillit à l'impact) promettrait le coup entier alors que ses camarades en portent la moitié.
        var raw = AfterFriendshipShare(victim, target, EffectiveDamage(attacker, from, victim, target));
        return System.Math.Min(raw, victim.Hp);
    }

    /// <summary>Part « Tueur de géants » des dégâts de l'attaque de <paramref name="from"/> sur
    /// <paramref name="target"/>, ou 0 si le trait ne s'applique pas (la cible n'a pas plus de PV ACTUELS que
    /// l'attaquant). Sert au feedback : afficher le « +N » du bonus à côté du chiffre de dégâts.</summary>
    public int GiantSlayerBonusFor(Cell from, Cell target)
    {
        var attacker = UnitAt(from);
        var victim = UnitAt(target);
        if (attacker == null || victim == null)
            return 0;
        return attacker.HasTrait(Trait.TueurDeGeants) && victim.Hp > attacker.Hp ? GiantSlayerDamageFor(attacker) : 0;
    }

    /// <summary>
    /// « Séisme » : déclenché À LA FIN DU TOUR ADVERSE (appelé par la scène). Chaque unité de
    /// <paramref name="actor"/> portant le trait <see cref="Trait.Seisme"/> frappe les ENNEMIS des 8 cases
    /// autour d'elle pour la MOITIÉ de sa puissance (arrondie vers le bas), moins les défenses de chaque
    /// cible : la secousse touche tout le voisinage mais ne vaut pas une attaque en règle. Les morts
    /// sont retirés et crédités au porteur. Renvoie les (case, dégâts réellement infligés) pour le feedback.
    /// </summary>
    public IReadOnlyList<(Cell Cell, int Damage)> ApplySeismes(Faction actor)
    {
        _seismeZone.Clear();
        var hits = new List<(Cell, int)>();
        if (IsOver)
            return hits;

        // On fige les porteurs AVANT : le splash retire des unités et invaliderait une itération sur Units().
        var casterCells = Units()
            .Where(cu => cu.Unit.Faction == actor && cu.Unit.HasTrait(Trait.Seisme))
            .Select(cu => cu.Cell).ToList();

        // Zone de tremblement : les 8 voisines de chaque porteur (feedback), indépendamment des cibles réelles.
        foreach (var casterCell in casterCells)
            foreach (var (dc, dr) in Neighbors8)
                _seismeZone.Add(new Cell(casterCell.Column + dc, casterCell.Row + dr));

        _dodges.Clear();   // les replis d'« Esquive » du séisme (feedback), comme pour une action normale

        foreach (var casterCell in casterCells)
        {
            if (UnitAt(casterCell) is not { } caster || !caster.HasTrait(Trait.Seisme))
                continue;   // porteur tombé entre-temps (cas de bord)
            // Cibles FIGÉES avant application : une victime qui se replie (« Esquive ») ne doit pas être
            // frappée deux fois par le MÊME porteur si son bond la ramène dans sa zone.
            var victims = new List<Unit>();
            foreach (var (dc, dr) in Neighbors8)
            {
                var c = new Cell(casterCell.Column + dc, casterCell.Row + dr);
                if (UnitAt(c) is { } victim && victim.Faction != actor)
                    victims.Add(victim);
            }

            foreach (var victim in victims)
            {
                if (CellOf(victim) is not { } c)
                    continue;
                var before = victim.Hp;
                ApplyDamage(victim, EffectiveDamage(caster, casterCell, victim, c, halfPower: true), caster);
                var dealt = before - victim.Hp;
                if (dealt > 0)
                    hits.Add((c, dealt));
                RemoveDeadAt(c, caster);
            }
        }

        UpdateWinner();   // un séisme peut achever le dernier ennemi → victoire
        return hits;
    }


    /// <summary>Nombre MAX d'ennemis foudroyés par « Orage » (tirés au hasard s'il y en a davantage).</summary>
    private const int OrageMaxTargets = 3;

    /// <summary>Nombre MAX d'ennemis foudroyés par « Tempête » : c'est là qu'elle surpasse l'Orage (même dégât,
    /// plus de cibles).</summary>
    private const int TempeteMaxTargets = 5;

    /// <summary>Nombre d'ennemis que la foudre de <paramref name="unit"/> peut frapper (Tempête &gt; Orage &gt; 0 si aucun).</summary>
    private static int StormTargetsFor(Unit unit) =>
        unit.HasTrait(Trait.Tempete) ? TempeteMaxTargets
        : unit.HasTrait(Trait.Orage) ? OrageMaxTargets
        : 0;

    /// <summary>
    /// « Orage » / « Tempête » : à l'attaque, la foudre frappe jusqu'à <see cref="StormTargetsFor"/> ennemis du
    /// porteur TIRÉS AU HASARD (parmi tous, SAUF la cible directe <paramref name="target"/>), chacun pour un
    /// dégât FIXE (<paramref name="amount"/>, ni réduit par Rempart/couvert ni majoré par les traits offensifs —
    /// mais un porteur d'« Esquive » foudroyé se replie, via <see cref="ApplyDamage"/>). Tirage via <see cref="_rng"/>.
    /// Les cibles sont figées avant application (la grille change en cours).
    /// </summary>
    private void StormStrike(Unit attacker, Cell target, int amount)
    {
        var victims = new List<Cell>();
        foreach (var (cell, unit) in Units())
            if (unit.Faction != attacker.Faction && cell != target)
                victims.Add(cell);

        // Ne foudroie qu'un nombre borné d'ennemis : mélange partiel (Fisher-Yates) pour en tirer autant au hasard.
        var count = System.Math.Min(StormTargetsFor(attacker), victims.Count);
        for (var i = 0; i < count; i++)
        {
            var j = i + _rng.Next(victims.Count - i);
            (victims[i], victims[j]) = (victims[j], victims[i]);
        }

        for (var i = 0; i < count; i++)
        {
            if (UnitAt(victims[i]) is not { } u)
                continue;
            ApplyDamage(u, amount, attacker);
            RemoveDeadAt(victims[i], attacker);
        }
    }

    /// <summary>« Transpercement » : touche l'ennemi situé une case derrière la cible (même direction).</summary>
    private void PierceBehind(Cell from, Cell target, Unit attacker)
    {
        var dc = System.Math.Sign(target.Column - from.Column);
        var dr = System.Math.Sign(target.Row - from.Row);
        var behind = new Cell(target.Column + dc, target.Row + dr);
        if (UnitAt(behind) is not { } u || u.Faction == attacker.Faction)
            return;
        var before = u.Hp;
        ApplyDamage(u, EffectiveDamage(attacker, from, u, behind), attacker);
        if (before - u.Hp is > 0 and var dealt)   // rien à signaler si l'unité derrière a esquivé
            _lastPierce = (behind, dealt, dc, dr);
        RemoveDeadAt(behind, attacker);
    }

    /// <summary>
    /// « Glace » : si <paramref name="landing"/> est une tuile glissante et que l'unité ne vole pas, la fait
    /// glisser d'une case dans sa direction d'arrivée (signe de <c>landing - from</c> ; diagonale pour un saut
    /// de cavalier), EN CHAÎNE tant qu'elle atterrit sur une autre tuile glissante. S'arrête AVANT un obstacle,
    /// un pion ou le bord du plateau. Repositionne l'unité dans <see cref="_units"/> et, si au moins une case a
    /// été glissée, enregistre le chemin (départ inclus → repos) dans <see cref="_lastSlide"/> pour le feedback.
    /// Renvoie la case de repos finale (= <paramref name="landing"/> si aucune glissade).
    /// <paramref name="record"/> = false quand l'appelant gère lui-même le feedback (ex. « Recule » qui a sa
    /// propre animation de glissement) : la glissade a bien lieu mais <see cref="_lastSlide"/> reste vide.
    /// </summary>
    private Cell SlideOnIce(Unit unit, Cell from, Cell landing, bool record = true)
    {
        // Pas de terrain (plateau plat), unité volante (survole la glace) ou case non glissante : rien à faire.
        if (_terrain == null || unit.HasTrait(Trait.Vol) || !Slippery(landing))
            return landing;

        var dir = new Cell(System.Math.Sign(landing.Column - from.Column), System.Math.Sign(landing.Row - from.Row));
        if (dir.Column == 0 && dir.Row == 0)
            return landing;   // aucune direction d'arrivée (ne devrait pas arriver) : pas de glissade

        var current = landing;
        var path = new List<Cell> { current };
        while (Slippery(current))
        {
            var next = new Cell(current.Column + dir.Column, current.Row + dir.Row);
            if (!InBounds(next) || BlocksMovement(next) || _units[next.Column, next.Row] != null)
                break;   // obstacle, pion ou bord : l'unité s'immobilise sur la case courante
            MoveUnit(current, next);
            current = next;
            path.Add(current);
        }

        if (path.Count > 1 && record)
            _lastSlide = path;   // au moins une case glissée : départ = path[0], repos = path[^1]
        return current;
    }

    /// <summary>Vrai si la case porte une tuile glissante (glace). Faux hors terrain.</summary>
    private bool Slippery(Cell cell) =>
        _terrain != null && _terrain[cell].Slippery;

    /// <summary>Vide les résultats de feedback (« Impact »/« Recule »/« Esquive »/« Riposte ») avant de résoudre une nouvelle action.</summary>
    private void ResetActionFx()
    {
        _impactHits.Clear();
        _impactZone.Clear();
        _dodges.Clear();
        _lastRecule = null;
        _lastRiposte = null;
        _lastPierce = null;
        _interceptions.Clear();
        _lastSlide = null;
        _thorns.Clear();
        _splashHits.Clear();
        _bounceHits.Clear();
        _bounceFrom = null;
        // Filet : une nouvelle action ne doit jamais démarrer avec une balle encore en l'air (la scène gèle
        // le tour tant qu'elle vole, donc la file est normalement déjà vidée).
        _bounceQueue.Clear();
        _bounceAttacker = null;
        _chainAttacker = null;
        _chainLinksLeft = 0;
        _lastCharm = null;
        _brokenItem = null;
        _grenadeBlast = null;
        _shieldHits.Clear();
        LastGrantedExtraTurn = false;
    }

    // ═══ COMMANDANT DUO : liens entre meneurs, trousses, objets à lancer ══════════════════════════

    /// <summary>+puissance du trait « Position stratégique » quand un allié est à portée (déplacement OU attaque).</summary>
    private const int PositionStrategiquePower = 5;

    /// <summary>Portée (Chebyshev) d'un rebond de la « Balle rebondissante » : la case d'à côté, pas plus.</summary>
    private const int BalleRebondRange = 1;

    /// <summary>Garde-fou : nombre maximal de maillons d'une chaîne (réaction en chaîne, rebonds).</summary>
    private const int MaxChainLinks = 8;

    /// <summary>Alliés à portée (tampon réutilisé) : « Lien d'amitié » et « Position stratégique ».</summary>
    private readonly List<Cell> _reachBuffer = new();

    /// <summary>Un partage de dégâts (« Lien d'amitié ») est en cours : JAMAIS relayé (sinon boucle infinie).</summary>
    private bool _sharing;

    /// <summary>Coups COLLATÉRAUX de l'action (éclaboussure de grenade, rebonds, tir en ligne, réaction en chaîne).</summary>
    private readonly List<(Cell Cell, int Damage, bool Killed)> _splashHits = new();

    /// <summary>« Flèche de Cupidon » : case de l'unité retournée par l'action (null si aucune).</summary>
    private Cell? _lastCharm;

    /// <summary>
    /// « Balle rebondissante » : les REBONDS de l'action, DANS L'ORDRE où la balle les a touchés (la cible
    /// directe n'y figure pas — c'est le point de départ, cf. <see cref="LastBounceFrom"/>). Tenus à part des
    /// autres coups collatéraux : la scène les anime un par un le long de la trajectoire de la balle, au lieu
    /// de tout faire jaillir au contact de l'attaque.
    /// </summary>
    private readonly List<(Cell Cell, int Damage, bool Killed)> _bounceHits = new();

    /// <summary>Case d'où la balle repart pour son premier rebond (la cible directe), ou null si aucun rebond.</summary>
    private Cell? _bounceFrom;

    /// <summary>Objet à lancer CONSOMMÉ par l'action (null si aucun) : la scène l'annonce et le retire du pion.</summary>
    private Equipment? _brokenItem;

    /// <summary>Coups collatéraux de la dernière action (grenade, rebonds, tir en ligne, réaction en chaîne).</summary>
    public IReadOnlyList<(Cell Cell, int Damage, bool Killed)> LastSplashHits => _splashHits;

    /// <summary>Case où la dernière action a fait EXPLOSER une grenade, ou null. La scène y joue le souffle.</summary>
    public Cell? LastGrenadeBlast => _grenadeBlast;

    private Cell? _grenadeBlast;

    /// <summary>Case de l'unité retournée par une « Flèche de Cupidon » lors de la dernière action, ou null.</summary>
    public Cell? LastCharm => _lastCharm;

    /// <summary>
    /// REBONDS de la « Balle rebondissante » de la dernière action, dans l'ordre de la trajectoire. La scène
    /// s'en sert pour faire voler la balle d'ennemi en ennemi et ne sortir chaque chiffre qu'à son arrivée.
    /// </summary>
    public IReadOnlyList<(Cell Cell, int Damage, bool Killed)> LastBounceHits => _bounceHits;

    /// <summary>Point de départ de la trajectoire des rebonds (la cible directe), ou null si la balle n'a pas rebondi.</summary>
    public Cell? LastBounceFrom => _bounceFrom;

    /// <summary>Objet à lancer brisé par la dernière action (null si aucun) : la scène le retire du gabarit.</summary>
    public Equipment? LastBrokenItem => _brokenItem;

    /// <summary>
    /// COMMANDANT DUO : puissance gagnée par le COMMANDANT pour chaque tranche de
    /// <see cref="CommandEffect.CrossKillStep"/> mises à mort de son COMPAGNON. 0 = nœud non acheté.
    /// Réglé par la scène depuis la run au lancement du combat.
    /// </summary>
    public int CrossKillPower { get; set; }

    /// <summary>
    /// COMMANDANT DUO : « Roque » acheté — les deux meneurs ÉCHANGENT leurs places quand l'un se déplace sur
    /// l'autre. Réglé par la scène depuis la run au lancement du combat.
    /// </summary>
    public bool RoqueEnabled { get; set; }

    /// <summary>
    /// COMMANDANT DUO : « La puissance du rock » achetée — puissance mise en réserve sur l'ARTISAN à chaque
    /// ROQUE, dépensée par sa prochaine attaque (cf. <see cref="Unit.RoquePower"/>). 0 = nœud non acheté.
    /// Réglée par la scène depuis la run au lancement du combat.
    /// </summary>
    public int RoquePower { get; set; }

    /// <summary>
    /// COMMANDANT DUO : « Continue sans moi » acheté — la chute d'UN meneur ne perd plus la partie tant que
    /// l'AUTRE est debout (cf. <see cref="UpdateWinner"/>). Le survivant termine le combat seul ; c'est la
    /// campagne qui décide ensuite du sort du tombé. Réglé par la scène depuis la run au lancement du combat.
    /// </summary>
    public bool SoloSurvivorEnabled { get; set; }

    /// <summary>
    /// L'AUTRE meneur du duo (même camp, essentiel, pas le même rôle compagnon/commandant), ou null. Cherché
    /// dans <see cref="_essential"/> : la liste garde aussi les meneurs TOMBÉS, ce qui est voulu — la puissance
    /// gagnée sur les mises à mort de l'autre reste acquise quand il n'est plus là.
    /// </summary>
    private Unit? OtherLeader(Unit leader)
    {
        if (!leader.IsEssential)
            return null;
        foreach (var u in _essential)
            if (u.Faction == leader.Faction && !ReferenceEquals(u, leader) && u.IsCompanion != leader.IsCompanion)
                return u;
        return null;
    }

    /// <summary>
    /// Puissance que le COMMANDANT tient des mises à mort de son COMPAGNON (nœud « fierté partagée » de l'arbre
    /// du DUO) : <see cref="CrossKillPower"/> par tranche de <see cref="CommandEffect.CrossKillStep"/> kills.
    /// Sens UNIQUE : le compagnon ne gagne rien des kills du commandant. 0 hors duo ou nœud non acheté.
    /// </summary>
    private int CrossKillPowerBonus(Unit unit) =>
        CrossKillPower <= 0 || unit.IsCompanion || OtherLeader(unit) is not { } other
            ? 0
            : CrossKillPower * (other.Kills / CommandEffect.CrossKillStep);

    /// <summary>
    /// Vrai si les deux unités sont les DEUX meneurs d'un même duo et que le « Roque » est acheté : l'une peut
    /// alors se déplacer sur l'autre pour échanger leurs places.
    /// </summary>
    private bool CanRoque(Unit mover, Unit? occupant) =>
        RoqueEnabled && occupant != null && mover.IsEssential && occupant.IsEssential
        && mover.Faction == occupant.Faction && mover.IsCompanion != occupant.IsCompanion;

    /// <summary>
    /// Ajoute à <paramref name="result"/> (vidé) les ALLIÉS de <paramref name="unit"/> qui sont à SA PORTÉE —
    /// déplacement OU attaque. Le déplacement suit <see cref="AppendAlliesInMoveRange"/> (rayons du motif, que
    /// rien ne borne sauf le bord) ; l'attaque suit les rayons du motif d'ATTAQUE jusqu'à la portée de tir,
    /// même règle. C'est la portée « sociale » du DUO : « Lien d'amitié » y partage les dégâts, « Position
    /// stratégique » y cherche un camarade. Sans doublon, l'unité elle-même exclue.
    /// </summary>
    private void AppendAlliesInReach(Cell from, Unit unit, List<Cell> result)
    {
        result.Clear();
        AppendAlliesInMoveRange(from, unit, result);

        var attackVectors = Movement.Vectors(unit.AttackDomaine);
        if (Movement.Kind(unit.AttackDomaine) == MovementKind.Jump)
        {
            foreach (var offset in attackVectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (UnitAt(to) is { } j && j.Faction == unit.Faction && !result.Contains(to))
                    result.Add(to);
            }
            return;
        }

        foreach (var dir in attackVectors)
            for (var step = 1; step <= EffectiveAttackRange(unit, from); step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to))
                    break;   // seul le bord du plateau arrête le rayon
                if (_units[to.Column, to.Row] is { } occ && occ.Faction == unit.Faction && !result.Contains(to))
                    result.Add(to);
            }
    }

    /// <summary>
    /// « Position stratégique » : +<see cref="PositionStrategiquePower"/> de puissance tant qu'AU MOINS UN allié
    /// est à portée de déplacement ou d'attaque (cf. <see cref="AppendAlliesInReach"/>). 0 sans le trait ou
    /// isolé. Effet CONTEXTUEL au placement, comme les auras : l'UI doit le demander ici.
    /// </summary>
    public int PositionStrategiquePowerBonus(Cell cell)
    {
        if (UnitAt(cell) is not { } u || !u.HasTrait(Trait.PositionStrategique))
            return 0;
        _reachBuffer.Clear();
        AppendAlliesInReach(cell, u, _reachBuffer);
        return _reachBuffer.Count > 0 ? PositionStrategiquePower : 0;
    }

    /// <summary>
    /// Alliés qui déclenchent « Position stratégique » sur le pion de <paramref name="cell"/> : ceux à SA
    /// portée (déplacement ou attaque). Vide si le pion n'a pas le trait. Sert au RENDU (le lien de puissance
    /// tracé entre le porteur et ses camarades) — le bonus, lui, ne dépend que de leur EXISTENCE.
    /// </summary>
    public void PositionStrategiqueAllies(Cell cell, List<Cell> result)
    {
        result.Clear();
        if (UnitAt(cell) is { } u && u.HasTrait(Trait.PositionStrategique))
            AppendAlliesInReach(cell, u, result);
    }

    /// <summary>
    /// Alliés qui PARTAGERAIENT les dégâts du pion de <paramref name="cell"/> (« Lien d'amitié ») : ceux à SA
    /// portée (déplacement ou attaque). Vide si le pion n'a pas le trait. Sert au RENDU : c'est exactement la
    /// liste que <see cref="ShareFriendshipDamage"/> ferait encaisser, pour que la chaîne montrée à l'écran ne
    /// mente jamais sur qui va prendre une part.
    /// </summary>
    public void LienDAmitieAllies(Cell cell, List<Cell> result)
    {
        result.Clear();
        if (UnitAt(cell) is { } u && u.HasTrait(Trait.LienDAmitie))
            AppendAlliesInReach(cell, u, result);
    }

    /// <summary>
    /// « Lien d'amitié » : les dégâts subis par le porteur sont DIVISÉS à parts égales entre lui et TOUS ses
    /// alliés à portée (déplacement ou attaque) — le trait est porté par la VICTIME seule, les alliés qui
    /// encaissent une part n'ont rien à porter. Renvoie ce qu'il reste à encaisser à la victime (le reste de
    /// la division lui revient). JAMAIS relayé (<see cref="_sharing"/>) : les parts distribuées ne se
    /// repartagent pas, y compris entre deux porteurs. Les alliés qui tombent sur leur part sont retirés du plateau.
    /// </summary>
    private int ShareFriendshipDamage(Unit victim, int amount, Unit? attacker)
    {
        if (CellOf(victim) is not { } here)
            return amount;

        // Liste LOCALE : la distribution rentre dans ApplyDamage (épines, esquive…) qui peut réutiliser le
        // tampon partagé. Les cases sont figées avant application (un replié ne doit pas encaisser deux fois).
        var partners = new List<Unit>();
        var reach = new List<Cell>();
        var split = SplitFriendship(victim, here, amount, reach);
        if (split.Partners == 0)
            return amount;
        foreach (var c in reach)
            if (UnitAt(c) is { } ally && !ReferenceEquals(ally, victim))
                partners.Add(ally);

        _sharing = true;
        foreach (var partner in partners)
        {
            if (CellOf(partner) is not { } pc)
                continue;
            var before = partner.Hp;
            ApplyDamage(partner, split.AllyShare, attacker);
            // Chiffre de dégâts sur le camarade : sans lui le partage est INVISIBLE (on ne verrait que le
            // total sur la victime, sans comprendre pourquoi les PV de l'autre descendent aussi).
            if (before - partner.Hp is > 0 and var dealt)
                _splashHits.Add((pc, dealt, !partner.IsAlive));
            RemoveDeadAt(pc, attacker);
        }
        _sharing = false;
        return split.VictimShare;   // sa part, le reste de la division, moins l'absorption du lien
    }

    /// <summary>
    /// PV retranchés à la part de CHAQUE unité touchée par « Lien d'amitié », victime comprise, une fois la
    /// division faite. Le lien ne se contente donc pas de répartir le coup : il l'ÉMOUSSE, et d'autant plus
    /// qu'il y a de camarades autour (chacun absorbe autant). Un coup assez petit peut être annulé.
    /// </summary>
    private const int FriendshipAbsorb = 1;

    /// <summary>Répartition d'un coup par « Lien d'amitié » (cf. <see cref="SplitFriendship"/>).</summary>
    /// <param name="AllyShare">Ce que prend CHACUN des <paramref name="Partners"/> camarades.</param>
    /// <param name="VictimShare">Ce qu'il reste à la victime : sa part, le reste de la division, moins l'absorption.</param>
    /// <param name="Partners">Nombre de camarades qui encaissent ; 0 = pas de partage du tout.</param>
    private readonly record struct FriendshipSplit(int AllyShare, int VictimShare, int Partners);

    /// <summary>
    /// Répartition d'un coup de <paramref name="amount"/> par « Lien d'amitié » entre <paramref name="victim"/>
    /// et ses alliés à portée, dont <paramref name="reach"/> reçoit les cases (la victime comprise).
    /// <see cref="FriendshipSplit.Partners"/> à 0 = pas de partage (trait absent, personne à portée, coup trop
    /// petit à diviser, ou partage déjà en cours) et la victime encaisse tout.
    ///
    /// Division à parts égales — le reste revient à la victime —, puis CHAQUE unité touchée retranche
    /// <see cref="FriendshipAbsorb"/> de sa part (jamais sous zéro).
    ///
    /// PARTAGÉE entre l'application réelle et l'APERÇU (<see cref="PreviewDamage"/>), pour que le chiffre
    /// annoncé au joueur soit exactement celui qu'il va voir descendre.
    /// </summary>
    private FriendshipSplit SplitFriendship(Unit victim, Cell here, int amount, List<Cell> reach)
    {
        reach.Clear();
        if (_sharing || amount <= 1 || !victim.HasTrait(Trait.LienDAmitie))
            return new FriendshipSplit(0, amount, 0);

        AppendAlliesInReach(here, victim, reach);
        var partners = 0;
        foreach (var c in reach)
            if (UnitAt(c) is { } ally && !ReferenceEquals(ally, victim))
                partners++;
        if (partners == 0)
            return new FriendshipSplit(0, amount, 0);

        var each = amount / (partners + 1);
        var rest = amount % (partners + 1);   // indivisible : il reste sur le dos de la victime
        return new FriendshipSplit(
            System.Math.Max(0, each - FriendshipAbsorb),
            System.Math.Max(0, each + rest - FriendshipAbsorb),
            partners);
    }

    /// <summary>
    /// Ce qu'il RESTERAIT à encaisser à <paramref name="victim"/> sur un coup de <paramref name="amount"/>
    /// une fois « Lien d'amitié » appliqué (sa part plus le reste de la division, moins l'absorption). Égal à
    /// <paramref name="amount"/> sans partage.
    /// </summary>
    private int AfterFriendshipShare(Unit victim, Cell here, int amount) =>
        SplitFriendship(victim, here, amount, _previewReach).VictimShare;

    /// <summary>Tampon de l'APERÇU de partage : jamais mêlé à <see cref="_reachBuffer"/> (lu pendant un coup).</summary>
    private readonly List<Cell> _previewReach = new();

    /// <summary>
    /// APERÇU du « Lien d'amitié » sur une attaque de <paramref name="from"/> vers <paramref name="target"/> :
    /// renvoie la part que prendrait CHACUN des camarades liés de la cible et remplit <paramref name="allies"/>
    /// de leurs cases. 0 (et liste vide) sans partage. La cible, elle, ne garde que
    /// <see cref="PreviewDamage"/> — les deux se lisent ensemble.
    /// </summary>
    public int PreviewSharedDamage(Cell from, Cell target, List<Cell> allies)
    {
        allies.Clear();
        if (UnitAt(from) is not { } attacker || UnitAt(target) is not { } victim)
            return 0;

        // Une part retombée à 0 par l'absorption ne s'annonce pas : le camarade ne prendra rien.
        var share = SplitFriendship(victim, target, EffectiveDamage(attacker, from, victim, target), _previewReach).AllyShare;
        if (share <= 0)
            return 0;
        foreach (var c in _previewReach)
            if (UnitAt(c) is { } ally && !ReferenceEquals(ally, victim))
                allies.Add(c);
        return share;
    }

    /// <summary>
    /// « Réaction en chaîne » : l'attaque qui TUE sa cible enchaîne sur une autre unité ennemie au CONTACT
    /// (portée 1 autour de l'attaquant), et recommence tant qu'elle tue. L'attaquant ne bouge pas sur les
    /// maillons ; la chaîne s'arrête au premier survivant, faute de cible, ou au garde-fou
    /// <see cref="MaxChainLinks"/>.
    ///
    /// ARMÉE ici, JOUÉE maillon par maillon par <see cref="ResolveNextChainLink"/> : la scène fait bondir
    /// l'attaquant sur chaque victime et ne résout le coup qu'à SON ARRIVÉE. Tout résoudre d'un bloc ferait
    /// mourir toute la file avant même qu'on ait vu le premier bond partir (même principe que les rebonds de
    /// la « Balle rebondissante », cf. <see cref="PlanBounce"/>).
    /// </summary>
    private void ArmChainReaction(Unit attacker, Cell target)
    {
        _chainAttacker = attacker;
        _chainOrigin = target;
        _chainLinksLeft = MaxChainLinks;
    }

    private Unit? _chainAttacker;
    private Cell _chainOrigin;
    private int _chainLinksLeft;

    /// <summary>Case de l'attaquant dont la chaîne est en cours (là où le moteur le tient), ou null.</summary>
    public Cell? ChainAttackerCell => _chainAttacker is { } a ? CellOf(a) : null;

    /// <summary>
    /// Case de la prochaine victime de la chaîne : le premier ennemi au CONTACT de la DERNIÈRE abattue — la
    /// chaîne se propage de proche en proche à partir du corps, pas autour de l'attaquant resté en arrière.
    /// Null quand elle est finie (personne autour, attaquant tombé, victime survivante, garde-fou atteint).
    /// </summary>
    public Cell? PendingChainTarget
    {
        get
        {
            if (_chainAttacker is not { IsAlive: true } a || _chainLinksLeft <= 0 || CellOf(a) is null)
                return null;
            foreach (var (dc, dr) in Neighbors8)
            {
                var c = new Cell(_chainOrigin.Column + dc, _chainOrigin.Row + dr);
                if (UnitAt(c) is { } u && u.Faction != a.Faction)
                    return c;
            }
            return null;
        }
    }

    /// <summary>Vrai s'il reste un maillon de « Réaction en chaîne » à jouer.</summary>
    public bool HasPendingChain => PendingChainTarget != null;

    /// <summary>
    /// Résout le maillon suivant AU MOMENT où le coup porte — la scène l'appelle quand l'attaquant arrive sur
    /// sa victime. Renvoie (case, dégâts réels, tuée) ou null s'il n'y avait plus rien à frapper. La chaîne
    /// s'éteint d'elle-même dès qu'une victime SURVIT.
    /// </summary>
    public (Cell Cell, int Damage, bool Killed)? ResolveNextChainLink()
    {
        if (_chainAttacker is not { } attacker || PendingChainTarget is not { } target
            || CellOf(attacker) is not { } from || UnitAt(target) is not { } victim)
        {
            _chainAttacker = null;
            return null;
        }

        _chainLinksLeft--;
        var before = victim.Hp;
        ApplyDamage(victim, EffectiveDamage(attacker, from, victim, target), attacker);
        var killed = !victim.IsAlive;
        var dealt = before - victim.Hp;
        RemoveDeadAt(target, attacker);
        _chainOrigin = target;       // le maillon suivant se cherche autour de CE corps
        if (!killed)
            _chainAttacker = null;   // la chaîne ne se propage que sur une mise à mort
        UpdateWinner();
        return (target, dealt, killed);
    }

    /// <summary>
    /// « Tir en ligne » : l'attaque touche TOUTES les cibles alignées à portée — autrement dit toutes les cases
    /// que le motif d'attaque atteint (cf. <see cref="AttackTargets(Cell)"/>), pas seulement celle visée. La
    /// cible directe <paramref name="target"/> est exclue (elle a déjà encaissé le coup principal).
    /// </summary>
    private void ApplyTirEnLigne(Unit attacker, Cell from, Cell target)
    {
        var others = AttackTargets(from);
        foreach (var c in others)
        {
            if (c == target || UnitAt(c) is not { } victim || victim.Faction == attacker.Faction)
                continue;
            var before = victim.Hp;
            ApplyDamage(victim, EffectiveDamage(attacker, from, victim, c), attacker);
            var killed = !victim.IsAlive;
            if (before - victim.Hp is > 0 and var dealt)
                _splashHits.Add((c, dealt, killed));
            RemoveDeadAt(c, attacker);
        }
    }

    /// <summary>
    /// « Grenade » (objet à lancer) : les 8 cases autour de la CIBLE encaissent la MOITIÉ des dégâts de
    /// l'attaque. Les alliés de l'attaquant sont épargnés (c'est un objet qu'on donne à son meneur, pas un
    /// piège pour son camarade).
    /// </summary>
    private void ApplyGrenade(Unit attacker, Cell from, Cell target)
    {
        _grenadeBlast = target;   // la grenade explose même sans victime autour : la scène a de quoi la montrer
        var victims = new List<Cell>();
        foreach (var (dc, dr) in Neighbors8)
        {
            var c = new Cell(target.Column + dc, target.Row + dr);
            if (UnitAt(c) is { } u && u.Faction != attacker.Faction)
                victims.Add(c);
        }

        foreach (var c in victims)
        {
            if (UnitAt(c) is not { } victim)
                continue;
            var before = victim.Hp;
            ApplyDamage(victim, EffectiveDamage(attacker, from, victim, c) / 2, attacker);
            var killed = !victim.IsAlive;
            if (before - victim.Hp is > 0 and var dealt)
                _splashHits.Add((c, dealt, killed));
            RemoveDeadAt(c, attacker);
        }
    }

    /// <summary>
    /// « Balle rebondissante » (objet à lancer) : PRÉPARE la trajectoire sans rien appliquer. La balle
    /// ricoche de victime en victime, chaque fois sur un ennemi ADJACENT (<see cref="BalleRebondRange"/>) au
    /// dernier touché, JAMAIS deux fois sur le même (la cible directe comprise). S'arrête faute de voisin, ou
    /// au garde-fou <see cref="MaxChainLinks"/>.
    ///
    /// Les dégâts sont CALCULÉS ici — tant que l'objet est encore sur le pion, donc son bonus compte — mais
    /// INFLIGÉS un par un par <see cref="ResolveNextBounce"/>, quand la balle arrive vraiment sur sa victime.
    /// Sans quoi tout le monde encaisserait et mourrait avant même de voir la balle partir.
    /// </summary>
    private void PlanBounce(Unit attacker, Cell from, Cell target)
    {
        var hit = new HashSet<Unit>();
        if (UnitAt(target) is { } first)
            hit.Add(first);
        var origin = target;

        for (var bounce = 0; bounce < MaxChainLinks; bounce++)
        {
            Cell? next = null;
            foreach (var (cell, unit) in Units())
                if (unit.Faction != attacker.Faction && !hit.Contains(unit)
                    && ChebyshevDistance(origin, cell) <= BalleRebondRange)
                {
                    next = cell;
                    break;
                }
            if (next is not { } c || UnitAt(c) is not { } victim)
                break;

            hit.Add(victim);
            _bounceQueue.Add((c, EffectiveDamage(attacker, from, victim, c)));
            origin = c;   // le rebond suivant part de la case qui vient d'être touchée
        }

        if (_bounceQueue.Count > 0)
        {
            _bounceFrom = target;       // la balle part de la cible directe
            _bounceAttacker = attacker; // c'est lui qui encaisse les épines et récolte les mises à mort
        }
    }

    /// <summary>Rebonds CALCULÉS mais pas encore infligés, dans l'ordre de la trajectoire.</summary>
    private readonly List<(Cell Cell, int Damage)> _bounceQueue = new();

    /// <summary>Lanceur de la balle en vol : crédité des mises à mort, cible des éventuelles épines.</summary>
    private Unit? _bounceAttacker;

    /// <summary>Vrai tant que la balle a encore des rebonds à porter (cf. <see cref="ResolveNextBounce"/>).</summary>
    public bool HasPendingBounce => _bounceQueue.Count > 0;

    /// <summary>
    /// Trajectoire RESTANTE de la balle, dans l'ordre : la scène s'en sert pour l'animer d'un pion à l'autre.
    /// Vide si la balle n'a pas rebondi.
    /// </summary>
    public IReadOnlyList<Cell> PendingBouncePath =>
        _bounceQueue.ConvertAll(b => b.Cell);

    /// <summary>
    /// Inflige le PROCHAIN rebond : c'est ici que la victime encaisse et, le cas échéant, tombe. Appelé par
    /// la scène AU MOMENT où la balle se pose sur elle. Renvoie la case touchée, les dégâts réellement
    /// encaissés et si le coup l'a abattue ; <c>null</c> quand il n'y a plus rien à résoudre.
    ///
    /// La victoire est réévaluée à chaque rebond : la balle peut très bien abattre le dernier ennemi.
    /// </summary>
    public (Cell Cell, int Damage, bool Killed)? ResolveNextBounce()
    {
        if (_bounceQueue.Count == 0)
            return null;

        var (cell, damage) = _bounceQueue[0];
        _bounceQueue.RemoveAt(0);
        var attacker = _bounceAttacker;
        // Lanceur abattu en route (épines d'un rebond) ou cible disparue : la balle retombe, on solde la file.
        if (attacker is null || CellOf(attacker) is null || UnitAt(cell) is not { } victim)
        {
            _bounceQueue.Clear();
            return null;
        }

        var before = victim.Hp;
        ApplyDamage(victim, damage, attacker);
        var killed = !victim.IsAlive;
        var dealt = before - victim.Hp;
        RemoveDeadAt(cell, attacker);
        if (dealt > 0)
            _bounceHits.Add((cell, dealt, killed));
        UpdateWinner();
        return (cell, dealt, killed);
    }

    /// <summary>
    /// « Flèche de Cupidon » (objet à lancer) : la cible SURVIVANTE change de camp pour le reste du combat
    /// (cf. <see cref="Unit.Charm"/>). Une cible abattue par le coup n'est pas retournée — elle est morte.
    /// Noté dans <see cref="_lastCharm"/> pour le feedback.
    /// </summary>
    private void ApplyCharm(Unit attacker, Cell target)
    {
        if (UnitAt(target) is not { IsAlive: true } victim || victim.Faction == attacker.Faction)
            return;
        if (victim.IsEssential)
            return;   // un boss ne se retourne pas : le combat se gagne en l'abattant
        victim.Charm(attacker.Faction);
        _lastCharm = target;
        UpdateWinner();   // le camp de la cible a changé : le combat peut être décidé par ce seul retournement
    }

    /// <summary>
    /// Objets à lancer : résout l'effet propre de celui que porte l'attaquant, APRÈS le coup principal. Un seul
    /// objet à la fois (Basile n'en prend un que s'il n'en a pas) ; le « Javelot meurtrier » n'a pas d'effet
    /// ici — sa puissance est déjà dans le coup.
    /// </summary>
    private void ResolveThrownItem(Unit attacker, Cell from, Cell target)
    {
        if (attacker.HasTrait(Trait.Grenade))
            ApplyGrenade(attacker, from, target);
        if (attacker.HasTrait(Trait.BalleRebondissante))
            PlanBounce(attacker, from, target);   // trajectoire calculée ; les coups partent à l'atterrissage
        if (attacker.HasTrait(Trait.FlecheDeCupidon))
            ApplyCharm(attacker, target);
    }

    /// <summary>
    /// Brise l'objet à lancer que porte <paramref name="unit"/> (il ne sert qu'UNE attaque) et le note dans
    /// <see cref="_brokenItem"/> : la scène l'annonce et le retire aussi du GABARIT pour que l'objet ne
    /// revienne pas au combat suivant. Sans effet si l'unité n'en porte pas.
    /// </summary>
    private void ConsumeThrownItem(Unit unit)
    {
        foreach (var item in unit.Equipments)
            if (item.Traits.Any(t => Trait.Thrown.Contains(t)))
            {
                unit.BreakEquipment(item);
                _brokenItem = item;
                return;
            }
    }

    /// <summary>
    /// « Impact » : le porteur inflige un dégât FIXE (<see cref="ImpactDamageFor"/>) à TOUS les ennemis des 8 cases
    /// autour de <paramref name="center"/> (sa position finale). Déclenché UNIQUEMENT par sa propre action
    /// (déplacement / attaque), jamais relayé par un autre trait. Fixe : ni Rempart ni couvert ne le réduisent,
    /// mais une victime d'« Esquive » se replie après coup (via <see cref="ApplyDamage"/>). Les coups réels sont
    /// notés dans <see cref="_impactHits"/> pour le feedback ; l'appelant a réinitialisé la liste via
    /// <see cref="ResetActionFx"/>.
    /// </summary>
    private void ApplyImpact(Unit unit, Cell center)
    {
        // Cibles FIGÉES avant application : une victime qui se replie (« Esquive ») ne doit pas être frappée
        // une deuxième fois si son bond la pose sur une autre case de la zone.
        var victims = new List<Unit>();
        foreach (var (dc, dr) in Neighbors8)
        {
            var c = new Cell(center.Column + dc, center.Row + dr);
            _impactZone.Add(c);   // toute la zone AoE tremble au feedback, même les cases sans cible
            if (UnitAt(c) is { } victim && victim.Faction != unit.Faction)
                victims.Add(victim);
        }

        foreach (var victim in victims)
        {
            if (CellOf(victim) is not { } c)
                continue;
            var before = victim.Hp;
            ApplyDamage(victim, ImpactDamageFor(unit), unit);
            if (before - victim.Hp is > 0 and var dealt)
                _impactHits.Add((c, dealt));
            RemoveDeadAt(c, unit);
        }
    }

    /// <summary>
    /// « Recule » : repousse la cible SURVIVANTE d'une case dans l'axe de l'attaque (attaquant → cible). Si la
    /// case derrière est libre et franchissable, la cible y glisse ; sinon (bord du plateau, unité, obstacle de
    /// terrain) elle est PLAQUÉE et encaisse <see cref="ReculeSlamDamage"/> en restant sur place. No-op si la
    /// cible n'a pas survécu (déjà tuée, ou sa place a été prise par l'attaquant). Résultat noté dans
    /// <see cref="_lastRecule"/> pour le feedback.
    /// </summary>
    private void ApplyRecule(Cell from, Cell target, Unit attacker)
    {
        if (UnitAt(target) is not { } victim || victim.Faction == attacker.Faction)
            return;
        var dc = System.Math.Sign(target.Column - from.Column);
        var dr = System.Math.Sign(target.Row - from.Row);
        if (dc == 0 && dr == 0)
            return;   // pas de direction exploitable (ne devrait pas arriver pour une attaque valide)

        var behind = new Cell(target.Column + dc, target.Row + dr);
        if (InBounds(behind) && _units[behind.Column, behind.Row] == null && !BlocksMovement(behind))
        {
            MoveUnit(target, behind);       // la cible glisse d'une case, poussée hors de portée
            // « Glace » : poussée sur une tuile glissante, elle CONTINUE de glisser dans la direction du recul
            // (mêmes règles : chaîne, arrêt sur obstacle/pion/bord ; les volants ne glissent pas). Feedback via
            // _lastRecule (le glissement de la victime est déjà animé côté scène), donc record:false.
            var rest = SlideOnIce(victim, target, behind, record: false);
            _lastRecule = (rest, 0);
        }
        else
        {
            var before = victim.Hp;         // plaquée contre un obstacle (bord / unité / terrain) : dégât bonus
            ApplyDamage(victim, ReculeSlamDamage, attacker);
            RemoveDeadAt(target, attacker);
            _lastRecule = (target, before - victim.Hp);
        }
    }

    /// <summary>« Interception » : chaque ennemi du mobile dont la portée couvre sa case d'arrivée le frappe.
    /// Le mobile peut CHANGER de case entre deux interceptions (repli d'« Esquive ») : chaque intercepteur est
    /// donc évalué depuis la case où le mobile se trouve RÉELLEMENT à son tour de frapper.</summary>
    private void TriggerInterceptions(Cell movedTo, Unit mover)
    {
        foreach (var (cell, unit) in Units())
        {
            if (unit.Faction == mover.Faction || !unit.HasTrait(Trait.Interception))
                continue;
            if (CellOf(mover) is not { } here)
                return;   // mobile retiré du plateau : plus rien à intercepter
            if (!ThreatenedCells(cell).Contains(here))
                continue;
            var before = mover.Hp;
            ApplyDamage(mover, EffectiveDamage(unit, cell, mover, here), unit);
            var killed = !mover.IsAlive;
            _interceptions.Add((cell, here, before - mover.Hp, killed));   // report pour l'animation d'attaque
            if (killed)
            {
                RemoveDeadAt(here, unit);   // l'intercepteur abat le mobile : kill crédité
                return;   // mobile abattu : plus rien à intercepter
            }
        }
    }

    /// <summary>
    /// Retire de la grille l'unité morte d'une case (l'essentiel reste suivi pour la victoire). Si un
    /// <paramref name="killer"/> est fourni et que le mort est de l'autre camp, la mise à mort lui est
    /// créditée (compteur de kills à vie, cf. <see cref="Unit.Kills"/>).
    /// </summary>
    private void RemoveDeadAt(Cell cell, Unit? killer = null)
    {
        if (UnitAt(cell) is not { IsAlive: false } dead)
            return;
        if (TryReviveWithEquipment(dead))
            return;   // « Queue de phénix » : reste sur sa case, vivant à 1 PV, équipement brisé (pas de kill)
        if (killer != null && dead.Faction != killer.Faction)
            killer.RecordKill();
        _units[cell.Column, cell.Row] = null;
        OnUnitDied(dead, cell);   // « Rage » : les alliés survivants du mort gagnent de la puissance
    }

    /// <summary>
    /// Toutes les unités TOMBÉES depuis le début du combat, dans l'ordre. JAMAIS vidée en cours de combat —
    /// contrairement aux tampons d'effets (cf. <see cref="ResetActionFx"/>), qui repartent à chaque action :
    /// la scène lit ce journal à son rythme, en gardant l'index de ce qu'elle a déjà traité. Une riposte, une
    /// interception ou un maillon de « Réaction en chaîne » ne peut donc pas lui faire manquer une mort.
    /// Alimentée par <see cref="OnUnitDied"/>, le passage obligé de TOUS les chemins de mort.
    /// </summary>
    public IReadOnlyList<(Cell Cell, Unit Unit)> DeathLog => _deathLog;

    private readonly List<(Cell Cell, Unit Unit)> _deathLog = new();

    /// <summary>
    /// Signale la mort de <paramref name="dead"/> (déjà retiré de la grille) : chaque ALLIÉ SURVIVANT porteur de
    /// « Rage » voit son bonus fixé à <see cref="RagePowerBonus"/> pour le RESTE du combat. NON cumulable : un
    /// porteur déjà enragé n'y gagne rien de plus aux morts suivantes (une seule fois par combat). Buff transitoire,
    /// non persisté — un nouveau combat repart d'unités neuves. Appelé à chaque chemin de mort.
    /// </summary>
    /// <param name="where">Case où l'unité est tombée : elle vient d'être libérée et peut déjà être reprise
    /// par son tueur, mais c'est LÀ que la scène doit poser le feedback de la mort.</param>
    private void OnUnitDied(Unit dead, Cell where)
    {
        _deathLog.Add((where, dead));   // journal des morts du combat (cf. DeathLog) : ce hook est le seul passage obligé
        foreach (var (_, u) in Units())
        {
            if (u.Faction != dead.Faction)
                continue;
            if (u.HasTrait(Trait.Rage))
                u.ActivateRage(RagePowerBonus);
            if (u.HasTrait(Trait.Vengeance))
                u.ActivateVengeance(VengeancePowerBonus);   // colère d'UN tour (cf. EndTurn)
        }

        // La colère durant « un tour », celle qui naît PENDANT le tour de son propre camp (un pion sacrifié
        // par la Brute, par exemple) ne doit pas être balayée par la fin de ce tour-là : elle vaut pour le
        // PROCHAIN. Cf. EndTurn.
        if (CurrentTurn == dead.Faction)
            _vengeanceFresh[(int)dead.Faction] = true;
    }

    /// <summary>
    /// Par camp : la « Vengeance » vient d'être activée pendant le tour de ce camp, elle survit donc à la fin
    /// du tour courant (cf. <see cref="OnUnitDied"/>).
    /// </summary>
    private readonly bool[] _vengeanceFresh = new bool[2];

    /// <summary>Bonus de puissance du trait « Vengeance » : accordé à la mort d'un allié, pour UN tour.</summary>
    public const int VengeancePowerBonus = 7;

    /// <summary>
    /// Fin du tour de <paramref name="faction"/> : ses unités oublient le coup qu'elles avaient reçu
    /// (cf. <see cref="Unit.HitSinceOwnTurn"/>). Un coup encaissé PENDANT ce tour (riposte, épines, interception
    /// sur l'attaque qu'elles portaient) est oublié du même coup : ce n'est pas une attaque à laquelle répondre.
    /// </summary>
    private void ClearRecentHitsFor(Faction faction)
    {
        foreach (var (_, u) in Units())
            if (u.Faction == faction)
                u.ClearRecentHit();
    }

    /// <summary>Retombée de la colère : toutes les unités du camp perdent leur bonus de « Vengeance ».</summary>
    private void ClearVengeanceFor(Faction faction)
    {
        foreach (var (_, u) in Units())
            if (u.Faction == faction)
                u.ClearVengeance();
    }

    /// <summary>
    /// Vrai si la « Renaissance ultime » (nœud d'arbre) a été déclenchée pendant ce combat. La scène le lit à
    /// la fin du combat pour la consommer DÉFINITIVEMENT dans la run (cf. <c>Run.UseUltimateRevive</c>) : le
    /// nœud ne sert qu'une fois par PARTIE.
    /// </summary>
    public bool UltimateReviveTriggered { get; private set; }

    /// <summary>
    /// Renaissance de l'unité TOMBÉE, dans cet ordre : « Renaissance ultime » de l'arbre (PV PLEINS, une fois
    /// par partie) d'abord, puis la « Queue de phénix » de l'équipement (1 PV, l'objet se brise). Renvoie vrai
    /// si une renaissance a eu lieu — l'unité RESTE alors en jeu (aucun kill crédité, aucune case libérée).
    /// </summary>
    private bool TryReviveWithEquipment(Unit unit)
    {
        if (unit.IsAlive)
            return false;
        if (unit.TryUltimateRevive())
        {
            UltimateReviveTriggered = true;
            return true;
        }
        if (unit.EquipmentWithTrait(Trait.Renaissance) is null)
            return false;
        unit.ReviveConsumingEquipment();
        return true;
    }

    /// <summary>Vrai si l'unité sait soigner : « Soin » (moitié de la puissance) ou « Soin parfait » (totalité).</summary>
    private static bool IsHealer(Unit unit) =>
        unit.HasTrait(Trait.Soin) || unit.HasTrait(Trait.SoinParfait);

    /// <summary>Montant soigné par le soigneur posé sur <paramref name="healerCell"/> : sa
    /// <see cref="EffectivePower"/> ENTIÈRE avec « Soin parfait », sinon la MOITIÉ (arrondie vers le bas).
    /// Les bonus de puissance (Berserk, Rage, auras, Formation) comptent donc aussi pour le soin. « Soin parfait »
    /// prime s'il porte les deux.</summary>
    private int HealAmount(Unit healer, Cell healerCell)
    {
        var power = EffectivePower(healer, healerCell);
        return healer.HasTrait(Trait.SoinParfait) ? power : power / 2;
    }

    /// <summary>Alliés BLESSÉS à portée qu'un soigneur (« Soin » ou « Soin parfait ») peut cibler.</summary>
    public List<Cell> HealTargets(Cell from)
    {
        var result = new List<Cell>();
        HealTargets(from, result);
        return result;
    }

    /// <summary>Variante SANS allocation de <see cref="HealTargets(Cell)"/>.</summary>
    public void HealTargets(Cell from, List<Cell> result)
    {
        result.Clear();
        var unit = ActiveUnitAt(from);
        if (unit == null || !IsHealer(unit))
            return;

        var attackDomaine = unit.AttackDomaine;   // le soin suit aussi le pattern d'attaque
        var vectors = Movement.Vectors(attackDomaine);
        if (Movement.Kind(attackDomaine) == MovementKind.Jump)
        {
            foreach (var off in vectors)
            {
                var to = new Cell(from.Column + off.Column, from.Row + off.Row);
                if (UnitAt(to) is { } a && a.Faction == unit.Faction && a.Hp < a.MaxHp)
                    result.Add(to);
            }
            return;
        }

        foreach (var dir in vectors)
            for (var step = 1; step <= EffectiveAttackRange(unit, from); step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to))
                    break;
                var occ = _units[to.Column, to.Row];
                if (occ == null)
                {
                    if (BlocksLineOfFire(to))
                        break;   // obstacle NU : coupe la ligne
                    continue;
                }
                // Allié PERCHÉ sur l'obstacle (« Vol ») : soignable comme les autres — s'il est attaquable
                // là-haut (cf. AppendAttackTargets), il doit pouvoir être soigné.
                if (occ.Faction == unit.Faction && occ.Hp < occ.MaxHp)
                    result.Add(to);   // premier allié blessé en vue
                break;                // toute unité borne la ligne
            }
    }

    /// <summary>Montant de soin qu'appliquerait le soigneur de <paramref name="from"/> (cf. <see cref="HealAmount"/>),
    /// pour l'aperçu de barre de vie. 0 si ce n'est pas un soigneur. Indépendant de la cible (le soin = fraction de la
    /// puissance du soigneur) ; l'affichage borne au PV manquants de la cible.</summary>
    public int PreviewHeal(Cell from) =>
        ActiveUnitAt(from) is { } healer && IsHealer(healer) ? HealAmount(healer, from) : 0;

    /// <summary>« Soin » / « Soin parfait » : soigne un allié ciblé (cf. <see cref="HealAmount"/> — moitié de
    /// la puissance, ou totalité). Fonctionne pour n'importe quel porteur du trait, commandant compris.
    /// Passe le tour.</summary>
    public MoveKind TryHeal(Cell from, Cell target)
    {
        var unit = ActiveUnitAt(from);
        if (unit == null || !HealTargets(from).Contains(target))
            return MoveKind.Invalid;

        UnitAt(target)!.Heal(HealAmount(unit, from));
        EndTurn();
        return MoveKind.Moved;   // action de soutien : tour consommé
    }

    /// <summary>
    /// Consomme l'ACTION de l'unité de <paramref name="from"/> pour un geste de CAMPAGNE que le moteur ne
    /// connaît pas (« Sacoche aimantée » : la sacoche est un objet de la MAP, pas une pièce). Rien n'est
    /// résolu ici — la scène a déjà appliqué l'effet —, mais le tour se termine exactement comme après une
    /// attaque (fins de tour comprises). <see cref="MoveKind.Invalid"/> si ce n'est pas le tour de l'unité.
    /// </summary>
    public MoveKind TrySpendAction(Cell from)
    {
        if (ActiveUnitAt(from) == null)
            return MoveKind.Invalid;

        ResetActionFx();
        EndTurn();
        return MoveKind.Moved;   // action de soutien : tour consommé
    }

    /// <summary>Passe le tour sans agir (ennemi passif en tutoriel). Sans effet si la partie est finie.</summary>
    public void PassTurn()
    {
        if (IsOver)
            return;
        ClearRecentHitsFor(CurrentTurn);   // passer, c'est aussi avoir joué son tour
        CurrentTurn = CurrentTurn.Opponent();
        if (CurrentTurn == Faction.Player)
            _extraTurnUsed = false;
    }

    /// <summary>
    /// Cibles qu'aurait l'unité de <paramref name="from"/> si elle se déplaçait en <paramref name="to"/>
    /// (plateau SIMULÉ puis restauré, tour inchangé). Outil d'IA pour repérer un coup qui amène à
    /// portée d'attaque. Renvoie une nouvelle liste (vide si <paramref name="from"/> est vide).
    /// </summary>
    public List<Cell> TargetsAfterMove(Cell from, Cell to)
    {
        var unit = UnitAt(from);
        if (unit == null)
            return new List<Cell>();

        var occupant = _units[to.Column, to.Row];
        _units[from.Column, from.Row] = null;
        _units[to.Column, to.Row] = unit;
        try
        {
            return AttackTargets(to);
        }
        finally
        {
            _units[to.Column, to.Row] = occupant;
            _units[from.Column, from.Row] = unit;
        }
    }

    /// <summary>
    /// L'attaquant prend la place de la cible tuée s'il POURRAIT s'y déplacer : case désormais
    /// libre ET atteignable par son mouvement (mêlée adjacente, saut du cavalier, ou ligne dégagée
    /// du lancier dans sa portée de déplacement). Bloqué par un allié ou hors d'atteinte → reste.
    /// </summary>
    private bool CanTakePlace(Cell from, Cell target)
    {
        LegalMoves(from, _placeBuffer);
        return _placeBuffer.Contains(target);
    }

    /// <summary>
    /// Vrai si l'attaquant de <paramref name="from"/> AVANCERAIT sur la case de <paramref name="target"/>, à
    /// supposer que sa victime y tombe. C'est la règle de <see cref="TryAttack"/> : « Statique » cloue son
    /// porteur sur place, et un tireur dont la portée d'ATTAQUE dépasse sa portée de DÉPLACEMENT reste où il
    /// est. Ne dit RIEN de la létalité du coup — l'appelant la juge à part (cf. <see cref="PreviewDamage"/>).
    /// Sert à l'aperçu de visée.
    /// </summary>
    public bool WouldTakePlace(Cell from, Cell target)
    {
        if (UnitAt(from) is not { } mover || mover.HasTrait(Trait.Statique))
            return false;
        // À la résolution, la victime est RETIRÉE de la grille AVANT ce test : sa case est alors LIBRE. On
        // reproduit cet état le temps du calcul, sinon LegalMoves écarterait la case (occupée) et l'aperçu
        // répondrait toujours non. Retrait et remise immédiats, sans rien entre les deux.
        var victim = _units[target.Column, target.Row];
        _units[target.Column, target.Row] = null;
        var can = CanTakePlace(from, target);
        _units[target.Column, target.Row] = victim;
        return can;
    }

    /// <summary>
    /// Vrai si <paramref name="unit"/>, postée en <paramref name="from"/>, POURRAIT frapper
    /// <paramref name="target"/> : mêmes règles que <see cref="AttackTargets(Cell)"/> — motif d'attaque,
    /// portée, zone morte, ligne de tir, traverse-allié — mais SANS la condition « c'est son tour ».
    /// Sert aux réactions (riposte), qui se produisent pendant le tour de l'adversaire.
    /// </summary>
    private bool CanStrike(Cell from, Unit unit, Cell target)
    {
        var reach = new List<Cell>();
        AppendAttackTargets(from, unit, unit.AttackDomaine, reach);   // « Attaque libre » déjà pris en compte
        return reach.Contains(target);
    }

    private Unit? ActiveUnitAt(Cell cell)
    {
        if (IsOver)
            return null;
        var unit = UnitAt(cell);
        return unit != null && unit.Faction == CurrentTurn ? unit : null;
    }

    private void MoveUnit(Cell from, Cell to)
    {
        _units[to.Column, to.Row] = _units[from.Column, from.Row];
        _units[from.Column, from.Row] = null;
    }

    /// <summary>
    /// Compte un saut PAR-DESSUS quelque chose pour une unité qui se déplace en L (domaine du Cavalier) :
    /// le saut « enjambe » les DEUX cases de sa grande branche (les cases (±1,0) et (±2,0) de l'axe long, ou
    /// (0,±1) et (0,±2)) ; si au moins l'une porte une unité (alliée ou ennemie) ou un obstacle de terrain
    /// (eau / montagne / mur), le saut est comptabilisé (cf. <see cref="Unit.ObstacleJumps"/>). C'est la source
    /// de points de commandement du commandant Cavalier (cf. <c>CommandeDef.JumpPoints</c>) ; le compteur est
    /// tenu pour TOUTE unité sauteuse, seul le commandant en tire des points. À appeler AVANT le déplacement.
    /// </summary>
    private void RecordObstacleJump(Unit unit, Cell from, Cell to)
    {
        if (unit.MovementKind != MovementKind.Jump)
            return;
        var dc = to.Column - from.Column;
        var dr = to.Row - from.Row;
        var (adc, adr) = (System.Math.Abs(dc), System.Math.Abs(dr));
        if (!((adc == 2 && adr == 1) || (adc == 1 && adr == 2)))
            return;   // pas un saut en L (ex. « Repositionnement stratégique ») : rien à enjamber

        var (sc, sr) = adc == 2 ? (System.Math.Sign(dc), 0) : (0, System.Math.Sign(dr));
        for (var step = 1; step <= 2; step++)
        {
            var over = new Cell(from.Column + sc * step, from.Row + sr * step);
            if (UnitAt(over) != null || (InBounds(over) && BlocksMovement(over)))
            {
                unit.RecordObstacleJump();
                return;
            }
        }
    }

    private void EndTurn()
    {
        UpdateWinner();
        if (IsOver)
            return;

        // « Vengeance » : la colère dure UN tour. Le camp qui vient de jouer la perd — sauf si elle vient de
        // naître pendant ce tour-ci (cf. OnUnitDied), auquel cas elle est gardée pour le tour suivant.
        var played = (int)CurrentTurn;
        if (_vengeanceFresh[played])
            _vengeanceFresh[played] = false;
        else
            ClearVengeanceFor(CurrentTurn);

        ClearRecentHitsFor(CurrentTurn);   // le camp qui vient de jouer a eu sa chance de répondre
        CurrentTurn = CurrentTurn.Opponent();
        if (CurrentTurn == Faction.Player)
            _extraTurnUsed = false;   // la main revient au joueur : son tour bonus est de nouveau disponible
    }

    private void UpdateWinner()
    {
        bool hasPlayer = false, hasEnemy = false;
        foreach (var (_, unit) in Units())
        {
            if (unit.Faction == Faction.Player) hasPlayer = true;
            else hasEnemy = true;
        }

        // Une unité essentielle morte décide la partie, même si son camp a d'autres unités :
        // commandant tombé = défaite ; boss tué = victoire (combat de boss).
        bool playerLeaderDown = false, enemyLeaderDown = false, playerLeaderStanding = false;
        foreach (var unit in _essential)
        {
            if (unit.IsAlive)
            {
                if (unit.Faction == Faction.Player) playerLeaderStanding = true;
                continue;
            }
            if (unit.Faction == Faction.Player) playerLeaderDown = true;
            else enemyLeaderDown = true;
        }
        // DUO — « Continue sans moi » : la chute d'UN meneur ne décide plus rien tant que l'AUTRE est debout.
        // Le camp joueur ne perd que s'il ne reste plus AUCUN meneur — ou plus aucune unité (test ci-dessous).
        if (SoloSurvivorEnabled && playerLeaderStanding)
            playerLeaderDown = false;

        // Camp joueur anéanti (ou commandant tombé) = défaite : toujours décisif, même à objectif.
        if (!hasPlayer || playerLeaderDown) Winner = Faction.Enemy;
        // Camp ennemi anéanti = victoire, SAUF en mode objectif (mission spéciale) où l'on laisse le
        // joueur poursuivre l'objectif. Le boss tué reste décisif dans tous les cas.
        else if ((!hasEnemy && _eliminationEndsGame) || enemyLeaderDown) Winner = Faction.Player;
    }
}
