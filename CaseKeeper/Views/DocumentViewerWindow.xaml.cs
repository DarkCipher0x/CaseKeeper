using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CaseKeeper.Models;
using CaseKeeper.Services;

namespace CaseKeeper.Views;

public partial class DocumentViewerWindow : Window
{
    private static LibraryService Lib => App.Library;

    private readonly CaseDocument _doc;
    private readonly List<Category> _categories;
    private readonly List<Party> _parties;
    private readonly List<PageInfo> _pages = new();
    private readonly DispatcherTimer _pageNoteTimer, _docNoteTimer;
    private readonly string _path;

    private PdfRenderer _pdf;
    private BitmapImage _image;
    private int _current = 1;           // 1-based page number
    private double _zoom = 1.0;
    private bool _fitWidthPending = true;
    private int _renderVersion;
    private bool _loadingNote;
    private int _notePageShown;         // the page whose note is currently in PageNoteBox (0 = none yet)
    private Dictionary<int, string> _pageText;

    /// <summary>Raised when the user adds a timeline event from this window.</summary>
    public event Action EventAdded;

    public DocumentViewerWindow(CaseDocument doc, int startPage, List<Category> categories, List<Party> parties)
    {
        InitializeComponent();
        _doc = doc;
        _categories = categories;
        _parties = parties;
        _path = Lib.GetFilePath(doc);
        _current = Math.Max(1, startPage);
        Closed += (_, _) => _pdf?.Dispose();

        _pageNoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _pageNoteTimer.Tick += (_, _) => SavePageNote();
        _docNoteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _docNoteTimer.Tick += (_, _) => SaveDocNote();

        UpdateHeader();
        _loadingNote = true;
        DocNoteBox.Text = doc.Notes;
        _loadingNote = false;

        Loaded += async (_, _) => await LoadDocumentAsync();
        Closing += (_, _) => { SavePageNote(); SaveDocNote(); };
        PreviewKeyDown += OnPreviewKeyDown;
        Scroller.SizeChanged += (_, _) => { if (_fitWidthPending) FitWidth(); };
    }

    private void UpdateHeader()
    {
        Title = $"{_doc.Title} – CaseKeeper";
        DocTitle.Text = _doc.Title;
        var meta = new List<string> { _doc.CategoryName };
        if (!string.IsNullOrWhiteSpace(_doc.PartyName)) meta.Add(_doc.PartyName);
        meta.Add(_doc.DocumentDateText);
        if (!string.IsNullOrWhiteSpace(_doc.Author)) meta.Add("From " + _doc.Author);
        DocMeta.Text = string.Join("  ·  ", meta);
        StarIcon.Text = _doc.IsImportant ? "" : "";
    }

    // ------------------------------------------------------------------ loading
    private async Task LoadDocumentAsync()
    {
        int pageCount = 1;
        LoadingText.Visibility = Visibility.Visible;
        try
        {
            if (!File.Exists(_path))
            {
                ShowUnsupported("This document's file is missing from the library folder.");
                return;
            }
            // Decrypted in memory when the library is password protected
            var content = await Task.Run(() => Lib.ReadDocumentBytes(_doc));

            if (_doc.IsPdf)
            {
                try
                {
                    _pdf = await PdfRenderer.OpenAsync(content);
                    pageCount = _pdf.PageCount;
                }
                catch (Exception ex)
                {
                    ShowUnsupported("This PDF couldn't be displayed here (it may be password-protected or damaged).\n" + ex.Message);
                }
            }
            else if (_doc.IsImage)
            {
                _image = new BitmapImage();
                _image.BeginInit();
                _image.CacheOption = BitmapCacheOption.OnLoad;
                _image.StreamSource = new MemoryStream(content);
                _image.EndInit();
                _image.Freeze();
            }
            else if (_doc.IsWord || _doc.IsText)
            {
                Scroller.Visibility = Visibility.Collapsed;
                TextContent.Visibility = Visibility.Visible;
                TextContent.Text = _doc.IsWord
                    ? await Task.Run(() => OcrService.ExtractDocx(content))
                    : OcrService.DecodeText(content);
            }
            else
            {
                ShowUnsupported($"{_doc.FileTypeLabel} files can't be shown inside CaseKeeper, but you can still keep notes on them here.");
            }
        }
        catch (Exception ex)
        {
            ShowUnsupported("This file couldn't be opened: " + ex.Message);
        }
        finally { LoadingText.Visibility = Visibility.Collapsed; }

        var notes = Lib.Db.GetPageNotes(_doc.Id);
        for (int i = 1; i <= pageCount; i++)
            _pages.Add(new PageInfo { PageNumber = i, Note = notes.TryGetValue(i, out var n) ? n : "" });
        PageList.ItemsSource = _pages;
        PageCountText.Text = $"of {pageCount}";
        _current = Math.Min(_current, pageCount);
        GoToPage(_current, force: true);
        if (_fitWidthPending) FitWidth();
        if (!_doc.IsPdf && !_doc.IsImage) ZoomText.Text = "";
    }

