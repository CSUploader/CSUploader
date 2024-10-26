namespace CSUploader.Upload.FileHosters
{
    public class ExtMatrix : FileHoster
    {
        public static string Name_ { get; } = nameof(ExtMatrix);

        public override string Name { get; protected set; } = Name_;

        public ExtMatrix() : base()
        {
        }
    }
}
