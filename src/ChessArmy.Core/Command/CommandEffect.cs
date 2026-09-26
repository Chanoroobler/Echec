using ChessArmy.Core.Battle;
using ChessArmy.Core.Equip;

namespace ChessArmy.Core.Command;

/// <summary>
/// Nature d'un <see cref="CommandEffect"/> : ce que le nœud d'arbre modifie une fois acheté.
/// </summary>
public enum CommandEffectKind
{
    /// <summary>Bonus de STAT sur le commandant seul.</summary>
    CommanderStat,

    /// <summary>Octroi d'un TRAIT de combat au commandant.</summary>
    CommanderTrait,

    /// <summary>Bonus de STAT sur toutes les unités NON essentielles (commandant exclu).</summary>
    UnitStat,

    /// <summary>Octroi d'un TRAIT de combat à toutes les unités non essentielles.</summary>
    UnitTrait,

    /// <summary>Agrandit la réserve (plafond de pions hors commandant).</summary>
    ReserveSlots,

    /// <summary>Agrandit le déploiement (pions posables sur le plateau, commandant compris).</summary>
    DeploySlots,

    /// <summary>Chaque fusion recrute EN PLUS une unité tier 1 déjà découverte (ou un domaine PRÉCIS, cf. <see cref="CommandEffect.Domaine"/>).</summary>
    FusionRecruit,

    /// <summary>Chaque unité de tier 2+ tombée au combat fait arriver en réserve une unité tier 1 (déjà vue, ou un domaine PRÉCIS).</summary>
    EliteDeathRecruit,

    /// <summary>Réduit d'<see cref="CommandEffect.Amount"/> le nombre de pions requis pour une fusion (jamais moins de 2).
    /// Optionnellement restreint à un <see cref="CommandEffect.Domaine"/> (sinon toutes les classes).</summary>
    FusionSizeReduction,

    /// <summary>Augmente d'<see cref="CommandEffect.Amount"/> la réduction de dégâts du trait « Rempart » (moteur : base 4).</summary>
    RempartBonus,

    /// <summary>Augmente d'<see cref="CommandEffect.Amount"/> le bonus de dégâts du trait « Tueur de géants » (moteur : base 5).</summary>
    TueurDeGeantsBonus,

    /// <summary>Augmente d'<see cref="CommandEffect.Amount"/> la puissance par allié adjacent du trait « Formation » (moteur : base 2).</summary>
    FormationBonus,

    /// <summary>Augmente d'<see cref="CommandEffect.Amount"/> les dégâts du trait « Impact » (moteur : base 5).</summary>
    ImpactBonus,

    /// <summary>
    /// Une unité du <see cref="CommandEffect.Domaine"/> visé qui TUE par son attaque rend la main au joueur :
    /// le tour ne passe pas. UNE seule fois entre deux tours ennemis (cf. <see cref="Battle.Match.ExtraTurnDomaine"/>).
    /// </summary>
    ExtraTurnOnKill,

    /// <summary>Donne <see cref="CommandEffect.Amount"/> slot(s) d'équipement au COMMANDANT (0 sans nœud : il ne s'équipe pas).</summary>
    CommanderEquipSlots,

    /// <summary>Donne <see cref="CommandEffect.Amount"/> slot(s) d'équipement EN PLUS à chaque pion (base 1).</summary>
    UnitEquipSlots,

    /// <summary>Chaque coffre donne <see cref="CommandEffect.Amount"/> équipement(s) EN PLUS (tirage complet et indépendant).</summary>
    ChestExtraItem,

    /// <summary>Ajoute <see cref="CommandEffect.Amount"/> POINTS de % aux chances RARE et LÉGENDAIRE d'un coffre.</summary>
    ChestRarityBonus,

    /// <summary>Chaque RELANCE d'unité rapporte <see cref="CommandEffect.Amount"/> équipement(s) (tirés comme un coffre).</summary>
    RerollEquipment,

    /// <summary>La tuile RECRUE du terrain donne <see cref="CommandEffect.Amount"/> pion(s) EN PLUS (tirage indépendant).</summary>
    RecruitExtraUnit,

