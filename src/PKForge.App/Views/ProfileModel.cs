using PKForge.Domain;
using PKForge.Engine;
using GV = PKHeX.Core.GameVersion;

namespace PKForge.App.Views;

/// <summary>One game shown on a region block of the profile (its picture is game_&lt;region&gt;_&lt;id&gt;.png).</summary>
public sealed record ProfileGame(string Id, GV[] Versions);

/// <summary>How the badges of a region are laid out on their pill.</summary>
public enum BadgeStyle { None, Row, Large, Zigzag, Pair, Rows }

/// <summary>One region block of the profile: its games, its Pokédex and its badges.</summary>
public sealed record ProfileRegion(string Id, string Title, ProfileGame[] Games, string[] Badges, BadgeStyle Style, float CardHeight, float BadgePillHeight);

/// <summary>
/// The regions of the profile screen and the rule that decides which species belong to each Pokédex.
/// Kanto to Alola use the classic number ranges; Galar, Hisui and Paldea use the game's own species table
/// (those Pokédexes reuse older species, so numbers alone do not describe them).
/// </summary>
public static class ProfileCatalog
{
    private static string[] Numbered(int count) => Enumerable.Range(1, count).Select(i => $"{i:00}").ToArray();

    public static readonly ProfileRegion[] Regions =
    [
        new("kanto", "Pokédex de Kanto",
            [new("red", [GV.RD]), new("blue", [GV.BU, GV.GN]), new("yellow", [GV.YW]), new("firered", [GV.FR]),
             new("leafgreen", [GV.LG]), new("letsgo_pikachu", [GV.GP]), new("letsgo_eevee", [GV.GE])],
            Numbered(8), BadgeStyle.Row, 449, 99),
        new("johto", "Pokédex de Johto",
            [new("gold", [GV.GD]), new("silver", [GV.SI]), new("crystal", [GV.C]), new("heartgold", [GV.HG]), new("soulsilver", [GV.SS])],
            Numbered(8), BadgeStyle.Row, 449, 99),
        new("hoenn", "Pokédex de Hoenn",
            [new("ruby", [GV.R]), new("sapphire", [GV.S]), new("emerald", [GV.E]), new("omegaruby", [GV.OR]), new("alphasapphire", [GV.AS])],
            Numbered(8), BadgeStyle.Row, 449, 99),
        new("sinnoh", "Pokédex de Sinnoh",
            [new("diamond", [GV.D]), new("pearl", [GV.P]), new("platinum", [GV.Pt]), new("brilliantdiamond", [GV.BD]), new("shiningpearl", [GV.SP])],
            Numbered(8), BadgeStyle.Row, 449, 99),
        new("unys", "Pokédex d’Unys",
            [new("black", [GV.B]), new("white", [GV.W]), new("black2", [GV.B2]), new("white2", [GV.W2])],
            Numbered(10), BadgeStyle.Row, 449, 99),
        new("kalos", "Pokédex de Kalos",
            [new("x", [GV.X]), new("y", [GV.Y])],
            Numbered(8), BadgeStyle.Row, 449, 99),
        new("alola", "Pokédex d’Alola",
            [new("moon", [GV.MN]), new("sun", [GV.SN]), new("ultramoon", [GV.UM]), new("ultrasun", [GV.US])],
            Numbered(18), BadgeStyle.Zigzag, 449, 99),
        new("galar", "Pokédex de Galar",
            [new("shield", [GV.SH]), new("sword", [GV.SW])],
            ["sword", "shield"], BadgeStyle.Pair, 523, 168),
        new("hisui", "Pokédex de Hisui",
            [new("arceus", [GV.PLA])],
            Numbered(5), BadgeStyle.Large, 449, 99),
        new("paldea", "Pokédex de Paldea",
            [new("scarlet", [GV.SL]), new("violet", [GV.VL])],
            Numbered(18), BadgeStyle.Rows, 615, 282),
    ];

    private static readonly (string Region, int From, int To)[] Ranges =
    [
        ("kanto", 1, 151), ("johto", 152, 251), ("hoenn", 252, 386), ("sinnoh", 387, 493),
        ("unys", 494, 649), ("kalos", 650, 721), ("alola", 722, 809),
    ];

    /// <summary>The species number the national Pokédex counts to (Bulbasaur to Pecharunt).</summary>
    public const int NationalTotal = 1025;

    private static readonly Dictionary<string, HashSet<int>> TableDex = BuildTableDex();

    private static Dictionary<string, HashSet<int>> BuildTableDex()
    {
        static HashSet<int> Of(PKHeX.Core.IPersonalTable table)
        {
            var set = new HashSet<int>();
            for (var species = 1; species <= table.MaxSpeciesID; species++)
                if (table.IsPresentInGame((ushort)species, 0)) set.Add(species);
            return set;
        }
        return new Dictionary<string, HashSet<int>>
        {
            ["galar"] = Of(PKHeX.Core.PersonalTable.SWSH),
            ["hisui"] = Of(PKHeX.Core.PersonalTable.LA),
            ["paldea"] = Of(PKHeX.Core.PersonalTable.SV),
        };
    }

    /// <summary>
    /// Whether the badges of a game can be read from its save. Alola, Galar, Hisui and Paldea keep them in
    /// game-event data that is not read yet; Let's Go and the Sinnoh remakes are not read either.
    /// </summary>
    public static bool BadgesReadable(string region, string game) => region switch
    {
        "kanto" => game is not ("letsgo_pikachu" or "letsgo_eevee"),
        "johto" or "hoenn" or "unys" or "kalos" => true,
        "sinnoh" => game is not ("brilliantdiamond" or "shiningpearl"),
        _ => false,
    };

