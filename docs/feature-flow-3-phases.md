# Feature — Structure de run en 3 phases

> Doc de design + prompt d'implémentation, à donner tel quel à un assistant de code.
> Cible : `src/Echec.Core/Campaign/Run.cs`, `RunSave.cs`, `src/Echec.Core/Map/CombatType.cs`,
> `src/Echec.Game/Scenes/GameplayScene.cs`, tests `tests/Echec.Core.Tests`.

## 1. Objectif

Remplacer la boucle plate actuelle (`TotalCombats = 6`, un seul boss en fin de run) par une
structure **rythmée en 3 phases**. Chaque phase enchaîne des escarmouches, une mission spéciale et
une mission boss. Une run = 3 phases. On veut piloter précisément **le nombre de pions ennemis** et
**leur force (tier)** selon la phase et la mission courante.

## 2. Terminologie

- **Run** — une partie complète = **3 phases**.
- **Phase** — un bloc de **6 missions** joué dans un ordre fixe (le « rythme »).
- **Mission** — un combat individuel. Trois natures : `Escarmouche`, `Speciale`, `Boss`.
- **Tier** — la force d'un pion, portée par sa `UnitClass` (`UnitClass.Tier`) : arbre à 3 tiers
  (base T1 → 2 branches T2 → 2 feuilles T3).

> ⚠️ **Collision de nommage à connaître.** Il existe déjà un enum `RunPhase`
> (`Placement / Battle / Recruitment / Victory / Defeat`) : c'est le **cycle interne d'un combat**,
> un axe DIFFÉRENT de la « phase » décrite ici (le bloc de 6 missions). Pour éviter toute confusion,
> le nouveau concept n'utilise **pas** le nom `RunPhase`. On introduit `PhaseIndex` (1..3),
> `MissionInPhase` (1..6) et `MissionKind`. Les deux axes coexistent sans se marcher dessus.

## 3. Rythme d'une phase

Chaque phase est composée de 6 missions, toujours dans cet ordre :

| Slot | Nature | Statut actuel |
|------|--------|---------------|
| 1 | Escarmouche | ✅ |
| 2 | Escarmouche | ✅ |
| 3 | **Mission spéciale** | ⏳ pas encore de contenu → **traitée comme une escarmouche** pour l'instant (mais typée `Speciale` pour brancher le futur) |
| 4 | Escarmouche | ✅ |
| 5 | Escarmouche | ✅ |
| 6 | **Boss** | ✅ |

Une run = phase 1 → phase 2 → phase 3, soit **18 missions** et **3 boss**. Le boss de la **phase 3
est le boss final** (seul à déclencher la victoire de la run).

Numéro de combat global : `CombatNumber = (PhaseIndex − 1) × 6 + MissionInPhase`, de 1 à 18.

## 4. Composition ennemie (nombre + tier) — tableau maître

Source de vérité. Chaque cellule liste le nombre de pions et leur répartition par tier. Pour un
boss, on compte **le boss + ses escortes**. Les domaines (Dame/Tour/Cavalier/Fou) restent tirés dans
le pool débloqué comme aujourd'hui ; ce tableau ne fixe **que l'effectif et le tier**.

### Phase 1 — Apprentissage (T1, le T2 apparaît en fin de phase)

| Slot | Mission | Effectif | Composition |
|------|---------|----------|-------------|
| 1 | Escarmouche | 2 | 2× T1 |
| 2 | Escarmouche | 3 | 3× T1 |
| 3 | Spéciale (→ escarmouche) | 4 | 4× T1 |
| 4 | Escarmouche | 6 | 5× T1 + **1× T2** |
| 5 | Escarmouche | 7 | 5× T1 + 2× T2 |
| 6 | Boss | **Boss + 9** | Boss + 7× T1 + 2× T2 |

> Démarrage adouci (2026-07-02) : escarmouches 1-2 à 2 puis 3 pions ; spéciale phase 1 à 4 pions en
> attendant son vrai contenu (le reste inchangé).

### Phase 2 — Montée en puissance (T2 dominant)

