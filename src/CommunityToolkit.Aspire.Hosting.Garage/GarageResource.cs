using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.Garage;

public sealed class GarageResource(
    string name,
    ParameterResource rpcSecret,
    ParameterResource adminToken,
    ParameterResource accessKeyId,
    ParameterResource secretAccessKey,
    string region)
    : ContainerResource(name), IResourceWithConnectionString
{
    public const string S3EndpointName = "s3";
    public const string AdminEndpointName = "admin";
    public const string RpcEndpointName = "rpc";
    public string Region { get; } = region;
    public List<GarageBucketResource> Buckets { get; } = [];
    public IResourceBuilder<GarageProvisionerResource> Provisioner { get; internal set; } = null!;
    public const string DataPath = "/var/lib/garage";
    public const string DefaultDataPath = DataPath;
    public const string ConfigPath = "/etc/garage.toml";

    public EndpointReference S3Endpoint => new(this, S3EndpointName);
    public EndpointReference AdminEndpoint => new(this, AdminEndpointName);
    public EndpointReference RpcEndpoint => new(this, RpcEndpointName);
    public ParameterResource RpcSecret { get; } = rpcSecret;
    public ParameterResource AdminToken { get; } = adminToken;
    public ParameterResource AccessKeyId { get; } = accessKeyId;
    public ParameterResource SecretAccessKey { get; } = secretAccessKey;

    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"AdminUrl={AdminEndpoint};AdminToken={AdminToken}");
}
