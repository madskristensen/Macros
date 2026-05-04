// Shows the build result in the status bar after a build completes.
// @trigger Build.SolutionBuildDone
#load ".intellisense/Macros.Intellisense.csx"

// LastBuildInfo returns 0 if all projects built successfully, or the number of failed projects
var dte = (EnvDTE80.DTE2)DTE;
int failedProjects = dte?.Solution?.SolutionBuild?.LastBuildInfo ?? -1;

string message = failedProjects switch
{
    -1 => "Build complete: Unable to determine result",
    0 => "Build complete: Success ✓",
    1 => "Build complete: 1 project failed",
    _ => $"Build complete: {failedProjects} projects failed"
};

await VS.StatusBar.ShowMessageAsync(message);