| Slot | Mission | Effectif | Composition |
|------|---------|----------|-------------|
| 1 | Escarmouche | 7 | 4× T1 + 3× T2 |
| 2 | Escarmouche | 8 | 4× T1 + 4× T2 |
| 3 | Spéciale (→ escarmouche) | 9 | 3× T1 + 6× T2 |
| 4 | Escarmouche | 9 | 3× T1 + 6× T2 |
| 5 | Escarmouche | 10 | 2× T1 + 8× T2 |
| 6 | Boss | Boss + 10 | Boss + 3× T1 + 7× T2 |

### Phase 3 — Fin de run (T2 → T3)

| Slot | Mission | Effectif | Composition |
|------|---------|----------|-------------|
| 1 | Escarmouche | 8 | 5× T2 + 3× T3 |
| 2 | Escarmouche | 9 | 5× T2 + 4× T3 |
| 3 | Spéciale (→ escarmouche) | 10 | 4× T2 + 6× T3 |
| 4 | Escarmouche | 10 | 4× T2 + 6× T3 |
| 5 | Escarmouche | 11 | 3× T2 + 8× T3 |
| 6 | **Boss final** | **Boss + 12** | Boss + 4× T2 + 8× T3 |

**Lecture de la courbe.** L'effectif monte de 3 à ~12 pions, le tier moyen glisse de T1 à T3 phase
après phase. Le **T2 s'invite dès l'escarmouche 4 de la phase 1** (1 seul, puis 2), devient la norme
en phase 2, et le T3 prend le relais en phase 3. Le pic (mission spéciale + boss) est le sommet de
chaque phase.

> **Note plateau.** Avec ces effectifs, pense à vérifier la taille des maps par phase (§9) : ~9 à 13
> ennemis + l'armée du joueur doivent tenir sans saturer. Repère indicatif : phase 1 → 6×6 peut
> devenir un peu juste au boss (9 ennemis), envisager 7×7 dès le boss de phase 1 ; phase 3 → 8×8
> minimum, voire plus grand pour le boss final (13 ennemis).

### Modèle génératif optionnel (« budget de menace »)

Si tu préfères une formule plutôt qu'une table figée, le tableau ci-dessus équivaut à un budget où
**coût d'un pion = son tier** (T1=1, T2=2, T3=3). Le budget total par mission croît de façon
monotone. Garde toutefois la **table comme source de vérité** : c'est plus lisible et plus facile à
équilibrer à la main. Le budget n'est qu'une lecture de secours pour extrapoler de futures missions.

## 5. Modèle de données

Dans `src/Echec.Core/Map/CombatType.cs`, étendre l'enum (déjà `Escarmouche`, `Boss`) :

```csharp
public enum CombatType
{
    Escarmouche, // tuer toutes les unités ennemies
    Speciale,    // mission spéciale (contenu à venir ; générée comme une escarmouche pour l'instant)
    Boss,        // tuer le boss
}
```

Dans `Run.cs`, remplacer les constantes de progression :

```csharp
public const int PhaseCount = 3;
public const int MissionsPerPhase = 6;
public const int TotalCombats = PhaseCount * MissionsPerPhase; // 18

// Rythme d'une phase (6 slots). La spéciale est présente dès maintenant mais générée comme une escarmouche.
private static readonly CombatType[] PhaseLayout =
{
    CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Speciale,
    CombatType.Escarmouche, CombatType.Escarmouche, CombatType.Boss,
};

public int PhaseIndex     => (CombatNumber - 1) / MissionsPerPhase + 1; // 1..3
public int MissionInPhase => (CombatNumber - 1) % MissionsPerPhase + 1; // 1..6
public CombatType CurrentMission => PhaseLayout[MissionInPhase - 1];

public bool IsBossCombat => CurrentMission == CombatType.Boss;   // remplace l'ancien `CombatNumber == TotalCombats`
public bool IsFinalBoss  => IsBossCombat && PhaseIndex == PhaseCount;
```

## 6. Génération de la vague — `BuildEnemyWave`

Réécrire `BuildEnemyWave()` pour lire l'effectif et la composition de tier depuis la table du §4,
indexée par `(PhaseIndex, MissionInPhase)`.

Algorithme cible :

1. Récupérer la ligne `(count, tierComposition)` correspondant à `(PhaseIndex, MissionInPhase)`.
   Stocker ça dans une table statique `Composition[PhaseIndex-1][MissionInPhase-1]` (18 entrées),
   chaque entrée = liste de tiers requis, ex. Phase 2 slot 5 → `[1,2,2,2,2]`.
