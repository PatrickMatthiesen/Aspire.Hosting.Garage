using System.Net;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;

namespace CommunityToolkit.Aspire.Hosting.Garage;

public sealed partial class GarageBucketResource : Resource, IResourceWithParent<GarageResource>, IResourceWithConnectionString
{
    public GarageBucketResource(string name, string bucketName, GarageResource parent) : base(name)
    {
        ValidateBucketName(bucketName);
        BucketName = bucketName;
        Parent = parent;
    }

    public string BucketName { get; }
    public GarageResource Parent { get; }

    public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create(
        $"ServiceUrl={Parent.S3Endpoint};AccessKeyId={Parent.AccessKeyId};SecretAccessKey={Parent.SecretAccessKey};Region={Parent.Region};ForcePathStyle=true;BucketName={BucketName}");

    internal static void ValidateBucketName(string bucketName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        if (bucketName.Length is < 3 or > 63 || !BucketNamePattern().IsMatch(bucketName)
            || IPAddress.TryParse(bucketName, out _)
            || bucketName.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Garage bucket names must be 3-63 lowercase letters, digits, dots, or hyphens; begin and end with a letter or digit; and not be an IP address.",
                nameof(bucketName));
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]*[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketNamePattern();
}
