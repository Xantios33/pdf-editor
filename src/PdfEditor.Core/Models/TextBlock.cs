namespace PdfEditor.Core.Models;

public class TextBlock
{
    public required string Text { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float FontSize { get; set; }
    public string? FontName { get; set; }
    public int PageIndex { get; set; }
    /// <summary>PDF baseline Y coordinate (for accurate text re-stamping).</summary>
    public float BaselineY { get; set; }
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public bool IsUnderline { get; set; }
    public byte ColorR { get; set; }
    public byte ColorG { get; set; }
    public byte ColorB { get; set; }
}
