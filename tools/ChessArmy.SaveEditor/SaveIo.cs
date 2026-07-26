using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChessArmy.Core.Campaign;

namespace ChessArmy.SaveEditor;

/// <summary>
/// Lecture/écriture des fichiers de progression. Les options JSON DOIVENT rester identiques à celles de
/// <c>ChessArmy.Engine.Persistence.SaveService</c> (indenté, champs inclus, enums en texte) : c'est ce qui
/// garantit qu'un fichier écrit ici est relu par le jeu, et l'inverse.
/// </summary>
internal static class SaveIo
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RunSave? LoadSlot(int index) => Read<RunSave>(GamePaths.SlotPath(index));

    public static void SaveSlot(int index, RunSave save) => Write(GamePaths.SlotPath(index), save);

    public static ProfileDto LoadProfile() => Read<ProfileDto>(GamePaths.ProfilePath) ?? new ProfileDto();

    public static void SaveProfile(ProfileDto profile) => Write(GamePaths.ProfilePath, profile);

    private static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }
}

/// <summary>
/// Copie du <c>ProfileDto</c> d'ChessArmy.Engine (méta-progression). Dupliquée plutôt que référencée : le DTO
/// vit dans ChessArmy.Engine, qui tire MonoGame. Les NOMS de propriétés font le contrat — les garder alignés.
/// </summary>
internal sealed class ProfileDto
{
    public bool HasPlayedBefore { get; set; }
    public List<string> DiscoveredUnits { get; set; } = new();
    public List<string> DiscoveredEquipment { get; set; } = new();
    public List<string> UnlockedCommanders { get; set; } = new();
}
