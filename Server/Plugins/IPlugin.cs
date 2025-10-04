using System.Threading.Tasks;

namespace UltimateServer.Plugins
{
    /// <summary>
    /// <summary>
    /// The main contract that all plugins must implement.
    /// </summary>
    public interface IPlugin
    {
        /// <summary>
        /// Gets the name of the plugin.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the version of the plugin.
        /// </summary>
        string Version { get; }

        /// <summary>
        /// This method is called by the PluginManager when the plugin is loaded.
        /// </summary>
        /// <param name="context">A context providing access to core services.</param>
        Task OnLoadAsync(IPluginContext context);

        /// <summary>
        /// This method is called by the PluginManager 20 time per second.
        /// </summary>
        /// <param name="context">A context providing access to core services.</param>
        Task OnUpdateAsync(IPluginContext context);

        /// <summary>
        /// This method is called by the PluginManager when the plugin is unloaded.
        /// </summary>
        Task OnUnloadAsync();
    }
}