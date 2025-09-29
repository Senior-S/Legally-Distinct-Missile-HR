using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Rocket.API;
using Rocket.Core.Extensions;
using Rocket.Core.Utils;
using SDG.Framework.Modules;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace Rocket.Core.Plugins
{
    public sealed class RocketPluginManager : MonoBehaviour
    {
        public delegate void PluginsLoaded();

        public event PluginsLoaded OnPluginsLoaded;

        // plugin simple name -> (domain handle, GameObject)
        private static readonly Dictionary<string, (IntPtr, GameObject)> pluginDomains = new();

        // Maps assembly name to .dll file path.
        private static Dictionary<AssemblyName, (string Path, bool IsPlugin)> libraries = new();

        internal static List<IRocketPlugin> Plugins
        {
            get
            {
                return pluginDomains.Values
                    .Select(g => g.Item2 != null ? g.Item2.GetComponent<IRocketPlugin>() : null)
                    .Where(p => p != null)
                    .ToList();
            }
        }

        public List<IRocketPlugin> GetPlugins()
        {
            return Plugins;
        }

        public IRocketPlugin GetPlugin(Assembly assembly)
        {
            return pluginDomains.Values
                .Select(g => g.Item2 != null ? g.Item2.GetComponent<IRocketPlugin>() : null)
                .FirstOrDefault(p => p != null && p.GetType().Assembly == assembly);
        }

        public IRocketPlugin GetPlugin(string name)
        {
            return pluginDomains.Values
                .Select(g => g.Item2 != null ? g.Item2.GetComponent<IRocketPlugin>() : null)
                .FirstOrDefault(p => p != null && p.Name == name);
        }

        public IRocketPlugin ForceLoadPlugin(string path)
        {
            string directoryName = Path.GetDirectoryName(path)!.TrimEnd('/', '\\');
            if (directoryName.EndsWith(Environment.LibrariesDirectory)) return null;

            Assembly assembly = LoadAssemblyFromPath(path);
            if (assembly == null)
                return null;

            LoadPluginGameObject(assembly);

            return GetPlugin(assembly.GetName().Name);
        }

        public void ManageReload(Assembly assembly)
        {
            AssemblyName assemblyName = assembly.GetName();
            UnloadPlugin(assemblyName);

            string pluginName = assemblyName.Name;
            string pluginPath = libraries.FirstOrDefault(c => c.Key.Name == pluginName).Value.Path;

            _ = ForceLoadPlugin(pluginPath);
        }

        private static Assembly FindAlreadyLoaded(string simpleName)
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a =>
                {
                    AssemblyName assemblyName = a.GetName();
                    return string.Equals(assemblyName.Name, simpleName, StringComparison.OrdinalIgnoreCase);
                });
        }
        
        private static bool IsPluginName(string simpleName)
        {
            foreach (var kv in libraries)
                if (string.Equals(kv.Key.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return kv.Value.IsPlugin;
            return false;
        }

        private static string FindPathBySimpleName(string simpleName)
        {
            foreach (var kv in libraries)
                if (string.Equals(kv.Key.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return kv.Value.Path;
            return null;
        }

        private Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                AssemblyName requested = new(args.Name);
                string simple = requested.Name;

                // If already loaded in this AppDomain, return it.
                Assembly already = FindAlreadyLoaded(simple);
                if (already != null)
                    return already;

                // Decide based on current AppDomain. Unity runs plugins in the root domain.
                bool inRoot = AppDomain.CurrentDomain.IsDefaultAppDomain();

                // If request is for a plugin assembly:
                if (IsPluginName(simple))
                {
                    if (inRoot)
                    {
                        // Allow loading plugin assemblies into root domain so MonoBehaviours can run.
                        string path = FindPathBySimpleName(simple);
                        if (!string.IsNullOrEmpty(path))
                            return Assembly.Load(File.ReadAllBytes(path));
                        // fall through to error
                    }
                    else
                    {
                        // In plugin AppDomain: don't load via root resolver; plugin DLLs are loaded by your icall.
                        return null;
                    }
                }

                // Non-plugin libs or anything else: pick best version from libraries
                var bestMatch = libraries
                    .OrderByDescending(c => c.Key.Version)
                    .FirstOrDefault(lib =>
                        string.Equals(lib.Key.Name, simple, StringComparison.OrdinalIgnoreCase) &&
                        lib.Key.Version >= requested.Version);

                if (!string.IsNullOrEmpty(bestMatch.Value.Path))
                    return Assembly.Load(File.ReadAllBytes(bestMatch.Value.Path));
            }
            catch (Exception ex)
            {
                Logger.LogException(ex, "Caught exception resolving dependency: " + args.Name);
            }

            Logger.LogError("Could not find dependency: " + args.Name);
            return null;
        }
        
        private void Awake()
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            ModuleHook.PreVanillaAssemblyResolvePostRedirects += OnAssemblyResolve;
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

            foreach (var pair in FindAssembliesInDirectory(Environment.PluginsDirectory)) // Required to avoid issues
            {
                if (!libraries.Any(c => c.Key.Name.Equals(pair.Key.Name, StringComparison.OrdinalIgnoreCase)))
                    libraries.Add(pair.Key, (pair.Value.Item1, true));
                else
                {
                    var match = libraries.FirstOrDefault(c => c.Key.Name.Equals(pair.Key.Name, StringComparison.OrdinalIgnoreCase));
                    libraries[match.Key] = (libraries[match.Key].Path, true);
                }
            }

            List<Assembly> pluginAssemblies = LoadAssembliesFromDirectory(Environment.PluginsDirectory);

            pluginAssemblies.ForEach(LoadPluginGameObject);

            OnPluginsLoaded.TryInvoke();
        }

        private static void LoadPluginGameObject(Assembly assembly)
        {
            List<Type> pluginImplementations = RocketHelper.GetTypesFromInterface(assembly, "IRocketPlugin");

            foreach (Type pluginType in pluginImplementations)
            {
                AssemblyName assemblyName = pluginType.Assembly.GetName();

                // Ensure any dependency plugins are loaded/instantiated first
                EnsurePluginGameObjectsForDependencies(pluginType.Assembly);

                Logger.Log($"[loadPlugins] {assemblyName.Name} domain: {pluginDomains[assemblyName.Name].Item1}");

                GameObject plugin = new(pluginType.Name, pluginType);
                DontDestroyOnLoad(plugin);

                string pluginName = assemblyName.Name;
                if (!pluginDomains.TryGetValue(pluginName, out (IntPtr, GameObject) domainTuple)) continue;

                domainTuple.Item2 = plugin;
                pluginDomains[pluginName] = domainTuple;
            }
        }

        private void unloadPlugins()
        {
            foreach (var kv in pluginDomains.ToArray())
            {
                try
                {
                    Logger.LogWarning("Unloading " + kv.Key + " domain and game object");

                    if (kv.Value.Item2 != null)
                        Destroy(kv.Value.Item2);

                    if (kv.Value.Item1 != IntPtr.Zero)
                        MonoHrNative.mono_hr_unload_domain(kv.Value.Item1);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, "Failed to unload plugin domain: " + kv.Key);
                }
            }

            pluginDomains.Clear();
        }

        internal void UnloadPlugin(AssemblyName assemblyName)
        {
            // Remove by key match on name (AssemblyName.Equals can be strict)
            AssemblyName keyToRemove = libraries.Keys.FirstOrDefault(k => k.Name == assemblyName.Name);
            if (keyToRemove != null)
                libraries.Remove(keyToRemove);

            string pluginName = assemblyName.Name;

            if (!pluginDomains.TryGetValue(pluginName, out (IntPtr, GameObject) items))
                return;

            try
            {
                if (items.Item2 != null)
                    Destroy(items.Item2);

                if (items.Item1 != IntPtr.Zero)
                    MonoHrNative.mono_hr_unload_domain(items.Item1);

                pluginDomains.Remove(pluginName);
            }
            catch (Exception e)
            {
                Logger.LogError(e, "Failed to unload plugin domain: " + pluginName);
            }
        }

        internal void Reload()
        {
            unloadPlugins();
            loadPlugins();
        }

        internal static List<Assembly> LoadAssembliesFromDirectory(string directory, string extension = "*.dll")
        {
            List<Assembly> assemblies = [];
            FileInfo[] pluginFiles = new DirectoryInfo(directory).GetFiles(extension, SearchOption.AllDirectories);

            foreach (FileInfo plugin in pluginFiles)
            {
                Assembly assembly = LoadAssemblyFromPath(plugin.FullName);
                if (assembly == null)
                    continue;

                assemblies.Add(assembly);
            }

            return assemblies;
        }

        // Replacement for GetAssembliesFromDirectory using AssemblyName as key
        private static Dictionary<AssemblyName, (string, bool)> FindAssembliesInDirectory(string directory)
        {
            Dictionary<AssemblyName, (string, bool)> map = new();
            FileInfo[] dlls = new DirectoryInfo(directory).GetFiles("*.dll", SearchOption.AllDirectories);

            foreach (FileInfo dll in dlls)
            {
                try
                {
                    AssemblyName name = AssemblyName.GetAssemblyName(dll.FullName);
                    map.Add(name, (dll.FullName, false));
                }
                catch
                {
                    // ignore non-managed or unreadable files
                }
            }

            return map;
        }

        private static bool ShouldSkipPreload(string n)
        {
            if (string.IsNullOrEmpty(n)) return true;
            if (string.Equals(n, "mscorlib", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(n, "netstandard", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(n, "com.rlabrecque.steamworks.net", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("System", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("Rocket", StringComparison.OrdinalIgnoreCase)) return true;
            if (n.StartsWith("SDG.", StringComparison.OrdinalIgnoreCase)) return true;
            // Some old plugins may still reference the 'Assembly-CSharp-firstpass'
            if (n.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)) return true;
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

        private static string FindAssemblyPathBySimpleName(string simpleName)
        {
            foreach (var kv in libraries)
            {
                if (string.Equals(kv.Key.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return kv.Value.Path;
            }

            return null;
        }

        // Load referenced plugin assemblies into the given domain, recursively
        private static Assembly EnsurePluginAssemblyDependencies(IntPtr domain, Assembly rootAsm)
        {
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            Queue<string> queue = new();

            AssemblyName assemblyName = rootAsm.GetName();
            AssemblyName[] refs;
            try
            {
                refs = rootAsm.GetReferencedAssemblies();
            }
            catch
            {
                return null;
            }

            foreach (AssemblyName an in refs)
            {
                if (!ShouldSkipPreload(an.Name))
                    queue.Enqueue(an.Name);
            }

            while (queue.Count > 0)
            {
                string name = queue.Dequeue();
                string path = FindAssemblyPathBySimpleName(name);
                if (string.IsNullOrEmpty(path))
                {
                    Logger.LogWarning($"[EnsurePluginAssemblyDependencies] Path not found for {name}");
                    continue;
                }

                if (pluginDomains.TryGetValue(name, out (IntPtr, GameObject) domainTuple))
                {
                    Logger.LogWarning($"[EnsurePluginAssemblyDependencies] Found already set up domain for {name}");

                    string selfPath = FindAssemblyPathBySimpleName(assemblyName.Name);
                    R.Plugins.UnloadPlugin(assemblyName);
                    Assembly assembly = TryLoadIntoDomain(domainTuple.Item1, selfPath);
                    pluginDomains.Add(assembly.GetName().Name, (domainTuple.Item1, null));
                    return assembly;
                }

                // Plugin haven't been loaded yet so let's add it into our domain
                if (libraries.Where(c => c.Value.IsPlugin).Any(c => c.Key.Name == name))
                {
                    Logger.LogWarning($"[EnsurePluginAssemblyDependencies] Dependency {name} is a plugin, adding it into own domain...");
                    Assembly assembly = TryLoadIntoDomain(domain, path);
                    
                    pluginDomains.Add(assembly.GetName().Name, (domain, null));
                    LoadPluginGameObject(assembly);
                    continue;
                }

                if (!seen.Add(name))
                    continue;

                Assembly depAsm = TryLoadIntoDomain(domain, path);
                if (depAsm != null) continue;

                Logger.LogWarning($"[EnsurePluginAssemblyDependencies] {assemblyName.Name} failed to load {name} into it's own domain");
            }

            return null;
        }

        // Ensure dependent plugins (by simple name) have GameObjects first
        private static void EnsurePluginGameObjectsForDependencies(Assembly asm)
        {
            AssemblyName[] refs;
            try
            {
                refs = asm.GetReferencedAssemblies();
            }
            catch
            {
                return;
            }

            foreach (AssemblyName an in refs)
            {
                if (ShouldSkipPreload(an.Name))
                    continue;
                if (libraries.TryGetValue(an, out (string Path, bool IsPlugin) tuple) && !tuple.IsPlugin)
                    continue;

                string depPath = FindAssemblyPathBySimpleName(an.Name);
                if (string.IsNullOrEmpty(depPath))
                    continue;

                if (pluginDomains.TryGetValue(an.Name, out (IntPtr, GameObject) depTuple) && depTuple.Item2 != null)
                    continue;

                _ = R.Plugins.ForceLoadPlugin(depPath);
            }
        }

        private static void PreloadDomainDependencies(IntPtr domain, IEnumerable<string> candidateDllPaths)
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

                if (ShouldSkipPreload(an.Name)) continue;
                if (loaded.Contains(an.Name)) continue;

                Assembly dep = TryLoadIntoDomain(domain, p);
                if (dep != null)
                    loaded.Add(an.Name);
            }
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
            if (libraries.TryGetValue(pluginAssemblyName, out (string Path, bool IsPlugin) libTuple) && !libTuple.IsPlugin)
                return null;

            if (pluginDomains.TryGetValue(pluginName, out (IntPtr, GameObject) tuple)) return null;

            tuple.Item1 = MonoHrNative.mono_hr_create_domain("Plugin:" + pluginName);
            if (tuple.Item1 == IntPtr.Zero)
            {
                Logger.LogError("Failed to create AppDomain for plugin " + pluginName);
                return null;
            }

            pluginDomains[pluginName] = (tuple.Item1, null);

            try
            {
                // 1) Preload shared libs into this domain
                PreloadDomainDependencies(tuple.Item1, libraries.Values.Where(c => !c.IsPlugin).Select(c => c.Path));

                // 2) Load the plugin itself
                Assembly assembly = TryLoadIntoDomain(tuple.Item1, fullPath);
                if (assembly == null)
                {
                    Logger.LogError("mono_hr_load_plugin returned null for " + pluginName);
                    return null;
                }

                // 3) Preload its non-framework referenced libraries
                Assembly newAssembly = EnsurePluginAssemblyDependencies(tuple.Item1, assembly);

                if (newAssembly != null) return newAssembly; // It got loaded into a dependency domain

                List<Type> types = RocketHelper.GetTypesFromInterface(assembly, "IRocketPlugin").FindAll(x => !x.IsAbstract);

                if (types.Count == 1)
                {
                    Logger.Log("Loaded " + assembly.GetName().Name + " into its own domain from " + fullPath);
                    return assembly;
                }

                Logger.LogError("Invalid or outdated plugin assembly: " + assembly.GetName().Name);
            }
            catch (BadImageFormatException)
            {
                // Not a managed assembly; ignore
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Could not load plugin assembly: " + pluginName);
            }

            return null;
        }
    }
}