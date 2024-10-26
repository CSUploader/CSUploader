namespace CSUploader.Upload.FileHosters
{
    public class Alfafile : FileHosterClient
    {
        public static string Name_ { get; } = nameof(Alfafile);

        public override string Name => Name_;

        public Alfafile() : base()
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
