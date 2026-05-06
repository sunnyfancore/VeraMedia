namespace VeraMedia.Api.Services;

public sealed record OfficeExportFile(string FileName, string ContentType, byte[] Content);

public interface IOfficeExportService
{
    OfficeExportFile CreateDocx(string title, string markdown);
    OfficeExportFile CreatePptx(string title, string markdown);
}