    private void ShowUnsupported(string message)
    {
        Scroller.Visibility = Visibility.Collapsed;
        UnsupportedPanel.Visibility = Visibility.Visible;
        UnsupportedText.Text = message;
    }

    // ------------------------------------------------------------------ navigation & rendering
    private void GoToPage(int page, bool force = false)
    {
        if (_pages.Count == 0) return;
        page = Math.Clamp(page, 1, _pages.Count);
        if (page == _current && !force) return;
        SavePageNote();
        _current = page;
        PageBox.Text = page.ToString();
        if (PageList.SelectedIndex != page - 1) PageList.SelectedIndex = page - 1;
        PageList.ScrollIntoView(PageList.SelectedItem);

        _loadingNote = true;
        PageNoteBox.Text = _pages[page - 1].Note;
        _loadingNote = false;
        _notePageShown = page;
        PageNoteLabel.Text = $"Notes for page {page}";
        SavedText.Text = "";
        if (TextExpander.IsExpanded) ShowPageText();

        _ = RenderAsync();
        Scroller.ScrollToTop();
    }

    private (double W, double H) CurrentPageSize()
    {
        if (_pdf != null) return _pdf.GetPageSize(_current - 1);
        if (_image != null) return (_image.PixelWidth, _image.PixelHeight);
        return (0, 0);
    }

    private async Task RenderAsync()
    {
        if (_pdf == null && _image == null) return;
        var (w, h) = CurrentPageSize();
        if (w <= 0) return;
        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
        PageImage.Width = w * _zoom;
        PageImage.Height = h * _zoom;

        if (_image != null)
        {
            PageImage.Source = _image;
            return;
        }

        int version = ++_renderVersion;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // Render at screen resolution (capped so very large zooms stay responsive)
        double scale = Math.Min(_zoom * dpi, 4.0);
        int index = _current - 1;
        try
        {
            var png = await Task.Run(() => _pdf.RenderPngAsync(index, scale));
            if (version != _renderVersion) return; // a newer render has started
            PageImage.Source = PdfRenderer.ToBitmap(png);
        }
        catch (Exception ex)
        {
            if (version == _renderVersion) ShowUnsupported("This page couldn't be displayed: " + ex.Message);
        }
    }

    private void SetZoom(double zoom)
    {
        _fitWidthPending = false;
        _zoom = Math.Clamp(zoom, 0.25, 4.0);
        _ = RenderAsync();
    }

