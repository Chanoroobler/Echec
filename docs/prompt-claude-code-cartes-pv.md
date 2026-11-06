Mets à jour toutes les cartes d'information des unités pour que les PV s'affichent comme les autres stats, avec la nouvelle icône `Assets/Icons/stat_pv.png` (cœur, 32×32, même cadre que `stat_deg`, `stat_dep` et `stat_tir`).

Aujourd'hui, `UnitCardRenderer.DrawCard` dessine une barre de PV « 1 carré = 1 PV » (`DrawHpBar`) suivie du texte « 12/12 », puis les 3 lignes de stats (PUISSANCE, MOUVEMENT, PORTÉE) avec leur icône.

À faire :

1. Dans `UnitCardRenderer.DrawCard`, remplace le bloc barre de PV + « 12/12 » par une **ligne de stat PV**, placée en première position, au-dessus de PUISSANCE : `DrawStat(sb, rect, y, "pv", Loc.T("stat.hp"), …)`. Mêmes dimensions, alignements et espacements que les 3 autres lignes, et la valeur en `Palette.White`. Ajoute la clé `stat.hp` (« PV ») dans `strings.csv`, toutes les langues, si elle n'existe pas.
2. Fais pareil sur **toutes les autres cartes et fiches** qui affichent les stats d'une unité : la carte de combat de `GameplayScene` (survol d'un pion allié ou ennemi), le Codex (`CodexView`), le recrutement, la fiche du commandant (`CommanderSelectScene`), et tout autre appel à `DrawHp` / `DrawStatRow`. Cherche-les tous.
   - En combat, la valeur s'écrit « 8/12 » (PV actuels / PV max), en `Purple5` si l'unité est blessée, en blanc sinon.
   - Si un bonus (équipement, arbre) modifie les PV max, la valeur s'écrit dans la même couleur que les autres stats bonifiées sur cette carte.
3. La barre de PV au-dessus des pions SUR LE PLATEAU ne change pas : seules les cartes et fiches d'information sont concernées.
4. Garde la même hauteur totale de carte. La barre et le texte « 12/12 » prenaient environ 30 px et la ligne de stat en prend 36, alors vérifie que les mots-clés ancrés en bas ne chevauchent rien. Si ça ne tient pas, augmente `CardH` de la différence et vérifie tous les endroits qui positionnent une carte (`DrawCardNear`, etc.).
5. Pixel perfect : icône 32×32 native, `PointClamp`. Si `stat_pv.png` est absent, garde le repli actuel de `DrawStat` (carré + initiale).

Compile, lance les tests, puis montre-moi une capture de chaque carte modifiée : combat (pion intact et pion blessé), Codex, recrutement et fiche du commandant.
