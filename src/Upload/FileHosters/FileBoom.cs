using System.Text.RegularExpressions;

namespace CSUploader.Upload.FileHosters
{
    public class FileBoom : FileHoster
    {
        public static string Name_ { get; } = nameof(FileBoom);

        // spa.js:27313
        private static string ClientId => "fb_web_app";

        // spa.js:27314
        private static string ClientSecret => "3Zc7urWyORW3HsHX67NMTVnb";

        public override string Name { get; protected set; } = Name_;

        private Regex PathIdRegex { get; } = new Regex("/file/([a-z0-9]{13,})", RegexOptions.Singleline | RegexOptions.Compiled);

        private Uri ApiUri { get; } = new Uri("https://api.fboom.me/v1", UriKind.Absolute);

        public FileBoom() : base()
        {
        }
    }
}
