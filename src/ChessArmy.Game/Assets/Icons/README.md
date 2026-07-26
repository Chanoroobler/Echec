# Icônes de carte d'unité

PNG chargés à la volée par `GameplayScene` (placeholder dessiné tant que le fichier est absent,
remplacé automatiquement dès qu'il est présent). Copiés à côté de l'exe via le `.csproj`
(`Assets\**\*.*`, `CopyToOutputDirectory="Always"`).

## Icônes de domaine (déplacement) — 39×39

Affichées sous le sprite du pion. Nom : `domaine_<domaine>.png` (en minuscules).

- `domaine_pion.png`
- `domaine_fou.png`
- `domaine_cavalier.png`
- `domaine_tour.png`
- `domaine_dame.png`

## Icônes de caractéristique — 32×32

Affichées à gauche de chaque ligne de stat. Nom : `stat_<clé>.png`.

- `stat_deg.png` — puissance (dégâts)
- `stat_dep.png` — mouvement (déplacement)
- `stat_tir.png` — portée (tir)

## Icônes de mission (frise de phase) — 32×32

Une icône par nœud de la frise chronologique en haut de l'écran (les missions de la phase
en cours). Nom : `mission_<type>.png` (en minuscules), où `<type>` est la nature du combat.
Sans PNG, un placeholder procédural est dessiné (épées croisées / étincelle / gemme).

- `mission_escarmouche.png` — combat classique (tuer toutes les unités ennemies)
- `mission_speciale.png` — mission spéciale
- `mission_boss.png` — combat de boss
