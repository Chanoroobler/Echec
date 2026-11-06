Ajoute l'écran CLASSEMENT du multijoueur décrit dans `docs/multijoueur-classement.md`. Lis-le en entier, ainsi que `docs/multijoueur-interface.md` pour l'habillage et la barre du haut. Les captures `docs/maquettes/multi-7` à `multi-9` sont à la résolution virtuelle du jeu (1280×720, échelle 1) : reproduis-les fidèlement. Les données de ces captures sont simulées.

Ce qu'il faut faire :

1. **Barre du haut** : ajoute le bouton CLASSEMENT entre les points et COFFRES, et l'icône de rang dans la pastille de rang. RETOUR revient au build d'armée.
2. **Icône de rang** : je fournis une seule image `Assets/Icons/rang.png` de 10×12 px en niveaux de gris. Recolore-la par palier avec les couleurs du tableau du document (teinte `SpriteBatch`). Affiche-la uniquement à ×2 (20×24) et à ×8 (80×96). Tant que l'image n'existe pas, dessine un repli au même format.
3. **Panneau MON RANG** : rang, cote, place mondiale, progression vers la division suivante (calculée avec `PvpRanks`), et les 8 statistiques.
4. **Panneau PALIERS** : les 5 rangs avec leurs plages lues dans `pvp.json`, et le palier courant mis en évidence.
5. **Classement** : onglets MONDIAL (8 par page, pagination, ma ligne épinglée en bas si je ne suis pas sur la page), AMIS et AUTOUR DE MOI, avec les colonnes, couleurs et hauteurs du document. Le panneau a une hauteur fixe, quel que soit l'onglet.
6. **Données** :
   - utilise les leaderboards Steam : un leaderboard trié décroissant, la cote envoyée après chaque partie classée (pas les défis entre amis), et le téléchargement Global, Friends et GlobalAroundUser ;
   - hors ligne, affiche « CLASSEMENT INDISPONIBLE HORS LIGNE » dans le cadre du classement ;
   - ajoute à `PvpProfile` l'historique des 20 dernières parties, la meilleure cote, la série en cours et la variation sur 7 jours, dans le fichier chiffré existant, avec une migration propre des anciens profils.
7. **Dernières parties** : les 4 plus récentes, au format du document.
8. **Textes** : uniquement via `strings.csv`, toutes les langues.

Contraintes :

- **Pixel perfect obligatoire** : tailles natives ou multiples entiers, `SamplerState.PointClamp`, la police du jeu, `Palette` et `UiStyle`.
- **Aucun texte qui déborde** : pseudos Steam longs tronqués « … », toutes les langues.
- **Manette** : `PvpFocus` sur les onglets, la pagination et RETOUR. LB/RB changent d'onglet.
- Compile et lance les tests. Ajoute des tests Core pour l'historique (taille max, ordre) et pour la progression de division.

Avant de coder, propose-moi ton plan, en particulier pour Steam et la migration du profil. Ensuite, montre-moi une capture du jeu à chaque étape importante.
