# Pop-ups du tuto (retouches) + thèmes de couleur de l'UI

Suite de `docs/tutoriel-popups.md`. Maquettes : canvas « Popup tutoriel » (lignes « Déroulé » et « Propositions de couleurs »).

Deux changements indépendants, à faire dans cet ordre :
1. retouches des pop-ups du tuto ;
2. thème de couleur de l'UI, choisi dans le menu Options (Doré par défaut).

## Règles communes

- Le **panneau de droite** ne change **ni de place, ni de taille, ni de contenu, ni de couleur de texte**. Seuls son fond tramé et son liseré gauche suivent le thème (partie 2).
- Le **voile** sur la zone de jeu reste `Palette.Black1 * 0.6f`, quel que soit le thème.
- Les **couleurs de texte** ne changent pas (titres `Yellow2`, texte `White`, libellés `Blue1`, infos `Cyan1`, en-tête `Yellow1`).
- Toutes les couleurs viennent de `Palette`, aucune couleur en dur.

---

## 1. Retouches des pop-ups du tuto

### Encart centré (Intro, PadLesson, FusionIntro, EquipIntro, TreeIntro, Done, Commander)

Mise en page, de haut en bas, **tout aligné à gauche** :
1. barre de progression par chapitres (inchangée) ;
2. titre ×3, `Yellow2` ;
3. texte ×1, `White`, `preserveCase: true` ;
4. `Intro` seulement : liste des 3 phases (voir plus bas) ;
5. pied : l'invite (« Clic pour commencer »…) ×1 `Cyan1`, **alignée à droite**. Pas de bouton « Passer le tuto » dans l'encart.

### Liste des 3 phases (Intro)

- Une simple liste numérotée : `1. Préparation (le placement)`, `2. Combat`, `3. Récompense`.
- Texte ×1, léger retrait à gauche (~8 px), ~4 px entre les lignes.
- **Aucun cadre** : pas de `DrawRecessed`, pas de fond, pas de bordure (ce ne sont pas des champs).
- Phase en cours (la 1) en `White`, les suivantes en `Blue1`. Pas d'étiquette « EN COURS ».

### Bouton « Passer le tuto »

