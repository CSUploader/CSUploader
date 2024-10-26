namespace CSUploader.Upload.FileHosters
{
    public class BRupload : FileHosterClient
    {
        public static string Name_ { get; } = nameof(BRupload);

        public override string Name => Name_;

        public BRupload() : base()
        {
        }

        public override Task UploadAsync(string filePath, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public override Task UploadAsync(string filePath, string? username, string? password, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
