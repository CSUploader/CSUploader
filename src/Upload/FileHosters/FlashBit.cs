namespace CSUploader.Upload.FileHosters
{
    public class FlashBit : FileHoster
    {
        public static string Name_ { get; } = nameof(FlashBit);

        public override string Name { get; protected set; } = Name_;

        public FlashBit() : base()
        {
        }
    }
}
