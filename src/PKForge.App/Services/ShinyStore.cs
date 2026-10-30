using System.Text.Json;

namespace PKForge.App.Services;

/// <summary>A shiny hunt in progress: one counter.</summary>
public sealed class Hunt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Species { get; set; }
    public string Name { get; set; } = "";
    public string GameId { get; set; } = "";
    public string MethodId { get; set; } = "";
    public bool Charm { get; set; }
    public Dictionary<string, int> Choices { get; set; } = new();
    public int Encounters { get; set; }
    public int Increment { get; set; } = 1;

    /// <summary>Time of the hunt before the current run, in milliseconds.</summary>
    public long TotalMs { get; set; }

    /// <summary>Time since the last encounter, before the current run, in milliseconds.</summary>
    public long SessionMs { get; set; }

    /// <summary>When the timer was last started; null while paused.</summary>
    public DateTime? RunningSinceUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public bool Running => RunningSinceUtc is not null;

    private long RunMs => RunningSinceUtc is { } since ? Math.Max(0, (long)(DateTime.UtcNow - since).TotalMilliseconds) : 0;

    public long TotalNowMs => TotalMs + RunMs;

    public long SessionNowMs => SessionMs + RunMs;

    /// <summary>Folds the running time into the stored times and restarts the run from now.</summary>
    public void Settle()
    {
        if (RunningSinceUtc is null) return;
        var run = RunMs;
        TotalMs += run;
        SessionMs += run;
        RunningSinceUtc = DateTime.UtcNow;
    }

    public void Pause()
    {
        Settle();
        RunningSinceUtc = null;
    }

    public void Start() => RunningSinceUtc ??= DateTime.UtcNow;
}

/// <summary>A shiny that was caught: a finished hunt, kept in the collection.</summary>
public sealed class ShinyCatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Species { get; set; }
    public string Name { get; set; } = "";
    public string GameId { get; set; } = "";
    public string MethodId { get; set; } = "";
    public bool Charm { get; set; }
    public Dictionary<string, int> Choices { get; set; } = new();
    public int Encounters { get; set; }
    public long TotalMs { get; set; }
    public DateTime CaughtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>PKHeX ball id (4 = Poké Ball).</summary>
    public int Ball { get; set; } = 4;

    public string Description { get; set; } = "";
    public bool Favorite { get; set; }
}

/// <summary>
/// The shiny hunts and the shiny collection, kept on the phone. Saved after every change,
/// through a temporary file so a cut never leaves half a file.
/// </summary>
public sealed class ShinyStore
{
    public sealed class Data
    {
        public List<Hunt> Hunts { get; set; } = [];
        public List<ShinyCatch> Catches { get; set; } = [];
    }

    private readonly Lock _gate = new();
    private readonly Data _data;

    public ShinyStore()
    {
        _data = Load();
    }

    private static string FilePath => Path.Combine(FileSystem.AppDataDirectory, "shiny-hunts.json");

    public IReadOnlyList<Hunt> Hunts => _data.Hunts;

    public IReadOnlyList<ShinyCatch> Catches => _data.Catches;

    private static Data Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch (Exception error)
        {
            AppLog.Warn("shiny", $"Could not read the hunts: {error.Message}");
        }
        return new Data();
    }

    /// <summary>Writes everything to disk. Call after any change.</summary>
    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var temporary = FilePath + ".part";
                File.WriteAllText(temporary, JsonSerializer.Serialize(_data));
                File.Move(temporary, FilePath, overwrite: true);
            }
            catch (Exception error)
            {
                AppLog.Warn("shiny", $"Could not save the hunts: {error.Message}");
            }
        }
    }

    public void Add(Hunt hunt)
    {
        _data.Hunts.Insert(0, hunt);
        Save();
    }

    public void Remove(Hunt hunt)
    {
        _data.Hunts.Remove(hunt);
        Save();
    }

    /// <summary>Moves a finished hunt into the collection and removes the counter.</summary>
    public ShinyCatch Catch(Hunt hunt, string name, int ball, string description)
    {
        hunt.Pause();
        var caught = new ShinyCatch
        {
            Species = hunt.Species,
            Name = name,
            GameId = hunt.GameId,
            MethodId = hunt.MethodId,
            Charm = hunt.Charm,
            Choices = new Dictionary<string, int>(hunt.Choices),
            Encounters = hunt.Encounters,
            TotalMs = hunt.TotalMs,
            CaughtUtc = DateTime.UtcNow,
            Ball = ball,
            Description = description,
        };
        _data.Hunts.Remove(hunt);
        _data.Catches.Insert(0, caught);
        Save();
        return caught;
    }

    public void Remove(ShinyCatch caught)
    {
        _data.Catches.Remove(caught);
        Save();
    }
}
