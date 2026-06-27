# Tuiles (sprites de terrain)

Chaque tuile est un **PNG 64×80** :
- largeur **64** px
- hauteur **80** px = surface jouable **64×64** + **16 px** d'épaisseur (en bas du sprite)

L'épaisseur est la « tranche » de la tuile, dessinée sous la surface : le plateau se
rend de l'arrière vers l'avant, donc la rangée de devant recouvre l'épaisseur de la
rangée de derrière. Toute tuile doit avoir cette épaisseur pour que l'empilement marche.

## Catalogue

Le fichier **`tiles.json`** est la source de vérité : il liste chaque tuile par son `id`,
avec ses règles de jeu (`blocksMove`, `blocksFire`). Le PNG attendu est `Assets/Tiles/<id>.png`.
Tant qu'un PNG est absent, le jeu affiche une tuile placeholder générée (voir
`Echec.Engine/Rendering/Textures.cs`).

- `blocksMove` : on ne peut ni s'arrêter ni passer sur la tuile (mur, eau).
- `blocksFire` : la tuile coupe la ligne de tir (mur). L'eau laisse passer le tir.

## Légende des maps (clés `key`)

Chaque tuile a une **clé** (champ `key` dans `tiles.json`) à utiliser dans la grille `tiles`
des maps (`Assets/Maps/*.json`). C'est la **légende globale** : pas besoin de la redéclarer
dans chaque map.

| Clé | id | Marchable | Tir | | Clé | id | Marchable | Tir |
|---|---|---|---|---|---|---|---|---|
| `1` | damier_clair | oui | oui | | `\|` | mur_vertical | non | non |
| `2` | damier_clair_og | oui | oui | | `_` | mur_bas | non | non |
| `3` | damier_clair_odb | oui | oui | | `[` | mur_angle_bg | non | non |
| `4` | damier_clair_ogb | oui | oui | | `]` | mur_angle_bd | non | non |
| `5` | damier_sombre | oui | oui | | `=` | mur_muret_dg | non | non |
| `6` | damier_sombre_og | oui | oui | | `h` | herbe | oui | oui |
| `7` | damier_sombre_od | oui | oui | | `t` | herbe_oh | oui | oui |
| `8` | damier_sombre_ogd | oui | oui | | `r` | herbe_od | oui | oui |
| `~` | eau_coin_hg | non | oui | | `l` | herbe_og | oui | oui |

> Légende historique : `grass.png`, `water.png`, `mountain.png` étaient les 3 tuiles de
> l'ancien système (terrain aléatoire). Les nouvelles tuiles passent toutes par `tiles.json`.
