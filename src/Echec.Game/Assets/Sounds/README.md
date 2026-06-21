# Effets sonores

Fichiers **WAV 16-bit PCM**. Chargés au runtime via `SoundEffect.FromStream`,
comme les sprites le sont via `Texture2D.FromStream`.

> ⚠️ `FromStream` n'accepte **que du WAV PCM** (16 bits). Pour du MP3/OGG il
> faudrait passer par le content pipeline (`Content/Content.mgcb`), ce qui sort
> de la convention « assets bruts » utilisée ici.

Le glob `Assets\**\*.*` du `.csproj` les copie tels quels à côté de l'exe
(`CopyToOutputDirectory=Always`) — il suffit de déposer les fichiers ici.

## Le câblage passe par un fichier de config

Le code ne référence **jamais** un nom de fichier en dur. La correspondance
**action → fichier** vit dans :

    src/Echec.Game/Assets/Config/sounds.json

Chaque entrée est `"clé d'action": "chemin du WAV relatif à ce dossier"`.
Pour remplacer un son : change la valeur dans le JSON (un autre `.wav`),
aucune recompilation du code n'est nécessaire. Clé absente ou fichier
introuvable = **silence** (aucune erreur, repli `SoundBank` → no-op).

## Clés d'action câblées

| Clé | Quand |
|-----|-------|
| `unit_pick` | On saisit une pièce (placement) |
| `unit_place` | On pose une pièce sur une case (placement) |
| `battle_start` | Lancement du combat (Entrée) |
| `unit_select` | Sélection d'une unité (combat) |
| `unit_deselect` | Désélection (clic droit / clic à vide) |
| `unit_move` | Déplacement d'une unité (joueur ou IA) |
| `unit_attack` | Attaque / capture (joueur ou IA) |
| `combat_won` | Combat remporté → recrutement |
| `recruit` | Choix d'une unité au recrutement |
| `victory` | Boss vaincu (fin de campagne) |
| `defeat` | Défaite (commandant tombé / armée détruite) |
| `menu_open` / `menu_close` | Ouverture / fermeture du menu pause |
| `menu_click` | Clic dans le menu pause |

## Côté code

- `SoundBank` (Echec.Engine/Audio) charge `sounds.json` et joue par clé.
- Exposé via `GameContext.Sounds` → `Context.Sounds.Play("unit_move")`.
- Le volume passe par l'`AudioManager` existant (Master × SFX).