    /// <summary>
    /// RECYCLER un équipement fait arriver en réserve <see cref="CommandEffect.Amount"/> pion(s) tier 1 : celui
    /// dont on a DÉJÀ le plus d'exemplaires (cf. <see cref="Campaign.Run.MostOwnedTier1"/>) — donc celui qui
    /// rapproche d'une fusion — ou la classe de base d'un <see cref="CommandEffect.Domaine"/> précis s'il est fixé.
    /// </summary>
    RecycleRecruit,

    // ── Commandant DUO (Basile + l'artisan meurtrier) ─────────────────────────────────────────────

    /// <summary>Bonus de STAT sur le SECOND meneur seulement (cf. <see cref="Battle.CommandeDef.CompanionId"/>).</summary>
    CompanionStat,

    /// <summary>Octroi d'un TRAIT de combat au SECOND meneur seulement.</summary>
    CompanionTrait,

    /// <summary>Les TUILES RECRUE des maps deviennent des TROUSSES DE SOIN (sans ce nœud, elles sont retirées).</summary>
    HealKitTiles,

    /// <summary>Les COFFRES des maps deviennent des SACOCHES (sans ce nœud, ils sont retirés).</summary>
    SatchelChests,

    /// <summary>
    /// Le COMMANDANT peut lui aussi puiser dans une sacoche — mais l'objet à lancer part chez son COMPAGNON,
    /// seul capable de s'en servir. Sans ce nœud, une sacoche ne répond qu'au compagnon lui-même.
    /// </summary>
    SatchelForCompanion,

    /// <summary>
    /// « Sacoche aimantée » : le COMPAGNON peut TIRER sur une sacoche à portée d'attaque pour en faire venir
    /// un objet jusqu'à lui — il n'a plus à marcher dessus. Un ennemi posté sur la sacoche en fait une cible
    /// d'attaque ordinaire : le tir le frappe, et rien n'est ramassé.
    /// </summary>
    SatchelPull,

    /// <summary>Chaque mise à mort du COMMANDANT fait apparaître une trousse de soin sur une case libre.</summary>
    HealKitOnKill,

    /// <summary>+<see cref="CommandEffect.Amount"/> PV max (définitif, sur la run) par TROUSSE DE SOIN utilisée.</summary>
    HealKitMaxHp,

    /// <summary>+<see cref="CommandEffect.Amount"/> PV max (définitif, sur la run) par OBJET pris dans une sacoche.</summary>
    SatchelMaxHp,

    /// <summary>« Roque » : les deux meneurs ÉCHANGENT leurs places quand l'un se déplace sur l'autre.</summary>
    Roque,

    /// <summary>
    /// « La puissance du rock » : chaque ROQUE met +<see cref="CommandEffect.Amount"/> de puissance en réserve
    /// sur l'ARTISAN, dépensée par sa prochaine attaque. NON cumulable (cf. <see cref="Battle.Unit.RoquePower"/>).
    /// Sans le nœud « Roque » de la même branche, il n'y a jamais de roque : l'effet dort.
    /// </summary>
    RoquePower,

    /// <summary>
    /// « Continue sans moi » : la chute d'UN meneur ne perd plus la run. Le survivant poursuit SEUL et hérite
    /// de la MOITIÉ de la puissance et des PV max du tombé (cf. <see cref="Campaign.Run.SoloSurvivor"/>).
    /// </summary>
    SoloSurvivor,

    /// <summary>
    /// Le COMMANDANT gagne <see cref="CommandEffect.Amount"/> de puissance par TRANCHE DE
    /// <see cref="CrossKillStep"/> mises à mort de son COMPAGNON (compteur à vie, cf. <see cref="Battle.Unit.Kills"/>).
    /// Sens unique : le compagnon ne gagne rien des kills du commandant.
    /// </summary>
    CrossKillPower,

    // ── Commandant BRUTE ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// +<see cref="CommandEffect.Amount"/> PV max au COMMANDANT par unité alliée tombée depuis le début de la
    /// run (cf. <see cref="Campaign.Run.AllyDeaths"/>). Définitif : le compteur ne redescend jamais.
    /// </summary>
    AllyDeathMaxHp,

