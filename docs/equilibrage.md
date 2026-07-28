# Équilibrage d'Echec — mesures & harnais de simulation

Ce document accompagne le harnais `tools/BalanceSim`. Il contient deux choses :

1. des **mesures immédiates** calculées sur les stats exactes du catalogue (arithmétique
   pure, sans simulation — donc fiables) ;
2. le **mode d'emploi du harnais** de simulation qui rejoue ton vrai `EnemyAi`/`Match`
   pour mesurer les combats.

> ⚠️ **Périmètre des chiffres.** Les mesures et le harnais utilisent le catalogue **codé
> par défaut** (`Domaines.Defaults()`, `Bosses.Defaults()`, `CampaignPlan.Defaults()`).
> En jeu, si `Assets/Config/units.json` / `campaign.json` sont chargés, ils **remplacent**
> ces valeurs. Tant que ton JSON reste aligné sur le code (ce que les commentaires
> imposent), les chiffres valent. Sinon, ajoute au démarrage du harnais un appel aux
> chargeurs JSON (`Domaines.Load(...)`, `Bosses.Load(...)`, `CampaignPlan.Load(...)`)
> pour mesurer exactement les valeurs livrées.

---

## 1. Diagnostic mesuré : d'où vient le « mal d'un coup »

### 1.1 Le one-shot à distance est le vrai coupable

