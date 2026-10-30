using PKHeX.Core;

namespace PKForge.App.Services;

/// <summary>One answer of a hunt setting, and how many extra shiny rolls it gives.</summary>
public sealed record HuntChoice(string Label, int Rolls);

/// <summary>A setting a method can have (Sparkling Power, outbreak progress, catch combo...).</summary>
public sealed record HuntOptionGroup(string Id, string Label, HuntChoice[] Choices);

/// <summary>
/// A way of hunting in one game. Odds are counted in rolls: every roll is one more
/// chance in <see cref="HuntGame.Denominator"/>. A few methods have a fixed published rate instead.
/// </summary>
/// <param name="Rolls">Extra rolls the method itself gives.</param>
/// <param name="Multiplier">How many Pokémon roll at once (hordes).</param>
/// <param name="CharmApplies">False when the Shiny Charm changes nothing for this method.</param>
/// <param name="CharmRolls">Rolls the charm gives here, when different from the game's.</param>
/// <param name="FixedOdds">Published "1 in N" for methods that are not roll based.</param>
/// <param name="FixedOddsCharm">The same with the Shiny Charm, when it differs.</param>
/// <param name="Options">Ids of the <see cref="HuntOptionGroup"/>s this method has.</param>
public sealed record HuntMethod(string Id, string Name, string Abbr, int Rolls = 0, int Multiplier = 1,
    bool CharmApplies = true, int? CharmRolls = null, double? FixedOdds = null, double? FixedOddsCharm = null,
    string[]? Options = null);

/// <summary>A game a hunt can be done in.</summary>
public sealed record HuntGame(string Id, string Name, GameVersion Icon, int Denominator, bool HasCharm, int CharmRolls, HuntMethod[] Methods);

/// <summary>The breakdown shown by the odds panel.</summary>
public sealed record OddsResult(string Base, int CharmRolls, int MethodRolls, string Current, bool Fixed, double Probability);

/// <summary>
/// Shiny odds of every game and method. The rolls come from the published shiny rates (Bulbapedia,
/// PokeTools, RotomLabs). To fix a rate, change the one line of the game or method concerned.
/// </summary>
public static class ShinyOdds
{
    private static HuntOptionGroup Group(string id, string label, params (string Label, int Rolls)[] choices) =>
        new(id, label, [.. choices.Select(c => new HuntChoice(c.Label, c.Rolls))]);

    public static readonly IReadOnlyList<HuntOptionGroup> Groups =
    [
        Group("sparkling", "Puissance Scintillante", ("Désactivée", 0), ("Niv. 1", 1), ("Niv. 2", 2), ("Niv. 3", 3)),
        // Scarlet / Violet outbreaks: 30 cleared gives one more roll, 60 two more.
        Group("outbreak", "Apparition massive : Pokémon vaincus", ("0 à 29", 0), ("30 à 59", 1), ("60 et plus", 2)),
        // Let's Go: the chained species spawns with more rolls as the catch combo grows.
        Group("combo", "Combo de captures", ("0 à 10", 0), ("11 à 20", 3), ("21 à 30", 7), ("31 et plus", 11)),
        Group("lure", "Leurre", ("Sans", 0), ("Avec", 1)),
        // Legends Arceus: Dex research level 10 gives +1 roll, perfect research +2 more.
        Group("research", "Recherches du Pokédex", ("Aucune", 0), ("Niveau 10", 1), ("Niveau 10 et parfait", 3)),
    ];

