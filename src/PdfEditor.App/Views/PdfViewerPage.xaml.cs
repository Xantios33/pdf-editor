using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PdfEditor.App.Helpers;
using PdfEditor.App.ViewModels;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using PdfEditor.Core.Models;
using PdfTextBlock = PdfEditor.Core.Models.TextBlock;

namespace PdfEditor.App.Views;

public sealed partial class PdfViewerPage : UserControl
{
    private const int RenderDpi = 300;
    private const double PdfBaseDpi = 96.0;

    private readonly MainViewModel _viewModel;
    private double _zoomPercent = 100.0;
    private int _bitmapWidth;
    private int _bitmapHeight;

    // Edit mode state
    private bool _isEditMode;
    private PdfTextBlock? _editingBlock;
    private List<PdfTextBlock>? _textBlocks;
    private readonly List<Border> _overlayBorders = new();
    private TextBox? _activeTextBox;
    private bool _preserveScrollOnRender;

    // Drag state
    private bool _isDragging;
    private Border? _dragBorder;
    private PdfTextBlock? _dragBlock;
    private Point _dragStartPoint;
    private double _dragOriginalLeft, _dragOriginalTop;
    private const double DragThreshold = 5.0;

    // Form field overlays
    private readonly List<FrameworkElement> _formFieldOverlays = new();

    // Format toolbar state — suppress change events during sync
    private bool _syncingToolbar;

    // Predefined colors for the color picker
    private static readonly (string Name, byte R, byte G, byte B)[] PredefinedColors =
    {
        ("Noir", 0, 0, 0),
        ("Blanc", 255, 255, 255),
        ("Rouge", 255, 0, 0),
        ("Vert", 0, 128, 0),
        ("Bleu", 0, 0, 255),
        ("Jaune", 255, 255, 0),
        ("Orange", 255, 165, 0),
        ("Violet", 128, 0, 128),
        ("Gris", 128, 128, 128),
        ("Marron", 139, 69, 19),
    };

    public PdfViewerPage()
    {
        this.InitializeComponent();
        _viewModel = App.Services.GetRequiredService<MainViewModel>();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        PageScrollViewer.AddHandler(
            PointerWheelChangedEvent,
            new PointerEventHandler(OnPointerWheelChanged),
            handledEventsToo: true);

        InitializeColorGrid();
    }

