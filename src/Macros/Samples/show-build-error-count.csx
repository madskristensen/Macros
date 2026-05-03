// Shows the build error and warning count in the status bar after a build completes.
// @trigger Build.SolutionBuildDone
#load ".intellisense/Macros.Intellisense.csx"

int errors = (int?)Trigger?.Data?["ErrorCount"] ?? 0;
int warnings = (int?)Trigger?.Data?["WarningCount"] ?? 0;
await VS.StatusBar.ShowMessageAsync($"Build complete: {errors} error(s), {warnings} warning(s)");
