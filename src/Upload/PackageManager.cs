// <copyright file="PackageManager.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Upload;

public class PackageManager
{
    private readonly PackageQueue packageQueue = new();

    public event EventHandler<PackageAddedEventArgs>? PackageAdded;

    public event EventHandler<PackageStatusChangedEventArgs>? PackageStatusChanged;

    public List<Package> Packages { get; } = [];

    public bool IsPaused { get; private set; }

    public void AddAndStartPackage(PackageOptions options)
    {
        Package package = new(options);

        AddPackage(package);

        StartPackage(package);
    }

    public void StartPackages()
    {
        foreach (PackageDetails packageDetails in Packages)
        {
            StartPackage(packageDetails);
        }
    }

    public void StartPackage(PackageDetails packageDetails)
    {
        if (IsPaused)
        {
            return;
        }

        if (packageDetails is Package package)
        {
            StartPackageDetails(package, true);

            foreach (PackageFile packageFile in package)
            {
                StartPackageDetails(packageFile, true);
            }
        }
        else if (packageDetails is PackageFile packageFile)
        {
            StartPackageDetails(packageFile, true);
        }
    }

    public void PausePackages(bool resume)
    {
        IsPaused = !resume;

        foreach (PackageDetails packageDetails in Packages)
        {
            PausePackage(packageDetails, resume);
        }
    }

    private static void PausePackage(PackageDetails packageDetails, bool resume)
    {
        if (packageDetails is Package package)
        {
            package.PauseAsync(resume);
        }
        else if (packageDetails is PackageFile packageFile)
        {
            packageFile.PauseAsync(resume);
        }
    }

    public void StopPackages()
    {
        foreach (PackageDetails packageDetails in Packages)
        {
            StopPackage(packageDetails);
        }
    }

    public static void StopPackage(PackageDetails packageDetails)
    {
        packageDetails.Stop();

        if (packageDetails is Package package)
        {
            foreach (PackageFile packageFile in package)
            {
                packageFile.Stop();
            }
        }
    }

    public void RemovePackage(PackageDetails packageDetails)
    {
        packageQueue.Remove(packageDetails);

        if (packageDetails is Package package)
        {
            Packages.Remove(package);

            package.Remove();
        }
        else if (packageDetails is PackageFile packageFile)
        {
            packageFile.Package.Remove(packageFile);
        }
    }

    public void RemovePackageFile(PackageFile packageFile)
    {
        packageQueue.Remove(packageFile);

        packageFile.Package.Remove(packageFile);
    }

    private void AddPackage(Package package)
    {
        if (Packages.Contains(package))
        {
            return;
        }

        // Add event handlers
        package.PackageFilesAdded += Package_PackageFilesAdded;
        package.StatusChanged += Package_StatusChanged;

        // Add to the list
        Packages.Add(package);

        if (!package.RequiresCompression && !string.IsNullOrEmpty(package.SaveFrom))
        {
            // Package does not need to be compressed; look for files and add them
            package.AddPackageFiles(package.SaveFrom);
        }

        // Fire package added event
        PackageAdded?.Invoke(this, new PackageAddedEventArgs(null, [package]));
    }

    private void Package_PackageFilesAdded(object? sender, PackageAddedEventArgs e)
    {
        if (e.ChildPackages != null)
        {
            foreach (PackageDetails packageDetails in e.ChildPackages)
            {
                StartPackage(packageDetails);
            }
        }

        PackageAdded?.Invoke(this, e);
    }

    private void Package_StatusChanged(object? sender, PackageStatusChangedEventArgs e)
    {
        PackageStatusChanged?.Invoke(this, e);

        StartPackageDetails(e.Package, false);

        // If the status of a package has changed and the job is compression
        if (sender is Package package && e.PackageJob == PackageJob.Compression)
        {
            // If it succeeded
            if (e.NewStatus == JobStatus.Success && !string.IsNullOrEmpty(package.Options.CompressionOptions.OutputDirectoryPath))
            {
                // Add the compressed output files as package files to the package
                package.AddPackageFiles(package.Options.CompressionOptions.OutputDirectoryPath);
            }
        }
    }

    private void StartPackageDetails(PackageDetails packageDetails, bool retry)
    {
        if (packageDetails.Status.Status != JobStatus.Running)
        {
            // Start packages with the same job
            StartPackage(packageDetails.Status.Job);
        }

        if (packageDetails.Status.Status == JobStatus.Idle || packageDetails.Status.Status == JobStatus.Success)
        {
            // Get next job for this package, and queue it if needed
            PackageJob? packageJob = packageDetails.GetNextJob();
            if (packageJob.HasValue)
            {
                Enqueue(packageJob.Value, packageDetails);
            }
        }
        else if (retry && packageDetails.Status.Status != JobStatus.Running && packageDetails.Status.Status != JobStatus.Queued && packageDetails.Status.Status != JobStatus.Success)
        {
            // Retry failed ones
            Enqueue(packageDetails.Status.Job, packageDetails);
        }
    }

    private bool StartPackage(PackageJob job)
    {
        // Get the maximum concurrent job count and find related jobs; these are jobs that should start if the given job is finished
        int? maxConcurrentJobs = null;
        List<PackageJob> relatedJobs = [job];
        if (job == PackageJob.Compression || job == PackageJob.Hashing)
        {
            // Compression and hashing are related jobs
            maxConcurrentJobs = AppSettings.Current.MaxConcurrentCPUJobs;
            relatedJobs.Add(job == PackageJob.Compression ? PackageJob.Hashing : PackageJob.Compression);
        }
        else if (job == PackageJob.Upload)
        {
            // Upload does not have a related job, but does have a concurrent job count
            maxConcurrentJobs = AppSettings.Current.MaxConcurrentUploadJobs;
        }

        // If the job has a maximum concurrent job limit, see if we've reached it
        if (maxConcurrentJobs.HasValue)
        {
            // Get all packages count which have a related job and are running
            int packageStatusCount = Packages.Where(p => p.Status != null).Count(p => relatedJobs.Contains(p.Status.Job) && p.Status.Status == JobStatus.Running);

            // Get all package files count in each package, which have a related job and are running
            int packageFileStatusCount = Packages.Sum(p => p.Where(pf => pf != null && pf.Status != null).Count(pf => relatedJobs.Contains(pf.Status.Job) && pf.Status.Status == JobStatus.Running));
            if (packageStatusCount + packageFileStatusCount >= maxConcurrentJobs.Value)
            {
                // Already running at maximum concurrent jobs; don't start a new one
                return false;
            }
        }

        // For each related job, find the first related job that is waiting to start
        foreach (PackageJob relatedJob in relatedJobs)
        {
            if (!packageQueue.TryDequeue(relatedJob, out PackageDetails? package))
            {
                continue;
            }

            // Start job
            package.StartAsync(relatedJob);

            // Only start 1 job at a time
            break;
        }

        return true;
    }

    private void Enqueue(PackageJob job, PackageDetails packageDetails)
    {
        if (packageDetails.Status?.Status != JobStatus.Queued)
        {
            packageQueue.Enqueue(job, packageDetails);

            packageDetails.ChangeStatus(job, JobStatus.Queued);
        }
    }
}
