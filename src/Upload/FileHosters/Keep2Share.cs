using System.Text.RegularExpressions;

namespace CSUploader.Upload.FileHosters
{
    public class Keep2Share : FileHoster
    {
        public static string Name_ { get; } = nameof(Keep2Share);

        // spa.js:36701
        private static string ClientId => "k2s_web_app";

        // spa.js:36702
        private static string ClientSecret => "pjc8pyZv7vhscexepFNzmu4P";

        public override string Name { get; protected set; } = Name_;

        private Regex PathIdRegex { get; } = new Regex("/file/([a-z0-9]{13,})", RegexOptions.Singleline | RegexOptions.Compiled);

        private Uri ApiUri { get; } = new Uri("https://api.k2s.cc/v1", UriKind.Absolute);

        public Keep2Share() : base()
        {
        }
    }
}
