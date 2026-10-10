using System.Text.Json;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>One imported save: who the trainer is, how far the game went, and where the copy of the file lives.</summary>
public sealed record SaveEntry(
    Guid Id, string Region, string Game, string GameLabel, string Trainer, int TID, int SID, string PlayTime, int Generation,
    int[] Caught, int[] Seen, bool IsPrime, DateTimeOffset ImportedUtc, DateTimeOffset UpdatedUtc, string SourceName)
{
    /// <summary>The copy of the save file kept by the library.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FileName => $"{Id:N}.sav";
}

public enum ImportKind { Added, Updated }

public sealed record ImportResult(SaveEntry Entry, ImportKind Kind);

/// <summary>Finds which game of the profile a save's game name stands for.</summary>
public static class SaveGames
{
    private static readonly (string Needle, string Region, string Game)[] Specific =
    [
        ("omega ruby", "hoenn", "omegaruby"), ("alpha sapphire", "hoenn", "alphasapphire"),
        ("ultra sun", "alola", "ultrasun"), ("ultra moon", "alola", "ultramoon"),
        ("brilliant diamond", "sinnoh", "brilliantdiamond"), ("shining pearl", "sinnoh", "shiningpearl"),
        ("legends: arceus", "hisui", "arceus"), ("heartgold", "johto", "heartgold"), ("soulsilver", "johto", "soulsilver"),
        ("firered", "kanto", "firered"), ("leafgreen", "kanto", "leafgreen"),
        ("black 2", "unys", "black2"), ("white 2", "unys", "white2"),
    ];