2. Pour chaque tier requis : tirer un **domaine** dans le pool débloqué (garder la logique
   d'`IntroOrder` / déblocage progressif existante, ou la simplifier), puis choisir une `UnitClass`
   **de ce tier** dans l'arbre du domaine.
3. Pour une mission `Boss` : ajouter d'abord `Commandes.Boss`, puis les escortes selon la compo.
4. Conserver le RNG déterministe existant (`CombatRng`) pour que « Continuer » rejoue la même vague.
5. Mélanger la vague (`Shuffle`) pour ne pas figer l'ordre.

Helper à ajouter (dans `Domaines` ou `Run`) pour choisir une classe d'un tier donné :

```csharp
// Toutes les UnitClass d'un domaine dont Tier == tier (parcours récursif de l'arbre).
public static IReadOnlyList<UnitClass> ClassesAtTier(Domaine d, int tier) { /* DFS sur BaseClass + Evolutions */ }
```

> **Boss par phase.** Il n'existe aujourd'hui qu'un `Commandes.Boss`. Deux options :
> (a) réutiliser ce même boss pour les 3 missions boss, en ne faisant varier que les escortes (le
> plus simple, recommandé pour la V1) ; (b) prévoir 3 défs de boss distinctes plus tard. Laisser un
> `TODO` pour (b).

## 7. Progression de la run — `CompleteCombat` / `Recruit`

- **Victoire de la run** uniquement sur `IsFinalBoss` (phase 3, slot 6). Aujourd'hui le code passe en
  `Victory` dès `IsBossCombat` : remplacer la condition par `IsFinalBoss`.
- Les **boss de phase 1 et 2** ne terminent pas la run : ils enchaînent normalement vers le
  recrutement, comme une escarmouche gagnée (option future : récompense renforcée après un boss).
- `Recruit` incrémente `CombatNumber` de 1 (1 → 18) ; `PhaseIndex` / `MissionInPhase` en dérivent
  automatiquement, aucun champ supplémentaire à sauvegarder.

## 8. Persistance — `RunSave.cs`

`CombatNumber` (désormais 1..18) reste le seul curseur de progression : **aucun champ à ajouter**.
Bumper `Version` (1 → 2) et invalider/migrer proprement les vieilles sauvegardes dont
`CombatNumber` visait l'ancienne échelle (1..6).

## 9. Rendu / scène — `GameplayScene.cs`

- **MapForCombat** (l.446) : la taille dépend aujourd'hui de `CombatNumber` (1-2→6×6, 3-4→7×7).
  Généraliser par `PhaseIndex` (ex. phase 1 → 6×6, phase 2 → 7×7, phase 3 → 8×8) et **filtrer les
  maps par `CurrentMission`** (une mission `Boss` doit piocher `CombatType.Boss`, une `Speciale`
  retombe sur `Escarmouche` tant qu'il n'y a pas de map dédiée).
- **Bandeau de combat** (l.5369) : remplacer `combat.number, CombatNumber, TotalCombats` par un
  libellé de phase, ex. « Phase {PhaseIndex}/3 — {nom de la mission} ». Ajouter les clés de
  localisation (`combat.phase`, `mission.escarmouche`, `mission.speciale`, `mission.boss`).
- **Musique** (l.1162) : `IsBossCombat` déclenche déjà la musique de boss ; envisager une piste
  distincte pour `IsFinalBoss`.
- **Méta-progression** (`UnlockedTier1Assets`, `RollSeenTier1`) : adapter au calendrier 18 missions
  (ou conserver la découverte par domaine, indépendante du barème de tier).

## 10. Critères d'acceptation

- Une run compte exactement 18 missions réparties en 3 phases de 6, dans l'ordre
  `Escarmouche, Escarmouche, Speciale, Escarmouche, Escarmouche, Boss`.
- `BuildEnemyWave` produit, pour chaque `(PhaseIndex, MissionInPhase)`, **l'effectif et la
  répartition de tier exacts** du tableau du §4.
- Un pion `Boss` (`Commandes.Boss`) n'apparaît **que** sur les missions boss ; le boss **final**
  n'apparaît qu'en phase 3.
- La victoire de run ne se déclenche que sur le boss final ; les boss 1 et 2 mènent au recrutement.
- « Continuer » (reprise de sauvegarde) rejoue la même vague et le même terrain (déterminisme
  conservé).

## 11. Tests (`tests/Echec.Core.Tests`)

- `BuildEnemyWave_Effectif_Et_Tiers(phase, mission)` : théorie couvrant les 18 missions, assert
  effectif total + comptes par tier == table du §4.
- `BossUniquementSurMissionBoss` : aucun boss hors slot 6 ; boss final seulement en phase 3.
- `VictoireSeulementSurBossFinal` : `CompleteCombat` en phase 1/2 boss → `Recruitment` ; en phase 3
  boss → `Victory`.
- `PhaseIndex_MissionInPhase_Mapping` : pour `CombatNumber` 1..18, `PhaseIndex`/`MissionInPhase`
  cohérents.
- `Determinisme` : deux `Run` de même `Seed` produisent des vagues identiques par mission.

---

## 12. Prompt prêt à coller

> **Contexte** : jeu tactique C#/.NET 9 (`Echec`), roguelite au tour par tour. La progression d'une
> run vit dans `src/Echec.Core/Campaign/Run.cs` (C# pur, testé). Les pions ont un `Tier` (1→3) porté
> par `UnitClass`. Il existe un enum `CombatType { Escarmouche, Boss }` et un enum SANS RAPPORT
> `RunPhase { Placement, Battle, ... }` (cycle interne d'un combat — **ne pas** le réutiliser pour ce
> travail).
>
> **Tâche** : restructurer la run en **3 phases de 6 missions** (rythme fixe
> `Escarmouche, Escarmouche, Speciale, Escarmouche, Escarmouche, Boss`), soit 18 missions et 3 boss.
> La mission `Speciale` est générée comme une escarmouche pour l'instant. Piloter l'effectif et le
> tier des ennemis selon `(PhaseIndex, MissionInPhase)` d'après la table ci-dessous :
>
> - Phase 1 : 3×T1 · 4×T1 · 5×T1 · (5T1+1T2) · (5T1+2T2) · Boss+(7T1+2T2)  ← le T2 démarre à l'escarmouche 4
> - Phase 2 : (4T1+3T2) · (4T1+4T2) · (3T1+6T2) · (3T1+6T2) · (2T1+8T2) · Boss+(3T1+7T2)
> - Phase 3 : (5T2+3T3) · (5T2+4T3) · (4T2+6T3) · (4T2+6T3) · (3T2+8T3) · BossFinal+(4T2+8T3)
>
> (Boss de phase 1 = 9 pions + 1 boss ; boss final = 12 pions + 1 boss.)
>
> **À faire** :
> 1. Étendre `CombatType` avec `Speciale`.
> 2. Dans `Run.cs` : `PhaseCount=3`, `MissionsPerPhase=6`, `TotalCombats=18`, un `PhaseLayout`,
>    les propriétés `PhaseIndex`, `MissionInPhase`, `CurrentMission`, `IsBossCombat`, `IsFinalBoss`.
> 3. Réécrire `BuildEnemyWave()` pour lire effectif + composition de tier depuis une table statique
>    de 18 entrées, en tirant les domaines dans le pool débloqué et une `UnitClass` du tier requis
>    (ajouter un helper `ClassesAtTier(Domaine, int)`). Conserver le RNG déterministe `CombatRng`.
> 4. Faire déclencher la victoire de run par `IsFinalBoss` (et non plus `IsBossCombat`).
> 5. Adapter `GameplayScene.cs` : `MapForCombat` (taille par phase + filtre map par `CurrentMission`),
>    le bandeau de combat (« Phase X/3 — <mission> » + clés de loc), et la sélection musicale.
> 6. `RunSave` : bumper `Version` à 2 (aucun nouveau champ — `CombatNumber` 1..18 suffit).
> 7. Ajouter les tests xUnit du §11.
>
> **Contraintes** : garder `Echec.Core` sans dépendance MonoGame ; déterminisme des vagues
> préservé ; ne rien casser du cycle `RunPhase` interne. Compiler (`dotnet build`) et faire passer
> `dotnet test`.
