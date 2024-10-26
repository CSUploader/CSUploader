using System.Text.RegularExpressions;

namespace CSUploader.Upload.FileHosters
{
    public class Rapidu : FileHoster
    {
        public static string Name_ { get; } = nameof(Rapidu);

        public override string Name { get; protected set; } = Name_;

        private Regex UrlIdRegex { get; } = new Regex("rapidu\\.(?:net|pl)/([0-9]+)/?", RegexOptions.Singleline | RegexOptions.Compiled);

        private string AjaxUrl { get; } = "https://rapidu.net/api/getFileDetails/";

        public Rapidu() : base()
        {
        }
    }
}
