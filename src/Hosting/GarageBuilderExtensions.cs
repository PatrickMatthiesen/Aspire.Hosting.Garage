using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Garage.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
namespace Aspire.Hosting;
public static class GarageBuilderExtensions
{
    public static IResourceBuilder<GarageResource> AddGarage(
        this IDistributedApplicationBuilder builder, string name,
        GarageResourceOptions? options = null,
        IResourceBuilder<ParameterResource>? rpcSecret = null,
        IResourceBuilder<ParameterResource>? adminToken = null,
        IResourceBuilder<ParameterResource>? accessKeyId = null,
        IResourceBuilder<ParameterResource>? secretAccessKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new();
        if (!Regex.IsMatch(options.Region, "^[a-z0-9][a-z0-9-]{0,62}$"))
            throw new ArgumentException("Region must contain lowercase letters, digits or hyphens.", nameof(options));
        if (options.CapacityBytes <= 0) throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be positive.");
        if (options.ConfigContents is not null && options.ConfigPath is not null)
            throw new ArgumentException("Specify ConfigPath or ConfigContents, not both.", nameof(options));
        rpcSecret ??= AddGeneratedSecret(builder, $"{name}-rpc-secret", 64);
        adminToken ??= AddGeneratedSecret(builder, $"{name}-admin-token", 64);
        accessKeyId ??= AddGeneratedSecret(builder, $"{name}-access-key-id", 26);
        secretAccessKey ??= AddGeneratedSecret(builder, $"{name}-secret-access-key", 64);
        var config = options.ConfigContents ?? (options.ConfigPath is { } path
            ? File.ReadAllText(Path.GetFullPath(path, builder.AppHostDirectory)) : DefaultConfig(options.Region));
        var resource = new GarageResource(name, rpcSecret.Resource, adminToken.Resource,
            accessKeyId.Resource, secretAccessKey.Resource, options.Region);
        var garage = builder.AddResource(resource)
            .WithImage(GarageContainerImageTags.Image).WithImageTag(GarageContainerImageTags.Tag)
            .WithImageSHA256(GarageContainerImageTags.Digest)
            .WithContainerFiles("/etc", [new ContainerFile { Name = "garage.toml", Contents = config }])
            .WithEnvironment("GARAGE_CONFIG_FILE", GarageResource.ConfigPath)
            .WithEnvironment("GARAGE_RPC_SECRET", rpcSecret).WithEnvironment("GARAGE_ADMIN_TOKEN", adminToken)
            .WithArgs("/garage", "server")
            .WithEndpoint(targetPort: 3900, scheme: "http", name: GarageResource.S3EndpointName)
            .WithEndpoint(targetPort: 3903, scheme: "http", name: GarageResource.AdminEndpointName)
            .WithEndpoint(targetPort: 3901, name: GarageResource.RpcEndpointName)
            .WithHttpHealthCheck("/health", endpointName: GarageResource.AdminEndpointName);
        resource.Provisioner = builder.AddResource(new GarageProvisionerResource($"{name}-provisioner", resource, options.CapacityBytes))
            .WithImage(options.ProvisionerImage).WithImageTag(options.ProvisionerTag)
            .WithEnvironment("GARAGE_ADMIN_URL", resource.AdminEndpoint)
            .WithEnvironment("GARAGE_ADMIN_TOKEN", adminToken)
            .WithEnvironment("GARAGE_ACCESS_KEY_ID", accessKeyId)
            .WithEnvironment("GARAGE_SECRET_ACCESS_KEY", secretAccessKey)
            .WithEnvironment("GARAGE_CAPACITY_BYTES", options.CapacityBytes.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("GARAGE_PROVISION_LAYOUT", options.ProvisionSingleNodeLayout ? "true" : "false")
            .WithEnvironment(context => context.EnvironmentVariables["GARAGE_BUCKETS"] =
                JsonSerializer.Serialize(resource.Buckets.Select(bucket => bucket.BucketName)))
            .WaitForStart(garage);

        var healthCheckKey = $"{name}_provisioning";
        builder.Services.AddHealthChecks().Add(new HealthCheckRegistration(
            healthCheckKey,
            services => new GarageProvisioningHealthCheck(
                services.GetRequiredService<ResourceNotificationService>(), resource.Provisioner.Resource),
            failureStatus: null, tags: null));
        garage.WithHealthCheck(healthCheckKey);

        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            // AppHost health checks are not exported to Compose. Preserve provisioning
            // readiness there with a completion dependency on each waiting consumer.
            // Never add it to Garage itself: the provisioner needs Garage to start first.
            foreach (var consumer in @event.Model.Resources)
            {
                var waits = consumer.Annotations.OfType<WaitAnnotation>().ToArray();
                if (waits.Any(wait => wait.WaitType == WaitType.WaitUntilHealthy
                    && (ReferenceEquals(wait.Resource, resource)
                        || wait.Resource is GarageBucketResource bucket && ReferenceEquals(bucket.Parent, resource)))
                    && !waits.Any(wait => ReferenceEquals(wait.Resource, resource.Provisioner.Resource)
                        && wait.WaitType == WaitType.WaitForCompletion))
                {
                    consumer.Annotations.Add(new WaitAnnotation(resource.Provisioner.Resource, WaitType.WaitForCompletion, 0));
                }
            }

            return Task.CompletedTask;
        });
        return garage;
    }
    public static IResourceBuilder<GarageBucketResource> AddBucket(
        this IResourceBuilder<GarageResource> garage, string name, string bucketName)
    {
        ArgumentNullException.ThrowIfNull(garage);
        var bucket = new GarageBucketResource(name, bucketName, garage.Resource);
        if (garage.Resource.Buckets.Any(existing => existing.BucketName == bucketName))
            throw new ArgumentException("This bucket is already declared on the Garage resource.", nameof(bucketName));
        // Aspire inherits health checks from IResourceWithParent resources.
        var builder = garage.ApplicationBuilder.AddResource(bucket);
        garage.Resource.AddBucket(bucket);
        return builder;
    }
    public static IResourceBuilder<GarageResource> WithDataVolume(this IResourceBuilder<GarageResource> garage, string? name = null)
        => garage.WithVolume(name ?? $"{garage.ApplicationBuilder.Environment.ApplicationName}-{garage.Resource.Name}-data", GarageResource.DataPath);
    private static IResourceBuilder<ParameterResource> AddGeneratedSecret(IDistributedApplicationBuilder builder, string name, int length)
        => builder.AddParameter(name, new GenerateParameterDefault
        {
            MinLength = length, Numeric = true, MinNumeric = length, Lower = false, Upper = false, Special = false
        }, secret: true, persist: true);
    private static string DefaultConfig(string region) => $$"""
        metadata_dir = "/var/lib/garage/meta"
        data_dir = "/var/lib/garage/data"
        db_engine = "sqlite"
        replication_factor = 1
        rpc_bind_addr = "[::]:3901"
        [s3_api]
        s3_region = "{{region}}"
        api_bind_addr = "0.0.0.0:3900"
        [admin]
        api_bind_addr = "0.0.0.0:3903"
        """;
}