    private static readonly Dictionary<string, (string Region, string Game)> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = ("kanto", "red"), ["blue"] = ("kanto", "blue"), ["green"] = ("kanto", "blue"), ["yellow"] = ("kanto", "yellow"),
        ["gold"] = ("johto", "gold"), ["silver"] = ("johto", "silver"), ["crystal"] = ("johto", "crystal"),
        ["ruby"] = ("hoenn", "ruby"), ["sapphire"] = ("hoenn", "sapphire"), ["emerald"] = ("hoenn", "emerald"),
        ["diamond"] = ("sinnoh", "diamond"), ["pearl"] = ("sinnoh", "pearl"), ["platinum"] = ("sinnoh", "platinum"),
        ["black"] = ("unys", "black"), ["white"] = ("unys", "white"),
        ["x"] = ("kalos", "x"), ["y"] = ("kalos", "y"),
        ["sun"] = ("alola", "sun"), ["moon"] = ("alola", "moon"),
        ["sword"] = ("galar", "sword"), ["shield"] = ("galar", "shield"),
        ["scarlet"] = ("paldea", "scarlet"), ["violet"] = ("paldea", "violet"),
    };

    /// <summary>
    /// The profile games a game name can stand for: one, two for a shared-layout pair ("Ruby / Sapphire",
    /// the player picks), none for hacks and games the profile does not show.
    /// </summary>
    public static IReadOnlyList<(string Region, string Game)> Resolve(string label)
    {
        var results = new List<(string Region, string Game)>();
        foreach (var part in label.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (ResolveOne(part) is { } found && !results.Contains(found)) results.Add(found);
        return results;
    }

    private static (string Region, string Game)? ResolveOne(string label)
    {
        var text = label.Trim();
        foreach (var prefix in new[] { "Pokémon ", "Pokemon " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        var lower = text.ToLowerInvariant();
        if (lower.Contains("let's go", StringComparison.Ordinal))
            return lower.Contains("pikachu", StringComparison.Ordinal) ? ("kanto", "letsgo_pikachu")
                : lower.Contains("eevee", StringComparison.Ordinal) || lower.Contains("évoli", StringComparison.Ordinal) ? ("kanto", "letsgo_eevee") : null;
        foreach (var (needle, region, game) in Specific)
            if (lower.Contains(needle, StringComparison.Ordinal)) return (region, game);
        return Exact.TryGetValue(text, out var exact) ? exact : null;
    }
}

/// <summary>
/// The library of imported saves. A copy of each save file and the facts read from it live in the app's own
/// folder, so a save stays available when the original moves. Importing the same trainer of the same game
/// again updates its entry; the first save of a game becomes its prime save, the one whose Pokédex counts.
/// </summary>
public sealed class SaveLibrary(ISaveEngine engine)
{
    private readonly object _gate = new();
    private List<SaveEntry>? _entries;

    private static string Folder => Path.Combine(FileSystem.AppDataDirectory, "saves");
    private static string IndexPath => Path.Combine(Folder, "index.json");

    public IReadOnlyList<SaveEntry> All
    {
        get
        {
            lock (_gate) return Load().ToList();
        }
    }

    public IReadOnlyList<SaveEntry> ForGame(string region, string game) =>
        All.Where(e => e.Region == region && e.Game == game).OrderByDescending(e => e.IsPrime).ThenByDescending(e => e.UpdatedUtc).ToList();

    private List<SaveEntry> Load()
    {
        if (_entries is not null) return _entries;
        try
        {
            _entries = File.Exists(IndexPath)
                ? JsonSerializer.Deserialize<List<SaveEntry>>(File.ReadAllText(IndexPath)) ?? []
                : [];
        }
        catch (Exception error)
        {
            AppLog.Warn("saves", $"The save library index could not be read: {error.Message}");
            _entries = [];
        }
        return _entries;
    }

    private void Persist()
    {
        Directory.CreateDirectory(Folder);
        var temp = IndexPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries));
        File.Move(temp, IndexPath, overwrite: true);
    }

    /// <summary>Reads the save's facts and stores them with a copy of the file (a new entry, or the update of a known trainer).</summary>
    public ImportResult Import(ReadOnlyMemory<byte> bytes, string sourceName, string? hint, SaveFormat format, string region, string game, string gameLabel)
    {
        using var session = engine.OpenSession(bytes, hint, format);
        var facts = session.ReadFacts() ?? throw new InvalidOperationException("Cette sauvegarde ne peut pas être lue.");
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var entries = Load();
            var known = entries.FirstOrDefault(e => e.Region == region && e.Game == game
                && e.Trainer == facts.Trainer && e.TID == facts.TID && e.SID == facts.SID);
            var id = known?.Id ?? Guid.NewGuid();
            var entry = new SaveEntry(id, region, game, gameLabel, facts.Trainer, facts.TID, facts.SID, facts.PlayTime, facts.Generation,
                [.. facts.Caught], [.. facts.Seen],
                known?.IsPrime ?? !entries.Any(e => e.Region == region && e.Game == game),
                known?.ImportedUtc ?? now, now, sourceName);
            Directory.CreateDirectory(Folder);
            File.WriteAllBytes(Path.Combine(Folder, entry.FileName), bytes.ToArray());
            if (known is null) entries.Add(entry);
            else entries[entries.IndexOf(known)] = entry;
            Persist();
            return new ImportResult(entry, known is null ? ImportKind.Added : ImportKind.Updated);
        }
    }

    /// <summary>Makes one save the prime save of its game (the others of that game stop being prime).</summary>
    public void SetPrime(Guid id)
    {
        lock (_gate)
        {
            var entries = Load();
            var target = entries.FirstOrDefault(e => e.Id == id);
            if (target is null) return;
            for (var i = 0; i < entries.Count; i++)
                if (entries[i].Region == target.Region && entries[i].Game == target.Game)
                    entries[i] = entries[i] with { IsPrime = entries[i].Id == id };
            Persist();
        }
    }

    /// <summary>Removes a save and its copy; if it was prime, the most recently updated save of that game takes over.</summary>
    public void Delete(Guid id)
    {
        lock (_gate)
        {
            var entries = Load();
            var target = entries.FirstOrDefault(e => e.Id == id);
            if (target is null) return;
            entries.Remove(target);
            try { File.Delete(Path.Combine(Folder, target.FileName)); }
            catch (Exception error) { AppLog.Warn("saves", $"Could not delete a save copy: {error.Message}"); }
            if (target.IsPrime)
            {
                var next = entries.Where(e => e.Region == target.Region && e.Game == target.Game).OrderByDescending(e => e.UpdatedUtc).FirstOrDefault();
                if (next is not null) entries[entries.IndexOf(next)] = next with { IsPrime = true };
            }
            Persist();
        }
    }
}
