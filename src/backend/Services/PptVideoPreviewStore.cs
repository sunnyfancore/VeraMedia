using System.Collections.Concurrent;

namespace VeraMedia.Api.Services;

public interface IPptVideoPreviewStore
{
    string Save(string workDir);
    string? GetSlidePath(string previewId, int slideIndex);
    string? GetSlidesDirectory(string previewId);
    void MarkHqReady(string previewId);
    bool IsHqReady(string previewId);
    void Remove(string previewId);
}

public sealed class PptVideoPreviewStore : IPptVideoPreviewStore, IDisposable
{
    private readonly ConcurrentDictionary<string, (string WorkDir, DateTime CreatedAt)> _previews = new();
    private readonly ConcurrentDictionary<string, bool> _hqReady = new();
    private readonly Timer _cleanupTimer;

    public PptVideoPreviewStore()
    {
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public string Save(string workDir)
    {
        var id = Guid.NewGuid().ToString("N");
        _previews[id] = (workDir, DateTime.UtcNow);
        return id;
    }

    public string? GetSlidePath(string previewId, int slideIndex)
    {
        if (!_previews.TryGetValue(previewId, out var entry)) return null;
        var path = Path.Combine(entry.WorkDir, "slides", $"slide-{slideIndex:D3}.png");
        return File.Exists(path) ? path : null;
    }

    public string? GetSlidesDirectory(string previewId)
    {
        if (!_previews.TryGetValue(previewId, out var entry)) return null;
        var path = Path.Combine(entry.WorkDir, "slides");
        return Directory.Exists(path) && Directory.EnumerateFiles(path, "slide-*.png").Any()
            ? path
            : null;
    }

    public void MarkHqReady(string previewId)
    {
        _hqReady[previewId] = true;
    }

    public bool IsHqReady(string previewId)
    {
        return _hqReady.TryGetValue(previewId, out var ready) && ready;
    }

    public void Remove(string previewId)
    {
        if (_previews.TryRemove(previewId, out var entry))
            TryDeleteWorkDir(entry.WorkDir);
        _hqReady.TryRemove(previewId, out _);
    }

    private void Cleanup(object? state)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, entry) in _previews)
        {
            if (now - entry.CreatedAt > TimeSpan.FromHours(4))
            {
                if (_previews.TryRemove(id, out var removed))
                    TryDeleteWorkDir(removed.WorkDir);
                _hqReady.TryRemove(id, out _);
            }
        }
    }

    private static void TryDeleteWorkDir(string workDir)
    {
        try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
    }

    public void Dispose() => _cleanupTimer.Dispose();
}
