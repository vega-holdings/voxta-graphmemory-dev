using Microsoft.Extensions.Logging;
using Voxta.Abstractions.Services;
using Voxta.Abstractions.Services.Memory;
using Voxta.Model.Shared.Forms;
using Voxta.Modules.GraphMemory.Configuration;

namespace Voxta.Modules.GraphMemory.Memory;

public class GraphMemoryProviderService(
    ILogger<GraphMemoryProviderService> logger
) : ServiceBase(logger), IMemoryProviderService, IAsyncDisposable
{
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
            PrefillMemoryWindow = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.PrefillMemoryWindow),
            MaxMemoryWindowEntries = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.MaxMemoryWindowEntries),
            ExpireMemoriesAfter = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.ExpireMemoriesAfter),
            MaxQueryResults = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.MaxQueryResults),
            MinScore = ModuleConfiguration.GetOptional((FormNumberFieldBase<double>)ModuleConfigurationProvider.MinScore) ?? 0,
            MaxHops = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.MaxHops),
            NeighborLimit = ModuleConfiguration.GetRequired((FormNumberFieldBase<int>)ModuleConfigurationProvider.NeighborLimit),
            DeterministicOnly = ModuleConfiguration.GetRequired(ModuleConfigurationProvider.DeterministicOnly),
        };

        IMemoryProviderInstance instance = new GraphMemoryProviderInstance(logger, settings);
        return Task.FromResult(instance);
    }

    public override ValueTask DisposeAsync() => base.DisposeAsync();
}
