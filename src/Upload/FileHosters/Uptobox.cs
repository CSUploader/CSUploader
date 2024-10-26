namespace CSUploader.Upload.FileHosters
{
    public class Uptobox : FileHoster
    {
        public static string Name_ { get; } = nameof(Uptobox);

        public override string Name { get; protected set; } = Name_;

        public Uptobox() : base()
        {
        }
    }
}