    /// <summary>
    /// +<see cref="CommandEffect.Amount"/> de puissance au COMMANDANT par TRANCHE DE
    /// <see cref="AllyDeathStep"/> unités alliées tombées sur la run (cf. <see cref="Campaign.Run.AllyDeaths"/>).
    /// </summary>
    AllyDeathPower,

    /// <summary>
    /// Recrute LA classe EXCLUSIVE nommée par <see cref="CommandEffect.Asset"/> (cf.
    /// <see cref="Battle.ExclusiveClasses"/>) : le Paysan de la Brute. Un seul exemplaire — le nœud ne
    /// redonne rien si le pion est déjà dans le roster.
    /// </summary>
    RecruitExclusive,

    /// <summary>
    /// Fait ÉVOLUER le pion exclusif : à l'achat, le joueur CHOISIT entre les assets de
    /// <see cref="CommandEffect.Choices"/> (deux évolutions, deux sprites), et le pion se transforme EN PLACE
    /// en gardant équipements, mises à mort et paliers de « Survivant ». C'est le seul type d'effet qui exige
    /// un choix au moment de l'achat (cf. <see cref="Campaign.Run.Unlock"/>).
    /// </summary>
    EvolveExclusive,

    /// <summary>
    /// Au lancement d'une mission, si la RÉSERVE compte STRICTEMENT PLUS de <see cref="CommandEffect.Amount"/>
    /// pions, recrute la classe de base du <see cref="CommandEffect.Domaine"/> visé. Rien si la réserve est
    /// déjà au plafond : la recrue est perdue.
    /// </summary>
    ReserveThresholdRecruit,

    /// <summary>
    /// « Révolte » : à l'achat, le pion EXCLUSIF (<see cref="CommandEffect.Asset"/>) prend le commandement —
    /// il devient l'unité essentielle avec ses propres stats, tandis que le commandant redevient un pion
    /// ORDINAIRE (mortel, mais qui garde tous les bonus de sa branche, cf.
    /// <see cref="Campaign.UnitSpec.CommanderBody"/>).
    /// </summary>
    Revolte,

    /// <summary>
    /// Octroi d'un TRAIT de combat au seul pion EXCLUSIF (le Paysan), sans toucher au reste de l'armée.
    /// </summary>
    ExclusiveTrait,
}

/// <summary>
/// Comment l'<see cref="CommandEffect.Amount"/> d'un effet de STAT est mis à l'échelle.
/// </summary>
public enum CommandScale
{
    /// <summary>Bonus plat : <c>Amount</c> tel quel.</summary>
    Flat,

    /// <summary>
    /// Bonus × le nombre de PAIRES DE CLASSES DISTINCTES du roster (hors commandant) : classes
    /// distinctes ÷ 2, arrondi vers le bas. Recalculé à chaque phase de placement — récompense la
    /// variété de l'armée. Cf. <see cref="Campaign.Run.DistinctPairs"/>.
    /// </summary>
    PerDistinctPair,

    /// <summary>
    /// Bonus × le nombre d'unités du <see cref="CommandEffect.Domaine"/> visé dans le roster (hors
    /// commandant, réserve ET pions déployés). Recalculé à chaque phase de placement. Cf.
    /// <see cref="Campaign.Run.DomaineUnitCount"/>.
    /// </summary>
    PerDomaineUnit,

    /// <summary>
    /// Bonus × le nombre d'unités du <see cref="CommandEffect.Domaine"/> visé réellement DÉPLOYÉES sur le
    /// plateau (la réserve ne compte pas). Dépend donc du plateau et non du roster : c'est la scène qui
    /// fournit le compte au moment où les pions sont instanciés (lancement du combat), et il est FIGÉ pour
    /// tout le combat. Sans compteur fourni → 0.
    /// </summary>
    PerDeployedDomaineUnit,

    /// <summary>
    /// Bonus × le nombre d'équipements POSSÉDÉS : ceux posés sur une unité de l'armée (commandant compris) ET
    /// ceux qui dorment en inventaire (cf. <see cref="Campaign.Run.EquippedItemCount"/>). Recalculé à chaque
    /// phase de placement — c'est le STOCK qui fait monter le bonus, pas le fait de l'équiper. Thème du
    /// commandant MARCHAND.
    /// </summary>
    PerEquippedItem,

