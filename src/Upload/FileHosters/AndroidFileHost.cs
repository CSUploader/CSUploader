namespace CSUploader.Upload.FileHosters
{
    public class AndroidFileHost : FileHosterClient
    {
        public static string Name_ { get; } = nameof(AndroidFileHost);

        public override string Name => Name_;

        public AndroidFileHost() : base()
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
