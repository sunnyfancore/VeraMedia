using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using SkiaSharp;

namespace VeraMedia.Api.Services;

public interface IPptxThumbnailRenderer
{
    List<SlideThumbnail> RenderAllSlides(string pptxPath, int width = 960, int height = 540);
}

public sealed record SlideThumbnail(int Index, byte[] PngData);

public sealed class PptxThumbnailRenderer : IPptxThumbnailRenderer
{
    private static readonly XNamespace PNs = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace ANs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace RNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private SKTypeface? _regularFont;
    private SKTypeface? _boldFont;

    public List<SlideThumbnail> RenderAllSlides(string pptxPath, int width = 960, int height = 540)
    {
        var results = new List<SlideThumbnail>();
        using var archive = System.IO.Compression.ZipFile.OpenRead(pptxPath);

        // Get presentation.xml for slide order and size
        var presEntry = archive.GetEntry("ppt/presentation.xml");
        long slideCx = 12192000, slideCy = 6858000;
        var slidePaths = new List<string>();

        if (presEntry is not null)
        {
            var presDoc = XDocument.Load(presEntry.Open());
            var sldSz = presDoc.Root?.Element(PNs + "sldSz");
            if (sldSz != null)
            {
                slideCx = (long?)sldSz.Attribute("cx") ?? slideCx;
                slideCy = (long?)sldSz.Attribute("cy") ?? slideCy;
            }

            // Get ordered slide paths from presentation rels
            var presRelsEntry = archive.GetEntry("ppt/_rels/presentation.xml.rels");
            if (presRelsEntry is not null)
            {
                var relsDoc = XDocument.Load(presRelsEntry.Open());
                var rels = relsDoc.Root?.Elements()
                    .Where(e => (string?)e.Attribute("Type") == "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide")
                    .ToDictionary(e => (string?)e.Attribute("Id") ?? "", e => "ppt/" + (string?)e.Attribute("Target")) ?? [];

                foreach (var sldId in presDoc.Root?.Element(PNs + "sldIdLst")?.Elements(PNs + "sldId") ?? [])
                {
                    var rid = (string?)sldId.Attribute(RNs + "id");
                    if (rid is not null && rels.TryGetValue(rid, out var path))
                        slidePaths.Add(path);
                }
            }
        }

        // Fallback: enumerate slides directly
        if (slidePaths.Count == 0)
        {
            slidePaths = archive.Entries
                .Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$"))
                .OrderBy(e => ExtractNumber(e.FullName))
                .Select(e => e.FullName)
                .ToList();
        }

        var scaleX = (float)width / slideCx;
        var scaleY = (float)height / slideCy;

        for (var i = 0; i < slidePaths.Count; i++)
        {
            var entry = archive.GetEntry(slidePaths[i]);
            if (entry is null) continue;

            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            var slideDoc = XDocument.Load(entry.Open());
            var shapeTree = slideDoc.Descendants(PNs + "spTree").FirstOrDefault();
            if (shapeTree is not null)
            {
                RenderShapeTree(canvas, shapeTree, scaleX, scaleY, archive);
            }

            var data = bitmap.Encode(SKEncodedImageFormat.Png, 90).ToArray();
            results.Add(new SlideThumbnail(i + 1, data));
        }

        return results;
    }

    private void RenderShapeTree(SKCanvas canvas, XElement shapeTree, float scaleX, float scaleY, System.IO.Compression.ZipArchive archive)
    {
        foreach (var sp in shapeTree.Elements(PNs + "sp"))
            RenderShape(canvas, sp, scaleX, scaleY, archive);
        foreach (var grp in shapeTree.Elements(PNs + "grpSp"))
            RenderShapeTree(canvas, grp, scaleX, scaleY, archive);
    }

    private void RenderShape(SKCanvas canvas, XElement sp, float scaleX, float scaleY, System.IO.Compression.ZipArchive archive)
    {
        var spPr = sp.Element(PNs + "spPr");
        if (spPr is null) return;

        // Get transform
        var xfrm = spPr.Element(ANs + "xfrm");
        if (xfrm is null) return;

        var off = xfrm.Element(ANs + "off");
        var ext = xfrm.Element(ANs + "ext");
        if (off is null || ext is null) return;

        var x = AttrLong(off, "x") * scaleX;
        var y = AttrLong(off, "y") * scaleY;
        var w = AttrLong(ext, "cx") * scaleX;
        var h = AttrLong(ext, "cy") * scaleY;

        if (w <= 0 || h <= 0) return;

        var rect = new SKRect(x, y, x + w, y + h);

        // Draw fill
        var fillColor = GetFill(spPr);
        if (fillColor is not null)
        {
            canvas.DrawRect(rect, new SKPaint { Color = fillColor.Value, IsAntialias = true });
        }

        // Draw outline
        var outlineColor = GetOutline(spPr);
        if (outlineColor is not null)
        {
            canvas.DrawRect(rect, new SKPaint { Color = outlineColor.Value, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 });
        }

        // Draw text
        var txBody = sp.Element(PNs + "txBody");
        if (txBody is null) txBody = spPr.Element(ANs + "txBody"); // fallback
        if (txBody is not null)
            RenderText(canvas, txBody, rect);
    }

