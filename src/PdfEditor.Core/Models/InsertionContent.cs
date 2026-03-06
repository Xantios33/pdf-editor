namespace PdfEditor.Core.Models;

public enum InsertionTool
{
    None,
    Text,
    Image,
    Line,
    Rectangle,
    Circle,
    // Form field tools
    FormTextField,
    FormCheckbox,
    FormRadioButton,
    FormDropdown,
    FormImage,
    FormDate,
}

public class InsertTextParams
{
    public int PageIndex { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public string Text { get; set; } = "";
    public float FontSize { get; set; } = 12f;
    public string? FontName { get; set; }
    public bool IsBold { get; set; }
    public bool IsItalic { get; set; }
    public float ColorR { get; set; }
    public float ColorG { get; set; }
    public float ColorB { get; set; }
}

public class InsertImageParams
{
    public int PageIndex { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public string ImageFilePath { get; set; } = "";
}

public class InsertShapeParams
{
    public int PageIndex { get; set; }
    public InsertionTool ShapeType { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public float StrokeWidth { get; set; } = 1f;
    public float StrokeR { get; set; }
    public float StrokeG { get; set; }
    public float StrokeB { get; set; }
    public float? FillR { get; set; }
    public float? FillG { get; set; }
    public float? FillB { get; set; }
}

public class CreateFormFieldParams
{
    public int PageIndex { get; set; }
    public InsertionTool FieldTool { get; set; }
    public string FieldName { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public string? DefaultValue { get; set; }
    public List<string>? Options { get; set; }
    public string? RadioGroupName { get; set; }
    public string? ImageFilePath { get; set; }
}
