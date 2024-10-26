namespace CSUploader.Upload.FileHosters
{
    public class ExLoad : FileHoster
    {
        public static string Name_ { get; } = nameof(ExLoad);

        public override string Name { get; protected set; } = Name_;

        public ExLoad() : base()
        {
        }
    }
}