    private void RenderText(SKCanvas canvas, XElement txBody, SKRect bounds)
    {
        var bodyPr = txBody.Element(ANs + "bodyPr");
        var anchor = (string?)bodyPr?.Attribute("anchor") ?? "t";
        var paragraphs = txBody.Elements(ANs + "p").ToList();
        if (paragraphs.Count == 0) return;

        var pad = 6f;
        var textRect = new SKRect(bounds.Left + pad, bounds.Top + pad, bounds.Right - pad, bounds.Bottom - pad);
        if (textRect.Width <= 0 || textRect.Height <= 0) return;

        // Collect lines
        var lines = new List<(string Text, float Size, bool Bold, SKColor Color, string Align)>();

        foreach (var para in paragraphs)
        {
            var pPr = para.Element(ANs + "pPr");
            var align = (string?)pPr?.Attribute("algn") ?? "l";
            var defRPr = pPr?.Element(ANs + "defRPr");
            var defSize = AttrInt(defRPr, "sz", 1800);
            var defBold = (string?)defRPr?.Attribute("b") == "1";
            var defColor = defRPr is not null ? GetRunColor(defRPr) : new SKColor(0x33, 0x41, 0x55);

            var runs = para.Elements(ANs + "r").ToList();
            if (runs.Count == 0)
            {
                // Empty paragraph = line spacing
                var emptySize = defSize / 100f * 0.75f;
                if (emptySize <= 0) emptySize = 10f;
                lines.Add(("", emptySize * 0.5f, false, SKColors.Transparent, align));
                continue;
            }

            foreach (var run in runs)
            {
                var rPr = run.Element(ANs + "rPr");
                var sz = AttrInt(rPr, "sz", defSize);
                var bold = (string?)rPr?.Attribute("b") is "1" or "true" ? true : (string?)rPr?.Attribute("b") is "0" or "false" ? false : defBold;
                var color = rPr is not null ? GetRunColor(rPr) : defColor;
                var text = (string?)run.Element(ANs + "t") ?? "";

                if (!string.IsNullOrEmpty(text))
                {
                    var fontSize = sz / 100f * 0.75f;
                    if (fontSize <= 0) fontSize = 13f;
                    lines.Add((text, fontSize, bold, color, align));
                }
            }
        }

        // Measure and wrap
        var wrappedLines = new List<(string Text, float Size, bool Bold, SKColor Color, string Align)>();
        float totalHeight = 0;

        foreach (var (text, size, bold, color, align) in lines)
        {
            if (string.IsNullOrEmpty(text))
            {
                wrappedLines.Add(("", size, false, SKColors.Transparent, align));
                totalHeight += size;
                continue;
            }

            using var paint = new SKPaint { TextSize = size, FakeBoldText = bold, Typeface = GetTypeface(bold) };
            var wrapped = WrapText(paint, text, textRect.Width);
            foreach (var line in wrapped)
            {
                wrappedLines.Add((line, size, bold, color, align));
                totalHeight += size * 1.35f;
            }
        }

        // Y offset for vertical alignment
        float yOffset = anchor switch
        {
            "ctr" => Math.Max(0, (textRect.Height - totalHeight) / 2),
            "b" => Math.Max(0, textRect.Height - totalHeight),
            _ => 0
        };

        // Render
        canvas.Save();
        canvas.ClipRect(bounds);

        float curY = textRect.Top + yOffset;
        foreach (var (text, size, bold, color, align) in wrappedLines)
        {
            if (string.IsNullOrEmpty(text)) { curY += size; continue; }

            using var paint = new SKPaint
            {
                Color = color,
                IsAntialias = true,
                TextSize = size,
                FakeBoldText = bold,
                Typeface = GetTypeface(bold),
            };

            curY += size;
            float xPos = textRect.Left;
            if (align == "ctr")
                xPos = textRect.Left + (textRect.Width - paint.MeasureText(text)) / 2;
            else if (align == "r")
                xPos = textRect.Right - paint.MeasureText(text);

            canvas.DrawText(text, xPos, curY, paint);
            curY += size * 0.35f;
        }

        canvas.Restore();
    }

