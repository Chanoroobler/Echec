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

## Déjà en place (2026-11-05)

| Brique | Où | Rôle |
|---|---|---|
| `BattleAction` | `Core/Battle/BattleAction.cs` | Un coup sous forme de données (type + 2 cases), sérialisable sur 5 octets sans allocation |
| `Match.Apply(action)` | `Core/Battle/Match.cs` | Point d'entrée unique d'un coup dans le moteur |
| `Match.ComputeStateHash()` | `Core/Battle/Match.cs` | Empreinte déterministe de l'état (FNV-1a), pour détecter une désynchronisation |
| Aléa unique | `Match` + `EnemyAi` | Tout l'aléa du combat, IA comprise, vient de la graine du `Match` |
| `IBattleController` | `Core/Battle/BattleController.cs` | Décide des coups d'un camp. `AiBattleController` aujourd'hui, un contrôleur distant demain |
| `ExecuteAction` | `GameplayScene` | Point d'entrée unique côté scène (souris, manette, IA), avec FX. Chaque coup validé va dans `_battleLog` |
| `_combatSeed` | `GameplayScene` | Graine neuve à chaque combat. Graine + `_battleLog` suffisent pour rejouer le combat |
| Tests | `tests/.../LockstepTests.cs` | Un combat IA rejoué depuis graine + coups sérialisés retrouve le même état à chaque coup |

## POC PvP (2026-11-05)

Bouton **MULTIJOUEUR** au menu principal, visible dans les builds PLAYTEST et Debug uniquement.

| Brique | Où | Rôle |
|---|---|---|
| `PvpSetup` | `Core/Battle/PvpSetup.cs` | Échiquier 8×8, même armée des deux côtés : 8 Soldats devant, puis Lancier, Cavalier, Mage, Archer, Commandant (roi, essentiel), Mage, Cavalier, Lancier |
| `PvpMatchmaker` | `Game/Net/PvpMatchmaker.cs` | Recherche par lobby Steam public (filtre `chessarmy_mode = pvp_poc_1`) : rejoint un lobby libre, sinon en crée un et attend |
| `PvpSession` | `Game/Net/PvpSession.cs` | Échange P2P (SteamNetworkingMessages, fiable) : poignée de main, coups + empreinte, départ de l'adversaire |
| `RemoteBattleController` | `Game/Net/RemoteBattleController.cs` | Camp adverse alimenté par le réseau |
| `PvpLobbyScene` | `Game/Scenes/PvpLobbyScene.cs` | Écran « recherche d'un adversaire », bouton Annuler |
| Mode PvP | `GameplayScene` (`_pvp`) | Combat sans campagne : pas de sauvegarde, de succès ni de méta-progression ; bandeau de tour, panneau de fin |

**Défier un ami.** L'écran MULTIJOUEUR propose RECHERCHE RAPIDE (lobby public) ou DÉFIER UN AMI : un lobby
**privé** est créé et la fenêtre d'invitation de l'overlay Steam s'ouvre (bouton INVITER UN AMI pour la rouvrir).
L'ami accepte depuis Steam : jeu lancé, l'invitation arrive par `GameLobbyJoinRequested_t` ; jeu fermé, Steam le
lance avec `+connect_lobby <id>`. Dans les deux cas `PvpInvites` met le lobby de côté et le menu principal (ou
l'écran multijoueur) le rejoint. Une invitation acceptée en pleine campagne attend le retour au menu.
Le défi nécessite l'overlay Steam (lancer le jeu depuis Steam).

**Plateau retourné.** Chaque client joue en bas en `Faction.Player`. Les armées étant symétriques, l'hôte et
l'invité construisent le même plateau local ; seul change le premier joueur (l'hôte). Un coup reçu est retourné
(`PvpSetup.Mirror`) avant d'être appliqué, et l'empreinte se calcule dans le repère de l'hôte
(`ComputeStateHash(mirrored: true)` chez l'invité). Le test `Pvp_MirroredClients_StayInSync` le vérifie.

**Maps multi.** Type de map `Multi` (`CombatType.Multi`), créé dans l'éditeur par le bouton **Nouveau multi**
(échiquier 8×8 d'herbe, armées marquées en E/P à titre indicatif). La map par défaut est `multi_8_01.json`.
Le jeu tire la map de la partie par la graine de l'hôte, parmi les maps Multi jouables (hors brouillons, triées
par nom) : les deux clients tombent sur la même. Une map est jouable si elle fait 8×8 et si les deux lignes de
chaque bord sont franchissables. Elle devrait être **symétrique haut/bas** (mêmes règles sur chaque case et sa
case miroir, buissons compris) : l'invité la voit alors comme l'hôte. Sinon il la joue retournée, avec les bonnes
règles mais des tuiles orientées à l'envers. L'éditeur affiche « Sym » / « Ret » dans la colonne Obj, et le récap
a une section MULTI. Seuls le terrain et les buissons comptent : coffres, recrues et chutes sont ignorés.

**Limites connues du POC.**
- Le retournement ne tient que si aucun trait n'utilise l'aléa ou l'ordre de parcours du plateau (Orage, Esquive…).
  C'est le cas des pions de cette armée. Avec d'autres armées, il faudra faire jouer `Faction.Enemy` à l'invité.
- Le roi adverse n'a pas de sprite ennemi dédié (`commandant_ia_*`) : il reprend le sprite joueur.
- Pas de limite de temps, pas d'abandon dédié (quitter par le menu pause = défaite par abandon chez l'adversaire),
  pas de reconnexion.
- Tester demande deux comptes Steam sur deux PC (Steam n'ouvre qu'un compte par machine).
