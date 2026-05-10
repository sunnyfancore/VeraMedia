using System.Collections.Concurrent;

namespace VeraMedia.Api.Services;

public sealed class PptVideoTask
{
    public string Id { get; init; } = "";
    public string Status { get; set; } = "pending"; // pending, processing, completed, failed
    public string Stage { get; set; } = "";
    public int Progress { get; set; }
    public int SlideIndex { get; set; }
    public int SlideTotal { get; set; }
    public string? VideoPath { get; set; }
    public string? VideoFileName { get; set; }
    public string? VideoEncoder { get; set; }
    public string? VideoEncoderMode { get; set; }
    public string? VideoEncoderLabel { get; set; }
    public string? VideoEncoderDevice { get; set; }
    public string? Error { get; set; }
    public string WorkDir { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    // Preview-specific fields
    public string? PreviewFileName { get; set; }
    public long PreviewFileSize { get; set; }
    public string? PreviewId { get; set; }
    public List<PptVideoPreviewSlideData>? PreviewSlides { get; set; }
}

public sealed record PptVideoPreviewSlideData(int Index, string Title, string Notes);

public sealed class PptVideoTaskManager : IPptVideoTaskManager, IDisposable
{
    private readonly ConcurrentDictionary<string, PptVideoTask> _tasks = new();
    private readonly Timer _cleanupTimer;

    public PptVideoTaskManager()
    {
        _cleanupTimer = new Timer(Cleanup, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    public PptVideoTask Create(string workDir)
    {
        var task = new PptVideoTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Status = "pending",
            Stage = "准备中",
            WorkDir = workDir,
        };
        _tasks[task.Id] = task;
        return task;
    }

    public PptVideoTask? Get(string taskId) => _tasks.GetValueOrDefault(taskId);

    public void UpdateProgress(string taskId, string stage, int progress, int slideIndex = 0, int slideTotal = 0)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = "processing";
            task.Stage = stage;
            task.Progress = Math.Clamp(progress, 0, 100);
            task.SlideIndex = slideIndex;
            task.SlideTotal = slideTotal;
        }
    }

    public void SetEncoder(string taskId, PptVideoEncoderInfo encoderInfo)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.VideoEncoder = encoderInfo.Encoder;
            task.VideoEncoderMode = encoderInfo.Mode;
            task.VideoEncoderLabel = encoderInfo.Label;
            task.VideoEncoderDevice = encoderInfo.Device;
        }
    }

    public void Complete(string taskId, string videoPath, string fileName, PptVideoEncoderInfo? encoderInfo = null)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            if (encoderInfo is not null)
            {
                task.VideoEncoder = encoderInfo.Encoder;
                task.VideoEncoderMode = encoderInfo.Mode;
                task.VideoEncoderLabel = encoderInfo.Label;
                task.VideoEncoderDevice = encoderInfo.Device;
            }
            task.Status = "completed";
            task.Stage = "完成";
            task.Progress = 100;
            task.VideoPath = videoPath;
            task.VideoFileName = fileName;
        }
    }

    public void CompletePreview(string taskId, string fileName, long fileSize, string? previewId, List<PptVideoPreviewSlideData> slides)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = "completed";
            task.Stage = "完成";
            task.Progress = 100;
            task.PreviewFileName = fileName;
            task.PreviewFileSize = fileSize;
            task.PreviewId = previewId;
            task.PreviewSlides = slides;
        }
    }

    public void Fail(string taskId, string error)
    {
        if (_tasks.TryGetValue(taskId, out var task))
        {
            task.Status = "failed";
            task.Stage = "失败";
            task.Error = error;
        }
    }

    public void Remove(string taskId)
    {
        if (_tasks.TryRemove(taskId, out var removed))
            TryDeleteWorkDir(removed.WorkDir);
    }

    private void Cleanup(object? state)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, task) in _tasks)
        {
            var age = now - task.CreatedAt;
            var terminal = task.Status is "completed" or "failed";
            if (age > TimeSpan.FromHours(6) || (terminal && age > TimeSpan.FromHours(2)))
            {
                if (_tasks.TryRemove(id, out var removed))
                    TryDeleteWorkDir(removed.WorkDir);
            }
        }
    }

    private static void TryDeleteWorkDir(string workDir)
    {
        try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
    }

    public void Dispose() => _cleanupTimer.Dispose();
}

public interface IPptVideoTaskManager
{
    PptVideoTask Create(string workDir);
    PptVideoTask? Get(string taskId);
    void UpdateProgress(string taskId, string stage, int progress, int slideIndex = 0, int slideTotal = 0);
    void SetEncoder(string taskId, PptVideoEncoderInfo encoderInfo);
    void Complete(string taskId, string videoPath, string fileName, PptVideoEncoderInfo? encoderInfo = null);
    void CompletePreview(string taskId, string fileName, long fileSize, string? previewId, List<PptVideoPreviewSlideData> slides);
    void Fail(string taskId, string error);
    void Remove(string taskId);
}