| Pop-up affichée | Emplacement du bouton |
|---|---|
| Encart centré (Intro, PadLesson, FusionIntro, EquipIntro, TreeIntro, Commander) | **coin bas-gauche de l'écran**, emplacement d'origine : `new Rectangle(20, vp.Height - 60, 200, 40)`, dessiné par-dessus le voile |
| Bandeau (étapes d'action) | **dans le bandeau, à droite**, comme dans la spec précédente ; le bandeau garde toute la largeur de la zone de jeu |
| Done (BRAVO !) | aucun bouton |
| Aucune pop-up (TreeDo, animations) | coin bas-gauche, comme à l'origine |

`TutorialSkipRect()` renvoie toujours le rectangle réellement dessiné, pour que le clic marche. BACK à la manette : inchangé.

---

## 2. Thème de couleur de l'UI

### Ce que le thème change

Uniquement :
- la **tuile tramée** des panneaux, pop-ups et boutons (`UiStyle.FillDither`, créée aujourd'hui par `Textures.CreateDitherTile(GraphicsDevice, 8, Palette.Black3, Palette.Black2)` dans `ChessArmyGame`) ;
- le **biseau** (`UiStyle.Highlight` = arête claire, `UiStyle.Shadow` = arête sombre) ;
- le **fond et le liseré gauche du panneau de droite** (`DrawPanelBackground` : `FillDither` + ligne de 2 px `Palette.Navy1`).

Rien d'autre : ni textes, ni plateau, ni eau, ni voile, ni surbrillances.

### Les 4 thèmes

| Thème | Libellé FR | Tramage pop-ups / boutons | Biseau clair | Biseau sombre | Tramage panneau droit | Liseré gauche panneau |
|---|---|---|---|---|---|---|
| Sombre (actuel) | SOMBRE | `Black3` / `Black2` | `Black5` | `Black1` | `Black3` / `Black2` | `Navy1` |
| Olive | OLIVE | `Green2` (#314e3f) / `Green1` (#4f5d42) | `Grey` (#9a9f87) | `Black1` | idem pop-ups | `Grey` |
| **Doré (par défaut)** | DORE | `Black4` (#1d3230) / `Black5` (#2b454f) | `Yellow1` (#e8b26f) | `Brown1` (#704d2b) | idem pop-ups | `Yellow1` |
| Cramoisi | CRAMOISI | `Purple1` (#40231e) / `Purple2` (#8a2c36) | `Purple3` (#a3412b) | `Black1` | `Black2` (#151015) / `Purple1` (#40231e) | `Purple2` |

Le cadre extérieur d'1 px (`Frame`, `Black1`) et la bande droite de 6 px du panneau (`Black1`) ne changent pas.

### Code

- `ChessArmy.Engine/UI/UiTheme.cs` (nouveau) : `enum UiThemeId { Dark, Olive, Gold, Crimson }` et un `record UiTheme(Color DitherA, Color DitherB, Color Highlight, Color Shadow, Color PanelA, Color PanelB, Color PanelEdge)` avec `static UiTheme For(UiThemeId id)`, qui ne lit que des couleurs de `Palette`.
- `UiStyle` :
  - prend le thème, possède **deux** tuiles : pop-ups/boutons et panneau ;
  - `SetTheme(UiThemeId)` recrée les deux tuiles et met à jour `Highlight`/`Shadow`, à chaud, sans redémarrer ;
  - ajoute `FillPanelDither(sb, r)` pour le fond du panneau droit ;
  - `DrawPanel`, `DrawButton` et `DrawRecessed` gardent leur signature.
- `GameplayScene.DrawPanelBackground` : `FillPanelDither` + liseré `Style.Theme.PanelEdge` à la place de `Palette.Navy1`. **Aucun autre changement dans le panneau.**
- `GameSettings` : `public UiThemeId UiTheme { get; set; } = UiThemeId.Gold;`.
- `SettingsDto` : propriété `UiTheme` (défaut `Gold`), lue et écrite dans `From` / `ApplyTo`. Un ancien `options.json` sans la clé donne donc Doré.
- `ChessArmyGame` : crée `UiStyle` avec le thème des réglages chargés.

### Menu Options

- Nouvelle ligne **« COULEURS UI »**, sous LANGUE, même composant flèche gauche / valeur / flèche droite que les autres lignes.
- Ordre des valeurs : DORE → OLIVE → CRAMOISI → SOMBRE (en boucle).
- `PauseMenu` :
  - `ThemeRow/ThemeLeft/ThemeValue/ThemeRight` dans `PauseLayout` ;
  - `FocusCount` des Options passe de 7 à 8, et « Retour » passe à l'index 7 ;
  - `AdjustFocused` : case 6 → `StepTheme(dir)` ;
  - nouvelle `MenuAction.ThemeChanged`, sur laquelle l'appelant fait `Context.Style.SetTheme(...)` puis sauvegarde les réglages, comme pour la langue ;
  - clic sur les flèches géré comme `LangLeft` / `LangRight`.
- Vérifier que le panneau Options tient encore à l'écran avec une ligne de plus. Sinon, réduire l'espacement vertical entre les lignes, sans toucher à leur hauteur.

### Nouvelles clés `strings.csv` (9 langues, pas de virgule dans les valeurs)

| Clé | FR | EN |
|---|---|---|
| `options.ui_theme` | COULEURS UI | UI COLORS |
| `theme.dark` | SOMBRE | DARK |
| `theme.olive` | OLIVE | OLIVE |
| `theme.gold` | DORE | GOLD |
| `theme.crimson` | CRAMOISI | CRIMSON |

---

## 3. Ne plus utiliser le mot « IA » dans les textes

Le joueur affronte **l'ennemi** ou **l'adversaire**, pas « l'IA ». Ces 7 clés de `strings.csv` sont les seules concernées (FR « IA » / EN « AI »). On change **uniquement les valeurs** ; les noms de clés (`difficulty.ai_*`, `hud.replay_ai`) restent tels quels pour ne pas toucher au code.

Format du CSV : pas de virgule dans les valeurs. Les majuscules suivent les textes actuels.

| Clé | FR | EN | IT | DE | ES | PL | TR | ZH |
|---|---|---|---|---|---|---|---|---|
| `difficulty.ai_low` | L'ennemi fait beaucoup d'erreurs | The enemy makes many mistakes | Il nemico commette molti errori | Der Gegner macht viele Fehler | El enemigo comete muchos errores | Wróg popełnia wiele błędów | Düşman çok hata yapar | 敌人经常失误 |
| `difficulty.ai_fewer` | L'ennemi fait moins d'erreurs | The enemy makes fewer mistakes | Il nemico commette meno errori | Der Gegner macht weniger Fehler | El enemigo comete menos errores | Wróg popełnia mniej błędów | Düşman daha az hata yapar | 敌人失误更少 |
| `difficulty.ai_fewer_again` | L'ennemi fait encore moins d'erreurs | The enemy makes even fewer mistakes | Il nemico commette ancora meno errori | Der Gegner macht noch weniger Fehler | El enemigo comete aun menos errores | Wróg popełnia jeszcze mniej błędów | Düşman daha da az hata yapar | 敌人失误更加少 |
| `hud.replay_ai` | REVOIR ACTION ENNEMIE | REPLAY ENEMY ACTION | RIVEDI AZIONE NEMICA | GEGNERAKTION WIEDERHOLEN | REVER ACCION ENEMIGA | POWTORZ RUCH WROGA | DUSMAN HAMLESINI TEKRAR | 重看敌方行动 |
| `tuto.enemy_plays` | Ton action est faite. C'est au tour de l'adversaire de jouer. | Your action is done. Now the opponent takes its turn. | La tua azione è fatta. Ora tocca all'avversario. | Deine Aktion ist erledigt. Jetzt ist der Gegner am Zug. | Tu acción está hecha. Ahora juega el rival. | Twoja akcja wykonana. Teraz gra przeciwnik. | Eylemin tamam. Sıra rakibe geçti. | 你的动作完成了。轮到对手行动。 |
| `tuto.replay` | Appuie sur R pour revoir la dernière action de l'ennemi. | Press R to replay the enemy's last action. | Premi R per rivedere l'ultima azione del nemico. | Drücke R um die letzte Aktion des Gegners zu wiederholen. | Pulsa R para volver a ver la última acción del enemigo. | Naciśnij R aby powtórzyć ostatni ruch wroga. | Düşmanın son hamlesini tekrar görmek için R ye bas. | 按R重看敌人的上一个动作。 |
| `tuto.replay_gp` | Appuie sur RB pour revoir la dernière action de l'ennemi. | Press RB to replay the enemy's last action. | Premi RB per rivedere l'ultima azione del nemico. | Drücke RB um die letzte Aktion des Gegners zu wiederholen. | Pulsa RB para volver a ver la última acción del enemigo. | Naciśnij RB aby powtórzyć ostatni ruch wroga. | Düşmanın son hamlesini tekrar görmek için RB ye bas. | 按RB重看敌人的上一个动作。 |

Vérification : après modification, aucune valeur FR ne contient le mot « IA » et aucune valeur EN le mot « AI ». Pour le vérifier, chercher dans `strings.csv` les mots entiers `IA`/`AI`, en ignorant les clés. Chercher aussi dans le code C# un libellé « IA » écrit en dur, hors commentaires.

---

## Critères de validation

- Nouvelle install ou `options.json` sans thème : l'UI est en **Doré**.
- Changer le thème dans Options change immédiatement les pop-ups, les boutons, le menu et le fond du panneau droit. Le choix survit à un redémarrage.
- Pour chacun des 4 thèmes : le panneau droit a exactement les mêmes positions, textes et couleurs de texte qu'avant (capture de comparaison).
- Encart Intro : titre et texte alignés à gauche, liste des phases sans cadre, bouton « Passer le tuto » en bas à gauche de l'écran.
- Étapes d'action : « Passer le tuto » dans le bandeau.
- Plus aucun texte du jeu ne dit « IA » (FR) ou « AI » (EN) : difficulté, bouton de replay, tuto.
- Build OK, `tests/` OK.

---

## Prompt Claude Code

```
Lis docs/popups-et-themes-ui.md et applique-le (parties 1, 2 puis 3).

Rappel : le panneau de droite ne bouge pas. Seuls son fond tramé et son liseré suivent le thème. Si tu penses devoir y toucher autrement, arrête-toi et demande-moi.

Propose-moi d'abord un plan et attends mon accord. Ensuite : build + tests, et pas de commit.
```
