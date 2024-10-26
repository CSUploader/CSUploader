namespace CSUploader.Upload.FileHosters
{
    public class Uploaded : FileHoster
    {
        public static string Name_ { get; } = nameof(Uploaded);

        public override string Name { get; protected set; } = Name_;

        public Uploaded() : base()
        {
        }
    }
}
