using System.Collections.Generic;
using System.Security.Cryptography;

namespace ChessArmy.Core.Pvp;

/// <summary>
/// Entier qui n'est JAMAIS stocké en clair en mémoire (anti-triche par édition de la RAM, type Cheat Engine) :
/// la valeur est chiffrée par une clé tirée au hasard, avec un contrôle d'intégrité. Chercher « 1250 » dans la
/// mémoire ne trouve rien ; modifier les octets est détecté (<see cref="Tampered"/>) et la valeur retombe à 0.
/// La clé change à chaque écriture, l'empreinte mémoire d'une même valeur change donc aussi.
/// </summary>
public struct ObscuredInt
{
    private int _key;
    private int _hidden;
    private int _check;

    /// <summary>Vrai dès qu'une valeur obscurcie a été modifiée de l'extérieur (le profil refuse alors d'enregistrer).</summary>
    public static bool Tampered { get; private set; }

    /// <summary>Signale une triche détectée ailleurs (ensemble obscurci, fichier de profil).</summary>
    internal static void FlagTampered() => Tampered = true;

    /// <summary>Tests : remet le drapeau de triche à zéro.</summary>
    internal static void ResetTamperFlag() => Tampered = false;

    public ObscuredInt(int value)
    {
        _key = _hidden = _check = 0;
        Value = value;
    }

    public int Value
    {
        readonly get
        {
            if (_key == 0 && _hidden == 0 && _check == 0)
                return 0;   // valeur par défaut du struct
            var value = _hidden ^ _key;
            if ((~value ^ (_key * 31 + 7)) != _check)
            {
                Tampered = true;
                return 0;
            }
            return value;
        }
        set
        {
            _key = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            _hidden = value ^ _key;
            _check = ~value ^ (_key * 31 + 7);
        }
    }

    public static implicit operator int(ObscuredInt o) => o.Value;
    public static implicit operator ObscuredInt(int v) => new(v);
    public override readonly string ToString() => Value.ToString();
}

/// <summary>
/// Ensemble de chaînes (déblocages) protégé par une empreinte obscurcie : ajouter une entrée en mémoire sans
/// passer par <see cref="Add"/> est détecté au prochain contrôle (<see cref="Verify"/>).
/// </summary>
public sealed class ObscuredSet
{
    private readonly HashSet<string> _items = new();
    private ObscuredInt _count;
    private ObscuredInt _digest;

    public IReadOnlyCollection<string> Items => _items;

    public bool Contains(string item) => Verify() && _items.Contains(item);

    public bool Add(string item)
    {
        if (!Verify() || !_items.Add(item))
            return false;
        Reseal();
        return true;
    }

    /// <summary>Vrai si le contenu correspond à l'empreinte ; sinon marque la triche (<see cref="ObscuredInt.Tampered"/>).</summary>
    public bool Verify()
    {
        if (_items.Count == _count.Value && Digest() == _digest.Value)
            return true;
        ObscuredInt.FlagTampered();
        return false;
    }

    private void Reseal()
    {
        _count = _items.Count;
        _digest = Digest();
    }

    private int Digest()
    {
        // Somme d'empreintes FNV : indépendante de l'ordre du HashSet.
        var sum = 0;
        foreach (var item in _items)
        {
            var h = unchecked((int)2166136261);
            foreach (var ch in item)
                h = unchecked((h ^ ch) * 16777619);
            sum = unchecked(sum + h);
        }
        return sum;
    }
}
