using System.Collections.Generic;
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

    // Résultats de la DERNIÈRE action (déplacement/attaque) du porteur, pour le feedback de la scène.
    // Réinitialisés au début de chaque TryMove/TryAttack ; restent vides/null si le trait est absent.
    private readonly List<(Cell Cell, int Damage)> _impactHits = new();   // « Impact » : ennemis touchés autour du porteur
    private readonly List<Cell> _impactZone = new();                      // « Impact » : les 8 cases de l'AoE (touchées ou non) pour le tremblement des tuiles
    private readonly List<Cell> _seismeZone = new();                      // « Séisme » : union des voisinages 3×3 des porteurs, pour le tremblement des tuiles
    private (Cell To, int SlamDamage)? _lastRecule;                        // « Recule » : case d'arrivée de la cible + dégât de plaquage (0 si glissée)
    private (Cell From, Cell To, int Damage, bool Killed)? _lastRiposte;   // « Riposte » : case du riposteur → assaillant + dégâts + assaillant abattu
    private (Cell Cell, int Damage, int Dc, int Dr)? _lastPierce;          // « Transpercement » : ennemi derrière la cible touché + dégâts + direction du coup
    private readonly List<(Cell From, Cell To, int Damage, bool Killed)> _interceptions = new();  // « Interception » : intercepteur → mobile + dégâts + mobile abattu (par ordre de frappe)

    // Source d'aléa du combat : sert AUJOURD'HUI uniquement au trait « Esquive » (25 % d'annuler une
    // attaque). Injectable pour des tests reproductibles ; sans esquive en jeu, elle n'est jamais tirée.
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
        IEnumerable<Cell>? playerBlockedCells = null, int rempartBonus = 0, int esquiveBonusPercent = 0,
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
        EsquiveBonusPercent = esquiveBonusPercent;
        TueurDeGeantsBonus = tueurGeantsBonus;
        FormationBonus = formationBonus;
    }

    /// <summary>Bonus d'arbre « Rempart renforcé » (points de réduction en PLUS de <see cref="BaseRempartReduction"/>).
    /// Appliqué UNIQUEMENT aux unités du JOUEUR — c'est SON arbre de commandement, l'ennemi n'en profite pas.
    /// Réglable par la scène si un nœud est acheté en cours de placement. Cf. <see cref="RempartReductionFor"/>.</summary>
    public int RempartBonus { get; set; }

    /// <summary>Bonus d'arbre « Esquive renforcée » (points de %, en PLUS de <see cref="BaseEsquiveChance"/>).
    /// Appliqué UNIQUEMENT aux unités du JOUEUR. Cf. <see cref="EsquiveChanceFor"/>.</summary>
    public int EsquiveBonusPercent { get; set; }

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

    /// <summary>Réduction EFFECTIVE du trait « Rempart » pour <paramref name="victim"/> : base, plus le bonus
    /// d'arbre SEULEMENT si c'est une unité du joueur (le renforcement ne touche pas l'ennemi).</summary>
    private int RempartReductionFor(Unit victim) =>
        BaseRempartReduction + (victim.Faction == Faction.Player ? RempartBonus : 0);

    /// <summary>Chance EFFECTIVE (0..1) du trait « Esquive » pour <paramref name="unit"/> : base, plus le bonus
    /// d'arbre SEULEMENT si c'est une unité du joueur.</summary>
    private double EsquiveChanceFor(Unit unit) =>
        BaseEsquiveChance + (unit.Faction == Faction.Player ? EsquiveBonusPercent / 100.0 : 0);

    /// <summary>Vrai si la case offre un COUVERT (buisson) : l'unité dessus reçoit moins de dégâts.</summary>
    private bool IsCover(Cell cell) => _cover.Contains(cell);

    /// <summary>Vrai si la tuile interdit le déplacement (mur, eau).</summary>
    private bool BlocksMovement(Cell cell) =>
        _terrain != null && _terrain[cell].BlocksMovement;

    /// <summary>Vrai si la tuile arrête une ligne de tir (mur).</summary>
    private bool BlocksLineOfFire(Cell cell) =>
        _terrain != null && _terrain[cell].BlocksLineOfFire;

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
        var unit = ActiveUnitAt(from);
        if (unit == null)
            return;

        var vectors = Movement.Vectors(unit.Domaine);
        var flies = unit.HasTrait(Trait.Vol);   // Vol : les obstacles de terrain (eau/montagne) ne bloquent plus

        if (Movement.Kind(unit.Domaine) == MovementKind.Jump)
        {
            foreach (var offset in vectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (InBounds(to) && _units[to.Column, to.Row] == null && (flies || !BlocksMovement(to))
                    && !BlocksPlayerLanding(to, unit))   // le joueur ne se pose pas sur un paysan (mission « protéger »)
                    result.Add(to);
            }
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
                if (_units[to.Column, to.Row] != null)
                {
                    if (phases) continue;   // Franchissement : on enjambe l'unité (sans pouvoir s'y poser)
                    break;                  // sinon une unité borne le déplacement
                }
                if (BlocksPlayerLanding(to, unit))
                    continue;   // le joueur GLISSE au travers d'un paysan (protéger) mais ne s'y arrête pas
                result.Add(to);
            }
        }
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

        // Pattern d'ATTAQUE natif de l'unité (peut différer du déplacement : cavalier monté = saut en L).
        AppendAttackTargets(from, unit, unit.AttackDomaine, result);

        // « Attaque libre » : AJOUTE le tir « comme une Dame » (8 directions en ligne) EN PLUS de l'attaque
        // native — un cavalier monté garde son attaque au SAUT (L) et gagne le tir en lignes.
        if (unit.HasTrait(Trait.AttaqueLibre) && unit.AttackDomaine != Domaine.Dame)
            AppendAttackTargets(from, unit, Domaine.Dame, result);
    }

    /// <summary>
    /// Ajoute à <paramref name="result"/> les ENNEMIS atteignables selon le pattern <paramref name="attackDomaine"/> :
    /// SAUT = cases en L (cavalier) ; GLISSÉ = 1er ennemi rencontré par direction (zone morte, ligne de tir /
    /// montagne, traverse-allié respectés). Sans doublon — permet d'UNIR plusieurs patterns (cf. « Attaque libre »).
    /// </summary>
    private void AppendAttackTargets(Cell from, Unit unit, Domaine attackDomaine, List<Cell> result)
    {
        var vectors = Movement.Vectors(attackDomaine);

        if (Movement.Kind(attackDomaine) == MovementKind.Jump)
        {
            foreach (var offset in vectors)
            {
                var to = new Cell(from.Column + offset.Column, from.Row + offset.Row);
                if (UnitAt(to) is { } target && target.Faction != unit.Faction && !result.Contains(to))
                    result.Add(to);
            }
            return;
        }

        var piercesAllies = unit.HasTrait(Trait.TraverseAllie);   // via HasTrait : classe (PiercesAllies) OU équipement
        var balistique = unit.HasTrait(Trait.Balistique);   // tir indirect : la montagne ne coupe plus la ligne
        foreach (var dir in vectors)
        {
            // Zone morte (portée min) UNIQUEMENT en ligne droite : en diagonale on peut tirer dès la
            // distance 1 (le contact « corps à corps » n'est interdit qu'en face/côté).
            var minStep = dir.Column != 0 && dir.Row != 0 ? 1 : unit.MinAttackRange;
            for (var step = 1; step <= unit.AttackRange; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to))
                    break; // hors plateau : la ligne de tir s'arrête
                if (BlocksLineOfFire(to) && !balistique)
                    break; // montagne : coupe la ligne (sauf tir balistique ; l'eau laisse toujours passer)

                var target = _units[to.Column, to.Row];
                if (target == null)
                    continue; // case vide (ou eau) : la ligne de tir continue

                if (target.Faction != unit.Faction)
                {
                    // Premier ennemi en vue : cible SI au-delà de la zone morte de cette direction.
                    // Dans tous les cas son corps borne la ligne (pas de tir au travers).
                    if (step >= minStep && !result.Contains(to))
                        result.Add(to);
                    break;
                }

                // Allié : le LANCIER le traverse sans le toucher (ne borne pas) ; sinon il bloque.
                if (!piercesAllies)
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

        // Pattern d'ATTAQUE natif (cavalier monté = saut en L), + « Attaque libre » = menace en lignes de Dame EN PLUS.
        AppendThreatenedCells(from, unit, unit.AttackDomaine, result);
        if (unit.HasTrait(Trait.AttaqueLibre) && unit.AttackDomaine != Domaine.Dame)
            AppendThreatenedCells(from, unit, Domaine.Dame, result);
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
        var balistique = unit.HasTrait(Trait.Balistique);   // tir indirect : la montagne ne coupe plus la ligne
        foreach (var dir in vectors)
        {
            var minStep = dir.Column != 0 && dir.Row != 0 ? 1 : unit.MinAttackRange;
            for (var step = 1; step <= unit.AttackRange; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to))
                    break; // hors plateau : la menace ne porte pas au-delà
                if (BlocksLineOfFire(to) && !balistique)
                    break; // montagne : coupe la ligne (sauf tir balistique ; l'eau laisse passer)

                var occupant = _units[to.Column, to.Row];
                if (occupant != null && occupant.Faction == unit.Faction && piercesAllies)
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
        MoveUnit(from, to);
        TriggerInterceptions(to, unit);   // ennemis avec « Interception » dont la portée couvre la case d'arrivée
        // « Impact » : à son propre déplacement, le porteur frappe les ennemis autour de sa case d'arrivée
        // (s'il a survécu à une éventuelle interception).
        if (unit.IsAlive && unit.HasTrait(Trait.Impact))
            ApplyImpact(unit, to);
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
        ApplyDamage(victim, EffectiveDamage(unit, from, victim, target), unit);

        // Drain de vie : l'attaquant récupère 50 % des dégâts RÉELLEMENT infligés (esquive/bouclier inclus).
        if (unit.HasTrait(Trait.DrainDeVie))
            unit.Heal((victimHpBefore - victim.Hp) / 2);

        // Transpercement : l'unité juste DERRIÈRE la cible (même direction) est aussi touchée.
        if (unit.HasTrait(Trait.Transpercement))
            PierceBehind(from, target, unit);

        // Orage / Tempête : la foudre frappe 3 ennemis AU HASARD (hors cible directe) pour un dégât fixe.
        if (StormDamageFor(unit) is > 0 and var storm)
            StormStrike(unit, target, storm);

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
            unit.RecordKill();                           // mise à mort créditée à l'attaquant (compteur à vie)
            _units[target.Column, target.Row] = null;   // case libérée AVANT de tester l'accès
            OnUnitDied(victim);                          // « Rage » : les alliés survivants du mort gagnent de la puissance
            // « Statique » : ne prend JAMAIS la place de sa cible — l'attaquant reste sur sa case (la case de
            // la victime reste libre). Sinon, comportement normal : il avance sur la case si l'accès le permet.
            if (!unit.HasTrait(Trait.Statique) && CanTakePlace(from, target))
                MoveUnit(from, target);
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
            if (victim.IsAlive && victim.HasTrait(Trait.Riposte)
                && UnitAt(from) is { } attacker && ReferenceEquals(attacker, unit)
                && CellOf(victim) is { } vc && CanStrike(vc, victim, from))
            {
                var attackerHpBefore = attacker.Hp;
                ApplyDamage(attacker, EffectiveDamage(victim, vc, attacker, from), victim);
                _lastRiposte = (vc, from, attackerHpBefore - attacker.Hp, !attacker.IsAlive);   // report feedback
                RemoveDeadAt(from, victim);   // la riposte tue l'attaquant : kill crédité à la victime
            }
            kind = MoveKind.Attacked; // l'attaquant reste sur place
        }

        // « Impact » : le porteur frappe les ennemis autour de sa position FINALE (from, ou la case prise sur
        // un kill) — jamais si un contre l'a abattu (CellOf renvoie null). Déclenché par sa seule attaque.
        if (unit.HasTrait(Trait.Impact) && CellOf(unit) is { } here)
            ApplyImpact(unit, here);

        EndTurn();
        return kind;
    }

    // ─── TRAITS : dégâts effectifs, formes d'attaque, réactions ───────────────────────────────────

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
    /// <summary>Chance de BASE (0..1) du trait « Esquive » (avant bonus d'arbre « Esquive renforcée »).</summary>
    public const double BaseEsquiveChance = 0.25; // 25 % de chance d'annuler une attaque subie (trait « Esquive »)
    private const int OrageDamage = 3;           // dégât fixe de l'orage (trait « Orage »)
    private const int TempeteDamage = 6;         // dégât fixe de la tempête (trait « Tempête »)
    private const int ImpactDamage = 5;          // dégât fixe autour du porteur d'« Impact » (déplacement/attaque)
    private const int ReculeSlamDamage = 5;      // dégât bonus quand « Recule » plaque la cible contre un obstacle

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

    /// <summary>Riposte de la DERNIÈRE attaque : case du riposteur (après un éventuel recul) → case de l'assaillant,
    /// dégâts réellement infligés, et si l'assaillant en est mort. <c>null</c> si aucune riposte. Pour le feedback.</summary>
    public (Cell From, Cell To, int Damage, bool Killed)? LastRiposte => _lastRiposte;

    /// <summary>« Interception(s) » du DERNIER déplacement : pour chaque intercepteur qui a frappé le mobile, sa case
    /// → la case d'arrivée du mobile, les dégâts réels et si le mobile en est mort (ordre de frappe). Vide sinon.
    /// Pour rejouer l'animation d'attaque de chaque intercepteur (le moteur a déjà appliqué les dégâts).</summary>
    public IReadOnlyList<(Cell From, Cell To, int Damage, bool Killed)> LastInterceptions => _interceptions;

    /// <summary>« Transpercement » de la DERNIÈRE attaque : case de l'ennemi transpercé (derrière la cible), dégâts
    /// réellement infligés (&gt; 0) et direction (dc, dr) du coup ; <c>null</c> si aucun transpercement (ou esquivé).
    /// Pour le feedback : recul + chiffre + mot-clé sur le pion transpercé.</summary>
    public (Cell Cell, int Damage, int Dc, int Dr)? LastPierce => _lastPierce;

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
    /// Bonus de puissance CONTEXTUEL total d'une unité : ce qu'elle tient de son PLACEMENT — auras alliées
    /// (<see cref="AuraPowerBonus"/>) et Formation (<see cref="FormationPowerBonus"/>) — et que sa fiche ne
    /// laisse pas deviner. La carte l'affiche en « +N » sur la puissance pour rester fidèle aux dégâts
    /// réellement infligés. Le Berserk (kills) et la Rage (alliés morts), eux, dépendent de l'état du pion
    /// (pas du placement) et se calculent à part.
    /// </summary>
    public int ContextualPowerBonus(Cell cell) => AuraPowerBonus(cell) + FormationPowerBonus(cell);

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
    /// PUISSANCE EFFECTIVE de l'unité posée sur <paramref name="cell"/> : sa puissance de base plus TOUS ses
    /// bonus offensifs contextuels — Berserk (+1 par ennemi tué à vie), Rage (+7 à la première mort d'un allié),
    /// Aura de puissance d'un allié adjacent, Formation (+2 par allié adjacent). C'est la valeur affichée sur
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
        power += AuraPowerBonus(cell);
        power += FormationPowerBonus(cell);
        return System.Math.Max(0, power);
    }

    /// <summary>
    /// Dégâts EFFECTIFS d'une attaque, traits inclus : la <see cref="EffectivePower"/> de l'attaquant, moins
    /// Rempart / Aura de rempart (partout SAUF au contact direct orthogonal), Duelliste (corps à corps) et
    /// le couvert d'un buisson. Borné à 0.
    /// </summary>
    private int EffectiveDamage(Unit attacker, Cell attackerCell, Unit victim, Cell victimCell)
    {
        var dmg = EffectivePower(attacker, attackerCell);

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
        if (IsCover(victimCell))               // cible à couvert dans un buisson
            dmg -= BushReduction;

        return System.Math.Max(0, dmg);
    }

    /// <summary>
    /// Applique des dégâts. « Esquive » peut annuler entièrement l'attaque (25 %).
    /// </summary>
    private void ApplyDamage(Unit unit, int amount, Unit? attacker)
    {
        if (amount <= 0)
            return;
        if (unit.HasTrait(Trait.Esquive) && _rng.NextDouble() < EsquiveChanceFor(unit))
            return;   // attaque esquivée : aucun dégât
        unit.TakeDamage(amount);
        unit.RecordHit();               // coup RÉELLEMENT encaissé (esquive/0 exclus) — points d'un commandant
        attacker?.RecordDamage(amount);  // dégâts RÉELLEMENT infligés — récap de fin de run (dégâts par type)
    }

    /// <summary>Dégâts EFFECTIFS qu'infligerait l'attaque de <paramref name="from"/> sur
    /// <paramref name="target"/> (traits inclus), bornés aux PV de la cible — pour l'affichage.</summary>
    public int PreviewDamage(Cell from, Cell target)
    {
        var attacker = UnitAt(from);
        var victim = UnitAt(target);
        if (attacker == null || victim == null)
            return 0;
        return System.Math.Min(EffectiveDamage(attacker, from, victim, target), victim.Hp);
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
    /// autour d'elle pour ses dégâts EFFECTIFS (sa puissance, moins les défenses de chaque cible). Les morts
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

        foreach (var casterCell in casterCells)
        {
            if (UnitAt(casterCell) is not { } caster || !caster.HasTrait(Trait.Seisme))
                continue;   // porteur tombé entre-temps (cas de bord)
            foreach (var (dc, dr) in Neighbors8)
            {
                var c = new Cell(casterCell.Column + dc, casterCell.Row + dr);
                if (UnitAt(c) is not { } victim || victim.Faction == actor)
                    continue;
                var before = victim.Hp;
                ApplyDamage(victim, EffectiveDamage(caster, casterCell, victim, c), caster);
                var dealt = before - victim.Hp;   // 0 si esquivé
                if (dealt > 0)
                    hits.Add((c, dealt));
                RemoveDeadAt(c, caster);
            }
        }

        UpdateWinner();   // un séisme peut achever le dernier ennemi → victoire
        return hits;
    }


    /// <summary>Nombre MAX d'ennemis foudroyés par Orage/Tempête (tirés au hasard s'il y en a davantage).</summary>
    private const int StormMaxTargets = 3;

    /// <summary>
    /// « Orage » / « Tempête » : à l'attaque, la foudre frappe jusqu'à <see cref="StormMaxTargets"/> ennemis du
    /// porteur TIRÉS AU HASARD (parmi tous, SAUF la cible directe <paramref name="target"/>), chacun pour un
    /// dégât FIXE (<paramref name="amount"/>, ni réduit par Rempart/couvert ni majoré par les traits offensifs —
    /// mais Esquive s'applique via <see cref="ApplyDamage"/>). Tirage via <see cref="_rng"/>.
    /// Les cibles sont figées avant application (la grille change en cours).
    /// </summary>
    private void StormStrike(Unit attacker, Cell target, int amount)
    {
        var victims = new List<Cell>();
        foreach (var (cell, unit) in Units())
            if (unit.Faction != attacker.Faction && cell != target)
                victims.Add(cell);

        // Ne foudroie que StormMaxTargets ennemis : mélange partiel (Fisher-Yates) pour en tirer autant au hasard.
        var count = System.Math.Min(StormMaxTargets, victims.Count);
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

    /// <summary>Vide les résultats de feedback (« Impact »/« Recule »/« Riposte ») avant de résoudre une nouvelle action.</summary>
    private void ResetActionFx()
    {
        _impactHits.Clear();
        _impactZone.Clear();
        _lastRecule = null;
        _lastRiposte = null;
        _lastPierce = null;
        _interceptions.Clear();
    }

    /// <summary>
    /// « Impact » : le porteur inflige un dégât FIXE (<see cref="ImpactDamage"/>) à TOUS les ennemis des 8 cases
    /// autour de <paramref name="center"/> (sa position finale). Déclenché UNIQUEMENT par sa propre action
    /// (déplacement / attaque), jamais relayé par un autre trait. Fixe : ni Rempart ni couvert ne le réduisent,
    /// mais Esquive peut l'annuler (via <see cref="ApplyDamage"/>). Les coups réels sont notés dans
    /// <see cref="_impactHits"/> pour le feedback ; l'appelant a réinitialisé la liste via <see cref="ResetActionFx"/>.
    /// </summary>
    private void ApplyImpact(Unit unit, Cell center)
    {
        foreach (var (dc, dr) in Neighbors8)
        {
            var c = new Cell(center.Column + dc, center.Row + dr);
            _impactZone.Add(c);   // toute la zone AoE tremble au feedback, même les cases sans cible
            if (UnitAt(c) is not { } victim || victim.Faction == unit.Faction)
                continue;
            var before = victim.Hp;
            ApplyDamage(victim, ImpactDamage, unit);
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
            _lastRecule = (behind, 0);
        }
        else
        {
            var before = victim.Hp;         // plaquée contre un obstacle (bord / unité / terrain) : dégât bonus
            ApplyDamage(victim, ReculeSlamDamage, attacker);
            RemoveDeadAt(target, attacker);
            _lastRecule = (target, before - victim.Hp);
        }
    }

    /// <summary>« Interception » : chaque ennemi du mobile dont la portée couvre la case d'arrivée le frappe.</summary>
    private void TriggerInterceptions(Cell movedTo, Unit mover)
    {
        foreach (var (cell, unit) in Units())
        {
            if (unit.Faction == mover.Faction || !unit.HasTrait(Trait.Interception))
                continue;
            if (!ThreatenedCells(cell).Contains(movedTo))
                continue;
            var before = mover.Hp;
            ApplyDamage(mover, EffectiveDamage(unit, cell, mover, movedTo), unit);
            var killed = !mover.IsAlive;
            _interceptions.Add((cell, movedTo, before - mover.Hp, killed));   // report pour l'animation d'attaque
            if (killed)
            {
                RemoveDeadAt(movedTo, unit);   // l'intercepteur abat le mobile : kill crédité
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
        OnUnitDied(dead);   // « Rage » : les alliés survivants du mort gagnent de la puissance
    }

    /// <summary>
    /// Signale la mort de <paramref name="dead"/> (déjà retiré de la grille) : chaque ALLIÉ SURVIVANT porteur de
    /// « Rage » voit son bonus fixé à <see cref="RagePowerBonus"/> pour le RESTE du combat. NON cumulable : un
    /// porteur déjà enragé n'y gagne rien de plus aux morts suivantes (une seule fois par combat). Buff transitoire,
    /// non persisté — un nouveau combat repart d'unités neuves. Appelé à chaque chemin de mort.
    /// </summary>
    private void OnUnitDied(Unit dead)
    {
        foreach (var (_, u) in Units())
            if (u.Faction == dead.Faction && u.HasTrait(Trait.Rage))
                u.ActivateRage(RagePowerBonus);
    }

    /// <summary>
    /// « Queue de phénix » : si l'unité TOMBÉE porte l'équipement de <see cref="Trait.Renaissance"/>, elle
    /// ressuscite à 1 PV et l'équipement se brise (consommé). Renvoie vrai si une renaissance a eu lieu —
    /// l'unité RESTE alors en jeu (aucun kill ne doit être crédité, aucune case libérée).
    /// </summary>
    private static bool TryReviveWithEquipment(Unit unit)
    {
        if (unit.IsAlive || unit.Equipment is not { } eq || !eq.GrantsTrait(Trait.Renaissance))
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
            for (var step = 1; step <= unit.AttackRange; step++)
            {
                var to = new Cell(from.Column + dir.Column * step, from.Row + dir.Row * step);
                if (!InBounds(to) || BlocksLineOfFire(to))
                    break;
                var occ = _units[to.Column, to.Row];
                if (occ == null)
                    continue;
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

    /// <summary>Passe le tour sans agir (ennemi passif en tutoriel). Sans effet si la partie est finie.</summary>
    public void PassTurn()
    {
        if (!IsOver)
            CurrentTurn = CurrentTurn.Opponent();
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
    /// Vrai si <paramref name="unit"/>, postée en <paramref name="from"/>, POURRAIT frapper
    /// <paramref name="target"/> : mêmes règles que <see cref="AttackTargets(Cell)"/> — motif d'attaque,
    /// portée, zone morte, ligne de tir, traverse-allié — mais SANS la condition « c'est son tour ».
    /// Sert aux réactions (riposte), qui se produisent pendant le tour de l'adversaire.
    /// </summary>
    private bool CanStrike(Cell from, Unit unit, Cell target)
    {
        var reach = new List<Cell>();
        AppendAttackTargets(from, unit, unit.AttackDomaine, reach);
        if (unit.HasTrait(Trait.AttaqueLibre) && unit.AttackDomaine != Domaine.Dame)
            AppendAttackTargets(from, unit, Domaine.Dame, reach);
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

    private void EndTurn()
    {
        UpdateWinner();
        if (!IsOver)
            CurrentTurn = CurrentTurn.Opponent();
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
        bool playerLeaderDown = false, enemyLeaderDown = false;
        foreach (var unit in _essential)
        {
            if (unit.IsAlive) continue;
            if (unit.Faction == Faction.Player) playerLeaderDown = true;
            else enemyLeaderDown = true;
        }

        // Camp joueur anéanti (ou commandant tombé) = défaite : toujours décisif, même à objectif.
        if (!hasPlayer || playerLeaderDown) Winner = Faction.Enemy;
        // Camp ennemi anéanti = victoire, SAUF en mode objectif (mission spéciale) où l'on laisse le
        // joueur poursuivre l'objectif. Le boss tué reste décisif dans tous les cas.
        else if ((!hasEnemy && _eliminationEndsGame) || enemyLeaderDown) Winner = Faction.Player;
    }
}
