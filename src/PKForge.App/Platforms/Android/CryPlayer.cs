using Android.Media;
using PKForge.App.Services;

namespace PKForge.App.Platforms.Android;

/// <summary>
/// Plays cries on their own MediaPlayer, flagged as game sound and without asking for audio
/// focus, so the background music keeps playing underneath. A new cry replaces the previous one.
/// </summary>
public sealed class CryPlayer : ICryPlayer
{
    private readonly object _gate = new();
    private MediaPlayer? _player;

    public void Play(string path)
    {
        lock (_gate)
        {
            Release();
            try
            {
                var player = new MediaPlayer();
                var attributes = new AudioAttributes.Builder()
                    .SetUsage(AudioUsageKind.Game)!
                    .SetContentType(AudioContentType.Sonification)!
                    .Build()!;
                player.SetAudioAttributes(attributes);
                player.SetDataSource(path);
                player.Prepare();
                player.Completion += (sender, _) =>
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_player, sender)) _player = null;
                    }
                    (sender as MediaPlayer)?.Release();
                };
                player.Start();
                _player = player;
            }
            catch (Exception error)
            {
                AppLog.Warn("cries", $"Cry playback failed: {error.Message}");
                Release();
            }
        }
    }

    private void Release()
    {
        if (_player is null) return;
        try { _player.Stop(); }
        catch (Exception) { /* already stopped */ }
        _player.Release();
        _player = null;
    }
}
