using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using SlackLineBridge.Models.Configurations;

namespace SlackLineBridge.Services
{
    public static class MediaCacheS3ClientFactory
    {
        public static IAmazonS3 Create(MediaCacheOptions options)
        {
            var s3Config = new AmazonS3Config
            {
                ForcePathStyle = options.ForcePathStyle,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
            };
            if (!string.IsNullOrWhiteSpace(options.Endpoint))
            {
                s3Config.ServiceURL = options.Endpoint.TrimEnd('/');
                s3Config.AuthenticationRegion = options.Region;
            }
            else
            {
                s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
            }

            if (!string.IsNullOrWhiteSpace(options.AccessKey))
            {
                return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), s3Config);
            }

            return new AmazonS3Client(s3Config);
        }
    }
}
