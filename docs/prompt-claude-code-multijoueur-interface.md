Mets en place la nouvelle interface du multijoueur décrite dans `docs/multijoueur-interface.md` (lis-le en entier, ainsi que `docs/multijoueur.md` pour les règles déjà en place). Les captures de référence sont dans `docs/maquettes/multi-*.png` : elles sont à la résolution virtuelle du jeu (1280×720, échelle 1), donc les positions et tailles se lisent directement dessus. Regarde-les avant de coder et reproduis-les fidèlement.

Ce qu'il faut faire :

1. **Écran unique Armée** : le bouton MULTIJOUEUR du menu principal mène directement au build d'armée (plus de hub). Fusionne `PvpLobbyScene` (profil, recherche, défi entre amis, invitations, tuto) et `PvpBuildScene` (armées, budget, placement) en un seul écran, avec la disposition du document : panneau commandant à gauche avec portrait ×3 et barres de stats multi, le vrai plateau multi 8×8 au centre (tuiles de la map Multi, sprites `_back`), la réserve paginée PIONS / ÉQUIPEMENTS à droite avec la fiche, et la bande du bas avec le budget et le bouton de lancement.
2. **Placement** : glisser-déposer à la souris pour poser, déplacer, échanger et retirer les pions, ainsi que pour équiper, déplacer et retirer les équipements (badge 32×32 en bas à droite de la case). Garde l'équivalent au clic et à la manette (`PvpFocus`). Respecte le tableau des interactions : l'équipement suit son pion, un échange échange aussi les équipements, rien sur la case du commandant, pas d'équipement sur le commandant, et le budget compte pions + équipements.
3. **Pagination** de la réserve : 8 cartes par page (4×2), boutons ‹ ›, points cliquables, compteur « 1/2 », page mémorisée par onglet, molette, LB/RB à la manette.
4. **Lancement** : tout est classé, donc pas de bouton « Partie classée ». LANCER UNE PARTIE démarre la recherche sans quitter l'écran, et l'armée reste modifiable pendant ce temps. Gère les états recherche / annuler / bot après `BotAfterSeconds` / adversaire trouvé : armée verrouillée, 5 s, puis combat avec l'armée sélectionnée sans passer par `PvpPrepScene`. Garde DÉFIER UN AMI en bascule et les invitations Steam.
5. **Barre du haut** : rang, cote, bilan à gauche. À droite, les points avec une icône de pièce, juste à gauche du bouton COFFRES, puis RETOUR.
6. **Écran Coffres** refait selon le document : deux panneaux, contenu en silhouettes, bouton OUVRIR avec le prix, « IL MANQUE X POINTS », animation `coffreAnimate.png`, puis la fenêtre « NOUVEAU ! ». Ce qui est obtenu est disponible tout de suite dans le build.
7. **Textes** : uniquement via `strings.csv`. Ajoute les nouvelles clés dans toutes les langues.

Contraintes :

- **Pixel perfect obligatoire** : chaque sprite et chaque icône à sa taille native ou à un multiple entier, `SamplerState.PointClamp`, aucune mise à l'échelle fractionnaire, la police du jeu. Utilise `Palette` et `UiStyle` existants, avec l'habillage des panneaux de l'écran de sélection du commandant.
- **Aucun texte qui déborde** : vérifie le nom et les traits de chaque commandant (le Maréchal a deux traits), les noms longs de pions et d'équipements (troncature « … »), et toutes les langues.
- Ne change pas les règles du multi, le réseau ni le format de sauvegarde (`PvpProfile`, `PvpBuild`) au-delà de ce qu'il faut pour l'interface.
- Lance la compilation et les tests existants (`tests/ChessArmy.Core.Tests`). Ajoute des tests Core pour la logique de placement si tu la sors de la scène (déplacement avec équipement, échange, budget, refus sur la case du commandant).
- Mets à jour `docs/multijoueur.md` (tableau « Parcours d'écrans ») à la fin.

Avant de coder, propose-moi ton plan : les scènes ou classes créées ou supprimées, et ce que tu fais de `PvpPrepScene`. Ensuite, fais-le étape par étape et montre-moi une capture du jeu à chaque étape importante.
