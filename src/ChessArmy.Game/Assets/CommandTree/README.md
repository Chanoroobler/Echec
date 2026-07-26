# Icônes de l'arbre de commandement

Un PNG **32×32** par nœud, nommé d'après le champ `icon` du nœud dans
`Assets/Config/commander_trees.json` (par défaut : l'`id` du nœud).

Les icônes sont dessinées **à leur taille native**, jamais redimensionnées (règle pixel-perfect du
projet). Un fichier manquant n'est pas une erreur : le jeu retombe sur un aplat coloré par branche
portant l'initiale du nœud.

Les fichiers actuels sont des **placeholders** générés (pixel art 16×16 mis à l'échelle ×2), à
remplacer par l'art définitif. Codes couleur repris de la palette maîtresse, par branche :

| Branche | Rôle | Teinte | Rehaut | Ombre |
|---|---|---|---|---|
| 0 | Commandant | `#e8b26f` | `#f5d893` | `#b6834c` |
| 1 | Troupes | `#699fad` | `#ede6cb` | `#2b454f` |
| 2 | Logistique | `#9a9f87` | `#ede6cb` | `#4f5d42` |

Contour commun : `#111215`. Fond transparent.

`point.png` fait exception : c'est le jeton **8×8** des points de commandement, accolé à chaque total de
points (en-tête de l'arbre, bouton COMMANDEMENT du panneau de placement). Absent, il retombe sur un
losange plein doré.

Le libellé et la description d'un nœud ne sont **pas** dans l'icône : ils vivent dans
`Assets/Config/strings.csv` (clés `tree.<id>` et `tree.<id>.desc`) et n'apparaissent qu'en infobulle.
