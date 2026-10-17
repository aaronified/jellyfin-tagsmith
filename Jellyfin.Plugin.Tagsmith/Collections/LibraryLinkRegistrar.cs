using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Jellyfin.Plugin.Tagsmith.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Tagsmith.Collections;

/// <summary>
/// Gets <see cref="LibraryLinkScript"/> into jellyfin-web, through whichever of two optional
/// plugins is installed: JavaScript Injector, or File Transformation. The script is served to every
/// visitor, signed in or not.
/// </summary>
/// <remarks>
/// <para>
/// Neither can be referenced at compile time (plugins load in separate contexts), so both
/// are reached by reflection against their public <c>PluginInterface</c> classes. Without
/// either, the libraries simply keep Jellyfin's own links, which is also what happens on
/// 10.11, where those links were already right.
/// </para>
/// <para>
/// JavaScript Injector is tried first: it publishes a Jellyfin 12 build and needs no help
/// from File Transformation, which has none at the time of writing. File Transformation
/// patches <c>index.html</c> as it is served and remains the fallback for servers that have
/// it and not the injector.
/// </para>
/// <para>
/// The plugins load in no guaranteed order, so a missing one is retried for a couple of
/// minutes before giving up.
/// </para>
/// </remarks>
public sealed class LibraryLinkRegistrar : IHostedService
{
    private const string InjectorType = "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";
    private const string TransformationType = "Jellyfin.Plugin.FileTransformation.PluginInterface";
    private const int Attempts = 6;
    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(20);

    /// <summary>The File Transformation registration id, stable for the same reason.</summary>
    private static readonly Guid _registrationId = Guid.Parse("8c1d4b7e-3a52-4f69-b0e8-6d2f9a147c35");

    private readonly ILogger<LibraryLinkRegistrar> _logger;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryLinkRegistrar"/> class.
    /// </summary>
    public LibraryLinkRegistrar(ILogger<LibraryLinkRegistrar> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// A script Tagsmith adds to the web client. The id is stable, so a re-registration
    /// replaces rather than stacks.
    /// </summary>
    private sealed record WebScript(string Id, string Name, string Source, bool Enabled, Func<string, string> Inject);

    /// <summary>The argument File Transformation builds from the page contents.</summary>
    public sealed class Payload
    {
        /// <summary>Gets or sets the file's current contents.</summary>
        public string? Contents { get; set; }
    }

    /// <summary>
    /// The callback File Transformation invokes with <c>index.html</c>. Public, static and
    /// synchronous because it is bound by reflection and its return value is read as a string.
    /// </summary>
    public static string Transform(Payload payload)
    {
        var html = payload?.Contents ?? string.Empty;
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return html;
        }

        return WebScripts(configuration).Where(s => s.Enabled).Aggregate(html, (page, script) => script.Inject(page));
    }

    /// <summary>
    /// Everything Tagsmith adds to the web client, with each script's own on/off setting.
    /// </summary>
    private static WebScript[] WebScripts(PluginConfiguration? configuration) =>
    [
        new("tagsmith-library-links", "Tagsmith library links", LibraryLinkScript.Script,
            configuration?.FixLibraryLinks ?? true, LibraryLinkScript.Inject),
        new("tagsmith-user-menu", "Tagsmith user menu", UserMenuScript.Script,
            configuration?.ShowUserMenuLink ?? true, UserMenuScript.Inject)
    ];

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RegisterWithRetryAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Deliberately does not unregister. JavaScript Injector keeps the script in its own
        // saved configuration, so unregistering on every shutdown would rewrite another
        // plugin's settings twice per restart for nothing, and would race a registration
        // still in flight. The cost is that uninstalling Tagsmith leaves its script behind;
        // the README says how to remove it. The id is stable, so the next start replaces it.
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    private async Task RegisterWithRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                if (TryRegister())
                {
                    return;
                }

                if (attempt < Attempts)
                {
                    await Task.Delay(_retryDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            _logger.LogInformation(
                "Tagsmith: neither JavaScript Injector nor File Transformation is installed; the Jellyfin 12 library links and avatar-menu entry are not added");
        }
        catch (OperationCanceledException)
        {
            // Server shutting down.
        }
    }

