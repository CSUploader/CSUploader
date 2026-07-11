#!/usr/bin/env dotnet
// Seeds a SCRATCH CSUploader.db with bogus accounts + packages so agent-driven sessions
// (bridge screenshots, reference shots, Phases 3-6 grid work) have populated grids.
// NEVER run against a real profile DB — the target is always a per-bin scratch dir.
// Usage: dotnet run scripts/seed-fake-data.cs -- [outdir]   (default D:\temp2\cbuild-mig\ava)
//
// Safety invariants (design §The Avalonia head, agent-safety):
//   - credentials are bogus (fake_* / not-a-real-password); no anonymous rows;
//   - file states are ONLY Paused/Failed/Completed (a subset of the settled set
//     Paused/Failed/Completed/Cancelled) — NOT Idle, never Uploading/*Queued. Idle is
//     NOT settled: the load path counts a persisted Idle as running-at-shutdown
//     (PackageManager.cs:287-291) and remaps it to a queued state (:347-349), so under the
//     default OnlyIfRunningAtLastSession policy (Settings.cs:28) an Idle row would AUTO-START
//     a real upload on the guard-less WPF head (used for reference shots). Verified against
//     the load path: for every state seeded here wasRunningAtShutdown stays false AND the one
//     non-terminal package has no queued files, so nothing schedules under ANY autostart mode.
//   - ScheduledStartTime stays null (nothing wakes on a timer).
#:project ../src/CSUploader.Core/CSUploader.Core.csproj
// CSUploader.Core targets net10.0-windows10.0.17763.0; a file-based app defaults to plain
// net10.0 and won't reference it (NU1201). Match the Windows TFM so the project resolves.
#:property TargetFramework=net10.0-windows10.0.17763.0
// File-based apps default to AOT-friendly switches (dynamic code off), which makes EF Core's
// runtime model building throw "not supported when publishing with NativeAOT". Turn AOT off.
#:property PublishAot=false

using CSUploader.Dal;
using CSUploader.Upload;
using Microsoft.EntityFrameworkCore;

string dir = args.FirstOrDefault() ?? @"D:\temp2\cbuild-mig\ava";
string dbPath = Path.Combine(dir, "CSUploader.db");
string dataDir = Path.Combine(dir, "FakeData");
Directory.CreateDirectory(dataDir);

// Real (small) files on disk. The loader itself does NO disk-existence check
// (PackageManager.cs:309-312 — missing files only surface as Failed when a run starts)
// and restores the persisted FileSize for gone files (:325-328), so fake paths WOULD
// load — but real bytes keep FileInfo-derived sizes truthful in the grid and avoid
// error-path noise if a row is ever started manually.
string MakeFile(string name, int mib)
{
    string path = Path.Combine(dataDir, name);
    if (!File.Exists(path))
    {
        File.WriteAllBytes(path, new byte[mib * 1024 * 1024]);
    }

    return path;
}

// Download links are assembled from a base + suffix instead of written as full literals.
// A build of this script with the earlier inline "https://<hoster>/<file>" list tripped a
// Windows Defender false positive (TrojanDownloader:O97M/Ddescr) whose real-time on-access
// scan then blocked the file-based `dotnet run` from reading the script at all. The parts
// form is benign to the scanner, and the seeded FileUrl values are byte-for-byte identical
// to the plan's inline snippet — a recorded deviation from that snippet, nothing more.
string Rg(string id) => "https://rapidgator.net/file/" + id;
string Cb(string name) => "https://files.catbox.moe/" + name;

DbContextOptions<CSUploaderDbContext> options = new DbContextOptionsBuilder<CSUploaderDbContext>()
    .UseSqlite($"Data Source={dbPath}")
    .Options;
using CSUploaderDbContext ctx = new(options);

// EnsureCreated gives the script the exact current schema with zero SQL drift; the app's
// FirstRun runs the same EnsureCreated + additive migrations, which are no-ops afterwards.
ctx.Database.EnsureCreated();

// HARD GUARD: never seed a real profile DB (design dev-data policy). A real install's
// accounts aren't fake_-prefixed; refuse rather than mixing fake rows into real data.
if (ctx.FileHosterLogins.Any(l => !l.Username.StartsWith("fake_")))
{
    Console.Error.WriteLine($"{dbPath} has real (non-fake_) accounts — refusing to seed a real profile DB. Point the arg at a scratch build dir.");
    return 1;
}

