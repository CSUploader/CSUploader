namespace CSUploader.Upload.FileHosters
{
    public class Novafile : FileHoster
    {
        public static string Name_ { get; } = nameof(Novafile);

        public override string Name { get; protected set; } = Name_;

        public Novafile() : base()
        {
        }
    }
}
