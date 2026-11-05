# Multijoueur : état de préparation du code

Faisabilité : **oui**. Le combat se prête au **lockstep** : les deux clients construisent le même `Match`
avec la même graine, puis s'échangent uniquement les coups (5 octets chacun). Chacun rejoue les coups de
l'autre et vérifie l'empreinte d'état. Le tour par tour permet de se passer de serveur et de synchronisation d'état.

Le game design (armées, classement, temps, cartes) n'est pas encore décidé. Ce document ne couvre que le code.

## Game design décidé (2026-11-05)

Réponses de l'auteur au questionnaire de cadrage. Elles font référence pour toutes les étapes.

**Ladder et rangs**
- Cote cachée (type Elo) pour le matchmaking ; rangs Bronze, Fer, Argent, Or, Master affichés comme paliers de
  cette cote, **avec divisions** (Argent III, II, I).
- Stockage **local**, mais données **obscurcies et encodées** (pas de JSON lisible ni modifiable à la main).
- Valeurs sensibles **obscurcies aussi en mémoire** (anti-triche par édition de la RAM, type Cheat Engine) :
  points de coffre, cote, rang, déblocages. Type dédié (valeur chiffrée par une clé aléatoire + contrôle
  d'intégrité), jamais la valeur en clair dans un champ.
  - Contre un **humain**, les valeurs de combat (PV, dégâts…) sont déjà protégées : une modification locale
    diverge de l'adversaire et déclenche la désynchronisation.
  - Contre un **bot**, il n'y a personne pour vérifier : le résultat de la partie sera revalidé en rejouant le
    journal des coups (graine + coups) avant d'accorder points et rang, plutôt que de chiffrer les PV dans le
    moteur (qui coûterait cher à chaque calcul).
- Recherche selon l'affluence : rang proche quand il y a du monde, élargie quand il y a peu de joueurs.
- Défis entre amis : **points de coffre seulement**, aucun effet sur le rang.

**Bot de secours (aucun adversaire au bout de 40 s)**
- Affiché comme bot : nom suivi de « (BOT) », et si son armée est copiée sur un vrai joueur, on dit laquelle.
- Une partie contre un bot **compte pour le rang et les points**. Le mode doit marcher même avec très peu de
  joueurs : bots **de plus en plus durs** selon le rang (IA de plus en plus intelligente, pas seulement plus précise).
- Armées des bots : **armées par défaut écrites à la main par rang** pour commencer ; armées copiées sur des
  joueurs de même rang plus tard (stockage partagé nécessaire).

**Composition de l'armée**
- Budget **unique pour tous**. Chaque pion et chaque équipement coûte des points (équipement selon sa rareté).
- Cases vides permises, doublons permis : seul le budget limite. Deux lignes de 8 cases.
- Commandant obligatoire et **gratuit** (hors budget). Sa case est **fixe au placement** (centre de la ligne du
  fond) ; il bouge normalement en combat. Sa mort = défaite.
- **1 équipement par pion, commandant exclu.**
- 3 builds sauvegardables ; la map est montrée, puis **30 s** pour choisir son build. Le build adverse reste caché.

**Commandants et progression**
- Ce sont **les commandants du solo** ; trois sont débloqués d'office : le Foudroyeur (`commandant`), le Bastion
  (`Commandant_lancier`) et le Maréchal (`Commandant_cavalier`). Les autres s'obtiennent par coffre.
- Stats propres au multi, sans arbre de compétences, en respectant l'archétype (Bastion = tank…).
- Progression **séparée** du solo. Départ : pions T1 + les 3 commandants.
- Deux coffres : **pions** (T2, T3, commandants) et **équipements**. Jamais de doublon. Tout pion obtenu est
  jouable directement (un T3 sans son T2). Catégorie entièrement débloquée = coffre grisé, points gardés.
- Gains : chaque pion tué (le perdant garde les siens) + bonus de victoire. **Pas de plafond** par jour.
  Points de départ suffisants pour ouvrir un coffre de chaque type (tuto multi).
- Barème (budget, coûts, prix des coffres, gains) : proposé par Claude dans un JSON, réglé ensuite.

**En partie**
- Réserve de **10 min** par joueur + **1 min max par coup** ; dépasser l'un ou l'autre = défaite.
  Le chrono est **arrêté pendant les animations**.
- Égalité : **proposition d'égalité** (l'autre accepte ou refuse) et **détection automatique** quand plus aucune
  pièce ne peut en atteindre une autre.
- Premier joueur **tiré au sort**.
- Déconnexion : **60 s** pour revenir, puis défaite.

## Implémentation (2026-11-05)

Bouton **MULTIJOUEUR** au menu principal, visible dans les builds PLAYTEST et Debug uniquement
(`PvpInvites.Compiled`). Le barème est dans `Assets/Config/pvp.json` (budget, coûts, coffres, gains, temps, rangs,
stats des commandants, bots).

### Parcours d'écrans

| Écran | Fichier | Rôle |
|---|---|---|
| Accueil multi | `Game/Scenes/PvpLobbyScene.cs` | Profil (rang, cote, points, bilan), PARTIE CLASSÉE, DÉFIER UN AMI, MES ARMÉES, COFFRES ; tuto à la première visite ; recherche et attente |
| Choix d'armée | `Game/Scenes/PvpPrepScene.cs` | Aperçu de la map tirée, choix d'une des 3 armées en 30 s, échange des armées |
| Combat | `GameplayScene` + `GameplayScene.Pvp.cs` | Pendules, égalité, déconnexion, gains, panneau de fin |
| Mes armées | `Game/Scenes/PvpBuildScene.cs` | Composer les 3 armées : commandant, pions, équipements, budget, corbeille |
| Coffres | `Game/Scenes/PvpChestScene.cs` | Coffre de pions et coffre d'équipements, sans doublon |

