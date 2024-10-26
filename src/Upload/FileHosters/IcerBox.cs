using System.Text.RegularExpressions;

namespace CSUploader.Upload.FileHosters
{
    public class IcerBox : FileHoster
    {
        public static string Name_ { get; } = nameof(IcerBox);

        public override string Name { get; protected set; } = Name_;

        private Regex UrlIdRegex { get; } = new Regex("icerbox\\.com/(.+?)(?:\\?|/|$)", RegexOptions.Singleline | RegexOptions.Compiled);

        private Uri ApiUri { get; } = new Uri("https://icerbox.com/api/v1", UriKind.Absolute);

        public IcerBox() : base()
        {
        }
    }
}
