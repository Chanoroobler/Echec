namespace ChessArmy.Engine.Localization;

/// <summary>
/// Langues disponibles pour l'interface. L'ordre des membres = l'ordre des colonnes de
/// <c>Assets/Config/strings.csv</c> (Francais → colonne 0, English → colonne 1, …) ET l'ordre
/// du sélecteur dans les options. Ajouter une langue = ajouter un membre ICI (à la FIN, pour ne pas
/// décaler les colonnes existantes ni casser les réglages sauvegardés) + une colonne là-bas.
/// Les langues latines (Italiano→Turkce) utilisent la police pixel ; ChineseSimplified (chinois simplifié)
/// utilise la police bitmap CJK Fusion Pixel via <c>Fonts.Active</c> (la PixelFont n'a pas d'idéogrammes).
/// </summary>
public enum Language { Francais, English, Italiano, Deutsch, Espanol, Polski, Turkce, ChineseSimplified }
