using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.Mediux.Web;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Mediux.Services;

/// <summary>
/// Registers MediUX web injections with FileTransformation after Jellyfin startup.
/// </summary>
public class FileTransformationStartupService : IScheduledTask
{
    private static readonly Guid IndexTransformationId = new("c8e4f2a1-9b3d-4e6f-a1c2-7d8e9f0a1b2c");

    private readonly ILogger<FileTransformationStartupService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileTransformationStartupService"/> class.
    /// </summary>
    public FileTransformationStartupService(ILogger<FileTransformationStartupService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "MediUX FileTransformation Startup";

    /// <inheritdoc />
    public string Key => "MediuxFileTransformationStartup";

    /// <inheritdoc />
    public string Description => "Registers MediUX set browser injection with the FileTransformation plugin.";

    /// <inheritdoc />
    public string Category => "Startup Services";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        RegisterFileTransformation();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
#if NET8_0
            Type = TaskTriggerInfo.TriggerStartup
#else
            Type = TaskTriggerInfoType.StartupTrigger
#endif
        };
    }

    private void RegisterFileTransformation()
    {
        try
        {
            var ftAssembly = AssemblyLoadContext.All
                .SelectMany(ctx => ctx.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation") ?? false);

            if (ftAssembly is null)
            {
                _logger.LogWarning(
                    "MediUX: FileTransformation plugin not found. Set browser injection is unavailable. " +
                    "Install FileTransformation from https://www.iamparadox.dev/jellyfin/plugins/manifest.json to enable it.");
                return;
            }

            var pluginInterfaceType = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
            if (pluginInterfaceType is null)
            {
                _logger.LogWarning("MediUX: FileTransformation PluginInterface type not found");
                return;
            }

            var registerMethod = pluginInterfaceType.GetMethod("RegisterTransformation", BindingFlags.Static | BindingFlags.Public);
            if (registerMethod is null)
            {
                _logger.LogWarning("MediUX: FileTransformation RegisterTransformation method not found");
                return;
            }

            var payloadType = registerMethod.GetParameters()[0].ParameterType;

            RegisterTransformation(registerMethod, payloadType, IndexTransformationId, "index.html", typeof(IndexTransformation));

            _logger.LogInformation("MediUX: Registered index.html transformation with FileTransformation plugin");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MediUX: Failed to register FileTransformation — web injections unavailable");
        }
    }

    private void RegisterTransformation(MethodInfo registerMethod, Type payloadType, Guid transformationId, string fileNamePattern, Type callbackClass)
    {
        var payload = CreateRegistrationPayload(payloadType, transformationId, fileNamePattern, callbackClass);
        registerMethod.Invoke(null, [payload]);
    }

    private object CreateRegistrationPayload(Type payloadType, Guid transformationId, string fileNamePattern, Type callbackClass)
    {
        var payload = Activator.CreateInstance(payloadType)
            ?? throw new InvalidOperationException("Could not create FileTransformation registration payload.");

        var indexer = payloadType.GetProperty("Item", [typeof(string)])
            ?? throw new InvalidOperationException("FileTransformation payload type has no string indexer.");

        var jValueType = Type.GetType("Newtonsoft.Json.Linq.JValue, Newtonsoft.Json")
            ?? throw new InvalidOperationException("Newtonsoft.Json.Linq.JValue type not found.");

        void Set(string key, string value)
        {
            var jValue = Activator.CreateInstance(jValueType, value)
                ?? throw new InvalidOperationException("Could not create JValue for key " + key);
            indexer.SetValue(payload, jValue, [key]);
        }

        Set("id", transformationId.ToString());
        Set("fileNamePattern", fileNamePattern);
        Set("callbackAssembly", GetType().Assembly.FullName!);
        Set("callbackClass", callbackClass.FullName!);
        Set("callbackMethod", "Transform");

        return payload;
    }
}
