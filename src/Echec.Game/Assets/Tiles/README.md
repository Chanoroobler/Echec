# Tuiles (sprites de terrain)

Dépose ici les sprites de tuiles, **PNG 64×74** :
- largeur **64** px
- hauteur **74** px = surface jouable **64×64** + **10 px** d'épaisseur (bas du sprite)

## Fichiers attendus

| Fichier | Terrain |
|---------|---------|
| `grass.png` | `TerrainType.Grass` |

Tant qu'un fichier est absent, le jeu affiche une tuile placeholder générée
automatiquement (voir `Echec.Engine/Rendering/Textures.cs`).

Le mapping terrain → fichier se fait dans `Echec.Game/Scenes/GameplayScene.cs`
(`TextureFor` + `GrassTilePath`).
