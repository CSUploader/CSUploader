namespace CSUploader.Upload.FileHosters
{
    public class UbiqFile : FileHoster
    {
        public static string Name_ { get; } = nameof(UbiqFile);

        public override string Name { get; protected set; } = Name_;

        public UbiqFile() : base()
        {
        }
    }
}
