using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Voxta.Abstractions.Modules;
using Voxta.Abstractions.Registration;
using Voxta.Model.Shared;
using Voxta.Modules.GraphMemory.Configuration;
using Voxta.Modules.GraphMemory.Memory;
using Voxta.Shared.HuggingFaceUtils;

namespace Voxta.Modules.GraphMemory;

[UsedImplicitly]
public class VoxtaModule : IVoxtaModule
{
    public const string ServiceName = "GraphMemory";

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
            },
            Pricing = ServiceDefinitionPricing.Free,
            Hosting = ServiceDefinitionHosting.Builtin,
            SupportsExplicitContent = true,
            Recommended = false,
            Notes = "Graph-backed memory provider (scaffold).",
            HelpLink = "https://doc.voxta.ai/",
            ModuleConfigurationProviderType = typeof(ModuleConfigurationProvider),
            ModuleConfigurationFieldsRequiringReload = ModuleConfigurationProvider.FieldsRequiringReload,
            ModuleInstallationProviderType = typeof(Configuration.ServiceInstallationProvider),
        });

        builder.Services.AddHuggingFaceModelDownloader();
        builder.AddMemoryProviderService<GraphMemoryProviderService>(ServiceName);
    }
}