Une attaque tue une cible d'un coup si `Damage ≥ PVmax` (hors réductions situationnelles).
En croisant les 28 classes joueur + commandant avec les attaquants, les pires menaces sont
celles qui one-shot **une grande partie du roster à distance** (portée ≥ 2, donc sans que tu
puisses réagir en t'approchant) :

| Unité ennemie | Tier | Dégâts | Portée | One-shot | Part du roster |
|---|---|---|---|---|---|
| Archimage | T3 | 22 | 4 | 25 / 29 | **86 %** |
| Démoniste | T3 | 20 | 4 | 24 / 29 | 83 % |
| Paladin | T3 | 18 | 3 | 22 / 29 | 76 % |
| **Sorcier** | **T2** | **18** | **4** | **21 / 29** | **72 %** |
| Cavalier griffon | T3 | 16 | 3 | 17 / 29 | 59 % |
| Dragoon | T3 | 16 | 3 | 17 / 29 | 59 % |

Le **Sorcier** est la ligne à retenir : c'est un **tier 2**, donc un ennemi **courant dès la
phase 2** (les vagues de phase 2 comptent jusqu'à 8 T2). Il tue 72 % de ton roster **d'un
seul tir à 4 cases**. Comme les dégâts sont **déterministes** (aucun jet de dé), il n'y a pas
de « mauvais coup de chance » qui te sauve : si le Sorcier atteint, l'unité meurt. C'est
exactement la sensation de coup injuste — et comme ta run s'arrête au boss de la **phase 2**
(`Run.EndAtPhase = 2`), c'est la menace dominante de la partie telle qu'elle se joue.

### 1.2 Pression one-shot par phase

Part moyenne du roster qu'**une** unité ennemie peut tuer d'un coup, et nombre de slots de la
vague qui peuvent one-shot **à distance** :

| Mission | One-shot moyen | Slots one-shot à distance |
|---|---|---|
| Ph1 m5 (5×T1+2×T2) | ~31 % | ~2 / 7 |
| Ph1 m6 Boss | ~28 % | ~2,5 / 10 |
| Ph2 m5 (2×T1+8×T2) | ~38 % | ~3,5 / 10 |
| Ph2 m6 Boss | ~36 % | ~3,4 / 11 |
| Ph3 m5 (3×T2+8×T3) | ~52 % | ~7 / 11 |
| Ph3 m6 Boss final | ~51 % | ~7,5 / 13 |

La phase 1 est déjà tendue (2 menaces à distance qui one-shot ~1/3 du roster) ; la phase 3
serait brutale (la moitié du roster tuable d'un coup, 7 tireurs mortels par vague) — utile à
savoir pour quand tu rouvriras la phase 3.

### 1.3 Burst multi-cibles (« tout en même temps »)

Les mécaniques capables de toucher plusieurs unités en **une** action :

- **Orage / Tempête** : attaque normale **+** dégât fixe (3 / 6) sur **3 unités au hasard**,
  en **ignorant couvert et rempart**. ⚠️ **Aucune classe du catalogue codé ne les porte** :
  ces traits n'apparaissent que si ton `units.json` les donne à un boss. Si c'est le cas, un
  seul tour de boss peut abîmer/tuer jusqu'à 4 unités — c'est un amplificateur majeur du
  « d'un coup », mais **hors périmètre du harnais** tant qu'il tourne sur les valeurs codées.

### 1.4 Poids du motif de déplacement/attaque (diagonale vs toutes directions)

Le tableau 1.1 compare `Dégâts ≥ PV` **sans la géométrie**. Or une menace ne compte que si
elle peut t'atteindre : le **motif d'attaque** décide combien de cases une unité menace depuis
une position, donc à quel point tu peux l'éviter.

| Motif | Directions | Cases menacées (portée r) | vs Dame |
|---|---|---|---|
| Dame | 8 | 8·r | **100 %** |
| Tour | 4 orthogonales | 4·r | 50 % |
| Fou | 4 diagonales | 4·r | 50 % |
| Cavalier | saut en L | 8 fixes (r n'agit pas) | ~33 %, **ignore les obstacles** |

**Conséquence majeure :** les 4 pires du tableau 1.1 — Archimage, Démoniste, Sorcier, Mage —
sont **tous des Fou** (diagonale). Leur menace réelle est **~2× plus faible** que le brut : on
les évite en se tenant hors de leurs 4 diagonales. Pondéré par la couverture, ce sont les
tueurs **Dame** (Barbare, Maître d'armes… et **le boss, qui est Dame**) qui deviennent les plus
durs à esquiver — mais eux sont en **portée 1** (mêlée), donc télégraphiés.

**Combien pondérer ?** Trois niveaux :

1. **Score de menace analytique** (pour ranger les ennemis) : létalité × facteur de couverture
   (Dame 1.0, Fou/Tour 0.5, Cavalier ~0.4 avec l'étiquette « ignore les blocages »). Premier
   ordre suffisant — ne pas aller plus fin.
2. **Frustration ressentie** : la couverture n'est PAS dominante. Ce qui pique, c'est le
   **one-shot à distance déterministe** (le Sorcier n'est « que » 50 % de couverture mais reste
   le pire ressenti : portée 4 + zéro contre-jeu). Pour le mode facile, pondère surtout par
   « peut-il one-shot à portée ≥ 2 ? ».
3. **La vérité mesurée** : inutile de deviner le poids — la **simulation résout déjà la
   géométrie exactement** (elle appelle les vrais `AttackTargets`/`LegalMoves`). Le harnais sort
   désormais une **ventilation des kills et one-shots par motif d'attaquant** (Dame/Fou/Tour/
   Cavalier) : si le Fou pèse peu dans cette colonne, c'est la preuve mesurée qu'il est évitable.

> Nuance : ces couvertures sont un plafond **plateau ouvert**. En mêlée dense, les attaques
> glissées (Dame/Tour/Fou) sont bornées par la première unité rencontrée → couverture réelle
> plus faible ; le **Cavalier** (saut, ignore les blocages) et les **tireurs longue portée**
> deviennent alors relativement plus dangereux.

---

## 2. Le harnais de simulation

### 2.1 Ce qu'il fait

Il rejoue ton **vrai code** (`EnemyAi.ChooseAction`, `Match`, génération de vagues via
`CampaignPlan` + `Run.ClassesAtTier` + `Bosses`). Pour chaque mission-clé, il joue des
centaines de combats *bot joueur* contre *IA ennemie* et agrège :

- **Victoires** — part des combats gagnés par le bot de référence ;
- **Pertes/combat** — unités joueur perdues en moyenne ;
- **Pic ≥ 2** — part des combats où au moins une action ennemie a tué **≥ 2 unités d'un coup** ;
- **1-shot/combat** — unités tuées depuis **PV pleins** en une action (le coup « injuste ») ;
- la **distribution des pics** (combien d'actions ennemies tuent 1, 2, 3… unités).

### 2.2 Comment le lancer

```bash
# Baseline — le jeu tel qu'il est aujourd'hui
dotnet run --project tools/BalanceSim

# Aperçu du preset « facile mais pas trop » (dégâts ennemis ×0.85 + 25 % de maladresse IA)
dotnet run --project tools/BalanceSim -- --preset-easy

# Réglages manuels
dotnet run --project tools/BalanceSim -- --enemy-dmg 0.8 --blunder 0.3 --runs 800
```

Options : `--runs N`, `--enemy-dmg F`, `--blunder F`, `--player N`, `--seed N`,
`--preset-easy`. (Détail en tête de `Program.cs`.)

### 2.3 Comment lire les résultats (⚠️ honnêteté sur les métriques)

- Le **taux de victoire est relatif au bot de référence** codé dans le harnais, pas à un
  humain. Ne le lis **pas** comme « la vraie difficulté ». Lis-le comme un **étalon** pour
  **comparer** deux réglages : baseline vs `--preset-easy`. Si le preset facile fait passer
  les pertes de 2,5 à 1,2 par combat, c'est ça, l'information.
- Les **pics** et **one-shots** sont, eux, **intrinsèques au design** (ils dépendent des
  dégâts/portées ennemis, pas de l'habileté du bot). Ce sont les métriques les plus fiables
  pour juger la frustration, et celles à surveiller en priorité.

### 2.4 Ce que le harnais NE modélise pas (à garder en tête)

Terrain (eau/montagne/couvert), arbre de commandement, équipements, recrutement/permadeath
entre combats, et positionnement humain. Le bot joue « glouton compétent » : il tue la
menace la plus dangereuse à portée, soigne sous 50 %, tire à distance, et évite de marcher
sur une case mortelle. C'est un adversaire *cohérent*, pas *optimal*.

---

## 3. Cibles proposées pour « facile mais pas trop »

À viser en comparant baseline → preset dans le harnais :

- **1-shot/combat** : ramener vers **< 0,5** (aujourd'hui c'est le nerf de la frustration).
- **Pic ≥ 2** : idéalement **< 10 %** des combats en phase facile.
- **Pertes/combat** : **< 1,5** en moyenne.
- **Victoires (étalon)** : le preset facile doit visiblement **améliorer** la survie vs
  baseline ; vise un bot de référence qui gagne largement (≈ 85 %+) sans jamais 0 perte.

### Leviers (rappel), du plus efficace au moins

1. **Maladresse IA** (`--blunder`) : l'IA saute parfois le kill parfait. Levier le plus
   puissant, déjà simulable dans le harnais.
2. **Règle anti-one-shot** (à coder dans `Match.ApplyDamage`) : une attaque d'un ennemi
   **non-boss** ne peut pas amener une unité de PV pleins à 0 (plafond `Hp-1`). ~3 lignes dans
   `ApplyDamage`. **Non simulé par le harnais** (nécessite le hook cœur) —
   c'est le meilleur candidat d'implémentation ensuite.
3. **Dégâts ennemis** (`--enemy-dmg`) : multiplicateur global. Réduit directement les
   one-shots. Alternative douce : +PV joueur.
4. **Dompter le burst** : `StormMaxTargets` = 1 en facile, retirer Tempête des
   profils faciles.
5. **Effectifs / tiers** (`CampaignPlan`) : −1 pion par vague en phase 1, retarder T2/T3.

### État : maladresse IA implémentée

`enum Difficulty` (Facile / Normal / Difficile) + `DifficultySettings` existent dans
`src/ChessArmy.Core/Battle/Difficulty.cs`. Un seul levier est branché pour l'instant, le plus
puissant : **la maladresse** (`AiAccuracy` = probabilité de jouer le meilleur coup ;
**0,50 / 0,75 / 1,00**). En pratique l'IA choisit par rangs de priorité et, quand elle rate son
jet, **descend d'un cran** (renonce au kill parfait pour une attaque simple, etc.) plutôt que de
jouer n'importe quoi — elle ne descend jamais jusqu'à ne rien faire.

Le cœur reste neutre : `EnemyAi.ChooseAction` prend `accuracy` en paramètre (défaut
`PerfectAccuracy`), c'est `GameplayScene.UpdateAiTurn` qui passe `DifficultySettings.Active`.
BalanceSim n'est donc pas affecté et continue de mesurer la baseline en jeu parfait.

### Prochaines étapes

1. Exposer le niveau dans le **menu Options** (`PauseMenu` + `options.json` + `strings.csv`) —
   aujourd'hui `DifficultySettings.Current` se change dans le code, et rien n'est persisté.
2. Brancher les leviers restants sur `DifficultySettings` : multiplicateur de dégâts,
   anti-one-shot, `StormMaxTargets`, effectifs de vagues.
3. Ajouter un `--ai-accuracy` au harnais et **re-mesurer** baseline vs facile pour vérifier les
   cibles ci-dessus (le `--blunder` actuel de BalanceSim est un prototype indépendant, qui
   remplace l'attaque par un coup au hasard — ce n'est pas la même règle que le jeu).
