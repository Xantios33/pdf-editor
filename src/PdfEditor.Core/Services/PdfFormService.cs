using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using PdfEditor.Core.Models;

namespace PdfEditor.Core.Services;

public class PdfFormService : IPdfFormService
{
    public List<FormField> ExtractFormFields(string filePath, int pageIndex)
    {
        var result = new List<FormField>();

        using var reader = new PdfReader(filePath);
        using var pdfDoc = new PdfDocument(reader);

        var pageNumber = pageIndex + 1;
        var page = pdfDoc.GetPage(pageNumber);

        // Collect widget refs that are in AcroForm (to get their high-level PdfFormField)
        var form = PdfFormCreator.GetAcroForm(pdfDoc, false);
        var acroFormFields = form?.GetAllFormFields() ?? new Dictionary<string, PdfFormField>();

        // Build a lookup: widget indirect ref → (fieldName, PdfFormField)
        var widgetToField = new Dictionary<PdfIndirectReference, (string name, PdfFormField field)>();
        foreach (var kvp in acroFormFields)
        {
            var widgets = kvp.Value.GetWidgets();
            if (widgets == null) continue;
            foreach (var w in widgets)
            {
                var r = w.GetPdfObject().GetIndirectReference();
                if (r != null)
                    widgetToField[r] = (kvp.Key, kvp.Value);
            }
        }

        // Iterate ALL annotations on the page — this catches orphan widgets
        // that are not registered in the AcroForm /Fields array
        var annotations = page.GetAnnotations();
        foreach (var annot in annotations)
        {
            if (annot is not PdfWidgetAnnotation widget)
                continue;

            var rect = widget.GetRectangle();
            if (rect == null)
                continue;

            var widgetDict = widget.GetPdfObject();
            var indRef = widgetDict.GetIndirectReference();

            // Determine field name, type, value, and read-only status
            string fieldName;
            FormFieldType fieldType;
            string? currentValue;
            bool isReadOnly;
            List<string>? options = null;

            if (indRef != null && widgetToField.TryGetValue(indRef, out var fieldInfo))
            {
                // This widget IS in the AcroForm — use the high-level API
                fieldName = fieldInfo.name;
                fieldType = MapFieldType(fieldInfo.field);
                currentValue = fieldInfo.field.GetValueAsString();
                isReadOnly = fieldInfo.field.IsReadOnly();

                if (fieldType == FormFieldType.Dropdown && fieldInfo.field is PdfChoiceFormField choiceField)
                {
                    var opts = choiceField.GetOptions();
                    if (opts != null)
                    {
                        options = new List<string>();
                        foreach (var opt in opts)
                            options.Add(opt.ToString() ?? "");
                    }
                }
            }
            else
            {
                // Orphan widget — read directly from the PDF dictionary
                fieldName = ReadFieldName(widgetDict);
                if (string.IsNullOrEmpty(fieldName))
                    continue; // Skip unnamed widgets (decorative)

                fieldType = MapFieldTypeFromDict(widgetDict);
                currentValue = fieldType == FormFieldType.Checkbox
                    ? ReadCheckboxState(widgetDict)
                    : ReadFieldValue(widgetDict);
                isReadOnly = ReadIsReadOnly(widgetDict);
            }

            var llx = rect.GetAsNumber(0)?.FloatValue() ?? 0;
            var lly = rect.GetAsNumber(1)?.FloatValue() ?? 0;
            var urx = rect.GetAsNumber(2)?.FloatValue() ?? 0;
            var ury = rect.GetAsNumber(3)?.FloatValue() ?? 0;

            var x = Math.Min(llx, urx);
            var y = Math.Min(lly, ury);
            var width = Math.Abs(urx - llx);
            var height = Math.Abs(ury - lly);

            result.Add(new FormField
            {
                FieldName = fieldName,
                FieldType = fieldType,
                CurrentValue = currentValue,
                Options = options,
                X = x,
                Y = y,
                Width = width,
                Height = height,
                PageIndex = pageIndex,
                IsReadOnly = isReadOnly,
            });
        }

        return result;
    }

    public void SetFieldValue(string filePath, string fieldName, string value)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        using var inputStream = new MemoryStream(fileBytes);
        using var reader = new PdfReader(inputStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        // Try AcroForm first
        var form = PdfFormCreator.GetAcroForm(pdfDoc, false);
        if (form != null)
        {
            var fields = form.GetAllFormFields();
            if (fields.TryGetValue(fieldName, out var field))
            {
                field.SetValue(value);
                pdfDoc.Close();
                return;
            }
        }

        // Fallback: find orphan widget by field name and set value directly
        for (int p = 1; p <= pdfDoc.GetNumberOfPages(); p++)
        {
            var page = pdfDoc.GetPage(p);
            var annotations = page.GetAnnotations();
            foreach (var annot in annotations)
            {
                if (annot is not PdfWidgetAnnotation widget)
                    continue;

                var dict = widget.GetPdfObject();
                var name = ReadFieldName(dict);
                if (name != fieldName)
                    continue;

                var ft = dict.GetAsName(PdfName.FT)
                    ?? dict.GetAsDictionary(PdfName.Parent)?.GetAsName(PdfName.FT);

                if (PdfName.Btn.Equals(ft))
                {
                    SetOrphanCheckboxValue(dict, value);
                }
                else
                {
                    // Text / Choice field
                    dict.Put(PdfName.V, new PdfString(value));
                    // Remove cached appearance so iText/reader regenerates it
                    dict.Remove(PdfName.AP);
                }

                dict.SetModified();
                pdfDoc.Close();
                return;
            }
        }

        pdfDoc.Close();
    }

