using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using PdfEditor.Core.Models;
using StandardFonts = iText.IO.Font.Constants.StandardFonts;

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
                        for (int i = 0; i < opts.Size(); i++)
                        {
                            var opt = opts.Get(i);
                            if (opt is PdfString pdfStr)
                                options.Add(pdfStr.ToUnicodeString());
                            else if (opt is PdfArray optArr && optArr.Size() > 1)
                                options.Add(((PdfString)optArr.Get(1)).ToUnicodeString());
                            else
                                options.Add(opt.ToString() ?? "");
                        }
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
                currentValue = (fieldType == FormFieldType.Checkbox || fieldType == FormFieldType.RadioButton)
                    ? ReadCheckboxState(widgetDict)
                    : ReadFieldValue(widgetDict);
                isReadOnly = ReadIsReadOnly(widgetDict);

                // Read dropdown options from orphan widget /Opt array
                if (fieldType == FormFieldType.Dropdown)
                {
                    var optArray = widgetDict.GetAsArray(PdfName.Opt);
                    if (optArray != null)
                    {
                        options = new List<string>();
                        for (int i = 0; i < optArray.Size(); i++)
                        {
                            var opt = optArray.Get(i);
                            if (opt is PdfString pdfStr)
                                options.Add(pdfStr.ToUnicodeString());
                            else if (opt is PdfArray optArr && optArr.Size() > 1)
                                options.Add(((PdfString)optArr.Get(1)).ToUnicodeString());
                            else
                                options.Add(opt.ToString() ?? "");
                        }
                    }
                }
            }

            var llx = rect.GetAsNumber(0)?.FloatValue() ?? 0;
            var lly = rect.GetAsNumber(1)?.FloatValue() ?? 0;
            var urx = rect.GetAsNumber(2)?.FloatValue() ?? 0;
            var ury = rect.GetAsNumber(3)?.FloatValue() ?? 0;

            var x = Math.Min(llx, urx);
            var y = Math.Min(lly, ury);
            var width = Math.Abs(urx - llx);
            var height = Math.Abs(ury - lly);

            // For checkbox/radio: find the "on" appearance name from /AP /N
            string? onAppearanceName = null;
            if (fieldType == FormFieldType.Checkbox || fieldType == FormFieldType.RadioButton)
            {
                var ap = widgetDict.GetAsDictionary(PdfName.AP);
                var normal = ap?.GetAsDictionary(PdfName.N);
                if (normal != null)
                {
                    foreach (var key in normal.KeySet())
                    {
                        if (key.GetValue() != "Off")
                        {
                            onAppearanceName = key.GetValue();
                            break;
                        }
                    }
                }
            }

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
                OnAppearanceName = onAppearanceName,
            });
        }

        return result;
    }

    public List<FormField> ExtractAllFormFields(string filePath)
    {
        // Get page count first
        int pageCount;
        using (var r = new PdfReader(filePath))
        using (var doc = new PdfDocument(r))
            pageCount = doc.GetNumberOfPages();

        var result = new List<FormField>();
        for (int i = 0; i < pageCount; i++)
            result.AddRange(ExtractFormFields(filePath, i));
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

    public void CreateFormField(string filePath, CreateFormFieldParams p)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        using var inputStream = new MemoryStream(fileBytes);
        using var reader = new PdfReader(inputStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        var pageNumber = p.PageIndex + 1;
        var page = pdfDoc.GetPage(pageNumber);
        var form = PdfFormCreator.GetAcroForm(pdfDoc, true);
        var rect = new Rectangle(p.X, p.Y, p.Width, p.Height);
        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

        switch (p.FieldTool)
        {
            case InsertionTool.FormTextField:
                var textField = new TextFormFieldBuilder(pdfDoc, p.FieldName)
                    .SetPage(pageNumber)
                    .SetWidgetRectangle(rect)
                    .SetFont(font)
                    .CreateText();
                if (!string.IsNullOrEmpty(p.DefaultValue))
                    textField.SetValue(p.DefaultValue);
                form.AddField(textField, page);
                break;

            case InsertionTool.FormCheckbox:
                var checkboxField = new CheckBoxFormFieldBuilder(pdfDoc, p.FieldName)
                    .SetPage(pageNumber)
                    .SetWidgetRectangle(rect)
                    .CreateCheckBox();
                form.AddField(checkboxField, page);
                break;

            case InsertionTool.FormRadioButton:
                var groupName = p.RadioGroupName ?? p.FieldName;
                // Check if radio group already exists
                var allFields = form.GetAllFormFields();
                PdfButtonFormField? radioGroup = null;
                if (allFields.TryGetValue(groupName, out var existing) && existing is PdfButtonFormField btn && btn.IsRadio())
                {
                    radioGroup = btn;
                }
                if (radioGroup == null)
                {
                    radioGroup = new RadioFormFieldBuilder(pdfDoc, groupName)
                        .CreateRadioGroup();
                }
                var radioAnnot = new RadioFormFieldBuilder(pdfDoc, groupName)
                    .CreateRadioButton(p.DefaultValue ?? "Option1", rect);
                radioGroup.AddKid(radioAnnot);
                if (!allFields.ContainsKey(groupName))
                    form.AddField(radioGroup, page);
                break;

            case InsertionTool.FormDropdown:
                var options = p.Options?.ToArray() ?? new[] { "Option 1", "Option 2", "Option 3" };
                var choiceField = new ChoiceFormFieldBuilder(pdfDoc, p.FieldName)
                    .SetPage(pageNumber)
                    .SetWidgetRectangle(rect)
                    .SetOptions(options)
                    .SetFont(font)
                    .CreateComboBox();
                if (!string.IsNullOrEmpty(p.DefaultValue))
                    choiceField.SetValue(p.DefaultValue);
                form.AddField(choiceField, page);
                break;

            case InsertionTool.FormImage:
                // Push button with image icon
                var imageBtn = new PushButtonFormFieldBuilder(pdfDoc, p.FieldName)
                    .SetPage(pageNumber)
                    .SetWidgetRectangle(rect)
                    .SetCaption("")
                    .CreatePushButton();
                if (!string.IsNullOrEmpty(p.ImageFilePath))
                {
                    imageBtn.SetImage(p.ImageFilePath);
                    // Store image path for later resize/regeneration
                    imageBtn.GetPdfObject().Put(new PdfName("ImagePath"), new PdfString(p.ImageFilePath));
                }
                form.AddField(imageBtn, page);
                break;

            case InsertionTool.FormDate:
                // Text field with date format
                var dateField = new TextFormFieldBuilder(pdfDoc, p.FieldName)
                    .SetPage(pageNumber)
                    .SetWidgetRectangle(rect)
                    .SetFont(font)
                    .CreateText();
                if (!string.IsNullOrEmpty(p.DefaultValue))
                    dateField.SetValue(p.DefaultValue);
                // Set JavaScript format action for date display (dd/MM/yyyy)
                var jsFormat = new PdfString("AFDate_FormatEx(\"dd/mm/yyyy\");");
                var jsKeystroke = new PdfString("AFDate_KeystrokeEx(\"dd/mm/yyyy\");");
                var formatAction = new PdfDictionary();
                formatAction.Put(PdfName.S, PdfName.JavaScript);
                formatAction.Put(new PdfName("JS"), jsFormat);
                var keystrokeAction = new PdfDictionary();
                keystrokeAction.Put(PdfName.S, PdfName.JavaScript);
                keystrokeAction.Put(new PdfName("JS"), jsKeystroke);
                var aa = new PdfDictionary();
                aa.Put(new PdfName("F"), formatAction);
                aa.Put(new PdfName("K"), keystrokeAction);
                dateField.GetPdfObject().Put(new PdfName("AA"), aa);
                form.AddField(dateField, page);
                break;
        }

        pdfDoc.Close();
    }

    public void UpdateFormFieldProperties(string filePath, FormFieldProperties props)
    {
        var fileBytes = File.ReadAllBytes(filePath);
        using var inputStream = new MemoryStream(fileBytes);
        using var reader = new PdfReader(inputStream);
        using var writer = new PdfWriter(filePath);
        using var pdfDoc = new PdfDocument(reader, writer);

        var form = PdfFormCreator.GetAcroForm(pdfDoc, false);

        // Try AcroForm high-level field first
        if (form != null)
        {
            var fields = form.GetAllFormFields();
            if (fields.TryGetValue(props.OriginalFieldName, out var field))
            {
                // Rename
                if (!string.IsNullOrEmpty(props.NewFieldName) && props.NewFieldName != props.OriginalFieldName)
                    field.SetFieldName(props.NewFieldName);

                // Font size — set via default appearance string
                if (props.FontSize.HasValue)
                {
                    var da = $"/Helv {props.FontSize.Value:F1} Tf 0 g";
                    field.GetPdfObject().Put(PdfName.DA, new PdfString(da));
                }

                // Options for choice fields
                if (props.Options != null && field is PdfChoiceFormField choiceField)
                {
                    var optArray = new iText.Kernel.Pdf.PdfArray();
                    foreach (var opt in props.Options)
                        optArray.Add(new PdfString(opt));
                    choiceField.GetPdfObject().Put(PdfName.Opt, optArray);
                }

                // Resize + reposition widget
                UpdateWidgetRect(field, props);

                field.GetPdfObject().SetModified();

                // PushButtons (images): re-apply image so it fills the new rect
                if (field is PdfButtonFormField btn && btn.IsPushButton())
                {
                    var imgPath = field.GetPdfObject().GetAsString(new PdfName("ImagePath"));
                    if (imgPath != null && File.Exists(imgPath.ToUnicodeString()))
                    {
                        btn.SetImage(imgPath.ToUnicodeString());
                        btn.RegenerateField();
                    }
                    // else: keep existing appearance, just update BBox
                    else
                    {
                        UpdatePushButtonAppearance(field, props);
                    }
                }
                else
                {
                    field.RegenerateField();
                }

                pdfDoc.Close();
                return;
            }
        }

        // Fallback: orphan widget
        for (int p = 1; p <= pdfDoc.GetNumberOfPages(); p++)
        {
            var page = pdfDoc.GetPage(p);
            foreach (var annot in page.GetAnnotations())
            {
                if (annot is not PdfWidgetAnnotation widget) continue;
                var dict = widget.GetPdfObject();
                var name = ReadFieldName(dict);
                if (name != props.OriginalFieldName) continue;

                // Rename
                if (!string.IsNullOrEmpty(props.NewFieldName) && props.NewFieldName != props.OriginalFieldName)
                    dict.Put(PdfName.T, new PdfString(props.NewFieldName));

                // Font size
                if (props.FontSize.HasValue)
                {
                    var da = $"/Helv {props.FontSize.Value:F1} Tf 0 g";
                    dict.Put(PdfName.DA, new PdfString(da));
                }

                // Options
                if (props.Options != null)
                {
                    var optArray = new iText.Kernel.Pdf.PdfArray();
                    foreach (var opt in props.Options)
                        optArray.Add(new PdfString(opt));
                    dict.Put(PdfName.Opt, optArray);
                }

                // Rect
                if (props.X.HasValue || props.Y.HasValue || props.Width.HasValue || props.Height.HasValue)
                {
                    var oldRect = dict.GetAsArray(PdfName.Rect);
                    var llx = oldRect?.GetAsNumber(0)?.FloatValue() ?? 0;
                    var lly = oldRect?.GetAsNumber(1)?.FloatValue() ?? 0;
                    var urx = oldRect?.GetAsNumber(2)?.FloatValue() ?? 0;
                    var ury = oldRect?.GetAsNumber(3)?.FloatValue() ?? 0;

                    var x = props.X ?? Math.Min(llx, urx);
                    var y = props.Y ?? Math.Min(lly, ury);
                    var w = props.Width ?? Math.Abs(urx - llx);
                    var h = props.Height ?? Math.Abs(ury - lly);

                    var newRect = new iText.Kernel.Pdf.PdfArray(new float[] { x, y, x + w, y + h });
                    dict.Put(PdfName.Rect, newRect);
                }

                // Remove cached appearance to regenerate (except push buttons with images)
                var ftOrphan = dict.GetAsName(PdfName.FT)
                    ?? dict.GetAsDictionary(PdfName.Parent)?.GetAsName(PdfName.FT);
                var ffOrphan = dict.GetAsNumber(PdfName.Ff)?.IntValue() ?? 0;
                var isPushBtn = PdfName.Btn.Equals(ftOrphan) && (ffOrphan & (1 << 16)) != 0;
                if (isPushBtn)
                    UpdateOrphanPushButtonAppearance(dict, props);
                else
                    dict.Remove(PdfName.AP);
                dict.SetModified();
                pdfDoc.Close();
                return;
            }
        }

        pdfDoc.Close();
    }

    private static void UpdateWidgetRect(PdfFormField field, FormFieldProperties props)
    {
        if (!props.X.HasValue && !props.Y.HasValue && !props.Width.HasValue && !props.Height.HasValue)
            return;

        var widgets = field.GetWidgets();
        if (widgets == null || widgets.Count == 0) return;

        var widget = widgets[0];
        var oldRect = widget.GetRectangle();
        if (oldRect == null) return;

        var llx = oldRect.GetAsNumber(0)?.FloatValue() ?? 0;
        var lly = oldRect.GetAsNumber(1)?.FloatValue() ?? 0;
        var urx = oldRect.GetAsNumber(2)?.FloatValue() ?? 0;
        var ury = oldRect.GetAsNumber(3)?.FloatValue() ?? 0;

        var x = props.X ?? Math.Min(llx, urx);
        var y = props.Y ?? Math.Min(lly, ury);
        var w = props.Width ?? Math.Abs(urx - llx);
        var h = props.Height ?? Math.Abs(ury - lly);

        var newRect = new Rectangle(x, y, w, h);
        widget.SetRectangle(new iText.Kernel.Pdf.PdfArray(new float[] { x, y, x + w, y + h }));
    }

    /// <summary>
    /// Updates the BBox of the appearance stream for a PushButton (AcroForm field)
    /// so the image scales to the new widget rectangle.
    /// </summary>
    private static void UpdatePushButtonAppearance(PdfFormField field, FormFieldProperties props)
    {
        var widgets = field.GetWidgets();
        if (widgets == null || widgets.Count == 0) return;

        var widget = widgets[0];
        var rect = widget.GetRectangle();
        if (rect == null) return;

        var w = props.Width ?? (rect.GetAsNumber(2).FloatValue() - rect.GetAsNumber(0).FloatValue());
        var h = props.Height ?? (rect.GetAsNumber(3).FloatValue() - rect.GetAsNumber(1).FloatValue());

        UpdateApBBox(widget.GetPdfObject(), Math.Abs(w), Math.Abs(h));
    }

    /// <summary>
    /// Updates the BBox of the appearance stream for an orphan PushButton widget.
    /// </summary>
    private static void UpdateOrphanPushButtonAppearance(PdfDictionary dict, FormFieldProperties props)
    {
        var rect = dict.GetAsArray(PdfName.Rect);
        if (rect == null) return;

        var w = props.Width ?? Math.Abs(rect.GetAsNumber(2).FloatValue() - rect.GetAsNumber(0).FloatValue());
        var h = props.Height ?? Math.Abs(rect.GetAsNumber(3).FloatValue() - rect.GetAsNumber(1).FloatValue());

        UpdateApBBox(dict, Math.Abs(w), Math.Abs(h));
    }

    private static void UpdateApBBox(PdfDictionary widgetDict, float w, float h)
    {
        var ap = widgetDict.GetAsDictionary(PdfName.AP);
        if (ap == null) return;

        // Update BBox in /N (normal appearance)
        var normal = ap.Get(PdfName.N);
        if (normal is PdfStream stream)
        {
            stream.Put(new PdfName("BBox"), new PdfArray(new float[] { 0, 0, w, h }));
            stream.SetModified();
        }
        else if (normal is PdfDictionary normalDict)
        {
            // Could be a dictionary of appearance states
            foreach (var key in normalDict.KeySet())
            {
                var val = normalDict.Get(key);
                if (val is PdfStream stateStream)
                {
                    stateStream.Put(new PdfName("BBox"), new PdfArray(new float[] { 0, 0, w, h }));
                    stateStream.SetModified();
                }
            }
        }
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
            var ff = dict.GetAsNumber(PdfName.Ff);
            var flags = ff?.IntValue() ?? 0;
            if ((flags & (1 << 15)) != 0) // bit 16 = Radio
                return FormFieldType.RadioButton;
            if ((flags & (1 << 16)) != 0) // bit 17 = PushButton
                return FormFieldType.PushButton;
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
            return asObj.GetValue(); // Return actual appearance name (e.g. "Yes", "Option1")
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
            if (buttonField.IsPushButton())
                return FormFieldType.PushButton;
            return FormFieldType.Checkbox;
        }
        if (field is PdfSignatureFormField)
            return FormFieldType.Signature;

        return FormFieldType.Unknown;
    }
}
