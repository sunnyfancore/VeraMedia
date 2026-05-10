using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VeraMedia.Api.Services;

public interface IPptVideoConversionService
{
    Task<PptVideoConversionResult> ConvertAsync(
        PptVideoConversionRequest request,
        Action<string, int, int, int>? onProgress,
        CancellationToken cancellationToken,
        Action<PptVideoEncoderInfo>? onEncoderSelected = null);
}

public sealed record PptVideoConversionRequest(
    string PptxPath,
    string OutputDir,
    int SecondsPerSlide,
    string Voice,
    string Speed,
    string Resolution,
    string? BgmPath,
    int Volume,
    Dictionary<int, string>? OverrideNotes,
    string? RenderedSlidesDir = null,
    string? DubbingMode = null,
    Dictionary<string, string>? DialogueVoices = null);

public sealed record PptVideoEncoderInfo(string Encoder, string Mode, string Label, string? Device);

public sealed record PptVideoConversionResult(string VideoPath, string FileName, int SlideCount, PptVideoEncoderInfo EncoderInfo);

internal sealed record PptVideoSegmentSpec(int Index, string Image, string? Audio, string? Subtitle, double Duration, string Output);
internal sealed record PptVideoDialogueTurn(string Speaker, string Text);

public sealed class PptVideoConversionService(EdgeTtsClient ttsClient, IPptxThumbnailRenderer pptxThumbnailRenderer, ILibreOfficeService libreOfficeService, ILogger<PptVideoConversionService> logger) : IPptVideoConversionService
{
    private const string VaapiEncoder = "h264_vaapi";
    private const string DefaultVaapiDevice = "/dev/dri/renderD128";
    private const string EncoderOverrideEnv = "VERAMEDIA_VIDEO_ENCODER";
    private const string VaapiParallelismEnv = "VERAMEDIA_VAAPI_PARALLELISM";
    private const string VaapiSingleProcessEnv = "VERAMEDIA_VAAPI_SINGLE_PROCESS";
    private static string? _cachedEncoder;
    private static readonly object EncoderLock = new();
    private static readonly Dictionary<string, string> VoiceMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zh"] = "zh-CN-XiaoxiaoNeural",
        ["zh-f"] = "zh-CN-XiaoxiaoNeural",
        ["zh-m"] = "zh-CN-YunxiNeural",
        ["zh-news"] = "zh-CN-YunyangNeural",
        ["zh-story"] = "zh-CN-XiaoyiNeural",
        ["zh-gentle"] = "zh-CN-XiaochenNeural",
        ["zh-cheerful"] = "zh-CN-XiaoxuanNeural",
        ["zh-boy"] = "zh-CN-YunfengNeural",
        ["zh-senior"] = "zh-CN-YunjianNeural",
        ["en"] = "en-US-JennyNeural",
        ["en-f"] = "en-US-JennyNeural",
        ["en-m"] = "en-US-GuyNeural",
        ["en-aria"] = "en-US-AriaNeural",
        ["en-davis"] = "en-US-DavisNeural",
        ["en-gb"] = "en-GB-SoniaNeural",
        ["en-gb-m"] = "en-GB-RyanNeural",
        ["ja"] = "ja-JP-NanamiNeural",
        ["ja-m"] = "ja-JP-KeitaNeural",
        ["ko"] = "ko-KR-SunHiNeural",
        ["ko-m"] = "ko-KR-InJoonNeural",
    };

    private static readonly Dictionary<string, string> SpeedMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0.75"] = "-25%",
        ["0.5"] = "-50%",
        ["1.0"] = "+0%",
        ["1.25"] = "+25%",
        ["1.5"] = "+50%",
        ["2.0"] = "+100%",
    };

    private static readonly Dictionary<string, string> ResolutionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["480p"] = "854:480",
        ["720p"] = "1280:720",
        ["1080p"] = "1920:1080",
        ["2k"] = "2560:1440",
    };

    public async Task<PptVideoConversionResult> ConvertAsync(
        PptVideoConversionRequest request,
        Action<string, int, int, int>? onProgress,
        CancellationToken cancellationToken,
        Action<PptVideoEncoderInfo>? onEncoderSelected = null)
    {
        var pptxPath = request.PptxPath;
        if (!File.Exists(pptxPath))
            throw new InvalidOperationException($"PPTX not found: {pptxPath}");

        var voice = VoiceMap.GetValueOrDefault(request.Voice, "zh-CN-XiaoxiaoNeural");
        var rate = SpeedMap.GetValueOrDefault(request.Speed, "+0%");
        var videoSize = ResolutionMap.GetValueOrDefault(request.Resolution, "1280:720");
        var renderDpi = GetRenderDpi(request.Resolution);
        var dialogueEnabled = string.Equals(request.DubbingMode, "dialogue", StringComparison.OrdinalIgnoreCase);

        Directory.CreateDirectory(request.OutputDir);
        var workDir = Path.Combine(request.OutputDir, "tmp");
        Directory.CreateDirectory(workDir);

        // Step 1: Extract speaker notes
        onProgress?.Invoke("提取演讲备注", 5, 0, 0);
        logger.LogInformation("Extracting speaker notes from {Path}", pptxPath);
        var notes = ExtractSpeakerNotes(pptxPath);
        if (request.OverrideNotes is { Count: > 0 })
        {
            foreach (var (idx, text) in request.OverrideNotes)
                notes[idx] = text;
        }

        // Step 2+3: Render slides and generate TTS IN PARALLEL (they are independent)
        onProgress?.Invoke("渲染画面 & 生成语音", 10, 0, 0);
        var expectedSlideCount = notes.Count > 0 ? notes.Keys.Max() : 0;

        // Start TTS immediately — it only depends on notes, not rendered images
        var audioDir = Path.Combine(request.OutputDir, "audio");
        Directory.CreateDirectory(audioDir);
        logger.LogInformation("Starting TTS generation for {Count} slides (parallel with rendering)", expectedSlideCount);
        var ttsTask = GenerateAllAudiosAsync(notes, expectedSlideCount, request.Voice, rate, audioDir, dialogueEnabled, request.DialogueVoices, cancellationToken);

        // Render slides concurrently
        logger.LogInformation("Rendering slides to images");
        List<string> slides;
        if (TryReuseRenderedSlides(request.RenderedSlidesDir, request.OutputDir, out slides))
        {
            logger.LogInformation("Reused {Count} pre-rendered slide frames from {Dir}", slides.Count, request.RenderedSlidesDir);
        }
        else
        {
            try
            {
                var pdfPath = await ConvertToPdfAsync(pptxPath, workDir, cancellationToken);
                onProgress?.Invoke("渲染幻灯片画面", 20, 0, 0);
                slides = RenderSlides(pdfPath, request.OutputDir, renderDpi);
                logger.LogInformation("Rendered {Count} slides via high-fidelity pipeline", slides.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "High-fidelity rendering failed, falling back to direct rendering");
                var (renderWidth, renderHeight) = ParseVideoResolution(request.Resolution);
                var thumbnails = pptxThumbnailRenderer.RenderAllSlides(pptxPath, renderWidth, renderHeight);
                var slidesDir = Path.Combine(request.OutputDir, "slides");
                Directory.CreateDirectory(slidesDir);
                slides = [];
                for (var i = 0; i < thumbnails.Count; i++)
                {
                    var path = Path.Combine(slidesDir, $"slide-{i + 1:D3}.png");
                    await System.IO.File.WriteAllBytesAsync(path, thumbnails[i].PngData, cancellationToken);
                    slides.Add(path);
                }
                logger.LogInformation("Rendered {Count} slides via fallback pipeline", slides.Count);
            }
        }

        if (slides.Count == 0)
            throw new InvalidOperationException("未能渲染出任何幻灯片。");

        // Wait for TTS to finish
        onProgress?.Invoke("等待语音生成完成", 35, 0, slides.Count);
        var audios = await ttsTask;
        logger.LogInformation("Generated {Count} audio tracks", audios.Count);
        var narratedSlides = Enumerable.Range(1, slides.Count)
            .Count(i => !string.IsNullOrWhiteSpace(notes.GetValueOrDefault(i)));
        if (narratedSlides > 0 && audios.Count == 0)
            throw new InvalidOperationException("旁白生成失败：语音合成服务不可用，请稍后重试。");
        if (audios.Count < narratedSlides)
            throw new InvalidOperationException($"旁白生成不完整：{narratedSlides} 页备注中仅生成 {audios.Count} 页音频，部分语音合成失败。");

        // Step 4: Create video segments (parallel)
        onProgress?.Invoke("合成视频片段", 50, 0, slides.Count);
        logger.LogInformation("Creating video segments in parallel");
        var segmentsDir = Path.Combine(request.OutputDir, "segments");
        Directory.CreateDirectory(segmentsDir);
        var subtitleDir = Path.Combine(request.OutputDir, "subtitles");
        Directory.CreateDirectory(subtitleDir);

        var encoder = DetectBestEncoder();
        var encoderInfo = DescribeEncoder(encoder);
        onEncoderSelected?.Invoke(encoderInfo);
        logger.LogInformation("Using video encoder: {Encoder} ({Mode})", encoderInfo.Encoder, encoderInfo.Mode);
        var maxParallel = encoder switch
        {
            "libx264" => Environment.ProcessorCount,
            VaapiEncoder => GetVaapiParallelism(),
            _ => Math.Max(Environment.ProcessorCount, 4)
        };
        var baseName = Path.GetFileNameWithoutExtension(pptxPath);
        var mergedPath = Path.Combine(workDir, $"{baseName}-merged.mp4");
        var mergedCreated = false;
        if (encoder == VaapiEncoder && ShouldUseVaapiSingleProcess())
        {
            try
            {
                onProgress?.Invoke("VAAPI 单进程合成视频", 52, 0, slides.Count);
                var specs = await BuildSegmentSpecsAsync(slides.Count, request.OutputDir, request.SecondsPerSlide, audios, notes, segmentsDir, subtitleDir, cancellationToken);
                await CreateVaapiPresentationAsync(specs, videoSize, mergedPath, cancellationToken);
                mergedCreated = File.Exists(mergedPath);
                onProgress?.Invoke("VAAPI 视频合成完成", 90, slides.Count, slides.Count);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Single-process VAAPI path failed; falling back to segmented encoding");
                try { if (File.Exists(mergedPath)) File.Delete(mergedPath); } catch { }
                mergedCreated = false;
            }
        }

        if (!mergedCreated)
        {
        var completedSegments = new (int Index, string Path)[slides.Count];
        var encodingCount = 0;
        var segmentSemaphore = new SemaphoreSlim(maxParallel);
        var segmentTasks = new List<Task>();

        for (var i = 1; i <= slides.Count; i++)
        {
            var slideIdx = i;
            var idx = $"{i:D3}";
            var image = Path.Combine(request.OutputDir, "slides", $"slide-{idx}.png");
            var segment = Path.Combine(segmentsDir, $"segment-{idx}.mp4");
            var audio = audios.GetValueOrDefault(i);
            var noteText = notes.GetValueOrDefault(i, "").Trim();

            segmentTasks.Add(Task.Run(async () =>
            {
                await segmentSemaphore.WaitAsync(cancellationToken);
                try
                {
                    if (!File.Exists(image))
                    {
                        logger.LogWarning("Slide image not found: {Path}, skipping segment", image);
                        return;
                    }

                    var duration = audio is not null
                        ? await GetAudioDurationAsync(audio, cancellationToken)
                        : request.SecondsPerSlide;
                    if (duration <= 0) duration = request.SecondsPerSlide;
                    var subtitle = WriteSlideSubtitle(noteText, duration, Path.Combine(subtitleDir, $"slide-{idx}.srt"));

                    Interlocked.Increment(ref encodingCount);
                    lock (completedSegments)
                    {
                        var done = completedSegments.Count(s => s.Path is not null);
                        var active = Volatile.Read(ref encodingCount);
                        onProgress?.Invoke($"正在编码视频片段 (并行 {active} 个)", 50 + (int)(40.0 * done / slides.Count), done, slides.Count);
                    }

                    try
                    {
                        await CreateSegmentAsync(image, audio, subtitle, duration, videoSize, segment, encoder, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to create segment for slide {Index}", slideIdx);
                        Interlocked.Decrement(ref encodingCount);
                        return;
                    }

                    Interlocked.Decrement(ref encodingCount);

                    if (!File.Exists(segment))
                    {
                        logger.LogWarning("Segment file not created for slide {Index}", slideIdx);
                        return;
                    }

                    lock (completedSegments)
                    {
                        completedSegments[slideIdx - 1] = (slideIdx, segment);
                        var done = completedSegments.Count(s => s.Path is not null);
                        onProgress?.Invoke("编码视频片段", 50 + (int)(40.0 * done / slides.Count), done, slides.Count);
                    }
                }
                finally
                {
                    segmentSemaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(segmentTasks);

        // Write segment list (only successful segments)
        var validSegments = completedSegments
            .Where(s => s.Path is not null && File.Exists(s.Path))
            .OrderBy(s => s.Index)
            .ToList();

        if (validSegments.Count == 0)
            throw new InvalidOperationException("所有视频片段均创建失败。");

        var segmentListPath = Path.Combine(request.OutputDir, "segments.txt");
        await using (var segmentWriter = new StreamWriter(segmentListPath, false))
        {
            foreach (var seg in validSegments)
                await segmentWriter.WriteLineAsync($"file '{EscapeConcatPath(seg.Path)}'");
        }

        // Step 5: Merge segments
        onProgress?.Invoke("合并视频片段", 92, 0, 0);
        logger.LogInformation("Merging {Count} video segments", slides.Count);
        await MergeSegmentsAsync(segmentListPath, mergedPath, cancellationToken);
        }

        // Step 6: Mix BGM if provided
        var outputPath = Path.Combine(request.OutputDir, $"{baseName}.mp4");
        if (!string.IsNullOrWhiteSpace(request.BgmPath) && File.Exists(request.BgmPath))
        {
            onProgress?.Invoke("混合背景音乐", 96, 0, 0);
            logger.LogInformation("Mixing background music");
            await MixBgmAsync(mergedPath, request.BgmPath, request.Volume, outputPath, cancellationToken);
        }
        else
        {
            File.Move(mergedPath, outputPath);
        }

        onProgress?.Invoke("完成", 100, 0, 0);
        logger.LogInformation("PPT video conversion complete: {Path}", outputPath);
        return new PptVideoConversionResult(outputPath, Path.GetFileName(outputPath), slides.Count, encoderInfo);
    }

    private static bool TryReuseRenderedSlides(string? sourceSlidesDir, string outputDir, out List<string> slides)
    {
        slides = [];
        if (string.IsNullOrWhiteSpace(sourceSlidesDir) || !Directory.Exists(sourceSlidesDir))
            return false;

        var sourceSlides = Directory.GetFiles(sourceSlidesDir, "slide-*.png")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sourceSlides.Count == 0)
            return false;

        var targetSlidesDir = Path.Combine(outputDir, "slides");
        Directory.CreateDirectory(targetSlidesDir);

        for (var i = 0; i < sourceSlides.Count; i++)
        {
            var target = Path.Combine(targetSlidesDir, $"slide-{i + 1:D3}.png");
            if (!string.Equals(Path.GetFullPath(sourceSlides[i]), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(sourceSlides[i], target, overwrite: true);
            slides.Add(target);
        }

        return true;
    }

    private static async Task<double> GetAudioDurationAsync(string path, CancellationToken ct)
    {
        try
        {
            var result = await RunProcessAsync("ffprobe",
                ["-v", "error", "-show_entries", "format=duration",
                 "-of", "default=noprint_wrappers=1:nokey=1", path], ct);
            return double.TryParse(result.Trim(), out var d) ? d : 0;
        }
        catch { return 0; }
    }

    private static async Task<List<PptVideoSegmentSpec>> BuildSegmentSpecsAsync(
        int slideCount,
        string outputDir,
        int secondsPerSlide,
        Dictionary<int, string> audios,
        Dictionary<int, string> notes,
        string segmentsDir,
        string subtitleDir,
        CancellationToken ct)
    {
        var specs = new List<PptVideoSegmentSpec>(slideCount);
        for (var i = 1; i <= slideCount; i++)
        {
            var idx = $"{i:D3}";
            var image = Path.Combine(outputDir, "slides", $"slide-{idx}.png");
            if (!File.Exists(image))
                throw new FileNotFoundException($"Slide image not found: {image}", image);

            var audio = audios.GetValueOrDefault(i);
            var duration = audio is not null
                ? await GetAudioDurationAsync(audio, ct)
                : secondsPerSlide;
            if (duration <= 0) duration = secondsPerSlide;

            var noteText = notes.GetValueOrDefault(i, "").Trim();
            var subtitle = WriteSlideSubtitle(noteText, duration, Path.Combine(subtitleDir, $"slide-{idx}.srt"));
            var segment = Path.Combine(segmentsDir, $"segment-{idx}.mp4");
            specs.Add(new PptVideoSegmentSpec(i, image, audio, subtitle, duration, segment));
        }

        return specs;
    }

    private static async Task CreateVaapiPresentationAsync(
        IReadOnlyList<PptVideoSegmentSpec> specs,
        string videoSize,
        string output,
        CancellationToken ct)
    {
        if (specs.Count == 0)
            throw new InvalidOperationException("No video segments to encode.");

        var args = new List<string>
        {
            "-y", "-loglevel", "error",
            "-vaapi_device", GetVaapiDevice(),
        };

        foreach (var spec in specs)
        {
            args.AddRange([
                "-loop", "1",
                "-framerate", "24",
                "-t", FormatSeconds(spec.Duration),
                "-i", spec.Image,
            ]);

            if (spec.Audio is not null && File.Exists(spec.Audio))
            {
                args.AddRange(["-i", spec.Audio]);
            }
            else
            {
                args.AddRange([
                    "-f", "lavfi",
                    "-t", FormatSeconds(spec.Duration),
                    "-i", "anullsrc=channel_layout=stereo:sample_rate=44100"
                ]);
            }
        }

        var filters = new List<string>();
        var concatInputs = new List<string>();
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var imageInput = i * 2;
            var audioInput = imageInput + 1;
            var vf = $"[{imageInput}:v]scale={videoSize}:force_original_aspect_ratio=decrease,pad={videoSize}:(ow-iw)/2:(oh-ih)/2,setsar=1";
            if (!string.IsNullOrWhiteSpace(spec.Subtitle) && File.Exists(spec.Subtitle))
                vf += $",subtitles='{EscapeFfmpegFilterPath(spec.Subtitle)}':force_style='FontName=Noto Sans CJK SC,FontSize=18,PrimaryColour=&H00FFFFFF,OutlineColour=&HAA000000,BorderStyle=1,Outline=1,Shadow=0,Alignment=2,MarginV=10'";
            vf += $",fps=24,trim=duration={FormatSeconds(spec.Duration)},setpts=PTS-STARTPTS,format=yuv420p[v{i}]";
            filters.Add(vf);

            filters.Add($"[{audioInput}:a]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo,atrim=0:{FormatSeconds(spec.Duration)},asetpts=PTS-STARTPTS[a{i}]");
            concatInputs.Add($"[v{i}][a{i}]");
        }

        filters.Add($"{string.Concat(concatInputs)}concat=n={specs.Count}:v=1:a=1[vcat][aout]");
        filters.Add("[vcat]format=nv12,hwupload[vout]");

        args.AddRange([
            "-filter_complex", string.Join(";", filters),
            "-map", "[vout]",
            "-map", "[aout]",
            "-c:v", VaapiEncoder,
            "-qp", "23",
            "-c:a", "aac",
            "-movflags", "+faststart",
            output
        ]);

        await RunProcessAsync("ffmpeg", [.. args], ct);
    }

    private static async Task CreateSegmentAsync(
        string image, string? audio, string? subtitle, double duration, string videoSize,
        string output, string encoder, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-y", "-loglevel", "error",
        };

        if (encoder == VaapiEncoder)
            args.AddRange(["-vaapi_device", GetVaapiDevice()]);

        args.AddRange([
            "-loop", "1", "-framerate", "24", "-i", image,
        ]);

        if (audio is not null && File.Exists(audio))
            args.AddRange(["-i", audio]);
        else
            args.AddRange(["-f", "lavfi", "-i", $"anullsrc=channel_layout=stereo:sample_rate=44100"]);

        var filters = $"scale={videoSize}:force_original_aspect_ratio=decrease,pad={videoSize}:(ow-iw)/2:(oh-ih)/2";
        if (!string.IsNullOrWhiteSpace(subtitle) && File.Exists(subtitle))
            filters += $",subtitles='{EscapeFfmpegFilterPath(subtitle)}':force_style='FontName=Noto Sans CJK SC,FontSize=18,PrimaryColour=&H00FFFFFF,OutlineColour=&HAA000000,BorderStyle=1,Outline=1,Shadow=0,Alignment=2,MarginV=10'";
        if (encoder == VaapiEncoder)
            filters += ",format=nv12,hwupload";

        args.AddRange([
            "-t", duration.ToString("F2"),
            "-vf", filters,
            "-c:v", encoder,
        ]);

        if (encoder == "libx264")
            args.AddRange(["-preset", "ultrafast", "-tune", "stillimage", "-crf", "28", "-threads", "2"]);
        else if (encoder == "h264_nvenc")
            args.AddRange(["-preset", "p1", "-tune", "hq", "-cq", "28"]);
        else if (encoder == VaapiEncoder)
            args.AddRange(["-qp", "23"]);

        if (encoder != VaapiEncoder)
            args.AddRange(["-pix_fmt", "yuv420p"]);

        args.AddRange(["-c:a", "aac", "-shortest", output]);

        await RunProcessAsync("ffmpeg", [.. args], ct);
    }

    private static string? WriteSlideSubtitle(string text, double duration, string outputPath)
    {
        var cues = BuildSubtitleCues(text);
        if (cues.Count == 0)
            return null;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var writer = new StreamWriter(outputPath, false);

        var totalChars = cues.Sum(c => c.Length);
        var usable = Math.Max(duration - 0.15, 0.5);
        var elapsed = 0.0;

        for (var i = 0; i < cues.Count; i++)
        {
            var ratio = (double)cues[i].Length / totalChars;
            var cueDur = Math.Max(usable * ratio, 0.3);
            var start = elapsed;
            var end = i == cues.Count - 1 ? usable : Math.Min(elapsed + cueDur, usable);

            writer.WriteLine(i + 1);
            writer.WriteLine($"{FormatSrtTime(start)} --> {FormatSrtTime(end)}");
            writer.WriteLine(cues[i]);
            writer.WriteLine();

            elapsed = end;
        }
        return outputPath;
    }

    private static List<string> BuildSubtitleCues(string text)
    {
        text = text
            .Replace("\r", "")
            .Replace("<#>", "", StringComparison.Ordinal)
            .Replace("&lt;#&gt;", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (string.IsNullOrWhiteSpace(text))
            return [];

        text = Regex.Replace(text, @"\s+", " ").Trim();

        const int maxLen = 35;
        var cues = new List<string>();

        foreach (var raw in Regex.Split(text, @"(?<=[。！？!?.;])"))
        {
            var sentence = raw.Trim();
            if (sentence.Length == 0) continue;

            if (sentence.Length <= maxLen)
            {
                cues.Add(sentence);
                continue;
            }

            var current = "";
            foreach (var part in Regex.Split(sentence, @"(?<=[，,、；：:])"))
            {
                var clause = part.Trim();
                if (clause.Length == 0) continue;

                if (current.Length > 0 && current.Length + clause.Length > maxLen)
                {
                    cues.Add(current);
                    current = clause;
                }
                else
                {
                    current += clause;
                }
            }

            if (current.Length > 0)
            {
                if (current.Length <= maxLen)
                {
                    cues.Add(current);
                }
                else
                {
                    for (var offset = 0; offset < current.Length; offset += maxLen)
                        cues.Add(current[offset..Math.Min(offset + maxLen, current.Length)]);
                }
            }
        }

        return cues.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
    }

    private static string FormatSrtTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }

    private static async Task MergeSegmentsAsync(string segmentList, string output, CancellationToken ct)
    {
        await RunProcessAsync("ffmpeg",
            ["-y", "-loglevel", "error",
             "-f", "concat", "-safe", "0", "-i", segmentList,
             "-c", "copy",
             output], ct);
    }

    private static string EscapeConcatPath(string path)
    {
        return path.Replace("\\", "/").Replace("'", "'\\''");
    }

    private static string EscapeFfmpegFilterPath(string path)
    {
        return path
            .Replace("\\", "/")
            .Replace(":", "\\:")
            .Replace("'", "\\'");
    }

    private static async Task MixBgmAsync(string video, string bgm, int volume, string output, CancellationToken ct)
    {
        var gain = Math.Clamp(volume, 0, 100) / 100.0;
        await RunProcessAsync("ffmpeg",
            ["-y", "-loglevel", "error",
             "-i", video, "-stream_loop", "-1", "-i", bgm,
             "-filter_complex", $"[1:a]volume={gain:F2}[bgm];[0:a][bgm]amix=inputs=2:duration=first:dropout_transition=2[a]",
             "-map", "0:v", "-map", "[a]",
             "-c:v", "copy", "-c:a", "aac", "-ar", "44100", "-ac", "2",
             output], ct);
    }

    private async Task<Dictionary<int, string>> GenerateAllAudiosAsync(
        Dictionary<int, string> notes, int totalSlides, string voiceKey, string rate,
        string audioDir, bool dialogueEnabled, IReadOnlyDictionary<string, string>? dialogueVoices, CancellationToken ct)
    {
        var audios = new Dictionary<int, string>();
        string? firstErrorMsg = null;
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(8);

        var edgeVoice = VoiceMap.GetValueOrDefault(voiceKey, "zh-CN-XiaoxiaoNeural");
        logger.LogInformation("Using Edge TTS for voice {Voice} ({EdgeVoice})", voiceKey, edgeVoice);

        for (var i = 1; i <= totalSlides; i++)
        {
            var text = notes.GetValueOrDefault(i, "").Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            var idx = i;
            var audioPath = Path.Combine(audioDir, $"slide-{idx:D3}.mp3");

            tasks.Add(Task.Run(async () =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    audioPath = await GenerateSlideAudioAsync(text, idx, voiceKey, edgeVoice, rate, audioDir, dialogueEnabled, dialogueVoices, ct);
                    lock (audios) { audios[idx] = audioPath; }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "TTS failed for slide {Index}", idx);
                    lock (audios)
                    {
                        firstErrorMsg ??= ex.InnerException?.Message ?? ex.Message;
                    }
                }
                finally { semaphore.Release(); }
            }, ct));
        }

        await Task.WhenAll(tasks);
        if (audios.Count == 0 && firstErrorMsg is not null)
        {
            throw new InvalidOperationException($"语音合成失败：{firstErrorMsg}");
        }
        return audios;
    }

    private async Task<string> GenerateSlideAudioAsync(
        string text,
        int slideIndex,
        string defaultVoiceKey,
        string defaultEdgeVoice,
        string rate,
        string audioDir,
        bool dialogueEnabled,
        IReadOnlyDictionary<string, string>? dialogueVoices,
        CancellationToken ct)
    {
        var outputPath = Path.Combine(audioDir, $"slide-{slideIndex:D3}.mp3");
        if (!dialogueEnabled)
        {
            await ttsClient.SynthesizeToFileAsync(text, defaultEdgeVoice, rate, outputPath, ct);
            return outputPath;
        }

        var turns = ParseDialogueTurns(text);
        if (turns.Count == 0)
        {
            await ttsClient.SynthesizeToFileAsync(text, defaultEdgeVoice, rate, outputPath, ct);
            return outputPath;
        }

        if (turns.Count == 1)
        {
            var singleVoice = ResolveDialogueEdgeVoice(turns[0].Speaker, defaultVoiceKey, dialogueVoices, 0);
            await ttsClient.SynthesizeToFileAsync(turns[0].Text, singleVoice, rate, outputPath, ct);
            return outputPath;
        }

        var turnFiles = new List<string>(turns.Count);
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (string.IsNullOrWhiteSpace(turn.Text)) continue;

            var turnPath = Path.Combine(audioDir, $"slide-{slideIndex:D3}-turn-{i + 1:D2}.mp3");
            var edgeVoice = ResolveDialogueEdgeVoice(turn.Speaker, defaultVoiceKey, dialogueVoices, i);
            await ttsClient.SynthesizeToFileAsync(turn.Text, edgeVoice, rate, turnPath, ct);
            turnFiles.Add(turnPath);
        }

        if (turnFiles.Count == 0)
        {
            await ttsClient.SynthesizeToFileAsync(text, defaultEdgeVoice, rate, outputPath, ct);
            return outputPath;
        }

        if (turnFiles.Count == 1)
        {
            File.Copy(turnFiles[0], outputPath, overwrite: true);
            return outputPath;
        }

        var pausePath = Path.Combine(audioDir, $"slide-{slideIndex:D3}-pause.mp3");
        await CreateDialoguePauseAsync(pausePath, ct);
        await ConcatDialogueAudioAsync(turnFiles, pausePath, outputPath, Path.Combine(audioDir, $"slide-{slideIndex:D3}-dialogue.txt"), ct);
        return outputPath;
    }

    private static async Task CreateDialoguePauseAsync(string outputPath, CancellationToken ct)
    {
        if (File.Exists(outputPath)) return;

        await RunProcessAsync("ffmpeg",
            ["-y", "-loglevel", "error",
             "-f", "lavfi", "-t", "0.35", "-i", "anullsrc=channel_layout=stereo:sample_rate=44100",
             "-q:a", "9", "-acodec", "libmp3lame", outputPath], ct);
    }

    private static async Task ConcatDialogueAudioAsync(
        IReadOnlyList<string> turnFiles,
        string pausePath,
        string outputPath,
        string listPath,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(listPath)!);
        await using (var writer = new StreamWriter(listPath, false))
        {
            for (var i = 0; i < turnFiles.Count; i++)
            {
                await writer.WriteLineAsync($"file '{EscapeConcatPath(turnFiles[i])}'");
                if (i < turnFiles.Count - 1)
                    await writer.WriteLineAsync($"file '{EscapeConcatPath(pausePath)}'");
            }
        }

        await RunProcessAsync("ffmpeg",
            ["-y", "-loglevel", "error",
             "-f", "concat", "-safe", "0", "-i", listPath,
             "-vn", "-ar", "44100", "-ac", "2", "-c:a", "libmp3lame", "-q:a", "4",
             outputPath], ct);
    }

    private static List<PptVideoDialogueTurn> ParseDialogueTurns(string text)
    {
        text = text
            .Replace("\r", "")
            .Replace("<#>", "", StringComparison.Ordinal)
            .Replace("&lt;#&gt;", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (string.IsNullOrWhiteSpace(text)) return [];

        var turns = new List<PptVideoDialogueTurn>();
        var currentSpeaker = "";
        var currentText = new StringBuilder();

        void Flush()
        {
            var spoken = Regex.Replace(currentText.ToString(), @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(spoken))
                turns.Add(new PptVideoDialogueTurn(string.IsNullOrWhiteSpace(currentSpeaker) ? "\u65c1\u767d" : currentSpeaker, spoken));
            currentText.Clear();
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (TryParseDialogueLine(line, out var speaker, out var spoken))
            {
                Flush();
                currentSpeaker = speaker;
                currentText.Append(spoken);
                continue;
            }

            if (currentText.Length > 0)
            {
                currentText.Append(' ').Append(line);
            }
            else
            {
                currentSpeaker = "\u65c1\u767d";
                currentText.Append(line);
            }
        }

        Flush();
        return turns;
    }

    private static bool TryParseDialogueLine(string line, out string speaker, out string spoken)
    {
        speaker = "";
        spoken = "";
        var match = Regex.Match(line, @"^\s*(?:[-*]\s*)?(?<speaker>[\p{L}\p{N}_\-\s]{1,24})\s*[\uFF1A:]\s*(?<text>.+)$");
        if (!match.Success) return false;

        var role = NormalizeSpeaker(match.Groups["speaker"].Value);
        if (string.IsNullOrWhiteSpace(role) || !Regex.IsMatch(role, @"\p{L}")) return false;
        if (role.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return false;

        var text = match.Groups["text"].Value.Trim();
        if (string.IsNullOrWhiteSpace(text)) return false;

        speaker = role;
        spoken = text;
        return true;
    }

    private static string ResolveDialogueEdgeVoice(
        string speaker,
        string defaultVoiceKey,
        IReadOnlyDictionary<string, string>? dialogueVoices,
        int turnIndex)
    {
        var voiceKey = ResolveDialogueVoiceKey(speaker, defaultVoiceKey, dialogueVoices, turnIndex);
        return VoiceMap.GetValueOrDefault(voiceKey, VoiceMap.GetValueOrDefault(defaultVoiceKey, "zh-CN-XiaoxiaoNeural"));
    }

    private static string ResolveDialogueVoiceKey(
        string speaker,
        string defaultVoiceKey,
        IReadOnlyDictionary<string, string>? dialogueVoices,
        int turnIndex)
    {
        var normalized = NormalizeSpeaker(speaker);
        if (dialogueVoices is not null)
        {
            foreach (var (role, voice) in dialogueVoices)
            {
                if (string.IsNullOrWhiteSpace(voice)) continue;
                if (string.Equals(NormalizeSpeaker(role), normalized, StringComparison.OrdinalIgnoreCase))
                    return voice.Trim();
            }
        }

        if (normalized.Contains("\u65c1\u767d", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("narrator", StringComparison.OrdinalIgnoreCase))
            return defaultVoiceKey;

        if (normalized.Contains("\u4e3b\u6301", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\u4e3b\u8bb2", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("host", StringComparison.OrdinalIgnoreCase))
            return defaultVoiceKey;

        if (normalized.Contains("\u5609\u5bbe", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\u540c\u4e8b", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("\u5ba2\u6237", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("guest", StringComparison.OrdinalIgnoreCase))
            return string.Equals(defaultVoiceKey, "zh-m", StringComparison.OrdinalIgnoreCase) ? "zh" : "zh-m";

        return turnIndex % 2 == 0
            ? defaultVoiceKey
            : string.Equals(defaultVoiceKey, "zh-m", StringComparison.OrdinalIgnoreCase) ? "zh" : "zh-m";
    }

    private static string NormalizeSpeaker(string speaker)
    {
        return speaker
            .Replace("[", "", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal)
            .Replace("\u3010", "", StringComparison.Ordinal)
            .Replace("\u3011", "", StringComparison.Ordinal)
            .Trim();
    }

    private static string DetectBestEncoder()
    {
        lock (EncoderLock)
        {
            if (_cachedEncoder is not null) return _cachedEncoder;
            var configuredEncoder = Environment.GetEnvironmentVariable(EncoderOverrideEnv);
            if (!string.IsNullOrWhiteSpace(configuredEncoder) &&
                !string.Equals(configuredEncoder.Trim(), "auto", StringComparison.OrdinalIgnoreCase) &&
                TryProbeEncoder(configuredEncoder.Trim()))
            {
                _cachedEncoder = configuredEncoder.Trim();
                return _cachedEncoder;
            }

            foreach (var candidate in new[] { "h264_nvenc", VaapiEncoder, "h264_qsv" })
            {
                if (!TryProbeEncoder(candidate)) continue;

                _cachedEncoder = candidate;
                return candidate;
            }

            _cachedEncoder = "libx264";
            return "libx264";
        }
    }

    private static bool TryProbeEncoder(string encoder)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in BuildEncoderProbeArgs(encoder))
                p.StartInfo.ArgumentList.Add(arg);
            p.Start();
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string[] BuildEncoderProbeArgs(string encoder)
    {
        if (encoder == VaapiEncoder)
        {
            return [
                "-vaapi_device", GetVaapiDevice(),
                "-f", "lavfi", "-i", "color=c=black:s=64x64:d=0.1",
                "-vf", "format=nv12,hwupload",
                "-c:v", encoder, "-f", "null", "-"
            ];
        }

        return ["-f", "lavfi", "-i", "color=c=black:s=64x64:d=0.1", "-c:v", encoder, "-f", "null", "-"];
    }

    private static PptVideoEncoderInfo DescribeEncoder(string encoder)
    {
        return encoder switch
        {
            VaapiEncoder => new PptVideoEncoderInfo(encoder, "gpu", "GPU (Intel VAAPI)", GetVaapiDevice()),
            "h264_nvenc" => new PptVideoEncoderInfo(encoder, "gpu", "GPU (NVIDIA NVENC)", null),
            "h264_qsv" => new PptVideoEncoderInfo(encoder, "gpu", "GPU (Intel QSV)", null),
            _ => new PptVideoEncoderInfo(encoder, "cpu", "CPU (libx264)", null)
        };
    }

    private static string GetVaapiDevice()
    {
        var configured = Environment.GetEnvironmentVariable("VAAPI_DEVICE");
        return string.IsNullOrWhiteSpace(configured) ? DefaultVaapiDevice : configured.Trim();
    }

    private static int GetVaapiParallelism()
    {
        var configured = Environment.GetEnvironmentVariable(VaapiParallelismEnv);
        if (int.TryParse(configured, out var parallelism))
            return Math.Clamp(parallelism, 1, 8);
        return 4;
    }

    private static bool ShouldUseVaapiSingleProcess()
    {
        var configured = Environment.GetEnvironmentVariable(VaapiSingleProcessEnv);
        return string.IsNullOrWhiteSpace(configured) ||
            configured.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            configured.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            configured.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSeconds(double seconds) =>
        seconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static (int Width, int Height) ParseVideoResolution(string? resolution)
    {
        return resolution?.Trim().ToLowerInvariant() switch
        {
            "480p" => (854, 480),
            "720p" => (1280, 720),
            "1080p" => (1920, 1080),
            "2k" => (2560, 1440),
            _ => (1280, 720)
        };
    }

    private static int GetRenderDpi(string? resolution)
    {
        return resolution?.Trim().ToLowerInvariant() switch
        {
            "480p" => 120,
            "720p" => 150,
            "1080p" => 200,
            "2k" => 300,
            _ => 150
        };
    }

    private static Dictionary<int, string> ExtractSpeakerNotes(string pptxPath)
    {
        // Use the ZIP-based approach (same as the Python script)
        var notes = new Dictionary<int, string>();
        using var archive = System.IO.Compression.ZipFile.OpenRead(pptxPath);

        var presentationRels = ReadRels(archive, "ppt/presentation.xml");
        var orderedSlides = new List<string>();

        var presentationEntry = archive.GetEntry("ppt/presentation.xml");
        if (presentationEntry is not null)
        {
            using var stream = presentationEntry.Open();
            var doc = System.Xml.Linq.XDocument.Load(stream);
            var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
            var rNs = System.Xml.Linq.XNamespace.Get("http://schemas.openxmlformats.org/officeDocument/2006/relationships");

            foreach (var sldId in doc.Root?.Descendants(System.Xml.Linq.XNamespace.Get("http://schemas.openxmlformats.org/presentationml/2006/main") + "sldId") ?? [])
            {
                var rid = sldId.Attribute(rNs + "id")?.Value;
                if (rid is not null && presentationRels.TryGetValue(rid, out var rel))
                    orderedSlides.Add(rel.Path);
            }
        }

        for (var i = 0; i < orderedSlides.Count; i++)
        {
            var slidePart = orderedSlides[i];
            var slideRels = ReadRels(archive, slidePart);
            var notePart = slideRels.Values.FirstOrDefault(r => r.Type.EndsWith("/notesSlide")).Path;

            if (notePart is not null)
            {
                notes[i + 1] = ReadXmlTexts(archive, notePart);
            }
            else
            {
                notes[i + 1] = "";
            }
        }

        return notes;
    }

    private static Dictionary<string, (string Path, string Type)> ReadRels(
        System.IO.Compression.ZipArchive archive, string part)
    {
        var rels = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var relsPath = $"ppt/_rels/{Path.GetFileName(part)}.rels";
        var entry = archive.GetEntry(relsPath);
        if (entry is null) return rels;

        using var stream = entry.Open();
        var doc = System.Xml.Linq.XDocument.Load(stream);
        foreach (var rel in doc.Root?.Elements() ?? [])
        {
            var id = rel.Attribute("Id")?.Value;
            var target = rel.Attribute("Target")?.Value;
            var type = rel.Attribute("Type")?.Value ?? "";
            if (id is not null && target is not null)
            {
                var normalizedPath = target.StartsWith("/") ? target.TrimStart('/') : NormalizePath(part, target);
                rels[id] = (normalizedPath, type);
            }
        }
        return rels;
    }

    private static string NormalizePath(string basePath, string relativePath)
    {
        var dir = Path.GetDirectoryName(basePath)!.Replace('\\', '/');
        var normalized = $"{dir}/{relativePath}";
        // Simple path normalization
        while (normalized.Contains("/./")) normalized = normalized.Replace("/./", "/");
        while (normalized.Contains("/../"))
        {
            var idx = normalized.IndexOf("/../");
            var prevSlash = normalized.LastIndexOf('/', idx - 1);
            if (prevSlash < 0) break;
            normalized = normalized[..prevSlash] + normalized[(idx + 3)..];
        }
        return normalized;
    }

    private static string ReadXmlTexts(System.IO.Compression.ZipArchive archive, string part)
    {
        var entry = archive.GetEntry(part);
        if (entry is null) return "";

        using var stream = entry.Open();
        var doc = System.Xml.Linq.XDocument.Load(stream);
        var aNs = System.Xml.Linq.XNamespace.Get("http://schemas.openxmlformats.org/drawingml/2006/main");

        return string.Join("\n", doc.Descendants(aNs + "t")
            .Select(e => e.Value.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t) && !IsNoise(t)));
    }

    internal static bool IsNoise(string text)
    {
        if (text.Length > 20) return false;
        // ‹#›, <#>, slide number placeholder
        if (text.Contains('‹') || text.Contains('›') || text.Contains("<#>", StringComparison.Ordinal)) return true;
        // Date patterns: 1.7.2013, 1/7/2013, 2013-7-1
        if (Regex.IsMatch(text, @"^\d{1,2}[./-]\d{1,2}[./-]\d{2,4}$")) return true;
        return false;
    }

    internal async Task<string> ConvertToPdfAsync(string pptxPath, string workDir, CancellationToken ct)
    {
        var outDir = Path.Combine(workDir, "pdf");
        return await libreOfficeService.ConvertToPdfAsync(pptxPath, outDir, ct);
    }

    internal static List<string> RenderSlides(string pdfPath, string outputDir, int dpi = 200)
    {
        var slidesDir = Path.Combine(outputDir, "slides");
        Directory.CreateDirectory(slidesDir);

        // First: render all pages in one call to discover page count
        // Then try parallel per-page rendering for speed
        try
        {
            var pageCount = GetPdfPageCount(pdfPath);
            if (pageCount > 0)
            {
                RenderSlidesParallel(pdfPath, slidesDir, dpi, pageCount);
            }
            else
            {
                var outputPattern = Path.Combine(slidesDir, "slide-%03d.png");
                RunProcessSync("mutool", "draw", "-r", dpi.ToString(), "-o", outputPattern, pdfPath);
            }
        }
        catch
        {
            try
            {
                var prefix = Path.Combine(slidesDir, "slide");
                RunProcessSync("pdftoppm", "-png", "-r", dpi.ToString(), pdfPath, prefix);
            }
            catch
            {
                var outputPattern = Path.Combine(slidesDir, "slide-%03d.png");
                RunProcessSync("mutool", "draw", "-r", dpi.ToString(), "-o", outputPattern, pdfPath);
            }
        }

        var images = Directory.GetFiles(slidesDir, "slide-*.png")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<string>();
        for (var i = 0; i < images.Count; i++)
        {
            var newName = Path.Combine(slidesDir, $"slide-{i + 1:D3}.png");
            if (images[i] != newName)
                File.Move(images[i], newName, overwrite: true);
            result.Add(newName);
        }

        return result;
    }

    private static int GetPdfPageCount(string pdfPath)
    {
        try
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "mutool",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            p.StartInfo.ArgumentList.Add("show");
            p.StartInfo.ArgumentList.Add(pdfPath);
            p.StartInfo.ArgumentList.Add("pages");
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10000);
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Count(line => line.TrimStart().StartsWith("page "));
        }
        catch
        {
            return 0;
        }
    }

    private static void RenderSlidesParallel(string pdfPath, string slidesDir, int dpi, int pageCount)
    {
        var maxParallel = Math.Min(pageCount, Environment.ProcessorCount);
        var exceptions = new List<Exception>();

        Parallel.For(1, pageCount + 1, new ParallelOptions { MaxDegreeOfParallelism = maxParallel }, page =>
        {
            var output = Path.Combine(slidesDir, $"slide-{page:D3}.png");
            try
            {
                RunProcessSync("mutool", "draw", "-r", dpi.ToString(), "-o", output, pdfPath, page.ToString());
            }
            catch (Exception ex)
            {
                lock (exceptions) { exceptions.Add(ex); }
            }
        });

        if (exceptions.Count == pageCount)
            throw new AggregateException("All page renders failed", exceptions);
    }

    private static void RunProcessSync(string fileName, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(120000);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"幻灯片渲染失败 (exit {process.ExitCode})");
        }
    }

    private static async Task<string> RunProcessAsync(string fileName, string[] args, CancellationToken ct = default)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var stderr = (await stderrTask).Trim();
        var stdout = (await stdoutTask).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} failed (exit {process.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}");
        }

        return stdout;
    }

}