    /// <summary>
    /// Bonus × le nombre d'équipements que la cible porte ELLE-MÊME. À ne pas confondre avec
    /// <see cref="PerEquippedItem"/>, qui compte tout le stock de l'armée : ici seul l'objet posé sur CE pion
    /// compte. Réservé de fait au commandant MARCHAND — c'est le seul à avoir des emplacements (cf.
    /// <see cref="CommandEffectKind.CommanderEquipSlots"/>), les autres portent toujours 0 et le bonus vaut 0.
    /// </summary>
    PerOwnEquippedItem,
}

/// <summary>
/// UN effet d'un nœud d'arbre de commandement. Modelé sur <see cref="EquipEffect"/> : soit un bonus de
/// STAT (avec une éventuelle mise à l'échelle, cf. <see cref="Scale"/>), soit l'octroi d'un TRAIT, soit
/// un effet de MÉTA (slots de réserve/déploiement, bonus de fusion) qui ne touche aucune unité mais la
/// <see cref="Campaign.Run"/>. La stat visée réutilise <see cref="EquipStat"/> : mêmes noms côté JSON
/// que pour les équipements (<c>hp</c>, <c>damage</c>, <c>moveRange</c>, <c>attackRange</c>).
/// </summary>
public sealed class CommandEffect
{
    private CommandEffect(CommandEffectKind kind, EquipStat stat, int amount, string? trait, CommandScale scale,
        Domaine? domaine, int? tier = null, string? asset = null, IReadOnlyList<string>? choices = null)
    {
        Kind = kind;
        Stat = stat;
        Amount = amount;
        Trait = trait;
        Scale = scale;
        Domaine = domaine;
        Tier = tier;
        Asset = asset;
        Choices = choices ?? System.Array.Empty<string>();
    }

    public CommandEffectKind Kind { get; }

    /// <summary>Stat visée (effets <see cref="CommandEffectKind.CommanderStat"/> / <see cref="CommandEffectKind.UnitStat"/>).</summary>
    public EquipStat Stat { get; }

    /// <summary>Bonus de base, avant mise à l'échelle (ou nombre de slots / de recrues).</summary>
    public int Amount { get; }

    /// <summary>Nom canonique du trait octroyé (cf. <c>Battle.Trait</c>), pour les effets de trait.</summary>
    public string? Trait { get; }

    /// <summary>Mise à l'échelle du bonus de stat (plat par défaut).</summary>
    public CommandScale Scale { get; }

    /// <summary>
    /// Domaine CIBLÉ, optionnel (null = tous). Pour un effet d'UNITÉ (<see cref="CommandEffectKind.UnitStat"/> /
    /// <see cref="CommandEffectKind.UnitTrait"/>), restreint le bonus aux seules unités de ce domaine. Pour un
    /// effet de RECRUE (<see cref="CommandEffectKind.FusionRecruit"/> / <see cref="CommandEffectKind.EliteDeathRecruit"/>),
    /// désigne la classe de base recrutée (au lieu d'un tier 1 déjà vu au hasard). Pour l'échelle
    /// <see cref="CommandScale.PerDomaineUnit"/>, désigne le domaine dont on compte les unités.
    /// </summary>
    public Domaine? Domaine { get; }

    /// <summary>
    /// TIER ciblé, optionnel (null = tous). Sur un effet d'UNITÉ, restreint le bonus aux pions de ce tier :
    /// c'est ce qui permet à « Révolte » de ne relever que la piétaille (tier 1).
    /// </summary>
    public int? Tier { get; }

    /// <summary>
    /// ASSET d'une classe EXCLUSIVE visée (cf. <see cref="Battle.ExclusiveClasses"/>) : le pion recruté par
    /// <see cref="CommandEffectKind.RecruitExclusive"/>, ou promu par <see cref="CommandEffectKind.Revolte"/>.
    /// </summary>
    public string? Asset { get; }

