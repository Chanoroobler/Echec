Revois la fenêtre « NOUVEAU ! » qui s'affiche à l'ouverture d'un coffre multi. Aujourd'hui, le texte sous l'objet est centré et empilé : « +4 PUISSANCE », « +6 PV », « ESQUIVE » puis la description du trait. On ne voit pas ce qui est un bonus, ce qui est un trait et ce qui est sa description.

Refais-la exactement comme dans `docs/multijoueur-interface.md`, section « Écran Coffres », sous-partie « fenêtre de révélation ». Les captures de référence sont `docs/maquettes/multi-5-coffre-recompense.png` (équipement), `multi-5b-coffre-recompense-pion.png` (pion à 3 traits) et `multi-5c-coffre-recompense-commandant.png`, à l'échelle 1 en 1280×720.

En résumé :

- **Sous le nom** : la ligne « ÉQUIPEMENT · LÉGENDAIRE » (la rareté en couleur), puis un filet.
- **Bloc « BONUS DU PION ÉQUIPÉ »** : une ligne par bonus, avec l'icône de stat 32×32 native, le libellé à gauche et la valeur « +4 » en `Yellow2` à droite. Pour un pion ou un commandant, le bloc s'appelle « STATISTIQUES » et montre les 4 stats en grille 2×2.
- **Bloc « TRAIT DONNÉ AU PION »** (« TRAIT » / « TRAITS » pour un pion) : un encadré par trait, avec le nom du trait en haut (`Yellow2` s'il vient d'un équipement, `Cyan1` sinon) et la description dessous, alignée à gauche (`kw.*.desc`).
- **Taille de l'objet** : ×6 pour un équipement, ×3 pour un pion ou un commandant, ×2 si l'objet a 3 traits ou plus. Tout doit tenir dans 720 px de haut.
- **Textes** : les titres de bloc vont dans `strings.csv`, toutes les langues. Pixel perfect et aucun débordement, toutes langues comprises.

Montre-moi une capture des 3 cas : un équipement avec un trait, le Paladin et un commandant.
