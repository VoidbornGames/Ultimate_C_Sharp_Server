using Microsoft.Extensions.DependencyInjection;
using System.Reflection;
using System.Runtime.Loader;
using UltimateServer.Events;
using UltimateServer.Plugins;

namespace UltimateServer.Services
{
    /// <summary>
    /// Manages the loading, unloading, and lifecycle of plugins in the application.
    /// Handles plugin discovery, instantiation, and provides isolation between plugins.
    /// </summary>
    public class PluginManager
    {
        private readonly Logger _logger;
        private readonly IEventBus _eventBus;
        public IServiceProvider _serviceProvider;

        // loaded plugin instances (pluginName -> IPlugin)
        private readonly Dictionary<string, IPlugin> _loadedPlugins = new();

        // plugin contexts (pluginName -> PluginContext)
        private readonly Dictionary<string, PluginContext> _pluginContexts = new();

        // original absolute path -> PluginLoadContext
        private readonly Dictionary<string, PluginLoadContext> _loadContexts = new();

        // original absolute path -> unique temp copy path
        private readonly Dictionary<string, string> _uniquePaths = new();

        /// <summary>
        /// Initializes a new instance of the PluginManager class.
        /// </summary>
        /// <param name="logger">Logger instance for logging plugin operations</param>
        /// <param name="eventBus">Event bus for publishing plugin-related events</param>
        /// <param name="serviceProvider">Service provider for dependency injection</param>
        public PluginManager(Logger logger, IEventBus eventBus, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _eventBus = eventBus;
            _serviceProvider = serviceProvider;
        }

        /// <summary>
        /// Scans the specified directory for plugin DLLs and loads them.
        /// Creates temporary copies of plugins to enable hot-reloading and isolation.
        /// </summary>
        /// <param name="pluginsDirectory">Directory containing plugin DLLs</param>
        public async Task LoadPluginsAsync(string pluginsDirectory = "plugins")
        {
            if (!Directory.Exists(pluginsDirectory))
            {
                _logger.Log($"🔌 Plugins directory not found at '{pluginsDirectory}'. Creating it.");
                Directory.CreateDirectory(pluginsDirectory);
                return;
            }

            var absDir = Path.GetFullPath(pluginsDirectory);
            _logger.Log($"🔌 Scanning for plugins in '{absDir}'...");

            // create a dedicated temp dir to hold the copies we will actually load
            var tempDir = Path.Combine(absDir, ".plugin_temp");
            Directory.CreateDirectory(tempDir);

            // enumerate only top-level dlls in plugins folder (not recursive)
            var dllFiles = Directory.GetFiles(absDir, "*.dll", SearchOption.TopDirectoryOnly)
                                    // skip files that are generated copies (filename ending with GUID)
                                    .Where(f => !IsGeneratedCopy(Path.GetFileNameWithoutExtension(f)))
                                    .ToList();

            if (!dllFiles.Any())
            {
                _logger.Log($"🔎 No plugin DLLs found in {absDir} (after skipping generated copies).");
                return;
            }

            foreach (var dllFile in dllFiles)
            {
                await LoadPluginFromFileAsync(dllFile, tempDir);
            }

            _logger.Log($"🔌 Plugin loading complete. {_loadedPlugins.Count} plugins loaded.");
        }

