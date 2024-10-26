using System.Text.RegularExpressions;

namespace CSUploader.Upload.FileHosters
{
    public partial class NitroFlare : FileHosterClient
    {
        public static string Name_ { get; } = nameof(NitroFlare);

        public override string Name => Name_;

        private Regex UrlIdRegex { get; } = new Regex("nitroflare\\.com/(?:view|watch)/([a-zA-Z0-9]+)(?:\\?|/|$)", RegexOptions.Singleline | RegexOptions.Compiled);

        private string AjaxUrl { get; } = "https://nitroflare.com/api/v2/getFileInfo?files={0}";

        public NitroFlare() : base()
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
