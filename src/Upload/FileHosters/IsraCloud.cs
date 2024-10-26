namespace CSUploader.Upload.FileHosters
{
    public class IsraCloud : FileHoster
    {
        public static string Name_ { get; } = nameof(IsraCloud);

        public override string Name { get; protected set; } = Name_;

        public IsraCloud() : base()
        {
        }
    }
}
