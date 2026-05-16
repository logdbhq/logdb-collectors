using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using com.logdb.windows.collector.ui.Classes;
using com.logdb.windows.collector.ui.ViewModels.Pages;

namespace com.logdb.windows.collector.ui.Views.Pages;

public partial class AdvancedPageView : UserControl
{
    private TextEditor? _editor;
    private AdvancedPageViewModel? _viewModel;
    private bool _editorInitialized;
    private bool _suppressSync;

    public AdvancedPageView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _editor = this.FindControl<TextEditor>("JsonEditor");
        InitializeEditor();
    }

    private void InitializeEditor()
    {
        if (_editor == null || _editorInitialized) return;
        _editorInitialized = true;

        _viewModel = DataContext as AdvancedPageViewModel;

        // Populate the editor BEFORE wiring Document.Changed so the initial assign
        // doesn't fire back into the VM.
        SyncEditorFromViewModel();

        _editor.Document.Changed += (_, _) => SyncTextToViewModel();
        _editor.TextArea.TextView.LineTransformers.Add(new JsonSyntaxHighlighter());
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        if (DataContext is AdvancedPageViewModel vm)
        {
            _viewModel = vm;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (_editorInitialized)
                Dispatcher.UIThread.Post(SyncEditorFromViewModel);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AdvancedPageViewModel.JsonEditorText))
        {
            Dispatcher.UIThread.Post(SyncEditorFromViewModel);
        }
    }

    private void SyncTextToViewModel()
    {
        if (_suppressSync || _editor == null || _viewModel == null) return;
        _suppressSync = true;
        try
        {
            _viewModel.JsonEditorText = _editor.Text ?? string.Empty;
        }
        finally
        {
            _suppressSync = false;
        }
    }

    private void SyncEditorFromViewModel()
    {
        if (_suppressSync || _editor == null || _viewModel == null) return;
        var vmText = _viewModel.JsonEditorText ?? string.Empty;
        if (_editor.Text == vmText) return;

        _suppressSync = true;
        try
        {
            _editor.Text = vmText;
        }
        finally
        {
            _suppressSync = false;
        }
    }
}
