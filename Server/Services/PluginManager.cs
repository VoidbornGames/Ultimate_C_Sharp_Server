using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using UltimateServer.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace UltimateServer.Services
{
    public class PluginManager
    {
        private readonly Logger _logger;
        private readonly IEventBus _eventBus;
        private readonly IServiceProvider _serviceProvider;

        // loaded plugin instances (pluginName -> IPlugin)
        private readonly Dictionary<string, IPlugin> _loadedPlugins = new();

        // plugin contexts (pluginName -> PluginContext)
        private readonly Dictionary<string, PluginContext> _pluginContexts = new();

        // original absolute path -> PluginLoadContext
        private readonly Dictionary<string, PluginLoadContext> _loadContexts = new();

        // original absolute path -> unique temp copy path
        private readonly Dictionary<string, string> _uniquePaths = new();

        public PluginManager(Logger logger, IEventBus eventBus, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _eventBus = eventBus;
            _serviceProvider = serviceProvider;
        }

        public async Task LoadPluginsAsync(string pluginsDirectory)
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

        public async Task UpdateLoadedPluginsAsync(CancellationToken cancellationToken = default)
        {
            foreach (var pluginEntry in _loadedPlugins)
            {
                try
                {
                    var plugin = pluginEntry.Value;
                    var context = _pluginContexts[plugin.Name];
                    await plugin.OnUpdateAsync(context);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ Error updating plugin {pluginEntry.Key}: {ex.Message}");
                }
            }
        }

        public IReadOnlyDictionary<string, IPlugin> GetLoadedPlugins() => _loadedPlugins;

        public PluginContext? GetPluginContext(string pluginName)
            => _pluginContexts.TryGetValue(pluginName, out var context) ? context : null;

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

    public class PluginLoadContext : AssemblyLoadContext
    {
        private readonly string _pluginPath;

        public PluginLoadContext(string pluginPath) : base(isCollectible: true)
        {
            _pluginPath = Path.GetFullPath(pluginPath);
        }

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