        /// <summary>
        /// Loads a plugin from a specific DLL file.
        /// Creates a unique copy to enable isolation and loads it into a separate assembly context.
        /// </summary>
        /// <param name="dllFile">Path to the plugin DLL file</param>
        /// <param name="tempDir">Temporary directory for plugin copies</param>
        private async Task LoadPluginFromFileAsync(string dllFile, string tempDir)
        {
            try
            {
                // always work with absolute path for the source file
                var absolutePath = Path.GetFullPath(dllFile);

                if (!IsValidNetAssembly(absolutePath))
                    return;

                // ensure temp dir exists
                Directory.CreateDirectory(tempDir);

                // copy original DLL into temp folder with a unique filename
                var uniqueFileName = $"{Path.GetFileNameWithoutExtension(absolutePath)}_{Guid.NewGuid()}{Path.GetExtension(absolutePath)}";
                var uniquePath = Path.Combine(tempDir, uniqueFileName);
                File.Copy(absolutePath, uniquePath, true);
                uniquePath = Path.GetFullPath(uniquePath); // guarantee absolute

                // store mapping so we can cleanup later
                _uniquePaths[absolutePath] = uniquePath;

                //_logger.Log($"🧩 Loading plugin from absolute path: {uniquePath}");

                // load into a collectible context
                var loadContext = new PluginLoadContext(uniquePath);
                var assembly = loadContext.LoadFromAssemblyPath(uniquePath);
                _loadContexts[absolutePath] = loadContext;

                var pluginTypes = assembly.GetTypes()
                    .Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                    .ToList();

                if (!pluginTypes.Any())
                {
                    _logger.LogWarning($"⚠️ No plugin types found in '{absolutePath}'");
                    SafeDelete(uniquePath);
                    _uniquePaths.Remove(absolutePath);
                    return;
                }

                foreach (var pluginType in pluginTypes)
                {
                    try
                    {
                        if (Activator.CreateInstance(pluginType) is not IPlugin plugin)
                        {
                            _logger.LogError($"❌ Failed to instantiate plugin type {pluginType.Name}.");
                            continue;
                        }

                        var context = new PluginContext(_logger, _eventBus, _serviceProvider);
                        _pluginContexts[plugin.Name] = context;

                        await plugin.OnLoadAsync(context);

                        _loadedPlugins[plugin.Name] = plugin;
                        _logger.Log($"✅ Loaded plugin: {plugin.Name} v{plugin.Version}");
                        await _eventBus.PublishAsync(new PluginLoadedEvent(plugin));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"❌ Failed to instantiate or load plugin type {pluginType.Name}: {ex.Message}");
                    }
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaderMessages = ex.LoaderExceptions?.Where(e => e != null)
                    .Select(e => e.Message)
                    .ToArray() ?? Array.Empty<string>();
                _logger.LogError($"❌ Reflection error loading '{dllFile}': {ex.Message}. Loader Exceptions: {string.Join(", ", loaderMessages)}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Failed to load plugin from '{dllFile}': {ex.Message}");
                _logger.LogError($"💡 This can happen if the file is locked, corrupted, or unsigned properly.");
            }
        }

