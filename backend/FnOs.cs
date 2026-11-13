using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DuplicateCleanup;

public interface IAuthority
{
    Task<string[]> Roots(uint uid, CancellationToken ct);
    Task<AclEntry[]> Check(uint uid, string[] paths, CancellationToken ct);
    Task<Dictionary<string, string>> Labels(string[] paths, string language, CancellationToken ct);
    Task Revoke(uint uid, string path, CancellationToken ct);
}

public sealed class FnOs : IAuthority, IDisposable
{
    readonly HttpClient client;
    public FnOs()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/trim_open_gateway_apiscope.socket"), ct); return new NetworkStream(socket, true); }
                catch { socket.Dispose(); throw; }
            }
        };
        client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = TimeSpan.FromSeconds(15) };
    }
    async Task<JsonElement> Call(string req, JsonElement data, CancellationToken ct)
    {
        var token = Environment.GetEnvironmentVariable("TRIM_API_TOKEN");
        if (string.IsNullOrEmpty(token)) throw new AppError("飞牛授权服务未就绪，请从应用中心重新启动应用", 503);
        using var message = new HttpRequestMessage(HttpMethod.Post, "/api/v1/trimapp");
        message.Headers.Authorization = new("Bearer", token);
        var body = new TrimCall(Guid.NewGuid().ToString("N"), req, AppInfo.Name, data);
        message.Content = new StringContent(JsonSerializer.Serialize(body, AppJson.Default.TrimCall), Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode) throw new AppError("飞牛授权服务拒绝请求，请检查系统版本和应用权限", 503);
            using var stream = await response.Content.ReadAsStreamAsync(ct);
            var reply = await JsonSerializer.DeserializeAsync(stream, AppJson.Default.TrimReply, ct);
            if (reply is null || reply.Code != 0) throw new AppError("飞牛授权检查失败，请刷新授权或检查应用权限", 503);
            return reply.Data;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        { throw new AppError("暂时无法连接飞牛授权服务，已停止文件访问", 503); }
    }
    static JsonElement Data(string json) { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); }
    public async Task<string[]> Roots(uint uid, CancellationToken ct)
    {
        var data = await Call("trim.file.getUserAccessibleFolders", Data($"{{\"uid\":{uid}}}"), ct);
        var result = data.Deserialize(AppJson.Default.AccessiblePaths) ?? throw new AppError("授权目录响应无效", 503);
        return result.Paths.Select(Paths.Normalize).Distinct(StringComparer.Ordinal).ToArray();
    }
    public async Task<AclEntry[]> Check(uint uid, string[] paths, CancellationToken ct)
    {
        if (paths.Length == 0) return [];
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject(); w.WriteNumber("uid", uid); w.WriteStartArray("path");
            foreach (var p in paths) w.WriteStringValue(p); w.WriteEndArray(); w.WriteEndObject();
        }
        var data = await Call("trim.file.checkUserACL", Data(Encoding.UTF8.GetString(ms.ToArray())), ct);
        return data.Deserialize(AppJson.Default.AclEntryArray) ?? [];
    }
    public async Task<Dictionary<string, string>> Labels(string[] paths, string language, CancellationToken ct)
    {
        if (paths.Length == 0) return [];
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject(); w.WriteStartArray("path"); foreach (var p in paths) w.WriteStringValue(p);
            w.WriteEndArray(); w.WriteString("language", language); w.WriteEndObject();
        }
        var data = await Call("trim.file.convertPath", Data(Encoding.UTF8.GetString(ms.ToArray())), ct);
        var entries = data.Deserialize(AppJson.Default.PathLabelArray) ?? [];
        return entries.ToDictionary(x => x.Path, x => x.SemanticPath, StringComparer.Ordinal);
    }
    public async Task Revoke(uint uid, string path, CancellationToken ct)
    {
        using var ms = new MemoryStream(); using (var w = new Utf8JsonWriter(ms))
        { w.WriteStartObject(); w.WriteNumber("uid", uid); w.WriteString("path", path); w.WriteEndObject(); }
        await Call("trim.file.delUserAccessibleFolder", Data(Encoding.UTF8.GetString(ms.ToArray())), ct);
    }
    public void Dispose() => client.Dispose();
}

public sealed class AccessGuard(IAuthority authority, AppPaths appPaths)
{
    public bool Protected(string path) => new[] { "/proc", "/sys", "/dev", "/run", "/var", "/etc", "/usr", "/bin", "/sbin", appPaths.Data, appPaths.Target }
        .Any(p => Paths.Within(p, path)) || path.Split('/').Any(p => p.StartsWith("@app", StringComparison.Ordinal));
    public async Task Require(uint uid, string root, string path, string permission, CancellationToken ct)
    {
        if (!Paths.Within(root, path) || Protected(path)) throw new AppError("路径超出可操作范围", 403);
        var roots = await authority.Roots(uid, ct);
        if (!roots.Any(r => Paths.Within(r, root))) throw new AppError("目录授权已撤销，请重新授权", 403, "authorizationRevoked");
        var entries = await authority.Check(uid, [path], ct);
        var acl = entries.FirstOrDefault(x => x.Path == path);
        var allowed = acl is not null && permission switch
        {
            "read" => acl.Readable,
            "write" => acl.Readable && acl.Writable,
            "delete" => acl.Readable && acl.Deletable,
            "replace" => acl.Readable && acl.Writable && acl.Deletable,
            _ => false
        };
        if (!allowed) throw new AppError("当前用户没有所需文件权限", 403, "aclDenied");
    }
    public async Task CheckRoots(uint uid, string[] roots, CancellationToken ct)
    {
        foreach (var root in roots)
        {
            await Require(uid, root, root, "read", ct);
            using var handle = new SecureRoot(root);
        }
    }
}

public sealed record AppPaths(string Data, string Target)
{
    public static AppPaths FromEnvironment()
    {
        var data = Environment.GetEnvironmentVariable("TRIM_PKGVAR");
        var target = Environment.GetEnvironmentVariable("TRIM_APPDEST");
        if (string.IsNullOrWhiteSpace(data) || string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("必须由飞牛应用中心启动，并提供 TRIM_PKGVAR 和 TRIM_APPDEST");
        return new(Path.GetFullPath(data), Path.GetFullPath(target));
    }
}
