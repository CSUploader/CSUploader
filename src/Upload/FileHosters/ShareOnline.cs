using System.Text.RegularExpressions;

namespace CSUploader.Upload.FileHosters
{
    public class ShareOnline : FileHoster
    {
        public static string Name_ { get; } = nameof(ShareOnline);

        public override string Name { get; protected set; } = Name_;

        private Regex UrlIdRegex { get; } = new Regex("(?:share\\-online\\.biz|egoshare\\.com)/(?:download\\.php\\?id\\=|dl/)([\\w]+)", RegexOptions.Singleline | RegexOptions.Compiled);

        private Uri ApiUri { get; } = new Uri("https://api.share-online.biz", UriKind.Absolute);

        public ShareOnline() : base()
        {
        }
    }
}
