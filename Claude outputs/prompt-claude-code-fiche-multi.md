# Prompt Claude Code — Écran Armée multijoueur : fiche sur le plateau

> À coller tel quel dans Claude Code, lancé à la racine du repo `Echec`.

---

## Contexte

Jeu MonoGame (net9.0) `ChessArmy`. L'écran à modifier est l'écran **Armée du multijoueur** :
`src/ChessArmy.Game/Scenes/PvpArmyScene*.cs` (partial class `PvpArmyScene`).

- Mise en page : `PvpArmyScene.Layout.cs` (classe `Lay`, recalculée à chaque frame, mode COMPLET 1280×720 et COMPACT 960×540).
- Rendu : `PvpArmyScene.Draw.cs` (`DrawBuild` → `DrawArmyTabs`, `DrawCommander`, `DrawBoard`, `DrawReserve`, `DrawSheet`, `DrawBand`).
- Plateau animé 7×7 : `UI/MoveAttackGrid.cs` (`DrawBoardWithPawn`, `BoardOuter` = 115 px).
- Couche écran pour sprites nets à demi-taille : `UI/NativeSprites.cs`.
- Données : `Assets/Config/units.json`, `equipment.json`, `pvp.json` (stats et coûts PROPRES AU MULTI), textes dans `Assets/Config/strings.csv` (clés `kw.<trait>.label/desc`, `stat.*`, `pvp.*`).

**Attention : un premier essai a déjà été écrit (par Claude Cowork, sans pouvoir compiler).** Fichiers touchés :
`PvpArmyScene.cs`, `PvpArmyScene.Layout.cs`, `PvpArmyScene.Draw.cs` et un nouveau `PvpArmyScene.Sheet.cs`.
Fais `git diff` / `git status` d'abord. Garde ce qui est bon, corrige ou réécris le reste. **Compile et lance le jeu pour vérifier chaque étape** (capture d'écran de l'écran multi, une carte survolée), c'est ce qui a manqué.

---

## Ce que je veux (maquette validée)

### 1. La fiche (tooltip) passe SUR LE PLATEAU
- Avant : la fiche d'info (pion / commandant / équipement) était sous la grille de la réserve, à droite.
- Maintenant : elle s'ouvre **par-dessus les 4 rangées du plateau juste au-dessus des deux lignes bleues du joueur** (rangées vides pendant la composition), sur toute la largeur du plateau, dans un `PvpArt.Panel`.
- Elle s'arrête quelques pixels au-dessus des têtes des pions de la rangée avant (les pions survolés sont relevés de 14 px).
- Elle n'apparaît que quand quelque chose est survolé / tenu (carte de la réserve, pion posé, badge d'équipement, commandant). Rien de survolé → plateau dégagé, pas de texte d'aide (l'aide est déjà dans la bande du bas).

### 2. Contenu de la fiche d'un PION / COMMANDANT (petit, compact, max de place pour le texte)
- **En-tête d'une ligne** : petit sprite de face (32 px = moitié, net via `NativeSprites`), **NOM** (échelle 2), **coût** en jaune (`pvp.cost` / `pvp.cost_fixed` pour le commandant), et à droite le motif de déplacement (`pvp.move.<domaine>`, ex. « DAME · 8 DIRECTIONS »). Filet en dessous.
- **Corps en colonnes** :
  1. **Stats** compactes : PV / PUISSANCE / MOUVEMENT / PORTÉE, icônes `Icons/stat_*.png` à 16 px (moitié, nettes), libellé gris, valeur à droite (dorée + « +N » si bonus d'équipement). Valeurs du MULTI (`pvp.json` → `unitStats`, `commanders`).
  2. **Déplacement animé** : `MoveAttackGrid.DrawBoardWithPawn` (le pion parcourt ses cases, tir en rouge, saut du cavalier).
  3. **Traits** : chaque trait « NOM : description complète » (code existant `TraitEntries` / `AddTraitLines` / `FitSheetLines` / `DrawSheetLines`), sur toute la hauteur. Les traits venant de l'équipement en doré.
  4. **Équipement porté** (si le pion en a un) : badge 32 + nom en couleur de rareté + effet. Colonne à part si la largeur le permet, sinon en tête de la colonne des traits.
- **Pas d'évolutions** : elles n'existent pas en multi.
- Icônes et sprites PETITS à dessein : toute la place va aux descriptions.

### 3. Contenu de la fiche d'un ÉQUIPEMENT
- En-tête : case 32 (`Equipment/background.png` + icône natives, liseré de rareté), NOM (couleur de rareté), rareté, coût (`pvp.json` → `equipmentCost`).
- Gauche : une ligne compacte par effet (« PV +5 », « TRAIT Rempart »). Droite : fonctionnement des traits donnés.
- Option bienvenue : rappel « sur ce pion : PV 13 → 18 » quand on survole le badge d'un pion équipé.

### 4. La réserve descend jusqu'en bas
- Plus de fiche dans le panneau de droite : la grille de cartes (pions ou équipements) remplit **toute la hauteur** du panneau (autant de rangées qu'il en tient, rangées réparties sans vide en bas). Pagination ‹ 1/N › inchangée. Vérifier que `Page` reste dans les bornes quand la grille change de taille.

### 5. Panneau du commandant : voir comment il se déplace
- Sous les barres de stats, remplacer l'icône de domaine seule par le **plateau 7×7 animé** (`MoveAttackGrid`) du commandant (domaine, portée, Zone morte…), avec « DÉPLACEMENT » et le motif (« CAVALIER · SAUT EN L ») à côté ou en dessous.
- Réduire le portrait d'un cran (×3 → ×2 en complet) si nécessaire pour que tout tienne. Garder flèches ‹ › et points de pagination.

### 6. Ne rien casser
- Garder TOUT le reste de l'écran tel quel : barre du haut (icône de rang, ?, pièces, CLASSEMENT avec trophée, COFFRES avec coffre, RETOUR), onglets ARMÉE 1/2/3, bande du bas, bouton LANCER UNE PARTIE, tutoriel (`PvpArmyScene.Tutorial.cs` : zones Reserve/Board), coffres, classement, manette (focus), glisser-déposer.
- Pas d'allocation par frame (le code met tout en cache : suivre ce style, clés `_sheetKey`).
- Les deux modes de canevas (complet 1280×720 et compact 960×540) doivent fonctionner.
- Pixel art : jamais d'échelle fractionnaire ; pour une demi-taille nette, passer par `NativeSprites.Queue` (repli canvas si `NativeSprites.CanQueue` est faux ou si le tuto / une erreur est affiché).

---

## Étapes attendues
1. `git status` + `git diff` pour voir l'essai existant ; lire les fichiers cités.
2. Compiler (`dotnet build src/ChessArmy.Game`) et corriger les erreurs.
3. Ajuster la mise en page / le rendu jusqu'à correspondre à la description ci-dessus.
4. Lancer le jeu, aller dans MULTIJOUEUR, survoler : un pion de la réserve (ex. Arbalétrier : Zone morte, Recule, Transpercer + Bannière du régiment = Formation), un pion posé avec équipement, le commandant (case fixe), un équipement. Prendre des captures et vérifier.
5. Me résumer ce qui a changé, fichier par fichier.
