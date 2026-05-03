// Inserts a TODO comment stamped with today's date.
#load ".intellisense/Macros.Intellisense.csx"

await TypeAsync($"// TODO ({DateTime.Now:yyyy-MM-dd}): ");
