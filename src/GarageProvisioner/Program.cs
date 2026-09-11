using GarageProvisioner;

var options = GarageProvisioningOptions.FromEnvironment();
using var httpClient = GarageAdminClient.CreateHttpClient(options);
var garage = new GarageAdminClient(httpClient);

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await garage.WaitUntilReachableAsync(timeout.Token);
if (options.ProvisionLayout) await garage.EnsureSingleNodeLayoutAsync(options.CapacityBytes, timeout.Token);
await garage.WaitUntilReachableAsync(timeout.Token);
await garage.EnsureKeyAsync(options.AccessKeyId, options.SecretAccessKey, timeout.Token);
foreach (var bucket in options.BucketNames)
{
    var bucketId = await garage.EnsureBucketAsync(bucket, timeout.Token);
    await garage.EnsureBucketPermissionsAsync(bucketId, options.AccessKeyId, timeout.Token);
}

Console.WriteLine("Garage layout, application key, bucket, and permissions are ready.");
