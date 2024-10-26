namespace CSUploader.Upload.FileHosters
{
    public class Datafile : FileHoster
    {
        public static string Name_ { get; } = nameof(Datafile);

        public override string Name { get; protected set; } = Name_;

        public Datafile() : base()
        {
        }
    }
}
