Refais l'écran de fin de partie du multijoueur comme décrit dans `docs/multijoueur-fin-de-partie.md`. Lis-le en entier. Les captures de référence sont `docs/maquettes/multi-11-fin-victoire.png`, `multi-12-fin-defaite.png` et `multi-13-fin-egalite.png`, en 1280×720 à l'échelle 1 : reproduis-les fidèlement. Les données de ces captures sont simulées.

Aujourd'hui, le panneau de fin (`GameplayScene.Pvp.cs`) n'affiche que le titre, « +70 POINTS (TOTAL 70) », la cote et un bouton RETOUR AU MULTI, avec « CLIC POUR REVENIR AU MENU ».

Ce qu'il faut faire :

1. Plateau du combat figé en fond, sous un voile, et fenêtre de 600 px de large avec, dans l'ordre :
   - le titre (VICTOIRE / DÉFAITE / ÉGALITÉ) et la raison de fin, toutes celles listées dans le document ;
   - le face-à-face des commandants (`_front` ×2 pour toi, `_ia_front` ×2 pour l'adversaire, perdant assombri, étiquette BOT) ;
   - le bloc CLASSEMENT : rang, cote animée « 950 › 966 +16 », barre de division avec les segments gagnés en jaune et perdus en rouge, promotion ou rétrogradation. Pour un défi entre amis, affiche « PARTIE AMICALE » à la place ;
   - le bloc POINTS DE COFFRE : détail des gains depuis `pvp.json`, puis le total avec la pièce ;
   - le bandeau « UN COFFRE PEUT ÊTRE OUVERT », cliquable, qui mène à l'écran Coffres ;
   - les 4 stats de la partie : tours, durée, pions tués, pions perdus ;
   - les boutons RETOUR À L'ARMÉE et REJOUER. REJOUER revient au build et relance aussitôt la recherche avec la même armée.
2. L'animation de la cote, de la barre et des points (1,2 s après 400 ms). Un clic ou A pendant l'animation l'arrête et affiche les valeurs finales.
3. Les données : `PvpReward` et le résultat du `Match`. Si quelque chose manque (raison de fin, tours, durée hors animations, pions perdus), ajoute-le côté Core, avec un test. Enregistre l'historique de la partie à ce moment-là si l'écran Classement est déjà en place.
4. En cas de désynchronisation, garde le message rouge actuel à la place des blocs CLASSEMENT et POINTS DE COFFRE.
5. Le total de points doit correspondre au barème de `pvp.json` : 2 par pion tué, +15 pour une victoire, +5 pour une égalité. Aujourd'hui, l'écran affiche +70 pour une victoire. Vérifie d'où vient cet écart et dis-moi ce que tu trouves avant de changer le calcul.
6. Les textes passent uniquement par `strings.csv`, dans toutes les langues.

Contraintes : pixel perfect (sprites ×2, icônes natives, `PointClamp`, police du jeu, `Palette` / `UiStyle`), aucun débordement dans toutes les langues (pseudos longs tronqués « … »), manette (A = REJOUER, B = RETOUR).

Avant de coder, propose-moi ton plan. Ensuite, montre-moi une capture de la victoire, de la défaite, de l'égalité, d'une partie contre un bot et d'un défi entre amis.