    /// <returns>Whether a plugin was found, so the caller can stop retrying.</returns>
    private bool TryRegister()
    {
        var configuration = Plugin.Instance?.Configuration;

        try
        {
            if (Find(InjectorType, "JavaScriptInjector") is { } injector)
            {
                var plugin = Plugin.Instance;
                foreach (var script in WebScripts(configuration))
                {
                    if (!script.Enabled)
                    {
                        // Switched off: make sure an earlier registration does not linger.
                        Invoke(injector, "UnregisterScript", script.Id);
                        continue;
                    }

                    var payload = JsonSerializer.Serialize(new
                    {
                        id = script.Id,
                        name = script.Name,
                        script = script.Source,
                        enabled = true,
                        requiresAuthentication = false,
                        pluginId = plugin?.Id.ToString(),
                        pluginName = plugin?.Name ?? "Tagsmith",
                        pluginVersion = plugin?.Version.ToString()
                    });

                    if (Invoke(injector, "RegisterScript", ToJObject(injector.Assembly, payload)) is false)
                    {
                        throw new InvalidOperationException($"JavaScript Injector declined the script '{script.Id}'");
                    }

                    _logger.LogInformation("Tagsmith: registered '{Script}' (via JavaScript Injector)", script.Name);
                }

                return true;
            }

            if (Find(TransformationType, ".FileTransformation") is { } transformation)
            {
                if (!WebScripts(configuration).Any(s => s.Enabled))
                {
                    return true;
                }

                var callback = typeof(LibraryLinkRegistrar);
                var payload = JsonSerializer.Serialize(new
                {
                    id = _registrationId,
                    fileNamePattern = @"^index\.html$",
                    callbackAssembly = callback.Assembly.FullName,
                    callbackClass = callback.FullName,
                    callbackMethod = nameof(Transform)
                });

                Invoke(transformation, "RegisterTransformation", ToJObject(transformation.Assembly, payload));
                _logger.LogInformation("Tagsmith: web client scripts registered (via File Transformation)");
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The surface moved or the call threw: leave the stock links alone, once, and do
            // not retry a failure that retrying will not cure.
            _logger.LogWarning(ex, "Tagsmith: could not register the web client scripts");
            return true;
        }

        return false;
    }

    /// <summary>
    /// Finds another plugin's <c>PluginInterface</c> type by a fragment of its assembly name.
    /// </summary>
    private static Type? Find(string typeName, string assemblyFragment) =>
        AssemblyLoadContext.All
            .SelectMany(context => context.Assemblies)
            .Where(a => a.FullName?.Contains(assemblyFragment, StringComparison.Ordinal) ?? false)
            .Select(a => a.GetType(typeName))
            .FirstOrDefault(t => t is not null);

    /// <summary>
    /// Builds the <c>JObject</c> both interfaces take, using the Newtonsoft.Json the target
    /// plugin was loaded against rather than any copy of Tagsmith's own.
    /// </summary>
    private static object ToJObject(Assembly target, string json)
    {
        var context = AssemblyLoadContext.GetLoadContext(target) ?? AssemblyLoadContext.Default;
        var newtonsoft = context.Assemblies.FirstOrDefault(a => a.GetName().Name == "Newtonsoft.Json")
            ?? context.LoadFromAssemblyName(new AssemblyName("Newtonsoft.Json"));

        var jobject = newtonsoft.GetType("Newtonsoft.Json.Linq.JObject", throwOnError: true)!;
        return jobject.GetMethod("Parse", [typeof(string)])!.Invoke(null, [json])!;
    }

    /// <summary>
    /// Calls a static method on the other plugin's interface. A method that is not there
    /// throws, so a renamed or removed entry point reads as a failure and is logged as one
    /// rather than as a registration that quietly never happened.
    /// </summary>
    private static object? Invoke(Type type, string method, object argument) =>
        (type.GetMethod(method) ?? throw new MissingMethodException(type.FullName, method)).Invoke(null, [argument]);
}
