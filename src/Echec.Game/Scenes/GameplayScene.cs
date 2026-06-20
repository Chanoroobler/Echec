using Echec.Core;
using Echec.Engine;
using Echec.Engine.Scenes;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Echec.Game.Scenes;

/// <summary>
/// Scène de partie : dessine l'échiquier à partir de l'état du domaine (Core).
/// Le rendu des pièces (textures/sprites) viendra via le pipeline de contenu.
/// </summary>
public sealed class GameplayScene : Scene
{
    private const int TileSize = 64;

    private static readonly Color LightSquare = new(240, 217, 181);
    private static readonly Color DarkSquare = new(181, 136, 99);

    private readonly Board _board = Board.CreateInitial();
    private Texture2D _pixel = null!;

    public GameplayScene(GameContext context) : base(context)
    {
    }

    public override void Load()
    {
        // Texture 1x1 réutilisée pour dessiner des rectangles colorés.
        _pixel = new Texture2D(Context.GraphicsDevice, 1, 1);
        _pixel.SetData([Color.White]);
    }

    public override void Unload() => _pixel.Dispose();

    public override void Update(GameTime gameTime)
    {
        // TODO : sélection d'une case au clic via Context.Input.WasLeftClicked.
    }

    public override void Draw(GameTime gameTime)
    {
        var spriteBatch = Context.SpriteBatch;
        spriteBatch.Begin();

        for (var rank = 0; rank < 8; rank++)
        {
            for (var file = 0; file < 8; file++)
            {
                var color = (file + rank) % 2 == 0 ? DarkSquare : LightSquare;
                // Rangée 0 (blancs) affichée en bas de l'écran.
                var screenRow = 7 - rank;
                var rectangle = new Rectangle(file * TileSize, screenRow * TileSize, TileSize, TileSize);
                spriteBatch.Draw(_pixel, rectangle, color);
            }
        }

        spriteBatch.End();
    }
}
