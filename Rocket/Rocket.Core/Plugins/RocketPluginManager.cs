using Rocket.API;
using Rocket.Core.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using System.Linq;
using System.Runtime.InteropServices;
using Rocket.Core.Extensions;


namespace Rocket.Core.Plugins
{
    public sealed class RocketPluginManager : MonoBehaviour
    {
        public delegate void PluginsLoaded();

        public event PluginsLoaded OnPluginsLoaded;

        [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr mono_hr_create_domain(string name);

        [DllImport("mono-2.0-bdwgc.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int mono_hr_unload_domain(IntPtr handle);

        private static Dictionary<string, (IntPtr, GameObject)> pluginDomains = new();

        //private static List<Assembly> pluginAssemblies;
        
        internal static List<IRocketPlugin> Plugins
        {
            get { return pluginDomains.Values.Select(g => g.Item2.GetComponent<IRocketPlugin>()).Where(p => p != null).ToList(); }
        }

        /// <summary>
        /// Maps assembly name to .dll file path.
        /// </summary>
        private static Dictionary<AssemblyName, string> libraries = new();

        public List<IRocketPlugin> GetPlugins()
        {
            return Plugins;
        }

        public IRocketPlugin GetPlugin(Assembly assembly)
        {
            return pluginDomains.Values.Select(g => g.Item2.GetComponent<IRocketPlugin>())
                .FirstOrDefault(p => p != null && p.GetType().Assembly == assembly);
        }

        public IRocketPlugin GetPlugin(string name)
        {
            return pluginDomains.Values.Select(g => g.Item2.GetComponent<IRocketPlugin>())
                .FirstOrDefault(p => p != null && ((IRocketPlugin)p).Name == name);
        }

        public IRocketPlugin ForceLoadPlugin(string path)
        {
            Assembly assembly = LoadAssemblyFromPath(path);
            if (assembly == null) return null;
            
            List<Type> pluginImplementations = RocketHelper.GetTypesFromInterface(assembly, "IRocketPlugin");
            foreach (Type pluginType in pluginImplementations)
            {
                GameObject plugin = new(pluginType.Name, pluginType);
                DontDestroyOnLoad(plugin);
                string pluginName = pluginType.Assembly.GetName().Name;
                (IntPtr, GameObject) domain = pluginDomains[pluginName];
                domain.Item2 = plugin;
                pluginDomains[pluginName] = domain;
            }

            return GetPlugin(assembly.GetName().Name);
        }

        public void ManageReload(Assembly assembly)
        {
            UnloadPlugin(assembly);
            
            string pluginName = assembly.GetName().Name;
            
            string pluginPath = libraries.FirstOrDefault(c => c.Key.Name == pluginName).Value;
            _ = ForceLoadPlugin(pluginPath);
        }

        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                AssemblyName requestedName = new(args.Name);
                if (pluginDomains.ContainsKey(requestedName.Name))
                    return null;

                var bestMatch = libraries
                    .FirstOrDefault(lib => string.Equals(lib.Key.Name, requestedName.Name) && lib.Key.Version >= requestedName.Version);

                if (!string.IsNullOrEmpty(bestMatch.Value))
                {
                    return Assembly.Load(File.ReadAllBytes(bestMatch.Value));
                }
            }
            catch (Exception ex)
            {
                Logging.Logger.LogException(ex, "Caught exception resolving dependency: " + args.Name);
            }

            Logging.Logger.LogError("Could not find dependency: " + args.Name);
            return null;
        }

        private void Awake()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            SDG.Framework.Modules.ModuleHook.PreVanillaAssemblyResolvePostRedirects += OnAssemblyResolve;
        }

        private void Start()
        {
            loadPlugins();
        }

        public Type GetMainTypeFromAssembly(Assembly assembly)
        {
            return RocketHelper.GetTypesFromInterface(assembly, "IRocketPlugin").FirstOrDefault();
        }

        private void loadPlugins()
        {
            libraries = FindAssembliesInDirectory(Environment.LibrariesDirectory);

            foreach (var pair in FindAssembliesInDirectory(Environment.PluginsDirectory))
            {
                if (!libraries.ContainsKey(pair.Key))
                    libraries.Add(pair.Key, pair.Value);
            }

            List<Assembly> pluginAssemblies = LoadAssembliesFromDirectory(Environment.PluginsDirectory);

            List<Type> pluginImplementations = RocketHelper.GetTypesFromInterface(pluginAssemblies, "IRocketPlugin");
            foreach (Type pluginType in pluginImplementations)
            {
                AssemblyName assemblyName = pluginType.Assembly.GetName();
                
                GameObject plugin = new(pluginType.Name, pluginType);
                DontDestroyOnLoad(plugin);
                string pluginName = assemblyName.Name;
                // If the plugin is missing a lib, it will be removed from pluginDomains so we can just skip it
                if (!pluginDomains.TryGetValue(pluginName, out (IntPtr, GameObject) domain)) continue;
                domain.Item2 = plugin;
                pluginDomains[pluginName] = domain;
            }
            OnPluginsLoaded.TryInvoke();
        }

        private void unloadPlugins()
        {
            foreach (var kv in pluginDomains)
            {
                try
                {
                    Logging.Logger.LogWarning($"Unloading {kv.Key} domain and game object");

                    if (kv.Value.Item2 != null)
                    {
                        Destroy(kv.Value.Item2);
                    }

                    if (kv.Value.Item1 != IntPtr.Zero)
                    {
                        mono_hr_unload_domain(kv.Value.Item1);    
                    }
                }
                catch (Exception e)
                {
                    Logging.Logger.LogError(e, "Failed to unload plugin domain: " + kv.Key);
                }
            }

            pluginDomains.Clear();
        }