    /// <summary>
    /// OPTIONS FIGÉES d'un nœud à choix (<see cref="CommandEffectKind.EvolveExclusive"/>). Le cas normal est
    /// de laisser cette liste VIDE : les options sont alors les évolutions de la classe que le pion exclusif
    /// porte AU MOMENT DE L'ACHAT (cf. <see cref="Campaign.Run.EvolutionChoices"/>), ce qui enchaîne
    /// naturellement les deux nœuds — « armement » ouvre sur archer/épéiste, puis « protection » sur les
    /// feuilles de la branche retenue. À ne renseigner que pour forcer une liste hors de cet arbre.
    /// </summary>
    public IReadOnlyList<string> Choices { get; }

    /// <summary>Nombre de mises à mort du COMPAGNON par palier de <see cref="CommandEffectKind.CrossKillPower"/>.</summary>
    public const int CrossKillStep = 3;

    /// <summary>Nombre de pertes alliées par palier de <see cref="CommandEffectKind.AllyDeathPower"/>.</summary>
    public const int AllyDeathStep = 3;

    /// <summary>Vrai si l'achat de ce nœud exige un CHOIX du joueur (cf. <see cref="Choices"/>).</summary>
    public bool NeedsChoice => Kind == CommandEffectKind.EvolveExclusive;

    /// <summary>Vrai si l'effet vise le COMMANDANT (stat ou trait). Un DUO : le commandant SEUL, pas son compagnon.</summary>
    public bool TargetsCommander => Kind is CommandEffectKind.CommanderStat or CommandEffectKind.CommanderTrait;

    /// <summary>Vrai si l'effet vise le SECOND meneur d'un DUO (stat ou trait).</summary>
    public bool TargetsCompanion => Kind is CommandEffectKind.CompanionStat or CommandEffectKind.CompanionTrait;

    /// <summary>Vrai si l'effet vise les unités NON essentielles (stat ou trait).</summary>
    public bool TargetsUnits => Kind is CommandEffectKind.UnitStat or CommandEffectKind.UnitTrait;

    /// <summary>
    /// Valeur effective du bonus, connaissant le nombre de paires de classes distinctes du roster et — pour
    /// l'échelle <see cref="CommandScale.PerDomaineUnit"/> — le nombre d'unités du domaine visé
    /// (<paramref name="domaineCount"/> évalué sur <see cref="Domaine"/>). Absent → l'échelle par domaine vaut 0.
    /// </summary>
    public int AmountFor(int distinctPairs, System.Func<Domaine, int>? domaineCount = null,
        System.Func<Domaine, int>? deployedCount = null, int equippedItems = 0, int ownEquippedItems = 0) =>
        Scale switch
        {
            CommandScale.PerDistinctPair => Amount * distinctPairs,
            CommandScale.PerEquippedItem => Amount * equippedItems,
            CommandScale.PerOwnEquippedItem => Amount * ownEquippedItems,
            CommandScale.PerDomaineUnit => Domaine is { } d ? Amount * (domaineCount?.Invoke(d) ?? 0) : Amount,
            CommandScale.PerDeployedDomaineUnit => Domaine is { } dd ? Amount * (deployedCount?.Invoke(dd) ?? 0) : Amount,
            _ => Amount,
        };

    public static CommandEffect CommanderStat(EquipStat stat, int amount, CommandScale scale = CommandScale.Flat,
        Domaine? domaine = null) =>
        new(CommandEffectKind.CommanderStat, stat, amount, null, scale, domaine);

    public static CommandEffect CommanderTrait(string trait) =>
        new(CommandEffectKind.CommanderTrait, default, 0, trait, CommandScale.Flat, null);

    public static CommandEffect UnitStat(EquipStat stat, int amount, CommandScale scale = CommandScale.Flat,
        Domaine? domaine = null, int? tier = null) =>
        new(CommandEffectKind.UnitStat, stat, amount, null, scale, domaine, tier);

    public static CommandEffect UnitTrait(string trait, Domaine? domaine = null) =>
        new(CommandEffectKind.UnitTrait, default, 0, trait, CommandScale.Flat, domaine);