    /// <summary>How many species a region's Pokédex holds.</summary>
    public static int Total(string region)
    {
        foreach (var (id, from, to) in Ranges)
            if (id == region) return to - from + 1;
        return TableDex.TryGetValue(region, out var set) ? set.Count : 0;
    }

    /// <summary>True when the species belongs to the region's Pokédex.</summary>
    public static bool InDex(string region, int species)
    {
        foreach (var (id, from, to) in Ranges)
            if (id == region) return species >= from && species <= to;
        return TableDex.TryGetValue(region, out var set) && set.Contains(species);
    }

    /// <summary>The region and game ("kanto", "red") a Pokémon's origin game belongs to, or null (GO, Z-A, unknown).</summary>
    public static (string Region, string Game)? Origin(GV version)
    {
        foreach (var region in Regions)
            foreach (var game in region.Games)
                if (Array.IndexOf(game.Versions, version) >= 0) return (region.Id, game.Id);
        return null;
    }

    public static string BadgeAsset(string region, string badge) => $"rotomphone/profile/badge_{region}_{badge}.png";

    public static string GameAsset(string region, string game) => $"rotomphone/profile/game_{region}_{game}.png";

    /// <summary>Every picture of the profile screen, for loading.</summary>
    public static IEnumerable<string> AssetNames()
    {
        yield return "rotomphone/profile/icon_national.png";
        yield return "rotomphone/profile/wave_tile.png";
        yield return "rotomphone/trainers/trainer_default.png";
        foreach (var region in Regions)
        {
            foreach (var game in region.Games) yield return GameAsset(region.Id, game.Id);
            foreach (var badge in region.Badges) yield return BadgeAsset(region.Id, badge);
        }
    }
}

/// <summary>What the profile screen shows, at one moment.</summary>
public sealed record ProfileData(
    string Name, string Title, int Captured, int NationalCaught,
    Dictionary<string, int> RegionCaught, int GoCaught, HashSet<string> OwnedGames, HashSet<string> OwnedBadges);

/// <summary>Counts for the profile, taken from the Bank (and later from imported saves).</summary>
public static class ProfileStats
{
    private static readonly Dictionary<Guid, int> VersionCache = new();
    private static readonly object Gate = new();

    /// <summary>The numbers that need no reading of the stored Pokémon: instant.</summary>
    public static ProfileData Quick(IReadOnlyList<BankEntry> all, string name)
    {
        var regions = ProfileCatalog.Regions.ToDictionary(r => r.Id, _ => 0);
        return new ProfileData(name, "Dresseur", all.Count, all.Select(e => e.Info.Species).Where(s => s > 0).Distinct().Count(),
            regions, 0, [], []);
    }

    /// <summary>
    /// The full numbers: each Pokémon's origin game decides which regional Pokédex it fills.
    /// Reads every stored Pokémon once (remembered afterwards), so it runs off the UI thread.
    /// </summary>
    public static ProfileData Deep(IBankService bank, IReadOnlyList<BankEntry> all, string name, Services.SaveLibrary? library = null)
    {
        var sets = ProfileCatalog.Regions.ToDictionary(r => r.Id, _ => new HashSet<int>());
        var go = new HashSet<int>();
        var games = new HashSet<string>();
        foreach (var entry in all)
        {
            var version = VersionOf(bank, entry);
            if (version == 0) continue;
            var gv = (GV)version;
            if (gv == GV.GO)
            {
                if (entry.Info.Species > 0) go.Add(entry.Info.Species);
                continue;
            }
            if (ProfileCatalog.Origin(gv) is not { } origin) continue;
            games.Add($"{origin.Region}/{origin.Game}");
            if (ProfileCatalog.InDex(origin.Region, entry.Info.Species)) sets[origin.Region].Add(entry.Info.Species);
        }
        var quick = Quick(all, name);
        var national = all.Select(e => e.Info.Species).Where(s => s > 0).ToHashSet();
        var badges = new HashSet<string>();
        if (library is not null)
        {
            // Imported saves: every game that has a save counts as owned; the Pokédex of each game's prime save adds to its region.
            foreach (var save in library.All)
            {
                games.Add($"{save.Region}/{save.Game}");
                if (save.IsPrime && save.Badges is { } earned)
                    foreach (var badge in earned) badges.Add(badge);
                if (!save.IsPrime || !sets.TryGetValue(save.Region, out var regionSet)) continue;
                foreach (var species in save.Caught)
                {
                    national.Add(species);
                    if (ProfileCatalog.InDex(save.Region, species)) regionSet.Add(species);
                }
            }
        }
        return quick with
        {
            NationalCaught = national.Count,
            RegionCaught = sets.ToDictionary(p => p.Key, p => p.Value.Count),
            GoCaught = go.Count,
            OwnedGames = games,
            OwnedBadges = badges,
        };
    }

    private static int VersionOf(IBankService bank, BankEntry entry)
    {
        lock (Gate)
            if (VersionCache.TryGetValue(entry.Id, out var known)) return known;
        var version = 0;
        try
        {
            var pk = EntityBytes.Parse(entry, bank.GetData(entry.Id));
            if (pk is not null) version = (int)pk.Version;
        }
        catch (Exception error)
        {
            Services.AppLog.Warn("profile", $"Could not read the origin of a Bank entry: {error.Message}");
        }
        lock (Gate) VersionCache[entry.Id] = version;
        return version;
    }
}
