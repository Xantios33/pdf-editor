using PdfEditor.Core.Models;

namespace PdfEditor.Core.Services;

public interface IPdfFormService
{
    List<FormField> ExtractFormFields(string filePath, int pageIndex);
    List<FormField> ExtractAllFormFields(string filePath);
    void SetFieldValue(string filePath, string fieldName, string value);
    void CreateFormField(string filePath, CreateFormFieldParams parameters);
    void UpdateFormFieldProperties(string filePath, FormFieldProperties properties);
}
