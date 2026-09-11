using System.Text.Json;
namespace GarageProvisioner;
public sealed record GarageProvisioningOptions(Uri AdminUrl, string AdminToken, string AccessKeyId,
    string SecretAccessKey, string[] BucketNames, long CapacityBytes, bool ProvisionLayout)
{
    public static GarageProvisioningOptions FromEnvironment()
    {
        static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"{name} is required.");
        var capacity = long.Parse(Required("GARAGE_CAPACITY_BYTES"), System.Globalization.CultureInfo.InvariantCulture);
        if (capacity <= 0) throw new InvalidOperationException("Capacity must be positive.");
        return new(new Uri(Required("GARAGE_ADMIN_URL")), Required("GARAGE_ADMIN_TOKEN"),
            Required("GARAGE_ACCESS_KEY_ID"), Required("GARAGE_SECRET_ACCESS_KEY"),
            JsonSerializer.Deserialize<string[]>(Required("GARAGE_BUCKETS")) ?? [], capacity,
            bool.Parse(Required("GARAGE_PROVISION_LAYOUT")));
    }
}