Sans Steam (lancement depuis Visual Studio), PARTIE CLASSÉE lance directement une partie contre un bot.

### Moteur (Core)

| Brique | Rôle |
|---|---|
| `Match` (`mirrorRows`, `pvpRules`) | Le client RETOURNÉ parcourt le plateau dans l'ordre du repère commun (`Units()`, voisins, points de chute, tirages au sort) : les deux clients résolvent chaque coup à l'identique, pour toutes les classes et tous les traits. Destruction mutuelle = nulle |
| `Match.IsDeadPosition` | Égalité automatique : plus aucun contact possible entre les deux camps, même en plusieurs coups (estimation prudente) |
| `BattleAction` Resign / AgreeDraw | Fins de partie (temps écoulé, abandon, égalité acceptée), journalisées comme les coups |
| `PvpSetup` | Création du plateau, pose des armées, règles de fin de coup (Séisme symétrique, position morte), rejeu, maps |
| `Pvp/PvpConfig` | Lecture de `pvp.json` |
| `Pvp/PvpBuild` + `PvpCatalog` | Armée (commandant + pions + équipements), coût, validation, commandants aux stats multi |
| `Pvp/PvpRanks` | Cote Elo, rangs à divisions |
| `Pvp/PvpProfile` | Profil multi : points, cote, bilan, déblocages, 3 armées ; fichier chiffré (AES) et signé (HMAC), lié au compte Steam |
| `Pvp/Obscured` | `ObscuredInt` / `ObscuredSet` : valeurs jamais en clair en mémoire, toute retouche détectée |
| `Pvp/PvpClock` | Réserve 10 min + 1 min par coup |
| `Pvp/PvpBotAi` | Bot de base (IA de campagne, précision selon le rang) ou tactique (évalue chaque coup) |

### Réseau (Game/Net)

| Brique | Rôle |
|---|---|
| `PvpMatchmaker` | Recherche par cote : lobbies triés par proximité, écart toléré qui s'élargit avec l'attente ; lobby privé pour les défis |
| `PvpSession` | Protocole P2P : bonjour/départ (graine + cotes), armées, coups (+ empreinte + réserve), égalité, battements de cœur, départ |
| `PvpInvites` | Invitations Steam acceptées (jeu lancé ou `+connect_lobby`) |
| `PvpProfileStore` | Fichier `%AppData%/Echec/save/pvp_<steamid>.dat` ; un fichier retouché est mis de côté (`.rejete`) |
| `PvpMatchInfo` | Description d'une partie (humain ou bot, armées, graine, premier joueur, classée ou non) |

### Règles appliquées

- **Premier joueur** : tiré de la graine de l'hôte (`PvpSetup.HostStarts`), connu des deux.
- **Pendules** : ne tournent que plateau posé (jamais pendant les animations ni une déconnexion). Chacun fait foi
  pour sa propre pendule et s'annonce perdant (`Resign`) ; la réserve part avec chaque coup pour recaler l'affichage
  adverse. Un adversaire dont la pendule tombe et qui ne se déclare pas est déclaré perdant 15 s plus tard.
- **Égalité** : bouton (ou Y) ; l'adversaire accepte ou refuse. Un bot accepte s'il est nettement en retard au
  matériel. Égalité automatique en position morte ou destruction mutuelle.
- **Déconnexion** : au-delà de 4 s de silence, compte à rebours de 60 s (pendules arrêtées), puis victoire.
  Quitter (menu pause) = départ immédiat = défaite, comptée même si on ferme la scène (anti rage-quit).
- **Gains** : points par pion tué (perdant compris) + bonus de victoire ou de nulle. Cote Elo si la partie est
  classée (pas pour les défis entre amis). Contre un bot, la partie est REJOUÉE depuis la graine et le journal
  des coups avant tout gain : un état final différent (mémoire modifiée) = partie non validée.
- **Désynchronisation** (empreintes différentes) : signalée en rouge, et la partie ne rapporte rien.

### Maps multi

Type de map `Multi` (`CombatType.Multi`), créé dans l'éditeur par le bouton **Nouveau multi** (échiquier 8×8,
armées marquées en E/P à titre indicatif). Map par défaut : `multi_8_01.json`. Le jeu tire la map par la graine de
l'hôte, parmi les maps Multi jouables (hors brouillons, triées par nom). Jouable = 8×8 et deux lignes
franchissables de chaque côté. **Symétrique haut/bas** recommandé : l'invité la voit alors comme l'hôte ; sinon il
la joue retournée (règles justes, tuiles orientées à l'envers). L'éditeur affiche « Sym » / « Ret » et le récap a
une section MULTI. Seuls le terrain et les buissons comptent.

### Tests (`tests/ChessArmy.Core.Tests/PvpTests.cs`)

- 60 parties au hasard entre un hôte et un invité retourné, armées tirées dans TOUTES les classes, commandants et
  équipements : empreintes identiques à chaque coup.
- Rejeu identique à la partie jouée ; abandon et égalité retournés ; position morte ; budget, armées des bots ;
  rangs et Elo ; profil chiffré (retouche et autre compte refusés) ; coffres sans doublon ; `ObscuredInt` qui
  détecte une retouche mémoire ; bot tactique qui prend le roi.

### Limites connues

- Tout est stocké côté client : ces protections arrêtent la triche facile, pas un tricheur déterminé (il faudrait un serveur).
- La reconnexion couvre une coupure réseau ; un joueur dont le jeu plante ne peut pas reprendre la partie.
- Les bots utilisent des armées écrites à la main par rang ; copier les armées de vrais joueurs demande un stockage partagé.
- Tester à deux demande deux comptes Steam sur deux PC.
