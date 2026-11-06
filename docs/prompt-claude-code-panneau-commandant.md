Revois le panneau COMMANDANT à gauche de l'écran multi, sous le portrait et le nom. La référence est `docs/maquettes/multi-10-panneau-commandant.png` (1280×720, échelle 1), décrite dans `docs/multijoueur-interface.md`, section « Panneau commandant (gauche) », points 4 et 5.

Changements :

1. **Retire l'icône de domaine et le texte « DÉPLACEMENT / DAME · 8 DIRECTIONS »**. Mets à la place, à gauche, le **plateau 7×7 animé des tooltips** : `MoveAttackGrid.DrawBoardWithPawn` (115×115), avec le sprite `_front` du commandant en couche écran et ses portées multi.
2. **Remplace les 4 barres segmentées** par les 4 stats empilées à droite du plateau, comme dans la fiche des pions. Chaque ligne fait 28 px de haut sur fond `Black3`, avec l'icône `stat_pv` / `stat_deg` / `stat_dep` / `stat_tir` 32×32 native, le libellé en gris et la valeur à droite (PV crème, PUISSANCE `Brown3`, MOUVEMENT `Cyan1`, PORTÉE `Yellow2`).
3. **Dessous**, chaque trait du commandant avec son nom (`Cyan1`) et sa description complète (`kw.*.desc`), à la place des étiquettes. La zone défile si ça ne tient pas.
4. **Reste du panneau inchangé** : portrait ×3, flèches, nom, points de pagination. Garde aussi « COMMANDANT … CASE FIXE » en haut.

Réutilise le même code que la fiche des pions (plateau + lignes de stats), sans le dupliquer. Pixel perfect, aucun débordement dans toutes les langues. Montre-moi une capture du Foudroyeur, du Bastion et du Maréchal.
