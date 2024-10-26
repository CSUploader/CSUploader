namespace CSUploader.Upload.FileHosters
{
    public class WuShare : FileHoster
    {
        public static string Name_ { get; } = nameof(WuShare);

        public override string Name { get; protected set; } = Name_;

        public WuShare() : base()
        {
        }
    }
}
