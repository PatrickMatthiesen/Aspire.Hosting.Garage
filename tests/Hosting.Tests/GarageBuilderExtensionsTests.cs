using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Garage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace Aspire.Hosting.Garage.Tests;

public sealed class GarageBuilderExtensionsTests
{
    [Fact]
    public void AddGarageRejectsMutuallyExclusiveConfiguration()
    {
        var builder = DistributedApplication.CreateBuilder();

        var exception = Assert.Throws<ArgumentException>(() => builder.AddGarage("storage", new GarageResourceOptions
        {
            ConfigPath = "garage.toml",
            ConfigContents = "[admin]\napi_bind_addr = \"0.0.0.0:3903\""
        }));

        Assert.Contains("ConfigPath", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ConfigContents", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddGarageRejectsInvalidCapacity()
    {
        var builder = DistributedApplication.CreateBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddGarage("storage", new GarageResourceOptions
        {
            CapacityBytes = 0,
            ConfigContents = MinimalConfiguration
        }));
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("-invalid")]
    [InlineData("invalid-")]
    [InlineData("two..dots")]
    [InlineData("192.168.1.1")]
    public void AddBucketRejectsInvalidGarageBucketNames(string bucketName)
    {
        var builder = DistributedApplication.CreateBuilder();
        var garage = builder.AddGarage("storage", new GarageResourceOptions { ConfigContents = MinimalConfiguration });

        Assert.Throws<ArgumentException>(() => garage.AddBucket("bucket", bucketName));
    }

    [Fact]
    public async Task AddGarageSupportsMultipleArbitraryBucketsAndProvisionerEnvironment()
    {
        var builder = DistributedApplication.CreateBuilder();
        var garage = builder.AddGarage("object-store", new GarageResourceOptions
        {
            Region = "private-region",
            CapacityBytes = 42_000_000,
            ConfigContents = MinimalConfiguration,
            ProvisionerImage = "local/garage-provisioner",
            ProvisionerTag = "test"
        });
        var photos = garage.AddBucket("photos", "trip-photos");
        var thumbnails = garage.AddBucket("thumbs", "trip-thumbnails");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var storage = Assert.Single(model.Resources.OfType<GarageResource>());
        Assert.NotNull(storage.Provisioner);
        var provisioner = storage.Provisioner!.Resource;

        Assert.Equal("private-region", storage.Region);
        Assert.Equal(42_000_000, provisioner.CapacityBytes);
        Assert.Equal("object-store-provisioner", provisioner.Name);
        Assert.Equal(2, storage.Buckets.Count);
        Assert.Equal(["trip-photos", "trip-thumbnails"], storage.Buckets.Select(b => b.BucketName));

#pragma warning disable CS0618 // Aspire 13.5.3 exposes this test helper; the replacement is not available on the package's supported API surface yet.
        var environment = await provisioner.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish);
#pragma warning restore CS0618
        Assert.Equal("[\"trip-photos\",\"trip-thumbnails\"]", environment["GARAGE_BUCKETS"]);

        Assert.Equal("private-region", photos.Resource.Parent.Region);
        Assert.Contains("BucketName=trip-thumbnails", thumbnails.Resource.ConnectionStringExpression.ValueExpression);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingForGarageOrBucketAlsoWaitsForSuccessfulProvisioning(bool waitForBucket)
    {
        var builder = DistributedApplication.CreateBuilder(["--operation", "publish"]);
        var garage = builder.AddGarage("storage", new GarageResourceOptions { ConfigContents = MinimalConfiguration });
        var bucket = garage.AddBucket("photos", "trip-photos");
        var consumer = builder.AddContainer("consumer", "busybox:1.36")
            .WithReference(bucket);
        if (waitForBucket) consumer.WaitFor(bucket);
        else consumer.WaitFor(garage);
        var startupOnly = builder.AddContainer("startup-only", "busybox:1.36").WaitForStart(garage);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));
        // Repeated model preparation must not accumulate duplicate dependencies.
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services, model));
        var provisioner = model.Resources.OfType<GarageProvisionerResource>().SingleOrDefault();
        Assert.NotNull(provisioner);

        var provisionerWait = Assert.Single(provisioner.Annotations.OfType<WaitAnnotation>());
        Assert.Same(model.Resources.OfType<GarageResource>().Single(), provisionerWait.Resource);
        Assert.Equal(WaitType.WaitUntilStarted, provisionerWait.WaitType);

        var consumerWait = Assert.Single(consumer.Resource.Annotations.OfType<WaitAnnotation>(), a => a.WaitType == WaitType.WaitForCompletion);
        Assert.Same(provisioner, consumerWait.Resource);
        Assert.Empty(garage.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.DoesNotContain(startupOnly.Resource.Annotations.OfType<WaitAnnotation>(), a => a.WaitType == WaitType.WaitForCompletion);
    }

    [Fact]
    public async Task ProvisioningHealthRequiresSuccessfulCompletionAndResetsOnRestart()
    {
        var builder = DistributedApplication.CreateBuilder();
        var garage = builder.AddGarage("storage");
        using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var healthChecks = app.Services.GetRequiredService<HealthCheckService>();

        async Task AssertHealth(HealthStatus expected)
        {
            var report = await healthChecks.CheckHealthAsync(registration => registration.Name == "storage_provisioning");
            Assert.Single(report.Entries);
            Assert.Equal(expected, report.Status);
        }

        Task SetState(string state, int? exitCode) => notifications.PublishUpdateAsync(
            garage.Resource.Provisioner.Resource,
            snapshot => snapshot with { State = new ResourceStateSnapshot(state, null), ExitCode = exitCode });

        await AssertHealth(HealthStatus.Unhealthy);
        await SetState(KnownResourceStates.Running, null);
        await AssertHealth(HealthStatus.Unhealthy);
        await SetState(KnownResourceStates.Exited, 1);
        await AssertHealth(HealthStatus.Unhealthy);
        await SetState(KnownResourceStates.Exited, null);
        await AssertHealth(HealthStatus.Unhealthy);
        await SetState(KnownResourceStates.Exited, 0);
        await AssertHealth(HealthStatus.Healthy);
        await SetState(KnownResourceStates.Running, null);
        await AssertHealth(HealthStatus.Unhealthy);
        await SetState(KnownResourceStates.Finished, 0);
        await AssertHealth(HealthStatus.Healthy);
    }

    [Fact]
    public void BucketCollectionCannotBypassRegistrationAndValidation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var garage = builder.AddGarage("storage");
        var buckets = garage.Resource.Buckets;
        var bucket = garage.AddBucket("photos", "trip-photos");

        Assert.Same(bucket.Resource, Assert.Single(buckets));
        Assert.True(bucket.Resource.TryGetAnnotationsIncludingAncestorsOfType<HealthCheckAnnotation>(out var checks));
        Assert.Equal(checks.Count(), checks.Select(check => check.Key).Distinct().Count());
        Assert.Throws<NotSupportedException>(() => ((ICollection<GarageBucketResource>)buckets).Clear());
        Assert.Throws<ArgumentException>(() => garage.AddBucket("other", "trip-photos"));
        Assert.Single(buckets);
    }

    [Fact]
    public void WithDataVolumeUsesTheResourceDataPath()
    {
        var builder = DistributedApplication.CreateBuilder();
        var garage = builder.AddGarage("storage", new GarageResourceOptions { ConfigContents = MinimalConfiguration })
            .WithDataVolume("persistent-garage");

        var volume = Assert.Single(garage.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("persistent-garage", volume.Source);
        Assert.Equal(GarageResource.DefaultDataPath, volume.Target);
    }

    private const string MinimalConfiguration = "metadata_dir = \"/var/lib/garage/meta\"\ndata_dir = \"/var/lib/garage/data\"\n[s3_api]\ns3_region = \"garage\"\n[admin]\napi_bind_addr = \"0.0.0.0:3903\"";
}
