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

    // Insertion mode state
    private InsertionTool _activeInsertionTool = InsertionTool.None;
    private bool _isInsertionPanelOpen;
    private bool _isInsertionDragging;
    private Point _insertionDragStart;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _insertionPreview;

    // Form field selection/resize state
    private FormField? _selectedField;
    private Border? _selectedFieldBorder;
    private readonly List<Border> _resizeHandles = new();
    private string? _activeResizeHandle;
    private Point _resizeStart;
    private double _selOrigX, _selOrigY, _selOrigW, _selOrigH;
    private bool _isMovingField;
    private bool _isMovePointerDown;
    private Point _moveStart;
    private double _moveOrigLeft, _moveOrigTop;

    // Grid & snap state
    private const double GridSize = 20.0;
    private const double SnapThreshold = 8.0;
    private bool _isGridVisible;
    private readonly List<Line> _gridLines = new();
    private readonly List<Line> _snapGuides = new();

    // Middle-button pan state
    private bool _isMiddleButtonPanning;
    private Point _panStart;
    private double _panOrigHOffset, _panOrigVOffset;

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

        // Middle-button pan
        PageScrollViewer.PointerPressed += OnScrollViewerPointerPressed;
        PageScrollViewer.PointerMoved += OnScrollViewerPointerMoved;
        PageScrollViewer.PointerReleased += OnScrollViewerPointerReleased;
        PageScrollViewer.PointerCanceled += OnScrollViewerPointerReleased;

        InitializeColorGrid();

        this.KeyDown += OnPageKeyDown;
    }

    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            if (_selectedField != null)
            {
                DeselectFormField();
                e.Handled = true;
            }
            else if (_activeInsertionTool != InsertionTool.None)
            {
                CancelInsertionMode();
                e.Handled = true;
            }
        }
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
                InsertButton.IsEnabled = _viewModel.HasDocument;
                ManagePagesButton.IsEnabled = _viewModel.HasDocument;
                UpdateNavigationButtons();
                break;

            case nameof(MainViewModel.CanUndo):
                UndoButton.IsEnabled = _viewModel.CanUndo;
                break;

            case nameof(MainViewModel.CanRedo):
                RedoButton.IsEnabled = _viewModel.CanRedo;
                break;

            case nameof(MainViewModel.CurrentPageIndex):
                RemoveFieldHighlight();
                UpdateNavigationButtons();
                break;

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

        if (_isGridVisible)
            DrawGrid();
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
            // Close insertion panel if open
            if (_isInsertionPanelOpen)
            {
                _isInsertionPanelOpen = false;
                InsertionPanel.Width = 0;
                InsertButton.Label = "Insérer";
                CancelInsertionMode();
            }

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

        var newLeft = SnapToGrid(_dragOriginalLeft + dx);
        var newTop = SnapToGrid(_dragOriginalTop + dy);
        (newLeft, newTop) = SnapToFields(newLeft, newTop, _dragBorder.Width, _dragBorder.Height, _dragBorder);
        Canvas.SetLeft(_dragBorder, newLeft);
        Canvas.SetTop(_dragBorder, newTop);

        e.Handled = true;
    }

    private async void OnBlockPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragBorder == null || _dragBlock == null)
            return;

        ClearSnapGuides();
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
        else
        {
            // Read-only fields: still allow right-click for properties
            border.PointerPressed += OnFormBorderRightClickOnly;
        }

        return border;
    }

    private void OnFormBorderPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not FormField field)
            return;

        // Right-click → open properties dialog
        var point = e.GetCurrentPoint(border);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            _ = ShowFormFieldPropertiesDialog(field);
            return;
        }

        // In insertion mode: select field for resize/move instead of inline editing
        if (_isInsertionPanelOpen)
        {
            e.Handled = true;
            SelectFormField(border, field);
            return;
        }

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

            case FormFieldType.PushButton:
                // No inline editor for push buttons (images)
                border.Visibility = Visibility.Visible;
                _activeFormBorder = null;
                return;

            case FormFieldType.Checkbox:
            case FormFieldType.RadioButton:
                // Toggle immediately, no editor needed
                var isOn = field.CurrentValue != null && field.CurrentValue != "Off";
                var onName = field.OnAppearanceName ?? "Yes";
                var newVal = isOn ? "Off" : onName;
                border.Visibility = Visibility.Visible;
                _activeFormBorder = null;
                _ = ApplyFormFieldValueAsync(field, newVal);
                return;

            case FormFieldType.Dropdown:
                var comboBox = new ComboBox
                {
                    Width = Math.Max(sw + 40, 120),
                    Height = Math.Max(sh + 10, 32),
                    FontSize = Math.Max(9, sh * 0.6),
                    BorderThickness = new Thickness(1.5),
                    BorderBrush = new SolidColorBrush(Colors.Green),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(240, 255, 255, 255)),
                    Tag = field,
                    PlaceholderText = field.CurrentValue ?? "Sélectionner...",
                };
                if (field.Options != null && field.Options.Count > 0)
                {
                    foreach (var option in field.Options)
                        comboBox.Items.Add(new ComboBoxItem { Content = option, Tag = option });
                    // Match current value
                    if (field.CurrentValue != null)
                    {
                        for (int i = 0; i < field.Options.Count; i++)
                        {
                            if (field.Options[i] == field.CurrentValue)
                            {
                                comboBox.SelectedIndex = i;
                                break;
                            }
                        }
                    }
                }
                comboBox.SelectionChanged += OnFormDropdownChanged;
                comboBox.LostFocus += OnFormEditorLostFocus;
                editor = comboBox;
                Canvas.SetLeft(editor, sx - 4);
                Canvas.SetTop(editor, sy - (Math.Max(sh + 10, 32) - sh) / 2);
                PageCanvas.Children.Add(editor);
                _activeFormEditor = editor;
                // Defer IsDropDownOpen until control is loaded
                comboBox.Loaded += (s, _) =>
                {
                    if (s is ComboBox cb)
                        cb.IsDropDownOpen = true;
                };
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
        DeselectFormField();
        CloseActiveFormEditor();

        foreach (var overlay in _formFieldOverlays)
        {
            if (overlay is Border border)
            {
                border.PointerPressed -= OnFormBorderPressed;
                border.PointerPressed -= OnFormBorderRightClickOnly;
            }

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
        if (sender is not ComboBox combo || combo.Tag is not FormField field)
            return;
        string? selected = combo.SelectedItem switch
        {
            ComboBoxItem item => item.Tag as string ?? item.Content as string,
            string s => s,
            _ => null,
        };
        if (selected != null)
            await ApplyFormFieldValueAsync(field, selected);
    }

    private async Task ApplyFormFieldValueAsync(FormField field, string value)
    {
        CloseActiveFormEditor();
        _preserveScrollOnRender = true;
        await _viewModel.SetFormFieldValueAsync(field.FieldName, value);
        if (!_isEditMode)
            await ShowFormFieldOverlaysAsync();
    }

    private void OnFormBorderRightClickOnly(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not FormField field)
            return;

        var point = e.GetCurrentPoint(border);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            _ = ShowFormFieldPropertiesDialog(field);
        }
    }

    // ---- Form field selection / resize / move ----

    private void SelectFormField(Border border, FormField field)
    {
        DeselectFormField();

        _selectedField = field;
        _selectedFieldBorder = border;

        // Highlight the selected border
        border.BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
        border.BorderThickness = new Thickness(2);

        // Create 8 resize handles
        var handlePositions = new[] { "tl", "t", "tr", "l", "r", "bl", "b", "br" };
        foreach (var pos in handlePositions)
        {
            var handle = new Border
            {
                Width = 8,
                Height = 8,
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Colors.DodgerBlue),
                BorderThickness = new Thickness(1.5),
                Tag = pos,
                CornerRadius = new CornerRadius(1),
            };

            handle.PointerPressed += OnResizeHandlePressed;
            handle.PointerMoved += OnResizeHandleMoved;
            handle.PointerReleased += OnResizeHandleReleased;
            handle.PointerEntered += OnResizeHandleEntered;
            handle.PointerExited += OnResizeHandleExited;

            PageCanvas.Children.Add(handle);
            _resizeHandles.Add(handle);
        }

        PositionResizeHandles();

        // Also allow move by dragging the border itself
        border.PointerPressed -= OnFormBorderPressed;
        border.PointerPressed += OnSelectedFieldPointerPressed;
        border.PointerMoved += OnSelectedFieldPointerMoved;
        border.PointerReleased += OnSelectedFieldPointerReleased;
    }

    private void DeselectFormField()
    {
        if (_selectedFieldBorder != null)
        {
            _selectedFieldBorder.PointerPressed -= OnSelectedFieldPointerPressed;
            _selectedFieldBorder.PointerMoved -= OnSelectedFieldPointerMoved;
            _selectedFieldBorder.PointerReleased -= OnSelectedFieldPointerReleased;
            PageCanvas.Children.Remove(_selectedFieldBorder);
            _formFieldOverlays.Remove(_selectedFieldBorder);
        }

        foreach (var handle in _resizeHandles)
        {
            handle.PointerPressed -= OnResizeHandlePressed;
            handle.PointerMoved -= OnResizeHandleMoved;
            handle.PointerReleased -= OnResizeHandleReleased;
            handle.PointerEntered -= OnResizeHandleEntered;
            handle.PointerExited -= OnResizeHandleExited;
            PageCanvas.Children.Remove(handle);
        }
        _resizeHandles.Clear();

        _selectedField = null;
        _selectedFieldBorder = null;
        _activeResizeHandle = null;
        _isMovingField = false;
        _isMovePointerDown = false;

        ClearSnapGuides();

        // Restore cursor
        ProtectedCursor = _activeInsertionTool != InsertionTool.None
            ? InputSystemCursor.Create(InputSystemCursorShape.Cross)
            : null;
    }

    private void PositionResizeHandles()
    {
        if (_selectedFieldBorder == null) return;

        var x = Canvas.GetLeft(_selectedFieldBorder);
        var y = Canvas.GetTop(_selectedFieldBorder);
        var w = _selectedFieldBorder.Width;
        var h = _selectedFieldBorder.Height;
        const double hs = 4; // half handle size

        foreach (var handle in _resizeHandles)
        {
            var tag = (string)handle.Tag;
            double hx = tag switch
            {
                "tl" or "l" or "bl" => x - hs,
                "t" or "b" => x + w / 2 - hs,
                "tr" or "r" or "br" => x + w - hs,
                _ => x,
            };
            double hy = tag switch
            {
                "tl" or "t" or "tr" => y - hs,
                "l" or "r" => y + h / 2 - hs,
                "bl" or "b" or "br" => y + h - hs,
                _ => y,
            };
            Canvas.SetLeft(handle, hx);
            Canvas.SetTop(handle, hy);
        }
    }

    private void OnResizeHandleEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border handle) return;
        var tag = (string)handle.Tag;
        var cursor = tag switch
        {
            "tl" or "br" => InputSystemCursorShape.SizeNorthwestSoutheast,
            "tr" or "bl" => InputSystemCursorShape.SizeNortheastSouthwest,
            "t" or "b" => InputSystemCursorShape.SizeNorthSouth,
            "l" or "r" => InputSystemCursorShape.SizeWestEast,
            _ => InputSystemCursorShape.Arrow,
        };
        ProtectedCursor = InputSystemCursor.Create(cursor);
    }

    private void OnResizeHandleExited(object sender, PointerRoutedEventArgs e)
    {
        if (_activeResizeHandle == null)
            ProtectedCursor = _activeInsertionTool != InsertionTool.None
                ? InputSystemCursor.Create(InputSystemCursorShape.Cross)
                : null;
    }

    private void OnResizeHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border handle || _selectedFieldBorder == null) return;

        _activeResizeHandle = (string)handle.Tag;
        _resizeStart = e.GetCurrentPoint(PageCanvas).Position;
        _selOrigX = Canvas.GetLeft(_selectedFieldBorder);
        _selOrigY = Canvas.GetTop(_selectedFieldBorder);
        _selOrigW = _selectedFieldBorder.Width;
        _selOrigH = _selectedFieldBorder.Height;

        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnResizeHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activeResizeHandle == null || _selectedFieldBorder == null) return;

        var pos = e.GetCurrentPoint(PageCanvas).Position;
        var dx = pos.X - _resizeStart.X;
        var dy = pos.Y - _resizeStart.Y;

        var newX = _selOrigX;
        var newY = _selOrigY;
        var newW = _selOrigW;
        var newH = _selOrigH;

        // Adjust based on handle position
        if (_activeResizeHandle.Contains('l'))
        {
            newX = _selOrigX + dx;
            newW = _selOrigW - dx;
        }
        if (_activeResizeHandle.Contains('r'))
        {
            newW = _selOrigW + dx;
        }
        if (_activeResizeHandle.StartsWith('t'))
        {
            newY = _selOrigY + dy;
            newH = _selOrigH - dy;
        }
        if (_activeResizeHandle.StartsWith('b') || _activeResizeHandle == "b")
        {
            newH = _selOrigH + dy;
        }

        // Enforce minimum size
        if (newW < 10) { newW = 10; if (_activeResizeHandle.Contains('l')) newX = _selOrigX + _selOrigW - 10; }
        if (newH < 10) { newH = 10; if (_activeResizeHandle.StartsWith('t')) newY = _selOrigY + _selOrigH - 10; }

        // Snap to grid
        newX = SnapToGrid(newX);
        newY = SnapToGrid(newY);
        newW = SnapToGrid(newW);
        newH = SnapToGrid(newH);
        if (newW < 10) newW = 10;
        if (newH < 10) newH = 10;

        Canvas.SetLeft(_selectedFieldBorder, newX);
        Canvas.SetTop(_selectedFieldBorder, newY);
        _selectedFieldBorder.Width = newW;
        _selectedFieldBorder.Height = newH;

        PositionResizeHandles();
        e.Handled = true;
    }

    private async void OnResizeHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border handle)
        {
            try { handle.ReleasePointerCapture(e.Pointer); } catch { /* already released */ }
        }

        if (_activeResizeHandle == null || _selectedFieldBorder == null || _selectedField == null)
            return;

        _activeResizeHandle = null;
        e.Handled = true;

        // Convert new screen rect to PDF coordinates
        var screenX = Canvas.GetLeft(_selectedFieldBorder);
        var screenY = Canvas.GetTop(_selectedFieldBorder);
        var screenW = _selectedFieldBorder.Width;
        var screenH = _selectedFieldBorder.Height;

        var (pdfLeft, pdfTop) = ScreenToPdfPoint(screenX, screenY);
        var (pdfRight, pdfBottom) = ScreenToPdfPoint(screenX + screenW, screenY + screenH);

        var pdfX = Math.Min(pdfLeft, pdfRight);
        var pdfY = Math.Min(pdfTop, pdfBottom);
        var pdfW = Math.Abs(pdfRight - pdfLeft);
        var pdfH = Math.Abs(pdfTop - pdfBottom);

        var props = new FormFieldProperties
        {
            OriginalFieldName = _selectedField.FieldName,
            PageIndex = _selectedField.PageIndex,
            X = (float)pdfX,
            Y = (float)pdfY,
            Width = (float)pdfW,
            Height = (float)pdfH,
        };

        _preserveScrollOnRender = true;
        DeselectFormField();
        await _viewModel.UpdateFormFieldPropertiesAsync(props);
        // ShowFormFieldOverlaysAsync is called automatically via PropertyChanged after re-render
    }

    // ---- Move selected field by dragging ----

    private void OnSelectedFieldPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border || border.Tag is not FormField field) return;

        // Right-click → properties dialog
        var point = e.GetCurrentPoint(border);
        if (point.Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            _ = ShowFormFieldPropertiesDialog(field);
            return;
        }

        _isMovePointerDown = true;
        _isMovingField = false;
        _moveStart = e.GetCurrentPoint(PageCanvas).Position;
        _moveOrigLeft = Canvas.GetLeft(border);
        _moveOrigTop = Canvas.GetTop(border);
        border.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSelectedFieldPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMovePointerDown || sender is not Border border || _selectedFieldBorder != border)
            return;

        var pos = e.GetCurrentPoint(PageCanvas).Position;
        var dx = pos.X - _moveStart.X;
        var dy = pos.Y - _moveStart.Y;

        if (!_isMovingField)
        {
            if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
                return;
            _isMovingField = true;
            border.Opacity = 0.7;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeAll);
        }

        var newLeft = SnapToGrid(_moveOrigLeft + dx);
        var newTop = SnapToGrid(_moveOrigTop + dy);
        (newLeft, newTop) = SnapToFields(newLeft, newTop, border.Width, border.Height, border);
        Canvas.SetLeft(border, newLeft);
        Canvas.SetTop(border, newTop);
        PositionResizeHandles();
        e.Handled = true;
    }

    private async void OnSelectedFieldPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border border) return;

        try { border.ReleasePointerCapture(e.Pointer); } catch { /* already released */ }

        ClearSnapGuides();
        var wasMoving = _isMovingField;
        _isMovePointerDown = false;
        _isMovingField = false;
        border.Opacity = 1.0;

        // Restore cursor
        ProtectedCursor = _activeInsertionTool != InsertionTool.None
            ? InputSystemCursor.Create(InputSystemCursorShape.Cross)
            : null;

        if (!wasMoving || _selectedField == null)
            return;

        e.Handled = true;

        // Convert new position to PDF coords — include width/height for correct rect
        var screenX = Canvas.GetLeft(border);
        var screenY = Canvas.GetTop(border);
        var screenW = border.Width;
        var screenH = border.Height;

        var (pdfLeft, pdfTop) = ScreenToPdfPoint(screenX, screenY);
        var (pdfRight, pdfBottom) = ScreenToPdfPoint(screenX + screenW, screenY + screenH);

        var pdfX = Math.Min(pdfLeft, pdfRight);
        var pdfY = Math.Min(pdfTop, pdfBottom);
        var pdfW = Math.Abs(pdfRight - pdfLeft);
        var pdfH = Math.Abs(pdfTop - pdfBottom);

        var props = new FormFieldProperties
        {
            OriginalFieldName = _selectedField.FieldName,
            PageIndex = _selectedField.PageIndex,
            X = (float)pdfX,
            Y = (float)pdfY,
            Width = (float)pdfW,
            Height = (float)pdfH,
        };

        _preserveScrollOnRender = true;
        DeselectFormField();
        await _viewModel.UpdateFormFieldPropertiesAsync(props);
    }

    private async Task ShowFormFieldPropertiesDialog(FormField field)
    {
        // Build dialog content
        var panel = new StackPanel { Spacing = 12, MinWidth = 350 };

        var nameBox = new TextBox
        {
            Header = "Nom du champ",
            Text = field.FieldName,
        };
        panel.Children.Add(nameBox);

        // Position & Size
        var posPanel = new StackPanel { Spacing = 8 };
        posPanel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = "Position et taille",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });

        var posGrid = new Grid();
        posGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        posGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8, GridUnitType.Pixel) });
        posGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        posGrid.RowDefinitions.Add(new RowDefinition());
        posGrid.RowDefinitions.Add(new RowDefinition());

        var xBox = new NumberBox { Header = "X", Value = Math.Round(field.X, 1), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1 };
        var yBox = new NumberBox { Header = "Y", Value = Math.Round(field.Y, 1), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1 };
        var wBox = new NumberBox { Header = "Largeur", Value = Math.Round(field.Width, 1), Minimum = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1 };
        var hBox = new NumberBox { Header = "Hauteur", Value = Math.Round(field.Height, 1), Minimum = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1 };

        Grid.SetColumn(xBox, 0); Grid.SetRow(xBox, 0);
        Grid.SetColumn(yBox, 2); Grid.SetRow(yBox, 0);
        Grid.SetColumn(wBox, 0); Grid.SetRow(wBox, 1);
        Grid.SetColumn(hBox, 2); Grid.SetRow(hBox, 1);
        posGrid.Children.Add(xBox);
        posGrid.Children.Add(yBox);
        posGrid.Children.Add(wBox);
        posGrid.Children.Add(hBox);
        posPanel.Children.Add(posGrid);
        panel.Children.Add(posPanel);

        // Font size (for text and dropdown)
        NumberBox? fontSizeBox = null;
        if (field.FieldType == FormFieldType.Text || field.FieldType == FormFieldType.Dropdown)
        {
            fontSizeBox = new NumberBox
            {
                Header = "Taille de police",
                Value = 0,
                PlaceholderText = "Auto",
                Minimum = 0,
                Maximum = 72,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
                SmallChange = 1,
            };
            panel.Children.Add(fontSizeBox);
        }

        // Options (for dropdown)
        TextBox? optionsBox = null;
        if (field.FieldType == FormFieldType.Dropdown)
        {
            optionsBox = new TextBox
            {
                Header = "Options (séparées par virgule)",
                Text = field.Options != null ? string.Join(", ", field.Options) : "",
                AcceptsReturn = false,
            };
            panel.Children.Add(optionsBox);
        }

        // Info
        panel.Children.Add(new Microsoft.UI.Xaml.Controls.TextBlock
        {
            Text = $"Type : {field.FieldType}",
            Foreground = new SolidColorBrush(Colors.Gray),
            FontSize = 12,
        });

        var dialog = new ContentDialog
        {
            Title = "Propriétés du champ",
            Content = panel,
            PrimaryButtonText = "Appliquer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return;

        // Build properties update
        var props = new FormFieldProperties
        {
            OriginalFieldName = field.FieldName,
            PageIndex = field.PageIndex,
        };

        if (nameBox.Text != field.FieldName)
            props.NewFieldName = nameBox.Text;

        if (!double.IsNaN(xBox.Value)) props.X = (float)xBox.Value;
        if (!double.IsNaN(yBox.Value)) props.Y = (float)yBox.Value;
        if (!double.IsNaN(wBox.Value)) props.Width = (float)wBox.Value;
        if (!double.IsNaN(hBox.Value)) props.Height = (float)hBox.Value;

        if (fontSizeBox != null && !double.IsNaN(fontSizeBox.Value) && fontSizeBox.Value > 0)
            props.FontSize = (float)fontSizeBox.Value;

        if (optionsBox != null && !string.IsNullOrWhiteSpace(optionsBox.Text))
        {
            props.Options = optionsBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }

        _preserveScrollOnRender = true;
        await _viewModel.UpdateFormFieldPropertiesAsync(props);
        if (!_isEditMode)
            await ShowFormFieldOverlaysAsync();
    }

    // ---- Insertion panel ----

    private void OnInsertPanelToggle(object sender, RoutedEventArgs e)
    {
        _isInsertionPanelOpen = !_isInsertionPanelOpen;

        if (_isInsertionPanelOpen)
        {
            // Close edit mode if active
            if (_isEditMode)
            {
                _isEditMode = false;
                EditModeButton.Label = "Éditer";
                ClearTextBlockOverlays();
                HideFormatToolbar();
                _textBlocks = null;
                _ = ShowFormFieldOverlaysAsync();
            }

            InsertionPanel.Width = 260;
            InsertButton.Label = "Insérer (ON)";
            EnsurePivotHandler();
        }
        else
        {
            InsertionPanel.Width = 0;
            InsertButton.Label = "Insérer";
            CancelInsertionMode();
            RemoveFieldHighlight();
        }
    }

    private void OnInsertTextClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.Text);

    private void OnInsertImageClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.Image);

    private void OnInsertLineClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.Line);

    private void OnInsertRectClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.Rectangle);

    private void OnInsertCircleClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.Circle);

    private void EnterInsertionMode(InsertionTool tool)
    {
        _activeInsertionTool = tool;
        _viewModel.ActiveTool = tool;

        // Visual feedback: highlight the active button
        ResetInsertionButtonStyles();
        var activeBtn = tool switch
        {
            InsertionTool.Text => InsertTextBtn,
            InsertionTool.Image => InsertImageBtn,
            InsertionTool.Line => InsertLineBtn,
            InsertionTool.Rectangle => InsertRectBtn,
            InsertionTool.Circle => InsertCircleBtn,
            InsertionTool.FormTextField => InsertFormTextBtn,
            InsertionTool.FormCheckbox => InsertFormCheckboxBtn,
            InsertionTool.FormRadioButton => InsertFormRadioBtn,
            InsertionTool.FormDropdown => InsertFormDropdownBtn,
            InsertionTool.FormImage => InsertFormImageBtn,
            InsertionTool.FormDate => InsertFormDateBtn,
            _ => null
        };
        if (activeBtn != null)
        {
            activeBtn.BorderThickness = new Thickness(2);
            activeBtn.BorderBrush = new SolidColorBrush(Colors.DodgerBlue);
        }

        // Show shape options for shape tools
        ShapeOptionsPanel.Visibility = (tool == InsertionTool.Line || tool == InsertionTool.Rectangle || tool == InsertionTool.Circle)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Show form field options
        var isFormTool = IsFormTool(tool);
        FormFieldOptionsPanel.Visibility = isFormTool ? Visibility.Visible : Visibility.Collapsed;
        FormFieldOptionsListPanel.Visibility = tool == InsertionTool.FormDropdown ? Visibility.Visible : Visibility.Collapsed;
        FormFieldRadioGroupPanel.Visibility = tool == InsertionTool.FormRadioButton ? Visibility.Visible : Visibility.Collapsed;

        if (isFormTool && string.IsNullOrEmpty(FormFieldNameBox.Text))
            FormFieldNameBox.Text = GenerateFieldName(tool);

        // Set cursor
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Cross);

        // Attach canvas handlers
        PageCanvas.PointerPressed -= OnCanvasInsertionPointerPressed;
        PageCanvas.PointerMoved -= OnCanvasInsertionPointerMoved;
        PageCanvas.PointerReleased -= OnCanvasInsertionPointerReleased;
        PageCanvas.PointerPressed += OnCanvasInsertionPointerPressed;
        PageCanvas.PointerMoved += OnCanvasInsertionPointerMoved;
        PageCanvas.PointerReleased += OnCanvasInsertionPointerReleased;
    }

    private void CancelInsertionMode()
    {
        _activeInsertionTool = InsertionTool.None;
        _viewModel.ActiveTool = InsertionTool.None;
        _isInsertionDragging = false;

        DeselectFormField();
        ResetInsertionButtonStyles();
        ShapeOptionsPanel.Visibility = Visibility.Collapsed;
        FormFieldOptionsPanel.Visibility = Visibility.Collapsed;

        if (_insertionPreview != null)
        {
            PageCanvas.Children.Remove(_insertionPreview);
            _insertionPreview = null;
        }

        ProtectedCursor = null;

        PageCanvas.PointerPressed -= OnCanvasInsertionPointerPressed;
        PageCanvas.PointerMoved -= OnCanvasInsertionPointerMoved;
        PageCanvas.PointerReleased -= OnCanvasInsertionPointerReleased;
    }

    private void OnInsertFormTextClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.FormTextField);

    private void OnInsertFormCheckboxClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.FormCheckbox);

    private void OnInsertFormRadioClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.FormRadioButton);

    private void OnInsertFormDropdownClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.FormDropdown);

    private void OnInsertFormImageClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.FormImage);

    private void OnInsertFormDateClick(object sender, RoutedEventArgs e)
        => EnterInsertionMode(InsertionTool.FormDate);

    private void ResetInsertionButtonStyles()
    {
        foreach (var btn in new Button[] { InsertTextBtn, InsertImageBtn, InsertLineBtn, InsertRectBtn, InsertCircleBtn,
                                           InsertFormTextBtn, InsertFormCheckboxBtn, InsertFormRadioBtn, InsertFormDropdownBtn,
                                           InsertFormImageBtn, InsertFormDateBtn })
        {
            btn.BorderThickness = new Thickness(0);
            btn.BorderBrush = null;
        }
    }

    private static int _fieldCounter;
    private static string GenerateFieldName(InsertionTool tool)
    {
        _fieldCounter++;
        return tool switch
        {
            InsertionTool.FormTextField => $"text_{_fieldCounter}",
            InsertionTool.FormCheckbox => $"checkbox_{_fieldCounter}",
            InsertionTool.FormRadioButton => $"radio_{_fieldCounter}",
            InsertionTool.FormDropdown => $"dropdown_{_fieldCounter}",
            InsertionTool.FormImage => $"image_{_fieldCounter}",
            InsertionTool.FormDate => $"date_{_fieldCounter}",
            _ => $"field_{_fieldCounter}"
        };
    }

    private static bool IsFormTool(InsertionTool tool) =>
        tool == InsertionTool.FormTextField || tool == InsertionTool.FormCheckbox
        || tool == InsertionTool.FormRadioButton || tool == InsertionTool.FormDropdown
        || tool == InsertionTool.FormImage || tool == InsertionTool.FormDate;

    // ---- Grid & Snap helpers ----

    private void OnGridToggle(object sender, RoutedEventArgs e)
    {
        _isGridVisible = !_isGridVisible;
        GridButton.Label = _isGridVisible ? "Grille (ON)" : "Grille";
        if (_isGridVisible)
            DrawGrid();
        else
            ClearGrid();
    }

    private void DrawGrid()
    {
        ClearGrid();
        var w = PageCanvas.Width;
        var h = PageCanvas.Height;
        if (w <= 0 || h <= 0) return;

        var stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 150, 150, 150));

        for (double x = GridSize; x < w; x += GridSize)
        {
            var line = new Line
            {
                X1 = x, Y1 = 0, X2 = x, Y2 = h,
                Stroke = stroke, StrokeThickness = 0.5
            };
            Canvas.SetZIndex(line, -1);
            _gridLines.Add(line);
            PageCanvas.Children.Add(line);
        }

        for (double y = GridSize; y < h; y += GridSize)
        {
            var line = new Line
            {
                X1 = 0, Y1 = y, X2 = w, Y2 = y,
                Stroke = stroke, StrokeThickness = 0.5
            };
            Canvas.SetZIndex(line, -1);
            _gridLines.Add(line);
            PageCanvas.Children.Add(line);
        }
    }

    private void ClearGrid()
    {
        foreach (var line in _gridLines)
            PageCanvas.Children.Remove(line);
        _gridLines.Clear();
    }

    private double SnapToGrid(double value)
        => _isGridVisible ? Math.Round(value / GridSize) * GridSize : value;

    private (double x, double y) SnapToFields(double x, double y, double w, double h, FrameworkElement? exclude)
    {
        ClearSnapGuides();

        // Collect all other overlay rects
        var rects = new List<(double Left, double Top, double Width, double Height)>();
        foreach (var el in _formFieldOverlays)
        {
            if (el == exclude) continue;
            rects.Add((Canvas.GetLeft(el), Canvas.GetTop(el), el.Width, el.Height));
        }
        foreach (var el in _overlayBorders)
        {
            if (el == exclude) continue;
            rects.Add((Canvas.GetLeft(el), Canvas.GetTop(el), el.Width, el.Height));
        }

        if (rects.Count == 0) return (x, y);

        double bestDx = double.MaxValue, snapX = x;
        double bestDy = double.MaxValue, snapY = y;

        // Edges of the moving element
        double left = x, right = x + w, cx = x + w / 2;
        double top = y, bottom = y + h, cy = y + h / 2;

        foreach (var r in rects)
        {
            double rLeft = r.Left, rRight = r.Left + r.Width, rCx = r.Left + r.Width / 2;
            double rTop = r.Top, rBottom = r.Top + r.Height, rCy = r.Top + r.Height / 2;

            // Check horizontal alignments (left, right, center)
            CheckSnap(left, rLeft, 0, ref bestDx, ref snapX, x);
            CheckSnap(left, rRight, 0, ref bestDx, ref snapX, x);
            CheckSnap(right, rLeft, -w, ref bestDx, ref snapX, x);
            CheckSnap(right, rRight, -w, ref bestDx, ref snapX, x);
            CheckSnap(cx, rCx, -w / 2, ref bestDx, ref snapX, x);

            // Check vertical alignments (top, bottom, center)
            CheckSnap(top, rTop, 0, ref bestDy, ref snapY, y);
            CheckSnap(top, rBottom, 0, ref bestDy, ref snapY, y);
            CheckSnap(bottom, rTop, -h, ref bestDy, ref snapY, y);
            CheckSnap(bottom, rBottom, -h, ref bestDy, ref snapY, y);
            CheckSnap(cy, rCy, -h / 2, ref bestDy, ref snapY, y);
        }

        // Draw snap guides
        var canvasH = PageCanvas.Height;
        var canvasW = PageCanvas.Width;

        if (bestDx < SnapThreshold)
        {
            // Find the aligned X coordinate for the guide line
            double guideX = FindAlignedEdge(snapX, snapX + w, snapX + w / 2, rects, horizontal: true);
            AddSnapGuide(guideX, 0, guideX, canvasH, isVertical: true);
            x = snapX;
        }

        if (bestDy < SnapThreshold)
        {
            double guideY = FindAlignedEdge(snapY, snapY + h, snapY + h / 2, rects, horizontal: false);
            AddSnapGuide(0, guideY, canvasW, guideY, isVertical: false);
            y = snapY;
        }

        return (x, y);
    }

    private static double FindAlignedEdge(double start, double end, double center,
        List<(double Left, double Top, double Width, double Height)> rects, bool horizontal)
    {
        foreach (var r in rects)
        {
            double rStart = horizontal ? r.Left : r.Top;
            double rEnd = horizontal ? r.Left + r.Width : r.Top + r.Height;
            double rCenter = (rStart + rEnd) / 2;

            if (Math.Abs(start - rStart) < 1) return rStart;
            if (Math.Abs(start - rEnd) < 1) return rEnd;
            if (Math.Abs(end - rStart) < 1) return rStart;
            if (Math.Abs(end - rEnd) < 1) return rEnd;
            if (Math.Abs(center - rCenter) < 1) return rCenter;
        }
        return start; // fallback
    }

    private static void CheckSnap(double edge, double target, double offset, ref double bestDist, ref double snapVal, double original)
    {
        var dist = Math.Abs(edge - target);
        if (dist < SnapThreshold && dist < bestDist)
        {
            bestDist = dist;
            snapVal = target + offset;
        }
    }

    private void AddSnapGuide(double x1, double y1, double x2, double y2, bool isVertical)
    {
        var guide = new Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = new SolidColorBrush(Colors.Magenta),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            Opacity = 0.8
        };
        Canvas.SetZIndex(guide, 1000);
        _snapGuides.Add(guide);
        PageCanvas.Children.Add(guide);
    }

    private void ClearSnapGuides()
    {
        foreach (var guide in _snapGuides)
            PageCanvas.Children.Remove(guide);
        _snapGuides.Clear();
    }

    private (double pdfX, double pdfY) ScreenToPdfPoint(double screenX, double screenY)
    {
        var imageW = PageImage.Width;
        var imageH = PageImage.Height;
        var pageHeightPdf = _bitmapHeight / (double)RenderDpi * 72.0;

        var pdfX = screenX / imageW * _bitmapWidth / (double)RenderDpi * 72.0;
        var pdfY = (1.0 - screenY / imageH) * pageHeightPdf;

        return (pdfX, pdfY);
    }

    private async void OnCanvasInsertionPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        DeselectFormField();

        if (_activeInsertionTool == InsertionTool.None) return;

        var pos = e.GetCurrentPoint(PageCanvas).Position;

        if (_activeInsertionTool == InsertionTool.Text)
        {
            // Show inline textbox for insertion
            e.Handled = true;
            ShowInsertionTextBox(pos);
            return;
        }

        // Shape tools, image & form field tools: start drag
        _isInsertionDragging = true;
        _insertionDragStart = pos;
        PageCanvas.CapturePointer(e.Pointer);

        // Create preview rectangle
        _insertionPreview = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Colors.DodgerBlue),
            StrokeThickness = 1.5,
            StrokeDashArray = new DoubleCollection { 4, 2 },
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 30, 144, 255)),
            Width = 0,
            Height = 0,
        };
        Canvas.SetLeft(_insertionPreview, pos.X);
        Canvas.SetTop(_insertionPreview, pos.Y);
        PageCanvas.Children.Add(_insertionPreview);

        e.Handled = true;
    }

    private void OnCanvasInsertionPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isInsertionDragging || _insertionPreview == null) return;

        var pos = e.GetCurrentPoint(PageCanvas).Position;
        var x = Math.Min(pos.X, _insertionDragStart.X);
        var y = Math.Min(pos.Y, _insertionDragStart.Y);
        var w = Math.Abs(pos.X - _insertionDragStart.X);
        var h = Math.Abs(pos.Y - _insertionDragStart.Y);

        x = SnapToGrid(x);
        y = SnapToGrid(y);
        w = SnapToGrid(w);
        h = SnapToGrid(h);

        Canvas.SetLeft(_insertionPreview, x);
        Canvas.SetTop(_insertionPreview, y);
        _insertionPreview.Width = w;
        _insertionPreview.Height = h;

        e.Handled = true;
    }

    private async void OnCanvasInsertionPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isInsertionDragging || _insertionPreview == null) return;

        PageCanvas.ReleasePointerCapture(e.Pointer);
        _isInsertionDragging = false;

        var pos = e.GetCurrentPoint(PageCanvas).Position;
        var screenX = Math.Min(pos.X, _insertionDragStart.X);
        var screenY = Math.Min(pos.Y, _insertionDragStart.Y);
        var screenW = Math.Abs(pos.X - _insertionDragStart.X);
        var screenH = Math.Abs(pos.Y - _insertionDragStart.Y);

        // Remove preview
        PageCanvas.Children.Remove(_insertionPreview);
        _insertionPreview = null;

        // Minimum size check
        if (screenW < 5 || screenH < 5)
        {
            e.Handled = true;
            return;
        }

        // Convert corners to PDF coordinates
        var (pdfLeft, pdfTop) = ScreenToPdfPoint(screenX, screenY);
        var (pdfRight, pdfBottom) = ScreenToPdfPoint(screenX + screenW, screenY + screenH);

        var pdfX = Math.Min(pdfLeft, pdfRight);
        var pdfY = Math.Min(pdfTop, pdfBottom);
        var pdfW = Math.Abs(pdfRight - pdfLeft);
        var pdfH = Math.Abs(pdfTop - pdfBottom);

        _preserveScrollOnRender = true;

        if (IsFormTool(_activeInsertionTool))
        {
            // Create form field
            var fieldName = FormFieldNameBox.Text;
            if (string.IsNullOrWhiteSpace(fieldName))
                fieldName = GenerateFieldName(_activeInsertionTool);

            var formParams = new CreateFormFieldParams
            {
                PageIndex = _viewModel.CurrentPageIndex,
                FieldTool = _activeInsertionTool,
                FieldName = fieldName,
                X = (float)pdfX,
                Y = (float)pdfY,
                Width = (float)pdfW,
                Height = (float)pdfH,
            };

            if (_activeInsertionTool == InsertionTool.FormDropdown && !string.IsNullOrWhiteSpace(FormFieldOptionsBox.Text))
            {
                formParams.Options = FormFieldOptionsBox.Text
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
            }

            if (_activeInsertionTool == InsertionTool.FormRadioButton && !string.IsNullOrWhiteSpace(FormFieldRadioGroupBox.Text))
            {
                formParams.RadioGroupName = FormFieldRadioGroupBox.Text;
            }

            // Image field: open file picker for image
            if (_activeInsertionTool == InsertionTool.FormImage)
            {
                var imagePath = await PickImageFileAsync();
                if (imagePath == null)
                {
                    e.Handled = true;
                    return; // User cancelled
                }
                formParams.ImageFilePath = imagePath;
            }

            // Date field: open date picker dialog
            if (_activeInsertionTool == InsertionTool.FormDate)
            {
                var dateValue = await ShowDatePickerDialogAsync();
                if (dateValue != null)
                    formParams.DefaultValue = dateValue;
            }

            await _viewModel.CreateFormFieldAsync(formParams);

            // Auto-increment field name for next placement
            FormFieldNameBox.Text = GenerateFieldName(_activeInsertionTool);
        }
        else if (_activeInsertionTool == InsertionTool.Image)
        {
            // Image insertion as form field (PushButton) for resize/drag support
            var imagePath = await PickImageFileAsync();
            if (imagePath == null)
            {
                e.Handled = true;
                return;
            }

            _fieldCounter++;
            var formParams = new CreateFormFieldParams
            {
                PageIndex = _viewModel.CurrentPageIndex,
                FieldTool = InsertionTool.FormImage,
                FieldName = $"img_{_fieldCounter}",
                X = (float)pdfX,
                Y = (float)pdfY,
                Width = (float)pdfW,
                Height = (float)pdfH,
                ImageFilePath = imagePath,
            };

            await _viewModel.CreateFormFieldAsync(formParams);
        }
        else
        {
            // Shape insertion
            var strokeWidth = (float)(StrokeWidthBox.Value is double v && !double.IsNaN(v) ? v : 1.0);

            var shapeParams = new InsertShapeParams
            {
                PageIndex = _viewModel.CurrentPageIndex,
                ShapeType = _activeInsertionTool,
                X = (float)pdfX,
                Y = (float)pdfY,
                Width = (float)pdfW,
                Height = (float)pdfH,
                StrokeWidth = strokeWidth,
                StrokeR = 0,
                StrokeG = 0,
                StrokeB = 0,
            };

            await _viewModel.InsertShapeAsync(shapeParams);
        }

        e.Handled = true;
    }

    private void ShowInsertionTextBox(Point screenPos)
    {
        // Remove any existing insertion textbox
        if (_activeTextBox != null)
        {
            PageCanvas.Children.Remove(_activeTextBox);
            _activeTextBox = null;
        }

        var textBox = new TextBox
        {
            Text = "",
            FontSize = 14,
            Width = 200,
            Height = 30,
            AcceptsReturn = false,
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Colors.DodgerBlue),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(220, 255, 255, 255)),
            Padding = new Thickness(4, 2, 4, 2),
            PlaceholderText = "Tapez votre texte...",
        };

        textBox.KeyDown += OnInsertionTextBoxKeyDown;
        textBox.LostFocus += OnInsertionTextBoxLostFocus;

        Canvas.SetLeft(textBox, screenPos.X);
        Canvas.SetTop(textBox, screenPos.Y);
        PageCanvas.Children.Add(textBox);
        _activeTextBox = textBox;

        // Store the screen position for later PDF conversion
        textBox.Tag = screenPos;

        textBox.Focus(FocusState.Programmatic);
    }

    private async void OnInsertionTextBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && sender is TextBox tb)
        {
            e.Handled = true;
            await ApplyInsertionTextAsync(tb);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            RemoveInsertionTextBox();
        }
    }

    private void OnInsertionTextBoxLostFocus(object sender, RoutedEventArgs e)
    {
        // Check if focus went to format toolbar
        if (FocusManager.GetFocusedElement(XamlRoot) is FrameworkElement focused)
        {
            if (IsChildOf(focused, FormatToolbar))
                return;
        }
        RemoveInsertionTextBox();
    }

    private void RemoveInsertionTextBox()
    {
        if (_activeTextBox != null)
        {
            _activeTextBox.KeyDown -= OnInsertionTextBoxKeyDown;
            _activeTextBox.LostFocus -= OnInsertionTextBoxLostFocus;
            PageCanvas.Children.Remove(_activeTextBox);
            _activeTextBox = null;
        }
    }

    private async Task ApplyInsertionTextAsync(TextBox tb)
    {
        var text = tb.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            RemoveInsertionTextBox();
            return;
        }

        var screenPos = tb.Tag is Point p ? p : new Point(0, 0);
        var (pdfX, pdfY) = ScreenToPdfPoint(screenPos.X, screenPos.Y);

        RemoveInsertionTextBox();

        var parameters = new InsertTextParams
        {
            PageIndex = _viewModel.CurrentPageIndex,
            X = (float)pdfX,
            Y = (float)pdfY,
            Text = text,
            FontSize = 12f,
        };

        _preserveScrollOnRender = true;
        await _viewModel.InsertTextAsync(parameters);
    }


    // ---- Form field modals ----

    private async Task<string?> PickImageFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".gif");
        picker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    private async Task<string?> ShowDatePickerDialogAsync()
    {
        var datePicker = new CalendarDatePicker
        {
            Date = DateTimeOffset.Now,
            DateFormat = "{day.integer}/{month.integer}/{year.full}",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        var dialog = new ContentDialog
        {
            Title = "Sélectionner une date",
            Content = datePicker,
            PrimaryButtonText = "Valider",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || datePicker.Date == null)
            return null;

        return datePicker.Date.Value.ToString("dd/MM/yyyy");
    }

    // ---- File operations ----

    private async void OnNewClick(object sender, RoutedEventArgs e)
    {
        _isEditMode = false;
        EditModeButton.Label = "Éditer";
        ClearTextBlockOverlays();
        ClearFormFieldOverlays();
        ClearGrid();
        HideFormatToolbar();
        _textBlocks = null;

        if (_isInsertionPanelOpen)
        {
            _isInsertionPanelOpen = false;
            InsertionPanel.Width = 0;
            InsertButton.Label = "Insérer";
            CancelInsertionMode();
        }

        await _viewModel.CreateDocumentAsync();
    }

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
            ClearGrid();
            HideFormatToolbar();
            _textBlocks = null;

            if (_isInsertionPanelOpen)
            {
                _isInsertionPanelOpen = false;
                InsertionPanel.Width = 0;
                InsertButton.Label = "Insérer";
                CancelInsertionMode();
            }

            await _viewModel.OpenDocumentAsync(file.Path);
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Document?.IsNew == true)
        {
            // New document: prompt for save location
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
            picker.SuggestedFileName = "Nouveau document";

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
                await _viewModel.SaveAsAsync(file.Path);
        }
        else if (_viewModel.SaveCommand.CanExecute(null))
        {
            await _viewModel.SaveCommand.ExecuteAsync(null);
        }
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

    // ---- Page management modal ----

    private class PageThumbnailItem
    {
        public int OriginalIndex { get; set; }
        public string Label { get; set; } = "";
        public Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? Thumbnail { get; set; }
    }

    private async void OnManagePagesClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Document == null) return;

        var items = new System.Collections.ObjectModel.ObservableCollection<PageThumbnailItem>();
        var gridView = new GridView
        {
            CanReorderItems = true,
            AllowDrop = true,
            CanDragItems = true,
            SelectionMode = ListViewSelectionMode.Single,
            ItemsSource = items,
            MinWidth = 600,
            MinHeight = 400,
            MaxHeight = 500,
            Padding = new Thickness(0, 0, 16, 0),
        };

        gridView.ItemTemplate = CreateThumbnailTemplate();

        var addBlankBtn = new Button { Content = "Ajouter page blanche", Margin = new Thickness(0, 0, 8, 0) };
        var deleteBtn = new Button { Content = "Supprimer la sélection", Margin = new Thickness(0, 0, 8, 0) };
        var addPdfBtn = new Button { Content = "Ajouter PDF" };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
            Children = { addBlankBtn, deleteBtn, addPdfBtn }
        };

        var loadingRing = new ProgressRing { IsActive = true, Width = 32, Height = 32, Margin = new Thickness(0, 12, 0, 0) };

        var contentPanel = new StackPanel
        {
            Children = { toolbar, gridView, loadingRing }
        };

        var dialog = new ContentDialog
        {
            Title = "Gestion des pages",
            Content = contentPanel,
            PrimaryButtonText = "Appliquer",
            CloseButtonText = "Annuler",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot,
        };

        // Load thumbnails
        await LoadThumbnailsAsync(items, loadingRing);

        // Button handlers
        addBlankBtn.Click += async (s, args) =>
        {
            int insertAfter = gridView.SelectedIndex >= 0 ? gridView.SelectedIndex : items.Count - 1;
            await _viewModel.AddBlankPageAsync(GetOriginalIndex(items, insertAfter));
            await LoadThumbnailsAsync(items, loadingRing);
        };

        deleteBtn.Click += async (s, args) =>
        {
            if (gridView.SelectedIndex < 0 || items.Count <= 1) return;
            int selectedIdx = gridView.SelectedIndex;
            await _viewModel.DeletePageAsync(GetOriginalIndex(items, selectedIdx));
            await LoadThumbnailsAsync(items, loadingRing);
        };

        addPdfBtn.Click += async (s, args) =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".pdf");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                int insertAfter = gridView.SelectedIndex >= 0 ? gridView.SelectedIndex : items.Count - 1;
                await _viewModel.InsertPagesFromAsync(file.Path, GetOriginalIndex(items, insertAfter));
                await LoadThumbnailsAsync(items, loadingRing);
            }
        };

        var result = await dialog.ShowAsync();

        if (result == ContentDialogResult.Primary)
        {
            // Check if order changed via drag-and-drop
            var newOrder = items.Select(i => i.OriginalIndex).ToArray();
            bool orderChanged = false;
            for (int i = 0; i < newOrder.Length; i++)
            {
                if (newOrder[i] != i)
                {
                    orderChanged = true;
                    break;
                }
            }

            if (orderChanged)
            {
                await _viewModel.ReorderPagesAsync(newOrder);
            }
        }
    }

    private static int GetOriginalIndex(System.Collections.ObjectModel.ObservableCollection<PageThumbnailItem> items, int displayIndex)
    {
        // After add/delete operations, items are reloaded fresh so OriginalIndex == displayIndex
        return displayIndex;
    }

    private async Task LoadThumbnailsAsync(
        System.Collections.ObjectModel.ObservableCollection<PageThumbnailItem> items,
        ProgressRing loadingRing)
    {
        loadingRing.IsActive = true;
        loadingRing.Visibility = Visibility.Visible;
        items.Clear();

        int pageCount = _viewModel.PageCount;
        var thumbnails = new SkiaSharp.SKBitmap?[pageCount];

        // Render thumbnails in parallel
        var tasks = new Task[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            int idx = i;
            tasks[i] = Task.Run(async () =>
            {
                thumbnails[idx] = await _viewModel.RenderThumbnailAsync(idx);
            });
        }
        await Task.WhenAll(tasks);

        // Convert to WriteableBitmaps on UI thread and populate
        for (int i = 0; i < pageCount; i++)
        {
            Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? wb = null;
            if (thumbnails[i] != null)
            {
                wb = Helpers.BitmapHelper.ToWriteableBitmap(thumbnails[i]!);
                thumbnails[i]!.Dispose();
            }

            items.Add(new PageThumbnailItem
            {
                OriginalIndex = i,
                Label = $"Page {i + 1}",
                Thumbnail = wb,
            });
        }

        loadingRing.IsActive = false;
        loadingRing.Visibility = Visibility.Collapsed;
    }

    private DataTemplate CreateThumbnailTemplate()
    {
        // Build DataTemplate in code since we can't use XAML resources from code-behind easily
        var xaml = @"
<DataTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"">
    <StackPanel Width=""130"" Margin=""4"" HorizontalAlignment=""Center"">
        <Border BorderBrush=""{ThemeResource CardStrokeColorDefaultBrush}""
                BorderThickness=""1"" Background=""White""
                Width=""120"" Height=""160"">
            <Image Source=""{Binding Thumbnail}"" Stretch=""Uniform"" />
        </Border>
        <TextBlock Text=""{Binding Label}"" HorizontalAlignment=""Center""
                   Margin=""0,4,0,0"" FontSize=""12"" />
    </StackPanel>
</DataTemplate>";
        return (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(xaml);
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

    // ---- Middle-button pan ----

    private void OnScrollViewerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(PageScrollViewer).Properties;
        if (!props.IsMiddleButtonPressed) return;

        _isMiddleButtonPanning = true;
        _panStart = e.GetCurrentPoint(PageScrollViewer).Position;
        _panOrigHOffset = PageScrollViewer.HorizontalOffset;
        _panOrigVOffset = PageScrollViewer.VerticalOffset;
        PageScrollViewer.CapturePointer(e.Pointer);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);
        e.Handled = true;
    }

    private void OnScrollViewerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMiddleButtonPanning) return;

        var pos = e.GetCurrentPoint(PageScrollViewer).Position;
        var dx = pos.X - _panStart.X;
        var dy = pos.Y - _panStart.Y;

        PageScrollViewer.ChangeView(
            _panOrigHOffset - dx,
            _panOrigVOffset - dy,
            null, disableAnimation: true);

        e.Handled = true;
    }

    private void OnScrollViewerPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isMiddleButtonPanning) return;

        _isMiddleButtonPanning = false;
        try { PageScrollViewer.ReleasePointerCapture(e.Pointer); } catch { }
        ProtectedCursor = _activeInsertionTool != InsertionTool.None
            ? InputSystemCursor.Create(InputSystemCursorShape.Cross)
            : null;
        e.Handled = true;
    }

    // ---- Field list tab ----

    private List<FormField>? _allFieldsCache;
    private List<FieldListItem>? _fieldListItems;

    private async Task RefreshFieldListAsync()
    {
        await _viewModel.ExtractAllFormFieldsAsync();
        _allFieldsCache = _viewModel.AllFormFields;

        if (_allFieldsCache == null || _allFieldsCache.Count == 0)
        {
            FieldListView.ItemsSource = null;
            _fieldListItems = null;
            FieldListEmpty.Visibility = Visibility.Visible;
            return;
        }

        FieldListEmpty.Visibility = Visibility.Collapsed;
        _fieldListItems = _allFieldsCache
            .OrderBy(f => f.PageIndex)
            .ThenBy(f => f.FieldName)
            .Select(f => new FieldListItem(f))
            .ToList();

        ApplyFieldSearchFilter();
    }

    private void OnFieldSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFieldSearchFilter();
    }

    private void ApplyFieldSearchFilter()
    {
        if (_fieldListItems == null) return;

        var query = FieldSearchBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query))
        {
            FieldListView.ItemsSource = _fieldListItems;
        }
        else
        {
            FieldListView.ItemsSource = _fieldListItems
                .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private Border? _fieldHighlight;

    private async void OnFieldListItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FieldListItem item) return;

        var field = item.Field;

        // Remove previous highlight
        RemoveFieldHighlight();

        // Navigate to the correct page if needed
        if (field.PageIndex != _viewModel.CurrentPageIndex)
        {
            _viewModel.CurrentPageIndex = field.PageIndex;
            await _viewModel.RenderCurrentPageAsync();
        }

        // Wait for layout to complete, then scroll and highlight
        DispatcherQueue.TryEnqueue(() =>
        {
            var (sx, sy, sw, sh) = FormFieldToScreen(field);

            // Center the field in the viewport
            var targetH = sx + sw / 2 - PageScrollViewer.ViewportWidth / 2;
            var targetV = sy + sh / 2 - PageScrollViewer.ViewportHeight / 2;
            targetH = Math.Max(0, targetH);
            targetV = Math.Max(0, targetV);

            PageScrollViewer.ChangeView(targetH, targetV, null, disableAnimation: false);

            // Add highlight border
            var highlight = new Border
            {
                Width = sw + 6,
                Height = sh + 6,
                BorderThickness = new Thickness(3),
                BorderBrush = new SolidColorBrush(Colors.OrangeRed),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 255, 69, 0)),
                CornerRadius = new CornerRadius(3),
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(highlight, sx - 3);
            Canvas.SetTop(highlight, sy - 3);
            PageCanvas.Children.Add(highlight);
            _fieldHighlight = highlight;

            // Blink animation: toggle opacity 3 times then stay solid
            BlinkHighlight(highlight);
        });
    }

    private async void BlinkHighlight(Border highlight)
    {
        for (int i = 0; i < 3; i++)
        {
            highlight.Opacity = 0.3;
            await Task.Delay(150);
            if (_fieldHighlight != highlight) return;
            highlight.Opacity = 1.0;
            await Task.Delay(150);
            if (_fieldHighlight != highlight) return;
        }
    }

    private void RemoveFieldHighlight()
    {
        if (_fieldHighlight != null)
        {
            PageCanvas.Children.Remove(_fieldHighlight);
            _fieldHighlight = null;
        }
    }

    // Refresh field list when the "Champs" tab is selected
    private bool _fieldListPivotHandlerAttached;
    private void EnsurePivotHandler()
    {
        if (_fieldListPivotHandlerAttached) return;
        _fieldListPivotHandlerAttached = true;
        SidePanelPivot.SelectionChanged += OnSidePanelPivotChanged;
    }

    private async void OnSidePanelPivotChanged(object sender, SelectionChangedEventArgs e)
    {
        // Index 1 = "Champs" tab
        if (SidePanelPivot.SelectedIndex == 1 && _viewModel.HasDocument)
        {
            await RefreshFieldListAsync();
        }
    }
}

public class FieldListItem
{
    public FormField Field { get; }
    public string Name => Field.FieldName;
    public string TypeLabel => Field.FieldType switch
    {
        FormFieldType.Text => "Texte",
        FormFieldType.Checkbox => "Case à cocher",
        FormFieldType.RadioButton => "Bouton radio",
        FormFieldType.Dropdown => "Liste déroulante",
        FormFieldType.PushButton => "Image",
        FormFieldType.Signature => "Signature",
        _ => "Inconnu"
    };
    public string PageLabel => $"p.{Field.PageIndex + 1}";
    public string Icon => Field.FieldType switch
    {
        FormFieldType.Text => "\uE8D2",
        FormFieldType.Checkbox => "\uE73A",
        FormFieldType.RadioButton => "\uECCB",
        FormFieldType.Dropdown => "\uE70D",
        FormFieldType.PushButton => "\uE91B",
        FormFieldType.Signature => "\uE8A3",
        _ => "\uE946"
    };

    public FieldListItem(FormField field) => Field = field;
}
