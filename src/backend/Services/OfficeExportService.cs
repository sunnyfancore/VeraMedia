using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace VeraMedia.Api.Services;

public sealed partial class OfficeExportService : IOfficeExportService
{
    private const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    private const string PptxContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public OfficeExportFile CreateDocx(string title, string markdown)
    {
        using var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new W.Document(new W.Body());
            var body = main.Document.Body!;

            body.Append(CreateWordParagraph(title, 30, true));
            foreach (var block in ParseBlocks(markdown))
            {
                if (string.IsNullOrWhiteSpace(block.Text)) continue;
                if (block.Level is > 0)
                {
                    body.Append(CreateWordParagraph(block.Text, block.Level == 1 ? 26 : 22, true));
                }
                else if (block.IsBullet)
                {
                    body.Append(CreateWordParagraph("- " + block.Text, 21, false));
                }
                else
                {
                    body.Append(CreateWordParagraph(block.Text, 21, false));
                }
            }

            body.Append(new W.SectionProperties(
                new W.PageSize { Width = 11906, Height = 16838 },
                new W.PageMargin { Top = 1134, Right = 1134, Bottom = 1134, Left = 1134 }));
            main.Document.Save();
        }

        return new OfficeExportFile(BuildFileName(title, "docx"), DocxContentType, stream.ToArray());
    }

    public OfficeExportFile CreatePptx(string title, string markdown)
    {
        var slides = BuildSlides(title, markdown);
        using var stream = new MemoryStream();
        using (var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation(
                new P.SlideSize { Cx = 12192000, Cy = 6858000 },
                new P.SlideIdList());
            var slideIdList = presentationPart.Presentation.SlideIdList!;

            uint id = 256;
            foreach (var slide in slides)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                slidePart.Slide = CreateSlide(slide.Title, slide.Items, slide.IsCover);
                slidePart.Slide.Save();
                slideIdList.Append(new P.SlideId { Id = id++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            }

            presentationPart.Presentation.Save();
        }

        return new OfficeExportFile(BuildFileName(title, "pptx"), PptxContentType, stream.ToArray());
    }

    private static W.Paragraph CreateWordParagraph(string text, int size, bool bold)
    {
        var props = new W.RunProperties(new W.FontSize { Val = size.ToString() });
        if (bold) props.Append(new W.Bold());
        return new W.Paragraph(
            new W.ParagraphProperties(new W.SpacingBetweenLines { After = "160", Line = "360", LineRule = W.LineSpacingRuleValues.Auto }),
            new W.Run(props, new W.Text(CleanInline(text)) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static P.Slide CreateSlide(string title, IReadOnlyList<string> items, bool isCover)
    {
        var shapeTree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));

        if (isCover)
        {
            shapeTree.Append(CreateTextShape(2, "Title", title, 720000, 2050000, 10700000, 1350000, 4000, true, false));
            shapeTree.Append(CreateTextShape(3, "Subtitle", string.Join("\n", items), 740000, 3550000, 10400000, 1200000, 1700, false, false));
        }
        else
        {
            shapeTree.Append(CreateTextShape(2, "Title", title, 560000, 520000, 11000000, 900000, 3000, true, false));
            shapeTree.Append(CreateTextShape(3, "Body", string.Join("\n", items), 760000, 1580000, 10600000, 4650000, 1850, false, true));
        }

        return new P.Slide(new P.CommonSlideData(shapeTree), new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static P.Shape CreateTextShape(uint id, string name, string text, long x, long y, long cx, long cy, int fontSize, bool bold, bool bullet)
    {
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => CreateDrawingParagraph(line, bullet, fontSize, bold))
            .ToArray();

        var textBody = new P.TextBody(
            new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
            new A.ListStyle());
        textBody.Append(paragraphs);

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.ShapeProperties(
                new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
            textBody);
    }

    private static A.Paragraph CreateDrawingParagraph(string text, bool bullet, int fontSize, bool bold)
    {
        var paragraphProperties = new A.ParagraphProperties();
        if (bullet) paragraphProperties.Append(new A.CharacterBullet { Char = "-" });

        return new A.Paragraph(
            paragraphProperties,
            new A.Run(
                new A.RunProperties { FontSize = fontSize, Bold = bold },
                new A.Text(CleanInline(text))),
            new A.EndParagraphRunProperties { FontSize = fontSize });
    }

    private static IReadOnlyList<SlideDraft> BuildSlides(string title, string markdown)
    {
        var blocks = ParseBlocks(markdown).Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
        var slides = new List<SlideDraft> { new(title, ["Generated from VeraMedia"], true) };
        var currentTitle = title;
        var currentItems = new List<string>();

        foreach (var block in blocks)
        {
            if (block.Level is > 0)
            {
                AddSlide(slides, currentTitle, currentItems);
                currentTitle = block.Text;
                currentItems = [];
                continue;
            }

            currentItems.Add(block.Text);
            if (currentItems.Count >= 5)
            {
                AddSlide(slides, currentTitle, currentItems);
                currentTitle = "Continued: " + currentTitle;
                currentItems = [];
            }
        }

        AddSlide(slides, currentTitle, currentItems);
        return slides.Take(12).ToList();
    }

    private static void AddSlide(List<SlideDraft> slides, string title, List<string> items)
    {
        if (items.Count == 0) return;
        slides.Add(new SlideDraft(title, items.Select(ClampSlideText).ToList(), false));
    }

    private static IReadOnlyList<MarkdownBlock> ParseBlocks(string markdown)
    {
        var blocks = new List<MarkdownBlock>();
        foreach (var raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("```") || line.StartsWith("<!--")) continue;
            if (line.StartsWith("![") || line.StartsWith("{{image:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains('|') && line.Trim('|', ' ', '-').Length == 0) continue;

            var heading = Regex.Match(line, "^(#{1,3})\\s+(.+)$");
            if (heading.Success)
            {
                blocks.Add(new MarkdownBlock(CleanInline(heading.Groups[2].Value), heading.Groups[1].Value.Length, false));
                continue;
            }

            var bullet = Regex.Match(line, "^[-*+]\\s+(.+)$");
            if (bullet.Success)
            {
                blocks.Add(new MarkdownBlock(CleanInline(bullet.Groups[1].Value), 0, true));
                continue;
            }

            if (line.Contains('|'))
            {
                var cells = line.Trim('|').Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (cells.Length > 1)
                {
                    blocks.Add(new MarkdownBlock(string.Join(" / ", cells.Select(CleanInline)), 0, true));
                    continue;
                }
            }

            blocks.Add(new MarkdownBlock(CleanInline(line), 0, false));
        }

        return blocks;
    }

    private static string CleanInline(string text)
    {
        var cleaned = Regex.Replace(text, @"[*_`>#\[\]\(\)]", "").Trim();
        return PrefixRegex().Replace(cleaned, "").Trim();
    }

    private static string ClampSlideText(string text) => text.Length > 110 ? text[..110] + "..." : text;

    private static string BuildFileName(string title, string extension)
    {
        var cleaned = Regex.Replace(title, @"[\\/:*?""<>|]+", "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "VeraMedia";
        return $"{cleaned[..Math.Min(cleaned.Length, 32)]}.{extension}";
    }

    [GeneratedRegex(@"^(?:\u6587\u7ae0\u6807\u9898|\u6807\u9898|\u9898\u76ee|\u4e3b\u9898|\u9875\u6807\u9898)\s*[:\uff1a]\s*")]
    private static partial Regex PrefixRegex();

    private sealed record MarkdownBlock(string Text, int Level, bool IsBullet);
    private sealed record SlideDraft(string Title, IReadOnlyList<string> Items, bool IsCover);
}
