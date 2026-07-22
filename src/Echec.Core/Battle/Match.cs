using System.Collections.Generic;
using Echec.Core.Map;

namespace Echec.Core.Battle;

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
        IEnumerable<Cell>? playerBlockedCells = null)
    {
        Width = width;
        Height = height;
        _units = new Unit?[width, height];
        _terrain = terrain;
        _cover = coverCells is null ? new HashSet<Cell>() : new HashSet<Cell>(coverCells);
        _rng = rng ?? new System.Random();
        _eliminationEndsGame = eliminationEndsGame;
        _playerBlocked = playerBlockedCells is null ? new HashSet<Cell>() : new HashSet<Cell>(playerBlockedCells);
    }

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
        foreach (var dir in vectors)
        {
            for (var step = 1; step <= unit.MoveRange; step++)
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

        MoveUnit(from, to);
        TriggerInterceptions(to, unit);   // ennemis avec « Interception » dont la portée couvre la case d'arrivée
        EndTurn();
        return MoveKind.Moved;
    }

    /// <summary>Attaque une cible ennemie à portée de tir. Passe le tour en cas de succès.</summary>
    public MoveKind TryAttack(Cell from, Cell target)
    {
        var unit = ActiveUnitAt(from);
        if (unit == null || !AttackTargets(from).Contains(target))
            return MoveKind.Invalid;

        var victim = _units[target.Column, target.Row]!;
        var victimHpBefore = victim.Hp;
        ApplyDamage(target, victim, EffectiveDamage(unit, from, victim, target), unit);

        // Drain de vie : l'attaquant récupère 50 % des dégâts RÉELLEMENT infligés (esquive/bouclier inclus).
        if (unit.HasTrait(Trait.DrainDeVie))
            unit.Heal((victimHpBefore - victim.Hp) / 2);

        // Dégâts de zone / Embrochage : éclaboussure (mêmes dégâts effectifs) sur les ennemis autour de la cible.
        if (unit.HasTrait(Trait.DegatsDeZone) || unit.HasTrait(Trait.Embrochage))
            SplashAround(target, unit, from);

        // Transpercement : l'unité juste DERRIÈRE la cible (même direction) est aussi touchée.
        if (unit.HasTrait(Trait.Transpercement))
            PierceBehind(from, target, unit);

        // Orage / Tempête : la foudre frappe 3 ennemis AU HASARD (hors cible directe) pour un dégât fixe.
        if (StormDamageFor(unit) is > 0 and var storm)
            StormStrike(unit, target, storm);

        MoveKind kind;
        if (!victim.IsAlive)
        {
            unit.RecordKill();                           // mise à mort créditée à l'attaquant (compteur à vie)
            _units[target.Column, target.Row] = null;   // case libérée AVANT de tester l'accès
            // « Statique » : ne prend JAMAIS la place de sa cible — l'attaquant reste sur sa case (la case de
            // la victime reste libre). Sinon, comportement normal : il avance sur la case si l'accès le permet.
            if (!unit.HasTrait(Trait.Statique) && CanTakePlace(from, target))
                MoveUnit(from, target);
            kind = MoveKind.Killed;
        }
        else
        {
            // Riposte : la victime SURVIVANTE contre-attaque, à condition de POUVOIR réellement frapper son
            // assaillant — mêmes règles que son attaque normale (motif, portée, zone morte, ligne de tir,
            // traverse-allié). Ce n'est donc plus réservé au corps à corps : un tireur riposte à distance,
            // mais un assaillant hors de portée, en diagonale d'une unité « Tour » ou derrière un obstacle
            // ne prend rien.
            if (victim.HasTrait(Trait.Riposte)
                && UnitAt(from) is { } attacker && ReferenceEquals(attacker, unit)
                && CanStrike(target, victim, from))
            {
                ApplyDamage(from, attacker, EffectiveDamage(victim, target, attacker, from), victim);
                RemoveDeadAt(from, victim);   // la riposte tue l'attaquant : kill crédité à la victime
            }
            kind = MoveKind.Attacked; // l'attaquant reste sur place
        }

        EndTurn();
        return kind;
    }

    // ─── TRAITS : dégâts effectifs, formes d'attaque, réactions ───────────────────────────────────

    private const int BushReduction = 4;         // -4 dégâts quand la cible est sur un buisson (couvert)
    private const int RempartReduction = 4;     // -4 dégâts d'une attaque à distance (>= 2)
    private const int DuellisteReduction = 4;    // -4 dégâts d'une attaque au corps à corps
    private const int RageBonus = 6;             // +6 puissance quand l'attaquant est sous le seuil PV
    private const int RageHpThreshold = 10;      // seuil de PV de Rage
    private const int BenedictionBonus = 5;      // +5 puissance offerte par un allié « Bénédiction » adjacent
    private const int AuraPuissanceBonus = 3;    // +3 puissance offerte par un allié « Aura de puissance » adjacent
    private const int AuraSurpuissanceBonus = 5; // +5 puissance offerte par un allié « Aura de surpuissance » adjacent
    private const int FormationBonus = 2;        // +2 puissance par allié adjacent (trait « Formation »)
    private const double EsquiveChance = 0.25;   // 25 % de chance d'annuler une attaque subie (trait « Esquive »)
    private const int OrageDamage = 3;           // dégât fixe de l'orage (trait « Orage »)
    private const int TempeteDamage = 6;         // dégât fixe de la tempête (trait « Tempête »)

    /// <summary>Dégât fixe de foudre infligé par <paramref name="unit"/> à l'attaque (Tempête &gt; Orage &gt; 0 si aucun).</summary>
    public static int StormDamageFor(Unit unit) =>
        unit.HasTrait(Trait.Tempete) ? TempeteDamage
        : unit.HasTrait(Trait.Orage) ? OrageDamage
        : 0;

    private static readonly (int Dc, int Dr)[] Neighbors8 =
        { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };

    private static int ChebyshevDistance(Cell a, Cell b) =>
        System.Math.Max(System.Math.Abs(a.Column - b.Column), System.Math.Abs(a.Row - b.Row));

    /// <summary>Vrai si une case adjacente porte un allié de <paramref name="faction"/> avec ce trait.</summary>
    private bool HasAdjacentAlly(Cell cell, Faction faction, string trait)
    {
        foreach (var (dc, dr) in Neighbors8)
            if (UnitAt(new Cell(cell.Column + dc, cell.Row + dr)) is { } u
                && u.Faction == faction && u.HasTrait(trait))
                return true;
        return false;
    }

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
    /// Dégâts EFFECTIFS d'une attaque, traits inclus : Rage / Bénédiction / Aura de puissance / Aura de
    /// surpuissance (offensifs), Rempart / Aura de rempart (à distance ≥ 2) et Duelliste (corps à corps) en
    /// réduction. Borné à 0.
    /// </summary>
    private int EffectiveDamage(Unit attacker, Cell attackerCell, Unit victim, Cell victimCell)
    {
        var dmg = attacker.Damage;
        if (attacker.HasTrait(Trait.Rage) && attacker.Hp < RageHpThreshold)
            dmg += RageBonus;
        if (HasAdjacentAlly(attackerCell, attacker.Faction, Trait.Benediction))
            dmg += BenedictionBonus;
        if (HasAdjacentAlly(attackerCell, attacker.Faction, Trait.AuraDePuissance))
            dmg += AuraPuissanceBonus;
        if (HasAdjacentAlly(attackerCell, attacker.Faction, Trait.AuraDeSurpuissance))
            dmg += AuraSurpuissanceBonus;
        if (attacker.HasTrait(Trait.Formation))
            dmg += FormationBonus * AdjacentAllyCount(attackerCell, attacker.Faction);

        var distance = ChebyshevDistance(attackerCell, victimCell);
        var shielded = victim.HasTrait(Trait.Rempart)
            || HasAdjacentAlly(victimCell, victim.Faction, Trait.AuraDeRempart);
        if (distance >= 2 && shielded)
            dmg -= RempartReduction;
        if (distance == 1 && victim.HasTrait(Trait.Duelliste))
            dmg -= DuellisteReduction;
        if (IsCover(victimCell))               // cible à couvert dans un buisson
            dmg -= BushReduction;

        return System.Math.Max(0, dmg);
    }

    /// <summary>
    /// Applique des dégâts. « Esquive » peut annuler entièrement l'attaque (25 %). Un allié adjacent
    /// « Bouclier divin » empêche la mort (PV ≥ 1).
    /// </summary>
    private void ApplyDamage(Cell cell, Unit unit, int amount, Unit? attacker)
    {
        if (amount <= 0)
            return;
        if (unit.HasTrait(Trait.Esquive) && _rng.NextDouble() < EsquiveChance)
            return;   // attaque esquivée : aucun dégât
        if (amount >= unit.Hp && HasAdjacentAlly(cell, unit.Faction, Trait.BouclierDivin))
            amount = unit.Hp - 1;   // laisse 1 PV : l'attaque n'est jamais mortelle
        if (amount > 0)
        {
            unit.TakeDamage(amount);
            unit.RecordHit();               // coup RÉELLEMENT encaissé (esquive/0 exclus) — points d'un commandant
            attacker?.RecordDamage(amount);  // dégâts RÉELLEMENT infligés — récap de fin de run (dégâts par type)
        }
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

    /// <summary>« Dégâts de zone » : touche les ennemis des 8 cases autour de la cible (mêmes dégâts effectifs).</summary>
    private void SplashAround(Cell center, Unit attacker, Cell attackerCell)
    {
        foreach (var (dc, dr) in Neighbors8)
        {
            var c = new Cell(center.Column + dc, center.Row + dr);
            if (UnitAt(c) is not { } u || u.Faction == attacker.Faction)
                continue;
            ApplyDamage(c, u, EffectiveDamage(attacker, attackerCell, u, c), attacker);
            RemoveDeadAt(c, attacker);
        }
    }

    /// <summary>Nombre MAX d'ennemis foudroyés par Orage/Tempête (tirés au hasard s'il y en a davantage).</summary>
    private const int StormMaxTargets = 3;

    /// <summary>
    /// « Orage » / « Tempête » : à l'attaque, la foudre frappe jusqu'à <see cref="StormMaxTargets"/> ennemis du
    /// porteur TIRÉS AU HASARD (parmi tous, SAUF la cible directe <paramref name="target"/>), chacun pour un
    /// dégât FIXE (<paramref name="amount"/>, ni réduit par Rempart/couvert ni majoré par les traits offensifs —
    /// mais Esquive/Bouclier divin s'appliquent via <see cref="ApplyDamage"/>). Tirage via <see cref="_rng"/>.
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
            ApplyDamage(victims[i], u, amount, attacker);
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
        ApplyDamage(behind, u, EffectiveDamage(attacker, from, u, behind), attacker);
        RemoveDeadAt(behind, attacker);
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
            ApplyDamage(movedTo, mover, EffectiveDamage(unit, cell, mover, movedTo), unit);
            if (!mover.IsAlive)
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
        if (killer != null && dead.Faction != killer.Faction)
            killer.RecordKill();
        _units[cell.Column, cell.Row] = null;
    }

    /// <summary>Alliés BLESSÉS à portée qu'un soigneur (trait « Soin ») peut cibler.</summary>
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
        if (unit == null || !unit.HasTrait(Trait.Soin))
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

    /// <summary>« Soin » : soigne un allié ciblé (montant = MOITIÉ de la puissance du soigneur, arrondie
    /// vers le bas). Fonctionne pour n'importe quel porteur du trait, commandant compris. Passe le tour.</summary>
    public MoveKind TryHeal(Cell from, Cell target)
    {
        var unit = ActiveUnitAt(from);
        if (unit == null || !HealTargets(from).Contains(target))
            return MoveKind.Invalid;

        UnitAt(target)!.Heal(unit.Damage / 2);
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
