using System;
using System.Threading;
using System.Threading.Tasks;
using Voxta.Abstractions.Model;
using Voxta.Abstractions.Registration;
using Voxta.Abstractions.Security;
using Voxta.Common.Reporting;
using Voxta.Shared.HuggingFaceUtils;

namespace Voxta.Modules.GraphMemory.Configuration;

public class ServiceInstallationProvider(IHuggingFaceModelResolverFactory modelResolverFactory) : IServiceInstallationProvider
{
    public string[] GetPythonDependencies(ISettingsSource settings) => Array.Empty<string>();

    public Task ConfigureModuleAsync(IAuthenticationContext auth, Module module, IDeferredReporter reporter, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task InstallSharedResourcesAsync(IAuthenticationContext auth, ISettingsSource settings, IDeferredReporter reporter, CancellationToken cancellationToken)
    {
        await HuggingFaceModelResolverExtensions.TryDownloadAsync(
            modelResolverFactory.Create(auth, settings.GetRequired(ModuleConfigurationProvider.ModelsDirectory)),
            settings.GetRequired(ModuleConfigurationProvider.EmbeddingModel),
            reporter,
            cancellationToken);
    }
}
