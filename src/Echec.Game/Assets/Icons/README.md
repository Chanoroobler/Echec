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
