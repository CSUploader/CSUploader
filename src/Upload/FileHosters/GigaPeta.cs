namespace CSUploader.Upload.FileHosters
{
    public class GigaPeta : FileHoster
    {
        public static string Name_ { get; } = nameof(GigaPeta);

        public override string Name { get; protected set; } = Name_;

        public GigaPeta() : base()
        {
        }
    }
}
