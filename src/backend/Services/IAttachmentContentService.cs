using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using A = DocumentFormat.OpenXml.Drawing;

namespace VeraMedia.Api.Services;

public interface IAttachmentContentService
{
    Task<IReadOnlyList<AttachmentContentResult>> ExtractAsync(
        IReadOnlyList<Contracts.AttachmentDto>? attachments,
        CancellationToken cancellationToken);
}

public sealed record AttachmentContentResult(
    string FileName,
    string ContentType,
    long Size,
    string Url,
    string? Text,
    string? Error);

public sealed partial class AttachmentContentService(IWebHostEnvironment environment) : IAttachmentContentService
{
    private const int MaxCharsPerFile = 20000;
    private const int MaxTotalChars = 60000;
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".csv", ".tsv",
        ".js", ".jsx", ".ts", ".tsx", ".py", ".cs", ".java", ".go", ".rs", ".c", ".cpp", ".h", ".sql", ".html", ".css"
    };

    public async Task<IReadOnlyList<AttachmentContentResult>> ExtractAsync(
        IReadOnlyList<Contracts.AttachmentDto>? attachments,
        CancellationToken cancellationToken)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return [];
        }

        var results = new List<AttachmentContentResult>();
        var totalChars = 0;
        foreach (var attachment in attachments)
        {
            if (totalChars >= MaxTotalChars)
            {
                results.Add(attachment.ToAttachmentResult(null, "附件内容已达到本次可读取上限，后续文件未展开。"));
                continue;
            }

            var result = await ExtractOneAsync(attachment, Math.Min(MaxCharsPerFile, MaxTotalChars - totalChars), cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.Text))
            {
                totalChars += result.Text.Length;
            }

            results.Add(result);
        }

        return results;
    }

    private async Task<AttachmentContentResult> ExtractOneAsync(
        Contracts.AttachmentDto attachment,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var path = ResolveUploadPath(attachment.Url);
        if (path is null || !File.Exists(path))
        {
            return attachment.ToAttachmentResult(null, "未找到服务端上传文件。");
        }

        var extension = Path.GetExtension(path);
        try
        {
            var text = extension.ToLowerInvariant() switch
            {
                ".docx" => ReadDocx(path),
                ".xlsx" => ReadXlsx(path),
                ".pptx" => ReadPptx(path),
                ".pdf" => await ReadPdfAsync(path, cancellationToken),
                ".doc" or ".xls" or ".ppt" => await ReadLegacyOfficeAsync(path, cancellationToken),
                _ when PlainTextExtensions.Contains(extension) => await ReadPlainTextAsync(path, cancellationToken),
                _ => null
            };

            text = NormalizeText(text, maxChars);
            return string.IsNullOrWhiteSpace(text)
                ? attachment.ToAttachmentResult(null, "暂未提取到可用文本内容。")
                : attachment.ToAttachmentResult(text, null);
        }
        catch (Exception ex)
        {
            return attachment.ToAttachmentResult(null, $"附件解析失败：{ex.Message}");
        }
    }

    private string? ResolveUploadPath(string url)
    {
        var pathPart = url;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            pathPart = uri.AbsolutePath;
        }

        pathPart = Uri.UnescapeDataString(pathPart).Replace('\\', '/');
        if (!pathPart.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var fileName = Path.GetFileName(pathPart);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var uploadRoot = Path.GetFullPath(Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "uploads"));
        var fullPath = Path.GetFullPath(Path.Combine(uploadRoot, fileName));
        return fullPath.StartsWith(uploadRoot, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    private static async Task<string> ReadPlainTextAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static string ReadDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return string.Join("\n", doc.MainDocumentPart?.Document?.Body?.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>()
            .Select(x => x.Text) ?? []);
    }

    private static string ReadPptx(string path)
    {
        using var doc = PresentationDocument.Open(path, false);
        var lines = new List<string>();
        foreach (var slidePart in doc.PresentationPart?.SlideParts ?? [])
        {
            lines.AddRange(slidePart.Slide?.Descendants<A.Text>().Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x)) ?? []);
            var notes = slidePart.NotesSlidePart?.NotesSlide?.Descendants<A.Text>().Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x));
            if (notes is not null) lines.AddRange(notes);
        }

        return string.Join("\n", lines);
    }

    private static string ReadXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var sharedStrings = doc.WorkbookPart?.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>()
            .Select(x => x.InnerText)
            .ToArray() ?? [];
        var rows = new List<string>();
        foreach (var worksheetPart in doc.WorkbookPart?.WorksheetParts ?? [])
        {
            foreach (var row in worksheetPart.Worksheet?.Descendants<Row>() ?? [])
            {
                var values = row.Elements<Cell>().Select(cell => ReadCell(cell, sharedStrings)).ToArray();
                if (values.Any(x => !string.IsNullOrWhiteSpace(x)))
                {
                    rows.Add(string.Join("\t", values));
                }
            }
        }

        return string.Join("\n", rows);
    }

    private static string ReadCell(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        var value = cell.CellValue?.Text ?? "";
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(value, out var index) && index >= 0 && index < sharedStrings.Count)
        {
            return sharedStrings[index];
        }

        return value;
    }

    private static async Task<string?> ReadPdfAsync(string path, CancellationToken cancellationToken)
    {
        var tool = ResolveTool("pdftotext");
        if (tool is null)
        {
            return null;
        }

        return await RunProcessAsync(tool, [path, "-"], Path.GetDirectoryName(path)!, cancellationToken);
    }

    private static async Task<string?> ReadLegacyOfficeAsync(string path, CancellationToken cancellationToken)
    {
        var tool = ResolveTool("soffice") ?? ResolveTool("libreoffice");
        if (tool is null)
        {
            return null;
        }

        var workDir = Path.Combine(Path.GetTempPath(), "veramedia-attachment-text", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            await RunProcessAsync(tool, ["--headless", "--convert-to", "txt:Text", "--outdir", workDir, path], workDir, cancellationToken);
            var txt = Directory.GetFiles(workDir, "*.txt").FirstOrDefault();
            return txt is null ? null : await File.ReadAllTextAsync(txt, cancellationToken);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    private static async Task<string?> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo);
        if (process is null) return null;
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"{fileName} 执行失败。" : stderr.Trim());
        }

        return stdout;
    }

    private static string? ResolveTool(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var candidates = OperatingSystem.IsWindows()
            ? new[] { $"{name}.exe", $"{name}.cmd", name }
            : new[] { name };
        foreach (var dir in paths)
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(dir, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static string? NormalizeText(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = ControlCharsRegex().Replace(text.ReplaceLineEndings("\n"), " ").Trim();
        normalized = ExcessBlankLinesRegex().Replace(normalized, "\n\n");
        return normalized.Length <= maxChars ? normalized : normalized[..maxChars] + "\n\n[内容过长，已截断]";
    }

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F]+")]
    private static partial Regex ControlCharsRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLinesRegex();
}

file static class AttachmentContentExtensions
{
    public static AttachmentContentResult ToAttachmentResult(this Contracts.AttachmentDto attachment, string? text, string? error) =>
        new(attachment.FileName, attachment.ContentType, attachment.Size, attachment.Url, text, error);
}
