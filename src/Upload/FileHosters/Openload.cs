namespace CSUploader.Upload.FileHosters
{
    public class Openload : FileHoster
    {
        public static string Name_ { get; } = nameof(Openload);

        public override string Name { get; protected set; } = Name_;

        public Openload() : base()
        {
        }
    }
}
