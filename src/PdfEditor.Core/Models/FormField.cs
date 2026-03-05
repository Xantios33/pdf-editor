namespace PdfEditor.Core.Models;

public enum FormFieldType { Text, Checkbox, Dropdown, RadioButton, Signature, Unknown }

public class FormField
{
    public required string FieldName { get; set; }
    public FormFieldType FieldType { get; set; }
    public string? CurrentValue { get; set; }
    public List<string>? Options { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public int PageIndex { get; set; }
    public bool IsReadOnly { get; set; }
}
