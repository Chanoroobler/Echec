# Multijoueur : écran de FIN DE PARTIE

Maquette validée le 2026-11-06. Il remplace le panneau de fin actuel de `GameplayScene.Pvp.cs`, qui affiche « VICTOIRE », « +70 POINTS (TOTAL 70) », « COTE 950 > 975 BRONZE II », RETOUR AU MULTI et « CLIC POUR REVENIR AU MENU ».
Captures à l'échelle 1 (1280×720) dans `docs/maquettes/` : `multi-11-fin-victoire.png`, `multi-12-fin-defaite.png`, `multi-13-fin-egalite.png`. Les pseudos et les chiffres sont simulés.

## Fond

Le plateau du combat reste affiché, figé à la position finale, sous un voile `Black1` à 72 %.

## Fenêtre (600 px de large, centrée, haut à y = 36)

Habillage des panneaux : fond `Black4`, bordure 3 px `Brown2`, liseré 2 px `Brown1`, contour 4 px `Black1`. Marges intérieures de 24 px.

1. **Titre** très grand, espacé de 6 px : « VICTOIRE » en `Yellow2`, « DÉFAITE » en `Purple5`, « ÉGALITÉ » en `White`.
2. **Raison** en gris dessous, selon la fin de partie :
   - « COMMANDANT ADVERSE ÉLIMINÉ » / « TON COMMANDANT EST TOMBÉ » ;
   - « TEMPS ÉCOULÉ » (le tien ou celui de l'adversaire) ;
   - « ABANDON DE L'ADVERSAIRE » / « TU AS ABANDONNÉ » ;
   - « ADVERSAIRE DÉCONNECTÉ » ;
   - « ÉGALITÉ ACCEPTÉE » / « PLUS AUCUN CONTACT POSSIBLE » (position morte) / « DESTRUCTION MUTUELLE ».
3. **Face-à-face**, chaque côté sur une colonne de 200 px avec « VS » en `Brown1` au milieu :
   - à gauche, ton commandant : sprite `_front` ×2 (128×128), « TOI » en `Yellow2`, nom du commandant en gris ;
   - à droite, le commandant adverse : sprite `_ia_front` ×2, pseudo Steam en crème (étiquette « BOT » sur fond `Black5` si c'est un bot), nom du commandant en gris ;
   - le perdant est assombri (luminosité 55 %) ; en égalité, aucun des deux.
4. Filet de 2 px `Black5`.
5. **Deux colonnes** :
   - **CLASSEMENT** : icône de rang ×2 + nom du rang (couleur claire du palier) ; encadré « COTE  950 › 975  +25 » (variation en `Cyan1` si gain, `Purple5` si perte, gris si 0) ; « VERS BRONZE I » + « 75 / 100 » ; barre de 10 segments : la base dans la couleur claire du palier, **les segments gagnés en `Yellow2` encadrés `Brown2`**, **les segments perdus en `Purple5`**.
     En cas de montée ou de descente de division ou de palier, afficher « PROMU BRONZE I ! » en `Yellow2` (ou « RÉTROGRADÉ BRONZE III » en `Purple5`) à la place de « VERS … ».
     Défi entre amis : remplacer ce bloc par « PARTIE AMICALE · SANS EFFET SUR LE RANG ».
   - **POINTS DE COFFRE** : une ligne par gain (fond `Black3`, valeur en `Yellow2` à droite), par exemple « 6 PIONS TUÉS × 5 : +30 », puis « VICTOIRE : +30 » ou « ÉGALITÉ : +5 » (barème actuel 5 / 30 / 5, validé le 2026-11-06 ; les maquettes montrent l'ancien 2 / 15) (`PointsPerKill`, `PointsForWin`, `PointsForDraw` de `pvp.json`), puis l'encadré « TOTAL [pièce] 127 » (bordure `Brown1`, pièce 10×10 affichée ×2).
6. **Bandeau « UN COFFRE PEUT ÊTRE OUVERT »** (icône coffre 32×32, fond `Black5`, bordure `Yellow2`), seulement si les points permettent d'ouvrir au moins un coffre non vide. Il est cliquable et ouvre l'écran Coffres.
7. **Stats de la partie** : 4 cases (fond `Black3`, valeur en crème au-dessus, libellé gris dessous) : TOURS, DURÉE (m:ss, hors animations), PIONS TUÉS, PIONS PERDUS.
8. **Boutons** (hauteur 52, côte à côte) : **RETOUR À L'ARMÉE** (secondaire, retour à l'écran de build) et **REJOUER** (principal, retour au build avec la recherche relancée aussitôt, même armée). Manette : A = REJOUER, B = RETOUR.

## Animation

Commence 400 ms après l'ouverture de la fenêtre et dure 1,2 s : la cote, la barre de division et le total de points défilent de l'ancienne valeur à la nouvelle. Clic ou A pendant l'animation : l'animation s'arrête et les valeurs finales s'affichent.

## Données

Utiliser `PvpReward` (points, cote avant et après, classée ou non) et le résultat du `Match` : raison de fin, pions tués et perdus, nombre de tours, durée. L'historique des parties (`docs/multijoueur-classement.md`) est enregistré à ce moment-là.
Si une désynchronisation a eu lieu (empreintes différentes), garder le message rouge actuel à la place des blocs CLASSEMENT et POINTS DE COFFRE, car la partie ne rapporte rien.

## Mise en œuvre (2026-11-06)

- Code : `GameplayScene.PvpEnd.cs` (fenêtre, animation, entrées) ; `GameplayScene.Pvp.cs` n'appelle plus que `BeginPvpEndScreen` / `UpdatePvpEndScreen` / `DrawPvpEndPanel`.
- Données Core : `Match.EndReason` (`MatchEndReason`), `Match.Round` (tours façon échecs, chaque camp une fois), `PvpClock.Elapsed` (durée hors animations et déconnexion), `PvpProfile.CanOpenChest`. Le coup « Resign » vaut « temps écoulé » (seule une pendule tombée l'envoie aujourd'hui).
- REJOUER : `new PvpArmyScene(ctx, PvpArmyScene.Entry.Replay)` relance `OnLaunch` (défi entre amis : `ReplayFriend` rouvre le salon privé) ; bandeau coffre : `Entry.Chests`.
- Format serré (960×540) : mêmes blocs, texte ×1, titre ×4, sprites toujours ×2. Sans bandeau coffre, la fenêtre raccourcit.
- Captures (Debug, données simulées) : `ChessArmy.Game.exe -pvpendshots <dossier> [filtre]`.
