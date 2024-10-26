namespace CSUploader.Upload.FileHosters
{
    public class Filecloud : FileHoster
    {
        public static string Name_ { get; } = nameof(Filecloud);

        public override string Name { get; protected set; } = Name_;

        public Filecloud() : base()
        {
        }
    }
}