        /// <summary>
        /// Calls the OnUpdateAsync method for all loaded plugins.
        /// Runs each plugin update in parallel without waiting for completion.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation</param>
        public async Task UpdateLoadedPluginsAsync(CancellationToken cancellationToken = default)
        {
            foreach (var pluginEntry in _loadedPlugins)
            {
                try
                {
                    var plugin = pluginEntry.Value;
                    var context = _pluginContexts[plugin.Name];
                    _ = Task.Run(async () => plugin.OnUpdateAsync(context));
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Error updating plugin {pluginEntry.Key}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Gets a read-only dictionary of all loaded plugins.
        /// </summary>
        /// <returns>Dictionary mapping plugin names to plugin instances</returns>
        public IReadOnlyDictionary<string, IPlugin> GetLoadedPlugins() => _loadedPlugins;

        /// <summary>
        /// Gets the context associated with a specific plugin.
        /// </summary>
        /// <param name="pluginName">Name of the plugin</param>
        /// <returns>PluginContext if found, null otherwise</returns>
        public PluginContext? GetPluginContext(string pluginName)
            => _pluginContexts.TryGetValue(pluginName, out var context) ? context : null;

        /// <summary>
        /// Unloads all loaded plugins and cleans up resources.
        /// Calls OnUnloadAsync for each plugin, unloads assembly contexts, and removes temporary files.
        /// </summary>
        public async Task UnloadAllPluginsAsync()
        {
            _logger.Log("🔌 Unloading all plugins...");

            // call plugin cleanup
            foreach (var plugin in _loadedPlugins.Values.ToList())
            {
                try
                {
                    await plugin.OnUnloadAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Error during OnUnloadAsync for plugin {plugin.Name}: {ex.Message}");
                }
            }

            // drop plugin references
            _loadedPlugins.Clear();
            _pluginContexts.Clear();

            // unload contexts
            foreach (var kv in _loadContexts.ToList())
            {
                try
                {
                    kv.Value.Unload();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Failed to unload PluginLoadContext for '{kv.Key}': {ex.Message}");
                }
            }
            _loadContexts.Clear();

            // force GC to let the collectible contexts unload
            for (int i = 0; i < 3; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(100);
            }

            // delete temp copies we created
            foreach (var temp in _uniquePaths.Values.ToList())
                SafeDelete(temp);
            _uniquePaths.Clear();

            // optionally remove the temp directory if empty
            try
            {
                var anyTemp = _uniquePaths.Values.FirstOrDefault();
                // already cleared; but also attempt to remove .plugin_temp if empty
                // this is best-effort; ignore errors
            }
            catch { }

            _logger.Log("🔌 All plugins unloaded.");
        }

        /// <summary>
        /// Determines if a file name represents a generated copy based on naming convention.
        /// Generated copies have a GUID appended to the filename.
        /// </summary>
        /// <param name="fileNameWithoutExtension">File name without extension</param>
        /// <returns>True if the file appears to be a generated copy</returns>
        private static bool IsGeneratedCopy(string fileNameWithoutExtension)
        {
            // Heuristic: if the last underscore-separated segment is a GUID, it's a generated copy
            // e.g. MyPlugin_3f2504e0-4f89-11d3-9a0c-0305e82c3301 -> skip
            if (string.IsNullOrEmpty(fileNameWithoutExtension))
                return false;

            var parts = fileNameWithoutExtension.Split('_');
            if (parts.Length < 2)
                return false;

            var last = parts.Last();
            return Guid.TryParse(last, out _);
        }

        /// <summary>
        /// Safely attempts to delete a file, ignoring any exceptions.
        /// </summary>
        /// <param name="path">Path to the file to delete</param>
        private static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // ignore - best-effort cleanup
            }
        }

        /// <summary>
        /// Validates that a file is a valid .NET assembly.
        /// </summary>
        /// <param name="absolutePath">Absolute path to the file to validate</param>
        /// <returns>True if the file is a valid .NET assembly</returns>
        private bool IsValidNetAssembly(string absolutePath)
        {
            try
            {
                AssemblyName.GetAssemblyName(absolutePath);
                return true;
            }
            catch (BadImageFormatException)
            {
                _logger.LogError($"❌ '{Path.GetFileName(absolutePath)}' is not a valid .NET assembly.");
                return false;
            }
            catch (FileNotFoundException ex)
            {
                _logger.LogError($"❌ File not found: {ex.FileName}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ Assembly validation failed for '{absolutePath}': {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Custom assembly load context for plugins that enables isolation and unloading.
    /// Resolves dependencies from the plugin's own directory first.
    /// </summary>
    public class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _pluginPath;

        /// <summary>
        /// Initializes a new instance of the PluginLoadContext class.
        /// </summary>
        /// <param name="pluginPath">Path to the plugin assembly</param>
        public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        {
            _pluginPath = Path.GetFullPath(pluginPath);
        }

        /// <summary>
        /// Loads an assembly given its name, first trying to resolve from the plugin directory.
        /// </summary>
        /// <param name="assemblyName">Name of the assembly to load</param>
        /// <returns>Loaded assembly or null if not found</returns>
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // attempt to resolve dependency from same directory as the plugin copy
            var dir = Path.GetDirectoryName(_pluginPath)!;
            var assemblyPath = Path.Combine(dir, $"{assemblyName.Name}.dll");
            if (File.Exists(assemblyPath))
                return LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            return null;
        }
    }
}