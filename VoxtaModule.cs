using JetBrains.Annotations;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.DependencyInjection;
using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Model.Shared;
using Voxta.Modules.GraphMemory.Configuration;
using Voxta.Modules.GraphMemory.Controllers;
using Voxta.Modules.GraphMemory.Memory;
using Voxta.Modules.GraphMemory.Services;
using Voxta.Shared.HuggingFaceUtils;

namespace Voxta.Modules.GraphMemory;

[UsedImplicitly]
public class VoxtaModule : IVoxtaModule
{
    public const string ServiceName = "GraphMemory";
    public const string AugmentationKey = "graph_memory";

    public void Configure(IVoxtaModuleBuilder builder)
    {
        builder.Register(new ModuleDefinition
        {
            ServiceName = ServiceName,
            Label = "Graph Memory Provider (alpha)",
            Experimental = true,
            Single = true,
            CanBeInstalledByAdminsOnly = false,
            Supports = new()
            {
                { ServiceTypes.Memory, ServiceDefinitionCategoryScore.High },
                { ServiceTypes.ChatAugmentations, ServiceDefinitionCategoryScore.Low },
            },
            Pricing = ServiceDefinitionPricing.Free,
            Hosting = ServiceDefinitionHosting.Builtin,
            SupportsExplicitContent = true,
            Recommended = false,
            Augmentations = [AugmentationKey],
            Notes = "Graph-backed memory provider (scaffold).",
            HelpLink = "/manage/graph-memory",
            ModuleConfigurationProviderType = typeof(ModuleConfigurationProvider),
            ModuleConfigurationFieldsRequiringReload = ModuleConfigurationProvider.FieldsRequiringReload,
            ModuleInstallationProviderType = typeof(Configuration.ServiceInstallationProvider),
        });

        builder.Services.AddHuggingFaceModelDownloader();
        builder.AddMemoryProviderService<GraphMemoryProviderService>(ServiceName);
        builder.AddChatAugmentationsService<GraphMemoryChatAugmentationsService>(ServiceName);

        builder.Services.AddControllers().PartManager.ApplicationParts.Add(new AssemblyPart(typeof(GraphMemoryApiController).Assembly));
    }
}
