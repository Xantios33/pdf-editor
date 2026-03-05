using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.PdfCleanup;
using PdfEditor.Core.Models;
using PdfEditor.Core.TextExtraction;
using StandardFonts = iText.IO.Font.Constants.StandardFonts;

namespace PdfEditor.Core.Services;

public class PdfTextService : IPdfTextService
{
    public List<TextBlock> ExtractTextBlocks(string filePath, int pageIndex)
    {
        using var reader = new PdfReader(filePath);
        using var pdfDoc = new PdfDocument(reader);

        var page = pdfDoc.GetPage(pageIndex + 1); // iText uses 1-based page numbers
        var pageHeight = page.GetPageSize().GetHeight();

        var strategy = new BoundedTextExtractionStrategy(pageIndex);
        PdfTextExtractor.GetTextFromPage(page, strategy);

        return strategy.GetTextBlocks(pageHeight);
    }

    public void ReplaceText(string filePath, TextBlock originalBlock, string newText)
    {
        // Read into memory, modify, write back to the same file
        var fileBytes = File.ReadAllBytes(filePath);
        using var inputStream = new MemoryStream(fileBytes);
        using var reader = new PdfReader(inputStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        var pageNumber = originalBlock.PageIndex + 1; // iText uses 1-based

        // Step 1: Actually REMOVE the original text from the content stream
        // using pdfSweep — this deletes the PDF operators, not just covers them.
        // Use a tight rectangle based on baseline + font metrics to avoid
        // eating into adjacent lines with tight leading.
        var page = pdfDoc.GetPage(pageNumber);
        var cleanRect = GetTightCleanRect(originalBlock);
        CleanUpProtectingAnnotations(pdfDoc, page, pageNumber, cleanRect);

        // Step 2: Stamp the new text at the original baseline position
        var canvas = new PdfCanvas(page);
        canvas.SaveState();

        // Apply text color
        var fillColor = new DeviceRgb(originalBlock.ColorR, originalBlock.ColorG, originalBlock.ColorB);
        canvas.SetFillColor(fillColor);

        // Resolve the correct standard PDF font
        var font = ResolveFont(originalBlock.FontName, originalBlock.IsBold, originalBlock.IsItalic);

        // Use the captured baseline Y
        var baselineY = originalBlock.BaselineY;
        canvas.BeginText();
        canvas.SetFontAndSize(font, originalBlock.FontSize);
        canvas.MoveText(originalBlock.X, baselineY);
        canvas.ShowText(newText);
        canvas.EndText();

        // Step 3: Draw underline if needed
        if (originalBlock.IsUnderline)
        {
            var textWidth = font.GetWidth(newText, originalBlock.FontSize);
            var lineY = baselineY - originalBlock.FontSize * 0.1f;
            var lineWidth = originalBlock.FontSize / 20f;

            canvas.SetStrokeColor(fillColor);
            canvas.SetLineWidth(lineWidth);
            canvas.MoveTo(originalBlock.X, lineY);
            canvas.LineTo(originalBlock.X + textWidth, lineY);
            canvas.Stroke();
        }

        canvas.RestoreState();

        pdfDoc.Close();
    }

    public void MoveTextBlock(string filePath, TextBlock block, float newX, float newY)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        using var inputStream = new MemoryStream(fileBytes);
        using var reader = new PdfReader(inputStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        var pageNumber = block.PageIndex + 1;

        // Step 1: Clean the original rectangle (tight, baseline-based)
        var page = pdfDoc.GetPage(pageNumber);
        var cleanRect = GetTightCleanRect(block);
        CleanUpProtectingAnnotations(pdfDoc, page, pageNumber, cleanRect);

        // Step 2: Stamp text at the new position
        var newBaselineY = block.BaselineY + (newY - block.Y);
        var canvas = new PdfCanvas(page);
        canvas.SaveState();

        var fillColor = new DeviceRgb(block.ColorR, block.ColorG, block.ColorB);
        canvas.SetFillColor(fillColor);

        var font = ResolveFont(block.FontName, block.IsBold, block.IsItalic);

        canvas.BeginText();
        canvas.SetFontAndSize(font, block.FontSize);
        canvas.MoveText(newX, newBaselineY);
        canvas.ShowText(block.Text);
        canvas.EndText();

        // Step 3: Draw underline if needed
        if (block.IsUnderline)
        {
            var textWidth = font.GetWidth(block.Text, block.FontSize);
            var lineY = newBaselineY - block.FontSize * 0.1f;
            var lineWidth = block.FontSize / 20f;

            canvas.SetStrokeColor(fillColor);
            canvas.SetLineWidth(lineWidth);
            canvas.MoveTo(newX, lineY);
            canvas.LineTo(newX + textWidth, lineY);
            canvas.Stroke();
        }

        canvas.RestoreState();

        pdfDoc.Close();
    }

    /// <summary>
    /// Runs PdfCleaner.CleanUp while temporarily removing widget annotations
    /// from the page so their appearance streams are not destroyed.
    /// </summary>
    private static void CleanUpProtectingAnnotations(
        PdfDocument pdfDoc, PdfPage page, int pageNumber, Rectangle cleanRect)
    {
        // Collect widget annotations and detach them from the page
        var annots = page.GetPdfObject().GetAsArray(PdfName.Annots);
        var savedWidgets = new List<PdfObject>();

        if (annots != null)
        {
            for (int i = annots.Size() - 1; i >= 0; i--)
            {
                var obj = annots.Get(i, false);
                var resolved = annots.Get(i);
                if (resolved is PdfDictionary dict)
                {
                    var subtype = dict.GetAsName(PdfName.Subtype);
                    if (PdfName.Widget.Equals(subtype))
                    {
                        savedWidgets.Add(obj);
                        annots.Remove(i);
                    }
                }
            }
        }

        // Now clean — only page content stream is affected
        var cleanUpLocations = new List<PdfCleanUpLocation>
        {
            new PdfCleanUpLocation(pageNumber, cleanRect, ColorConstants.WHITE)
        };
        PdfCleaner.CleanUp(pdfDoc, cleanUpLocations);

        // Restore widget annotations
        if (savedWidgets.Count > 0)
        {
            if (annots == null)
            {
                annots = new PdfArray();
                page.GetPdfObject().Put(PdfName.Annots, annots);
            }
            foreach (var widget in savedWidgets)
                annots.Add(widget);
        }
    }

    /// <summary>
    /// Computes a tight cleaning rectangle based on baseline + font metrics
    /// instead of the full descent→ascent bounding box.
    /// Typical font metrics: ascender ≈ 80% of fontSize, descender ≈ 20%.
    /// The full bbox often overlaps adjacent lines on tight leading, so we
    /// shrink it slightly to avoid collateral damage.
    /// </summary>
    private static Rectangle GetTightCleanRect(TextBlock block)
    {
        // Use baseline-relative bounds:
        //   bottom = baseline - descent (descender portion of font)
        //   top    = baseline + ascent  (ascender portion of font)
        // Standard PDF fonts: descender ≈ 20-25% of fontSize, ascender ≈ 75-80%.
        // We use conservative values to stay strictly within the glyph zone.
        var descent = block.FontSize * 0.25f;
        var ascent = block.FontSize * 0.75f;

        var bottom = block.BaselineY - descent;
        var top = block.BaselineY + ascent;

        return new Rectangle(
            block.X,
            bottom,
            block.Width,
            top - bottom);
    }

    private static PdfFont ResolveFont(string? fontName, bool isBold, bool isItalic)
    {
        var family = DetectFontFamily(fontName);

        var standardFontName = family switch
        {
            FontFamily.Times => (isBold, isItalic) switch
            {
                (true, true) => StandardFonts.TIMES_BOLDITALIC,
                (true, false) => StandardFonts.TIMES_BOLD,
                (false, true) => StandardFonts.TIMES_ITALIC,
                _ => StandardFonts.TIMES_ROMAN
            },
            FontFamily.Courier => (isBold, isItalic) switch
            {
                (true, true) => StandardFonts.COURIER_BOLDOBLIQUE,
                (true, false) => StandardFonts.COURIER_BOLD,
                (false, true) => StandardFonts.COURIER_OBLIQUE,
                _ => StandardFonts.COURIER
            },
            // Default to Helvetica
            _ => (isBold, isItalic) switch
            {
                (true, true) => StandardFonts.HELVETICA_BOLDOBLIQUE,
                (true, false) => StandardFonts.HELVETICA_BOLD,
                (false, true) => StandardFonts.HELVETICA_OBLIQUE,
                _ => StandardFonts.HELVETICA
            }
        };

        try
        {
            return PdfFontFactory.CreateFont(standardFontName);
        }
        catch
        {
            return PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
        }
    }

    private static FontFamily DetectFontFamily(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName))
            return FontFamily.Helvetica;

        var upper = fontName.ToUpperInvariant();

        if (upper.Contains("TIMES") || upper.Contains("SERIF") || upper.Contains("ROMAN"))
            return FontFamily.Times;

        if (upper.Contains("COURIER") || upper.Contains("MONO") || upper.Contains("CONSOL"))
            return FontFamily.Courier;

        // Helvetica, Arial, Sans, and everything else
        return FontFamily.Helvetica;
    }

    private enum FontFamily
    {
        Helvetica,
        Times,
        Courier
    }
}
