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

    private string _effectiveRedactedJson = string.Empty;
    private string _jsonEditorText = string.Empty;
    private string _jsonValidationMessage = "-";

    public AdvancedPageViewModel(LocalCollectorAdminClient adminClient, Action<string, bool> statusCallback)
        : base("Advanced")
    {
        _adminClient = adminClient;
        _statusCallback = statusCallback;

        RefreshEffectiveConfigCommand = new AsyncRelayCommand(RefreshEffectiveAsync);
        LoadCurrentIntoEditorCommand = new RelayCommand(LoadCurrentIntoEditor);
        ApplyJsonCommand = new AsyncRelayCommand(ApplyJsonAsync);
    }

    public string EffectiveRedactedJson
    {
        get => _effectiveRedactedJson;
        set => SetProperty(ref _effectiveRedactedJson, value);
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

    public AsyncRelayCommand RefreshEffectiveConfigCommand { get; }
    public RelayCommand LoadCurrentIntoEditorCommand { get; }
    public AsyncRelayCommand ApplyJsonCommand { get; }

    public override async Task RefreshAsync()
    {
        await RefreshEffectiveAsync();
    }

    private async Task RefreshEffectiveAsync()
    {
        var redacted = await _adminClient.GetEffectiveRedactedConfigAsync()
                       ?? _adminClient.SnapshotWorkingConfig();
        EffectiveRedactedJson = JsonSerializer.Serialize(redacted, JsonOptions);
        JsonValidationMessage = "Effective redacted configuration loaded.";
    }

    private void LoadCurrentIntoEditor()
    {
        var config = _adminClient.SnapshotWorkingConfig();
        config.LogDB.ApiKey = string.Empty;
        JsonEditorText = JsonSerializer.Serialize(config, JsonOptions);
        JsonValidationMessage = "Editor loaded from current config. API key is intentionally blank.";
    }

    private async Task ApplyJsonAsync()
    {
        if (string.IsNullOrWhiteSpace(JsonEditorText))
        {
            JsonValidationMessage = "JSON editor is empty.";
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

        var result = await _adminClient.ApplyConfigAsync(config);
        JsonValidationMessage = result.Message;
        _statusCallback(result.Message, result.Success);
        if (result.Success)
        {
            await RefreshEffectiveAsync();
        }
    }
}
