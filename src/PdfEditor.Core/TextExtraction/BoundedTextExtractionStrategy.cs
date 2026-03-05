using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using PdfEditor.Core.Models;
using TextBlock = PdfEditor.Core.Models.TextBlock;

namespace PdfEditor.Core.TextExtraction;

/// <summary>
/// Custom iText extraction strategy that captures text chunks with their
/// bounding rectangles, font name, and font size.
/// Groups nearby characters into logical text blocks (lines).
/// </summary>
public class BoundedTextExtractionStrategy : ITextExtractionStrategy, IEventListener
{
    private readonly List<TextChunk> _chunks = new();
    private readonly int _pageIndex;

    public BoundedTextExtractionStrategy(int pageIndex)
    {
        _pageIndex = pageIndex;
    }

    public void EventOccurred(IEventData data, EventType type)
    {
        if (type != EventType.RENDER_TEXT || data is not TextRenderInfo renderInfo)
            return;

        var text = renderInfo.GetText();
        if (string.IsNullOrEmpty(text))
            return;

        var baseline = renderInfo.GetBaseline();
        var ascentLine = renderInfo.GetAscentLine();
        var descentLine = renderInfo.GetDescentLine();

        var bottomLeft = descentLine.GetStartPoint();
        var topRight = ascentLine.GetEndPoint();
        var baselineY = baseline.GetStartPoint().Get(1);

        // Compute actual rendered font size accounting for CTM * text matrix scaling
        var nominalFontSize = renderInfo.GetFontSize();
        var textMatrix = renderInfo.GetTextMatrix();
        // Y-scale factor: length of the unit Y vector transformed by the text matrix
        float yScale = new Vector(0, nominalFontSize, 0).Cross(textMatrix).Length() / nominalFontSize;
        var fontSize = Math.Abs(nominalFontSize * yScale);

        var font = renderInfo.GetFont();
        var fontName = GetCleanFontName(font);

        // Detect bold/italic using multi-layer strategy
        var (isBold, isItalic) = DetectFontStyle(font, fontName);

        // Extract fill color (considering text render mode)
        var (colorR, colorG, colorB) = ExtractTextColor(renderInfo);

        _chunks.Add(new TextChunk
        {
            Text = text,
            X = bottomLeft.Get(0),
            Y = bottomLeft.Get(1),
            Width = topRight.Get(0) - bottomLeft.Get(0),
            Height = topRight.Get(1) - bottomLeft.Get(1),
            BaselineY = baselineY,
            FontSize = fontSize,
            FontName = fontName,
            IsBold = isBold,
            IsItalic = isItalic,
            ColorR = colorR,
            ColorG = colorG,
            ColorB = colorB
        });
    }

    /// <summary>
    /// Gets a clean font family name, stripping subset prefix (ABCDEF+) and style suffixes.
    /// Prefers the family name from the font's name table over the PostScript name.
    /// </summary>
    private static string GetCleanFontName(PdfFont? font)
    {
        if (font == null) return "Unknown";

        var fontProgram = font.GetFontProgram();
        var fontNames = fontProgram?.GetFontNames();
        if (fontNames == null) return "Unknown";

        // Try family name first (cleaner than PostScript name)
        var familyNameTable = fontNames.GetFamilyName();
        if (familyNameTable is { Length: > 0 })
        {
            var entry = familyNameTable[0];
            if (entry.Length > 0)
            {
                var familyName = entry[^1]; // last element is the actual name
                if (!string.IsNullOrEmpty(familyName))
                    return familyName;
            }
        }

        // Fall back to PostScript name, strip subset prefix (ABCDEF+)
        var psName = fontNames.GetFontName() ?? "Unknown";
        if (psName.Length > 7 && psName[6] == '+')
            psName = psName.Substring(7);

        return psName;
    }

