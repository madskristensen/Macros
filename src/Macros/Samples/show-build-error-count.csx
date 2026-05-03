// Shows the build error and warning count in the status bar after a build completes.
// @trigger Build.SolutionBuildDone
#load ".intellisense/Macros.Intellisense.csx"

object errorObj = null, warningObj = null;
Trigger?.Payload?.TryGetValue("ErrorCount", out errorObj);
Trigger?.Payload?.TryGetValue("WarningCount", out warningObj);
int errors = errorObj is int e ? e : 0;
int warnings = warningObj is int w ? w : 0;
await VS.StatusBar.ShowMessageAsync($"Build complete: {errors} error(s), {warnings} warning(s)");
