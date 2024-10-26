namespace CSUploader.Upload.FileHosters
{
    public class FilesMonster : FileHoster
    {
        public static string Name_  { get; } = nameof(FilesMonster);

        public override string Name { get; protected set; } = Name_;

        public FilesMonster() : base()
        {
        }
    }
}
