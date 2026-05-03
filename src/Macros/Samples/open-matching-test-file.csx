// Opens a sibling test file that matches the current document name.
// For example, Foo.cs opens FooTests.cs when it exists.
#load ".intellisense/Macros.Intellisense.csx"

var doc = DTE.ActiveDocument;
if (doc?.FullName == null) return;

var fileName = System.IO.Path.GetFileNameWithoutExtension(doc.Name);
var directory = System.IO.Path.GetDirectoryName(doc.FullName);
var testFileName = $"{fileName}Tests.cs";
var testPath = System.IO.Path.Combine(directory, testFileName);

if (System.IO.File.Exists(testPath))
{
    await OpenFileAsync(testPath);
}
else
{
    await VS.StatusBar.ShowMessageAsync($"Macros: Could not find {testFileName}");
}
