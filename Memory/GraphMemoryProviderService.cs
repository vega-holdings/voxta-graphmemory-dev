using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.Memory;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.GraphMemory.Configuration;
using Voxta.Abstractions.DependencyInjection;
using Voxta.Abstractions.Services.TextGen;

namespace Voxta.Modules.GraphMemory.Memory;

public class GraphMemoryProviderService(
    ILogger<GraphMemoryProviderService> logger,
    IDynamicServiceAccessor<ITextGenService> textGenAccessor
) : ServiceBase(logger), IMemoryProviderService, IAsyncDisposable
{
    private readonly IDynamicServiceAccessor<ITextGenService> _textGenAccessor = textGenAccessor;

    public Task WarmupAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<IMemoryProviderInstance> CreateAsync(CancellationToken cancellationToken)
    {
        var settings = new GraphMemorySettings
        {
            GraphPath = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.GraphPath),
            EmbeddingModel = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.EmbeddingModel),
            ModelsDirectory = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ModelsDirectory),
            ExtractionPromptPath = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.ExtractionPromptPath),
            GraphExtractionPromptPath = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.GraphExtractionPromptPath),
            EnablePlaceholderExtraction = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.EnablePlaceholderExtraction),
            EnableGraphExtraction = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.EnableGraphExtraction),
            GraphExtractionTrigger = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.GraphExtractionTrigger),
            PrefillMemoryWindow = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.PrefillMemoryWindow),
            MaxMemoryWindowEntries = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.MaxMemoryWindowEntries),
            ExpireMemoriesAfter = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.ExpireMemoriesAfter),
            MaxQueryResults = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.MaxQueryResults),
            MinScore = ModuleConfiguration.GetOptional((FormNumberFieldBase<double>)ModuleConfigurationProvider.MinScore) ?? 0,
            MaxHops = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.MaxHops),
            NeighborLimit = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.NeighborLimit),
            DeterministicOnly = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.DeterministicOnly),
        };

        GraphMemoryRuntime.Initialize(settings.GraphPath);

        IMemoryProviderInstance instance = new GraphMemoryProviderInstance(logger, settings, _textGenAccessor);
        return Task.FromResult(instance);
    }

    public override ValueTask DisposeAsync() => base.DisposeAsync();
}