    private void InitializeColorGrid()
    {
        foreach (var (name, r, g, b) in PredefinedColors)
        {
            var swatch = new Border
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Gray),
                Tag = (r, g, b),
            };
            ToolTipService.SetToolTip(swatch, name);
            ColorGrid.Items.Add(swatch);
        }
    }

    private double GetLogicalWidth(double zoom) => _bitmapWidth * (PdfBaseDpi / RenderDpi) * (zoom / 100.0);
    private double GetLogicalHeight(double zoom) => _bitmapHeight * (PdfBaseDpi / RenderDpi) * (zoom / 100.0);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.CurrentPageBitmap):
                if (_viewModel.CurrentPageBitmap != null)
                {
                    var bitmap = _viewModel.CurrentPageBitmap;
                    _bitmapWidth = bitmap.Width;
                    _bitmapHeight = bitmap.Height;

                    PageImage.Source = BitmapHelper.ToWriteableBitmap(bitmap);
                    EmptyStateText.Visibility = Visibility.Collapsed;

                    if (_viewModel.CurrentPageIndex == 0)
                        _zoomPercent = 100.0;

                    ApplyImageSize();

                    if (_preserveScrollOnRender)
                    {
                        _preserveScrollOnRender = false;
                        // Just re-center horizontally, keep vertical position
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            var hOffset = Math.Max(0, (PageScrollViewer.ExtentWidth - PageScrollViewer.ViewportWidth) / 2);
                            PageScrollViewer.ChangeView(hOffset, null, null, disableAnimation: true);
                        });
                    }
                    else
                    {
                        CenterScrollAfterLayout();
                    }

                    // If in edit mode, re-extract and show overlays for the new page
                    if (_isEditMode)
                        _ = ShowTextBlockOverlaysAsync();
                    else
                        _ = ShowFormFieldOverlaysAsync();
                }
                break;

            case nameof(MainViewModel.StatusText):
                StatusTextBlock.Text = _viewModel.StatusText;
                break;

            case nameof(MainViewModel.PageIndicator):
                PageIndicatorText.Text = _viewModel.PageIndicator;
                break;

            case nameof(MainViewModel.IsLoading):
                LoadingRing.IsActive = _viewModel.IsLoading;
                break;

            case nameof(MainViewModel.HasDocument):
                SaveButton.IsEnabled = _viewModel.HasDocument;
                EditModeButton.IsEnabled = _viewModel.HasDocument;
                UpdateNavigationButtons();
                break;

            case nameof(MainViewModel.CanUndo):
                UndoButton.IsEnabled = _viewModel.CanUndo;
                break;

            case nameof(MainViewModel.CanRedo):
                RedoButton.IsEnabled = _viewModel.CanRedo;
                break;

            case nameof(MainViewModel.CurrentPageIndex):
            case nameof(MainViewModel.PageCount):
                UpdateNavigationButtons();
                break;
        }
    }

    private void ApplyImageSize()
    {
        var w = GetLogicalWidth(_zoomPercent);
        var h = GetLogicalHeight(_zoomPercent);
        PageImage.Width = w;
        PageImage.Height = h;
        PageCanvas.Width = w;
        PageCanvas.Height = h;

        // Center the loading ring
        Canvas.SetLeft(LoadingRing, (w - 48) / 2);
        Canvas.SetTop(LoadingRing, (h - 48) / 2);

        _viewModel.StatusText = $"Zoom: {_zoomPercent:F0}%";

        // Reposition overlays when size changes
        if (_isEditMode && _textBlocks != null)
            RepositionOverlays();

        if (!_isEditMode && _formFieldOverlays.Count > 0)
            RepositionFormFieldOverlays();
    }

    private void CenterScrollAfterLayout()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var hOffset = Math.Max(0, (PageScrollViewer.ExtentWidth - PageScrollViewer.ViewportWidth) / 2);
            PageScrollViewer.ChangeView(hOffset, 0, null, disableAnimation: true);
        });
    }

    private void UpdateNavigationButtons()
    {
        PrevButton.IsEnabled = _viewModel.PrevPageCommand.CanExecute(null);
        NextButton.IsEnabled = _viewModel.NextPageCommand.CanExecute(null);
    }

    // ---- Coordinate conversion helpers ----

    private (double x, double y, double width, double height) PdfToScreen(PdfTextBlock block)
    {
        var imageW = PageImage.Width;
        var imageH = PageImage.Height;

        var screenX = block.X / 72.0 * RenderDpi / _bitmapWidth * imageW;
        var pdfTop = block.Y + block.Height;
        var pageHeightPdf = _bitmapHeight / (double)RenderDpi * 72.0;
        var screenY = (1.0 - pdfTop / pageHeightPdf) * imageH;
        var screenW = block.Width / 72.0 * RenderDpi / _bitmapWidth * imageW;
        var screenH = block.Height / 72.0 * RenderDpi / _bitmapHeight * imageH;

        return (screenX, screenY, screenW, screenH);
    }

    // ---- Edit mode ----

    private async void OnEditModeClick(object sender, RoutedEventArgs e)
    {
        _isEditMode = !_isEditMode;

        if (_isEditMode)
        {
            EditModeButton.Label = "Éditer (ON)";
            ClearFormFieldOverlays();
            await ShowTextBlockOverlaysAsync();
        }
        else
        {
            EditModeButton.Label = "Éditer";
            ClearTextBlockOverlays();
            HideFormatToolbar();
            _textBlocks = null;
            await ShowFormFieldOverlaysAsync();
        }
    }

    private async Task ShowTextBlockOverlaysAsync()
    {
        ClearTextBlockOverlays();

        await _viewModel.ExtractTextBlocksAsync();
        _textBlocks = _viewModel.CurrentTextBlocks;

        if (_textBlocks == null || _textBlocks.Count == 0 || _bitmapWidth == 0)
            return;

        foreach (var block in _textBlocks)
        {
            var (sx, sy, sw, sh) = PdfToScreen(block);

            var border = new Border
            {
                Width = Math.Max(sw, 4),
                Height = Math.Max(sh, 4),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.DodgerBlue),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, 30, 144, 255)),
                Tag = block,
                CornerRadius = new CornerRadius(2),
            };

            border.PointerPressed += OnBlockPointerPressed;
            border.PointerMoved += OnBlockPointerMoved;
            border.PointerReleased += OnBlockPointerReleased;
            border.PointerEntered += OnBlockPointerEntered;
            border.PointerExited += OnBlockPointerExited;

            Canvas.SetLeft(border, sx);
            Canvas.SetTop(border, sy);
            PageCanvas.Children.Add(border);
            _overlayBorders.Add(border);
        }
    }

    private void ClearTextBlockOverlays()
    {
        // Remove the active textbox if any
        if (_activeTextBox != null)
        {
            PageCanvas.Children.Remove(_activeTextBox);
            _activeTextBox = null;
            _editingBlock = null;
        }

        foreach (var border in _overlayBorders)
        {
            border.PointerPressed -= OnBlockPointerPressed;
            border.PointerMoved -= OnBlockPointerMoved;
            border.PointerReleased -= OnBlockPointerReleased;
            border.PointerEntered -= OnBlockPointerEntered;
            border.PointerExited -= OnBlockPointerExited;
            PageCanvas.Children.Remove(border);
        }
        _overlayBorders.Clear();
    }

    private void RepositionOverlays()
    {
        if (_textBlocks == null || _bitmapWidth == 0)
            return;

        // Reposition borders
        foreach (var border in _overlayBorders)
        {
            if (border.Tag is PdfTextBlock block)
            {
                var (sx, sy, sw, sh) = PdfToScreen(block);
                Canvas.SetLeft(border, sx);
                Canvas.SetTop(border, sy);
                border.Width = Math.Max(sw, 4);
                border.Height = Math.Max(sh, 4);
            }
        }

        // Reposition active textbox
        if (_activeTextBox != null && _editingBlock != null)
        {
            var (sx, sy, sw, sh) = PdfToScreen(_editingBlock);
            Canvas.SetLeft(_activeTextBox, sx);
            Canvas.SetTop(_activeTextBox, sy);
            _activeTextBox.Width = Math.Max(sw + 20, 80);
            _activeTextBox.Height = Math.Max(sh + 10, 30);
            _activeTextBox.FontSize = Math.Max(10, sh * 0.7);
        }
    }

    // ---- Drag & drop pointer events ----

    private void OnBlockPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
    }

    private void OnBlockPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging)
            ProtectedCursor = null;
    }

    private void OnBlockPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not PdfTextBlock block)
            return;

        border.CapturePointer(e.Pointer);
        _dragBorder = border;
        _dragBlock = block;
        _dragStartPoint = e.GetCurrentPoint(PageCanvas).Position;
        _dragOriginalLeft = Canvas.GetLeft(border);
        _dragOriginalTop = Canvas.GetTop(border);
        _isDragging = false;

        e.Handled = true;
    }

    private void OnBlockPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragBorder == null || _dragBlock == null)
            return;

        var currentPos = e.GetCurrentPoint(PageCanvas).Position;
        var dx = currentPos.X - _dragStartPoint.X;
        var dy = currentPos.Y - _dragStartPoint.Y;

        if (!_isDragging)
        {
            if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
                return;
            _isDragging = true;
            _dragBorder.BorderBrush = new SolidColorBrush(Colors.Orange);
            _dragBorder.Opacity = 0.7;
        }

        Canvas.SetLeft(_dragBorder, _dragOriginalLeft + dx);
        Canvas.SetTop(_dragBorder, _dragOriginalTop + dy);

        e.Handled = true;
    }

    private async void OnBlockPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragBorder == null || _dragBlock == null)
            return;

        _dragBorder.ReleasePointerCapture(e.Pointer);
        var wasDragging = _isDragging;
        var border = _dragBorder;
        var block = _dragBlock;

        _dragBorder = null;
        _dragBlock = null;
        _isDragging = false;
        ProtectedCursor = null;

        if (!wasDragging)
        {
            // Short click — open inline editor (same as old OnBlockTapped)
            OpenInlineEditor(border, block);
            e.Handled = true;
            return;
        }

        // Drag completed — convert new screen position to PDF coords
        var newScreenX = Canvas.GetLeft(border);
        var newScreenY = Canvas.GetTop(border);
        var (pdfX, pdfY) = ScreenToPdf(newScreenX, newScreenY, border.Width, border.Height);

        // Reset border visual before re-render
        border.BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
        border.Opacity = 1.0;

        _preserveScrollOnRender = true;
        await _viewModel.MoveTextBlockAsync(block, (float)pdfX, (float)pdfY);

        if (_isEditMode)
            await ShowTextBlockOverlaysAsync();

        e.Handled = true;
    }

    private (double pdfX, double pdfY) ScreenToPdf(double screenX, double screenY, double screenW, double screenH)
    {
        var imageW = PageImage.Width;
        var imageH = PageImage.Height;
        var pageHeightPdf = _bitmapHeight / (double)RenderDpi * 72.0;

        // Inverse of PdfToScreen
        var pdfX = screenX / imageW * _bitmapWidth / (double)RenderDpi * 72.0;
        var pdfTop = (1.0 - screenY / imageH) * pageHeightPdf;
        var pdfH = screenH / imageH * pageHeightPdf;
        var pdfY = pdfTop - pdfH;

        return (pdfX, pdfY);
    }

    private void OpenInlineEditor(Border tappedBorder, PdfTextBlock block)
    {
        // If already editing another block, cancel that edit first
        if (_activeTextBox != null && _editingBlock != null)
        {
            _activeTextBox.LostFocus -= OnInlineTextBoxLostFocus;
            CancelEdit();
        }

        _editingBlock = block;

        var (sx, sy, sw, sh) = PdfToScreen(block);

        // Hide the tapped border
        tappedBorder.Visibility = Visibility.Collapsed;

        // Create an inline TextBox at the same position — transparent to blend with PDF
        var textBox = new TextBox
        {
            Text = block.Text,
            FontSize = Math.Max(10, sh * 0.7),
            Width = Math.Max(sw + 20, 80),
            Height = Math.Max(sh + 10, 30),
            AcceptsReturn = false,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Colors.DodgerBlue),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255)),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, block.ColorR, block.ColorG, block.ColorB)),
            FontWeight = block.IsBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = block.IsItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            Padding = new Thickness(1, 0, 1, 0),
            Tag = tappedBorder, // Keep reference to the border we replaced
        };

        textBox.KeyDown += OnInlineTextBoxKeyDown;
        textBox.LostFocus += OnInlineTextBoxLostFocus;

        Canvas.SetLeft(textBox, sx);
        Canvas.SetTop(textBox, sy);
        PageCanvas.Children.Add(textBox);

        _activeTextBox = textBox;

        // Show the format toolbar with this block's properties
        ShowFormatToolbar(block);

        textBox.Focus(FocusState.Programmatic);
        textBox.SelectAll();
    }

    private async void OnInlineTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            await ApplyEditAsync();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
    }

    private void OnInlineTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // Don't cancel if focus moved to a format toolbar control
        if (FocusManager.GetFocusedElement(XamlRoot) is FrameworkElement focused)
        {
            if (IsChildOf(focused, FormatToolbar))
                return;
        }

        // Click outside = cancel without saving
        if (_editingBlock != null && _activeTextBox != null)
        {
            CancelEdit();
        }
    }

    private static bool IsChildOf(DependencyObject child, DependencyObject parent)
    {
        var current = child;
        while (current != null)
        {
            if (current == parent)
                return true;
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void CancelEdit()
    {
        if (_activeTextBox == null) return;

        // Restore the hidden border
        if (_activeTextBox.Tag is Border hiddenBorder)
            hiddenBorder.Visibility = Visibility.Visible;

        _activeTextBox.KeyDown -= OnInlineTextBoxKeyDown;
        _activeTextBox.LostFocus -= OnInlineTextBoxLostFocus;
        PageCanvas.Children.Remove(_activeTextBox);
        _activeTextBox = null;
        _editingBlock = null;

        HideFormatToolbar();
    }

    private async Task ApplyEditAsync()
    {
        if (_editingBlock == null || _activeTextBox == null) return;

        var newText = _activeTextBox.Text;
        var block = _editingBlock;

        // Read format toolbar values into the block before replacing
        SyncBlockFromToolbar(block);

        if (newText == block.Text && !HasFormatChanged(block))
        {
            // No change — just cancel
            CancelEdit();
            return;
        }

        // Keep the TextBox visible during the re-render so there's no flash.
        // Detach events to prevent re-entry, but leave it on the Canvas.
        var keepVisible = _activeTextBox;
        keepVisible.KeyDown -= OnInlineTextBoxKeyDown;
        keepVisible.LostFocus -= OnInlineTextBoxLostFocus;
        keepVisible.IsReadOnly = true;
        keepVisible.Opacity = 0.6;
        _activeTextBox = null;
        _editingBlock = null;

        HideFormatToolbar();

        // Apply the PDF modification + re-render in the background
        _preserveScrollOnRender = true;
        await _viewModel.ReplaceTextAsync(block, newText);

        // Now the bitmap is updated behind the TextBox — remove it
        PageCanvas.Children.Remove(keepVisible);

        // Re-extract and re-show overlays on the fresh render
        if (_isEditMode)
            await ShowTextBlockOverlaysAsync();
    }

    private bool HasFormatChanged(PdfTextBlock block)
    {
        // If the block was modified by SyncBlockFromToolbar, the replacement
        // should still proceed even if text is unchanged.
        // This is a simplification — always replace when format toolbar is used.
        return true;
    }

    // ---- Format toolbar ----

    private void ShowFormatToolbar(PdfTextBlock block)
    {
        _syncingToolbar = true;

        // Font family
        var family = DetectFontFamilyIndex(block.FontName);
        FontFamilyCombo.SelectedIndex = family;

        // Font size
        FontSizeBox.Value = Math.Round(block.FontSize, 1);

        // Bold / Italic / Underline
        BoldToggle.IsChecked = block.IsBold;
        ItalicToggle.IsChecked = block.IsItalic;
        UnderlineToggle.IsChecked = block.IsUnderline;

        // Color button foreground indicator
        UpdateColorButtonIndicator(block.ColorR, block.ColorG, block.ColorB);

        FormatToolbar.Visibility = Visibility.Visible;
        _syncingToolbar = false;
    }

    private void HideFormatToolbar()
    {
        FormatToolbar.Visibility = Visibility.Collapsed;
    }

    private void SyncBlockFromToolbar(PdfTextBlock block)
    {
        // Font family — update FontName to match selection
        block.FontName = FontFamilyCombo.SelectedIndex switch
        {
            1 => "Times",
            2 => "Courier",
            _ => "Helvetica"
        };

        // Font size
        if (!double.IsNaN(FontSizeBox.Value) && FontSizeBox.Value >= 6)
            block.FontSize = (float)FontSizeBox.Value;

        // Bold / Italic / Underline
        block.IsBold = BoldToggle.IsChecked == true;
        block.IsItalic = ItalicToggle.IsChecked == true;
        block.IsUnderline = UnderlineToggle.IsChecked == true;
    }

    private static int DetectFontFamilyIndex(string? fontName)
    {
        if (string.IsNullOrEmpty(fontName))
            return 0; // Helvetica

        var upper = fontName.ToUpperInvariant();
        if (upper.Contains("TIMES") || upper.Contains("SERIF") || upper.Contains("ROMAN"))
            return 1;
        if (upper.Contains("COURIER") || upper.Contains("MONO") || upper.Contains("CONSOL"))
            return 2;
        return 0; // Helvetica
    }

    private void UpdateColorButtonIndicator(byte r, byte g, byte b)
    {
        ColorButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }

    // Format toolbar event handlers

    private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar || _editingBlock == null) return;
        // Preview: no direct visual effect on TextBox font (standard PDF fonts don't map to system fonts)
    }

    private void OnFontSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingToolbar || _activeTextBox == null || double.IsNaN(args.NewValue)) return;
        // No direct preview — PDF font size doesn't map 1:1 to screen TextBox size
    }

    private void OnBoldToggleClick(object sender, RoutedEventArgs e)
    {
        if (_syncingToolbar || _activeTextBox == null) return;
        _activeTextBox.FontWeight = BoldToggle.IsChecked == true
            ? Microsoft.UI.Text.FontWeights.Bold
            : Microsoft.UI.Text.FontWeights.Normal;
    }

    private void OnItalicToggleClick(object sender, RoutedEventArgs e)
    {
        if (_syncingToolbar || _activeTextBox == null) return;
        _activeTextBox.FontStyle = ItalicToggle.IsChecked == true
            ? Windows.UI.Text.FontStyle.Italic
            : Windows.UI.Text.FontStyle.Normal;
    }

    private void OnUnderlineToggleClick(object sender, RoutedEventArgs e)
    {
        // TextBox doesn't support TextDecorations directly — no preview for underline
    }

    private void OnColorSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingToolbar || _editingBlock == null || _activeTextBox == null) return;

        if (ColorGrid.SelectedItem is Border swatch && swatch.Tag is (byte r, byte g, byte b))
        {
            _editingBlock.ColorR = r;
            _editingBlock.ColorG = g;
            _editingBlock.ColorB = b;

            // Preview in TextBox
            _activeTextBox.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
            UpdateColorButtonIndicator(r, g, b);

            ColorFlyout.Hide();
        }
    }

    // ---- Form field overlays ----

    private (double x, double y, double width, double height) FormFieldToScreen(FormField field)
    {
        var imageW = PageImage.Width;
        var imageH = PageImage.Height;

        var screenX = field.X / 72.0 * RenderDpi / _bitmapWidth * imageW;
        var pdfTop = field.Y + field.Height;
        var pageHeightPdf = _bitmapHeight / (double)RenderDpi * 72.0;
        var screenY = (1.0 - pdfTop / pageHeightPdf) * imageH;
        var screenW = field.Width / 72.0 * RenderDpi / _bitmapWidth * imageW;
        var screenH = field.Height / 72.0 * RenderDpi / _bitmapHeight * imageH;

        return (screenX, screenY, screenW, screenH);
    }

    private async Task ShowFormFieldOverlaysAsync()
    {
        ClearFormFieldOverlays();

        if (_bitmapWidth == 0 || !_viewModel.HasDocument)
            return;

        await _viewModel.ExtractFormFieldsAsync();
        var fields = _viewModel.CurrentFormFields;

        if (fields == null || fields.Count == 0)
            return;

        foreach (var field in fields)
        {
            var (sx, sy, sw, sh) = FormFieldToScreen(field);
            var overlay = CreateFormFieldBorder(field, sw, sh);

            Canvas.SetLeft(overlay, sx);
            Canvas.SetTop(overlay, sy);
            PageCanvas.Children.Add(overlay);
            _formFieldOverlays.Add(overlay);
        }
    }

    // Active form field editor (shown on click of a form overlay border)
    private FrameworkElement? _activeFormEditor;
    private Border? _activeFormBorder;

    private FrameworkElement CreateFormFieldBorder(FormField field, double sw, double sh)
    {
        var isReadOnly = field.IsReadOnly ||
            field.FieldType == FormFieldType.RadioButton ||
            field.FieldType == FormFieldType.Signature ||
            field.FieldType == FormFieldType.Unknown;

        var borderColor = isReadOnly ? Colors.Gray : Colors.Green;
        var bgAlpha = isReadOnly ? (byte)15 : (byte)25;

        var border = new Border
        {
            Width = sw,
            Height = sh,
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(borderColor),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(bgAlpha, borderColor.R, borderColor.G, borderColor.B)),
            CornerRadius = new CornerRadius(1),
            Tag = field,
        };

        var tooltip = field.FieldName;
        if (!string.IsNullOrEmpty(field.CurrentValue))
            tooltip += $": {field.CurrentValue}";
        ToolTipService.SetToolTip(border, tooltip);

        if (!isReadOnly)
        {
            border.PointerPressed += OnFormBorderPressed;
            border.PointerEntered += (s, e) => { if (s is Border b) b.BorderThickness = new Thickness(2); };
            border.PointerExited += (s, e) => { if (s is Border b) b.BorderThickness = new Thickness(1); };
        }

        return border;
    }

    private void OnFormBorderPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not FormField field)
            return;

        e.Handled = true;
        CloseActiveFormEditor();

        var (sx, sy, sw, sh) = FormFieldToScreen(field);

        _activeFormBorder = border;
        border.Visibility = Visibility.Collapsed;

        FrameworkElement editor;

        switch (field.FieldType)
        {
            case FormFieldType.Text:
                var textBox = new TextBox
                {
                    Text = field.CurrentValue ?? "",
                    Width = Math.Max(sw + 16, 80),
                    Height = Math.Max(sh + 10, 28),
                    FontSize = Math.Max(9, sh * 0.7),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = new SolidColorBrush(Colors.Green),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 255, 255)),
                    Padding = new Thickness(2, 0, 2, 0),
                    AcceptsReturn = false,
                    Tag = field,
                };
                textBox.KeyDown += OnFormTextBoxKeyDown;
                textBox.LostFocus += OnFormTextBoxLostFocus;
                editor = textBox;
                // Offset so the editor visually covers the field
                Canvas.SetLeft(editor, sx - 4);
                Canvas.SetTop(editor, sy - (Math.Max(sh + 10, 28) - sh) / 2);
                PageCanvas.Children.Add(editor);
                _activeFormEditor = editor;
                textBox.Focus(FocusState.Programmatic);
                textBox.SelectAll();
                return;

            case FormFieldType.Checkbox:
                // Toggle immediately, no editor needed
                var newVal = (field.CurrentValue == "Yes" || field.CurrentValue == "On") ? "Off" : "Yes";
                border.Visibility = Visibility.Visible;
                _activeFormBorder = null;
                _ = ApplyFormFieldValueAsync(field, newVal);
                return;

            case FormFieldType.Dropdown:
                var comboBox = new ComboBox
                {
                    Width = Math.Max(sw + 16, 80),
                    Height = Math.Max(sh + 10, 32),
                    FontSize = Math.Max(9, sh * 0.6),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = new SolidColorBrush(Colors.Green),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 255, 255)),
                    Tag = field,
                };
                if (field.Options != null)
                {
                    foreach (var option in field.Options)
                        comboBox.Items.Add(option);
                    if (field.CurrentValue != null)
                        comboBox.SelectedItem = field.CurrentValue;
                }
                comboBox.SelectionChanged += OnFormDropdownChanged;
                comboBox.LostFocus += OnFormEditorLostFocus;
                editor = comboBox;
                Canvas.SetLeft(editor, sx - 4);
                Canvas.SetTop(editor, sy - (Math.Max(sh + 10, 32) - sh) / 2);
                PageCanvas.Children.Add(editor);
                _activeFormEditor = editor;
                comboBox.IsDropDownOpen = true;
                return;

            default:
                border.Visibility = Visibility.Visible;
                _activeFormBorder = null;
                return;
        }
    }

    private void CloseActiveFormEditor()
    {
        if (_activeFormEditor != null)
        {
            if (_activeFormEditor is TextBox tb)
            {
                tb.KeyDown -= OnFormTextBoxKeyDown;
                tb.LostFocus -= OnFormTextBoxLostFocus;
            }
            else if (_activeFormEditor is ComboBox combo)
            {
                combo.SelectionChanged -= OnFormDropdownChanged;
                combo.LostFocus -= OnFormEditorLostFocus;
            }

            PageCanvas.Children.Remove(_activeFormEditor);
            _activeFormEditor = null;
        }

        if (_activeFormBorder != null)
        {
            _activeFormBorder.Visibility = Visibility.Visible;
            _activeFormBorder = null;
        }
    }

    private void ClearFormFieldOverlays()
    {
        CloseActiveFormEditor();

        foreach (var overlay in _formFieldOverlays)
        {
            if (overlay is Border border)
                border.PointerPressed -= OnFormBorderPressed;

            PageCanvas.Children.Remove(overlay);
        }
        _formFieldOverlays.Clear();
    }

    private void RepositionFormFieldOverlays()
    {
        foreach (var overlay in _formFieldOverlays)
        {
            var field = overlay.Tag as FormField;
            if (field == null) continue;

            var (sx, sy, sw, sh) = FormFieldToScreen(field);
            Canvas.SetLeft(overlay, sx);
            Canvas.SetTop(overlay, sy);
            overlay.Width = sw;
            overlay.Height = sh;
        }
    }

    private async void OnFormTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox tb && tb.Tag is FormField field)
        {
            e.Handled = true;
            await ApplyFormFieldValueAsync(field, tb.Text);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseActiveFormEditor();
        }
    }

    private async void OnFormTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.Tag is FormField field)
        {
            var newValue = tb.Text;
            if (newValue != (field.CurrentValue ?? ""))
            {
                await ApplyFormFieldValueAsync(field, newValue);
            }
            else
            {
                CloseActiveFormEditor();
            }
        }
    }

    private void OnFormEditorLostFocus(object sender, RoutedEventArgs e)
    {
        CloseActiveFormEditor();
    }

    private async void OnFormDropdownChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.Tag is FormField field && combo.SelectedItem is string selected)
        {
            await ApplyFormFieldValueAsync(field, selected);
        }
    }

    private async Task ApplyFormFieldValueAsync(FormField field, string value)
    {
        CloseActiveFormEditor();
        _preserveScrollOnRender = true;
        await _viewModel.SetFormFieldValueAsync(field.FieldName, value);
        if (!_isEditMode)
            await ShowFormFieldOverlaysAsync();
    }

    // ---- File operations ----

    private async void OnOpenClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".pdf");
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file != null)
        {
            _isEditMode = false;
            EditModeButton.Label = "Éditer";
            ClearTextBlockOverlays();
            ClearFormFieldOverlays();
            HideFormatToolbar();
            _textBlocks = null;
            await _viewModel.OpenDocumentAsync(file.Path);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SaveCommand.CanExecute(null))
            await _viewModel.SaveCommand.ExecuteAsync(null);
    }

    private async void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_editingBlock != null)
            CancelEdit();

        _preserveScrollOnRender = true;
        await _viewModel.UndoAsync();

        if (_isEditMode)
            await ShowTextBlockOverlaysAsync();
    }

    private async void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_editingBlock != null)
            CancelEdit();

        _preserveScrollOnRender = true;
        await _viewModel.RedoAsync();

        if (_isEditMode)
            await ShowTextBlockOverlaysAsync();
    }

    // ---- Navigation ----

    private async void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.PrevPageCommand.CanExecute(null))
            await _viewModel.PrevPageCommand.ExecuteAsync(null);
    }

    private async void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.NextPageCommand.CanExecute(null))
            await _viewModel.NextPageCommand.ExecuteAsync(null);
    }

    // ---- Zoom ----

    private void OnZoomInClick(object sender, RoutedEventArgs e) => SetZoom(_zoomPercent * 1.25);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(_zoomPercent / 1.25);

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var isCtrlPressed = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (isCtrlPressed)
        {
            var delta = e.GetCurrentPoint(PageScrollViewer).Properties.MouseWheelDelta;
            var factor = delta > 0 ? 1.15 : 1.0 / 1.15;
            SetZoom(_zoomPercent * factor);
            e.Handled = true;
        }
    }

    private void SetZoom(double newZoomPercent)
    {
        _zoomPercent = Math.Clamp(newZoomPercent, 10, 500);
        if (_bitmapWidth > 0)
            ApplyImageSize();
    }
}
