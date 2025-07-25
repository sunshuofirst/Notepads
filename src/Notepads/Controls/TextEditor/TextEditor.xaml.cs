﻿namespace Notepads.Controls.TextEditor
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Notepads.Commands;
    using Notepads.Controls.FindAndReplace;
    using Notepads.Controls.GoTo;
    using Notepads.Extensions;
    using Notepads.Models;
    using Notepads.Services;
    using Notepads.Utilities;
    using Windows.ApplicationModel.DataTransfer;
    using Windows.ApplicationModel.Resources;
    using Windows.Storage;
    using Windows.System;
    using Windows.UI.Core;
    using Windows.UI.Text;
    using Windows.UI.Xaml;
    using Windows.UI.Xaml.Controls;
    using Windows.UI.Xaml.Controls.Primitives;
    using Windows.UI.Xaml.Input;

    public enum TextEditorMode
    {
        Editing = 0,
        DiffPreview
    }

    public enum FileModificationState
    {
        Untouched,
        Modified,
        RenamedMovedOrDeleted
    }

    public sealed partial class TextEditor : ITextEditor, IDisposable
    {
        public new event RoutedEventHandler Loaded;
        public new event RoutedEventHandler Unloaded;
        public new event KeyEventHandler KeyDown;
        public event EventHandler ModeChanged;
        public event EventHandler ModificationStateChanged;
        public event EventHandler FileModificationStateChanged;
        public event EventHandler LineEndingChanged;
        public event EventHandler EncodingChanged;
        public event EventHandler TextChanging;
        public event EventHandler ChangeReverted;
        public event EventHandler SelectionChanged;
        public event EventHandler FontZoomFactorChanged;
        public event EventHandler FileSaved;
        public event EventHandler FileReloaded;
        public event EventHandler FileRenamed;

        public Guid Id { get; set; }

        public INotepadsExtensionProvider ExtensionProvider;

        public string FileNamePlaceholder { get; set; } = string.Empty;

        public FileType FileType { get; private set; }

        public TextFile LastSavedSnapshot { get; private set; }

        public LineEnding? RequestedLineEnding { get; private set; }

        public Encoding RequestedEncoding { get; private set; }

        public string EditingFileName { get; private set; }

        public string EditingFilePath { get; private set; }

        private StorageFile _editingFile;

        public StorageFile EditingFile
        {
            get => _editingFile;
            private set
            {
                _editingFile = value;
                UpdateDocumentInfo();
            }
        }

        private void UpdateDocumentInfo()
        {
            if (EditingFile == null)
            {
                EditingFileName = null;
                EditingFilePath = null;
                FileType = FileTypeUtility.GetFileTypeByFileName(FileNamePlaceholder);
            }
            else
            {
                EditingFileName = EditingFile.Name;
                EditingFilePath = EditingFile.Path;
                FileType = FileTypeUtility.GetFileTypeByFileName(EditingFile.Name);
            }

            // Hide content preview if current file type is not supported for previewing
            if (!FileTypeUtility.IsPreviewSupported(FileType))
            {
                if (SplitPanel != null && SplitPanel.Visibility == Visibility.Visible)
                {
                    ShowHideContentPreview();
                }
            }
        }

        private bool _isModified;

        public bool IsModified
        {
            get => _isModified;
            private set
            {
                if (_isModified != value)
                {
                    _isModified = value;
                    ModificationStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public FileModificationState FileModificationState
        {
            get => _fileModificationState;
            private set
            {
                if (_fileModificationState != value)
                {
                    _fileModificationState = value;
                    FileModificationStateChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        private bool _loaded;

        private FileModificationState _fileModificationState;

        private bool _isContentPreviewPanelOpened;

        private readonly ResourceLoader _resourceLoader = ResourceLoader.GetForCurrentView();

        private CancellationTokenSource _fileStatusCheckerCancellationTokenSource;

        private readonly int _fileStatusCheckerPollingRateInSec = 6;

        private readonly double _fileStatusCheckerDelayInSec = 0.3;

        private readonly SemaphoreSlim _fileStatusSemaphoreSlim = new SemaphoreSlim(1, 1);

        private TextEditorMode _mode = TextEditorMode.Editing;

        private readonly ICommandHandler<KeyRoutedEventArgs> _keyboardCommandHandler;

        private IContentPreviewExtension _contentPreviewExtension;

        private SearchContext _lastSearchContext = new SearchContext(string.Empty);

        private bool _isAutoFilenameModeEnabled = true; // Track if auto-filename mode is enabled

        public TextEditorMode Mode
        {
            get => _mode;
            private set
            {
                if (_mode != value)
                {
                    _mode = value;
                    ModeChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public bool DisplayLineNumbers
        {
            get => TextEditorCore.DisplayLineNumbers;
            set => TextEditorCore.DisplayLineNumbers = value;
        }

        public bool DisplayLineHighlighter
        {
            get => TextEditorCore.DisplayLineHighlighter;
            set => TextEditorCore.DisplayLineHighlighter = value;
        }

        public TextEditor()
        {
            InitializeComponent();

            TextEditorCore.TextChanging += TextEditorCore_OnTextChanging;
            TextEditorCore.SelectionChanged += TextEditorCore_OnSelectionChanged;
            TextEditorCore.KeyDown += TextEditorCore_OnKeyDown;
            TextEditorCore.CopyTextToWindowsClipboardRequested += TextEditorCore_CopyTextToWindowsClipboardRequested;
            TextEditorCore.CutSelectedTextToWindowsClipboardRequested += TextEditorCore_CutSelectedTextToWindowsClipboardRequested;
            TextEditorCore.ContextFlyout = new TextEditorContextFlyout(this, TextEditorCore);

            // Init shortcuts
            _keyboardCommandHandler = GetKeyboardCommandHandler();

            ThemeSettingsService.OnThemeChanged += ThemeSettingsService_OnThemeChanged;

            base.Loaded += TextEditor_Loaded;
            base.Unloaded += TextEditor_Unloaded;
            base.KeyDown += TextEditor_KeyDown;

            TextEditorCore.FontZoomFactorChanged += TextEditorCore_OnFontZoomFactorChanged;
        }

        private void TextEditor_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            KeyDown?.Invoke(this, e);
        }

        // Unhook events and clear state
        public void Dispose()
        {
            StopCheckingFileStatus();

            TextEditorCore.TextChanging -= TextEditorCore_OnTextChanging;
            TextEditorCore.SelectionChanged -= TextEditorCore_OnSelectionChanged;
            TextEditorCore.KeyDown -= TextEditorCore_OnKeyDown;
            TextEditorCore.CopyTextToWindowsClipboardRequested -= TextEditorCore_CopyTextToWindowsClipboardRequested;
            TextEditorCore.CutSelectedTextToWindowsClipboardRequested -= TextEditorCore_CutSelectedTextToWindowsClipboardRequested;

            if (TextEditorCore.ContextFlyout is TextEditorContextFlyout contextFlyout)
            {
                contextFlyout.Dispose();
            }

            ThemeSettingsService.OnThemeChanged -= ThemeSettingsService_OnThemeChanged;

            Unloaded?.Invoke(this, new RoutedEventArgs());

            base.Loaded -= TextEditor_Loaded;
            base.Unloaded -= TextEditor_Unloaded;
            base.KeyDown -= TextEditor_KeyDown;

            TextEditorCore.FontZoomFactorChanged -= TextEditorCore_OnFontZoomFactorChanged;

            _contentPreviewExtension?.Dispose();

            if (SplitPanel != null)
            {
                SplitPanel.KeyDown -= SplitPanel_OnKeyDown;
                UnloadObject(SplitPanel);
            }

            if (SideBySideDiffViewer != null)
            {
                SideBySideDiffViewer.OnCloseEvent -= SideBySideDiffViewer_OnCloseEvent;
                SideBySideDiffViewer.Dispose();
                UnloadObject(SideBySideDiffViewer);
            }

            if (FindAndReplacePlaceholder != null && FindAndReplacePlaceholder.Content is FindAndReplaceControl findAndReplaceControl)
            {
                findAndReplaceControl.Dispose();
                UnloadObject(FindAndReplacePlaceholder);
            }

            if (GoToPlaceholder != null && GoToPlaceholder.Content is GoToControl goToControl)
            {
                goToControl.Dispose();
                UnloadObject(GoToPlaceholder);
            }

            if (GridSplitter != null)
            {
                UnloadObject(GridSplitter);
            }

            _fileStatusSemaphoreSlim.Dispose();
            TextEditorCore.Dispose();
        }

        private async void ThemeSettingsService_OnThemeChanged(object sender, ElementTheme theme)
        {
            await Dispatcher.CallOnUIThreadAsync(() =>
            {
                if (Mode == TextEditorMode.DiffPreview)
                {
                    SideBySideDiffViewer.RenderDiff(LastSavedSnapshot.Content, TextEditorCore.GetText(), theme);
                    Task.Factory.StartNew(async () =>
                    {
                        await Dispatcher.CallOnUIThreadAsync(() => { SideBySideDiffViewer.Focus(); });
                    });
                }
            });
        }

        public async Task RenameAsync(string newFileName)
        {
            if (EditingFile == null)
            {
                FileNamePlaceholder = newFileName;
                // If user manually renames, disable auto-filename mode
                var defaultFileName = _resourceLoader.GetString("TextEditor_DefaultNewFileName");
                if (!string.Equals(newFileName, defaultFileName, StringComparison.OrdinalIgnoreCase) &&
                    !(newFileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && GetFirstLineAsFileName(GetText()) != null))
                {
                    _isAutoFilenameModeEnabled = false;
                }
            }
            else
            {
                await EditingFile.RenameAsync(newFileName);
            }

            UpdateDocumentInfo();

            FileRenamed?.Invoke(this, EventArgs.Empty);
        }

        public string GetText()
        {
            return TextEditorCore.GetText();
        }

        // Make sure this method is thread safe
        public TextEditorStateMetaData GetTextEditorStateMetaData()
        {
            TextEditorCore.GetScrollViewerPosition(out var horizontalOffset, out var verticalOffset);
            TextEditorCore.GetTextSelectionPosition(out var textSelectionStartPosition, out var textSelectionEndPosition);

            var metaData = new TextEditorStateMetaData
            {
                FileNamePlaceholder = FileNamePlaceholder,
                LastSavedEncoding = EncodingUtility.GetEncodingName(LastSavedSnapshot.Encoding),
                LastSavedLineEnding = LineEndingUtility.GetLineEndingName(LastSavedSnapshot.LineEnding),
                DateModifiedFileTime = LastSavedSnapshot.DateModifiedFileTime,
                HasEditingFile = EditingFile != null,
                IsModified = IsModified,
                SelectionStartPosition = textSelectionStartPosition,
                SelectionEndPosition = textSelectionEndPosition,
                WrapWord = TextEditorCore.TextWrapping == TextWrapping.Wrap ||
                           TextEditorCore.TextWrapping == TextWrapping.WrapWholeWords,
                ScrollViewerHorizontalOffset = horizontalOffset,
                ScrollViewerVerticalOffset = verticalOffset,
                FontZoomFactor = TextEditorCore.GetFontZoomFactor() / 100,
                IsContentPreviewPanelOpened = _isContentPreviewPanelOpened,
                IsInDiffPreviewMode = (Mode == TextEditorMode.DiffPreview)
            };

            if (RequestedEncoding != null)
            {
                metaData.RequestedEncoding = EncodingUtility.GetEncodingName(RequestedEncoding);
            }

            if (RequestedLineEnding != null)
            {
                metaData.RequestedLineEnding = LineEndingUtility.GetLineEndingName(RequestedLineEnding.Value);
            }

            return metaData;
        }

        private void TextEditor_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded?.Invoke(this, e);

            StartCheckingFileStatusPeriodically();

            // Insert "Legacy Windows Notepad" style date and time if document starts with ".LOG"
            TextEditorCore.TryInsertNewLogEntry();
        }

        private void TextEditor_Unloaded(object sender, RoutedEventArgs e)
        {
            Unloaded?.Invoke(this, e);
            StopCheckingFileStatus();
        }

        public async void StartCheckingFileStatusPeriodically()
        {
            if (EditingFile == null) return;
            StopCheckingFileStatus();
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;
            _fileStatusCheckerCancellationTokenSource = cancellationTokenSource;

            try
            {
                await Task.Run(async () =>
                {
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_fileStatusCheckerDelayInSec), cancellationToken);
                        LoggingService.LogInfo($"[{nameof(TextEditor)}] Checking file status for \"{EditingFile.Path}\".", consoleOnly: true);
                        await CheckAndUpdateFileStatusAsync(cancellationToken);
                        await Task.Delay(TimeSpan.FromSeconds(_fileStatusCheckerPollingRateInSec), cancellationToken);
                    }
                }, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                // ignore
            }
            catch (ObjectDisposedException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"[{nameof(TextEditor)}] Failed to check status for file [{EditingFile?.Path}]: {ex.Message}");
            }
        }

        public void StopCheckingFileStatus()
        {
            if (_fileStatusCheckerCancellationTokenSource?.IsCancellationRequested == false)
            {
                _fileStatusCheckerCancellationTokenSource.Cancel();
            }
        }

        private async Task CheckAndUpdateFileStatusAsync(CancellationToken cancellationToken)
        {
            if (EditingFile == null) return;

            await _fileStatusSemaphoreSlim.WaitAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                _fileStatusSemaphoreSlim.Release();
                return;
            }

            FileModificationState? newState = null;

            if (!await FileSystemUtility.FileExistsAsync(EditingFile))
            {
                newState = FileModificationState.RenamedMovedOrDeleted;
            }
            else
            {
                long fileModifiedTime = await FileSystemUtility.GetDateModifiedAsync(EditingFile);
                newState = fileModifiedTime != LastSavedSnapshot.DateModifiedFileTime ?
                    FileModificationState.Modified :
                    FileModificationState.Untouched;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _fileStatusSemaphoreSlim.Release();
                return;
            }

            await Dispatcher.CallOnUIThreadAsync(() =>
            {
                FileModificationState = newState.Value;
            });

            _fileStatusSemaphoreSlim.Release();
        }

        private KeyboardCommandHandler GetKeyboardCommandHandler()
        {
            return new KeyboardCommandHandler(new List<IKeyboardCommand<KeyRoutedEventArgs>>
            {
                new KeyboardCommand<KeyRoutedEventArgs>(true, false, false, VirtualKey.F, (args) => ShowFindAndReplaceControl(showReplaceBar: false)),
                new KeyboardCommand<KeyRoutedEventArgs>(true, false, true, VirtualKey.F, (args) => ShowFindAndReplaceControl(showReplaceBar: true)),
                new KeyboardCommand<KeyRoutedEventArgs>(true, false, false, VirtualKey.H, (args) => ShowFindAndReplaceControl(showReplaceBar: true)),
                new KeyboardCommand<KeyRoutedEventArgs>(true, false, false, VirtualKey.G, (args) => ShowGoToControl()),
                new KeyboardCommand<KeyRoutedEventArgs>(false, true, false, VirtualKey.P, (args) => { if (FileTypeUtility.IsPreviewSupported(FileType)) ShowHideContentPreview(); }),
                new KeyboardCommand<KeyRoutedEventArgs>(false, true, false, VirtualKey.D, (args) => ShowHideSideBySideDiffViewer()),
                new KeyboardCommand<KeyRoutedEventArgs>(VirtualKey.F3, (args) =>
                    InitiateFindAndReplace(new FindAndReplaceEventArgs (_lastSearchContext, string.Empty, FindAndReplaceMode.FindOnly, SearchDirection.Next), out _)),
                new KeyboardCommand<KeyRoutedEventArgs>(false, false, true, VirtualKey.F3, (args) =>
                    InitiateFindAndReplace(new FindAndReplaceEventArgs (_lastSearchContext, string.Empty, FindAndReplaceMode.FindOnly, SearchDirection.Previous), out _)),
                new KeyboardCommand<KeyRoutedEventArgs>(VirtualKey.Escape, (args) => { OnEscapeKeyDown(); }, shouldHandle: false, shouldSwallow: true)
            });
        }

        public void Init(TextFile textFile, StorageFile file, bool resetLastSavedSnapshot = true, bool clearUndoQueue = true, bool isModified = false, bool resetText = true)
        {
            _loaded = false;
            EditingFile = file;
            RequestedEncoding = null;
            RequestedLineEnding = null;
            
            // Reset auto-filename mode for new documents
            _isAutoFilenameModeEnabled = (file == null);
            
            if (resetText)
            {
                TextEditorCore.SetText(textFile.Content);
            }
            if (resetLastSavedSnapshot)
            {
                textFile.Content = TextEditorCore.GetText();
                LastSavedSnapshot = textFile;
            }
            if (clearUndoQueue)
            {
                TextEditorCore.ClearUndoQueue();
            }
            IsModified = isModified;
            
            // 确保TextChanging事件订阅始终有效
            EnsureTextChangingEventSubscription();
            
            _loaded = true;
        }

        private void EnsureTextChangingEventSubscription()
        {
            // 先取消订阅以避免重复订阅
            TextEditorCore.TextChanging -= TextEditorCore_OnTextChanging;
            // 重新订阅事件
            TextEditorCore.TextChanging += TextEditorCore_OnTextChanging;
        }

        public async Task ReloadFromEditingFileAsync(Encoding encoding = null)
        {
            if (EditingFile != null)
            {
                var textFile = await FileSystemUtility.ReadFileAsync(EditingFile, ignoreFileSizeLimit: false, encoding: encoding);
                Init(textFile, EditingFile, clearUndoQueue: false);
                
                // 确保TextChanging事件订阅始终有效
                EnsureTextChangingEventSubscription();
                
                LineEndingChanged?.Invoke(this, EventArgs.Empty);
                EncodingChanged?.Invoke(this, EventArgs.Empty);
                StartCheckingFileStatusPeriodically();
                CloseSideBySideDiffViewer();
                HideGoToControl();
                FileReloaded?.Invoke(this, EventArgs.Empty);
                AnalyticsService.TrackEvent(encoding == null ? "OnFileReloaded" : "OnFileReopenedWithEncoding");
            }
        }

        public void ResetEditorState(TextEditorStateMetaData metadata, string newText = null)
        {
            if (!string.IsNullOrEmpty(metadata.RequestedEncoding))
            {
                TryChangeEncoding(EncodingUtility.GetEncodingByName(metadata.RequestedEncoding));
            }

            if (!string.IsNullOrEmpty(metadata.RequestedLineEnding))
            {
                TryChangeLineEnding(LineEndingUtility.GetLineEndingByName(metadata.RequestedLineEnding));
            }

            if (newText != null)
            {
                TextEditorCore.SetText(newText);
            }

            TextEditorCore.TextWrapping = metadata.WrapWord ? TextWrapping.Wrap : TextWrapping.NoWrap;
            TextEditorCore.FontSize = metadata.FontZoomFactor * AppSettingsService.EditorFontSize;
            TextEditorCore.SetTextSelectionPosition(metadata.SelectionStartPosition, metadata.SelectionEndPosition);
            TextEditorCore.SetScrollViewerInitPosition(metadata.ScrollViewerHorizontalOffset, metadata.ScrollViewerVerticalOffset);
            TextEditorCore.ClearUndoQueue();
        }

        public void RevertAllChanges()
        {
            Init(LastSavedSnapshot, EditingFile, clearUndoQueue: false);
            ChangeReverted?.Invoke(this, EventArgs.Empty);
        }

        public bool TryChangeEncoding(Encoding encoding)
        {
            if (encoding == null) return false;

            if (!EncodingUtility.Equals(LastSavedSnapshot.Encoding, encoding))
            {
                RequestedEncoding = encoding;
                IsModified = true;
                EncodingChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (RequestedEncoding != null && EncodingUtility.Equals(LastSavedSnapshot.Encoding, encoding))
            {
                RequestedEncoding = null;
                IsModified = !NoChangesSinceLastSaved();
                EncodingChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            return false;
        }

        public bool TryChangeLineEnding(LineEnding lineEnding)
        {
            if (LastSavedSnapshot.LineEnding != lineEnding)
            {
                RequestedLineEnding = lineEnding;
                IsModified = true;
                LineEndingChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            if (RequestedLineEnding != null && LastSavedSnapshot.LineEnding == lineEnding)
            {
                RequestedLineEnding = null;
                IsModified = !NoChangesSinceLastSaved();
                LineEndingChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            return false;
        }

        public LineEnding GetLineEnding()
        {
            return RequestedLineEnding ?? LastSavedSnapshot.LineEnding;
        }

        public Encoding GetEncoding()
        {
            return RequestedEncoding ?? LastSavedSnapshot.Encoding;
        }

        private void OpenSplitView(IContentPreviewExtension extension)
        {
            SplitPanel.Content = extension;
            SplitPanelColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            SplitPanelColumnDefinition.MinWidth = 100.0f;
            SplitPanel.Visibility = Visibility.Visible;
            GridSplitter.Visibility = Visibility.Visible;
            AnalyticsService.TrackEvent("MarkdownContentPreview_Opened");
            _isContentPreviewPanelOpened = true;
        }

        private void CloseSplitView()
        {
            SplitPanelColumnDefinition.Width = new GridLength(0);
            EditorColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            SplitPanelColumnDefinition.MinWidth = 0.0f;
            SplitPanel.Visibility = Visibility.Collapsed;
            GridSplitter.Visibility = Visibility.Collapsed;
            TextEditorCore.ResetFocusAndScrollToPreviousPosition();
            _isContentPreviewPanelOpened = false;
        }

        public void ShowHideContentPreview()
        {
            if (_contentPreviewExtension == null)
            {
                _contentPreviewExtension = ExtensionProvider?.GetContentPreviewExtension(FileType);
                if (_contentPreviewExtension == null) return;
                _contentPreviewExtension.Bind(TextEditorCore);
            }

            if (SplitPanel == null) LoadSplitView();

            if (SplitPanel.Visibility == Visibility.Collapsed)
            {
                _contentPreviewExtension.IsExtensionEnabled = true;
                OpenSplitView(_contentPreviewExtension);
            }
            else
            {
                _contentPreviewExtension.IsExtensionEnabled = false;
                CloseSplitView();
            }
        }

        public void OpenSideBySideDiffViewer()
        {
            if (string.Equals(LastSavedSnapshot.Content, TextEditorCore.GetText())) return;
            if (Mode == TextEditorMode.DiffPreview) return;
            if (SideBySideDiffViewer == null) LoadSideBySideDiffViewer();
            Mode = TextEditorMode.DiffPreview;
            TextEditorCore.IsEnabled = false;
            EditorRowDefinition.Height = new GridLength(0);
            SideBySideDiffViewRowDefinition.Height = new GridLength(1, GridUnitType.Star);
            SideBySideDiffViewer.Visibility = Visibility.Visible;
            SideBySideDiffViewer.RenderDiff(LastSavedSnapshot.Content, TextEditorCore.GetText(), ThemeSettingsService.ThemeMode);
            SideBySideDiffViewer.Focus();
            AnalyticsService.TrackEvent("SideBySideDiffViewer_Opened");
        }

        public void CloseSideBySideDiffViewer()
        {
            if (Mode != TextEditorMode.DiffPreview) return;
            Mode = TextEditorMode.Editing;
            TextEditorCore.IsEnabled = true;
            EditorRowDefinition.Height = new GridLength(1, GridUnitType.Star);
            SideBySideDiffViewRowDefinition.Height = new GridLength(0);
            SideBySideDiffViewer.Visibility = Visibility.Collapsed;
            SideBySideDiffViewer.StopRenderingAndClearCache();
            TextEditorCore.ResetFocusAndScrollToPreviousPosition();
        }

        private void ShowHideSideBySideDiffViewer()
        {
            if (Mode != TextEditorMode.DiffPreview)
            {
                OpenSideBySideDiffViewer();
            }
            else
            {
                CloseSideBySideDiffViewer();
            }
        }

        /// <summary>
        /// Returns 1-based indexing values
        /// </summary>
        public void GetLineColumnSelection(
            out int startLine,
            out int endLine,
            out int startColumn,
            out int endColumn,
            out int selected,
            out int lineCount)
        {
            TextEditorCore.GetLineColumnSelection(
                out startLine,
                out endLine,
                out startColumn,
                out endColumn,
                out selected,
                out lineCount,
                GetLineEnding());
        }

        public double GetFontZoomFactor()
        {
            return TextEditorCore.GetFontZoomFactor();
        }

        public void SetFontZoomFactor(double fontZoomFactor)
        {
            TextEditorCore.SetFontZoomFactor(fontZoomFactor);
        }

        public bool IsEditorEnabled()
        {
            return TextEditorCore.IsEnabled;
        }

        public async Task SaveContentToFileAndUpdateEditorStateAsync(StorageFile file)
        {
            if (Mode == TextEditorMode.DiffPreview) CloseSideBySideDiffViewer();
            TextFile textFile = await SaveContentToFileAsync(file); // Will throw if not succeeded
            FileModificationState = FileModificationState.Untouched;
            Init(textFile, file, clearUndoQueue: false, resetText: false);
            FileSaved?.Invoke(this, EventArgs.Empty);
            StartCheckingFileStatusPeriodically();
        }

        private async Task<TextFile> SaveContentToFileAsync(StorageFile file)
        {
            var text = TextEditorCore.GetText();
            var encoding = RequestedEncoding ?? LastSavedSnapshot.Encoding;
            var lineEnding = RequestedLineEnding ?? LastSavedSnapshot.LineEnding;
            await FileSystemUtility.WriteTextToFileAsync(file, LineEndingUtility.ApplyLineEnding(text, lineEnding), encoding); // Will throw if not succeeded
            var newFileModifiedTime = await FileSystemUtility.GetDateModifiedAsync(file);
            return new TextFile(text, encoding, lineEnding, newFileModifiedTime);
        }

        public string GetContentForSharing()
        {
            return TextEditorCore.Document.Selection.StartPosition == TextEditorCore.Document.Selection.EndPosition ?
                TextEditorCore.GetText() :
                TextEditorCore.Document.Selection.Text;
        }

        public void TypeText(string text)
        {
            if (TextEditorCore.IsEnabled)
            {
                TextEditorCore.Document.Selection.TypeText(text);
            }
        }

        public void Focus()
        {
            if (Mode == TextEditorMode.DiffPreview)
            {
                SideBySideDiffViewer.Focus();
            }
            else if (Mode == TextEditorMode.Editing)
            {
                TextEditorCore.ResetFocusAndScrollToPreviousPosition();
            }
        }

        public FlyoutBase GetContextFlyout()
        {
            return TextEditorCore.ContextFlyout;
        }

        public void CopyTextToWindowsClipboard(TextControlCopyingToClipboardEventArgs args)
        {
            if (args != null)
            {
                args.Handled = true;
            }

            if (AppSettingsService.IsSmartCopyEnabled)
            {
                TextEditorCore.SmartlyTrimTextSelection();
            }

            CopyTextToWindowsClipboardInternal(true);
        }

        public void CutSelectedTextToWindowsClipboard(TextControlCuttingToClipboardEventArgs args)
        {
            if (args != null)
            {
                args.Handled = true;
            }

            CopyTextToWindowsClipboardInternal(false);
            TextEditorCore.Document.Selection.SetText(TextSetOptions.None, string.Empty);
        }

        private void CopyTextToWindowsClipboardInternal(bool clearLineSelection)
        {
            try
            {
                DataPackage dataPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };

                // Selection.Length can be negative when user selecting text from right to left
                var isTextSelected = TextEditorCore.Document.Selection.Length != 0;
                var cursorPosition = TextEditorCore.Document.Selection.StartPosition;

                if (!isTextSelected)
                {
                    TextEditorCore.Document.Selection.Expand(TextRangeUnit.Paragraph);
                }

                var text = LineEndingUtility.ApplyLineEnding(TextEditorCore.Document.Selection.Text, GetLineEnding());
                dataPackage.SetText(text);

                if (clearLineSelection && !isTextSelected)
                {
                    TextEditorCore.Document.Selection.SetRange(cursorPosition, cursorPosition);
                }

                Clipboard.SetContentWithOptions(dataPackage, new ClipboardContentOptions() { IsAllowedInHistory = true, IsRoamable = true });
                Clipboard.Flush(); // This method allows the content to remain available after the application shuts down.
            }
            catch (Exception ex)
            {
                LoggingService.LogError($"[{nameof(TextEditor)}] Failed to copy plain text to Windows clipboard: {ex.Message}");
            }
        }

        public bool NoChangesSinceLastSaved(bool compareTextOnly = false)
        {
            if (!_loaded) return true;

            if (!compareTextOnly)
            {
                if (RequestedLineEnding != null)
                {
                    return false;
                }

                if (RequestedEncoding != null)
                {
                    return false;
                }
            }

            return string.Equals(LastSavedSnapshot.Content, TextEditorCore.GetText());
        }

        private void OnEscapeKeyDown()
        {
            if (_isContentPreviewPanelOpened)
            {
                _contentPreviewExtension.IsExtensionEnabled = false;
                CloseSplitView();
            }
            else if (FindAndReplacePlaceholder != null && FindAndReplacePlaceholder.Visibility == Visibility.Visible)
            {
                HideFindAndReplaceControl();
                TextEditorCore.Focus(FocusState.Programmatic);
            }
            else if (GoToPlaceholder != null && GoToPlaceholder.Visibility == Visibility.Visible)
            {
                HideGoToControl();
                TextEditorCore.Focus(FocusState.Programmatic);
            }
        }

        private void LoadSplitView()
        {
            FindName("SplitPanel");
            FindName("GridSplitter");
            SplitPanel.Visibility = Visibility.Collapsed;
            GridSplitter.Visibility = Visibility.Collapsed;
            SplitPanel.KeyDown += SplitPanel_OnKeyDown;
        }

        private void LoadSideBySideDiffViewer()
        {
            FindName("SideBySideDiffViewer");
            SideBySideDiffViewer.Visibility = Visibility.Collapsed;
            SideBySideDiffViewer.OnCloseEvent += SideBySideDiffViewer_OnCloseEvent;
        }

        private void SideBySideDiffViewer_OnCloseEvent(object sender, EventArgs e)
        {
            CloseSideBySideDiffViewer();
        }

        private void SplitPanel_OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var result = _keyboardCommandHandler.Handle(e);
            if (result.ShouldHandle)
            {
                e.Handled = true;
            }
        }

        private void TextEditorCore_OnSelectionChanged(object sender, RoutedEventArgs e)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void TextEditorCore_OnFontZoomFactorChanged(object sender, double e)
        {
            FontZoomFactorChanged?.Invoke(this, EventArgs.Empty);
        }

        private void TextEditorCore_OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            var ctrl = Window.Current.CoreWindow.GetKeyState(VirtualKey.Control);
            var alt = Window.Current.CoreWindow.GetKeyState(VirtualKey.Menu);

            if (FindAndReplacePlaceholder?.Visibility == Visibility.Visible && !ctrl.HasFlag(CoreVirtualKeyStates.Down) && !alt.HasFlag(CoreVirtualKeyStates.Down))
            {
                if (e.Key == VirtualKey.F3)
                {
                    return;
                }
            }

            var result = _keyboardCommandHandler.Handle(e);
            if (result.ShouldHandle)
            {
                e.Handled = true;
            }
        }

        private void TextEditorCore_OnTextChanging(RichEditBox textEditor, RichEditBoxTextChangingEventArgs args)
        {
            if (!args.IsContentChanging || !_loaded) return;
            if (IsModified)
            {
                IsModified = !NoChangesSinceLastSaved();
            }
            else
            {
                IsModified = !NoChangesSinceLastSaved(compareTextOnly: true);
            }
            TextChanging?.Invoke(this, EventArgs.Empty);

            GoToPlaceholder?.Dismiss();

            // Auto-update filename based on first line for unsaved documents
            TryUpdateFileNameFromFirstLine();
        }

        private void TextEditorCore_CopyTextToWindowsClipboardRequested(object sender, TextControlCopyingToClipboardEventArgs e)
        {
            CopyTextToWindowsClipboard(e);
        }

        private void TextEditorCore_CutSelectedTextToWindowsClipboardRequested(object sender, TextControlCuttingToClipboardEventArgs e)
        {
            CutSelectedTextToWindowsClipboard(e);
        }

        private void FindAndReplaceControl_OnToggleReplaceModeButtonClicked(object sender, bool showReplaceBar)
        {
            ShowFindAndReplaceControl(showReplaceBar);
        }

        public void ShowFindAndReplaceControl(bool showReplaceBar)
        {
            if (!TextEditorCore.IsEnabled || Mode != TextEditorMode.Editing)
            {
                return;
            }

            GoToPlaceholder?.Dismiss();

            if (FindAndReplacePlaceholder == null)
            {
                FindName("FindAndReplacePlaceholder"); // Lazy loading
            }

            var findAndReplace = (FindAndReplaceControl)FindAndReplacePlaceholder.Content;

            if (findAndReplace == null) return;

            FindAndReplacePlaceholder.Height = findAndReplace.GetHeight(showReplaceBar);
            findAndReplace.ShowReplaceBar(showReplaceBar);

            if (FindAndReplacePlaceholder.Visibility == Visibility.Collapsed)
            {
                FindAndReplacePlaceholder.Show();
            }

            findAndReplace.Focus(TextEditorCore.GetSearchString(), FindAndReplaceMode.FindOnly);
        }

        public void HideFindAndReplaceControl()
        {
            FindAndReplacePlaceholder?.Dismiss();
        }

        private async void FindAndReplaceControl_OnFindAndReplaceButtonClicked(object sender, FindAndReplaceEventArgs e)
        {
            TextEditorCore.Focus(FocusState.Programmatic);
            InitiateFindAndReplace(e, out bool found);

            // In case user hit "enter" key in search box instead of clicking on search button or hit F3
            // We should re-focus on FindAndReplaceControl to make the next search "flows"
            if (!(sender is Button))
            {
                if (found)
                {
                    // Wait for layout to refresh (ScrollViewer scroll to the found text) before focusing
                    await Task.Delay(10);
                }
                FindAndReplaceControl.Focus(string.Empty, e.FindAndReplaceMode);
            }
        }

        private void InitiateFindAndReplace(FindAndReplaceEventArgs findAndReplaceEventArgs, out bool found)
        {
            found = false;

            if (string.IsNullOrEmpty(findAndReplaceEventArgs.SearchContext.SearchText)) return;

            bool regexError = false;

            if (FindAndReplacePlaceholder?.Visibility == Visibility.Visible)
                _lastSearchContext = findAndReplaceEventArgs.SearchContext;

            switch (findAndReplaceEventArgs.FindAndReplaceMode)
            {
                case FindAndReplaceMode.FindOnly:
                    found = findAndReplaceEventArgs.SearchDirection == SearchDirection.Next
                        ? TextEditorCore.TryFindNextAndSelect(
                            findAndReplaceEventArgs.SearchContext,
                            stopAtEof: false,
                            out regexError)
                        : TextEditorCore.TryFindPreviousAndSelect(
                            findAndReplaceEventArgs.SearchContext,
                            stopAtBof: false,
                            out regexError);
                    break;
                case FindAndReplaceMode.Replace:
                    found = findAndReplaceEventArgs.SearchDirection == SearchDirection.Next
                        ? TextEditorCore.TryFindNextAndReplace(
                            findAndReplaceEventArgs.SearchContext,
                            findAndReplaceEventArgs.ReplaceText,
                            out regexError)
                        : TextEditorCore.TryFindPreviousAndReplace(
                            findAndReplaceEventArgs.SearchContext,
                            findAndReplaceEventArgs.ReplaceText,
                            out regexError);
                    break;
                case FindAndReplaceMode.ReplaceAll:
                    found = TextEditorCore.TryFindAndReplaceAll(
                        findAndReplaceEventArgs.SearchContext,
                        findAndReplaceEventArgs.ReplaceText,
                        out regexError);
                    break;
            }

            if (!found)
            {
                if (findAndReplaceEventArgs.SearchContext.UseRegex && regexError)
                {
                    NotificationCenter.Instance.PostNotification(_resourceLoader.GetString("FindAndReplace_NotificationMsg_InvalidRegex"), 1500);
                }
                else
                {
                    NotificationCenter.Instance.PostNotification(_resourceLoader.GetString("FindAndReplace_NotificationMsg_NotFound"), 1500);
                }
            }
        }

        private void FindAndReplacePlaceholder_Closed(object sender, InAppNotificationClosedEventArgs e)
        {
            FindAndReplacePlaceholder.Visibility = Visibility.Collapsed;
        }

        private void FindAndReplaceControl_OnDismissKeyDown(object sender, RoutedEventArgs e)
        {
            FindAndReplacePlaceholder?.Dismiss();
            TextEditorCore.Focus(FocusState.Programmatic);
        }

        public void ShowGoToControl()
        {
            if (!TextEditorCore.IsEnabled || Mode != TextEditorMode.Editing) return;

            FindAndReplacePlaceholder?.Dismiss();

            if (GoToPlaceholder == null)
                FindName("GoToPlaceholder"); // Lazy loading

            var goToControl = (GoToControl)GoToPlaceholder.Content;

            if (goToControl == null) return;

            GoToPlaceholder.Height = goToControl.GetHeight();

            if (GoToPlaceholder.Visibility == Visibility.Collapsed)
                GoToPlaceholder.Show();

            GetLineColumnSelection(out var startLine, out _, out _, out _, out _, out var lineCount);
            goToControl.SetLineData(startLine, lineCount);
            goToControl.Focus();
        }

        public void HideGoToControl()
        {
            GoToPlaceholder?.Dismiss();
        }

        private void GoToControl_OnGoToButtonClicked(object sender, GoToEventArgs e)
        {
            var found = false;

            if (int.TryParse(e.SearchLine, out var line))
            {
                found = TextEditorCore.GoTo(line);
            }

            if (!found)
            {
                GoToControl.Focus();
                NotificationCenter.Instance.PostNotification(_resourceLoader.GetString("FindAndReplace_NotificationMsg_NotFound"), 1500);
            }
            else
            {
                HideGoToControl();
                TextEditorCore.Focus(FocusState.Programmatic);
            }
        }

        private void GoToPlaceholder_Closed(object sender, InAppNotificationClosedEventArgs e)
        {
            GoToPlaceholder.Visibility = Visibility.Collapsed;
        }

        private void GoToControl_OnDismissKeyDown(object sender, RoutedEventArgs e)
        {
            GoToPlaceholder.Dismiss();
            TextEditorCore.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// Updates the FileNamePlaceholder with the first line of content for unsaved documents
        /// </summary>
        private void TryUpdateFileNameFromFirstLine()
        {
            // Only apply to unsaved documents (no EditingFile) and when auto-filename mode is enabled
            if (EditingFile != null || !_isAutoFilenameModeEnabled) return;

            // Get the default filename from resources
            var defaultFileName = _resourceLoader.GetString("TextEditor_DefaultNewFileName");
            
            // Only update if currently using the default placeholder OR
            // if the current placeholder appears to be auto-generated from first line (ends with .txt)
            bool isDefaultPlaceholder = string.Equals(FileNamePlaceholder, defaultFileName, StringComparison.OrdinalIgnoreCase);
            bool isAutoGeneratedPlaceholder = !string.IsNullOrEmpty(FileNamePlaceholder) && 
                                              FileNamePlaceholder.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                                              !string.Equals(FileNamePlaceholder, defaultFileName, StringComparison.OrdinalIgnoreCase);
            
            if (!isDefaultPlaceholder && !isAutoGeneratedPlaceholder) return;

            var content = GetText();
            
            // If content is empty, revert to default filename
            if (string.IsNullOrWhiteSpace(content))
            {
                if (!isDefaultPlaceholder)
                {
                    FileNamePlaceholder = defaultFileName;
                    FileRenamed?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            // Extract first line
            var firstLine = GetFirstLineAsFileName(content);
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                // If we can't get a valid first line but content exists, revert to default
                if (!isDefaultPlaceholder)
                {
                    FileNamePlaceholder = defaultFileName;
                    FileRenamed?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            // Create new filename with .txt extension
            var newFileName = $"{firstLine}.txt";
            
            // Only update if the filename actually changed
            if (!string.Equals(FileNamePlaceholder, newFileName, StringComparison.OrdinalIgnoreCase))
            {
                FileNamePlaceholder = newFileName;
                FileRenamed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Extracts and sanitizes the first line of content to be used as filename
        /// /// </summary>
        /// <param name="content">The text content</param>
        /// <returns>Sanitized first line suitable for filename</returns>
        private string GetFirstLineAsFileName(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return null;

            // Get first line
            var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var firstLine = lines[0]?.Trim();
            
            if (string.IsNullOrWhiteSpace(firstLine)) return null;

            // Limit length to prevent overly long filenames
            const int maxLength = 100;
            if (firstLine.Length > maxLength)
            {
                firstLine = firstLine.Substring(0, maxLength).Trim();
            }

            // Remove or replace invalid filename characters
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new StringBuilder();
            
            foreach (char c in firstLine)
            {
                if (invalidChars.Contains(c))
                {
                    // Replace common invalid chars with similar alternatives
                    switch (c)
                    {
                        case '<':
                            sanitized.Append('(');
                            break;
                        case '>':
                            sanitized.Append(')');
                            break;
                        case ':':
                            sanitized.Append('-');
                            break;
                        case '"':
                            sanitized.Append('\'');
                            break;
                        case '/':
                        case '\\':
                            sanitized.Append('-');
                            break;
                        case '|':
                            sanitized.Append('-');
                            break;
                        case '?':
                        case '*':
                            // Skip these characters
                            break;
                        default:
                            // Skip other invalid characters
                            break;
                    }
                }
                else
                {
                    sanitized.Append(c);
                }
            }

            var result = sanitized.ToString().Trim();
            
            // Ensure the result is not empty after sanitization
            if (string.IsNullOrWhiteSpace(result)) return null;

            // Remove leading/trailing dots and spaces (Windows filename restrictions)
            result = result.Trim(' ', '.');
            
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
    }
}