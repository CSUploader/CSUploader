using System.Text.RegularExpressions;

namespace CSUploader.Upload.FileHosters
{
    public class TezFiles : FileHosterClient
    {
        public static string Name_ { get; } = nameof(TezFiles);

        // spa.js:25307
        private static string ClientId => "tz_web_app";

        // spa.js:25308
        private static string ClientSecret => "fa3JaicegiY7phoili4Phui8";

        public override string Name => Name_;

        private Regex PathIdRegex { get; } = new Regex("/file/([a-z0-9]{13,})", RegexOptions.Singleline | RegexOptions.Compiled);

        private Uri ApiUri { get; } = new Uri("https://api.tezfiles.com/v1", UriKind.Absolute);

        public TezFiles() : base()
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
