using System.Runtime.Loader;
using AgentBrook.Runtime.Abstractions;
using AgentBrook.Plugin.Hosting.Abstractions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBrook.Plugin.Hosting;

/// <summary>插件包 manifest（P01：本地包形态；S9 版本化插件包按此演进）。</summary>
public sealed record PluginPackageManifest(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("entry")] string Entry,
    [property: JsonPropertyName("minContractVersion")] string MinContractVersion,
    [property: JsonPropertyName("capabilityId")] string CapabilityId,
    [property: JsonPropertyName("entrySha256")] string EntrySha256)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(Version) &&
        !string.IsNullOrWhiteSpace(Entry) && !string.IsNullOrWhiteSpace(MinContractVersion) &&
        !string.IsNullOrWhiteSpace(CapabilityId) &&
        ContentRef.IsValidSha256(EntrySha256);
}

/// <summary>包校验/加载异常（加载前拒绝，而非运行后类型转换失败——§15.4 契约同源）。</summary>
public sealed class PluginPackageException(string code, string message)
    : InvalidOperationException($"[{code}] {message}")
{
    public string Code { get; } = code;
}

/// <summary>
/// 插件包安装与加载（implementation-plan §5-S6 任务1/4；P02：从空根经真实加载流程安装，不直接 new 插件类）。
/// ALC 隔离：包目录只放私有依赖；公共契约（SharedContractAssemblies）回退默认上下文同源——
/// 包携带契约副本 → 类型双源，加载前拒绝。
/// </summary>
public static class PluginPackageLoader
{
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// 校验包目录（S8 兼容双格式）：manifest 可解析、entry 存在、SHA256 一致。坏包 → 抛（P01 坏包拒绝）。
    /// 形态 A（新宿主）：{id,version,entry,minContractVersion,capabilityId,entrySha256}；
    /// 形态 B（服务端分发，与 PrivacyMasking 发布一致）：{id,version,files:[{path,sha256}]}——
    /// entry 由目录内唯一 AgentBrook.Plugin.*.dll 推导，files 逐文件校验。
    /// </summary>
    /// <summary>
    /// 文件 SHA256（带重试）：安装/卸载刚落盘或旧 ALC 尚未释放时，瞬时读取可能得到旧内容——
    /// 直接判定"篡改/损坏"会误报（用户实测"第一次安装失败、再点一次成功"）。
    /// </summary>
    private static string HashFileStable(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
                if (attempt > 0) Thread.Sleep(50);   // 让写入方有机会完成
                return hash;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(200);
            }
        }
    }

    public static PluginPackageManifest Validate(string packageDir)
    {
        if (!Directory.Exists(packageDir))
            throw new PluginPackageException(ImErrorCodes.BadPayload, $"包目录缺失：{packageDir}");

        var manifestPath = Path.Combine(packageDir, ManifestFileName);

        JsonDocument? doc = null;
        if (File.Exists(manifestPath))
        {
            try { doc = JsonDocument.Parse(File.ReadAllText(manifestPath)); }
            catch (JsonException ex)
            {
                throw new PluginPackageException(ImErrorCodes.BadPayload, $"manifest 不可解析：{ex.Message}");
            }
        }

        bool HasRoot() => doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object;

        string GetString(string name)
        {
            if (!HasRoot()) return "";
            var root = doc!.RootElement;
            return root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "";
        }

        using (doc)
        {
            var id = GetString("id") != "" ? GetString("id") : Path.GetFileName(packageDir.TrimEnd('/'));
            var version = GetString("version") != "" ? GetString("version") : "0.0.0";

            // 形态 B：files[] 逐文件校验（服务端分发格式）
            if (HasRoot() && doc!.RootElement.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in filesEl.EnumerateArray())
                {
                    var path = f.TryGetProperty("path", out var pe) ? pe.GetString() : null;
                    var sha = f.TryGetProperty("sha256", out var se) ? se.GetString() : null;
                    if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(sha)) continue;
                    var filePath = Path.Combine(packageDir, path);
                    if (!File.Exists(filePath))
                        throw new PluginPackageException(ImErrorCodes.BadPayload, $"包文件缺失：{path}");
                    var actualFile = HashFileStable(filePath);
                    if (actualFile != sha)
                        throw new PluginPackageException(ImErrorCodes.BadPayload,
                            $"文件 hash 不符（篡改/损坏）：{path}");
                }
            }

            // entry 推导：优先 manifest.entry；否则目录内唯一 AgentBrook.Plugin.*.dll（排除抽象/契约程序集）
            var entry = GetString("entry");
            if (string.IsNullOrWhiteSpace(entry))
            {
                var candidates = Directory.GetFiles(packageDir, "AgentBrook.Plugin.*.dll")
                    .Where(p => !Path.GetFileName(p).Contains("Abstractions", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (candidates.Count != 1)
                    throw new PluginPackageException(ImErrorCodes.BadPayload,
                        $"入口 dll 不唯一：{string.Join(", ", candidates.Select(Path.GetFileName))}");
                entry = Path.GetFileName(candidates[0]);
            }

            var entryPath = Path.Combine(packageDir, entry);
            if (!File.Exists(entryPath))
                throw new PluginPackageException(ImErrorCodes.BadPayload, $"entry 程序集缺失：{entry}");

            // 契约副本拒绝（两种形态都适用，§15.4 契约同源）
            foreach (var contract in PluginHost.SharedContractAssemblies)
            {
                if (File.Exists(Path.Combine(packageDir, contract + ".dll")))
                    throw new PluginPackageException(ImErrorCodes.ContractVersionMismatch,
                        $"包携带公共契约副本：{contract}.dll（契约必须同源）。");
            }

            // entry 校验：形态 A 显式 entrySha256；形态 B 由 files[] 清单覆盖（entry 在清单内则校验）
            var entrySha = GetString("entrySha256");
            if (ContentRef.IsValidSha256(entrySha ?? ""))
            {
                var actual = HashFileStable(entryPath);
                if (actual != entrySha)
                    throw new PluginPackageException(ImErrorCodes.BadPayload,
                        $"entry hash 不符（篡改/损坏）：{entry} 期望 {entrySha![..12]}… 实际 {actual[..12]}…");
            }
            else if (HasRoot() && doc!.RootElement.TryGetProperty("files", out var fe) && fe.ValueKind == JsonValueKind.Array)
            {
                var fileName = Path.GetFileName(entry);
                foreach (var f in fe.EnumerateArray())
                {
                    if ((f.TryGetProperty("path", out var pe) ? pe.GetString() : "") == fileName)
                    {
                        var expected = f.TryGetProperty("sha256", out var se) ? se.GetString() : null;
                        var actual = HashFileStable(entryPath);
                        if (!string.IsNullOrEmpty(expected) && actual != expected)
                            throw new PluginPackageException(ImErrorCodes.BadPayload,
                                $"entry hash 不符（篡改/损坏）：{entry}");
                        break;
                    }
                }
            }

            var minContract = HasRoot() ? GetString("minContractVersion") : HostingContract.Version;
            var capabilityId = HasRoot() ? GetString("capabilityId") : "";
            var declaredSha = HasRoot() ? GetString("entrySha256") : "";
            return new PluginPackageManifest(id, string.IsNullOrEmpty(version) ? "0.0.0" : version,
                entry, string.IsNullOrEmpty(minContract) ? HostingContract.Version : minContract,
                capabilityId ?? "", ContentRef.IsValidSha256(declaredSha ?? "") ? declaredSha : "");
        }
    }

    /// <summary>
    /// 加载 entry 程序集内的**全部**能力工厂（一个包可同时提供通道与登录等能力，
    /// 如微信通道包内 weixin-channel + weixin-login）。宿主按 CapabilityId 注册，互不覆盖。
    /// </summary>
    public static (IReadOnlyList<ICapabilityFactory> Factories, PluginPackageManifest Manifest) LoadFactories(
        string packageDir)
    {
        var manifest = Validate(packageDir);
        if (!HostingContract.IsCompatible(HostingContract.Version, manifest.MinContractVersion))
            throw new PluginPackageException(ImErrorCodes.ContractVersionMismatch,
                $"包要求契约 {manifest.MinContractVersion}，宿主 {HostingContract.Version}（加载前拒绝）。");

        var context = new PluginPackageLoadContext(packageDir);
        var entryPath = Path.Combine(packageDir, manifest.Entry);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(entryPath);
        }
        catch (BadImageFormatException ex)
        {
            throw new PluginPackageException(ImErrorCodes.BadPayload, $"entry 程序集不可加载（RID/架构不符）：{ex.Message}");
        }

        var factories = new List<ICapabilityFactory>();
        foreach (var type in assembly.GetTypes().Where(t => typeof(ICapabilityFactory).IsAssignableFrom(t) && !t.IsAbstract))
        {
            var factory = (ICapabilityFactory)Activator.CreateInstance(type)!;
            if (!HostingContract.IsCompatible(HostingContract.Version, factory.MinContractVersion))
                throw new PluginPackageException(ImErrorCodes.ContractVersionMismatch,
                    $"工厂要求契约 {factory.MinContractVersion}，宿主 {HostingContract.Version}。");
            factories.Add(factory);
        }
        if (factories.Count == 0)
            throw new PluginPackageException(ImErrorCodes.BadPayload, "包内未找到 ICapabilityFactory 实现。");
        return (factories, manifest);
    }

    /// <summary>加载 entry 程序集（ALC：包目录私有依赖优先，公共契约回退默认上下文）并实例化能力工厂。</summary>
    public static (ICapabilityFactory Factory, PluginPackageManifest Manifest) LoadFactory(string packageDir)
    {
        var manifest = Validate(packageDir);
        if (!HostingContract.IsCompatible(HostingContract.Version, manifest.MinContractVersion))
            throw new PluginPackageException(ImErrorCodes.ContractVersionMismatch,
                $"包要求契约 {manifest.MinContractVersion}，宿主 {HostingContract.Version}（加载前拒绝）。");

        var context = new PluginPackageLoadContext(packageDir);
        var entryPath = Path.Combine(packageDir, manifest.Entry);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromAssemblyPath(entryPath);
        }
        catch (BadImageFormatException ex)
        {
            throw new PluginPackageException(ImErrorCodes.BadPayload, $"entry 程序集不可加载（RID/架构不符）：{ex.Message}");
        }

        var factoryType = assembly.GetTypes()
            .SingleOrDefault(t => typeof(ICapabilityFactory).IsAssignableFrom(t) && !t.IsAbstract)
            ?? throw new PluginPackageException(ImErrorCodes.BadPayload, "包内未找到 ICapabilityFactory 实现。");
        var factory = (ICapabilityFactory)Activator.CreateInstance(factoryType)!
            ?? throw new PluginPackageException(ImErrorCodes.BadPayload, "工厂实例化失败。");
        if (!HostingContract.IsCompatible(HostingContract.Version, factory.MinContractVersion))
            throw new PluginPackageException(ImErrorCodes.ContractVersionMismatch,
                $"工厂要求契约 {factory.MinContractVersion}，宿主 {HostingContract.Version}。");
        return (factory, manifest);
    }

    /// <summary>从构建产物制作包（manifest + entry hash；S6 任务1：从本地产物制作插件包）。</summary>
    public static PluginPackageManifest BuildPackage(string id, string version, string capabilityId,
        string entryDllPath, string packageDir)
    {
        Directory.CreateDirectory(packageDir);
        var entryName = Path.GetFileName(entryDllPath);
        File.Copy(entryDllPath, Path.Combine(packageDir, entryName), overwrite: true);
        // 附带相邻的私有依赖（非契约、非宿主共享）——P0 两插件无私有依赖，此循环为通用形态保留
        var dir = Path.GetDirectoryName(entryDllPath)!;
        foreach (var dll in Directory.GetFiles(dir, "*.dll"))
        {
            var name = Path.GetFileName(dll);
            if (PluginHost.SharedContractAssemblies.Contains(Path.GetFileNameWithoutExtension(name))) continue;
            if (name == entryName) continue;
            if (!name.StartsWith("AgentBrook.", StringComparison.Ordinal))
                File.Copy(dll, Path.Combine(packageDir, name), overwrite: true);
        }
        var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(packageDir, entryName)))).ToLowerInvariant();
        var manifest = new PluginPackageManifest(id, version, entryName, HostingContract.Version, capabilityId, sha);
        File.WriteAllText(Path.Combine(packageDir, ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
        return manifest;
    }
}

/// <summary>包 ALC：包目录内存在则加载（私有依赖）；否则回退默认上下文（公共契约同源）。</summary>
public sealed class PluginPackageLoadContext : AssemblyLoadContext
{
    private readonly string _packageDir;

    public PluginPackageLoadContext(string packageDir) : base("PluginPackage:" + Path.GetFileName(packageDir.TrimEnd('/')), isCollectible: true)
    {
        _packageDir = packageDir;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name ?? "";
        if (PluginHost.SharedContractAssemblies.Contains(name))
            return null;   // 契约同源：回退默认上下文
        var path = Path.Combine(_packageDir, name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}
