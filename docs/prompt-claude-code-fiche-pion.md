Revois la fiche affichée sous la réserve de l'écran multi, au survol d'un pion de la réserve, d'un pion du plateau ou du commandant. La nouvelle disposition est décrite dans `docs/multijoueur-interface.md` (section « Réserve (droite) », partie « Fiche »), et la capture de référence est `docs/maquettes/multi-1-build-pions.png` (1280×720, échelle 1).

Changements :

1. **Retire le nom et le coût** de la fiche : ils sont déjà sur la carte de la réserve.
2. **En haut, côte à côte** :
   - à gauche, le plateau 7×7 animé (`MoveAttackGrid.DrawBoardWithPawn`, 115×115, pion en couche écran) ;
   - à droite, les **4 stats empilées** : PV, PUISSANCE, MOUVEMENT, PORTÉE. Chaque ligne fait 28 px de haut sur fond `Black3`, avec l'icône `stat_pv` / `stat_deg` / `stat_dep` / `stat_tir` 32×32 native, le libellé en gris, le bonus « +5 » en `Yellow2` et la valeur alignée à droite (en `Yellow2` si un équipement la modifie).
3. **Dessous, sur toute la largeur du panneau** : l'encart de l'équipement porté, puis chaque trait avec son nom (`Cyan1`, ou `Yellow2` s'il vient de l'équipement) et sa description complète (`kw.*.desc`). La zone défile si ça ne tient pas.
4. **Fiche du commandant** : même disposition, avec ses stats multi.

Contraintes : pixel perfect, aucun débordement dans toutes les langues, textes via `strings.csv`. Montre-moi une capture du Garde équipé, du Paladin (3 traits) et du commandant.