    /// <summary>
    /// Multi-layer bold/italic detection:
    /// 1. FontNames.IsBold/IsItalic (macStyle flags from font program)
    /// 2. PDF font descriptor flags and FontWeight
    /// 3. Font name string parsing (fallback)
    /// </summary>
    private static (bool isBold, bool isItalic) DetectFontStyle(PdfFont? font, string fontName)
    {
        if (font == null) return (false, false);

        bool isBold = false;
        bool isItalic = false;

        // Layer 1: FontNames from the font program (most reliable)
        var fontNames = font.GetFontProgram()?.GetFontNames();
        if (fontNames != null)
        {
            isBold = fontNames.IsBold() || fontNames.GetFontWeight() >= 700;
            isItalic = fontNames.IsItalic();
        }

        // Layer 2: PDF font descriptor flags and FontWeight
        if (!isBold || !isItalic)
        {
            try
            {
                var fontDict = font.GetPdfObject() as PdfDictionary;
                var descriptorDict = fontDict?.GetAsDictionary(PdfName.FontDescriptor);
                if (descriptorDict != null)
                {
                    // Check /Flags
                    var flagsObj = descriptorDict.GetAsNumber(PdfName.Flags);
                    if (flagsObj != null)
                    {
                        int flags = flagsObj.IntValue();
                        if (!isItalic) isItalic = (flags & 64) != 0;       // Bit 7: Italic
                        if (!isBold) isBold = (flags & 262144) != 0;       // Bit 19: ForceBold
                    }

                    // Check /FontWeight (more reliable for bold)
                    var weightObj = descriptorDict.GetAsNumber(PdfName.FontWeight);
                    if (weightObj != null && !isBold)
                        isBold = weightObj.IntValue() >= 700;

                    // Check /ItalicAngle (non-zero means italic)
                    var italicAngle = descriptorDict.GetAsNumber(PdfName.ItalicAngle);
                    if (italicAngle != null && !isItalic)
                        isItalic = Math.Abs(italicAngle.FloatValue()) > 0.5f;
                }
            }
            catch
            {
                // Ignore font descriptor access errors
            }
        }

        // Layer 3: Font name string parsing (fallback)
        if (!isBold || !isItalic)
        {
            var nameUpper = fontName.ToUpperInvariant();
            if (!isBold) isBold = nameUpper.Contains("BOLD");
            if (!isItalic) isItalic = nameUpper.Contains("ITALIC") || nameUpper.Contains("OBLIQUE");
        }

        return (isBold, isItalic);
    }

    /// <summary>
    /// Extracts text color considering text render mode (fill vs stroke).
    /// </summary>
    private static (byte r, byte g, byte b) ExtractTextColor(TextRenderInfo renderInfo)
    {
        try
        {
            int renderMode = renderInfo.GetTextRenderMode();
            Color? color = null;

            // Modes 0, 2, 4, 6 use fill color; modes 1, 5 use stroke color
            if (renderMode == 0 || renderMode == 2 || renderMode == 4 || renderMode == 6)
                color = renderInfo.GetFillColor();
            else if (renderMode == 1 || renderMode == 5)
                color = renderInfo.GetStrokeColor();
            // Modes 3, 7 = invisible text

            if (color != null)
                return ConvertToRgb(color);
        }
        catch
        {
            // Default to black on any error
        }
        return (0, 0, 0);
    }

    private static byte ToByte(float value)
    {
        return (byte)Math.Clamp(value * 255f, 0f, 255f);
    }

    private static (byte r, byte g, byte b) ConvertToRgb(Color color)
    {
        float[] components = color.GetColorValue();

        if (color is DeviceRgb)
        {
            return (ToByte(components[0]), ToByte(components[1]), ToByte(components[2]));
        }

        if (color is DeviceCmyk cmyk)
        {
            // Use iText's built-in CMYK→RGB conversion
            var rgb = DeviceCmyk.ConvertCmykToRgb(cmyk);
            var rgbValues = rgb.GetColorValue();
            return (ToByte(rgbValues[0]), ToByte(rgbValues[1]), ToByte(rgbValues[2]));
        }

        if (color is DeviceGray)
        {
            var gray = ToByte(components[0]);
            return (gray, gray, gray);
        }

        // ICCBased or other color spaces: try reading as RGB if 3 components
        if (components.Length >= 3)
            return (ToByte(components[0]), ToByte(components[1]), ToByte(components[2]));

        if (components.Length == 1)
        {
            var gray = ToByte(components[0]);
            return (gray, gray, gray);
        }

        // Fallback: black
        return (0, 0, 0);
    }

