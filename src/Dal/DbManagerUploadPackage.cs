namespace CSUploader.Dal
{
    public partial class DbManager
    {
        public virtual Task<UploadPackageDto[]> GetUploadPackagesAsync(CancellationToken cancellationToken = default)
        {
            return UploadPackageManager.GetAllAsync(cancellationToken);
        }
    }
}
