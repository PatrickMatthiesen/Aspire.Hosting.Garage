using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GarageProvisioner;

public sealed class GarageAdminClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task WaitUntilReachableAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync("health", cancellationToken);
                // A fresh Garage node reports 503 until a layout is assigned. Receiving that
                // response still proves the admin API is ready for provisioning.
                if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    return;
                }

                lastError = new HttpRequestException($"Garage health returned HTTP {(int)response.StatusCode}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(500 * Math.Pow(2, attempt), 5_000)), cancellationToken);
        }

        throw new InvalidOperationException("Garage did not become reachable before the provisioning timeout.", lastError);
    }

    public async Task EnsureSingleNodeLayoutAsync(long capacityBytes, CancellationToken cancellationToken)
    {
        using var status = await GetJsonAsync("v2/GetClusterStatus", cancellationToken);
        var nodes = status.RootElement.GetProperty("nodes");
        if (nodes.GetArrayLength() != 1)
            throw new InvalidOperationException("Automatic layout provisioning requires exactly one node. Disable it for an existing cluster.");
        var node = nodes.EnumerateArray().FirstOrDefault(item => item.GetProperty("isUp").GetBoolean());
        if (node.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException("Garage reported no connected node to assign to the development layout.");
        }

        if (node.TryGetProperty("role", out var role) && role.ValueKind == JsonValueKind.Object
            && role.TryGetProperty("capacity", out var capacity) && capacity.ValueKind == JsonValueKind.Number)
        {
            return;
        }

        var nodeId = node.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Garage returned a node without an identifier.");
        using var layout = await GetJsonAsync("v2/GetClusterLayout", cancellationToken);
        var currentVersion = layout.RootElement.GetProperty("version").GetUInt64();

        await PostJsonAsync("v2/UpdateClusterLayout", new
        {
            roles = new[] { new { id = nodeId, zone = "local", capacity = capacityBytes, tags = Array.Empty<string>() } }
        }, cancellationToken);
        await PostJsonAsync("v2/ApplyClusterLayout", new { version = currentVersion + 1 }, cancellationToken);
    }

    public async Task EnsureKeyAsync(string accessKeyId, string secretAccessKey, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"v2/GetKeyInfo?id={Uri.EscapeDataString(accessKeyId)}&showSecretKey=true",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            await PostJsonAsync("v2/ImportKey", new
            {
                accessKeyId,
                secretAccessKey,
                name = "Aspire application storage"
            }, cancellationToken);
            return;
        }

        await EnsureSuccessAsync(response);
        using var document = await ReadJsonAsync(response, cancellationToken);
        var existingSecret = document.RootElement.GetProperty("secretAccessKey").GetString();
        if (existingSecret is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(existingSecret), Encoding.UTF8.GetBytes(secretAccessKey)))
        {
            throw new InvalidOperationException("The configured Garage access key already exists with a different secret. Refusing to rotate or expose either value.");
        }
    }

    public async Task<string> EnsureBucketAsync(string bucketName, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(
            $"v2/GetBucketInfo?globalAlias={Uri.EscapeDataString(bucketName)}",
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            using var created = await PostJsonAsync("v2/CreateBucket", new { globalAlias = bucketName }, cancellationToken);
            return created.RootElement.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Garage created a bucket without returning its identifier.");
        }

        await EnsureSuccessAsync(response);
        using var existing = await ReadJsonAsync(response, cancellationToken);
        return existing.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Garage returned a bucket without its identifier.");
    }

    public async Task EnsureBucketPermissionsAsync(
        string bucketId,
        string accessKeyId,
        CancellationToken cancellationToken)
    {
        using var key = await GetJsonAsync(
            $"v2/GetKeyInfo?id={Uri.EscapeDataString(accessKeyId)}",
            cancellationToken);
        var alreadyAllowed = key.RootElement.GetProperty("buckets").EnumerateArray().Any(bucket =>
            bucket.GetProperty("id").GetString() == bucketId
            && bucket.GetProperty("permissions").GetProperty("read").GetBoolean()
            && bucket.GetProperty("permissions").GetProperty("write").GetBoolean()
            && bucket.GetProperty("permissions").GetProperty("owner").GetBoolean());
        if (alreadyAllowed)
        {
            return;
        }

        await PostJsonAsync("v2/AllowBucketKey", new
        {
            bucketId,
            accessKeyId,
            permissions = new { read = true, write = true, owner = true }
        }, cancellationToken);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, body, JsonOptions, cancellationToken);
        await EnsureSuccessAsync(response);
        return await ReadJsonAsync(response, cancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var requestPath = response.RequestMessage?.RequestUri?.AbsolutePath ?? "Garage admin API";
        await response.Content.LoadIntoBufferAsync();
        throw new HttpRequestException($"Garage admin request '{requestPath}' failed with HTTP {(int)response.StatusCode}.", null, response.StatusCode);
    }

    public static HttpClient CreateHttpClient(GarageProvisioningOptions options)
    {
        var client = new HttpClient { BaseAddress = new Uri(options.AdminUrl.ToString().TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.AdminToken);
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }
}
