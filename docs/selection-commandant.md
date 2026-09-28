# Écran de sélection du commandant : refonte

Maquette : `docs/maquettes/selection-commandant.png`, et le canvas « Popup tutoriel », artboard « B · Liste à filets ».
Fichier concerné : `src/ChessArmy.Game/Scenes/CommanderSelectScene.cs`.

Coordonnées en **pixels virtuels** (canevas 960×540 ; la maquette est à ×2).
Style : `UiStyle` et thème de couleur courant (Doré par défaut, cf. `popups-et-themes-ui.md`), `PixelFont`, couleurs de `Palette` uniquement.

---

## 1. Fond

Même fond que le menu principal : `Textures.CreateVerticalDitherGradient(w, h, Palette.Black4, Palette.Navy2, Palette.Black1)`, recréé si la taille change (même code que `MainMenuScene`).
Plus d'aplat `Navy2`.

## 2. Moitié gauche (x 0 → 520) : le commandant

De haut en bas :
- **Titre** « CHOISIR UN COMMANDANT » ×2, `Yellow2`, centré sur la moitié gauche, y ≈ 24.
- **Commandant choisi** : sprite ×4 (256 px), centré sur la moitié gauche, y ≈ 64. **Pas de socle**, pas de bannière.
- **Carrousel** : commandant précédent à gauche, suivant à droite, sprites **×2** (128 px), **atténués** (même alpha qu'aujourd'hui), **centrés verticalement sur le sprite choisi**. Ils restent dans la moitié gauche.
- **Nom** ×3, `White`, centré, y ≈ 346, avec les **flèches** (boutons 36×40) de part et d'autre du nom.
- **Pastilles** sous le nom (y ≈ 392) : 7 barres de 4×4, celle du commandant courant plus large (16×4) en `Yellow2`, les autres en `Black5`. Remplace « 1 / 7 ».
- **Difficulté** (voir §4).
- Le **bouton de l'arbre posé sur le sprite disparaît** : il passe dans le cadre de droite.
- Le texte « ECHAP POUR REVENIR » est **retiré** (l'indication manette peut rester si besoin, sinon retirée aussi).

## 3. Cadre de droite (x 540 → 928, y 24 → 456) : la fiche

`DrawPanel` (tramage + biseau du thème). Toujours visible : **plus de sous-panneaux au survol**.
Tout le texte à l'**échelle 1**. Libellés en `White` ; titres de section en `Yellow1`. Sections séparées par un **filet horizontal** : 1 px `Black1` + 1 px `Black5` en dessous.

Dans l'ordre :

1. **STATS**, sur deux colonnes séparées par un **filet vertical** (1 px `Black1` + 1 px `Black5` à droite) :
   - colonne gauche : `PV`, `PUISSANCE`, `MOUVEMENT`, `PORTEE`. Valeur juste après le libellé (libellé ~84 px de large, valeur alignée à droite sur ~28 px). Couleurs des valeurs : PV `White`, puissance `Brown3`, mouvement `Cyan2`, portée `Yellow2` (comme la carte d'unité) ;
   - colonne droite : `DEPLOIEMENT` / `RESERVE` (valeurs `White`, même alignement), puis `DEPLACEMENT` + l'icône de domaine du commandant (`_card.DrawDomaineBadge`, 39 px). Pas d'autre schéma de déplacement.
2. **GAIN DE POINT DE COMMANDE** : une ligne par source, texte `White`, en commençant **toujours** par la mission :
   - `commander.points_mission` avec `def.MissionPoints` (**tous les commandants**, souvent +2, mais Marchand 1, Artisan 3 : lire la valeur, ne pas l'écrire en dur) ;
   - puis les sources propres au commandant déjà gérées aujourd'hui (fusion, touché, tir, saut, butin, soin, paire, allié tombé), mêmes clés et mêmes conditions que `DrawArmyPanel` ;
   - `commander.duo_leaders` reste affiché pour le duo (`Yellow2`).
3. **UNITES DE DEPART** : les vignettes actuelles (portrait 64×64 sur fond `Green1`, nom dessous). `commander.alone` si aucune. Survol : la carte de l'unité, comme aujourd'hui.
4. **Bouton ARBRE DE COMPETENCE** : toute la largeur du cadre, hauteur 36, icône arbre (`arbre.png` ou le repli dessiné) + libellé `commander.tree`. Ouvre l'arbre comme le bouton actuel (même logique, même focus manette).
5. **HISTORIQUE** : 4 valeurs en grille 2×2 :
   - `PARTIES JOUEES` · `VICTOIRES` (valeur `Yellow1`)
   - `UNITES TUEES` · `TEMPS DE JEU` (format `6H 42`, cf. `TimeText`)

Si le contenu dépasse (commandant avec beaucoup de sources de points), réduire l'espacement entre sections, jamais la taille du texte.

## 4. Difficulté (sous les pastilles, x 42 → 478)

- **3 boutons nommés** côte à côte, hauteur 36 : `FACILE`, `NORMAL`, `DIFFICILE` (`difficulty.facile/normal/difficile`), nom à gauche, **jauge de danger** à droite : 3 carrés 6×6, allumés selon le niveau (1, 2, 3) en `Purple5`, éteints en `Black1`.
- **Niveau déjà gagné avec ce commandant** (`HasWonWith`) : ses carrés allumés passent en **`Grey`** au lieu de `Purple5`. Pas de coche, pas de couronne, pas de cadre vert.
- **Niveau sélectionné** : **pas de cadre** ; fond du bouton **plus clair** = tuile tramée « sélection » (voir §5). Nom en `Yellow2`.
- Sous les boutons, **toujours visible** (plus d'infobulle) : la liste des effets du niveau sélectionné, un tiret par ligne, texte `White`, mêmes règles que `DrawDifficultyTooltip` (lignes IA / vagues / équipement / objectif / pas de recommencer).
- Puis, si le niveau sélectionné est déjà gagné, une ligne en **`Yellow2`** :
  - si les 3 niveaux ne sont **pas** tous gagnés : `difficulty.already_won_level` « Deja gagne avec ce commandant a cette difficulte » (nouvelle clé) ;
  - si les 3 sont gagnés : `difficulty.already_won` (existant).

## 5. Boutons : enfoncement au survol, plus de cadre jaune

Pour **tous** les boutons de l'écran (flèches, difficultés, arbre, RETOUR, LANCER) :
- **survol** : le bouton **s'enfonce** (biseau inversé, contenu décalé de 1 px) **et** prend la même tuile claire que l'état sélectionné ;
- **clic** : biseau inversé épais, contenu décalé de 2 px, même tuile claire ;
- **plus de cadre jaune** au survol (le `Border(... Yellow2 ...)` de survol disparaît). À la manette, l'élément focus est dessiné **à l'état survolé** (enfoncé + fond clair) au lieu d'être encadré.
- **Tuile « sélection / survol »** : tramage `Black5` / `Blue3` dans le thème Doré. L'ajouter au `UiTheme` (`SelectA`, `SelectB`) pour les autres thèmes : Sombre `Black4`/`Black5`, Olive `Green1`/`Grey`, Cramoisi `Purple2`/`Purple3`.
- `UiStyle.DrawButton` : en `Hover` et `Pressed`, remplir avec cette tuile au lieu du voile blanc/noir. Ajouter `DrawButton(sb, r, state, selected: true)` pour l'état sélectionné (fond clair, biseau normal).

Barre du bas (sous le cadre, y 474 → 514) : `RETOUR` (≈ 126 px) puis `LANCER` (reste de la largeur), **même taille de texte ×1** tous les deux, LANCER en `Yellow2`, RETOUR en `White`. Pas de cadre doré autour de LANCER.

## 6. Historique : données à enregistrer (nouveau)

Aujourd'hui la sauvegarde ne connaît que `HasWonWith(commandant, difficulté)`. Ajouter au **profil**, **par commandant** (clé = `CommandeDef.Id`) :

| Donnée | Quand l'incrémenter |
|---|---|
| `RunsStarted` | au lancement d'une nouvelle partie avec ce commandant (clic sur LANCER) |
| `RunsWon` | quand la partie est gagnée (même moment que l'enregistrement de `HasWonWith`) |
| `EnemiesKilled` | à chaque unité ennemie tuée pendant une partie avec ce commandant |
| `PlayTimeSeconds` | temps passé en partie (hors menus pause), cumulé et sauvegardé avec la run |

Profil sans ces champs = 0 partout (pas de migration bloquante). Tuto exclu du comptage.

## 7. Nouvelles clés `strings.csv` (9 langues, pas de virgule dans les valeurs)

| Clé | FR | EN |
|---|---|---|
| `commander.history` | HISTORIQUE | HISTORY |
| `commander.runs` | PARTIES JOUEES | RUNS PLAYED |
| `commander.wins` | VICTOIRES | VICTORIES |
| `commander.kills` | UNITES TUEES | UNITS KILLED |
| `commander.playtime` | TEMPS DE JEU | PLAY TIME |
| `commander.movement_pattern` | DEPLACEMENT | MOVEMENT |
| `difficulty.already_won_level` | Deja gagne avec ce commandant a cette difficulte | Already won with this commander at this difficulty |

Clés réutilisées : `commander.title`, `commander.stats`, `stat.hp/power/movement/range`, `commander.deploy`, `commander.reserve`, `commander.points`, `commander.points_*`, `commander.starting_units`, `commander.alone`, `commander.tree`, `commander.back`, `commander.start`, `difficulty.*`.

## Ne pas toucher

- La logique du carrousel (boucle, défilement amorti), le choix de difficulté, le verrouillage des commandants (`IsUnlocked`, silhouettes, `commander.locked`), l'ouverture de l'arbre, le lancement de la partie.
- La navigation manette par rangées : l'adapter au nouvel ordre (carrousel → difficulté → arbre → retour/lancer), sans changer les touches.

## Critères de validation

- À 960×540 et à 1280×720, rien ne déborde ni ne se chevauche ; le cadre de droite ne dépasse pas.
- Stats, armée, gains, unités, arbre et historique visibles **sans survol**.
- Survol souris : boutons enfoncés + fond clair, jamais de cadre jaune. Manette : l'élément focus est lisible.
- Un niveau gagné a ses carrés en `Grey` ; le message jaune suit la règle « à cette difficulté » / « tous gagnés ».
- L'historique s'incrémente correctement après une partie (lancée, gagnée, ennemis tués, temps).
- Build OK, `tests/` OK.

---

## Prompt Claude Code

```
Lis docs/selection-commandant.md et applique-le. La maquette de référence est docs/maquettes/selection-commandant.png.

On ne change que l'écran de sélection du commandant (et UiStyle/UiTheme pour l'état survol/sélection des boutons). La logique du carrousel, de la difficulté, du verrouillage et du lancement ne change pas.

Propose-moi d'abord un plan et attends mon accord. Ensuite : build + tests, et pas de commit.
```
