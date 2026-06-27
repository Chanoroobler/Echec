# Tilesets

Un **tileset** = une image PNG contenant plusieurs tuiles en grille, déposée ici
(`Assets/Tilesets/<nom>.png`). Le jeu dessine chaque tuile **directement depuis la feuille**
(rectangle source) — pas besoin de la découper en fichiers séparés.

## Convention

- Grille **régulière**, **64×80 par cellule** (64 surface + 16 épaisseur), **sans espacement**
  entre les cellules (cellule (col,row) = pixels `[col*64 .. col*64+64] × [row*80 .. row*80+80]`).
- Le mapping cellule → tuile se déclare dans `Assets/Tiles/tiles.json` :
  - section `tilesets` : chaque feuille = `{ "file": "<nom>.png", "cellW": 64, "cellH": 80 }` ;
  - chaque tuile reçoit `sheet` + `col` + `row` (0,0 = cellule en haut à gauche).
- Une tuile **sans** `sheet` est cherchée en PNG individuel `Assets/Tiles/<id>.png`
  (repli placeholder). C'est le cas des 3 tuiles historiques `grass`/`water`/`mountain`.

## Ajouter un tileset

1. Dépose `Assets/Tilesets/<nom>.png`.
2. Donne l'ordre des cellules (ligne par ligne) ; j'ajoute la feuille + les `sheet`/`col`/`row`
   correspondants dans `tiles.json`.
