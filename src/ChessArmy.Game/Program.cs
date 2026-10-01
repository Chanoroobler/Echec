#if !DEMO
// Steam AVANT la création du jeu (et donc du périphérique graphique) : l'overlay s'y accroche.
// La démo n'initialise pas Steam (pas de succès).
ChessArmy.Game.SteamService.Init();
#endif
try
{
    using var game = new ChessArmy.Game.ChessArmyGame();
    game.Run();
}
finally
{
    ChessArmy.Game.SteamService.Shutdown();
}
