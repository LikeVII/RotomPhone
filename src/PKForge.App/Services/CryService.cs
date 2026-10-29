using System.Net;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>Plays one cry file. Implemented per platform; it must never stop the background music.</summary>
public interface ICryPlayer
{
    void Play(string path);
}

/// <summary>Which PokeAPI cry file goes with a Pokémon.</summary>
public static class CryCatalog
{
    /// <summary>Highest national dex number the app knows (Paldea DLC included).</summary>
    public const int LastSpecies = 1025;

    /// <summary>
    /// The PokeAPI id of the cry of a look: the form id (e.g. 10035) when the form has its own
    /// HOME render, the species number otherwise. Shiny and gender never change a cry.
    /// </summary>
    public static int IdFor(SpriteLook look)
    {
        var plain = look with { Shiny = false, Traits = look.Traits with { Female = false } };
        var path = SpriteCatalog.Home(plain)?.Path;
        return path is not null && int.TryParse(Path.GetFileNameWithoutExtension(path), out var id) ? id : look.Species;
    }

    /// <summary>Every cry the app can ask for: all species plus every alternate form it has art for.</summary>
    public static IReadOnlyList<int> AllIds()
    {
        var ids = new SortedSet<int>(Enumerable.Range(1, LastSpecies));
        foreach (var look in SpriteCatalog.AllRemoteLooks())
        {
            if (look.Shiny || look.Traits.Female) continue;
            ids.Add(IdFor(look));
        }
        return [.. ids];
    }
}

/// <summary>
/// Pokémon cries from PokeAPI's "latest" set. A cry is downloaded the first time it is needed
/// and kept on the phone; after that it plays offline. A form without its own cry falls back
/// to the species cry. Files that do not exist upstream leave a small marker so they are not
/// asked for again.
/// </summary>
public sealed class CryService(ICryPlayer? player = null)
{
    private const string BaseUrl = "https://raw.githubusercontent.com/PokeAPI/cries/main/cries/pokemon/latest/";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static string Folder => Path.Combine(FileSystem.AppDataDirectory, "cries");

    private static string FilePath(int id) => Path.Combine(Folder, $"{id}.ogg");

    private static string MissingMarker(int id) => Path.Combine(Folder, $"{id}.none");

    /// <summary>True when this cry is on the phone, or known not to exist upstream.</summary>
    public static bool IsSettled(int id) => File.Exists(FilePath(id)) || File.Exists(MissingMarker(id));

    /// <summary>Plays the cry of a Pokémon; downloads it first when it is not on the phone yet.</summary>
    public async Task PlayAsync(SpriteLook look)
    {
        if (player is null) return;
        try
        {
            var id = CryCatalog.IdFor(look);
            var file = await EnsureAsync(id, CancellationToken.None).ConfigureAwait(false);
            if (file is null && id != look.Species)
                file = await EnsureAsync(look.Species, CancellationToken.None).ConfigureAwait(false);
            if (file is not null) player.Play(file);
        }
        catch (Exception error)
        {
            AppLog.Warn("cries", $"Could not play the cry: {error.Message}");
        }
    }

    /// <summary>The local file of a cry, downloading it when needed; null when there is none (or no network).</summary>
    public static async Task<string?> EnsureAsync(int id, CancellationToken cancellationToken)
    {
        var file = FilePath(id);
        if (File.Exists(file)) return file;
        if (File.Exists(MissingMarker(id))) return null;
        try
        {
            Directory.CreateDirectory(Folder);
            using var response = await Http.GetAsync($"{BaseUrl}{id}.ogg", cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                await File.WriteAllBytesAsync(MissingMarker(id), [], cancellationToken).ConfigureAwait(false);
                return null;
            }
            response.EnsureSuccessStatusCode();
            var temporary = $"{file}.{Guid.NewGuid():N}.part";
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(temporary))
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, file, overwrite: true);
            return file;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or TaskCanceledException
                                      && !cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn("cries", $"Cry {id} download failed: {error.Message}");
            return null;
        }
    }
}

/// <summary>
/// "Download all cries": fetches every cry that is not on the phone yet, a bounded number at a
/// time. Purely additive and resumable: finished files are never touched, so cancelling and
/// starting again continues where it stopped.
/// </summary>
public sealed class CryPackDownloader
{
    /// <summary>Rough size of the full set; shown before starting.</summary>
    public const string SizeHint = "~25 MB";

    private const int Parallelism = 8;

    /// <summary>How many cries this phone still lacks; 0 once everything is in.</summary>
    public int CountMissing() => CryCatalog.AllIds().Count(id => !CryService.IsSettled(id));

    /// <summary>Downloads what is missing; returns how many cries could not be fetched (network trouble).</summary>
    public async Task<int> RunAsync(Action<string, double> onProgress, CancellationToken cancellationToken)
    {
        var wanted = CryCatalog.AllIds().Where(id => !CryService.IsSettled(id)).ToList();
        var total = wanted.Count;
        if (total == 0) return 0;
        var done = 0;
        var failed = 0;
        var gate = new SemaphoreSlim(Parallelism);
        var tasks = wanted.Select(async id =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = await CryService.EnsureAsync(id, cancellationToken).ConfigureAwait(false);
                if (file is null && !CryService.IsSettled(id)) Interlocked.Increment(ref failed);
            }
            finally
            {
                gate.Release();
                var current = Interlocked.Increment(ref done);
                if (current % 20 == 0 || current == total)
                    onProgress($"{current} / {total}", (double)current / total);
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return failed;
    }
}