    public ICollection<EventType> GetSupportedEvents()
    {
        return new HashSet<EventType> { EventType.RENDER_TEXT };
    }

    public string GetResultantText()
    {
        return string.Join("", _chunks.Select(c => c.Text));
    }

    /// <summary>
    /// Returns text blocks grouped by line.
    /// Chunks on the same baseline (within tolerance) are merged into a single block.
    /// </summary>
    public List<TextBlock> GetTextBlocks(float pageHeight)
    {
        if (_chunks.Count == 0)
            return new List<TextBlock>();

        // Sort by Y descending (top to bottom), then X ascending (left to right)
        var sorted = _chunks.OrderByDescending(c => c.Y).ThenBy(c => c.X).ToList();

        var blocks = new List<TextBlock>();
        var currentLine = new List<TextChunk> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            var chunk = sorted[i];
            var prev = currentLine[^1];

            // Same line if Y coordinates are close (within half the font height)
            var yTolerance = Math.Max(prev.Height, chunk.Height) * 0.5f;
            var isSameLine = Math.Abs(chunk.Y - prev.Y) < yTolerance;

            // Also check horizontal gap — if too far, start a new block
            var horizontalGap = chunk.X - (prev.X + prev.Width);
            var maxGap = prev.FontSize * 2;

            if (isSameLine && horizontalGap < maxGap)
            {
                currentLine.Add(chunk);
            }
            else
            {
                blocks.Add(MergeChunksToBlock(currentLine, pageHeight));
                currentLine = new List<TextChunk> { chunk };
            }
        }

        if (currentLine.Count > 0)
            blocks.Add(MergeChunksToBlock(currentLine, pageHeight));

        return blocks;
    }

    private TextBlock MergeChunksToBlock(List<TextChunk> chunks, float pageHeight)
    {
        // Sort by X to ensure left-to-right reading order within the line
        // (chunks may arrive in wrong order due to Y-descending primary sort
        //  when Y values differ slightly on the same visual line)
        var ordered = chunks.OrderBy(c => c.X).ToList();
        var text = string.Join("", ordered.Select(c => c.Text));
        var minX = chunks.Min(c => c.X);
        var minY = chunks.Min(c => c.Y);
        var maxX = chunks.Max(c => c.X + c.Width);
        var maxY = chunks.Max(c => c.Y + c.Height);
        var fontSize = chunks.Average(c => c.FontSize);
        var fontName = chunks[0].FontName;

        // Take style properties from the first chunk (by X position)
        var first = ordered[0];
        // Average baseline Y across chunks on this line
        var baselineY = (float)chunks.Average(c => c.BaselineY);

        return new TextBlock
        {
            Text = text,
            // PDF coordinates: origin is bottom-left
            // We store in PDF coordinate system, conversion to screen happens in UI
            X = minX,
            Y = minY,
            Width = maxX - minX,
            Height = maxY - minY,
            BaselineY = baselineY,
            FontSize = (float)fontSize,
            FontName = fontName,
            PageIndex = _pageIndex,
            IsBold = first.IsBold,
            IsItalic = first.IsItalic,
            ColorR = first.ColorR,
            ColorG = first.ColorG,
            ColorB = first.ColorB
        };
    }

    private class TextChunk
    {
        public string Text { get; set; } = "";
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float BaselineY { get; set; }
        public float FontSize { get; set; }
        public string FontName { get; set; } = "";
        public bool IsBold { get; set; }
        public bool IsItalic { get; set; }
        public byte ColorR { get; set; }
        public byte ColorG { get; set; }
        public byte ColorB { get; set; }
    }
}
