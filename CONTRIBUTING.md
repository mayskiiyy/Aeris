# Contributing

You need Windows and the .NET 8 SDK. Check that the project builds before you open a pull request:

```powershell
dotnet build src/Aeris/Aeris.csproj -c Release
```

Keep pull requests small and describe the problem, the change and how you tested it.

## Ground rules

- Scans and cleanup run in the background and can be cancelled. Keep it that way.
- Anything that deletes files needs a confirmation that defaults to "No".
- A new cleanup target needs a clear reason. Arbitrary user folders must never become deletion targets.
- Test cleanup only on files you created for the test, never on real data. Cover fresh and old dates, locked and inaccessible files, symlinks and junctions, cancellation and paths outside the allowed roots.
- Check the UI in both themes, at the minimum window size (960 × 700) and in at least one language with long words, such as German or Polish.

## Strings and translations

All user-facing text lives in `src/Aeris/Languages/*.xaml`. When you add a string, add the key to every language file. If you can't translate it, copy the English text so the key exists everywhere. Don't put user-facing text directly in C# or XAML.

## What not to commit

`bin/`, `obj/`, `artifacts/`, local test folders, screenshots with personal data, paths containing your user name, keys and certificates.
