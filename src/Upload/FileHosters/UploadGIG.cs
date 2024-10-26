namespace CSUploader.Upload.FileHosters
{
    public class UploadGIG : FileHoster
    {
        public static string Name_ { get; } = nameof(UploadGIG);

        public override string Name { get; protected set; } = Name_;

        public UploadGIG() : base()
        {
        }
    }
}