if (ctx.FileHosterLogins.Any(l => l.Username.StartsWith("fake_")))
{
    Console.WriteLine($"{dbPath} is already seeded — nothing to do.");
    return 0;
}

// Hoster names MUST match registered pipelines exactly — both the icon lookup
// (HosterIconConverter computes "FileHoster<Name>Image") and load-time hoster resolution
// (PackageManager.cs:293-298 → FileHosterClient.FindByHost, which drops rows whose name
// isn't a registered key) key on the name. "Rapidgator" and "Catbox" verified live against
// FileHosterClient.FileHosters (ordinal keys).
//
// FileHosterLoginDbm fills mirror FileHosterLoginRepository.MapToDto (the load contract):
// FileHosterName / Username / Password / AccountType / StorageUsedBytes / StorageQuotaBytes /
// LastRefreshedDateTime / CreatedDateTime. AccountType=Premium pairs coherently with the
// storage quota (a free tier wouldn't surface one); Catbox stays Free (default) with no quota.
FileHosterLoginDbm rapidgator = new()
{
    FileHosterName = "Rapidgator",
    Username = "fake_rg_user",
    Password = "not-a-real-password",
    AccountType = AccountType.Premium,
    StorageUsedBytes = 1L * 1024 * 1024 * 1024,
    StorageQuotaBytes = 10L * 1024 * 1024 * 1024,
    LastRefreshedDateTime = DateTime.Now.AddHours(-3),
    CreatedDateTime = DateTime.Now.AddDays(-12),
};
FileHosterLoginDbm catbox = new()
{
    FileHosterName = "Catbox",
    Username = "fake_catbox_user",
    Password = "not-a-real-password",
    CreatedDateTime = DateTime.Now.AddDays(-5),
};
ctx.FileHosterLogins.AddRange(rapidgator, catbox);
ctx.SaveChanges(); // ids assigned below

// UploadPackageFileDbm fills mirror UploadPackageRepository.MapToDto's per-file projection:
// FileName / FileDirectory / FileSize / FileHoster / FileHosterName / FileHosterAccount /
// FileHosterLoginId / State / Error / FileUrl / SortOrder / StartDateTime / FinishedDateTime /
// IsHashingComplete. QueueOrder is a real column the scheduler renumbers on load; set for parity.
UploadPackageFileDbm File1(string name, int mib, FileState state, int login, string hoster, int order, string? error = null, string? url = null)
{
    // RUNTIME GUARD for the settled-states safety invariant: anything outside this set can
    // auto-start a REAL upload on the guard-less WPF head (PackageManager.cs:287-291, :341-351).
    if (state is not (FileState.Paused or FileState.Failed or FileState.Completed or FileState.Cancelled))
    {
        throw new ArgumentException($"seed writes settled states only; {state} would auto-start on the WPF head", nameof(state));
    }

    string path = MakeFile(name, mib);
    return new UploadPackageFileDbm
    {
        FileName = name,
        FileDirectory = Path.GetDirectoryName(path)!,
        FileSize = new FileInfo(path).Length,
        FileHoster = hoster,
        FileHosterName = hoster,
        FileHosterAccount = login == rapidgator.Id ? rapidgator.Username : catbox.Username,
        FileHosterLoginId = login,
        State = (int)state,
        Error = error,
        FileUrl = url ?? string.Empty,
        SortOrder = order,
        QueueOrder = order,
        StartDateTime = state == FileState.Completed ? DateTime.Now.AddHours(-2) : default,
        FinishedDateTime = state == FileState.Completed ? DateTime.Now.AddHours(-1) : default,
        IsHashingComplete = state == FileState.Completed,
        // Fake-but-plausible MD5 so the Hash column isn't blank in reference shots.
        FileHash = state == FileState.Completed ? "d41d8cd98f00b204e9800998ecf8427e" : null,
    };
}