    private static string ReadFieldName(PdfDictionary dict)
    {
        var tObj = dict.GetAsString(PdfName.T);
        if (tObj != null)
            return tObj.ToUnicodeString();

        // Check parent
        var parent = dict.GetAsDictionary(PdfName.Parent);
        if (parent != null)
        {
            var parentT = parent.GetAsString(PdfName.T);
            if (parentT != null)
                return parentT.ToUnicodeString();
        }

        return "";
    }

    private static FormFieldType MapFieldTypeFromDict(PdfDictionary dict)
    {
        var ft = dict.GetAsName(PdfName.FT);
        if (ft == null)
        {
            var parent = dict.GetAsDictionary(PdfName.Parent);
            ft = parent?.GetAsName(PdfName.FT);
        }

        if (ft == null)
            return FormFieldType.Unknown;

        if (PdfName.Tx.Equals(ft))
            return FormFieldType.Text;
        if (PdfName.Ch.Equals(ft))
            return FormFieldType.Dropdown;
        if (PdfName.Btn.Equals(ft))
        {
            // Check flags for radio vs checkbox
            var ff = dict.GetAsNumber(PdfName.Ff);
            if (ff != null && (ff.IntValue() & (1 << 15)) != 0) // bit 16 = Radio
                return FormFieldType.RadioButton;
            return FormFieldType.Checkbox;
        }
        if (PdfName.Sig.Equals(ft))
            return FormFieldType.Signature;

        return FormFieldType.Unknown;
    }

    private static string? ReadFieldValue(PdfDictionary dict)
    {
        var vObj = dict.Get(PdfName.V);
        if (vObj == null)
        {
            var parent = dict.GetAsDictionary(PdfName.Parent);
            vObj = parent?.Get(PdfName.V);
        }

        if (vObj is PdfString pdfStr)
            return pdfStr.ToUnicodeString();
        if (vObj is PdfName pdfName)
            return pdfName.GetValue();

        return null;
    }

    private static bool ReadIsReadOnly(PdfDictionary dict)
    {
        var ff = dict.GetAsNumber(PdfName.Ff);
        if (ff == null)
        {
            var parent = dict.GetAsDictionary(PdfName.Parent);
            ff = parent?.GetAsNumber(PdfName.Ff);
        }

        if (ff == null)
            return false;

        return (ff.IntValue() & 1) != 0; // bit 1 = ReadOnly
    }

    /// <summary>
    /// Toggles an orphan checkbox widget. Reads /AP/N to discover the "on" state name
    /// (could be /On, /Oui, /Yes, etc.) and sets /V + /AS accordingly.
    /// </summary>
    private static void SetOrphanCheckboxValue(PdfDictionary dict, string value)
    {
        var wantChecked = value != "Off";

        // Find the "on" state name from /AP/N dictionary keys
        var onStateName = "On"; // default fallback
        var ap = dict.GetAsDictionary(PdfName.AP);
        if (ap != null)
        {
            var normal = ap.GetAsDictionary(PdfName.N);
            if (normal != null)
            {
                foreach (var key in normal.KeySet())
                {
                    var keyName = key.GetValue();
                    if (keyName != "Off")
                    {
                        onStateName = keyName;
                        break;
                    }
                }
            }
        }

        var stateName = wantChecked ? onStateName : "Off";
        dict.Put(PdfName.V, new PdfName(stateName));
        dict.Put(new PdfName("AS"), new PdfName(stateName));
        // Keep /AP intact — the existing appearance streams already have the right visuals
    }

    /// <summary>
    /// Reads the "on" state name from a checkbox widget's /AP/N dictionary.
    /// Returns the current checked state as "Yes" (checked) or "Off" (unchecked).
    /// </summary>
    private static string ReadCheckboxState(PdfDictionary dict)
    {
        var asObj = dict.GetAsName(new PdfName("AS"));
        if (asObj != null && asObj.GetValue() != "Off")
            return "Yes"; // checked
        return "Off";
    }

    private static FormFieldType MapFieldType(PdfFormField field)
    {
        if (field is PdfTextFormField)
            return FormFieldType.Text;
        if (field is PdfChoiceFormField)
            return FormFieldType.Dropdown;
        if (field is PdfButtonFormField buttonField)
        {
            if (buttonField.IsRadio())
                return FormFieldType.RadioButton;
            return FormFieldType.Checkbox;
        }
        if (field is PdfSignatureFormField)
            return FormFieldType.Signature;

        return FormFieldType.Unknown;
    }
}
