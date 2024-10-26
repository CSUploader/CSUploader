namespace CSUploader.Upload.FileHosters
{
    public class TakeFile : FileHoster
    {
        public static string Name_ { get; } = nameof(TakeFile);

        public override string Name { get; protected set; } = Name_;

        public TakeFile() : base()
        {
        }
    }
}