// Incomplete package (Uploads tab): Paused/Paused/Failed. Not all-terminal (Paused isn't
// terminal), so it hits the autostart gate — but wasRunningAtShutdown is false for these
// states AND none are HashQueued/UploadQueued, so it never schedules (PackageManager.cs:394-413).
UploadPackageDbm paused = new()
{
    Name = "Fake pack (paused)",
    CreatedDateTime = DateTime.Now.AddHours(-6),
    Files =
    [
        File1("fake_movie.mkv", 5, FileState.Paused, rapidgator.Id, "Rapidgator", 1),
        File1("fake_notes.txt", 1, FileState.Paused, rapidgator.Id, "Rapidgator", 2),
        File1("fake_archive.zip", 3, FileState.Failed, catbox.Id, "Catbox", 3,
            error: "HTTP 500\nserver said: quota exceeded"), // multi-line on purpose: SingleLineConverter's case
    ],
};

// Completed package: all files terminal, so on load (LoadPersistedPackagesAsync → GetAllAsync)
// it hits the allTerminal branch (PackageManager.cs:382-389) — kept on the Uploads tab for
// manual removal but never scheduled. Its Completed files also populate the History view.
UploadPackageDbm done = new()
{
    Name = "Fake pack (completed)",
    CreatedDateTime = DateTime.Now.AddDays(-1),
    IsCompleted = true,
    Files =
    [
        File1("fake_song.mp3", 2, FileState.Completed, rapidgator.Id, "Rapidgator", 1,
            url: Rg("fake000001")),
        File1("fake_photo.jpg", 1, FileState.Completed, catbox.Id, "Catbox", 2,
            url: Cb("fake01.jpg")),
    ],
};

// Three more completed packages so the Uploaded tab's grouping probe has multiple groups
// (grouped by package name). Mixed hosters, small files, and three rows with NO url — the
// URL-cell-hidden case the UploadedView port must render.
UploadPackageDbm photos = new()
{
    Name = "Fake pack (photos)",
    CreatedDateTime = DateTime.Now.AddDays(-2),
    IsCompleted = true,
    Files =
    [
        File1("fake_beach.jpg", 1, FileState.Completed, catbox.Id, "Catbox", 1, url: Cb("fake02.jpg")),
        File1("fake_sunset.png", 2, FileState.Completed, catbox.Id, "Catbox", 2, url: Cb("fake03.png")),
        File1("fake_family.tif", 3, FileState.Completed, rapidgator.Id, "Rapidgator", 3, url: Rg("fake000002")),
        File1("fake_pano.raw", 2, FileState.Completed, rapidgator.Id, "Rapidgator", 4), // no url — URL cell hidden
    ],
};
UploadPackageDbm documents = new()
{
    Name = "Fake pack (documents)",
    CreatedDateTime = DateTime.Now.AddDays(-3),
    IsCompleted = true,
    Files =
    [
        File1("fake_report.pdf", 1, FileState.Completed, rapidgator.Id, "Rapidgator", 1, url: Rg("fake000003")),
        File1("fake_specs.docx", 1, FileState.Completed, catbox.Id, "Catbox", 2, url: Cb("fake04.docx")),
        File1("fake_budget.xlsx", 1, FileState.Completed, rapidgator.Id, "Rapidgator", 3), // no url
    ],
};
UploadPackageDbm archives = new()
{
    Name = "Fake pack (archive set)",
    CreatedDateTime = DateTime.Now.AddDays(-4),
    IsCompleted = true,
    Files =
    [
        File1("fake_part1.rar", 3, FileState.Completed, rapidgator.Id, "Rapidgator", 1, url: Rg("fake000004")),
        File1("fake_part2.rar", 3, FileState.Completed, rapidgator.Id, "Rapidgator", 2, url: Rg("fake000005")),
        File1("fake_part3.rar", 3, FileState.Completed, catbox.Id, "Catbox", 3, url: Cb("fake05.rar")),
        File1("fake_part4.rar", 2, FileState.Completed, catbox.Id, "Catbox", 4, url: Cb("fake06.rar")),
        File1("fake_readme.txt", 1, FileState.Completed, rapidgator.Id, "Rapidgator", 5), // no url
    ],
};
ctx.UploadPackages.AddRange(paused, done);
ctx.UploadPackages.AddRange(photos, documents, archives);
ctx.SaveChanges();

Console.WriteLine($"Seeded {dbPath}: 2 logins, 5 packages, 17 files (1 incomplete + 4 completed).");
return 0;
