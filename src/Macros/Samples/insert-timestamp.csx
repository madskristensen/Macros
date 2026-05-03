// Inserts the current date and time at the caret.
#load ".intellisense/Macros.Intellisense.csx"

await TypeAsync(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
