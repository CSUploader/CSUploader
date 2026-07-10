# tests/CLAUDE.md

Conventions for writing tests in this project. Inherits everything from the root `CLAUDE.md`.

## Framework & libraries

- **xUnit** (`[Fact]`, `[Theory]`) — assertion via `Assert.*`
- **Moq** for interface mocks (`Mock<T>`, `Mock.Of<T>()`)
- **Microsoft.Data.Sqlite** for in-memory SQLite when a real EF context is needed

## Naming & layout

- One test class per production class, mirroring the source folder structure:
  `src/Dal/UploadPackageFileRepository.cs` → `tests/Dal/UploadPackageFileRepositoryTests.cs`.
- Test method name describes the scenario and expected outcome:
  `MethodName_StateUnderTest_ExpectedBehavior`
  e.g. `HideAsync_FlipsIsHiddenFlagWithoutDeleting`.
- For commands that take heterogeneous parameters (e.g. `IList` of selected items vs. a single
  item), cover both parameter shapes — see `ConnectionManagerViewModelTests.TestCommand_*`.
- Group related setup in private helper methods (`InsertPackageAsync`, `InsertFileAsync`) at the bottom of the class — keep individual tests focused on the one behavior they cover.

## Structure

- Use Arrange / Act / Assert. Comments are optional, but the visual flow should be obvious.
- Keep tests independent — no shared mutable state between test methods.
- Each test class implements `IDisposable` if it owns a `SqliteConnection` or `UploadScheduler`.
  - **Use `IAsyncLifetime` instead** when teardown needs to `await` (e.g. draining fire-and-forget
    `Task.Run` callbacks before closing the SQLite connection — see
    `PackageManagerSoftRemoveTests` for the canonical pattern, and
    `PackageManager.DrainPendingPersistenceAsync` for the SUT-side hook that makes the drain
    deterministic). The SUT exposes such drain helpers as `internal` and reaches the test
    project through `InternalsVisibleTo`. Implement `Task InitializeAsync() => Task.CompletedTask;`
    when there's no async setup work.
  - The cross-test failure mode this prevents: a fire-and-forget continuation from test N
    that captures test N's repos / `DbContextFactory` keeps running after test N's
    `Dispose()` closes the connection. The continuation throws (silently — `Mock.Of<IAppLogger>()`
    swallows it) but congests the thread pool enough that test N+1's polling assertions
    intermittently time out. Symptom: full-suite flakes that vanish in isolation and on re-run.

## Repository tests

- Spin up an in-memory SQLite via `new SqliteConnection("Data Source=:memory:")` and call `db.Database.EnsureCreated()`. Pattern reference: `tests/Dal/FileHosterLoginRepositoryTests.cs`.
- Provide a private `TestDbContextFactory` implementing `IDbContextFactory<CSUploaderDbContext>`.

## ViewModel tests

- Mock `IDialogService` and `IAppLogger` with Moq. `IDialogService` is fully async — set up its dialog methods with `ReturnsAsync` (e.g. `ShowOptOutConfirmationAsync`, `ShowConfirmationAsync`) to return the value the test needs.
- ViewModels that marshal to the UI thread or create UI-thread timers take `IUiDispatcher`; construct them with `new WpfUiDispatcher()`, **not** `Mock.Of<IUiDispatcher>()`. The real dispatcher is inert without a running `Application` — `Post` is a no-op, `InvokeAsync` runs inline, and `CreateTimer` yields an inert timer — which is exactly the headless-test behaviour the VMs rely on; a bare mock would return a null timer/`Task` and NRE in the constructor. Clipboard-touching VMs take `IClipboardService`; `Mock.Of<IClipboardService>()` is fine (its async members return completed tasks).
- For ViewModels that take `PackageManager`, construct a real one with in-memory repos and a real `UploadScheduler` — the scheduler's background loop is idle until packages are added, so it doesn't interfere with tests.
- Invoke `[RelayCommand]` methods through their generated command (`vm.SomeCommand.ExecuteAsync(parameter)`), not via reflection.
- Pass `IList`-style parameters as `new List<T> { ... }` to mirror what the DataGrid binding sends at runtime.

## What to assert

- Both the persisted DB state **and** the in-memory ViewModel state when both are affected — pre-soft-delete bugs slipped through tests that only checked one.
- For commands with confirmation dialogs, write at least one happy-path test (confirmed) and one declined test.
