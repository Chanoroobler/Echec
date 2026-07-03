# Types de mission

Une run d'Echec = **3 phases de 6 missions** (18 combats au total), jouées dans un rythme fixe.
Chaque mission a une **nature** portée par l'enum `CombatType`
([src/Echec.Core/Map/CombatType.cs](../src/Echec.Core/Map/CombatType.cs)) :

```csharp
public enum CombatType
{
    Escarmouche, // tuer toutes les unités ennemies
    Speciale,    // mission spéciale (contenu à venir ; générée comme une escarmouche pour l'instant)
    Boss,        // tuer le boss
}
```

## Rythme d'une phase

Les 6 missions d'une phase suivent toujours le même ordre (`Run.PhaseLayout`) :

| Slot | 1 | 2 | 3 | 4 | 5 | 6 |
|------|---|---|---|---|---|---|
| Nature | Escarmouche | Escarmouche | **Spéciale** | Escarmouche | Escarmouche | **Boss** |

Une run = phase 1 → phase 2 → phase 3, soit **18 missions** et **3 boss**.
`CombatNumber` (1..18) est le seul curseur ; `PhaseIndex` (1..3) et `MissionInPhase` (1..6) en dérivent.

---

## ⚔️ Escarmouche

- **Objectif** : éliminer **toutes** les unités ennemies.
- **Fréquence** : 4 par phase (slots 1, 2, 4, 5) — la nature la plus courante.
- **Récompense** : recrutement (draft des 3 derniers ennemis vaincus), puis mission suivante.
- **Map** : pioche une map de type `Escarmouche` à la taille de la phase, sinon terrain aléatoire.

C'est la mission « de base » : le déroulé standard de la boucle placement → combat → recrutement.

---

## ⭐ Spéciale

- **Objectif** : *à définir* — pas encore de contenu propre.
- **Fréquence** : 1 par phase (slot 3), au milieu de la phase.
- **État actuel** : la mission est **typée `Speciale`** (pour brancher le futur) mais **générée
  exactement comme une escarmouche** — même objectif (tuer tous les ennemis), même effectif que le
  tableau ci-dessous, et elle **retombe sur une map d'escarmouche** tant qu'aucune map dédiée n'existe.
- **Récompense** : identique à une escarmouche pour l'instant (recrutement).

> 🚧 **À détailler plus tard.** Idées de pistes à remplir : objectif alternatif (survivre X tours,
> escorter/protéger une unité, atteindre une case, récupérer un objet…), récompense renforcée,
> modificateurs de terrain, mini-boss, etc. Quand le contenu sera défini :
> - lui donner sa propre génération dans `Run.BuildEnemyWave` (ou une méthode dédiée),
> - créer des maps de type `Speciale` et lever le repli vers `Escarmouche` dans
>   `GameplayScene.MapForCombat`,
> - ajouter l'objectif/condition de victoire côté `Match`/scène.

---

## 👑 Boss

- **Objectif** : tuer le **boss** (unité essentielle ennemie, `Commandes.Boss`). Les escortes peuvent
  survivre — seul le boss compte.
- **Fréquence** : 1 par phase (slot 6), en fin de phase. Il y a donc **3 boss par run**.
- **Composition** : le boss **+ ses escortes** (voir tableau). Le même `Commandes.Boss` est réutilisé
  pour les 3 phases pour l'instant ; seules les escortes changent *(TODO : boss distincts par phase)*.
- **Victoire de run** : **seul le boss FINAL** (boss de la phase 3, `IsFinalBoss`) gagne la partie.
  Les boss des phases 1 et 2 **enchaînent normalement vers le recrutement**, comme une escarmouche
  gagnée.
- **Map** : pioche une map de type `Boss` à la taille de la phase, sinon terrain aléatoire.
- **Musique** : piste « boss » sur les 3 combats de boss *(TODO : piste distincte pour le boss final)*.

---

## Effectif & force par mission

Source de vérité : la table `Run.WaveTiers`. L'effectif monte de 2 à ~12 pions et le tier moyen
glisse de T1 à T3 phase après phase (T1 = base, T2 = branche, T3 = feuille de l'arbre de classes).
Pour un boss, on compte **le boss + ses escortes**.

### Phase 1 — apprentissage (T1 ; le T2 arrive à l'escarmouche 4)

| Slot | Nature | Effectif | Composition |
|------|--------|----------|-------------|
| 1 | Escarmouche | 2 | 2× T1 |
| 2 | Escarmouche | 3 | 3× T1 |
| 3 | Spéciale | 4 | 4× T1 *(temporaire, en attendant la vraie mission spéciale)* |
| 4 | Escarmouche | 6 | 5× T1 + 1× T2 |
| 5 | Escarmouche | 7 | 5× T1 + 2× T2 |
| 6 | Boss | Boss + 9 | Boss + 7× T1 + 2× T2 |

### Phase 2 — montée en puissance (T2 dominant)

| Slot | Nature | Effectif | Composition |
|------|--------|----------|-------------|
| 1 | Escarmouche | 7 | 4× T1 + 3× T2 |
| 2 | Escarmouche | 8 | 4× T1 + 4× T2 |
| 3 | Spéciale | 9 | 3× T1 + 6× T2 |
| 4 | Escarmouche | 9 | 3× T1 + 6× T2 |
| 5 | Escarmouche | 10 | 2× T1 + 8× T2 |
| 6 | Boss | Boss + 10 | Boss + 3× T1 + 7× T2 |

### Phase 3 — fin de run (T2 → T3)

| Slot | Nature | Effectif | Composition |
|------|--------|----------|-------------|
| 1 | Escarmouche | 8 | 5× T2 + 3× T3 |
| 2 | Escarmouche | 9 | 5× T2 + 4× T3 |
| 3 | Spéciale | 10 | 4× T2 + 6× T3 |
| 4 | Escarmouche | 10 | 4× T2 + 6× T3 |
| 5 | Escarmouche | 11 | 3× T2 + 8× T3 |
| 6 | Boss final | Boss + 12 | Boss + 4× T2 + 8× T3 |

Le **domaine** de chaque pion (Dame/Tour/Cavalier/Fou) est tiré dans le pool débloqué ; la table ne
fixe que **l'effectif et le tier**.

---

## Où c'est dans le code

| Élément | Fichier |
|---------|---------|
| Enum des natures | [CombatType.cs](../src/Echec.Core/Map/CombatType.cs) |
| Rythme, table d'effectifs, génération des vagues, progression | [Run.cs](../src/Echec.Core/Campaign/Run.cs) (`PhaseLayout`, `WaveTiers`, `BuildEnemyWave`, `CompleteCombat`) |
| Type porté par une map | [MapData.cs](../src/Echec.Core/Map/MapData.cs) (`Type`) |
| Choix de la map (taille par phase + filtre par nature) | [GameplayScene.cs](../src/Echec.Game/Scenes/GameplayScene.cs) (`MapForCombat`) |
| Design d'ensemble des 3 phases | [feature-flow-3-phases.md](feature-flow-3-phases.md) |
