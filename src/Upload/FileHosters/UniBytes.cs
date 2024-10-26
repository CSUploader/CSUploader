namespace CSUploader.Upload.FileHosters
{
    public class UniBytes : FileHosterClient
    {
        public static string Name_ { get; } = nameof(UniBytes);

        public override string Name => Name_;

        public UniBytes() : base()
        {
        }

        public override Task UploadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override Task UploadAsync(string filePath, string username, string password, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
