using System.Reflection;
using System.Runtime.Loader;

namespace AgentBrook.Agent.Plugins;

/// <summary>
/// 插件隔离加载上下文（doc/18 §5）：可收集（停用/卸载支持）。
/// 契约程序集（AgentBrook.Plugin.Abstractions）与宿主共享程序集
/// （Microsoft.Extensions.AI* / Microsoft.Agents.AI*）回退默认上下文——保证
/// IChatClient 等类型同源可交换；插件私有依赖（Sqlite/OnnxRuntime 等）
/// 经 AssemblyDependencyResolver 从插件目录解析（含 runtimes/{rid}/native 非托管库）。
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private const string ContractAssembly = "AgentBrook.Plugin.Abstractions";

    private static readonly string[] SharedPrefixes =
    [
        "Microsoft.Extensions.AI",      // 宿主管道同源（含 .Abstractions）
        "Microsoft.Agents.AI",          // 宿主管道同源
    ];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginMainAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginMainAssemblyPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginMainAssemblyPath);
    }

    public Assembly LoadFromPlugin(string assemblyPath) => LoadFromAssemblyPath(assemblyPath);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is ContractAssembly)
        {
            return null;   // 契约回退默认上下文（类型同源）
        }
        foreach (var prefix in SharedPrefixes)
        {
            if (assemblyName.Name?.StartsWith(prefix, StringComparison.Ordinal) == true)
            {
                return null;
            }
        }
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is not null ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is not null ? LoadUnmanagedDllFromPath(path) : nint.Zero;
    }
}