    public static CommandEffect ReserveSlots(int amount) =>
        new(CommandEffectKind.ReserveSlots, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect DeploySlots(int amount) =>
        new(CommandEffectKind.DeploySlots, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect FusionRecruit(int amount = 1, Domaine? domaine = null) =>
        new(CommandEffectKind.FusionRecruit, default, amount, null, CommandScale.Flat, domaine);

    public static CommandEffect EliteDeathRecruit(int amount = 1, Domaine? domaine = null) =>
        new(CommandEffectKind.EliteDeathRecruit, default, amount, null, CommandScale.Flat, domaine);

    public static CommandEffect FusionSizeReduction(int amount = 1, Domaine? domaine = null) =>
        new(CommandEffectKind.FusionSizeReduction, default, amount, null, CommandScale.Flat, domaine);

    public static CommandEffect RempartBonus(int amount) =>
        new(CommandEffectKind.RempartBonus, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect TueurDeGeantsBonus(int amount) =>
        new(CommandEffectKind.TueurDeGeantsBonus, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect FormationBonus(int amount) =>
        new(CommandEffectKind.FormationBonus, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect ImpactBonus(int amount) =>
        new(CommandEffectKind.ImpactBonus, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect ExtraTurnOnKill(Domaine? domaine = null) =>
        new(CommandEffectKind.ExtraTurnOnKill, default, 1, null, CommandScale.Flat, domaine);

    public static CommandEffect CommanderEquipSlots(int amount = 1) =>
        new(CommandEffectKind.CommanderEquipSlots, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect UnitEquipSlots(int amount = 1) =>
        new(CommandEffectKind.UnitEquipSlots, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect ChestExtraItem(int amount = 1) =>
        new(CommandEffectKind.ChestExtraItem, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect ChestRarityBonus(int amount) =>
        new(CommandEffectKind.ChestRarityBonus, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect RerollEquipment(int amount = 1) =>
        new(CommandEffectKind.RerollEquipment, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect RecruitExtraUnit(int amount = 1) =>
        new(CommandEffectKind.RecruitExtraUnit, default, amount, null, CommandScale.Flat, null);

    public static CommandEffect RecycleRecruit(int amount = 1, Domaine? domaine = null) =>
        new(CommandEffectKind.RecycleRecruit, default, amount, null, CommandScale.Flat, domaine);

    public static CommandEffect CompanionStat(EquipStat stat, int amount, CommandScale scale = CommandScale.Flat) =>
        new(CommandEffectKind.CompanionStat, stat, amount, null, scale, null);

    public static CommandEffect CompanionTrait(string trait) =>
        new(CommandEffectKind.CompanionTrait, default, 0, trait, CommandScale.Flat, null);

    /// <summary>Effet de DUO sans valeur (sa seule présence compte) : trousses, sacoches, roque…</summary>
    public static CommandEffect Flag(CommandEffectKind kind, int amount = 1) =>
        new(kind, default, amount, null, CommandScale.Flat, null);

    /// <summary>Recrue d'une classe EXCLUSIVE (le Paysan), en un seul exemplaire.</summary>
    public static CommandEffect RecruitExclusive(string asset) =>
        new(CommandEffectKind.RecruitExclusive, default, 1, null, CommandScale.Flat, null, null, asset);

    /// <summary>Évolution du pion exclusif, au CHOIX entre deux assets (cf. <see cref="NeedsChoice"/>).</summary>
    public static CommandEffect EvolveExclusive(IReadOnlyList<string> choices) =>
        new(CommandEffectKind.EvolveExclusive, default, 1, null, CommandScale.Flat, null, null, null, choices);

    /// <summary>Recrue conditionnée par la taille de la réserve (seuil = <paramref name="threshold"/>).</summary>
    public static CommandEffect ReserveThresholdRecruit(int threshold, Domaine domaine) =>
        new(CommandEffectKind.ReserveThresholdRecruit, default, threshold, null, CommandScale.Flat, domaine);

    /// <summary>« Révolte » : le pion exclusif nommé prend le commandement.</summary>
    public static CommandEffect Revolte(string asset) =>
        new(CommandEffectKind.Revolte, default, 1, null, CommandScale.Flat, null, null, asset);

    /// <summary>Trait de combat octroyé au seul pion EXCLUSIF.</summary>
    public static CommandEffect ExclusiveTrait(string trait) =>
        new(CommandEffectKind.ExclusiveTrait, default, 0, trait, CommandScale.Flat, null);
}
