using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Xml.Linq;
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
    private const string PptxMainPartContentType = "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml";
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

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

    public async Task<OfficeExportFile> CreatePptxAsync(string title, string markdown)
    {
        var slides = BuildSlides(title, markdown);
        var imageCache = await DownloadSlideImagesAsync(slides);

        using var stream = new MemoryStream();
        using (var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var presentationPart = doc.AddPresentationPart();
            presentationPart.Presentation = new P.Presentation(
                new P.SlideMasterIdList(),
                new P.SlideIdList(),
                new P.SlideSize { Cx = 12192000, Cy = 6858000 },
                new P.NotesSize { Cx = 6858000, Cy = 9144000 });
            var slideLayoutPart = AddPresentationInfrastructure(presentationPart);
            var slideIdList = presentationPart.Presentation.SlideIdList!;

            uint id = 256;
            var rng = new Random(title.GetHashCode() ^ slides.Count);
            for (var index = 0; index < slides.Count; index++)
            {
                var slide = slides[index];
                var slidePart = presentationPart.AddNewPart<SlidePart>();

                string? imageRelId = null;
                if (imageCache.TryGetValue(index, out var imgBytes))
                {
                    var contentType = DetectImageContentType(imgBytes);
                    var imagePart = slidePart.AddImagePart(contentType);
                    using var ms = new MemoryStream(imgBytes);
                    imagePart.FeedData(ms);
                    imageRelId = slidePart.GetIdOfPart(imagePart);
                }

                slidePart.Slide = CreateSlide(slide, index, slides.Count, rng, imageRelId);
                slidePart.AddPart(slideLayoutPart);
                if (!string.IsNullOrWhiteSpace(slide.Notes))
                {
                    AddNotesSlide(slidePart, slide.Notes);
                }
                slidePart.Slide.Save();
                slideIdList.Append(new P.SlideId { Id = id++, RelationshipId = presentationPart.GetIdOfPart(slidePart) });
            }

            presentationPart.Presentation.Save();
        }

        var fileTitle = slides.FirstOrDefault()?.Title ?? title;
        return new OfficeExportFile(BuildFileName(fileTitle, "pptx"), PptxContentType, NormalizePptxPackage(stream.ToArray()));
    }

    private static async Task<Dictionary<int, byte[]>> DownloadSlideImagesAsync(IReadOnlyList<SlideDraft> slides)
    {
        var result = new Dictionary<int, byte[]>();
        var tasks = new List<(int Index, Task<byte[]?> Task)>();

        for (var i = 0; i < slides.Count; i++)
        {
            var url = slides[i].ImageUrl;
            if (string.IsNullOrWhiteSpace(url)) continue;
            tasks.Add((i, DownloadImageBytesAsync(url)));
        }

        foreach (var (idx, task) in tasks)
        {
            var bytes = await task;
            if (bytes is { Length: > 100 })
                result[idx] = bytes;
        }

        return result;
    }

    private static async Task<byte[]?> DownloadImageBytesAsync(string url)
    {
        try
        {
            return await ImageHttp.GetByteArrayAsync(url);
        }
        catch
        {
            return null;
        }
    }

    private static string DetectImageContentType(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 3 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
            return "image/gif";
        return "image/jpeg";
    }

    private static byte[] NormalizePptxPackage(byte[] content)
    {
        using var input = new MemoryStream(content);
        using var source = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        using var output = new MemoryStream();
        using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            XDocument? contentTypes = null;
            foreach (var entry in source.Entries)
            {
                if (string.Equals(entry.FullName, "[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(entry.Open());
                    contentTypes = XDocument.Parse(reader.ReadToEnd());
                    continue;
                }

                var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                copy.LastWriteTime = entry.LastWriteTime;
                using var inputStream = entry.Open();
                using var outputStream = copy.Open();
                inputStream.CopyTo(outputStream);
            }

            contentTypes ??= new XDocument(new XElement(ContentTypesNs + "Types"));
            NormalizeContentTypes(contentTypes);

            var contentTypesEntry = target.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
            using var writer = new StreamWriter(contentTypesEntry.Open(), new UTF8Encoding(false));
            contentTypes.Save(writer, SaveOptions.DisableFormatting);
        }

        return output.ToArray();
    }

    private static readonly XNamespace ContentTypesNs = "http://schemas.openxmlformats.org/package/2006/content-types";

    private static void NormalizeContentTypes(XDocument contentTypes)
    {
        var root = contentTypes.Root ?? throw new InvalidOperationException("Missing PPTX content types root.");

        var xmlDefaults = root.Elements(ContentTypesNs + "Default")
            .Where(x => string.Equals((string?)x.Attribute("Extension"), "xml", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (xmlDefaults.Count == 0)
        {
            root.AddFirst(new XElement(ContentTypesNs + "Default",
                new XAttribute("Extension", "xml"),
                new XAttribute("ContentType", "application/xml")));
        }
        else
        {
            xmlDefaults[0].SetAttributeValue("ContentType", "application/xml");
            foreach (var duplicate in xmlDefaults.Skip(1))
                duplicate.Remove();
        }

        EnsureDefaultContentType(root, "rels", "application/vnd.openxmlformats-package.relationships+xml");
        EnsureOverrideContentType(root, "/ppt/presentation.xml", PptxMainPartContentType);
    }

    private static void EnsureDefaultContentType(XElement root, string extension, string contentType)
    {
        var existing = root.Elements(ContentTypesNs + "Default")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            root.AddFirst(new XElement(ContentTypesNs + "Default",
                new XAttribute("Extension", extension),
                new XAttribute("ContentType", contentType)));
        }
        else
        {
            existing.SetAttributeValue("ContentType", contentType);
        }
    }

    private static void EnsureOverrideContentType(XElement root, string partName, string contentType)
    {
        var existing = root.Elements(ContentTypesNs + "Override")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("PartName"), partName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            root.Add(new XElement(ContentTypesNs + "Override",
                new XAttribute("PartName", partName),
                new XAttribute("ContentType", contentType)));
        }
        else
        {
            existing.SetAttributeValue("ContentType", contentType);
        }
    }

    private static SlideLayoutPart AddPresentationInfrastructure(PresentationPart presentationPart)
    {
        var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
        var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();
        var themePart = slideMasterPart.AddNewPart<ThemePart>();

        themePart.Theme = new A.Theme(
            new A.ThemeElements(
                new A.ColorScheme(
                    new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText, LastColor = "000000" }),
                    new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window, LastColor = "FFFFFF" }),
                    new A.Dark2Color(new A.RgbColorModelHex { Val = "1F2937" }),
                    new A.Light2Color(new A.RgbColorModelHex { Val = "F8FAFC" }),
                    new A.Accent1Color(new A.RgbColorModelHex { Val = "2F7CFF" }),
                    new A.Accent2Color(new A.RgbColorModelHex { Val = "36D6C4" }),
                    new A.Accent3Color(new A.RgbColorModelHex { Val = "F79009" }),
                    new A.Accent4Color(new A.RgbColorModelHex { Val = "12B76A" }),
                    new A.Accent5Color(new A.RgbColorModelHex { Val = "7C3AED" }),
                    new A.Accent6Color(new A.RgbColorModelHex { Val = "EF4444" }),
                    new A.Hyperlink(new A.RgbColorModelHex { Val = "155EEF" }),
                    new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "7C3AED" }))
                { Name = "VeraMedia" },
                new A.FontScheme(
                    new A.MajorFont(new A.LatinFont { Typeface = "Aptos Display" }, new A.EastAsianFont { Typeface = "Microsoft YaHei" }, new A.ComplexScriptFont { Typeface = "Arial" }),
                    new A.MinorFont(new A.LatinFont { Typeface = "Aptos" }, new A.EastAsianFont { Typeface = "Microsoft YaHei" }, new A.ComplexScriptFont { Typeface = "Arial" }))
                { Name = "VeraMedia" },
                new A.FormatScheme(
                    new A.FillStyleList(
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        CreateThemeGradientFill(5400000),
                        CreateThemeGradientFill(16200000)),
                    new A.LineStyleList(
                        new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 9525 },
                        new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 25400 },
                        new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 38100 }),
                    new A.EffectStyleList(new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList()), new A.EffectStyle(new A.EffectList())),
                    new A.BackgroundFillStyleList(
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                        new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })))
                { Name = "VeraMedia" }))
        { Name = "VeraMedia Theme" };
        themePart.Theme.Save();

        slideLayoutPart.SlideLayout = new P.SlideLayout(
            new P.CommonSlideData(CreateEmptyShapeTree()),
            new P.ColorMapOverride(new A.MasterColorMapping()))
        { Type = P.SlideLayoutValues.Blank, Preserve = true };
        slideLayoutPart.SlideLayout.Save();

        slideMasterPart.SlideMaster = new P.SlideMaster(
            new P.CommonSlideData(CreateEmptyShapeTree()),
            new P.ColorMap
            {
                Background1 = A.ColorSchemeIndexValues.Light1,
                Text1 = A.ColorSchemeIndexValues.Dark1,
                Background2 = A.ColorSchemeIndexValues.Light2,
                Text2 = A.ColorSchemeIndexValues.Dark2,
                Accent1 = A.ColorSchemeIndexValues.Accent1,
                Accent2 = A.ColorSchemeIndexValues.Accent2,
                Accent3 = A.ColorSchemeIndexValues.Accent3,
                Accent4 = A.ColorSchemeIndexValues.Accent4,
                Accent5 = A.ColorSchemeIndexValues.Accent5,
                Accent6 = A.ColorSchemeIndexValues.Accent6,
                Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
            },
            new P.SlideLayoutIdList(new P.SlideLayoutId { Id = 2147483649U, RelationshipId = slideMasterPart.GetIdOfPart(slideLayoutPart) }),
            new P.TextStyles(new P.TitleStyle(), new P.BodyStyle(), new P.OtherStyle()));
        slideMasterPart.AddPart(themePart);
        slideMasterPart.SlideMaster.Save();
        // PowerPoint expects slide layouts to keep a reverse relationship to their master.
        slideLayoutPart.AddPart(slideMasterPart);

        var presentation = presentationPart.Presentation ?? throw new InvalidOperationException("Presentation part is not initialized.");
        var masterIds = presentation.SlideMasterIdList ??= new P.SlideMasterIdList();
        masterIds.Append(new P.SlideMasterId
        {
            Id = 2147483648U,
            RelationshipId = presentationPart.GetIdOfPart(slideMasterPart)
        });

        return slideLayoutPart;
    }

    private static A.GradientFill CreateThemeGradientFill(int angle) =>
        new(
            new A.GradientStopList(
                new A.GradientStop(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }) { Position = 0 },
                new A.GradientStop(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }) { Position = 100000 }),
            new A.LinearGradientFill { Angle = angle, Scaled = true });

    private static P.ShapeTree CreateEmptyShapeTree()
    {
        return new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));
    }

    private static W.Paragraph CreateWordParagraph(string text, int size, bool bold)
    {
        var props = new W.RunProperties(new W.FontSize { Val = size.ToString() });
        if (bold) props.Append(new W.Bold());
        return new W.Paragraph(
            new W.ParagraphProperties(new W.SpacingBetweenLines { After = "160", Line = "360", LineRule = W.LineSpacingRuleValues.Auto }),
            new W.Run(props, new W.Text(CleanInline(text)) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static P.Slide CreateSlide(SlideDraft slide, int index, int total, Random rng, string? imageRelId)
    {
        var palette = ThemePalette.From(slide.Theme);
        var shapeTree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));

        var layout = (slide.Layout ?? "").Trim().ToLowerInvariant();
        var gradAngle = 5400000 + rng.Next(-2700000, 2700000);

        if (slide.IsCover || layout.Contains("cover"))
        {
            shapeTree.Append(CreateGradientRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.DarkBackground, palette.DarkPanel, gradAngle));
            var decorVariant = index % 4;
            switch (decorVariant)
            {
                case 0:
                    shapeTree.Append(CreateEllipseShape(3, "Decor", 8500000, -800000, 4500000, 4500000, palette.Accent, 12));
                    shapeTree.Append(CreateEllipseShape(4, "Decor", -1200000, 4500000, 3500000, 3500000, palette.Secondary, 10));
                    break;
                case 1:
                    shapeTree.Append(CreateRoundedRectShape(3, "Decor", 8000000, -500000, 5000000, 3500000, palette.Accent, "", 30000));
                    shapeTree.Append(CreateEllipseShape(4, "Decor", -800000, 5200000, 2800000, 2800000, palette.Warm, 8));
                    break;
                case 2:
                    shapeTree.Append(CreateGradientRectShape(3, "Decor", 9000000, 0, 3192000, 6858000, palette.DarkPanel, palette.Accent, 5400000));
                    shapeTree.Append(CreateEllipseShape(4, "Decor", 9800000, 4000000, 3000000, 3000000, palette.Secondary, 10));
                    break;
                default:
                    shapeTree.Append(CreateEllipseShape(3, "Decor", 7500000, 1000000, 6000000, 6000000, palette.Secondary, 6));
                    shapeTree.Append(CreateRectShape(4, "Decor", 11000000, 0, 400000, 6858000, palette.Accent));
                    break;
            }

            if (imageRelId != null)
            {
                shapeTree.Append(CreatePictureShape(15, "Cover Image", 7200000, 1200000, 4400000, 4200000, imageRelId, 15000));
                shapeTree.Append(CreateRectShape(5, "Accent Bar", 850000, 1500000, 700000, 80000, palette.Accent));
                shapeTree.Append(CreateTextShape(6, "Title", slide.Title, 850000, 1700000, 5800000, 1800000, 4000, true, false, fontColor: "FFFFFF"));
                var coverSub = string.IsNullOrWhiteSpace(slide.Subtitle) ? string.Join(" · ", slide.Items.Take(2)) : slide.Subtitle;
                shapeTree.Append(CreateTextShape(7, "Subtitle", coverSub, 870000, 3600000, 5800000, 930000, 1600, false, false, fontColor: "CBD5E1"));
            }
            else
            {
                shapeTree.Append(CreateRectShape(5, "Accent Bar", 850000, 1500000, 700000, 80000, palette.Accent));
                shapeTree.Append(CreateTextShape(6, "Title", slide.Title, 850000, 1700000, 9800000, 1800000, 4000, true, false, fontColor: "FFFFFF"));
                var coverSub = string.IsNullOrWhiteSpace(slide.Subtitle) ? string.Join(" · ", slide.Items.Take(2)) : slide.Subtitle;
                shapeTree.Append(CreateTextShape(7, "Subtitle", coverSub, 870000, 3600000, 8400000, 930000, 1600, false, false, fontColor: "CBD5E1"));
            }
            shapeTree.Append(CreateRectShape(8, "Footer Line", 850000, 5500000, 2800000, 40000, palette.Accent));
            shapeTree.Append(CreateTextShape(9, "Footer", DateTime.Now.ToString("yyyy.MM.dd"), 850000, 5620000, 2700000, 360000, 1100, false, false, fontColor: "94A3B8"));
        }
        else if (layout.Contains("section"))
        {
            shapeTree.Append(CreateGradientRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.DarkBackground, palette.DarkPanel, gradAngle));
            AddSlideDecorations(shapeTree, palette, index, 95, true);
            shapeTree.Append(CreateEllipseShape(4, "Number Badge", 850000, 1300000, 700000, 700000, palette.Accent));
            shapeTree.Append(CreateTextShape(5, "Section Number", $"{Math.Max(1, index):00}", 950000, 1420000, 500000, 420000, 1800, true, false, fontColor: "FFFFFF"));
            shapeTree.Append(CreateTextShape(6, "Title", slide.Title, 850000, 2250000, 9400000, 1300000, 3600, true, false, fontColor: "FFFFFF"));
            var sectionCopy = string.IsNullOrWhiteSpace(slide.Subtitle) ? string.Join("\n", slide.Items.Take(3)) : slide.Subtitle;
            shapeTree.Append(CreateTextShape(7, "Section Copy", sectionCopy, 900000, 3750000, 8500000, 900000, 1450, false, false, fontColor: "CBD5E1"));
            shapeTree.Append(CreateRectShape(8, "Bottom Accent", 850000, 5300000, 2200000, 80000, palette.Accent));
        }
        else if (layout.Contains("agenda") || layout.Contains("目录") || layout.Contains("contents"))
        {
            shapeTree.Append(CreateRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.LightBackground));
            shapeTree.Append(CreateGradientRectShape(3, "Top Bar", 0, 0, 12192000, 220000, palette.Accent, palette.Secondary, 0));
            shapeTree.Append(CreateTextShape(5, "Title", slide.Title, 760000, 510000, 10300000, 760000, 2520, true, false, fontColor: palette.Title));
            var items = slide.Items.Take(6).ToList();
            for (var i = 0; i < items.Count; i++)
            {
                var y = 1580000L + i * 690000L;
                var cardColor = i % 2 == 0 ? "FFFFFF" : palette.Card;
                var accentColor = i % 3 == 0 ? palette.Accent : i % 3 == 1 ? palette.Secondary : palette.Warm;
                shapeTree.Append(CreateRoundedRectShape((uint)(20 + i * 4), $"Agenda Row {i}", 980000, y, 9700000, 500000, cardColor, palette.Line));
                shapeTree.Append(CreateEllipseShape((uint)(21 + i * 4), $"Agenda No {i}", 1120000, y + 85000, 330000, 330000, accentColor));
                shapeTree.Append(CreateTextShape((uint)(22 + i * 4), $"Agenda No Text {i}", $"{i + 1}", 1195000, y + 130000, 180000, 240000, 1100, true, false, fontColor: "FFFFFF"));
                shapeTree.Append(CreateTextShape((uint)(23 + i * 4), $"Agenda Text {i}", items[i], 1700000, y + 95000, 8600000, 310000, 1260, true, false, fontColor: palette.Body));
            }
            shapeTree.Append(CreateTextShape(90, "Footer", "VeraMedia AI Workspace", 760000, 6260000, 3300000, 260000, 900, false, false, fontColor: palette.Muted));
            shapeTree.Append(CreateTextShape(91, "Page Number", $"{Math.Min(index + 1, total)}/{total}", 10800000, 6260000, 700000, 260000, 900, false, false, fontColor: palette.Muted));
        }
        else if (layout.Contains("quote"))
        {
            shapeTree.Append(CreateGradientRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.DarkBackground, palette.DarkPanel, gradAngle));
            AddSlideDecorations(shapeTree, palette, index, 95, true);
            shapeTree.Append(CreateTextShape(4, "Quote Mark", "“", 800000, 800000, 1200000, 1500000, 9600, true, false, fontColor: palette.Accent));
            var quoteText = slide.Items.Count > 0 ? slide.Items[0] : slide.Title;
            shapeTree.Append(CreateTextShape(5, "Quote Body", quoteText, 1100000, 2000000, 9500000, 2400000, 2800, false, false, fontColor: "FFFFFF"));
            var attribution = string.IsNullOrWhiteSpace(slide.Subtitle) ? slide.Title : slide.Subtitle;
            shapeTree.Append(CreateRectShape(6, "Quote Line", 1100000, 4700000, 1200000, 60000, palette.Accent));
            shapeTree.Append(CreateTextShape(7, "Attribution", $"— {attribution}", 1100000, 4900000, 8000000, 500000, 1400, false, false, fontColor: "CBD5E1"));
            if (imageRelId != null)
            {
                shapeTree.Append(CreatePictureShape(16, "Quote Image", 8800000, 1500000, 2800000, 2800000, imageRelId, 1400000));
            }
            shapeTree.Append(CreateTextShape(90, "Footer", "VeraMedia AI Workspace", 760000, 6260000, 3300000, 260000, 900, false, false, fontColor: "94A3B8"));
            shapeTree.Append(CreateTextShape(91, "Page Number", $"{Math.Min(index + 1, total)}/{total}", 10800000, 6260000, 700000, 260000, 900, false, false, fontColor: "94A3B8"));
        }
        else if (layout.Contains("stats") || layout.Contains("metrics") || layout.Contains("kpi"))
        {
            shapeTree.Append(CreateRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.LightBackground));
            shapeTree.Append(CreateGradientRectShape(3, "Top Bar", 0, 0, 12192000, 220000, palette.Accent, palette.Secondary, 0));
            AddSlideDecorations(shapeTree, palette, index, 95, false);
            shapeTree.Append(CreateTextShape(5, "Title", slide.Title, 760000, 510000, 10300000, 760000, 2520, true, false, fontColor: palette.Title));
            var metrics = slide.Items.Take(4).ToList();
            var colCount = Math.Min(metrics.Count, 4);
            var cardW = colCount <= 2 ? 4800000L : 2500000L;
            var gap = colCount <= 2 ? 400000L : 200000L;
            var totalW = colCount * cardW + (colCount - 1) * gap;
            var startX = (12192000L - totalW) / 2;
            for (var i = 0; i < metrics.Count; i++)
            {
                var x = startX + i * (cardW + gap);
                var accent = i % 3 == 0 ? palette.Accent : i % 3 == 1 ? palette.Secondary : palette.Warm;
                var parts = metrics[i].Split(new[] { '：', ':' }, 2);
                var label = parts.Length > 1 ? parts[0].Trim() : $"指标 {i + 1}";
                var value = parts.Length > 1 ? parts[1].Trim() : metrics[i];
                shapeTree.Append(CreateRoundedRectShape((uint)(20 + i * 5), $"Stat Card {i}", x, 1700000, cardW, 3800000, "FFFFFF", palette.Line, 12000));
                shapeTree.Append(CreateRectShape((uint)(21 + i * 5), $"Stat Accent {i}", x + 100000, 1700000, cardW - 200000, 80000, accent));
                shapeTree.Append(CreateTextShape((uint)(22 + i * 5), $"Stat Label {i}", label, x + 200000, 2100000, cardW - 400000, 500000, 1300, true, false, fontColor: palette.Muted));
                shapeTree.Append(CreateTextShape((uint)(23 + i * 5), $"Stat Value {i}", value, x + 200000, 2700000, cardW - 400000, 1200000, 3600, true, false, fontColor: accent));
                if (parts.Length <= 1 && slide.Items.Count > metrics.Count + i)
                {
                    shapeTree.Append(CreateTextShape((uint)(24 + i * 5), $"Stat Desc {i}", slide.Items[metrics.Count + i], x + 200000, 4100000, cardW - 400000, 1000000, 1100, false, false, fontColor: palette.Body));
                }
            }
            shapeTree.Append(CreateTextShape(90, "Footer", "VeraMedia AI Workspace", 760000, 6260000, 3300000, 260000, 900, false, false, fontColor: palette.Muted));
            shapeTree.Append(CreateTextShape(91, "Page Number", $"{Math.Min(index + 1, total)}/{total}", 10800000, 6260000, 700000, 260000, 900, false, false, fontColor: palette.Muted));
        }
        else if (layout.Contains("summary"))
        {
            shapeTree.Append(CreateRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.LightBackground));
            shapeTree.Append(CreateGradientRectShape(3, "Top Bar", 0, 0, 12192000, 220000, palette.Accent, palette.Secondary, 0));
            AddSlideDecorations(shapeTree, palette, index, 95, false);
            shapeTree.Append(CreateTextShape(5, "Title", slide.Title, 760000, 510000, 10300000, 760000, 2520, true, false, fontColor: palette.Title));
            var summaryItems = slide.Items.Take(6).ToList();
            for (var i = 0; i < summaryItems.Count; i++)
            {
                var col = i % 3;
                var row = i / 3;
                var x = 780000L + col * 3650000L;
                var y = 1780000L + row * 1550000L;
                var color = i % 3 == 0 ? palette.Card : i % 3 == 1 ? palette.SoftSecondary : palette.SoftWarm;
                var accent = i % 3 == 0 ? palette.Accent : i % 3 == 1 ? palette.Secondary : palette.Warm;
                shapeTree.Append(CreateRoundedRectShape((uint)(20 + i * 3), $"Summary Card {i}", x, y, 3300000, 1180000, color, palette.Line));
                shapeTree.Append(CreateRectShape((uint)(21 + i * 3), $"Summary Accent {i}", x + 150000, y, 3000000, 80000, accent));
                shapeTree.Append(CreateTextShape((uint)(22 + i * 3), $"Summary Text {i}", summaryItems[i], x + 240000, y + 260000, 2860000, 760000, 1280, true, false, fontColor: palette.Body));
            }
            shapeTree.Append(CreateTextShape(90, "Footer", "VeraMedia AI Workspace", 760000, 6260000, 3300000, 260000, 900, false, false, fontColor: palette.Muted));
            shapeTree.Append(CreateTextShape(91, "Page Number", $"{Math.Min(index + 1, total)}/{total}", 10800000, 6260000, 700000, 260000, 900, false, false, fontColor: palette.Muted));
        }
        else if (layout.Contains("two-column") || layout.Contains("compare") || layout.Contains("comparison"))
        {
            shapeTree.Append(CreateRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.LightBackground));
            shapeTree.Append(CreateGradientRectShape(3, "Top Bar", 0, 0, 12192000, 220000, palette.Accent, palette.Secondary, 0));
            shapeTree.Append(CreateRectShape(4, "Title Rail", 560000, 650000, 80000, 680000, palette.Secondary));
            shapeTree.Append(CreateTextShape(5, "Title", slide.Title, 760000, 510000, 10300000, 760000, 2520, true, false, fontColor: palette.Title));
            if (!string.IsNullOrWhiteSpace(slide.Subtitle))
            {
                shapeTree.Append(CreateTextShape(6, "Subtitle", slide.Subtitle, 780000, 1240000, 10200000, 420000, 1220, false, false, fontColor: palette.Muted));
            }

            var items = slide.Items.Take(6).ToList();
            var left = string.Join("\n", items.Take((items.Count + 1) / 2));
            var right = string.Join("\n", items.Skip((items.Count + 1) / 2));
            var y = string.IsNullOrWhiteSpace(slide.Subtitle) ? 1700000 : 1900000;

            if (imageRelId != null)
            {
                shapeTree.Append(CreateRoundedRectShape(7, "Left Column", 780000, y, 5150000, 3700000, "FFFFFF", palette.Line));
                shapeTree.Append(CreateRectShape(9, "Left Accent", 930000, y + 60000, 4850000, 80000, palette.Accent));
                shapeTree.Append(CreateTextShape(11, "Left Body", string.Join("\n", items), 1120000, y + 420000, 4480000, 2840000, 1360, false, true, fontColor: palette.Body));
                shapeTree.Append(CreatePictureShape(16, "Column Image", 6300000, y, 5150000, 3700000, imageRelId, 10000));
            }
            else
            {
                shapeTree.Append(CreateRoundedRectShape(7, "Left Column", 780000, y, 5150000, 3700000, "FFFFFF", palette.Line));
                shapeTree.Append(CreateRoundedRectShape(8, "Right Column", 6300000, y, 5150000, 3700000, palette.Card, palette.Line));
                shapeTree.Append(CreateRectShape(9, "Left Accent", 930000, y + 60000, 4850000, 80000, palette.Accent));
                shapeTree.Append(CreateRectShape(10, "Right Accent", 6450000, y + 60000, 4850000, 80000, palette.Secondary));
                shapeTree.Append(CreateTextShape(11, "Left Body", left, 1120000, y + 420000, 4480000, 2840000, 1360, false, true, fontColor: palette.Body));
                shapeTree.Append(CreateTextShape(12, "Right Body", right, 6640000, y + 420000, 4480000, 2840000, 1360, false, true, fontColor: palette.Body));
            }
            shapeTree.Append(CreateTextShape(90, "Footer", "VeraMedia AI Workspace", 760000, 6260000, 3300000, 260000, 900, false, false, fontColor: palette.Muted));
            shapeTree.Append(CreateTextShape(91, "Page Number", $"{Math.Min(index + 1, total)}/{total}", 10800000, 6260000, 700000, 260000, 900, false, false, fontColor: palette.Muted));
        }
        else if (layout.Contains("process") || layout.Contains("timeline"))
        {
            shapeTree.Append(CreateRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.LightBackground));
            shapeTree.Append(CreateGradientRectShape(3, "Top Bar", 0, 0, 12192000, 220000, palette.Accent, palette.Secondary, 0));
            shapeTree.Append(CreateTextShape(5, "Title", slide.Title, 760000, 510000, 10300000, 760000, 2520, true, false, fontColor: palette.Title));
            if (!string.IsNullOrWhiteSpace(slide.Subtitle))
            {
                shapeTree.Append(CreateTextShape(6, "Subtitle", slide.Subtitle, 780000, 1240000, 10200000, 420000, 1220, false, false, fontColor: palette.Muted));
            }

            var steps = slide.Items.Take(5).ToList();
            var startX = 850000L;
            var stepY = 2840000L;
            var stepGap = 2100000L;
            var useRoundBadge = index % 2 == 0;
            if (steps.Count > 1)
            {
                shapeTree.Append(CreateRectShape(30, "Timeline Line", startX + 340000, stepY + 290000, (steps.Count - 1) * stepGap - 20000, 50000, palette.SoftAccent));
            }
            for (var i = 0; i < steps.Count; i++)
            {
                var x = startX + i * stepGap;
                var idBase = 40 + i * 4;
                var badgeColor = i % 2 == 0 ? palette.Accent : palette.Secondary;
                if (useRoundBadge)
                {
                    shapeTree.Append(CreateEllipseShape((uint)idBase, $"Step Dot {i}", x + 20000, stepY, 640000, 640000, badgeColor));
                }
                else
                {
                    shapeTree.Append(CreateRoundedRectShape((uint)idBase, $"Step Badge {i}", x + 20000, stepY, 640000, 640000, badgeColor, "", 10000));
                }
                shapeTree.Append(CreateTextShape((uint)(idBase + 1), $"Step No {i}", (i + 1).ToString(), x + 195000, stepY + 115000, 300000, 350000, 1600, true, false, fontColor: "FFFFFF"));
                shapeTree.Append(CreateRoundedRectShape((uint)(idBase + 2), $"Step Card {i}", x - 340000, stepY + 900000, 1360000, 1550000, "FFFFFF", palette.Line));
                shapeTree.Append(CreateTextShape((uint)(idBase + 3), $"Step Text {i}", steps[i], x - 180000, stepY + 1120000, 1040000, 1050000, 1050, true, false, fontColor: palette.Body));
            }
            shapeTree.Append(CreateTextShape(90, "Footer", "VeraMedia AI Workspace", 760000, 6260000, 3300000, 260000, 900, false, false, fontColor: palette.Muted));
            shapeTree.Append(CreateTextShape(91, "Page Number", $"{Math.Min(index + 1, total)}/{total}", 10800000, 6260000, 700000, 260000, 900, false, false, fontColor: palette.Muted));
        }
        else if (layout.Contains("data-card"))
        {
            shapeTree.Append(CreateGradientRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.DarkBackground, palette.DarkPanel, gradAngle));
            shapeTree.Append(CreateRectShape(3, "Top Accent", 0, 0, 12192000, 220000, palette.Secondary));
            AddSlideDecorations(shapeTree, palette, index, 95, true);
            shapeTree.Append(CreateTextShape(5, "Title", slide.Title, 760000, 510000, 10300000, 760000, 2520, true, false, fontColor: "FFFFFF"));
            if (!string.IsNullOrWhiteSpace(slide.Subtitle))
            {
                shapeTree.Append(CreateTextShape(6, "Subtitle", slide.Subtitle, 780000, 1240000, 10200000, 420000, 1220, false, false, fontColor: "CBD5E1"));
            }

            var cards = slide.Items.Take(4).ToList();
            for (var i = 0; i < cards.Count; i++)
            {
                var x = 800000L + (i % 2) * 5400000L;
                var cardY = 1900000L + (i / 2) * 1900000L;
                var accent = i % 2 == 0 ? palette.Secondary : palette.Accent;
                shapeTree.Append(CreateRoundedRectShape((uint)(30 + i * 4), $"Data Card {i}", x, cardY, 4850000, 1500000, palette.DarkPanel, palette.DarkLine));
                shapeTree.Append(CreateRectShape((uint)(31 + i * 4), $"Data Accent {i}", x + 150000, cardY, 4550000, 70000, accent));
                shapeTree.Append(CreateTextShape((uint)(32 + i * 4), $"Data Label {i}", $"0{i + 1}", x + 330000, cardY + 270000, 700000, 350000, 1250, true, false, fontColor: accent));
                shapeTree.Append(CreateTextShape((uint)(33 + i * 4), $"Data Text {i}", cards[i], x + 1150000, cardY + 300000, 3300000, 980000, 1280, true, false, fontColor: "F8FAFC"));
            }
            shapeTree.Append(CreateTextShape(90, "Footer", "VeraMedia AI Workspace", 760000, 6260000, 3300000, 260000, 900, false, false, fontColor: "94A3B8"));
            shapeTree.Append(CreateTextShape(91, "Page Number", $"{Math.Min(index + 1, total)}/{total}", 10800000, 6260000, 700000, 260000, 900, false, false, fontColor: "94A3B8"));
        }
        else if (layout.Length == 0 || layout.Contains("title-content"))
        {
            shapeTree.Append(CreateRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.LightBackground));
            shapeTree.Append(CreateGradientRectShape(3, "Top Bar", 0, 0, 12192000, 220000, palette.Accent, palette.Secondary, 0));
            AddSlideDecorations(shapeTree, palette, index, 95, false);
            shapeTree.Append(CreateRectShape(4, "Title Rail", 560000, 650000, 80000, 680000, palette.Secondary));
            shapeTree.Append(CreateTextShape(5, "Title", slide.Title, 760000, 510000, 10300000, 760000, 2520, true, false, fontColor: palette.Title));
            if (!string.IsNullOrWhiteSpace(slide.Subtitle))
            {
                shapeTree.Append(CreateTextShape(6, "Subtitle", slide.Subtitle, 780000, 1240000, 10200000, 420000, 1220, false, false, fontColor: palette.Muted));
            }

            var bodyY = string.IsNullOrWhiteSpace(slide.Subtitle) ? 1580000 : 1760000;

            if (imageRelId != null)
            {
                shapeTree.Append(CreateRoundedRectShape(7, "Content Card", 720000, bodyY - 110000, 5900000, 4350000, "FFFFFF", palette.Line));
                shapeTree.Append(CreateTextShape(8, "Body", string.Join("\n", slide.Items.Take(6)), 1020000, bodyY + 220000, 5300000, 3720000, 1520, false, true, fontColor: palette.Body));
                shapeTree.Append(CreatePictureShape(16, "Content Image", 6900000, bodyY - 110000, 4540000, 4350000, imageRelId, 10000));
            }
            else
            {
                shapeTree.Append(CreateRoundedRectShape(7, "Content Card", 720000, bodyY - 110000, 7750000, 4350000, "FFFFFF", palette.Line));
                shapeTree.Append(CreateTextShape(8, "Body", string.Join("\n", slide.Items.Take(6)), 1020000, bodyY + 220000, 7060000, 3720000, 1520, false, true, fontColor: palette.Body));
                var visualText = string.Join("\n", new[] { slide.Visual }.Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(visualText))
                {
                    shapeTree.Append(CreateRoundedRectShape(9, "Visual Card", 8780000, bodyY - 110000, 2660000, 4350000, palette.SoftSecondary, palette.SecondaryLine));
                    shapeTree.Append(CreateTextShape(10, "Visual Label", "VISUAL", 9070000, bodyY + 170000, 2100000, 310000, 1100, true, false, fontColor: palette.SecondaryText));
                    shapeTree.Append(CreateTextShape(11, "Visual Direction", visualText, 9070000, bodyY + 620000, 2100000, 3150000, 1120, false, false, fontColor: palette.Body));
                }
                else
                {
                    var sideAccent = index % 3 == 0 ? palette.Accent : index % 3 == 1 ? palette.Secondary : palette.Warm;
                    var sideCards = new[]
                    {
                        (palette.Card, palette.SoftAccent, "KEY POINT", sideAccent),
                        (palette.SoftSecondary, palette.SecondaryLine, "INSIGHT", palette.SecondaryText),
                        (palette.SoftWarm, palette.Warm, "HIGHLIGHT", palette.Warm),
                    };
                    var pick = sideCards[index % sideCards.Length];
                    shapeTree.Append(CreateRoundedRectShape(9, "Side Card", 8780000, bodyY - 110000, 2660000, 4350000, pick.Item1, pick.Item2));
                    shapeTree.Append(CreateTextShape(10, "Side Label", pick.Item3, 9070000, bodyY + 170000, 2100000, 310000, 1100, true, false, fontColor: pick.Item4));
                    var sideText = slide.Items.Count > 2 ? slide.Items[0] : "Use concise bullets, charts, flows, or scenario visuals to support this page.";
                    shapeTree.Append(CreateTextShape(11, "Side Copy", sideText, 9070000, bodyY + 620000, 2100000, 2200000, 1120, false, false, fontColor: palette.Body));
                }
            }

            shapeTree.Append(CreateTextShape(12, "Footer", "VeraMedia AI Workspace", 760000, 6260000, 3300000, 260000, 900, false, false, fontColor: palette.Muted));
            shapeTree.Append(CreateTextShape(13, "Page Number", $"{Math.Min(index + 1, total)}/{total}", 10800000, 6260000, 700000, 260000, 900, false, false, fontColor: palette.Muted));
        }
        else
        {
            shapeTree.Append(CreateRectShape(2, "Background", 0, 0, 12192000, 6858000, palette.LightBackground));
            shapeTree.Append(CreateGradientRectShape(3, "Top Bar", 0, 0, 12192000, 220000, palette.Accent, palette.Secondary, 0));
            AddSlideDecorations(shapeTree, palette, index, 95, false);
            shapeTree.Append(CreateRectShape(4, "Title Rail", 560000, 650000, 80000, 680000, palette.Secondary));
            shapeTree.Append(CreateTextShape(5, "Title", slide.Title, 760000, 510000, 10300000, 760000, 2520, true, false, fontColor: palette.Title));
            if (!string.IsNullOrWhiteSpace(slide.Subtitle))
            {
                shapeTree.Append(CreateTextShape(6, "Subtitle", slide.Subtitle, 780000, 1240000, 10200000, 420000, 1220, false, false, fontColor: palette.Muted));
            }

            var bodyY = string.IsNullOrWhiteSpace(slide.Subtitle) ? 1580000 : 1760000;

            if (imageRelId != null)
            {
                shapeTree.Append(CreateRoundedRectShape(7, "Content Card", 720000, bodyY - 110000, 5900000, 4350000, "FFFFFF", palette.Line));
                shapeTree.Append(CreateTextShape(8, "Body", string.Join("\n", slide.Items.Take(6)), 1020000, bodyY + 220000, 5300000, 3720000, 1520, false, true, fontColor: palette.Body));
                shapeTree.Append(CreatePictureShape(16, "Content Image", 6900000, bodyY - 110000, 4540000, 4350000, imageRelId, 10000));
            }
            else
            {
                shapeTree.Append(CreateRoundedRectShape(7, "Content Card", 720000, bodyY - 110000, 10600000, 4350000, "FFFFFF", palette.Line));
                shapeTree.Append(CreateTextShape(8, "Body", string.Join("\n", slide.Items.Take(6)), 1020000, bodyY + 220000, 9900000, 3720000, 1520, false, true, fontColor: palette.Body));
            }

            shapeTree.Append(CreateTextShape(12, "Footer", "VeraMedia AI Workspace", 760000, 6260000, 3300000, 260000, 900, false, false, fontColor: palette.Muted));
            shapeTree.Append(CreateTextShape(13, "Page Number", $"{Math.Min(index + 1, total)}/{total}", 10800000, 6260000, 700000, 260000, 900, false, false, fontColor: palette.Muted));
        }

        return new P.Slide(new P.CommonSlideData(shapeTree), new P.ColorMapOverride(new A.MasterColorMapping()));
    }

    private static void AddNotesSlide(SlidePart slidePart, string notes)
    {
        var notesPart = slidePart.AddNewPart<NotesSlidePart>();
        var shapeTree = new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()));

        shapeTree.Append(CreateTextShape(2, "Notes", notes, 680000, 700000, 11200000, 5200000, 1600, false, false));
        notesPart.NotesSlide = new P.NotesSlide(
            new P.CommonSlideData(shapeTree),
            new P.ColorMapOverride(new A.MasterColorMapping()));
        notesPart.NotesSlide.Save();
    }

    private static P.Shape CreateRectShape(uint id, string name, long x, long y, long cx, long cy, string fillColor, string lineColor = "")
    {
        var shapeProperties = new P.ShapeProperties(
            new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
            new A.SolidFill(new A.RgbColorModelHex { Val = fillColor }));
        shapeProperties.Append(string.IsNullOrWhiteSpace(lineColor)
            ? new A.Outline(new A.NoFill())
            : new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = lineColor })));

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            shapeProperties,
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()));
    }

    private static P.Shape CreateTextShape(uint id, string name, string text, long x, long y, long cx, long cy, int fontSize, bool bold, bool bullet, string fontColor = "101828")
    {
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => CreateDrawingParagraph(line, bullet, fontSize, bold, fontColor))
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
                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                new A.NoFill(),
                new A.Outline(new A.NoFill())),
            textBody);
    }

    private static P.Shape CreateRoundedRectShape(uint id, string name, long x, long y, long cx, long cy, string fillColor, string lineColor = "", int cornerRadius = 10000)
    {
        var geometry = new A.PresetGeometry(new A.AdjustValueList(
            new A.ShapeGuide { Name = "adj", Formula = $"val {cornerRadius}" }
        )) { Preset = A.ShapeTypeValues.RoundRectangle };

        var shapeProperties = new P.ShapeProperties(
            new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
            geometry,
            new A.SolidFill(new A.RgbColorModelHex { Val = fillColor }));
        shapeProperties.Append(string.IsNullOrWhiteSpace(lineColor)
            ? new A.Outline(new A.NoFill())
            : new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = lineColor })));

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            shapeProperties,
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()));
    }

    private static P.Shape CreateEllipseShape(uint id, string name, long x, long y, long cx, long cy, string fillColor, int alphaPercent = 100)
    {
        var fill = new A.SolidFill();
        var color = new A.RgbColorModelHex { Val = fillColor };
        if (alphaPercent < 100)
            color.Append(new A.Alpha { Val = alphaPercent * 1000 });
        fill.Append(color);

        var shapeProperties = new P.ShapeProperties(
            new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Ellipse },
            fill,
            new A.Outline(new A.NoFill()));

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            shapeProperties,
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()));
    }

    private static P.Shape CreateGradientRectShape(uint id, string name, long x, long y, long cx, long cy, string color1, string color2, int angle = 5400000)
    {
        var gradFill = new A.GradientFill(
            new A.GradientStopList(
                new A.GradientStop(new A.RgbColorModelHex { Val = color1 }) { Position = 0 },
                new A.GradientStop(new A.RgbColorModelHex { Val = color2 }) { Position = 100000 }),
            new A.LinearGradientFill { Angle = angle, Scaled = true });

        var shapeProperties = new P.ShapeProperties(
            new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }),
            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
            gradFill,
            new A.Outline(new A.NoFill()));

        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            shapeProperties,
            new P.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph()));
    }

    private static P.Picture CreatePictureShape(uint id, string name, long x, long y, long cx, long cy, string relationshipId, int cornerRadius = 0)
    {
        var shapeProps = new P.ShapeProperties(
            new A.Transform2D(new A.Offset { X = x, Y = y }, new A.Extents { Cx = cx, Cy = cy }));

        if (cornerRadius > 0)
        {
            shapeProps.Append(new A.PresetGeometry(new A.AdjustValueList(
                new A.ShapeGuide { Name = "adj", Formula = $"val {cornerRadius}" }
            )) { Preset = A.ShapeTypeValues.RoundRectangle });
        }
        else
        {
            shapeProps.Append(new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle });
        }

        return new P.Picture(
            new P.NonVisualPictureProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.BlipFill(
                new A.Blip { Embed = relationshipId },
                new A.Stretch(new A.FillRectangle())),
            shapeProps);
    }

    private static void AddSlideDecorations(P.ShapeTree tree, ThemePalette p, int slideIndex, uint baseId, bool darkMode)
    {
        var variant = slideIndex % 8;
        var colors = new[] { p.Accent, p.Secondary, p.Warm };
        var c1 = colors[slideIndex % 3];
        var c2 = colors[(slideIndex + 1) % 3];

        switch (variant)
        {
            case 0:
                tree.Append(CreateEllipseShape(baseId, "Decor", 9500000, -600000, 3500000, 3500000, c1, 8));
                tree.Append(CreateEllipseShape(baseId + 1, "Decor", 10200000, 500000, 1800000, 1800000, c2, 12));
                break;
            case 1:
                tree.Append(CreateEllipseShape(baseId, "Decor", -800000, 4800000, 3000000, 3000000, c2, 10));
                tree.Append(CreateRectShape(baseId + 1, "Decor", 0, darkMode ? 6650000 : 6700000, 12192000, 158000, c1));
                break;
            case 2:
                tree.Append(CreateGradientRectShape(baseId, "Decor", 10500000, 0, 1692000, 6858000, darkMode ? p.DarkPanel : p.SoftAccent, darkMode ? p.DarkBackground : p.LightBackground, 2700000));
                break;
            case 3:
                tree.Append(CreateRoundedRectShape(baseId, "Decor", 10500000, 5000000, 2000000, 2000000, darkMode ? p.DarkLine : p.SoftSecondary, "", 20000));
                tree.Append(CreateEllipseShape(baseId + 1, "Decor", -600000, -600000, 2200000, 2200000, c1, 6));
                break;
            case 4:
                tree.Append(CreateRectShape(baseId, "Decor", 11800000, 1000000, 392000, 1200000, c1));
                tree.Append(CreateRectShape(baseId + 1, "Decor", 11800000, 2400000, 392000, 800000, c2));
                break;
            case 5:
                tree.Append(CreateEllipseShape(baseId, "Decor", 5500000, -2000000, 8000000, 8000000, darkMode ? c1 : p.SoftAccent, 5));
                break;
            case 6:
                tree.Append(CreateRoundedRectShape(baseId, "Decor", -400000, 5500000, 3800000, 1800000, darkMode ? p.DarkLine : p.SoftWarm, "", 15000));
                tree.Append(CreateEllipseShape(baseId + 1, "Decor", 10800000, -500000, 2000000, 2000000, c2, 10));
                break;
            case 7:
                tree.Append(CreateGradientRectShape(baseId, "Decor", 0, 6400000, 12192000, 458000, c1, c2, 0));
                break;
        }
    }

    private static A.Paragraph CreateDrawingParagraph(string text, bool bullet, int fontSize, bool bold, string fontColor)
    {
        var paragraphProperties = new A.ParagraphProperties();
        paragraphProperties.Append(new A.SpaceBefore(new A.SpacingPoints { Val = 600 }));
        if (bullet)
        {
            paragraphProperties.Append(new A.BulletSizePercentage { Val = 70000 });
            paragraphProperties.Append(new A.BulletFont { Typeface = "Arial" });
            paragraphProperties.Append(new A.CharacterBullet { Char = "\u2022" });
        }

        return new A.Paragraph(
            paragraphProperties,
            new A.Run(
                new A.RunProperties(new A.SolidFill(new A.RgbColorModelHex { Val = fontColor })) { FontSize = fontSize, Bold = bold },
                new A.Text(CleanInline(text))),
            new A.EndParagraphRunProperties { FontSize = fontSize });
    }

    private static IReadOnlyList<SlideDraft> BuildSlides(string title, string markdown)
    {
        if (TryBuildSlidesFromSpec(title, markdown, out var specSlides))
        {
            return specSlides;
        }

        var blocks = ParseBlocks(markdown).Where(x => !string.IsNullOrWhiteSpace(x.Text)).ToList();
        var slides = new List<SlideDraft> { new(title, "Generated from VeraMedia", ["Generated from VeraMedia"], "", "", "", "商务精美", true) };
        var currentTitle = title;
        var currentItems = new List<string>();
        var currentNotes = new List<string>();

        foreach (var block in blocks)
        {
            if (block.Level is > 0)
            {
                AddSlide(slides, currentTitle, currentItems, currentNotes);
                currentTitle = block.Text;
                currentItems = [];
                currentNotes = [];
                continue;
            }

            if (IsNotesLine(block.Text, out var notes))
            {
                currentNotes.Add(notes);
                continue;
            }

            currentItems.Add(block.Text);
            if (currentItems.Count >= 5)
            {
                AddSlide(slides, currentTitle, currentItems, currentNotes);
                currentTitle = "Continued: " + currentTitle;
                currentItems = [];
                currentNotes = [];
            }
        }

        AddSlide(slides, currentTitle, currentItems, currentNotes);
        return slides.Take(36).ToList();
    }

    private static void AddSlide(List<SlideDraft> slides, string title, List<string> items, List<string> notes)
    {
        if (items.Count == 0) return;
        var bullets = items.Select(ClampSlideText).ToList();
        var speakerNotes = string.Join("\n", notes.Select(CleanInline));
        if (string.IsNullOrWhiteSpace(speakerNotes))
        {
            speakerNotes = BuildDefaultNotes(title, bullets);
        }

        slides.Add(new SlideDraft(title, "", bullets, "title-content", "", speakerNotes, "商务精美", false));
    }

    private static bool TryBuildSlidesFromSpec(string fallbackTitle, string content, out IReadOnlyList<SlideDraft> slides)
    {
        slides = [];
        var json = ExtractPptSpecJson(content);
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var title = ReadString(root, "title") ?? fallbackTitle;
            var theme = ReadString(root, "theme") ?? "商务精美";
            if (root.TryGetProperty("design", out var design) && design.ValueKind == JsonValueKind.Object)
            {
                theme = ReadString(design, "theme") ?? theme;
            }
            if (!root.TryGetProperty("slides", out var slideArray) || slideArray.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var result = new List<SlideDraft>();
            var coverSubtitle = ReadString(root, "subtitle") ?? ReadString(root, "audience") ?? "Generated from VeraMedia";
            result.Add(new SlideDraft(title, coverSubtitle, [coverSubtitle], "cover", "", BuildDefaultNotes(title, [coverSubtitle]), theme, true));

            foreach (var slide in slideArray.EnumerateArray().Take(36))
            {
                if (slide.ValueKind != JsonValueKind.Object) continue;
                var slideTitle = ReadString(slide, "title") ?? "未命名页面";
                var subtitle = ReadString(slide, "subtitle") ?? "";
                var layout = ReadString(slide, "layout") ?? "";
                var visual = string.Join("\n", new[]
                {
                    ReadString(slide, "visual"),
                    ReadString(slide, "visualSuggestion"),
                    ReadString(slide, "diagram"),
                    ReadString(slide, "chart"),
                    ReadString(slide, "imagePrompt"),
                    ReadString(slide, "design")
                }.Where(x => !string.IsNullOrWhiteSpace(x)));
                var notes = ReadString(slide, "notes") ?? ReadString(slide, "narration") ?? "";
                var bullets = ReadStringArray(slide, "bullets")
                    .Concat(ReadStringArray(slide, "points"))
                    .Concat(ReadStringArray(slide, "keyPoints"))
                    .Concat(ReadStringArray(slide, "metrics"))
                    .Concat(ReadStringArray(slide, "kpis"))
                    .Select(ClampSlideText)
                    .Take(6)
                    .ToList();
                if (bullets.Count == 0)
                {
                    var body = ReadString(slide, "body");
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        bullets.AddRange(body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5).Select(ClampSlideText));
                    }
                }

                if (bullets.Count == 0) bullets.Add(" ");
                if (string.IsNullOrWhiteSpace(notes))
                {
                    notes = BuildDefaultNotes(slideTitle, bullets);
                }

                var imageUrl = ReadString(slide, "imageUrl");
                if (result.Count == 1 && layout.Contains("cover", StringComparison.OrdinalIgnoreCase))
                {
                    result[0] = new SlideDraft(slideTitle, string.IsNullOrWhiteSpace(subtitle) ? coverSubtitle : subtitle, bullets, "cover", visual, notes, theme, true, imageUrl);
                    continue;
                }
                result.Add(new SlideDraft(slideTitle, subtitle, bullets, NormalizeLayout(layout), visual, notes, theme, false, imageUrl));
            }

            slides = result.Count > 1 ? result : [];
            return slides.Count > 0;
        }
        catch
        {
            slides = [];
            return false;
        }
    }

    private static string? ExtractPptSpecJson(string content)
    {
        var fenced = Regex.Match(content, @"```(?:json|ppt-spec|veramedia-ppt)?[^\n`]*\n?([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (fenced.Success && fenced.Groups[1].Value.Contains("\"slides\"", StringComparison.OrdinalIgnoreCase))
        {
            return fenced.Groups[1].Value.Trim();
        }

        var marker = Regex.Match(content, @"PPT_SPEC\s*[:：]\s*(\{[\s\S]*\})", RegexOptions.IgnoreCase);
        if (marker.Success)
        {
            return marker.Groups[1].Value.Trim();
        }

        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            var candidate = content[start..(end + 1)];
            if (candidate.Contains("\"slides\"", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? CleanInline(value.GetString() ?? "")
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value.EnumerateArray()
            .Select(x => x.ValueKind == JsonValueKind.String ? CleanInline(x.GetString() ?? "") : "")
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static bool IsNotesLine(string text, out string notes)
    {
        var match = Regex.Match(text, @"^(?:备注|旁白|演讲稿|配音稿|Speaker Notes?|Narration)\s*[:：]\s*(.+)$", RegexOptions.IgnoreCase);
        notes = match.Success ? match.Groups[1].Value.Trim() : "";
        return match.Success;
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

    private static string BuildDefaultNotes(string title, IReadOnlyList<string> bullets)
    {
        var points = bullets
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Take(3)
            .ToList();
        if (points.Count == 0)
        {
            return CleanInline(title);
        }

        return $"{CleanInline(title)}。{string.Join("；", points.Select(CleanInline))}。";
    }

    private static string NormalizeLayout(string layout)
    {
        var value = (layout ?? "").Trim().ToLowerInvariant();
        if (value.Contains("cover")) return "cover";
        if (value.Contains("section")) return "section";
        if (value.Contains("agenda") || value.Contains("contents") || value.Contains("目录")) return "agenda";
        if (value.Contains("summary")) return "summary";
        if (value.Contains("quote")) return "quote";
        if (value.Contains("stats") || value.Contains("metrics") || value.Contains("kpi")) return "stats";
        if (value.Contains("data")) return "data-card";
        if (value.Contains("process")) return "process";
        if (value.Contains("timeline")) return "timeline";
        if (value.Contains("two-column") || value.Contains("compare")) return "two-column";
        return "title-content";
    }

    private static string BuildFileName(string title, string extension)
    {
        var cleaned = SanitizeFileNameStem(title);
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "VeraMedia";
        return $"{cleaned[..Math.Min(cleaned.Length, 48)].Trim()}.{extension.TrimStart('.')}";
    }

    private static string SanitizeFileNameStem(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var normalized = (title ?? "").Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);

        foreach (var ch in normalized)
        {
            if (invalid.Contains(ch) || char.IsControl(ch))
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' ? ch : ' ');
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim(' ', '-', '_', '.');
    }

    [GeneratedRegex(@"^(?:\u6587\u7ae0\u6807\u9898|\u6807\u9898|\u9898\u76ee|\u4e3b\u9898|\u9875\u6807\u9898)\s*[:\uff1a]\s*")]
    private static partial Regex PrefixRegex();

    private sealed record ThemePalette(
        string Accent,
        string Secondary,
        string Warm,
        string Title,
        string Body,
        string Muted,
        string LightBackground,
        string Card,
        string Line,
        string SoftAccent,
        string SoftSecondary,
        string SoftWarm,
        string SecondaryLine,
        string SecondaryText,
        string DarkBackground,
        string DarkPanel,
        string DarkLine)
    {
        public static ThemePalette From(string theme)
        {
            var value = theme ?? "";
            if (value.Contains("\u9ad8\u7aef", StringComparison.OrdinalIgnoreCase) || value.Contains("\u54c1\u724c", StringComparison.OrdinalIgnoreCase))
            {
                return new("C8A45D", "22C7A9", "F97316", "101828", "1D2939", "667085", "F7F4EE", "FFFBF3", "E7D8B5", "F2E2B8", "E6FFFA", "FFF3E8", "99F6E4", "0F766E", "0F1117", "191B22", "514229");
            }

            if (value.Contains("\u54a8\u8be2", StringComparison.OrdinalIgnoreCase) || value.Contains("\u89c6\u89c9\u53d9\u4e8b", StringComparison.OrdinalIgnoreCase))
            {
                return new("315CFF", "12B76A", "D0A85C", "101828", "1D2939", "667085", "F7F8FB", "EEF2FF", "D8DEEA", "C7D2FE", "ECFDF3", "FFF8E8", "A7F3D0", "047857", "09111F", "111827", "2D3748");
            }

            if (value.Contains("科技", StringComparison.OrdinalIgnoreCase))
            {
                return new("2563EB", "06B6D4", "F59E0B", "0F172A", "1E293B", "64748B", "F8FBFF", "EFF6FF", "D8E6FF", "BFDBFE", "ECFEFF", "FFF7ED", "A5F3FC", "0E7490", "07111F", "0B1B33", "1E3A5F");
            }

            if (value.Contains("极简", StringComparison.OrdinalIgnoreCase))
            {
                return new("111827", "64748B", "A16207", "111827", "374151", "6B7280", "FAFAFA", "F3F4F6", "E5E7EB", "E5E7EB", "F8FAFC", "FEF3C7", "CBD5E1", "475569", "111827", "1F2937", "374151");
            }

            if (value.Contains("发布会", StringComparison.OrdinalIgnoreCase))
            {
                return new("7C3AED", "EC4899", "F59E0B", "111827", "1F2937", "6B7280", "FBFAFF", "F5F3FF", "DDD6FE", "DDD6FE", "FCE7F3", "FFF7ED", "F9A8D4", "BE185D", "100820", "25133E", "4C1D95");
            }

            if (value.Contains("数据", StringComparison.OrdinalIgnoreCase))
            {
                return new("0F766E", "2563EB", "F97316", "0F172A", "1E293B", "64748B", "F8FAFC", "ECFDF5", "CCFBF1", "BAE6FD", "D1FAE5", "FFEDD5", "A7F3D0", "047857", "06231F", "123B35", "2DD4BF");
            }

            if (value.Contains("培训", StringComparison.OrdinalIgnoreCase))
            {
                return new("EA580C", "2563EB", "16A34A", "111827", "374151", "6B7280", "FFFBF7", "FFF7ED", "FED7AA", "DBEAFE", "EFF6FF", "DCFCE7", "BFDBFE", "1D4ED8", "1C1208", "32200E", "9A3412");
            }

            return new("2F7CFF", "36D6C4", "F79009", "0F172A", "1D2939", "667085", "F6F8FC", "EFF6FF", "E1E8F2", "B8D0FF", "ECFDF3", "FFF7ED", "B7E4CB", "067647", "0B1224", "111B33", "263653");
        }
    }

    private sealed record MarkdownBlock(string Text, int Level, bool IsBullet);
    private sealed record SlideDraft(string Title, string Subtitle, IReadOnlyList<string> Items, string Layout, string Visual, string Notes, string Theme, bool IsCover, string? ImageUrl = null);
}
