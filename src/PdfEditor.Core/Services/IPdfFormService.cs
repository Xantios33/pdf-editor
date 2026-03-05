using PdfEditor.Core.Models;

namespace PdfEditor.Core.Services;

public interface IPdfFormService
{
    List<FormField> ExtractFormFields(string filePath, int pageIndex);
    void SetFieldValue(string filePath, string fieldName, string value);
}