        internal void UnloadPlugin(Assembly assembly)
        {
            AssemblyName assemblyName = assembly.GetName();
            libraries.Remove(assemblyName);
            string pluginName = assemblyName.Name;

            if (!pluginDomains.TryGetValue(pluginName, out (IntPtr, GameObject) items)) return;
            
            try
            {
                if (items.Item2 is not null)
                {
                    Destroy(items.Item2);    
                }
                if (items.Item1 != IntPtr.Zero)
                {
                    mono_hr_unload_domain(items.Item1);
                }

                pluginDomains.Remove(pluginName);
            }
            catch (Exception e)
            {
                Logging.Logger.LogError(e, "Failed to unload plugin domain: " + pluginName);
            }
        }

        internal void Reload()
        {
            unloadPlugins();
            loadPlugins();
        }

        /// <summary>
        /// Replacement for GetAssembliesFromDirectory using AssemblyName as key rather than string.
        /// </summary>
        private static Dictionary<AssemblyName, string> FindAssembliesInDirectory(string directory)
        {
            Dictionary<AssemblyName, string> l = new();
            IEnumerable<FileInfo> libraries = new DirectoryInfo(directory).GetFiles("*.dll", SearchOption.AllDirectories);
            foreach (FileInfo library in libraries)
            {
                try
                {
                    AssemblyName name = AssemblyName.GetAssemblyName(library.FullName);
                    l.Add(name, library.FullName);
                }
                catch
                {
                }
            }

            return l;
        }

        private static bool ShouldSkipPreload(AssemblyName an)
        {
            // Skip domain-neutral/BCL/Unity assemblies
            string n = an.Name;
            if (string.Equals(n, "mscorlib", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(n, "netstandard", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("System", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)) return true; // UnityEngine.*, Unity.*
            return false;
        }

        private static Assembly TryLoadIntoDomain(IntPtr domain, string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                return Icalls.mono_hr_load_plugin(domain, bytes);
            }
            catch
            {
                return null;
            }
        }

        private static void PreloadDomainDependencies(
            IntPtr domain,
            IEnumerable<string> candidateDllPaths)
        {
            HashSet<string> loaded = new(StringComparer.OrdinalIgnoreCase);

            foreach (string p in candidateDllPaths)
            {
                AssemblyName an;
                try
                {
                    an = AssemblyName.GetAssemblyName(p);
                }
                catch
                {
                    continue;
                } // not a managed assembly

                if (ShouldSkipPreload(an)) continue;
                if (loaded.Contains(an.Name)) continue;

                Assembly dep = TryLoadIntoDomain(domain, p);
                if (dep != null)
                    loaded.Add(an.Name);
            }
        }

        public static List<Assembly> LoadAssembliesFromDirectory(string directory, string extension = "*.dll")
        {
            List<Assembly> assemblies = [];
            IEnumerable<FileInfo> pluginFiles = new DirectoryInfo(directory).GetFiles(extension, SearchOption.AllDirectories);

            foreach (FileInfo plugin in pluginFiles)
            {
                Assembly assembly = LoadAssemblyFromPath(plugin.FullName);
                if(assembly == null) continue;
                assemblies.Add(assembly);
            }

            return assemblies;
        }

        private static Assembly LoadAssemblyFromPath(string fullPath)
        {
            AssemblyName pluginAssemblyName;
            try
            {
                pluginAssemblyName = AssemblyName.GetAssemblyName(fullPath);
            }
            catch
            {
                return null;
            }

            string pluginName = pluginAssemblyName.Name;
            if (!pluginDomains.TryGetValue(pluginName, out (IntPtr, GameObject) items) || items.Item1 == IntPtr.Zero)
            {
                items.Item1 = mono_hr_create_domain($"Plugin:{pluginName}");
                if (items.Item1 == IntPtr.Zero)
                {
                    Logging.Logger.LogError($"Failed to create AppDomain for plugin {pluginName}");
                    return null;
                }

                pluginDomains[pluginName] = (items.Item1, null);
            }

            try
            {
                // 1) Preload shared libs into this domain (e.g., Rocket.Core/API)
                // 'libraries' should already be populated from Environment.LibrariesDirectory
                PreloadDomainDependencies(items.Item1, libraries.Values);

                Logging.Logger.Log("Domain dependencies loaded, loading plugin...");
                
                // 2) Load the plugin itself
                Assembly assembly = TryLoadIntoDomain(items.Item1, fullPath);
                if (assembly == null)
                {
                    Logging.Logger.LogError($"mono_hr_load_plugin returned null for {pluginName}");
                    return null;
                }

                List<Type> types = RocketHelper.GetTypesFromInterface(assembly, "IRocketPlugin").FindAll(x => !x.IsAbstract);
                if (types.Count == 1)
                {
                    Logging.Logger.Log($"Loaded {assembly.GetName().Name} into its own domain from {fullPath}");
                    return assembly;
                }

                Logging.Logger.LogError("Invalid or outdated plugin assembly: " + assembly.GetName().Name);
            }
            catch (BadImageFormatException)
            {
                // Not a managed assembly; ignore
            }
            catch (Exception ex)
            {
                Logging.Logger.LogError(ex, "Could not load plugin assembly: " + pluginName);
            }

            return null;
        }
    }
}