using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pinboard.App.Services.AiSessions;

public sealed class DriveAuthorizationException() : Exception("Google Drive authorization is required.");
public sealed class DriveQuotaException() : Exception("Google Drive storage is full.");
public sealed record DriveClientConfiguration(string ClientId, string ClientSecret);
public sealed record DriveTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt, string? ConfirmedAccount);

// DPAPI ties saved authorization to the current Windows user, outside the archive and source tree.
public static class WindowsUserSecrets
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Transform(byte[] bytes, bool encrypt)
    {
        var input = new Blob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            var success = encrypt ? CryptProtectData(ref input, "Pinboard Google Drive", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try { var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result; }
            finally { LocalFree(output.Data); }
        }
        finally { Marshal.FreeHGlobal(input.Data); }
    }
    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", Transform(JsonSerializer.SerializeToUtf8Bytes(value), true)); File.Move(path + ".tmp", path, true);
    }
    public static T? Load<T>(string path)
    {
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(Transform(File.ReadAllBytes(path), false));
    }
}

public sealed class GoogleDriveSessionCloud : ISessionCloud, ISessionTransferProgressSource, ISessionCloudInspector
{
    private const string Api = "https://www.googleapis.com/drive/v3/";
    private const string UploadApi = "https://www.googleapis.com/upload/drive/v3/";
    private readonly SessionCatalog _catalog;
    private readonly HttpClient _http;
    private readonly string _clientPath;
    private readonly string _tokenPath;
    private readonly Func<CancellationToken, Task<string>>? _testTokenProvider;
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly SemaphoreSlim _uploadGate = new(1, 1);
    private DriveTokens? _tokens;
    private DriveClientConfiguration? _client;
    private (string Account, string Root, string Objects, string Versions)? _folders;
    private string? _lastAccountId;
    public bool IsConfigured => _client is not null;
    public bool IsConnected => _testTokenProvider is not null || !string.IsNullOrWhiteSpace(_tokens?.ConfirmedAccount);
    public string? ConfirmedAccountId => _tokens?.ConfirmedAccount ?? (_testTokenProvider is not null ? _lastAccountId : null);
    public event Action<long, long>? TransferProgress;
    public GoogleDriveSessionCloud(SessionCatalog catalog, string profileDirectory, HttpClient? http = null,
        Func<CancellationToken, Task<string>>? tokenProvider = null)
    {
        _catalog = catalog; _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) }; _testTokenProvider = tokenProvider;
        _clientPath = Path.Combine(profileDirectory, "GoogleDrive", "client.bin"); _tokenPath = Path.Combine(profileDirectory, "GoogleDrive", "authorization.bin");
        try { _client = WindowsUserSecrets.Load<DriveClientConfiguration>(_clientPath); _tokens = WindowsUserSecrets.Load<DriveTokens>(_tokenPath); }
        catch (CryptographicException) { }
        catch (System.ComponentModel.Win32Exception) { }
        catch (JsonException) { }
    }
    public async Task ImportConfigurationAsync(string path, CancellationToken ct = default)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Choose the Google Cloud Desktop app JSON configuration.");
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        if (!json.RootElement.TryGetProperty("installed", out var installed)) throw new InvalidDataException("An OAuth Desktop app configuration is required.");
        var clientId = SessionJson.String(installed, "client_id");
        if (!clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal)) throw new InvalidDataException("Invalid Google OAuth client ID.");
        _client = new(clientId, SessionJson.String(installed, "client_secret"));
        WindowsUserSecrets.Save(_clientPath, _client); Disconnect();
    }
    public static (string Verifier, string Challenge, string State) CreatePkce()
    {
        static string Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var verifier = Url(RandomNumberGenerator.GetBytes(32));
        return (verifier, Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), Url(RandomNumberGenerator.GetBytes(32)));
    }
    public async Task<DriveAccount> ConnectAsync(CancellationToken ct = default)
    {
        var client = _client ?? throw new InvalidOperationException("Import a Google Cloud Desktop app configuration first.");
        Disconnect();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        var redirect = $"http://127.0.0.1:{port}/"; using var listener = new HttpListener(); listener.Prefixes.Add(redirect); listener.Start();
        var pkce = CreatePkce();
        var values = new Dictionary<string, string> { ["client_id"] = client.ClientId, ["redirect_uri"] = redirect,
            ["response_type"] = "code", ["scope"] = "https://www.googleapis.com/auth/drive.file", ["state"] = pkce.State,
            ["code_challenge"] = pkce.Challenge, ["code_challenge_method"] = "S256", ["access_type"] = "offline", ["prompt"] = "consent" };
        var url = "https://accounts.google.com/o/oauth2/v2/auth?" + string.Join('&', values.Select(x => Uri.EscapeDataString(x.Key) + "=" + Uri.EscapeDataString(x.Value)));
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        var valid = context.Request.QueryString["state"] == pkce.State && !string.IsNullOrEmpty(context.Request.QueryString["code"]);
        var page = Encoding.UTF8.GetBytes(valid ? "Pinboard authorization received. You may return to Pinboard." : "Authorization was cancelled or invalid.");
        context.Response.ContentType = "text/plain; charset=utf-8"; await context.Response.OutputStream.WriteAsync(page, timeout.Token); context.Response.Close();
        if (!valid) throw new DriveAuthorizationException();
        var data = await ExchangeTokenAsync(new() { ["code"] = context.Request.QueryString["code"]!, ["client_id"] = client.ClientId,
            ["client_secret"] = client.ClientSecret, ["redirect_uri"] = redirect, ["code_verifier"] = pkce.Verifier, ["grant_type"] = "authorization_code" }, timeout.Token);
        _tokens = data with { ConfirmedAccount = null }; _folders = null;
        // The worker cannot upload until the user confirms this destination account in the connection window.
        WindowsUserSecrets.Save(_tokenPath, _tokens);
        return await GetAccountAsync(timeout.Token);
    }
    public void ConfirmAccount(string accountId)
    {
        if (_tokens is null) throw new DriveAuthorizationException();
        _tokens = _tokens with { ConfirmedAccount = accountId }; WindowsUserSecrets.Save(_tokenPath, _tokens);
    }
    public void Disconnect()
    {
        _tokens = null; _folders = null; _lastAccountId = null;
        if (File.Exists(_tokenPath)) File.Delete(_tokenPath);
    }
    private async Task<DriveTokens> ExchangeTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        if (!response.IsSuccessStatusCode) throw new DriveAuthorizationException();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); var e = json.RootElement;
        return new(SessionJson.String(e, "access_token"), SessionJson.String(e, "refresh_token"),
            DateTimeOffset.UtcNow.AddSeconds(e.GetProperty("expires_in").GetInt32()), _tokens?.ConfirmedAccount);
    }
    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        if (_testTokenProvider is not null) return await _testTokenProvider(ct);
        await _tokenGate.WaitAsync(ct);
        try
        {
            var token = _tokens ?? throw new DriveAuthorizationException();
            if (token.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(1))
            {
                var client = _client ?? throw new DriveAuthorizationException();
                var refreshed = await ExchangeTokenAsync(new() { ["client_id"] = client.ClientId, ["client_secret"] = client.ClientSecret,
                    ["refresh_token"] = token.RefreshToken, ["grant_type"] = "refresh_token" }, ct);
                _tokens = refreshed with { RefreshToken = token.RefreshToken, ConfirmedAccount = token.ConfirmedAccount };
                WindowsUserSecrets.Save(_tokenPath, _tokens);
            }
            return _tokens.AccessToken;
        }
        finally { _tokenGate.Release(); }
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            if (_tokens is not null) _tokens = _tokens with { ExpiresAt = DateTimeOffset.MinValue };
            response.Dispose(); throw new DriveAuthorizationException();
        }
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            var text = await response.Content.ReadAsStringAsync(ct); response.Dispose();
            if (text.Contains("storageQuotaExceeded", StringComparison.Ordinal)) throw new DriveQuotaException();
            var limited = text.Contains("rateLimitExceeded", StringComparison.Ordinal) || text.Contains("userRateLimitExceeded", StringComparison.Ordinal);
            throw new HttpRequestException(limited ? "Google Drive rate limit reached. Upload will retry." : "Google Drive denied this request.", null,
                limited ? HttpStatusCode.TooManyRequests : HttpStatusCode.Forbidden);
        }
        return response;
    }
    private async Task<JsonDocument> JsonRequestAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, ct); response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    public async Task<DriveAccount> GetAccountAsync(CancellationToken ct)
    {
        using var json = await JsonRequestAsync(HttpMethod.Get, Api + "about?fields=user(permissionId,displayName),storageQuota(limit,usage)", null, ct);
        var e = json.RootElement; var user = e.GetProperty("user"); var quota = e.GetProperty("storageQuota");
        var id = SessionJson.String(user, "permissionId");
        if (_tokens?.ConfirmedAccount is { } confirmed && id != confirmed) throw new DriveAuthorizationException();
        _lastAccountId = id;
        return new(id, SessionJson.String(user, "displayName"), long.TryParse(SessionJson.String(quota, "limit"), out var limit) ? limit : null,
            long.TryParse(SessionJson.String(quota, "usage"), out var usage) ? usage : 0);
    }
    private async Task<(string Account, string Root, string Objects, string Versions)> FoldersAsync(CancellationToken ct)
    {
        if (!IsConnected) throw new DriveAuthorizationException();
        if (_folders is { } cached) return cached;
        var account = await GetAccountAsync(ct);
        if (_folders is { } folders && folders.Account == account.Id) return folders;
        var root = await FolderAsync("Pinboard AI Sessions Backups", null, "ai-sessions-root", ct);
        var objects = await FolderAsync("Objects", root, "ai-sessions-objects", ct);
        var versions = await FolderAsync("Versions", root, "ai-sessions-versions", ct);
        _folders = (account.Id, root, objects, versions); return _folders.Value;
    }
    private async Task<string> FolderAsync(string name, string? parent, string kind, CancellationToken ct)
    {
        var query = $"trashed=false and mimeType='application/vnd.google-apps.folder' and appProperties has {{ key='pinboardKind' and value='{kind}' }}";
        if (parent is not null) query += $" and '{parent}' in parents";
        else query += " and 'root' in parents";
        using var existing = await JsonRequestAsync(HttpMethod.Get, Api + "files?spaces=drive&fields=files(id)&q=" + Uri.EscapeDataString(query), null, ct);
        if (existing.RootElement.GetProperty("files").EnumerateArray().FirstOrDefault() is var first && first.ValueKind == JsonValueKind.Object)
            return first.GetProperty("id").GetString()!;
        using var created = await JsonRequestAsync(HttpMethod.Post, Api + "files?fields=id", new { name,
            mimeType = "application/vnd.google-apps.folder", parents = new[] { parent ?? "root" }, appProperties = new { pinboardKind = kind } }, ct);
        return created.RootElement.GetProperty("id").GetString()!;
    }
    public async Task<string> UploadObjectAsync(string hash, string path, CancellationToken ct)
    {
        var folders = await FoldersAsync(ct); return await UploadAsync(folders.Account, folders.Objects, hash, path, hash + ".bin", ct);
    }
    public async Task<string> UploadManifestAsync(SessionManifest manifest, string path, CancellationToken ct)
    {
        var folders = await FoldersAsync(ct); await using var file = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
        return await UploadAsync(folders.Account, folders.Versions, hash, path, manifest.VersionId + ".json", ct);
    }
    private async Task<string> UploadAsync(string account, string folder, string hash, string path, string name, CancellationToken ct)
    {
        if (!IsConnected) throw new DriveAuthorizationException();
        await _uploadGate.WaitAsync(ct);
        try
        {
            var remote = _catalog.GetObject(account, hash); var length = new FileInfo(path).Length;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
            var checksum = Convert.ToHexString(await MD5.HashDataAsync(file, ct)).ToLowerInvariant(); file.Position = 0;
            if (!string.IsNullOrEmpty(remote?.FileId)) { TransferProgress?.Invoke(length, length); return remote.FileId; }
            // A completed request may have succeeded just before process interruption. Recover by object hash.
            var query = $"trashed=false and '{folder}' in parents and appProperties has {{ key='sha256' and value='{hash}' }}";
            using (var existing = await JsonRequestAsync(HttpMethod.Get, Api + "files?fields=files(id,size,md5Checksum)&q=" + Uri.EscapeDataString(query), null, ct))
                foreach (var item in existing.RootElement.GetProperty("files").EnumerateArray())
                    if (VerifyRemote(item, length, checksum))
                    {
                        var recovered = item.GetProperty("id").GetString()!; _catalog.PutObject(account, new(hash, recovered, null, length));
                        TransferProgress?.Invoke(length, length); return recovered;
                    }
            var uri = remote?.UploadUri; long offset = 0;
            if (uri is not null)
            {
                ValidateUploadUri(uri);
                using var probe = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ByteArrayContent([]) };
                probe.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */" + length);
                using var response = await SendAsync(probe, ct);
                if ((int)response.StatusCode == 308) offset = ResumeOffset(response);
                else if (response.IsSuccessStatusCode) return await CompleteUploadAsync(response, account, hash, length, checksum, ct);
                else if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) uri = null;
                else response.EnsureSuccessStatusCode();
            }
            if (uri is null)
            {
                using var initiate = new HttpRequestMessage(HttpMethod.Post, UploadApi + "files?uploadType=resumable&fields=id,size,md5Checksum")
                { Content = new StringContent(JsonSerializer.Serialize(new { name, parents = new[] { folder }, appProperties = new { sha256 = hash, pinboardKind = "ai-sessions-data" } }), Encoding.UTF8, "application/json") };
                initiate.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");
                initiate.Headers.TryAddWithoutValidation("X-Upload-Content-Length", length.ToString());
                using var response = await SendAsync(initiate, ct); response.EnsureSuccessStatusCode();
                uri = response.Headers.Location?.AbsoluteUri ?? throw new InvalidDataException("Google Drive did not return an upload session."); ValidateUploadUri(uri);
                _catalog.PutObject(account, new(hash, "", uri, 0));
            }
            if (offset < 0 || offset > length) throw new InvalidDataException("Invalid upload resume offset.");
            TransferProgress?.Invoke(offset, length);
            file.Position = offset; var buffer = new byte[1024 * 1024];
            while (offset < length)
            {
                var count = (int)Math.Min(buffer.Length, length - offset); await file.ReadExactlyAsync(buffer.AsMemory(0, count), ct);
                using var put = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new ByteArrayContent(buffer, 0, count) };
                put.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                put.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + count - 1, length);
                using var response = await SendAsync(put, ct);
                if ((int)response.StatusCode == 308)
                {
                    var acknowledged = ResumeOffset(response);
                    if (acknowledged <= offset || acknowledged > offset + count) throw new IOException("Google Drive returned an invalid upload acknowledgement.");
                    offset = acknowledged; file.Position = offset; _catalog.PutObject(account, new(hash, "", uri, offset));
                    TransferProgress?.Invoke(offset, length);
                }
                else { response.EnsureSuccessStatusCode(); return await CompleteUploadAsync(response, account, hash, length, checksum, ct); }
            }
            throw new IOException("Google Drive upload has not confirmed completion.");
        }
        finally { _uploadGate.Release(); }
    }
    private static void ValidateUploadUri(string value)
    {
        var uri = new Uri(value);
        if (uri.Scheme != "https" || uri.Host != "www.googleapis.com") throw new InvalidDataException("Unexpected Google Drive upload host.");
    }
    private static long ResumeOffset(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Range", out var ranges)) return 0;
        return long.TryParse(ranges.First().Split('-').Last(), out var end) ? end + 1 : throw new InvalidDataException("Invalid upload range.");
    }
    private static bool VerifyRemote(JsonElement e, long length, string checksum) =>
        SessionJson.String(e, "size") == length.ToString() && SessionJson.String(e, "md5Checksum") == checksum;
    private async Task<string> CompleteUploadAsync(HttpResponseMessage response, string account, string hash, long length, string checksum, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!VerifyRemote(json.RootElement, length, checksum)) throw new InvalidDataException("Google Drive file size or checksum did not match.");
        var id = json.RootElement.GetProperty("id").GetString()!; _catalog.PutObject(account, new(hash, id, null, length));
        TransferProgress?.Invoke(length, length); return id;
    }
    public async Task DownloadObjectAsync(string fileId, string destination, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + "files/" + Uri.EscapeDataString(fileId) + "?alt=media");
        using var response = await SendAsync(request, ct); response.EnsureSuccessStatusCode();
        await using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
        await response.Content.CopyToAsync(file, ct);
    }
    public async Task<string?> FindObjectAsync(string hash, CancellationToken ct)
    {
        var folders = await FoldersAsync(ct);
        var query = $"trashed=false and '{folders.Objects}' in parents and appProperties has {{ key='sha256' and value='{hash}' }}";
        using var json = await JsonRequestAsync(HttpMethod.Get, Api + "files?fields=files(id)&q=" + Uri.EscapeDataString(query), null, ct);
        foreach (var file in json.RootElement.GetProperty("files").EnumerateArray()) return file.GetProperty("id").GetString();
        return null;
    }
    public async Task<IReadOnlyList<(string FileId, string Hash)>> ListManifestsAsync(CancellationToken ct)
    {
        var folders = await FoldersAsync(ct); var result = new List<(string, string)>(); string? page = null;
        var query = $"trashed=false and '{folders.Versions}' in parents and appProperties has {{ key='pinboardKind' and value='ai-sessions-data' }}";
        do
        {
            using var json = await JsonRequestAsync(HttpMethod.Get, Api + "files?pageSize=1000&fields=nextPageToken,files(id,appProperties)&q="
                + Uri.EscapeDataString(query) + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, ct);
            foreach (var file in json.RootElement.GetProperty("files").EnumerateArray())
                if (file.TryGetProperty("appProperties", out var props) && SessionJson.String(props, "sha256") is { Length: 64 } hash)
                    result.Add((file.GetProperty("id").GetString()!, hash));
            page = SessionJson.String(json.RootElement, "nextPageToken");
        } while (!string.IsNullOrEmpty(page));
        return result;
    }
    public async Task<CloudArchiveInventory> InspectAsync(CancellationToken ct)
    {
        if (!IsConnected) throw new DriveAuthorizationException();
        await GetAccountAsync(ct); // Verify the confirmed destination before inspecting metadata.
        var roots = await ListMetadataAsync("trashed=false and 'root' in parents and mimeType='application/vnd.google-apps.folder' and appProperties has { key='pinboardKind' and value='ai-sessions-root' }", ct);
        var files = new List<(string Kind, string Hash, long Size, string Checksum)>(); int objects = 0, versions = 0, invalid = 0;
        foreach (var root in roots)
        {
            var folders = await ListMetadataAsync($"trashed=false and '{QueryValue(SessionJson.String(root, "id"))}' in parents and mimeType='application/vnd.google-apps.folder'", ct);
            foreach (var folder in folders)
            {
                if (!folder.TryGetProperty("appProperties", out var props)) continue;
                var kind = SessionJson.String(props, "pinboardKind");
                if (kind is not ("ai-sessions-objects" or "ai-sessions-versions")) continue;
                foreach (var file in await ListMetadataAsync($"trashed=false and '{QueryValue(SessionJson.String(folder, "id"))}' in parents and appProperties has {{ key='pinboardKind' and value='ai-sessions-data' }}", ct))
                {
                    if (kind == "ai-sessions-objects") objects++; else versions++;
                    var hash = file.TryGetProperty("appProperties", out var properties) ? SessionJson.String(properties, "sha256") : "";
                    var checksum = SessionJson.String(file, "md5Checksum");
                    if (hash.Length != 64 || hash.Any(ch => !Uri.IsHexDigit(ch)) || checksum.Length != 32 || checksum.Any(ch => !Uri.IsHexDigit(ch))
                        || !long.TryParse(SessionJson.String(file, "size"), out var size) || size < 0) { invalid++; continue; }
                    files.Add((kind, hash.ToLowerInvariant(), size, checksum.ToLowerInvariant()));
                }
            }
        }
        var duplicates = files.GroupBy(file => file).Where(group => group.Count() > 1).ToArray();
        return new(roots.Count, objects, versions, duplicates.Length, duplicates.Sum(group => group.Count() - 1),
            duplicates.Sum(group => (group.Count() - 1) * group.Key.Size),
            files.GroupBy(file => (file.Kind, file.Hash)).Count(group => group.Select(file => (file.Size, file.Checksum)).Distinct().Count() > 1), invalid);
    }
    private static string QueryValue(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");
    private async Task<IReadOnlyList<JsonElement>> ListMetadataAsync(string query, CancellationToken ct)
    {
        var files = new Dictionary<string, JsonElement>(StringComparer.Ordinal); var visited = new HashSet<string>(); string? page = null;
        do
        {
            using var json = await JsonRequestAsync(HttpMethod.Get, Api + "files?spaces=drive&pageSize=1000&fields=nextPageToken,incompleteSearch,files(id,size,md5Checksum,appProperties)&q="
                + Uri.EscapeDataString(query) + (page is null ? "" : "&pageToken=" + Uri.EscapeDataString(page)), null, ct);
            if (json.RootElement.TryGetProperty("incompleteSearch", out var incomplete) && incomplete.ValueKind == JsonValueKind.True)
                throw new IOException("Google Drive returned an incomplete inventory; retry the check.");
            foreach (var file in json.RootElement.GetProperty("files").EnumerateArray()) files[SessionJson.String(file, "id")] = file.Clone();
            page = SessionJson.String(json.RootElement, "nextPageToken");
            if (page.Length > 0 && !visited.Add(page)) throw new IOException("Google Drive repeated an inventory page; retry the check.");
        } while (!string.IsNullOrEmpty(page));
        return files.Values.ToArray();
    }
}
