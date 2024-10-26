namespace CSUploader.Upload.FileHosters
{
    public class Upstore : FileHoster
    {
        public static string Name_ { get; } = nameof(Upstore);

        public override string Name { get; protected set; } = Name_;

        public Upstore() : base()
        {
        }
    }
}
