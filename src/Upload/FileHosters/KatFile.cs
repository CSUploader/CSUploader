namespace CSUploader.Upload.FileHosters
{
    public partial class KatFile : FileHoster
    {
        public static string Name_ { get; } = nameof(KatFile);

        public override string Name { get; protected set; } = Name_;

        public KatFile() : base()
        {
        }
    }
}
