using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>
/// File access for the app's own stored copies of imported saves ("rotomlib:" documents); everything else
/// goes to the platform access. The box screens, the backups and the safe writer need nothing else to work
/// on a stored copy: they only ever see a document id.
/// </summary>
public sealed class LibrarySaveAccess(ISaveFileAccess platform) : ISaveFileAccess
{
    public const string Prefix = "rotomlib:";

    public static string IdFor(SaveEntry entry) => Prefix + entry.FileName;

    public static bool IsLibrary(string documentId) => documentId.StartsWith(Prefix, StringComparison.Ordinal);

    // Only the file name is kept, so an id can never point outside the library folder.
    private static string PathOf(string documentId) =>
        Path.Combine(SaveLibrary.FolderPath, Path.GetFileName(documentId[Prefix.Length..]));

    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default)
    {
        if (!IsLibrary(documentId)) return await platform.ReadAsync(documentId, cancellationToken).ConfigureAwait(false);
        return await File.ReadAllBytesAsync(PathOf(documentId), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (!IsLibrary(documentId))
        {
            await platform.WriteAtomicallyAsync(documentId, bytes, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Written beside the copy and swapped in, so a copy is never left half written.
        var path = PathOf(documentId);
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes.ToArray(), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