    private static List<string> WrapText(SKPaint paint, string text, float maxWidth)
    {
        if (maxWidth <= 0 || paint.MeasureText(text) <= maxWidth)
            return [text];

        var lines = new List<string>();
        var current = "";
        foreach (var ch in text)
        {
            var test = current + ch;
            if (paint.MeasureText(test) > maxWidth && current.Length > 0)
            {
                lines.Add(current);
                current = ch.ToString();
            }
            else
            {
                current = test;
            }
        }
        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    private SKTypeface GetTypeface(bool bold)
    {
        if (bold)
        {
            return _boldFont ??= SKTypeface.FromFamilyName("Noto Sans CJK SC", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                ?? SKTypeface.FromFamilyName("Microsoft YaHei", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                ?? SKTypeface.Default;
        }
        return _regularFont ??= SKTypeface.FromFamilyName("Noto Sans CJK SC", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            ?? SKTypeface.FromFamilyName("Microsoft YaHei", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
            ?? SKTypeface.Default;
    }

    private static SKColor GetRunColor(XElement rPr)
    {
        var solid = rPr.Element(ANs + "solidFill");
        if (solid is not null) return ParseColor(solid);
        return new SKColor(0x33, 0x41, 0x55);
    }

    private static SKColor? GetFill(XElement spPr)
    {
        var solid = spPr.Element(ANs + "solidFill");
        if (solid is not null) return ParseColor(solid);

        var grad = spPr.Element(ANs + "gradFill");
        if (grad is not null)
        {
            var first = grad.Descendants(ANs + "gs").FirstOrDefault();
            if (first?.Element(ANs + "srgbClr") is { } clr)
                return ParseHexClr(clr);
            if (first?.Element(ANs + "schemeClr") is { } scheme)
                return SchemeColor(scheme);
        }

        if (spPr.Element(ANs + "noFill") is not null) return null;
        return null;
    }

    private static SKColor? GetOutline(XElement spPr)
    {
        var ln = spPr.Element(ANs + "ln");
        if (ln is null) return null;
        if (ln.Element(ANs + "noFill") is not null) return null;
        var solid = ln.Element(ANs + "solidFill");
        if (solid is not null) return ParseColor(solid);
        return null;
    }

    private static SKColor ParseColor(XElement fillParent)
    {
        if (fillParent.Element(ANs + "srgbClr") is { } rgb) return ParseHexClr(rgb);
        if (fillParent.Element(ANs + "schemeClr") is { } scheme) return SchemeColor(scheme);
        if (fillParent.Element(ANs + "prstClr") is { } preset)
        {
            return (string?)preset.Attribute("val") switch
            {
                "black" => SKColors.Black, "white" => SKColors.White,
                "red" => SKColors.Red, "blue" => SKColors.Blue,
                _ => SKColors.Gray
            };
        }
        return SKColors.Gray;
    }

    private static SKColor ParseHexClr(XElement clr)
    {
        var hex = ((string?)clr.Attribute("val") ?? "").Replace("#", "").Trim();
        if (hex.Length == 6)
        {
            try { return new SKColor(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16)); }
            catch { }
        }
        return SKColors.Gray;
    }

    private static SKColor SchemeColor(XElement scheme)
    {
        return (string?)scheme.Attribute("val") switch
        {
            "accent1" => new SKColor(0x2F, 0x7C, 0xFF),
            "accent2" => new SKColor(0x36, 0xD6, 0xC4),
            "accent3" => new SKColor(0xF7, 0x90, 0x09),
            "accent4" => new SKColor(0x12, 0xB7, 0x6A),
            "accent5" => new SKColor(0x7C, 0x3A, 0xED),
            "accent6" => new SKColor(0xEF, 0x44, 0x44),
            "dk1" or "tx1" => new SKColor(0x00, 0x00, 0x00),
            "dk2" or "tx2" => new SKColor(0x1F, 0x29, 0x37),
            "lt1" or "bg1" => SKColors.White,
            "lt2" or "bg2" => new SKColor(0xF8, 0xFA, 0xFC),
            "hlink" => new SKColor(0x15, 0x5E, 0xEF),
            _ => SKColors.Gray
        };
    }

    private static long AttrLong(XElement el, string name) => (long?)el.Attribute(name) ?? 0;
    private static int AttrInt(XElement? el, string name, int def) => el is null ? def : (int?)el.Attribute(name) ?? def;
    private static int ExtractNumber(string s) { var m = System.Text.RegularExpressions.Regex.Match(s, @"\d+"); return m.Success ? int.Parse(m.Value) : 0; }
}
