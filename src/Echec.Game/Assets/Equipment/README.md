# Icônes d'équipement

PNG **32×32** par équipement. Le nom de fichier = le champ `"icon"` de l'équipement dans
`src/Echec.Game/Assets/Config/equipment.json` (à défaut, son `"id"`).

Exemple : pour `{ "id": "vigueur", ... }` sans champ `icon`, dépose `vigueur.png` ici.

## Repli automatique

Tant que le PNG n'existe pas, le jeu dessine un **placeholder** (aplat coloré selon le type —
bleu = stat, or = trait — avec l'initiale du nom). Tu peux donc ajouter les icônes progressivement.

## Rendu

L'icône est rendue en **pixel-perfect** : jamais d'agrandissement fractionnaire. Dans un emplacement
plus petit que 32 px, elle est réduite à une échelle **entière** (1/2, 1/3…) via `DrawSpriteFit`.
Garde donc l'art lisible en 32×32 (et idéalement en 16×16).