    private static readonly Dictionary<string, HuntOptionGroup> ById = Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);

    public static HuntOptionGroup GroupFor(string id) => ById[id];

    // ── Methods ──────────────────────────────────────────────────────────────

    private static readonly HuntMethod Wild = new("random", "Rencontre aléatoire", "RE");
    private static readonly HuntMethod Soft = new("soft", "Soft reset", "SR");
    private static readonly HuntMethod Gift = new("gift", "Cadeau", "Ca", CharmApplies: false);
    private static readonly HuntMethod Masuda4 = new("masuda", "Méthode Masuda", "MM", Rolls: 4);
    private static readonly HuntMethod Masuda5 = new("masuda", "Méthode Masuda", "MM", Rolls: 5);
    // Eggs without the Masuda method: the charm adds a single roll in the games that have it.
    private static readonly HuntMethod Breeding = new("breeding", "Élevage", "Br", CharmRolls: 1);
    private static readonly HuntMethod BreedingShinyParent = new("breedshiny", "Élevage (parent chromatique)", "Br", FixedOdds: 64);
    private static readonly HuntMethod RadarGen4 = new("radar", "Poké Radar (chaîne de 40)", "PR", FixedOdds: 200);
    private static readonly HuntMethod RadarBdsp = new("radar", "Poké Radar (chaîne de 40)", "PR", FixedOdds: 99.9, CharmApplies: false);
    private static readonly HuntMethod Horde = new("horde", "Horde", "Ho", Multiplier: 5);
    private static readonly HuntMethod FriendSafari = new("safari", "Safari des Amis", "SA", Rolls: 4);
    private static readonly HuntMethod Sos = new("sos", "Appel à l'aide (chaîne de 30+)", "SO", Rolls: 12);
    private static readonly HuntMethod Combo = new("combo", "Combo de captures", "CC", Options: ["combo", "lure"]);
    private static readonly HuntMethod DynamaxAdventure = new("dynadv", "Aventure Dynamax", "AD", FixedOdds: 300, FixedOddsCharm: 100);
    private static readonly HuntMethod PlaRandom = new("random", "Rencontre aléatoire", "RE", Options: ["research"]);
    private static readonly HuntMethod PlaOutbreak = new("outbreak", "Apparition massive", "AM", Rolls: 25, Options: ["research"]);
    private static readonly HuntMethod PlaMassive = new("massive", "Apparition massive spatio-temporelle", "AS", Rolls: 12, Options: ["research"]);
    private static readonly HuntMethod SvRandom = new("random", "Rencontre aléatoire", "RE", Options: ["sparkling"]);
    private static readonly HuntMethod SvOutbreak = new("outbreak", "Apparition massive", "AM", Options: ["outbreak", "sparkling"]);
    private static readonly HuntMethod TeraRaid = new("tera", "Téra-Raid", "Tr", FixedOdds: 4103.05, CharmApplies: false);

    // ── Games, newest first ──────────────────────────────────────────────────

    private static HuntGame G(string id, string name, GameVersion icon, int denominator, int charmRolls, params HuntMethod[] methods) =>
        new(id, name, icon, denominator, charmRolls > 0, charmRolls, methods);

    public static readonly IReadOnlyList<HuntGame> Games =
    [
        G("scarlet", "Pokémon Écarlate", GameVersion.SL, 4096, 2, SvRandom, SvOutbreak, Masuda5, Breeding, TeraRaid),
        G("violet", "Pokémon Violet", GameVersion.VL, 4096, 2, SvRandom, SvOutbreak, Masuda5, Breeding, TeraRaid),
        G("pla", "Légendes Pokémon : Arceus", GameVersion.PLA, 4096, 3, PlaRandom, PlaOutbreak, PlaMassive),
        G("bd", "Pokémon Diamant Étincelant", GameVersion.BD, 4096, 2, Wild, Soft, Masuda5, Breeding, RadarBdsp),
        G("sp", "Pokémon Perle Scintillante", GameVersion.SP, 4096, 2, Wild, Soft, Masuda5, Breeding, RadarBdsp),
        G("sword", "Pokémon Épée", GameVersion.SW, 4096, 2, Wild, Soft, Masuda5, Breeding, DynamaxAdventure),
        G("shield", "Pokémon Bouclier", GameVersion.SH, 4096, 2, Wild, Soft, Masuda5, Breeding, DynamaxAdventure),
        G("lgp", "Pokémon Let's Go, Pikachu !", GameVersion.GP, 4096, 2, Wild, Combo, Soft),
        G("lge", "Pokémon Let's Go, Évoli !", GameVersion.GE, 4096, 2, Wild, Combo, Soft),
        G("ultrasun", "Pokémon Ultra-Soleil", GameVersion.US, 4096, 2, Wild, Soft, Gift, Masuda5, Sos),
        G("ultramoon", "Pokémon Ultra-Lune", GameVersion.UM, 4096, 2, Wild, Soft, Gift, Masuda5, Sos),
        G("sun", "Pokémon Soleil", GameVersion.SN, 4096, 2, Wild, Soft, Gift, Masuda5, Sos),
        G("moon", "Pokémon Lune", GameVersion.MN, 4096, 2, Wild, Soft, Gift, Masuda5, Sos),
        G("oras-r", "Pokémon Rubis Oméga", GameVersion.OR, 4096, 2, Wild, Soft, Gift, Masuda5, Horde),
        G("oras-s", "Pokémon Saphir Alpha", GameVersion.AS, 4096, 2, Wild, Soft, Gift, Masuda5, Horde),
        G("x", "Pokémon X", GameVersion.X, 4096, 2, Wild, Soft, Gift, Masuda5, Horde, FriendSafari),
        G("y", "Pokémon Y", GameVersion.Y, 4096, 2, Wild, Soft, Gift, Masuda5, Horde, FriendSafari),
        G("black2", "Pokémon Noire 2", GameVersion.B2, 8192, 2, Wild, Soft, Masuda5),
        G("white2", "Pokémon Blanche 2", GameVersion.W2, 8192, 2, Wild, Soft, Masuda5),
        G("black", "Pokémon Noire", GameVersion.B, 8192, 0, Wild, Soft, Masuda5),
        G("white", "Pokémon Blanche", GameVersion.W, 8192, 0, Wild, Soft, Masuda5),
        G("heartgold", "Pokémon HeartGold", GameVersion.HG, 8192, 0, Wild, Soft, Masuda4),
        G("soulsilver", "Pokémon SoulSilver", GameVersion.SS, 8192, 0, Wild, Soft, Masuda4),
        G("platinum", "Pokémon Platine", GameVersion.Pt, 8192, 0, Wild, Soft, Masuda4, RadarGen4),
        G("diamond", "Pokémon Diamant", GameVersion.D, 8192, 0, Wild, Soft, Masuda4, RadarGen4),
        G("pearl", "Pokémon Perle", GameVersion.P, 8192, 0, Wild, Soft, Masuda4, RadarGen4),
        G("firered", "Pokémon Rouge Feu", GameVersion.FR, 8192, 0, Wild, Soft),
        G("leafgreen", "Pokémon Vert Feuille", GameVersion.LG, 8192, 0, Wild, Soft),
        G("emerald", "Pokémon Émeraude", GameVersion.E, 8192, 0, Wild, Soft),
        G("ruby", "Pokémon Rubis", GameVersion.R, 8192, 0, Wild, Soft),
        G("sapphire", "Pokémon Saphir", GameVersion.S, 8192, 0, Wild, Soft),
        G("crystal", "Pokémon Cristal", GameVersion.C, 8192, 0, Wild, Soft, BreedingShinyParent),
        G("gold", "Pokémon Or", GameVersion.GD, 8192, 0, Wild, Soft, BreedingShinyParent),
        G("silver", "Pokémon Argent", GameVersion.SI, 8192, 0, Wild, Soft, BreedingShinyParent),
    ];

    public static HuntGame? Game(string id) => Games.FirstOrDefault(g => g.Id == id);

    public static HuntMethod? Method(HuntGame game, string id) => game.Methods.FirstOrDefault(m => m.Id == id);

    /// <summary>The settings that matter for a method in a game: the charm and its option groups.</summary>
    public static IReadOnlyList<HuntOptionGroup> OptionsOf(HuntMethod method) =>
        [.. (method.Options ?? []).Select(GroupFor)];

    // ── Odds ─────────────────────────────────────────────────────────────────

    public static OddsResult Compute(HuntGame game, HuntMethod method, bool charm, IReadOnlyDictionary<string, int> choices)
    {
        var withCharm = charm && game.HasCharm && method.CharmApplies;
        if (method.FixedOdds is { } published)
        {
            var odds = withCharm && method.FixedOddsCharm is { } better ? better : published;
            var text = $"1/{Math.Round(odds)}";
            return new OddsResult(text, 0, 0, text, true, 1 / odds);
        }

        var charmRolls = withCharm ? method.CharmRolls ?? game.CharmRolls : 0;
        var methodRolls = method.Rolls;
        foreach (var id in method.Options ?? [])
        {
            var group = GroupFor(id);
            var index = Math.Clamp(choices.GetValueOrDefault(id), 0, group.Choices.Length - 1);
            methodRolls += group.Choices[index].Rolls;
        }

        var rolls = (1 + charmRolls + methodRolls) * method.Multiplier;
        var probability = Math.Min(1.0, rolls / (double)game.Denominator);
        // With a multiplier (hordes) the extra rolls are shown as the method's own.
        var shownMethodRolls = rolls - 1 - charmRolls;
        return new OddsResult($"1/{game.Denominator}", charmRolls, shownMethodRolls, $"1/{Math.Round(1 / probability)}", false, probability);
    }

    /// <summary>Chance of having found the shiny after this many encounters, in percent.</summary>
    public static double ChanceAfter(double probability, int encounters) =>
        encounters <= 0 ? 0 : (1 - Math.Pow(1 - probability, encounters)) * 100;
}
