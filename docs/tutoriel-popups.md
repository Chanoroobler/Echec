# Tutoriel : refonte des pop-ups

Maquettes : canvas « Popup tutoriel » (Design, claude.ai). 8 écrans + un prototype cliquable.

## Problème

`DrawTutorialBigPanel` centre un encart de `min(viewport.Width - 120, 700)` px sur **tout** l'écran (960 px virtuels).
Le panneau de droite fait `RightPanelWidth` = 240 px : l'encart déborde dessus et masque l'inventaire.

## Périmètre : ce qui change, ce qui ne bouge pas

**On ne modifie QUE les pop-ups du tutoriel.**

Ne bouge pas :
- le panneau de droite : `PanelRect`, `DrawPanelBackground`, `DrawPlacementPanel`, l'inventaire, `FightButtonRect`, `CommandTreeButtonRect`, les textes du panneau. Aucun changement de position, de taille, de couleur ou de contenu ;
- la machine à états `TutorialGuide` (ordre des étapes, `Advance`, gating des entrées) ;
- les surbrillances pulsées déjà en place (étape 1 de `DrawTutorialOverlay`) ;
- `DrawTutorialCardReview` (la bulle à gauche de la carte est déjà bien placée) ;
- `DrawTutorialTreeHint` (dessiné par-dessus la modale de l'arbre) ;
- le style : `UiStyle.DrawPanel` / `DrawButton` (tramage + biseau), `PixelFont`, couleurs de `Palette` uniquement. Pas de nouvelle police, pas de couleur en dur.

## Règle de base : la zone de jeu

Toutes les pop-ups vivent dans la **zone de jeu** : `x ∈ [0, viewport.Width - RightPanelWidth]`.
Aucune pop-up, aucun voile, ne recouvre le panneau de droite.

```csharp
private Rectangle PlayArea()
{
    var vp = VirtualViewport;
    return new Rectangle(0, 0, vp.Width - RightPanelWidth, vp.Height);
}
```

## Deux formes de pop-up

### 1. Encart (explication : le jeu attend un clic)

Étapes : `Intro`, `PadLesson`, `FusionIntro`, `EquipIntro`, `TreeIntro`, `Done`, et `Commander` (avec projecteur, voir plus bas).

- Largeur : `min(PlayArea.Width - 48, 560)`, centré dans `PlayArea` (horizontalement et verticalement).
- Voile `Palette.Black1 * 0.6f` sur `PlayArea` seulement (le panneau reste net).
- Fond : `Context.Style.DrawPanel` (inchangé).
- Contenu, de haut en bas :
  1. barre de progression par chapitres (voir plus bas) ;
  2. titre ×3, `Palette.Yellow2`, centré (capitales) ;
  3. corps ×2, `Palette.White`, centré, `preserveCase: true`, replié avec `WrapText(body, largeur - 2*pad, 2)` ;
  4. pour `Intro` seulement : liste des 3 phases (voir plus bas) ;
  5. pied : bouton « PASSER LE TUTO » à gauche (`DrawButton`, texte ×1 `White`), invite (« Clic pour commencer »…) à droite, ×1, `Palette.Cyan1`. Pas de bouton « passer » sur `Done`.

`Commander` : même encart mais **en haut** de `PlayArea` (marge haute ~40 px) pour ne pas couvrir le commandant, et projecteur sur la case du commandant : voile partout dans `PlayArea` **sauf** cette case, entourée d'un cadre `Palette.Yellow2` de 3 px. (4 rectangles de voile autour de la case suffisent.)

### 2. Bandeau (action : le plateau reste jouable)

Étapes : `PickSoldier`, `PlaceSoldier`, `StartCombat`, `CameraLesson`, `DangerLesson`, `Chest`, `Move`, `ReplayLesson`, `Attack`, `FusionDo`, `RerollLesson`, `DeployFused`, `EquipDo`, `TreeOpen`.

Remplace `DrawAnchoredPopup` / `DrawPawnPopup` **pour le tuto uniquement** (les autres usages éventuels restent tels quels).

- Position : bas de `PlayArea`, `x = 20`, largeur `PlayArea.Width - 40`, marge basse 10 px. Hauteur selon le texte (~54 px).
- Si le bandeau recouvre une case occupée du plateau (selon la caméra), le placer en **haut** de `PlayArea` à la place.
- Fond : `DrawPanel`. Pas de voile.
- Contenu :
  - colonne gauche : barre de progression (compacte, sur une ligne) puis consigne ×1 `Palette.Yellow2`, `preserveCase: true`, repliée ;
  - si l'étape attend une touche (`DangerLesson`, `ReplayLesson`, `CameraLesson`) : touche dessinée (petit `DrawButton` avec le nom de touche en `White`, ex. « ESPACE » / « R » / « RT ») + verbe ×1 `Cyan1` (« MAINTENIR », « APPUYER ») ;
  - à droite : bouton « PASSER LE TUTO » (`DrawButton`, ~140×32).
- Les pieds existants (`tuto.next` sur `CameraLesson` et `RerollLesson`) s'affichent à droite de la consigne en `Cyan1`.
- Pas de bouton « suivant » : l'étape avance quand le joueur agit (comportement actuel).

Les messages contextuels existants restent : `tuto.enemy_plays`, `tuto.counter`, `tuto.attack2`, et les variantes manette `_gp` (`TutoT` gère déjà).

## Barre de progression par chapitres

3 chapitres, avec l'indice de `TutorialStep` :

| Chapitre | Étapes | Nb |
|---|---|---|
| PLACEMENT | `Intro` → `StartCombat` | 6 |
| COMBAT | `CameraLesson` → `Commander` | 7 |
| PREPARATION | `FusionIntro` → `TreeDo` | 9 |

À la souris, `PadLesson` est sautée : on la compte faite.

Rendu : pour chaque chapitre, son libellé ×1 puis une rangée de segments (hauteur 3 px, 2 px d'écart), largeur proportionnelle au nombre d'étapes.
- segment fait : `Palette.Yellow1` ; segment courant : `Palette.Yellow2` ; à venir : `Palette.Black5` ;
- libellé du chapitre courant : `Palette.Yellow2`, les autres : `Palette.Blue1`.
- `Done` : tout est fait.

## Liste des 3 phases (encart `Intro`)

Le corps devient « Un combat se déroule en 3 phases : » suivi de 3 lignes :
1. Préparation (le placement) : ligne courante, cadre `Yellow2`, étiquette « EN COURS » ×1 `Yellow1` ;
2. Combat ;
3. Récompense.

Chaque ligne : `DrawRecessed`, numéro + libellé ×2. Les lignes à venir sont en `Palette.Blue1`.

## Bouton « Passer le tuto »

Le bouton en bas à gauche disparaît quand une pop-up est visible : il est dans l'encart ou dans le bandeau.
`TutorialSkipRect()` doit renvoyer **le rectangle réellement dessiné** (celui de l'encart ou du bandeau) pour que `TutorialSkipPressed()` continue de marcher.
Quand aucune pop-up n'est affichée (`TreeDo`, animations `_fx.Active`), on garde l'ancien bouton en bas à gauche.
BACK à la manette : inchangé.

## Nouvelles clés dans `strings.csv` (9 langues)

| Clé | FR | EN |
|---|---|---|
| `tuto.chapter_placement` | PLACEMENT | DEPLOYMENT |
| `tuto.chapter_combat` | COMBAT | BATTLE |
| `tuto.chapter_prep` | PREPARATION | PREPARATION |
| `tuto.intro_lead` | Un combat se déroule en 3 phases : | A battle has 3 phases: |
| `tuto.phase_prep` | Préparation (le placement) | Preparation (placement) |
| `tuto.phase_combat` | Combat | Battle |
| `tuto.phase_reward` | Récompense | Reward |
| `tuto.in_progress` | EN COURS | NOW |
| `tuto.key_hold` | MAINTENIR | HOLD |
| `tuto.key_press` | APPUYER | PRESS |

Traduire IT, DE, ES, PL, TR, ZH dans le même style que les clés `tuto.*` existantes (pas de virgule dans les valeurs, cf. le format du CSV). `tuto.intro_body` reste pour compatibilité.

## Critères de validation

- À 960×540 comme en ultra-large, aucune pop-up du tuto ne chevauche `PanelRect()`.
- Le panneau de droite est identique pixel pour pixel avant/après (capture de comparaison sur `Intro` et `PickSoldier`).
- Toutes les étapes, de `Intro` à `Done`, s'affichent à la souris et à la manette ; le texte ne déborde jamais du cadre (FR et DE, langue la plus longue).
- « Passer le tuto » marche au clic à chaque étape, et BACK à la manette.
- Les tests existants passent (`tests/`).

---

## Prompt pour l'agent de code

```
Contexte : jeu ChessArmy (C#, MonoGame), dépôt Echec. Lis d'abord docs/tutoriel-popups.md : c'est la spec complète.

Tâche : refaire UNIQUEMENT les pop-ups du tutoriel dans src/ChessArmy.Game/Scenes/GameplayScene.cs
(DrawTutorialOverlay, DrawTutorialBigPanel, et la façon dont les consignes des étapes d'action sont affichées),
pour qu'elles restent dans la zone de jeu, à gauche du panneau.

Contraintes strictes :
- NE PAS toucher au panneau de droite : PanelRect, DrawPanelBackground, DrawPlacementPanel, l'inventaire,
  FightButtonRect, CommandTreeButtonRect et leurs textes restent identiques (position, taille, couleurs, contenu).
- Aucun voile ni pop-up ne recouvre le panneau : tout reste dans x < viewport.Width - RightPanelWidth.
- Ne pas modifier TutorialGuide (étapes, ordre, Advance), ni les surbrillances pulsées existantes,
  ni DrawTutorialCardReview, ni DrawTutorialTreeHint.
- Garder le style existant : Context.Style.DrawPanel / DrawButton / DrawRecessed (fond tramé + biseau),
  Context.Font (PixelFont, échelles ×1/×2/×3), couleurs de Palette uniquement, aucune couleur en dur.
- Textes via Loc / TutoT ; ajouter les nouvelles clés listées dans la spec à strings.csv pour les 9 langues.

À faire :
1. Ajouter PlayArea() et centrer les encarts (Intro, PadLesson, FusionIntro, EquipIntro, TreeIntro, Done) dedans,
   largeur min(PlayArea.Width - 48, 560), voile Black1*0.6 limité à PlayArea.
2. Commander : encart en haut de PlayArea + projecteur sur la case du commandant (voile sauf cette case, cadre Yellow2).
3. Étapes d'action : bandeau en bas de PlayArea (en haut s'il recouvre une case occupée) avec progression,
   consigne Yellow2 ×1, touche dessinée si besoin, bouton PASSER LE TUTO.
4. Barre de progression par chapitres (PLACEMENT 6 / COMBAT 7 / PREPARATION 9) dans l'encart et le bandeau.
5. Intro : liste des 3 phases, la 1re marquée EN COURS.
6. TutorialSkipRect() renvoie le rectangle du bouton réellement dessiné ; ancien emplacement seulement
   quand aucune pop-up n'est visible.

Vérifie : build OK, tests OK, puis décris les étapes à contrôler en jeu (souris et manette, FR et DE).
Ne fais pas de commit : laisse-moi relire le diff.
```
