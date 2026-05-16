using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;
using com.logdb.windows.collector.ui.Services;
using com.logdb.windows.collector.ui.ViewModels.Infrastructure;

namespace com.logdb.windows.collector.ui.ViewModels.Pages;

public sealed class AdvancedPageViewModel : PageViewModelBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly LocalCollectorAdminClient _adminClient;
    private readonly Action<string, bool> _statusCallback;

    private string _jsonEditorText = string.Empty;
    private string _jsonValidationMessage = "-";

    public AdvancedPageViewModel(LocalCollectorAdminClient adminClient, Action<string, bool> statusCallback)
        : base("Advanced")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;

        ResetEditorCommand = new RelayCommand(LoadCurrentIntoEditor);
        ApplyJsonCommand = new AsyncRelayCommand(ApplyJsonAsync);
    }

    public string JsonEditorText
    {
        get => _jsonEditorText;
        set => SetProperty(ref _jsonEditorText, value);
    }

    public string JsonValidationMessage
    {
        get => _jsonValidationMessage;
        set => SetProperty(ref _jsonValidationMessage, value);
    }

    public RelayCommand ResetEditorCommand { get; }
    public AsyncRelayCommand ApplyJsonCommand { get; }

    public override Task RefreshAsync()
    {
        // Auto-populate on every page entry so the editor is never empty.
        LoadCurrentIntoEditor();
        return Task.CompletedTask;
    }

    private void LoadCurrentIntoEditor()
    {
        var config = _adminClient.SnapshotWorkingConfig();
        // Blank the API key in the editor for safety. Apply preserves the live key
        // when this field is empty, so users can't accidentally wipe it.
        config.LogDB.ApiKey = string.Empty;
        JsonEditorText = JsonSerializer.Serialize(config, JsonOptions);
        JsonValidationMessage = "Loaded from current config. Edit and click Apply Changes when ready.";
    }

    private async Task ApplyJsonAsync()
    {
        if (string.IsNullOrWhiteSpace(JsonEditorText))
        {
            JsonValidationMessage = "Editor is empty. Click Reset to load the current config.";
            _statusCallback(JsonValidationMessage, false);
            return;
        }

        CollectorConfigDto? config;
        try
        {
            config = JsonSerializer.Deserialize<CollectorConfigDto>(JsonEditorText, JsonOptions);
        }
        catch (Exception ex)
        {
            JsonValidationMessage = $"Invalid JSON: {ex.Message}";
            _statusCallback(JsonValidationMessage, false);
            return;
        }

        if (config == null)
        {
            JsonValidationMessage = "JSON could not be parsed.";
            _statusCallback(JsonValidationMessage, false);
            return;
        }

        // Preserve the existing API key when the editor has it blank.
        if (string.IsNullOrWhiteSpace(config.LogDB.ApiKey))
        {
            config.LogDB.ApiKey = _adminClient.SnapshotWorkingConfig().LogDB.ApiKey;
        }

        var result = await _adminClient.ApplyConfigAsync(config);
        JsonValidationMessage = result.Message;
        _statusCallback(result.Message, result.Success);
        if (result.Success)
        {
            // Reload from the now-applied state so the editor reflects what's actually running.
            LoadCurrentIntoEditor();
            JsonValidationMessage = "Applied successfully. Editor reloaded from the new live config.";
        }
    }
}
