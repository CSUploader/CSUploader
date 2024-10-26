namespace CSUploader.Upload.FileHosters
{
    public class HitFile : FileHoster
    {
        public static string Name_ { get; } = nameof(HitFile);

        public override string Name { get; protected set; } = Name_;

        public HitFile() : base()
        {
        }
    }
}