    private void FitWidth()
    {
        var (w, h) = CurrentPageSize();
        if (w <= 0 || Scroller.ActualWidth <= 0) return;
        bool sideways = Math.Abs(PageRotation.Angle % 180) == 90;
        double available = Scroller.ActualWidth - 70;
        _zoom = Math.Clamp(available / (sideways ? h : w), 0.25, 4.0);
        _ = RenderAsync();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => GoToPage(_current - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => GoToPage(_current + 1);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.2);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / 1.2);
    private void FitWidth_Click(object sender, RoutedEventArgs e) { _fitWidthPending = true; FitWidth(); }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        PageRotation.Angle = (PageRotation.Angle + 90) % 360;
        if (_fitWidthPending) FitWidth();
    }

    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageList.SelectedItem is PageInfo p) GoToPage(p.PageNumber);
    }

    private void PageBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && int.TryParse(PageBox.Text, out var p)) GoToPage(p);
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            SetZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
            e.Handled = true;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox { IsReadOnly: false }) return; // don't steal keys while typing notes
        switch (e.Key)
        {
            case Key.PageDown: GoToPage(_current + 1); e.Handled = true; break;
            case Key.PageUp: GoToPage(_current - 1); e.Handled = true; break;
            case Key.Home when Keyboard.Modifiers == ModifierKeys.Control: GoToPage(1); e.Handled = true; break;
            case Key.End when Keyboard.Modifiers == ModifierKeys.Control: GoToPage(_pages.Count); e.Handled = true; break;
            case Key.Escape: Close(); break;
        }
    }

    // ------------------------------------------------------------------ notes (auto-saved)
    private void PageNoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingNote) return;
        SavedText.Text = "Editing…";
        _pageNoteTimer.Stop();
        _pageNoteTimer.Start();
    }

    private void SavePageNote()
    {
        _pageNoteTimer.Stop();
        if (_notePageShown < 1 || _notePageShown > _pages.Count) return; // box hasn't been loaded with a page's note yet
        var page = _pages[_notePageShown - 1];
        var text = PageNoteBox.Text;
        if (page.Note == text) return;
        page.Note = text;
        Lib.Db.SavePageNote(_doc.Id, page.PageNumber, text);
        SavedText.Text = "✓ Saved";
    }

    private void DocNoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingNote) return;
        _docNoteTimer.Stop();
        _docNoteTimer.Start();
    }

    private void SaveDocNote()
    {
        _docNoteTimer.Stop();
        if (_doc.Notes == DocNoteBox.Text) return;
        var fresh = Lib.Db.GetDocument(_doc.Id);
        if (fresh == null) return;
        fresh.Notes = DocNoteBox.Text;
        Lib.Db.UpdateDocument(fresh);
        _doc.Notes = DocNoteBox.Text;
    }

    // ------------------------------------------------------------------ text & find
    private Dictionary<int, string> PageText => _pageText ??= Lib.Db.GetAllPageText(_doc.Id);

    private void TextExpander_Expanded(object sender, RoutedEventArgs e) => ShowPageText();

    private void ShowPageText()
    {
        _pageText = null; // reload in case scanning finished since the window opened
        PageTextBox.Text = PageText.TryGetValue(_current, out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : _doc.OcrStatus switch
            {
                OcrStatus.Pending or OcrStatus.Running => "(This document is still being scanned. Reopen it shortly.)",
                OcrStatus.Failed => "(Text couldn't be read from this document.)",
                _ => "(No text was found on this page.)"
            };
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var term = FindBox.Text.Trim();
        _pageText = null;
        var results = new List<Tuple<int, string>>();
        if (term.Length > 0)
        {
            foreach (var (page, text) in PageText.OrderBy(kv => kv.Key))
            {
                int idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) results.Add(Tuple.Create(page, Snippet(text, idx, term.Length)));
            }
            foreach (var p in _pages.Where(p => p.Note.Contains(term, StringComparison.OrdinalIgnoreCase)))
                results.Add(Tuple.Create(p.PageNumber, "(in your note) " + Snippet(p.Note, p.Note.IndexOf(term, StringComparison.OrdinalIgnoreCase), term.Length)));
        }
        FindResults.ItemsSource = results.Count > 0 ? results : new List<Tuple<int, string>> { Tuple.Create(0, "No matches") };
        FindResults.Visibility = term.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private static string Snippet(string text, int idx, int len)
    {
        int start = Math.Max(0, idx - 40), end = Math.Min(text.Length, idx + len + 60);
        return (start > 0 ? "…" : "") + text[start..end].Replace("\r", " ").Replace("\n", " ") + (end < text.Length ? "…" : "");
    }

    private void FindResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FindResults.SelectedItem is Tuple<int, string> { Item1: > 0 } r) GoToPage(r.Item1);
    }

    // ------------------------------------------------------------------ actions
    private void Star_Click(object sender, RoutedEventArgs e)
    {
        SaveDocNote();
        var fresh = Lib.Db.GetDocument(_doc.Id);
        fresh.IsImportant = !fresh.IsImportant;
        Lib.Db.UpdateDocument(fresh);
        _doc.IsImportant = fresh.IsImportant;
        UpdateHeader();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        SaveDocNote();
        var fresh = Lib.Db.GetDocument(_doc.Id);
        var dlg = new DocumentEditWindow(fresh, _categories, _parties) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Lib.Db.UpdateDocument(fresh);
        _doc.Title = fresh.Title;
        _doc.CategoryId = fresh.CategoryId;
        _doc.PartyId = fresh.PartyId;
        _doc.DocumentDate = fresh.DocumentDate;
        _doc.Author = fresh.Author;
        _doc.IsImportant = fresh.IsImportant;
        _doc.CategoryName = _categories.FirstOrDefault(c => c.Id == fresh.CategoryId)?.DisplayName ?? "";
        _doc.PartyName = _parties.FirstOrDefault(p => p.Id == fresh.PartyId)?.DisplayName ?? "";
        _loadingNote = true;
        DocNoteBox.Text = _doc.Notes = fresh.Notes;
        _loadingNote = false;
        UpdateHeader();
    }

    private void AddEvent_Click(object sender, RoutedEventArgs e)
    {
        var ev = new TimelineEvent
        {
            EventDate = _doc.DocumentDate ?? DateTime.Today, DocumentId = _doc.Id, Title = _doc.Title,
            Description = _pages.Count > 1 ? $"See page {_current}." : ""
        };
        var dlg = new EventEditWindow(ev, new List<CaseDocument> { _doc }) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        Lib.Db.SaveEvent(ev);
        EventAdded?.Invoke();
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        if (!MainWindow.ConfirmConfidentiality(this)) return;
        try { await ShareService.ShareFilesAsync(this, _doc.Title, new[] { Lib.MakeFriendlyCopy(_doc) }); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't open the Windows Share window: " + ex.Message, "Share"); }
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Lib.MakeFriendlyCopy(_doc)) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't open the file: " + ex.Message, "Open"); }
    }
}
