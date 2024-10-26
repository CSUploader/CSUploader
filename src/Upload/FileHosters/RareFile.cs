namespace CSUploader.Upload.FileHosters
{
    public class RareFile : FileHoster
    {
        public static string Name_ { get; } = nameof(RareFile);

        public override string Name { get; protected set; } = Name_;

        public RareFile() : base()
        {
        }
    }
}
