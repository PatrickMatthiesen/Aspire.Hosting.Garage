using Amazon.S3;
using Amazon.S3.Model;
using System.Data.Common;
using System.Security.Cryptography;
var values = new DbConnectionStringBuilder { ConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__photos")! };
string Get(string key) => (string)values[key];
using var client = new AmazonS3Client(Get("AccessKeyId"), Get("SecretAccessKey"), new AmazonS3Config { ServiceURL = Get("ServiceUrl"), AuthenticationRegion = Get("Region"), ForcePathStyle = true });
var bytes = RandomNumberGenerator.GetBytes(1217141);
var key = "integration-probe/" + Guid.NewGuid().ToString("N");
try {
 await client.PutObjectAsync(new PutObjectRequest { BucketName=Get("BucketName"), Key=key, InputStream=new MemoryStream(bytes), UseChunkEncoding=false, ChecksumAlgorithm=ChecksumAlgorithm.SHA256 });
 using var result = await client.GetObjectAsync(Get("BucketName"), key);
 using var output = new MemoryStream(); await result.ResponseStream.CopyToAsync(output);
 if (!bytes.SequenceEqual(output.ToArray())) throw new Exception("S3 roundtrip mismatch");
 Console.WriteLine($"GARAGE_PROBE_OK: authenticated S3 PUT/GET matched all {bytes.Length} bytes");
} finally { await client.DeleteObjectAsync(Get("BucketName"), key); }

