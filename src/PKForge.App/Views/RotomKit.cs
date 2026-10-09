using PKHeX.Core;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>Loads the Rotom Phone pictures bundled in the app package (once, cached).</summary>
public static class RotomAssets
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SKImage?> Cache = new();

    public static readonly string[] TypeNames =
    [
        "normal", "fighting", "flying", "poison", "ground", "rock", "bug", "ghost", "steel",
        "fire", "water", "grass", "electric", "psychic", "ice", "dragon", "dark", "fairy",
    ];

    /// <summary>The picture for a bundled logical name, or null when it is missing / still loading.</summary>
    public static SKImage? Get(string logicalName) => Cache.TryGetValue(logicalName, out var image) ? image : null;

    /// <summary>Decodes the given pictures (those not loaded yet) off the UI thread.</summary>
    public static Task WarmAsync(IEnumerable<string> names)
    {
        var list = names.ToArray();
        return Task.Run(() =>
        {
            foreach (var name in list) Load(name);
        });
    }

    /// <summary>Decodes every home-screen picture off the UI thread.</summary>
    public static Task WarmAsync() => Task.Run(() =>
    {
        foreach (var name in AllNames()) Load(name);
    });

    private static IEnumerable<string> AllNames()
    {
        foreach (var n in new[]
                 {
                     "rotom_base", "rotom_eyes_open", "rotom_eyes_closed", "rotom_mouth_closed",
                     "rotom_mouth_open", "bubble_shell", "floor", "black_square",
                 })
            yield return $"rotomphone/rotom/{n}.png";
        foreach (var t in TypeNames) yield return $"rotomphone/types/type_{t}.png";
        yield return "rotomphone/trainers/trainer_default.png";
        foreach (var n in new[] { "menu_panel", "btn_blank", "btn_pokedex", "btn_pokemon", "btn_shiny_collection", "btn_teams" })
            yield return $"rotomphone/menu/{n}.png";
    }

    private static void Load(string name)
    {
        if (Cache.ContainsKey(name)) return;
        SKImage? image = null;
        try
        {
            using var stream = FileSystem.OpenAppPackageFileAsync(name).GetAwaiter().GetResult();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            image = SKImage.FromEncodedData(bytes.ToArray());
        }
        catch (Exception)
        {
            image = null;
        }
        Cache[name] = image;
    }
}

/// <summary>The bundled FOT-UDKakugo face for the Rotom Phone screens (Skia cannot resolve MAUI aliases).</summary>
public static class RotomFont
{
    private static SKTypeface? _face;
    private static Task? _task;
    private static readonly object Gate = new();

    public static SKTypeface Face => Volatile.Read(ref _face) ?? SKTypeface.Default;

    public static Task WarmAsync()
    {
        lock (Gate)
            return _task ??= Task.Run(() =>
            {
                SKTypeface? face = null;
                try
                {
                    using var stream = FileSystem.OpenAppPackageFileAsync("FOT-UDKakugo.ttf").GetAwaiter().GetResult();
                    using var bytes = new MemoryStream();
                    stream.CopyTo(bytes);
                    var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, "FOT-UDKakugo.ttf");
                    File.WriteAllBytes(cache, bytes.ToArray());
                    face = SKTypeface.FromFile(cache);
                }
                catch (Exception)
                {
                    face = null;
                }
                Volatile.Write(ref _face, face ?? SKTypeface.Default);
            });
    }
}

/// <summary>French region and game names for what the yellow box says about a Pokémon.</summary>
public static class RotomOrigin
{
    public static string Game(GameVersion version) => version switch
    {
        GameVersion.RD => "Rouge", GameVersion.GN => "Vert", GameVersion.BU => "Bleu", GameVersion.YW => "Jaune",
        GameVersion.GD => "Or", GameVersion.SI => "Argent", GameVersion.C => "Cristal",
        GameVersion.S => "Saphir", GameVersion.R => "Rubis", GameVersion.E => "Émeraude",
        GameVersion.FR => "Rouge Feu", GameVersion.LG => "Vert Feuille",
        GameVersion.D => "Diamant", GameVersion.P => "Perle", GameVersion.Pt => "Platine",
        GameVersion.HG => "HeartGold", GameVersion.SS => "SoulSilver",
        GameVersion.W => "Blanche", GameVersion.B => "Noire", GameVersion.W2 => "Blanche 2", GameVersion.B2 => "Noire 2",
        GameVersion.X => "X", GameVersion.Y => "Y", GameVersion.AS => "Saphir Alpha", GameVersion.OR => "Rubis Oméga",
        GameVersion.SN => "Soleil", GameVersion.MN => "Lune", GameVersion.US => "Ultra-Soleil", GameVersion.UM => "Ultra-Lune",
        GameVersion.GO => "Pokémon GO", GameVersion.GP => "Let's Go Pikachu", GameVersion.GE => "Let's Go Évoli",
        GameVersion.SW => "Épée", GameVersion.SH => "Bouclier",
        GameVersion.PLA => "Légendes Arceus", GameVersion.BD => "Diamant Étincelant", GameVersion.SP => "Perle Scintillante",
        GameVersion.SL => "Écarlate", GameVersion.VL => "Violet", GameVersion.ZA => "Légendes Z-A",
        _ => "?",
    };

    public static string Region(GameVersion version) => version switch
    {
        GameVersion.RD or GameVersion.GN or GameVersion.BU or GameVersion.YW
            or GameVersion.FR or GameVersion.LG or GameVersion.GP or GameVersion.GE => "Kanto",
        GameVersion.GD or GameVersion.SI or GameVersion.C or GameVersion.HG or GameVersion.SS => "Johto",
        GameVersion.S or GameVersion.R or GameVersion.E or GameVersion.AS or GameVersion.OR => "Hoenn",
        GameVersion.D or GameVersion.P or GameVersion.Pt or GameVersion.BD or GameVersion.SP => "Sinnoh",
        GameVersion.PLA => "Hisui",
        GameVersion.W or GameVersion.B or GameVersion.W2 or GameVersion.B2 => "Unys",
        GameVersion.X or GameVersion.Y or GameVersion.ZA => "Kalos",
        GameVersion.SN or GameVersion.MN or GameVersion.US or GameVersion.UM => "Alola",
        GameVersion.SW or GameVersion.SH => "Galar",
        GameVersion.SL or GameVersion.VL => "Paldea",
        _ => "",
    };

    /// <summary>"Kalos (X)"; just the game when the region is unknown.</summary>
    public static string Line(GameVersion version)
    {
        var region = Region(version);
        var game = Game(version);
        return region.Length == 0 ? game : $"{region} ({game})";
    }
}
