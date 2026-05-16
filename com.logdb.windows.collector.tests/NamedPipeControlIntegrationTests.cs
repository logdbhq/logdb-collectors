using System.Text.Json;
using com.logdb.windows.collector.shared.Contracts;

namespace com.logdb.windows.collector.tests;

[CollectionDefinition(CollectionName)]
public sealed class CollectorIntegrationCollection : ICollectionFixture<CollectorIntegrationFixture>
{
    public const string CollectionName = "CollectorIntegration";
}

[Collection(CollectorIntegrationCollection.CollectionName)]
public sealed class NamedPipeControlIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CollectorIntegrationFixture _fixture;

    public NamedPipeControlIntegrationTests(CollectorIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Status_Command_ReturnsCollectorStatusPayload()
    {
        var response = await _fixture.SendAsync(ControlCommands.GetStatus);

        Assert.True(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.PayloadJson));

        var payload = JsonSerializer.Deserialize<CollectorStatusDto>(response.PayloadJson!, JsonOptions);
        Assert.NotNull(payload);
        Assert.NotEmpty(payload!.Modules);
    }

    [Fact]
    public async Task Diagnostics_Command_ReturnsPayload()
    {
        var response = await _fixture.SendAsync(ControlCommands.GetDiagnostics, "20");

        Assert.True(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.PayloadJson));

        var payload = JsonSerializer.Deserialize<List<DiagnosticEntryDto>>(response.PayloadJson!, JsonOptions);
        Assert.NotNull(payload);
    }

    [Fact]
    public async Task Unknown_Command_ReturnsFailure()
    {
        var response = await _fixture.SendAsync("invalid-command");

        Assert.False(response.Success);
        Assert.Contains("Unknown command", response.Message ?? string.Empty);
    }

    [Fact]
    public async Task ValidateIisPaths_Command_ReturnsTypedPayload()
    {
        var response = await _fixture.SendAsync(ControlCommands.ValidateIisPaths);

        Assert.True(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.PayloadJson));

        var payload = JsonSerializer.Deserialize<ValidationResultDto>(response.PayloadJson!, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal("NO_PATHS", payload!.Code);
    }

    [Fact]
    public async Task PreviewMetrics_Command_ReturnsPreviewPayload()
    {
        var response = await _fixture.SendAsync(ControlCommands.PreviewMetrics, "{\"max\":20}");

        Assert.True(response.Success);
        Assert.False(string.IsNullOrWhiteSpace(response.PayloadJson));

        var payload = JsonSerializer.Deserialize<PreviewResultDto<MetricPreviewRowDto>>(response.PayloadJson!, JsonOptions);
        Assert.NotNull(payload);
        Assert.NotEmpty(payload!.Rows);
    }
}
